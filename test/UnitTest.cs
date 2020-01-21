using adb;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

using adb.physic;
using adb.utils;
using adb.logic;
using adb.sqlparser;
using adb.optimizer;
using adb.test;
using adb.expr;
using adb.dml;

// failed tests:
// sql = "select 5+5 as a1 from a where a1 > 2;";

namespace test
{
    // Test Utils
    public class TU
    {
        static internal string error_ = null;
        static internal List<Row> ExecuteSQL(string sql) => ExecuteSQL(sql, out _);

        static internal List<Row> ExecuteSQL(string sql, out string physicplan, QueryOption option = null)
        {
            var results = SQLStatement.ExecSQL(sql, out physicplan, out error_, option);
            Console.WriteLine(physicplan);
            return results;
        }
        static internal void ExecuteSQL(string sql, string resultstr)
        {
            var result = ExecuteSQL(sql);
            Assert.AreEqual(resultstr, string.Join(";", result));
        }
        static internal void ExecuteSQL(string sql, string resultstr, out string physicplan, QueryOption option = null)
        {
            var result = SQLStatement.ExecSQL(sql, out physicplan, out error_, option);
            Assert.AreEqual(resultstr, string.Join(";", result));
        }

        static public void PlanAssertEqual(string l, string r)
        {
            char[] splitters = { ' ', '\t', '\r', '\n' };
            var lw = l.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
            var rw = r.Split(splitters, StringSplitOptions.RemoveEmptyEntries);

            Assert.AreEqual(lw.Length, rw.Length);
            for (int i = 0; i < lw.Length; i++)
                Assert.AreEqual(lw[i], rw[i]);
        }

        // a;c;b == b;a;c
        static public void ResultAreEqualNoOrder(string l, string r) 
        {
            char[] splitters = { ';' };
            var lw = l.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
            var rw = r.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(lw.Length, rw.Length);
            Assert.IsTrue(lw.OrderBy(x => x).SequenceEqual(rw.OrderBy(x => x)));
        }

        public static int CountStr(string text, string pattern)
        {
            Assert.IsNotNull(text);
            text = text.ToLower();
            pattern = pattern.ToLower();

            // Loop through all instances of the string 'text'.
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }
        public static void CountOccurrences(string text, string pattern, int expected)
        {
            var count = CountStr(text, pattern);
            Assert.AreEqual(expected, count);
        }
    }

    [TestClass]
    public class UtilsTest
    {
        [TestMethod]
        public void TestCSVReader()
        {
            List<string> r = new List<string>();
            Utils.ReadCsvLine(@"../../../data/test.tbl",
                x => r.Add(string.Join(",", x)));
            Assert.AreEqual(3, r.Count);
            Assert.AreEqual("1,2,3,4", r[0]);
            Assert.AreEqual("2,2,3,4", r[1]);
            Assert.AreEqual("5,6,7,8", r[2]);
        }
    }

    [TestClass]
    public class CodeGenTest
    {
        [TestMethod]
        public void TestSimpleSlect()
        {
            // you may encounter an error saying can't find roslyn/csc.exe
            // one work around is to copy the folder there.
            //
            string sql = "select * from a, b, c where a1>b1 and a2>c2;";
            QueryOption option = new QueryOption();
            option.profile_.enabled_ = false;
            option.optimize_.use_codegen_ = true;

            var result = TU.ExecuteSQL(sql, out string resultstr, option);
            // Assert.AreEqual("1;2;2;2;2", string.Join(";", result));
        }
    }

    [TestClass]
    public class DDLTest
    {
        [TestMethod]
        public void TestCreateTable()
        {
            var sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a4 numeric(9));";
            try
            {
                var l = RawParser.ParseSqlStatements(sql);
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.Message.Contains("SemanticAnalyzeException"));
            }
            sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), " +
                "a5 numeric(9), a6 double, a7 date, a8 varchar(100), primary key (a1));";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as CreateTableStmt;
            Assert.AreEqual(8, stmt.cols_.Count);
            Assert.AreEqual(1, stmt.cons_.Count);
        }

        [TestMethod]
        public void TestAnalyze()
        {
            var sql = "analyze a;";
            var stmt = RawParser.ParseSqlStatements(sql);
            stmt.Exec();
        }
    }

    [TestClass]
    public class TpcTest
    {
        [TestMethod]
        public void TestBenchmarks()
        {
            TestTpcds();
            TestTpch();
        }

        void TestTpcds()
        {
            var files = Directory.GetFiles(@"../../../tpcds");

            Tpcds.CreateTables();

            // make sure all queries can generate phase one opt plan
            QueryOption option = new QueryOption();
            option.optimize_.enable_subquery_to_markjoin_ = true;
            option.optimize_.remove_from = false;
            option.optimize_.use_memo_ = false;
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var result = TU.ExecuteSQL(sql, out string phyplan, option);
                Assert.IsNotNull(phyplan); Assert.IsNotNull(result);
            }
        }

        void TestTpch()
        {
            Tpch.CreateTables();

            // make sure all queries parsed
            var files = Directory.GetFiles(@"../../../tpch");
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var stmt = RawParser.ParseSingleSqlStatement(sql);
                stmt.Bind(null);
                Console.WriteLine(stmt.CreatePlan().PrintString(0));
            }
            Assert.AreEqual(22, files.Length);

            // load data
            Tpch.LoadTables("0001");
            Tpch.AnalyzeTables();

            // execute queries
            string phyplan = "";
            var option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;
                Assert.IsTrue(option.optimize_.enable_subquery_to_markjoin_);
                option.optimize_.remove_from = true;

                var result = TU.ExecuteSQL(File.ReadAllText(files[0]), out _, option);
                Assert.AreEqual(4, result.Count);
                result = TU.ExecuteSQL(File.ReadAllText(files[1]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[2]), out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashJoin"));
                Assert.AreEqual(8, result.Count);
                result = TU.ExecuteSQL(File.ReadAllText(files[3]), out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin")); Assert.AreEqual(0, TU.CountStr(phyplan, "Subquery"));
                Assert.AreEqual(5, result.Count);
                Assert.AreEqual("1-URGENT,9;2-HIGH,7;3-MEDIUM,9;4-NOT SPECIFIED,7;5-LOW,12", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[4]), out phyplan, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[5]), out _, option);
                Assert.AreEqual("77949.9186", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[6]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[7]), out _, option);
                Assert.AreEqual("0,0", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[8]), out _, option);
                Assert.AreEqual(9, result.Count);
                Assert.AreEqual("ARGENTINA,0,121664.3574;ETHIOPIA,0,160941.78;IRAN,0,183368.022;IRAQ,0,179598.8939;KENYA,0,577214.8907;MOROCCO,0,1687292.0869;"+
                    "PERU,0,564372.7491;UNITED KINGDOM,0,2309462.0142;UNITED STATES,0,274483.6167",
                    string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[9]), out _, option);
                Assert.AreEqual(20, result.Count);
                result = TU.ExecuteSQL(File.ReadAllText(files[10]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[11]), out _, option);
                Assert.AreEqual("MAIL,5,5;SHIP,5,10", string.Join(";", result));
                // FIXME: agg on agg from
                option.optimize_.remove_from = false;
                result = TU.ExecuteSQL(File.ReadAllText(files[12]), out _, option);
                Assert.AreEqual(26, result.Count);
                option.optimize_.remove_from = true;
                result = TU.ExecuteSQL(File.ReadAllText(files[13]), out _, option);
                Assert.AreEqual(1, result.Count);
                Assert.AreEqual(true, result[0].ToString().Contains("15.23"));
                // q15 cte
                result = TU.ExecuteSQL(File.ReadAllText(files[15]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[16]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[17]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[18]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[19]), out _, option);
                Assert.AreEqual("", string.Join(";", result));
                // q21 plan too slow
                //result = TU.ExecuteSQL(File.ReadAllText(files[20]), out _, option);
                //Assert.AreEqual("", string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[21]), out phyplan, option);
                Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
                Assert.AreEqual(7, result.Count);
            }
        }
    }

    [TestClass]
    public class OptimizerTest
    {
        [TestMethod]
        public void TestMemo()
        {
            string phyplan = "";
            QueryOption option = new QueryOption();
            option.optimize_.use_memo_ = true;
            option.optimize_.enable_subquery_to_markjoin_ = true;

            var sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            var result = TU.ExecuteSQL(sql, out _, option);
            var memo = Optimizer.memoset_[0];
            memo.CalcStats(out int tlogics, out int tphysics);
            Assert.AreEqual(9, memo.cgroups_.Count);
            Assert.AreEqual(18, tlogics); Assert.AreEqual(26, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select * from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);";
            option.optimize_.enable_subquery_to_markjoin_ = false; // FIXME: they shall work together
            result = TU.ExecuteSQL(sql, out _, option);
            Assert.AreEqual("0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5", string.Join(";", result));
            option.optimize_.enable_subquery_to_markjoin_ = true;

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";
            result = TU.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(15, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,c,b where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";   // FIXME: different #plans
            result = TU.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(17, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3";
            option.optimize_.memo_disable_crossjoin = false;
            result = TU.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(6, memo.cgroups_.Count);
            Assert.AreEqual(11, tlogics); Assert.AreEqual(17, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            option.optimize_.memo_disable_crossjoin = true;
            result = TU.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(5, memo.cgroups_.Count);
            Assert.AreEqual(7, tlogics); Assert.AreEqual(11, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TU.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(9, memo.cgroups_.Count);
            Assert.AreEqual(18, tlogics); Assert.AreEqual(26, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select count(b1) from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TU.ExecuteSQL(sql, out _, option);
            Assert.AreEqual("3", string.Join(";", result));

            sql = "select count(*) from a where a1 in (select b2 from b where b1 > 0) and a2 in (select b3 from b where b1 > 0);";
            result = TU.ExecuteSQL(sql, out phyplan, option); TU.CountOccurrences(phyplan, "PhysicFilter", 0); Assert.AreEqual("1", string.Join(";", result));

            sql = "select count(*) from (select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1 and a1>0) v;";
            result = TU.ExecuteSQL(sql, out phyplan, option); TU.CountOccurrences(phyplan, "PhysicFilter", 0); Assert.AreEqual("2", string.Join(";", result));

            sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));"; 
            result = TU.ExecuteSQL(sql, out phyplan, option);
            var answer = @"PhysicScanTable a (actual rows=3)
                            Output: a.a2[1]
                            Filter: a.a3[2]>@1
                            <ScalarSubqueryExpr> cached 1
                                -> PhysicHashAgg  (actual rows=1)
                                    Output: {min(b.b1*2)}[0]
                                    Aggregates: min(b.b1[1]*2)
                                    -> PhysicFilter  (actual rows=3)
                                        Output: {b.b1*2}[1],b.b1[2],2
                                        Filter: {#marker}[0]
                                        -> PhysicSingleMarkJoin  (actual rows=3)
                                            Output: #marker,{b.b1*2}[0],b.b1[1],{2}[2]
                                            Filter: b.b2[3]>=c.c2[4]-1 and c.c2[4]=b.b2[3]
                                            -> PhysicScanTable b (actual rows=3)
                                                Output: b.b1[0]*2,b.b1[0],2,b.b2[1]
                                                Filter: b.b3[2]>@3
                                                <ScalarSubqueryExpr> 3
                                                    -> PhysicScanTable c (actual rows=1, loops=3)
                                                        Output: c.c2[1]
                                                        Filter: c.c2[1]=?b.b2[1]
                                            -> PhysicScanTable c (actual rows=3, loops=3)
                                                Output: c.c2[1]";
            Assert.AreEqual("1;2;3", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select count(*) from a, b,c,d where a1+b1+c1+d1=1;";
            result = TU.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(0, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual("4", string.Join(";", result));

            // FIXME: a.a1+b.a1=5-c.a1, a.a1+b.a1+c.a1=5
            sql = "select a.a1,b.a1,c.a1, a.a1+b.a1+c.a1 from a, a b, a c where a.a1=5-b.a1-c.a1;";
            result = TU.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "a.a1[0]=5-b.a1[1]-c.a1[2]"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual("2,2,1,5;2,1,2,5;1,2,2,5", string.Join(";", result));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2;";
            result = TU.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "NLJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual("0,1,2,3;1,2,3,4;2,3,4,5", string.Join(";", result));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2 join d on a4=2*d3;";
            result = TU.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "NLJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: a.a1[0]=b.b1[4] or a.a3[2]=b.b3[5]"));
            Assert.AreEqual(2, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual("1,2,3,4", string.Join(";", result));


            Assert.IsTrue(option.optimize_.use_memo_);
        }
    }

    [TestClass]
    public class SubqueryTest
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestExistsSubquery()
        {
            QueryOption option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i==0;
                // exist-subquery
                var phyplan = "";
                var sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1);";
                var result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("1;2", string.Join(";", result));
                sql = "select a2 from a where exists (select * from a);";
                result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("1;2;3", string.Join(";", result));
                sql = "select a2 from a where not exists (select * from a b where b.a3>=a.a1+b.a1+1);";
                result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("3", string.Join(";", result));
                sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) and a2>2;";
                result = TU.ExecuteSQL(sql, out phyplan);
                Assert.AreEqual(0, result.Count);
                sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2;";
                result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("1;2;3", string.Join(";", result));
                sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
                result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("0,1;1,2", string.Join(";", result));
                // multiple subquery - FIXME: shall be two mark join
                sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
                     and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
                result = TU.ExecuteSQL(sql, out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicMarkJoin"));
                Assert.AreEqual("2", string.Join(";", result));
            }
        }

        [TestMethod]
        public void TestScalarSubquery()
        {
            QueryOption option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;

                var phyplan = "";
                var sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2);";
                var result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("0,2;1,3;2,4", string.Join(";", result));
                sql = "select a1, a3  from a where a.a2 = (select b1*2 from b where b2 = a2);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("1,3", string.Join(";", result));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("0,2", string.Join(";", result));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) and a2>1;";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("1,3", string.Join(";", result));
                sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = 3);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("2", string.Join(";", result));
                sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("2", string.Join(";", result));
                sql = @"select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 
                    and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<3);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("0;1", string.Join(";", result));
                sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 or b1 = (select b1 from b where b2 = 2*a1 and b3>1) and b2<3);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("0;1;2", string.Join(";", result));
                sql = "select a1,a2,b2 from b join a on a1=b1 where a1-1 < (select a2/2 from a where a2=b2);";
                result = TU.ExecuteSQL(sql, out phyplan); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleMarkJoin"));
                Assert.AreEqual("0,1,1;1,2,2", string.Join(";", result));

                //  OR condition failed sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) or a2>1;";
            }
        }

        [TestMethod]
        public void TestExecSubFrom()
        {
            #region regular w/o FromQuery removal
            var sql = "select * from a, (select * from b) c";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            Assert.AreEqual("0,1,2,3,0,1,2,3", result[0].ToString());
            Assert.AreEqual("2,3,4,5,2,3,4,5", result[8].ToString());
            sql = "select * from a, (select * from b where b2>2) c";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            sql = "select b.a1 + b.a2 from (select a1 from a) b";
            result = ExecuteSQL(sql);
            Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select b.a1 + a2 from (select a1,a2 from a) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;3;5", string.Join(";", result));
            sql = "select a3 from (select a1,a3 from a) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2;3;4", string.Join(";", result));
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2;3;4", string.Join(";", result));
            sql = "select count(*) from (select * from a where a1 > 1) b;";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashAgg  (actual rows=1)
                            Output: {count(*)(0)}[0]
                            Aggregates: count(*)(0)
                            -> PhysicFromQuery <b> (actual rows=1)
                                Output: 0
                                -> PhysicScanTable a (actual rows=1)
                                    Output: a.a1[0],a.a2[1],a.a3[2],a.a4[3]
                                    Filter: a.a1[0]>1
                        ";  // observing no double push down
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select b1, b2 from (select a3, a4 from a) b(b2);";
            result = ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select b2 from (select a3, a4 from a) b(b2,b3,b4);";
            result = ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select sum(a12) from (select a1*a2 a12 from a);";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select sum(a12) from (select a1*a2 a12 from a) b;";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select a4 from (select a3, a4 from a) b(a4);"; 
            result = ExecuteSQL(sql); Assert.AreEqual("2;3;4", string.Join(";", result));
            sql = "select c.d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1);";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select sum(e1) from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) d(e1);";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select e1 from (select * from (select sum(a12) from (select a1*a2 as a12, a1, a2 from a) b) c(d1)) d(e1);";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select e1 from (select e1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(e1)) d;";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = "select e1 from(select d1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            result = ExecuteSQL(sql); Assert.AreEqual("8", string.Join(";", result));
            sql = " select a1, sum(a12) from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a1) group by a1;";
            result = ExecuteSQL(sql); Assert.AreEqual("0,0;1,2;2,6", string.Join(";", result));
            sql = "select a1, sum(a12) as a2 from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a12) group by a1;";
            result = ExecuteSQL(sql); Assert.AreEqual("0,0", string.Join(";", result));
            sql = @"SELECT e1  FROM   (SELECT d1 FROM   (SELECT Sum(ab12) 
                                        FROM   (SELECT e1 * b2 ab12 FROM   (SELECT e1 FROM   (SELECT d1 
                                                                FROM   (SELECT Sum(ab12) 
                                                                        FROM   (SELECT a1 * b2 ab12 FROM  a  JOIN b ON a1 = b1) b) 
                                                                       c(d1)) 
                                                               d(e1)) a JOIN b ON e1 = 8*b1) b) c(d1)) d(e1); ";
            result = ExecuteSQL(sql); Assert.AreEqual("16", string.Join(";", result));
            sql = "select *, cd.* from (select a.* from a join b on a1=b1) ab , (select c1 , c3 from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = ExecuteSQL(sql); Assert.AreEqual("0,1,2,3,0,2,0,2;1,2,3,4,1,3,1,3;2,3,4,5,2,4,2,4", string.Join(";", result));
            #endregion

            // these queries we can remove from
            QueryOption option = new QueryOption();
            option.optimize_.remove_from = true;
            sql = "select a1 from(select b1 as a1 from b) c;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select b1 from (select count(*) as b1 from b) a;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select c100 from (select c1 c100 from c) c where c100>1";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select * from (select a1*a2 a12, a1 a7 from a) b(a12);";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select * from (select a1*a2 a12, a1 a7 from a) b;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select *, cd.* from (select a.* from a join b on a1=b1) ab , (select c1 , c3 from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select a12*a12 from (select a1*a2 a12, a1 a7 from a) b;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            sql = "select a2, count(*), sum(a2) from (select a2 from a) b where a2*a2> 1 group by a2;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("2,1,2;3,1,3", string.Join(";", result));
            sql = "select b1, b2+b2, c100 from (select b1, count(*) as b2 from b group by b1) a, (select c1 c100 from c) c where c100>1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("0,2,2;1,2,2;2,2,2", string.Join(";", result));
            sql = "select b1+b1, b2+b2, c100 from (select b1, count(*) as b2 from b group by b1) a, (select c1 c100 from c) c where c100>1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("0,2,2;2,2,2;4,2,2", string.Join(";", result));
            sql = "select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1);";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("8", string.Join(";", result));
            sql = "select e1 from (select d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1)) d(e1);";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("8", string.Join(";", result));
            sql = "select sum(e1+1) from (select a1, a2, a1*a2 a12 from a) b(e1);";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("6", string.Join(";", result));
            sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b ;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("1;1;1", string.Join(";", result));
            sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("0,1;1,2", string.Join(";", result));
            sql = "select b4*b1+b2*b3 from (select 1 as b4, b3, count(*) as b1, sum(b1) b2 from b group by b3) a;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFromQuery"));
            Assert.AreEqual("1;4;9", string.Join(";", result));

            // FIXME
            sql = "select sum(e1+1) from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) b(e1);";
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;";
            // sql = "select * from (select max(b3) maxb3 from b) b where maxb3>1";    // WRONG!
            sql = "select a1 from a, (select max(b3) maxb3 from b) b where a1 < maxb3"; // WRONG!
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;"; // WRONG
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b1,b2,c100 from (select count(*) as b1, sum(b1) b2 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b1+b2,c100 from (select count(*) as b1, sum(b1) b2 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b4*b1+b2*b3 from (select 1 as b4, b3, count(*) as b1, sum(b1) b2 from b group by b3) a;"; // OK
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where b1>1 and c100>1;"; // ANSWER WRONG


            // FIXME: if we turn memo on, we have problems resolving columns
        }

        [TestMethod]
        public void TestExecSubquery()
        {
            var sql = "select a1, a3  from a where a.a1 = (select b1,b2 from b)";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select a1, a2  from a where a.a1 = (select b1 from b)";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticExecutionException"));
            sql = "select a1,a1,a3,a3, (select * from b where b2=2) from a where a1>1"; // * handling
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));

            // subquery in selection
            sql = "select a1,a1,a3,a3, (select b3 from b where b2=2) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,3");
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b1 and b2=3) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,4");
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b2 and b2=3) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,");

            // scalar subquery
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 3)";
            result = ExecuteSQL(sql); Assert.AreEqual("2,4", string.Join(";", result));
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 4)";
            result = ExecuteSQL(sql); Assert.AreEqual(0, result.Count);

            // correlated scalar subquery
            // test1: simple case
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);"; TU.ExecuteSQL(sql, "0,2");
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4);"; TU.ExecuteSQL(sql, "0,2;1,3");
            // test2: 2+ variables
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b1 = a1 and b3<3);"; TU.ExecuteSQL(sql, "0,2");
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b4 = a4 and b1 = a1 and b2<5);"; TU.ExecuteSQL(sql, "0,2;1,3;2,4");
            // test3: deep vars
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<4);"; TU.ExecuteSQL(sql, "0;1;2");
            // test4: deep/ref 2+ outside vars
            sql = "select a1,a2,a3  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            result = ExecuteSQL(sql); Assert.AreEqual("0,1,2;1,2,3", string.Join(";", result));
            sql = @" select a1+a2+a3  from a where a.a1 = (select b1 from b bo where b4 = a4 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            result = ExecuteSQL(sql); Assert.AreEqual("6", string.Join(";", result));
            sql = @"select a4  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
            and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            result = ExecuteSQL(sql); Assert.AreEqual("3;4;5", string.Join(";", result));
            sql = @"select a1 from a, b where a1=b1 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
            and b1 = (select b1 from b where b3 = a3 and bo.b3 = a3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and b3<5);";
            result = ExecuteSQL(sql); Assert.AreEqual("0;1;2", string.Join(";", result));
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
            and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            result = ExecuteSQL(sql); Assert.AreEqual("0;1;2", string.Join(";", result));
            sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1"; TU.ExecuteSQL(sql, "5");

            // in-list and in-subquery
            sql = "select a2 from a where a1 in (1,2,3);"; TU.ExecuteSQL(sql, "2;3");

            // disable it for now due to introduce table alias (a__1) confusing matching
            // sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));"; TU.ExecuteSQL(sql, "2;3");
            // sql = "select a2 from a where a1 in (select a2 from a a1 where exists (select * from a b where b.a3>=a1.a1+b.a1+1));"; //2,3
            // sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));"; //2,3
            // sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>a1+b.a1+1));"; //2,3, ok
            // sql = "select a2 from a where a1 in (select a2 from a a1 where exists (select * from a b where b.a3>=a.a1+b.a1+1));"; // 2
            // sql = "select a2 from a where a1 in (select a2 from a where exists(select * from a b where b.a3 >= a.a1 + b.a1 + 1));";

            // TODO: add not cases
        }
    }

    [TestClass]
    public class DMLTest
    {
        [TestMethod]
        public void TestInsert()
        {
            var sql = "insert into a values(1+2*3, 'something' ,'2019-09-01', 50.2, 50);";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as InsertStmt;
            Assert.AreEqual(5, stmt.vals_.Count);
            sql = "insert into test values(1+2,2*3,3,4);";
            var result = TU.ExecuteSQL(sql);
            sql = "insert into test select * from a where a1>1;";
            result = TU.ExecuteSQL(sql);
            sql = "insert into test select * from b where b1>1;";
            result = TU.ExecuteSQL(sql);
        }

        [TestMethod]
        public void TestCopy()
        {
            string filename = @"'..\..\..\data\test.tbl'";
            var sql = $"copy test from {filename};";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as CopyStmt;
            Assert.AreEqual(filename, stmt.fileName_);
            sql = $"copy test from {filename} where a1 >1;";
            var result = TU.ExecuteSQL(sql);
        }
    }

    [TestClass]
    public class ParserTest
    {
        [TestInitialize]
        public void TestInitialize()
        {
        }

        [TestMethod]
        public void TestColExpr()
        {
            ColExpr col = new ColExpr(null, "a", "a1", new IntType());
            Assert.AreEqual("a.a1", col.ToString());
        }

        [TestMethod]
        public void TestSelectStmt()
        {
            var sql = "with cte1 as (select * from a), cte2 as (select * from b) select a1,a1+a2 from cte1 where a1<6 group by a1, a1+a2 " +
                                "union select b2, b3 from cte2 where b2 > 3 group by b1, b1+b2 " +
                                "order by 2, 1 desc";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as SelectStmt;
            Assert.AreEqual(2, stmt.ctes_.Count);
            Assert.AreEqual(2, stmt.setqs_.Count);
            Assert.AreEqual(2, stmt.orders_.Count);
        }

        [TestMethod]
        public void TestOutputName()
        {
            var sql = "select a1 from(select b1 as a1 from b) c;";
            var result = TU.ExecuteSQL(sql); Assert.AreEqual("0;1;2", string.Join(";", result));
            sql = "select b1 from(select b1 as a1 from b) c;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select b1 from(select b1 as a1 from b) c(b1);"; TU.ExecuteSQL(sql, "0;1;2");

            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1"; TU.ExecuteSQL(sql, "5");
            sql = "select 5 as a6 from a where a6 > 2;";    // a6 is an output name
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select* from(select 5 as a6 from a where a1 > 1)b where a6 > 1;"; TU.ExecuteSQL(sql, "5");

            sql = "select a.b1+c.b1 from (select count(*) as b1 from b) a, (select c1 b1 from c) c where c.b1>1;"; TU.ExecuteSQL(sql, "5");
            sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) and b.b1 > (select c2 / 2 from c where c.c2 = b2);"; TU.ExecuteSQL(sql, "2");
            sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c3 = b3) and b.b1 > (select c2 / 2 from c where c.c3 = b3);"; TU.ExecuteSQL(sql, "2");
            sql = "select a1*a2 a12, a1 a6 from a;"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select * from (select a1*a2 a12, a1 a6 from a) b;"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select * from (select a1*a2 a12, a1 a9 from a) b(a12, a9);"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c(c1,c3) where c3>1) a;"; TU.ExecuteSQL(sql, "7");

            sql = "select a1 as a5, a2 from a where a2>2;"; TU.ExecuteSQL(sql, "2,3");

            sql = "select c1 as c2, c3 from c join d on c1 = d1 and c2=d1;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select c2, c1+c1 as c2, c3 from c join d on c1 = d1 and (c2+c2)>d1;"; TU.ExecuteSQL(sql, "1,0,2;2,2,3;3,4,4");

            // table alias
            sql = "select * from a , a;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select * from b , a b;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select a1,a2,b2 from b, a where a1=b1 and a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0,1,1;1,2,2;2,3,3");
        }
    }

    [TestClass]
    public class FunctionTest
    {
        [TestMethod]
        public void TestCast()
        {
            var sql = "select cast('2001-01-3' as date) + interval '30' day;"; TU.ExecuteSQL(sql, "2/2/2001 12:00:00 AM");
            sql = "select cast('2001-01-3' as date) + 30 days;"; TU.ExecuteSQL(sql, "2/2/2001 12:00:00 AM");
        }
    }

    [TestClass]
    public class CreatePlanTest
    {
        [TestMethod]
        public void TestConst()
        {
            var phyplan = "";
            var sql = "select repeat('ab', 2) from a;";
            var result = TU.ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "abab"));
            Assert.AreEqual("abab;abab;abab", string.Join(";", result));
            sql = "select 1+2*3, 1+2+a1 from a where a1+2+(1*5+1)>2*3 and 1+2=2+1;";
            result = TU.ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "and True")); // FIXME
            Assert.AreEqual(1, TU.CountStr(phyplan, "7,3+a.a1[0]"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "a.a1[0]+2+6>6")); // FIXME
            Assert.AreEqual("7,3;7,4;7,5", string.Join(";", result));
            sql = "select 1+20*3, 1+2.1+a1 from a where a1+2+(1*5+1)>2*4.6 and 1+2<2+1.4;";
            result = TU.ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "and True")); // FIXME
            Assert.AreEqual(1, TU.CountStr(phyplan, "61"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "9.2"));
            Assert.AreEqual("61,5.1", string.Join(";", result));
        }
    }

    [TestClass]
    public class GeneralTest
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestInitialize]
        public void TestInitialize()
        {
        }

        [TestMethod]
        public void TestExecNLJ()
        {
            var sql = "select a.a1 from a, b where a2 > 1";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(2 * 3, result.Count);
            sql = "select a.a1 from a, b where a2>2";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1 * 3, result.Count);
            sql = "select a.a2,a.a2,a3,a.a1+b2 from a,b where a.a1 > 1";
            result = ExecuteSQL(sql); Assert.AreEqual("3,3,4,3;3,3,4,4;3,3,4,5", string.Join(";", result));
            sql = @"select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            Assert.AreEqual(3, result[0].ColCount());
            Assert.AreEqual("0,1,2", result[0].ToString());
            Assert.AreEqual("1,1,2", result[1].ToString());
            Assert.AreEqual("2,1,2", result[2].ToString());
            Assert.AreEqual("0,2,3", result[3].ToString());
            Assert.AreEqual("1,2,3", result[4].ToString());
            Assert.AreEqual("2,2,3", result[5].ToString());
            Assert.AreEqual("0,3,4", result[6].ToString());
            Assert.AreEqual("1,3,4", result[7].ToString());
            Assert.AreEqual("2,3,4", result[8].ToString());
        }

        [TestMethod]
        public void TestExpr()
        {
            string phyplan;
            var sql = "select a2 from a where a1 between (1 , 2);";
            var result = ExecuteSQL(sql); Assert.AreEqual("2;3", string.Join(";", result));
            sql = "select count(a1) from a where 3>2 or 2<5";
            var answer = @"PhysicHashAgg   (actual rows=1)
                            Output: {count(a.a1)}[0]
                               Aggregates: count(a.a1[0])
                            -> PhysicScanTable a  (actual rows=3)
                                Output: a.a1[0]";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual("3", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);
        }

        [TestMethod]
        public void TestCTE()
        {
            var sql = @"with cte1 as (select* from a) select * from a where a1>1;"; TU.ExecuteSQL(sql, "2,3,4,5");
            sql = @"with cte1 as (select* from a) select * from cte1 where a1>1;"; TU.ExecuteSQL(sql, "2,3,4,5");
            sql = @"with cte1 as (select * from a),cte3 as (select * from cte1) select * from cte3 where a1>1"; TU.ExecuteSQL(sql, "2,3,4,5");
            sql = @"with cte1 as (select b3, max(b2) maxb2 from b where b1<1 group by b3)
                        select a1, maxb2 from a, cte1 where a.a3=cte1.b3 and a1<2;"; TU.ExecuteSQL(sql, "0,1");
            sql = @"with cte1 as (select* from a),	cte2 as (select* from b),
                    	cte3 as (with cte31 as (select* from c)
                                select* from cte2 , cte31 where b1 = c1)
                    select max(cte3.b1) from cte3;"; TU.ExecuteSQL(sql, "2");
        }

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var sql = "select a1+a2,a1-a2,a1*a2 from a;";
            var result = ExecuteSQL(sql); Assert.AreEqual(3, result.Count);
            sql = "select a1 from a where a2>1;";
            result = ExecuteSQL(sql); Assert.AreEqual("1;2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL(sql); Assert.AreEqual("2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 or a3> 3;";
            result = ExecuteSQL(sql); Assert.AreEqual("1;2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL("select a1 from a where a2>2");
            Assert.AreEqual("2", string.Join(";", result));
            sql = "select a1,a1,a3,a3 from a where a1>1;";
            result = ExecuteSQL(sql); Assert.AreEqual("2,2,4,4", string.Join(";", result));
            sql = "select a1,a1,a4,a4 from a where a1+a2>2;";
            result = ExecuteSQL(sql); Assert.AreEqual("1,1,4,4;2,2,5,5", string.Join(";", result));
            sql = "select a1,a1,a3,a3 from a where a1+a2+a3>2;";
            result = ExecuteSQL(sql); Assert.AreEqual("0,0,2,2;1,1,3,3;2,2,4,4", string.Join(";", result));
            sql = "select a1 from a where a1+a2>2;";
            result = ExecuteSQL(sql); Assert.AreEqual("1;2", string.Join(";", result));
        }

        [TestMethod]
        public void TestExecResult()
        {
            string sql = "select 2+6*3+2*6";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(32, result[0][0]);
            sql = "select substring('abc', 1, 2);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("ab", string.Join(";", result));
        }

        [TestMethod]
        public void TestExecProject()
        {
            string sql = "select b.a1 + a2 from (select a1,a2 from a, c) b";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            int i; Assert.AreEqual(1, result[0].ColCount());
            for (i = 0; i < 3; i++) Assert.AreEqual(1, result[i][0]);
            for (; i < 6; i++) Assert.AreEqual(3, result[i][0]);
            for (; i < 9; i++) Assert.AreEqual(5, result[i][0]);
            sql = "select b.a1 + a2 from (select a1,a3,a2 from a, c) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            Assert.AreEqual(1, result[0].ColCount());
            for (i = 0; i < 3; i++) Assert.AreEqual(1, result[i][0]);
            for (; i < 6; i++) Assert.AreEqual(3, result[i][0]);
            for (; i < 9; i++) Assert.AreEqual(5, result[i][0]);
            sql = "select b.a1 + a2 from (select a1,a2,a4,a2,a1 from a, c) b";
            result = ExecuteSQL(sql);
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
        }

        [TestMethod]
        public void TestFromQueryRemoval()
        {
            var sql = "select b1+b1 from (select b1 from b) a";
            var stmt = RawParser.ParseSingleSqlStatement(sql);
            stmt.Exec(); var phyplan = stmt.physicPlan_;
            var answer = @"PhysicFromQuery <a>  (actual rows=3)
                            Output: a.b1[0]+a.b1[0]
                            -> PhysicScanTable b  (actual rows=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan.PrintString(0));
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            stmt = RawParser.ParseSingleSqlStatement(sql);
            stmt.Exec(); phyplan = stmt.physicPlan_;    // FIXME: filter is still there
            answer = @"PhysicFilter  (actual rows=3)
                        Output: {a.b1+c.c1}[0]
                        Filter: c.c1[1]>1
                        -> PhysicNLJoin  (actual rows=9)
                            Output: a.b1[0]+c.c1[1],c.c1[1]
                            -> PhysicFromQuery <a> (actual rows=3)
                                Output: a.b1[0]
                                -> PhysicScanTable b (actual rows=3)
                                    Output: b.b1[0]
                            -> PhysicFromQuery <c> (actual rows=3, loops=3)
                                Output: c.c1[0]
                                -> PhysicScanTable c (actual rows=3, loops=3)
                                    Output: c.c1[0]";
            TU.PlanAssertEqual(answer, phyplan.PrintString(0));
            var result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("4", result[2].ToString());
            sql = "select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2-b1>1";
            stmt = RawParser.ParseSingleSqlStatement(sql);
            stmt.Exec(); phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin  (actual rows=3)
                        Output: a.b1[0]+c.c1[1]
                        Filter: c.c2[2]-a.b1[0]>1
                        -> PhysicFromQuery <a> (actual rows=3)
                            Output: a.b1[0]
                            -> PhysicScanTable b (actual rows=3)
                                Output: b.b1[0]
                        -> PhysicFromQuery <c> (actual rows=3, loops=3)
                            Output: c.c1[0],c.c2[1]
                            -> PhysicScanTable c (actual rows=3, loops=3)
                                Output: c.c1[0],c.c2[1]";
            TU.PlanAssertEqual(answer, phyplan.PrintString(0));
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;2;3", string.Join(";", result));
        }

        [TestMethod]
        public void TestJoin()
        {
            var sql = "select a.a1, b.b1 from a join b on a.a1=b.b1;";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashJoin   (actual rows=3)
                            Output: a.a1[0],b.b1[1]
                            Filter: a.a1[0]=b.b1[1]
                            -> PhysicScanTable a  (actual rows=3)
                                Output: a.a1[0]
                            -> PhysicScanTable b  (actual rows=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual("0,0;1,1;2,2", string.Join(";", result));
            sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2=c.c2;";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicHashJoin   (actual rows=3)
                        Output: a.a1[1],b.b1[2],a.a2[3],c.c2[0]
                        Filter: a.a2[3]=c.c2[0]
                        -> PhysicScanTable c  (actual rows=3)
                            Output: c.c2[1]
                        -> PhysicHashJoin   (actual rows=3)
                            Output: a.a1[0],b.b1[2],a.a2[1]
                            Filter: a.a1[0]=b.b1[2]
                            -> PhysicScanTable a  (actual rows=3)
                                Output: a.a1[0],a.a2[1]
                            -> PhysicScanTable b  (actual rows=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,0,1,1", result[0].ToString());
            Assert.AreEqual("1,1,2,2", result[1].ToString());
            Assert.AreEqual("2,2,3,3", result[2].ToString());
            sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2<c.c3;";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicNLJoin  (actual rows=6)
                        Output: a.a1[2],b.b1[3],a.a2[4],c.c2[0]
                        Filter: a.a2[4]<c.c3[1]
                        -> PhysicScanTable c (actual rows=3)
                            Output: c.c2[1],c.c3[2]
                        -> PhysicHashJoin  (actual rows=3, loops=3)
                            Output: a.a1[0],b.b1[2],a.a2[1]
                            Filter: a.a1[0]=b.b1[2]
                            -> PhysicScanTable a (actual rows=3, loops=3)
                                Output: a.a1[0],a.a2[1]
                            -> PhysicScanTable b (actual rows=3, loops=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual(6, result.Count);

            // hash join 
            sql = "select count(*) from a join b on a1 = b1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual("3", string.Join(";", result));
            sql = "select count(*) from a join b on a1 = b1 and a2 = b2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: a.a1[1]=b.b1[3] and a.a2[2]=b.b2[4]"));
            Assert.AreEqual("3", string.Join(";", result));
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where a1+b1=c1+d1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);

            // FIXME: becuase join order prevents push down - comparing below 2 cases
            sql = "select * from a, b, c where a1 = b1 and b2 = c2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual("0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", string.Join(";", result));
            sql = "select * from a, b, c where a1 = b1 and a1 = c1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: a.a1[0]=b.b1[4] and a.a1[0]=c.c1[8]"));
            Assert.AreEqual("0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", string.Join(";", result));

            // these queries depends on we can decide left/right side parameter dependencies
            var option = new QueryOption();
            option.optimize_.enable_subquery_to_markjoin_ = false;
            sql = "select a1+b1 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0;2;4", out _, option);
            sql = "select a1+b1 from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0;2;4", out _, option);
            sql = "select a2+c3 from a join c on a1=c1 where a1 < (select b2 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3)"; TU.ExecuteSQL(sql, "3;5;7", out _, option);
            sql = "select a2+c3 from c join a on a1=c1 where a1 < (select b2 from b join a on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3)"; TU.ExecuteSQL(sql, "3;5;7", out _, option);

            // FAILED
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1+d1"; // FIXME
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1 and a2+b2=d2;";
        }

        [TestMethod]
        public void TestAggregation()
        {
            var sql = "select a1, sum(a1) from a group by a2";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));
            sql = "select max(sum(a)+1) from a;";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("nested"));
            sql = "select a1, sum(a1) from a group by a1 having sum(a2) > a3;";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));  // FIXME: error message doesn't propogate
            sql = "select * from a having sum(a2) > a1;";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));

            sql = "select 'one', count(b1), count(*), avg(b1), min(b4), count(*), min(b2)+max(b3), sum(b2) from b where b3>1000;";
            TU.ExecuteSQL(sql, "one,0,0,,,0,,");
            sql = "select 'one', count(b1), count(*), avg(b1) from b where b3>1000 having avg(b2) is not null;";
            TU.ExecuteSQL(sql, "");

            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashAgg  (actual rows=2)
                            Output: 7,{4-a.a3/2}[0]*2+1+{sum(a.a1)}[1],{sum(a.a1)}[1]+{sum(a.a1+a.a2)}[2]*2
                            Aggregates: sum(a.a1[0]), sum(a.a1[0]+a.a2[2])
                            Group by: 4-a.a3[3]/2
                            -> PhysicScanTable a (actual rows=3)
                                Output: a.a1[0],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]
                        ";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual("7,3,2;7,4,19", string.Join(";", result));
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1,3,4,2;0,2,6,18", string.Join(";", result));
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0,1;1,2", string.Join(";", result));
            sql = "select a2, sum(a1) from a where a1>0 group by a2";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2,1;3,2", string.Join(";", result));
            sql = "select a3/2*2, sum(a3), count(a3), stddev_samp(a3) from a group by 1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2,5,2,0.707106781186548;4,4,1,", string.Join(";", result));
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2>1) a;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("7", string.Join(";", result));
            sql = "select d1, sum(d2) from (select c1/2, sum(c1) from (select b1, count(*) as a1 from b group by b1)c(c1, c2) group by c1/2) d(d1, d2) group by d1;";
            result = ExecuteSQL(sql); Assert.AreEqual("0,1;1,2", string.Join(";", result));
            sql = "select b1+b1 from b group by b1;";
            result = ExecuteSQL(sql); Assert.AreEqual("0;2;4", string.Join(";", result));
            sql = "select sum(b1+b1) from b group by b1;";
            result = ExecuteSQL(sql); Assert.AreEqual("0;2;4", string.Join(";", result));
            sql = "select 2+b1+b1+b2 from b group by b1,b2;";
            result = ExecuteSQL(sql); Assert.AreEqual("3;6;9", string.Join(";", result));
            sql = "select sum(2+b1+b1+b2) from b group by b1,b2;";
            result = ExecuteSQL(sql); Assert.AreEqual("3;6;9", string.Join(";", result));
            sql = "select max(b1) from (select sum(a1) from a)b(b1);";
            result = ExecuteSQL(sql); Assert.AreEqual("3", string.Join(";", result));
            sql = "select sum(e1+e1*3) from (select sum(a1) a12 from a) d(e1);";
            result = ExecuteSQL(sql); Assert.AreEqual("12", string.Join(";", result));
            sql = "select a1 from a group by a1 having sum(a2) > 2;";
            result = ExecuteSQL(sql); Assert.AreEqual("2", string.Join(";", result));
            sql = "select a1, sum(a1) from a group by a1 having sum(a2) > 2;";
            result = ExecuteSQL(sql); Assert.AreEqual("2,2", string.Join(";", result));
            sql = "select max(b1) from b having max(b1)>1;";
            result = ExecuteSQL(sql); Assert.AreEqual("2", string.Join(";", result));
            sql = "select a3, sum(a1) from a group by a3 having sum(a2) > a3/2;";
            result = ExecuteSQL(sql); Assert.AreEqual("3,1;4,2", string.Join(";", result));

            // failed:
            // sql = "select a1, sum(a1) from a group by a1 having sum(a2) > a3;";
            // sql = "select * from a having sum(a2) > 1;";
        }

        [TestMethod]
        public void TestSort()
        {
            var sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by a3";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("SemanticAnalyzeException"));

            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by 1";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicOrder   (actual rows=2)
                            Output: {4-a.a3/2}[0],{4-a.a3/2*2+1+min(a.a1)}[1],{avg(a.a4)+count(a.a1)}[2],{max(a.a1)+sum(a.a1+a.a2)*2}[3]
                            Order by: {4-a.a3/2}[0]
                            -> PhysicHashAgg   (actual rows=2)
                                Output: {4-a.a3/2}[0],{4-a.a3/2}[0]*2+1+{min(a.a1)}[1],{avg(a.a4)}[2]+{count(a.a1)}[3],{max(a.a1)}[4]+{sum(a.a1+a.a2)}[5]*2
                                Aggregates: min(a.a1[1]), avg(a.a4[2]), count(a.a1[1]), max(a.a1[1]), sum(a.a1[1]+a.a2[4])
                                Group by: {4-a.a3/2}[0]
                                -> PhysicScanTable a  (actual rows=3)
                                    Output: 4-a.a3[2]/2,a.a1[0],a.a4[3],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql); Assert.AreEqual("0,2,6,18;1,3,4,2", string.Join(";", result));
            sql = "select * from a where a1>0 order by a1;";
            result = ExecuteSQL(sql); Assert.AreEqual("1,2,3,4;2,3,4,5", string.Join(";", result));
            sql = " select count(a2) as ca2 from a group by a1/2 order by count(a2);";
            result = ExecuteSQL(sql); Assert.AreEqual("1;2", string.Join(";", result));
            sql = " select count(a2) as ca2 from a group by a1/2 order by 1;";
            result = ExecuteSQL(sql); Assert.AreEqual("1;2", string.Join(";", result));
        }

        [TestMethod]
        public void TestSetOps()
        {
        }

        [TestMethod]
        public void TestLimit()
        {
            var sql = "select a1,a1 from a limit 2;";
            var result = ExecuteSQL(sql); Assert.AreEqual("0,0;1,1", string.Join(";", result));
        }

        [TestMethod]
        public void TestIndex()
        {
            string phyplan;
            var option =new QueryOption();
            option.optimize_.use_memo_ = false; // because of costs

            var sql = "select * from d where 1*3-1=d1;";
            var result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("2,2,,5", string.Join(";", result));
            sql = "select * from d where 1<d1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("2,2,,5;3,3,5,6", string.Join(";", result));
        }

        [TestMethod]
        public void TestPushdown()
        {
            var option = new QueryOption();
            var sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1 and a1+b3>2";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicNLJoin   (actual rows=3)
                        Output: a.a2[0],a.a3[1],a.a1[2]+b.b2[3]
                        Filter: a.a1[2]+b.b3[4]>2
                        -> PhysicScanTable a  (actual rows=1)
                            Output: a.a2[1],a.a3[2],a.a1[0]
                            Filter: a.a1[0]>1
                        -> PhysicScanTable b  (actual rows=3)
                            Output: b.b2[1],b.b3[2]";
            TU.PlanAssertEqual(answer, phyplan);

            // FIXME: you can see c1+b1>2 is not pushed down
            sql = "select a1,b1,c1 from a,b,c where a1+b1+c1>5 and c1+b1>2";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicNLJoin  (actual rows=1)
                        Output: a.a1[0],b.b1[1],c.c1[2]
                        Filter: a.a1[0]+b.b1[1]+c.c1[2]>5
                        -> PhysicScanTable a (actual rows=3)
                            Output: a.a1[0]
                        -> PhysicNLJoin  (actual rows=3, loops=3)
                            Output: b.b1[1],c.c1[0]
                            Filter: c.c1[0]+b.b1[1]>2
                            -> PhysicScanTable c (actual rows=3, loops=3)
                                Output: c.c1[0]
                            -> PhysicScanTable b (actual rows=3, loops=9)
                                Output: b.b1[0]";
            Assert.AreEqual("2,2,2", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            option.optimize_.enable_subquery_to_markjoin_ = false;
            result = TU.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicScanTable a (actual rows=0)
                        Output: 1
                        Filter: a.a1[0]>@1
                        <ScalarSubqueryExpr> cached 1
                            -> PhysicScanTable b (actual rows=0)
                                Output: b.b1[0],#b.b2[1]
                                Filter: b.b2[1]>@2 and b.b1[0]>@3
                                <ScalarSubqueryExpr> 2
                                    -> PhysicScanTable c (actual rows=1, loops=3)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]
                                <ScalarSubqueryExpr> 3
                                    -> PhysicScanTable c (actual rows=1, loops=3)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]";
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicScanTable a (actual rows=0)
                        Output: 1
                        Filter: a.a1[0]>@1
                        <ScalarSubqueryExpr> cached 1
                            -> PhysicFilter  (actual rows=0)
                                Output: b.b1[1]
                                Filter: {#marker}[0]
                                -> PhysicSingleMarkJoin  (actual rows=0)
                                    Output: #marker,b.b1[0]
                                    Filter: b.b2[1]>c.c2[2] and c.c2[2]=b.b2[1]
                                    -> PhysicScanTable b (actual rows=0)
                                        Output: b.b1[0],b.b2[1]
                                        Filter: b.b1[0]>@3
                                        <ScalarSubqueryExpr> 3
                                            -> PhysicScanTable c (actual rows=1, loops=3)
                                                Output: c.c2[1]
                                                Filter: c.c2[1]=?b.b2[1]
                                    -> PhysicScanTable c (actual rows=0)
                                        Output: c.c2[1]";
            TU.PlanAssertEqual(answer, phyplan);
            sql = "select 1 from a where a.a1 >= (select b1 from b where b.b2 >= (select c2 from c where c.c2=b2) and b.b1*2 = ((select c2 from c where c.c2=b2)))";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("1;1", string.Join(";", result));

            // b3+c2 as a whole push to the outer join side
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter  (actual rows=27)
                        Output: {b.b3+c.c2}[1]
                        Filter: {#marker}[0]
                        -> PhysicSingleMarkJoin  (actual rows=27)
                            Output: #marker,{b.b3+c.c2}[0]
                            Filter: a.a1[1]>=b__1.b1[2] and b__1.b1[2]=a.a1[1]
                            -> PhysicNLJoin  (actual rows=27)
                                Output: {b.b3+c.c2}[1],a.a1[0]
                                -> PhysicScanTable a (actual rows=3)
                                    Output: a.a1[0]
                                    Filter: a.a2[1]>=@2
                                    <ScalarSubqueryExpr> 2
                                        -> PhysicScanTable c as c__2 (actual rows=1, loops=3)
                                            Output: c__2.c2[1]
                                            Filter: c__2.c1[0]=?a.a1[0]
                                -> PhysicNLJoin  (actual rows=9, loops=3)
                                    Output: b.b3[1]+c.c2[0]
                                    -> PhysicScanTable c (actual rows=3, loops=3)
                                        Output: c.c2[1]
                                    -> PhysicScanTable b (actual rows=3, loops=9)
                                        Output: b.b3[2]
                            -> PhysicScanTable b as b__1 (actual rows=3, loops=27)
                                Output: b__1.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);

            // key here is bo.b3=a3 show up in 3rd subquery
            sql = @"select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 
                        and bo.b3 = a3 and b3> 1) and b2<3);";
            option.optimize_.enable_subquery_to_markjoin_ = false;
            result = TU.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicScanTable a (actual rows=2)
                        Output: a.a1[0],#a.a2[1],#a.a3[2]
                        Filter: a.a1[0]=@1
                        <ScalarSubqueryExpr> 1
                            -> PhysicScanTable b as bo (actual rows=0, loops=3)
                                Output: bo.b1[0],#bo.b3[2]
                                Filter: bo.b2[1]=?a.a2[1] and bo.b1[0]=@2 and bo.b2[1]<3
                                <ScalarSubqueryExpr> 2
                                    -> PhysicScanTable b (actual rows=0, loops=9)
                                        Output: b.b1[0]
                                        Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[2]>1";
            Assert.AreEqual("0;1", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter  (actual rows=2)
                        Output: a.a1[1]
                        Filter: {#marker}[0]
                        -> PhysicSingleMarkJoin  (actual rows=3)
                            Output: #marker,a.a1[0]
                            Filter: a.a1[0]=bo.b1[3] and bo.b2[4]=a.a2[1]
                            -> PhysicScanTable a (actual rows=3)
                                Output: a.a1[0],a.a2[1],#a.a3[2]
                            -> PhysicScanTable b as bo (actual rows=0, loops=3)
                                Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                Filter: bo.b1[0]=@2 and bo.b2[1]<3
                                <ScalarSubqueryExpr> 2
                                    -> PhysicScanTable b (actual rows=0, loops=9)
                                        Output: b.b1[0]
                                        Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[2]>1";
            Assert.AreEqual("0;1", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);

            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            option.optimize_.enable_subquery_to_markjoin_ = false;
            result = TU.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicNLJoin  (actual rows=3)
                        Output: a.a1[2]
                        Filter: b.b2[3]=c.c2[0]
                        -> PhysicScanTable c (actual rows=3)
                            Output: c.c2[1],#c.c3[2]
                        -> PhysicHashJoin  (actual rows=1, loops=3)
                            Output: a.a1[2],b.b2[0]
                            Filter: a.a1[2]=b.b1[1]
                            -> PhysicScanTable b (actual rows=3, loops=3)
                                Output: b.b2[1],b.b1[0]
                            -> PhysicScanTable a (actual rows=1, loops=3)
                                Output: a.a1[0],#a.a2[1],#a.a3[2]
                                Filter: a.a1[0]=@1 and a.a2[1]=@3
                                <ScalarSubqueryExpr> 1
                                    -> PhysicFilter  (actual rows=0, loops=9)
                                        Output: bo.b1[0]
                                        Filter: bo.b2[1]=?a.a2[1] and bo.b2[1]<5 and bo.b1[0]=@2
                                        <ScalarSubqueryExpr> 2
                                            -> PhysicScanTable b as b__2 (actual rows=0, loops=81)
                                                Output: b__2.b1[0]
                                                Filter: b__2.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b__2.b3[2]>1
                                        -> PhysicFromQuery <bo> (actual rows=9, loops=9)
                                            Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                            -> PhysicNLJoin  (actual rows=9, loops=9)
                                                Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                                -> PhysicScanTable b as b_1 (actual rows=3, loops=9)
                                                    Output: b_1.b2[1],b_1.b3[2]
                                                -> PhysicScanTable b as b_2 (actual rows=3, loops=27)
                                                    Output: b_2.b1[0]
                                <ScalarSubqueryExpr> 3
                                    -> PhysicScanTable b as bo (actual rows=1, loops=9)
                                        Output: bo.b2[1],#bo.b3[2]
                                        Filter: bo.b1[0]=?a.a1[0] and bo.b2[1]=@4 and ?c.c3[2]<5
                                        <ScalarSubqueryExpr> 4
                                            -> PhysicScanTable b as b__4 (actual rows=0, loops=27)
                                                Output: b__4.b2[1]
                                                Filter: b__4.b4[3]=?a.a3[2]+1 and ?bo.b3[2]=?a.a3[2] and b__4.b3[2]>0";
            Assert.AreEqual("0;1;2", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);
            // run again with subquery expansion enabled
            // FIXME: b2<5 is not push down due to FromQuery barrier
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter  (actual rows=3)
                        Output: a.a1[1]
                        Filter: {#marker}[0] and bo.b2[2]<5 and bo.b1[3]=@2
                        <ScalarSubqueryExpr> 2
                            -> PhysicScanTable b as b__2 (actual rows=1, loops=3)
                                Output: b__2.b1[0]
                                Filter: b__2.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b__2.b3[2]>1
                        -> PhysicSingleMarkJoin  (actual rows=3)
                            Output: #marker,a.a1[0],bo.b2[2],bo.b1[3]
                            Filter: a.a1[0]=bo.b1[3] and bo.b2[2]=a.a2[1]
                            -> PhysicNLJoin  (actual rows=3)
                                Output: a.a1[2],a.a2[3]
                                Filter: b.b2[4]=c.c2[0]
                                -> PhysicScanTable c (actual rows=3)
                                    Output: c.c2[1],#c.c3[2]
                                -> PhysicHashJoin  (actual rows=3, loops=3)
                                    Output: a.a1[2],a.a2[3],b.b2[0]
                                    Filter: a.a1[2]=b.b1[1]
                                    -> PhysicScanTable b (actual rows=3, loops=3)
                                        Output: b.b2[1],b.b1[0]
                                    -> PhysicScanTable a (actual rows=3, loops=3)
                                        Output: a.a1[0],a.a2[1],#a.a3[2]
                                        Filter: a.a2[1]=@3
                                        <ScalarSubqueryExpr> 3
                                            -> PhysicFilter  (actual rows=1, loops=9)
                                                Output: bo.b2[1]
                                                Filter: {#marker}[0]
                                                -> PhysicSingleMarkJoin  (actual rows=1, loops=9)
                                                    Output: #marker,bo.b2[0]
                                                    Filter: bo.b2[0]=b__4.b2[2]
                                                    -> PhysicScanTable b as bo (actual rows=1, loops=9)
                                                        Output: bo.b2[1],#bo.b3[2]
                                                        Filter: bo.b1[0]=?a.a1[0] and ?c.c3[2]<5 and bo.b3[2]=?a.a3[2]
                                                    -> PhysicScanTable b as b__4 (actual rows=1, loops=9)
                                                        Output: b__4.b2[1]
                                                        Filter: b__4.b4[3]=?a.a3[2]+1 and b__4.b3[2]>0
                            -> PhysicFromQuery <bo> (actual rows=9, loops=3)
                                Output: bo.b2[1],bo.b1[0],#bo.b3[2]
                                -> PhysicNLJoin  (actual rows=9, loops=3)
                                    Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                    -> PhysicScanTable b as b_1 (actual rows=3, loops=3)
                                        Output: b_1.b2[1],b_1.b3[2]
                                    -> PhysicScanTable b as b_2 (actual rows=3, loops=9)
                                        Output: b_2.b1[0]";
            Assert.AreEqual("0;1;2", string.Join(";", result));
            TU.PlanAssertEqual(answer, phyplan);
        }

        [TestMethod]
        public void TestNull()
        {
            var phyplan = "";
            var sql = "select count(*) from r;";
            var result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("3", string.Join(";", result));
            sql = "select count(r1) from r;";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("1", string.Join(";", result));
            sql = "select " +
              "'|r3: null,null,3|', sum(r1), avg(r1), min(r1), max(r1), count(*), count(r1), " +
              "'|r3: 2,null,4|', sum(r3), avg(r3), min(r3), max(r3), count(r3) from r;";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("|r3: null,null,3|,3,3,3,3,3,1,|r3: 2,null,4|,6,3,2,4,2", string.Join(";", result));
            sql = "select a1, a2, r1 from r join a on a1=r1 or a2=r1;";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("2,3,3", string.Join(";", result));
            sql = "select a1, a2, r1 from r join a on a2=r1;";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual("2,3,3", string.Join(";", result));
            sql = "select null=null, null<>null, null>null, null<null, null>=null, null<=null, " +
                "null+null, null-null, null*null, null/null, " +
                "null+8, null-8, null*8, null/8, null/8 is null;";
            result = ExecuteSQL(sql, out phyplan); Assert.AreEqual(",,,,,,,,,,,,,,True", string.Join(";", result));
        }
    }
    }

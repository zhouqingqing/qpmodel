using adb;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

// failed tests:
// sql = "select 5+5 as a1 from a where a1 > 2;";

namespace test
{
    public class TestHelper
    {
        static internal string error_ = null;
        static internal List<Row> ExecuteSQL(string sql) => ExecuteSQL(sql, out _);

        static internal List<Row> ExecuteSQL(string sql, out string physicplan, OptimizeOption option = null)
        {
            try
            {
                error_ = null;

                var stmt = RawParser.ParseSqlStatement(sql);
                if (option != null)
                    stmt.optimizeOpt_ = option;
                var result = stmt.Exec(true);
                physicplan = stmt.physicPlan_.PrintString(0);
                return result;
            }
            catch (Exception e)
            {
                error_ = e.Message;
                physicplan = null;
                return null;
            }
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

        public static int CountStringOccurrences(string text, string pattern)
        {
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
    public class DDLTest
    {
        [TestMethod]
        public void TestCreateTable()
        {
            var sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a4 numeric(9));";
            try
            {
                var l = RawParser.ParseSqlStatement(sql) as CreateTableStmt;
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.Message.Contains("SemanticAnalyzeException"));
            }
            sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a5 numeric(9), a6 double, a7 date, a8 varchar(100));";
            var stmt = RawParser.ParseSqlStatement(sql) as CreateTableStmt;
            Assert.AreEqual(8, stmt.cols_.Count);
        }

        [TestMethod]
        public void TestAnalyze()
        {
            var sql = "analyze a;";
            var stmt = RawParser.ParseSqlStatement(sql) as AnalyzeStmt;
            stmt.Exec(true);
        }
    }

    [TestClass]
    public class TpchTest
    {
        [TestMethod]
        public void TestTpch()
        {
            // make sure all queries parsed
            var files = Directory.GetFiles(@"../../../tpch");
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var stmt = RawParser.ParseSqlStatement(sql);
                Console.WriteLine(sql);
            }
            Assert.AreEqual(22, files.Length);

            // parse and plan
            foreach (var v in files)
            {
                if (v.Contains("15") || v.Contains("08") || v.Contains("07"))
                    continue;
                var sql = File.ReadAllText(v);
                var stmt = RawParser.ParseSqlStatement(sql);
                stmt.Bind(null);
                Console.WriteLine(stmt.CreatePlan().PrintString(0));
            }

            // load data
            Tpch.LoadTables("0001");

            // execute queries
            string phyplan = "";
            OptimizeOption option = new OptimizeOption();

            for (int i = 0; i < 2; i++)
            {
                option.use_memo_ = i == 0;
                option.enable_subquery_to_markjoin_ = true;

                var result = TestHelper.ExecuteSQL(File.ReadAllText(files[0]), out _, option);
                Assert.AreEqual(4, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[1]), out _, option);
                Assert.AreEqual(0, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[2]), out phyplan, option);
                Assert.AreEqual(2, TestHelper.CountStringOccurrences(phyplan, "PhysicHashJoin"));
                Assert.AreEqual(8, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[3]), out _, option);
                Assert.AreEqual(5, result.Count);
                Assert.AreEqual("1-URGENT,33;2-HIGH,27;5-LOW,38;4-NOT SPECIFIED,37;3-MEDIUM,36", string.Join(";", result));
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[4]), out _, option);
                Assert.AreEqual(0, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[5]), out _, option);
                Assert.AreEqual("48091", string.Join(";", result));
                // q7 n1.n_name, n2.n_name matching
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[7]), out _, option);
                Assert.AreEqual("0,0", string.Join(";", result));
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[8]), out _, option);
                Assert.AreEqual(9, result.Count);
                TestHelper.ResultAreEqualNoOrder("MOROCCO,0,1687299;KENYA,0,577213;PERU,0,564370;UNITED STATES,0,274484;IRAQ,0,179599;" +
                                 "UNITED KINGDOM,0,2309469;IRAN,0,183369;ETHIOPIA,0,160941;ARGENTINA,0,121664",
                    string.Join(";", result));
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[9]), out _, option);
                Assert.AreEqual(43, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[10]), out _, option);
                Assert.AreEqual(0, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[11]), out _, option);
                Assert.AreEqual("SHIP,5,10;MAIL,5,5", string.Join(";", result));
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[12]), out _, option);
                Assert.AreEqual(100, result.Count);
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[13]), out _, option);
                Assert.AreEqual(1, result.Count);
                Assert.AreEqual(true, result[0].ToString().Contains("15.23"));
                // q15 cte
                // q16 binding error
                // q17 parameter join order
                //result = TestHelper.ExecuteSQL(File.ReadAllText(files[16]), out _, option);
                //Assert.AreEqual(0, result.Count);
                // q18 join filter push down
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[18]), out _, option);
                Assert.AreEqual(0, result.Count);
                // q20 parameter join order
                // q21 parameter join order
                result = TestHelper.ExecuteSQL(File.ReadAllText(files[21]), out _, option); 
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
            OptimizeOption option = new OptimizeOption();
            option.use_memo_ = true;
            option.enable_subquery_to_markjoin_ = true;

            var sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            var result = TestHelper.ExecuteSQL(sql, out _, option);
            var memo = Optimizer.memoset_[0];
            memo.CalcStats(out int tlogics, out int tphysics);
            Assert.AreEqual(9, memo.cgroups_.Count);
            Assert.AreEqual(18, tlogics); Assert.AreEqual(26, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select * from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);";
            option.enable_subquery_to_markjoin_ = false; // FIXME: they shall work together
            result = TestHelper.ExecuteSQL(sql, out _, option);
            Assert.AreEqual("0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5", string.Join(";", result));
            option.enable_subquery_to_markjoin_ = true;

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";
            result = TestHelper.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(15, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,c,b where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";   // FIXME: different #plans
            result = TestHelper.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(17, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3";
            result = TestHelper.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(6, memo.cgroups_.Count);
            Assert.AreEqual(11, tlogics); Assert.AreEqual(17, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TestHelper.ExecuteSQL(sql, out _, option);
            memo = Optimizer.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(9, memo.cgroups_.Count);
            Assert.AreEqual(18, tlogics); Assert.AreEqual(26, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select count(b1) from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TestHelper.ExecuteSQL(sql, out _, option);
            Assert.AreEqual("3", string.Join(";", result));

            sql = "select count(*) from a where a1 in (select b2 from b where b1 > 0) and a2 in (select b3 from b where b1 > 0);";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(0, TestHelper.CountStringOccurrences(phyplan, "PhysicFilter"));
            Assert.AreEqual("1", string.Join(";", result));

            sql = "select count(*) from (select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1 and a1>0) v;";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(0, TestHelper.CountStringOccurrences(phyplan, "PhysicFilter"));
            Assert.AreEqual("2", string.Join(";", result));

            sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));"; 
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            var answer = @"PhysicScanTable a  (actual rows = 3)
                            Output: a.a2[1]
                            Filter: a.a3[2]>@1
                            <ScalarSubqueryExpr> 1
                                -> PhysicHashAgg   (actual rows = 3)
                                    Output: {min(b.b1*2)}[0]
                                    Agg Core: min(b.b1[1]*2)
                                    -> PhysicScanTable b  (actual rows = 9)
                                        Output: b.b1[0]*2,b.b1[0],2,#b.b2[1]
                                        Filter: b.b2[1]>=@2 and b.b3[2]>@3
                                        <ScalarSubqueryExpr> 2
                                            -> PhysicScanTable c  (actual rows = 9)
                                                Output: c.c2[1]-1
                                                Filter: c.c2[1]=?b.b2[1]
                                        <ScalarSubqueryExpr> 3
                                            -> PhysicScanTable c  (actual rows = 9)
                                                Output: c.c2[1]
                                                Filter: c.c2[1]=?b.b2[1]";
            Assert.AreEqual("1;2;3", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);

            sql = "select count(*) from a, b,c,d where a1+b1+c1+d1=1;";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(0, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual("4", string.Join(";", result));

            // FIXME: a.a1+b.a1=5-c.a1, a.a1+b.a1+c.a1=5
            sql = "select a.a1,b.a1,c.a1, a.a1+b.a1+c.a1 from a, a b, a c where a.a1=5-b.a1-c.a1;";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "a.a1[0]=5-b.a1[1]-c.a1[2]"));
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual("2,2,1,5;2,1,2,5;1,2,2,5", string.Join(";", result));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2;";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "NLJoin"));
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual("0,1,2,3;1,2,3,4;2,3,4,5", string.Join(";", result));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2 join d on a4=2*d3;";
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "NLJoin"));
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "Filter: a.a1[0]=b.b1[4] or a.a3[2]=b.b3[5]"));
            Assert.AreEqual(2, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual("1,2,3,4", string.Join(";", result));


            Assert.IsTrue(option.use_memo_);
        }
    }

    [TestClass]
    public class SubqueryTest
    {
        internal List<Row> ExecuteSQL(string sql) => TestHelper.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TestHelper.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestExistsSubquery()
        {
            // exist-subquery
            var phyplan = "";
            var sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1);";
            var result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("1;2", string.Join(";", result));
            sql = "select a2 from a where exists (select * from a);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(0, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("1;2;3", string.Join(";", result));
            sql = "select a2 from a where not exists (select * from a b where b.a3>=a.a1+b.a1+1);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("3", string.Join(";", result));
            sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) and a2>2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(0, result.Count);
            sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("1;2;3", string.Join(";", result));
            sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("0,1;1,2", string.Join(";", result));
            // multiple subquery - FIXME: shall be two mark join
            sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
                     and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(2, TestHelper.CountStringOccurrences(phyplan, "PhysicMarkJoin"));
            Assert.AreEqual("2", string.Join(";", result));
        }

        [TestMethod]
        public void TestScalarSubquery()
        {
            var phyplan = "";
            var sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2);";
            var result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("0,2;1,3;2,4", string.Join(";", result));
            sql = "select a1, a3  from a where a.a2 = (select b1*2 from b where b2 = a2);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("1,3", string.Join(";", result));
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("0,2", string.Join(";", result));
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) and a2>1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("1,3", string.Join(";", result));
            sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = 3);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("2", string.Join(";", result));
            sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("2", string.Join(";", result));
            sql = @"select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 
                    and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<3);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("0;1", string.Join(";", result));
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 or b1 = (select b1 from b where b2 = 2*a1 and b3>1) and b2<3);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicSingleMarkJoin"));
            Assert.AreEqual("0;1;2", string.Join(";", result));

            //  OR condition failed sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) or a2>1;";
        }

        [TestMethod]
        public void TestExecSubFrom()
        {
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
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));
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
            var answer = @"PhysicHashAgg   (actual rows = 1)
                Output: {count(*)(0)}[0]
                Agg Core: count(*)(0)
                -> PhysicFromQuery <b>  (actual rows = 1)
                    Output: 0
                    -> PhysicScanTable a  (actual rows = 1)
                        Output: a.a1[0],a.a2[1],a.a3[2],a.a4[3]
                        Filter: a.a1[0]>1";  // observing no double push down
            TestHelper.PlanAssertEqual(answer, phyplan);
        }

        [TestMethod]
        public void TestExecSubquery()
        {
            var sql = "select a1, a3  from a where a.a1 = (select b1,b2 from b)";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));
            sql = "select a1, a2  from a where a.a1 = (select b1 from b)";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticExecutionException"));
            sql = "select a1,a1,a3,a3, (select * from b where b2=2) from a where a1>1"; // * handling
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));

            // subquery in selection
            sql = "select a1,a1,a3,a3, (select b3 from b where b2=2) from a where a1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,2,4,4,3", result[0].ToString());
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b1 and b2=3) from a where a1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,2,4,4,4", result[0].ToString());
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b2 and b2=3) from a where a1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual($"2,2,4,4,", result[0].ToString());

            // scalar subquery
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 3)";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,4", result[0].ToString());
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 4)";
            result = ExecuteSQL(sql);
            Assert.AreEqual(0, result.Count);

            // correlated scalar subquery
            // test1: simple case
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("0,2", result[0].ToString());
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("0,2", result[0].ToString());
            Assert.AreEqual("1,3", result[1].ToString());
            // test2: 2+ variables
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b1 = a1 and b3<3);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("0,2", result[0].ToString());
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b4 = a4 and b1 = a1 and b2<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0,2;1,3;2,4", string.Join(";", result));
            // test3: deep vars
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            // test4: deep/ref 2+ outside vars
            sql = "select a1,a2,a3  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("0,1,2", result[0].ToString());
            Assert.AreEqual("1,2,3", result[1].ToString());
            sql = @" select a1+a2+a3  from a where a.a1 = (select b1 from b bo where b4 = a4 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("6", result[0].ToString());
            sql = @"select a4  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
                and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("3;4;5", string.Join(";", result));
            sql = @"select a1 from a, b where a1=b1 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and b3<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            // in-list and in-subquery
            sql = "select a2 from a where a1 in (1,2,3);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2;3", string.Join(";", result));
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2;3", string.Join(";", result));

            // fail due to parameter dependency order: join shall flip the side
            sql = "select * from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);";
            sql = "select * from a join c on a1=c1 where a1 < (select b2 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3) x";

            // TODO: add not cases

            // failed due to fixcolumnordinal can't do expression as a whole (instead it can only do colref) or parameter dependency order
            //sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            //result = ExecuteSQL(sql);
            //Assert.AreEqual(1, result.Count);
            //Assert.AreEqual("5", result[0].ToString());
        }
    }

    [TestClass]
    public class DMLTest
    {
        [TestMethod]
        public void TestInsert()
        {
            var sql = "insert into a values(1+2*3, 'something' ,'2019-09-01', 50.2, 50);";
            var stmt = RawParser.ParseSqlStatement(sql) as InsertStmt;
            Assert.AreEqual(5, stmt.vals_.Count);
            sql = "insert into a values(1+2,2*3,3,4);";
            var result = TestHelper.ExecuteSQL(sql);
            sql = "insert into a select * from a where a1>1;";
            result = TestHelper.ExecuteSQL(sql);
            sql = "insert into a select * from b where b1>1;";
            result = TestHelper.ExecuteSQL(sql);
        }

        [TestMethod]
        public void TestCopy()
        {
            string filename = @"'..\..\..\data\test.tbl'";
            var sql = $"copy a from {filename};";
            var stmt = RawParser.ParseSqlStatement(sql) as CopyStmt;
            Assert.AreEqual(filename, stmt.fileName_);
            sql = $"copy a from {filename} where a1 >1;";
            var result = TestHelper.ExecuteSQL(sql);
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
            var stmt = RawParser.ParseSqlStatement(sql) as SelectStmt;
            Assert.AreEqual(2, stmt.ctes_.Count);
            Assert.AreEqual(2, stmt.setqs_.Count);
            Assert.AreEqual(2, stmt.orders_.Count);
        }
        [TestMethod]
        public void TestAlias()
        {
            var sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1";
            var result = TestHelper.ExecuteSQL(sql);
            Assert.AreEqual("5", string.Join(";", result));
            sql = "select 5 as a6 from a where a6 > 2;";    // a6 is an output alias
            result = TestHelper.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));
            sql = "select* from(select 5 as a6 from a where a1 > 1)b where a6 > 1;";
            result = TestHelper.ExecuteSQL(sql);
            Assert.AreEqual("5", string.Join(";", result));

            // failed tests:
            // alias not handled well: c(b1), a(b1)
            //                        sql = "select a.b1+c.b1 from (select count(*) as b1 from b) a, (select c1 b1 from c) c where c.b1>1;";
            // sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) and b.b1 > (select c2 / 2 from c where c.c2 = b2);";
            //  if you change second c.c2=b2 => c.c3=b3 then no problem, I think we confused them somewhere

        }
    }

    [TestClass]
    public class GeneralTest
    {
        internal List<Row> ExecuteSQL(string sql)=> TestHelper.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TestHelper.ExecuteSQL(sql, out physicplan);

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
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("3,3,4,3", result[0].ToString());
            Assert.AreEqual("3,3,4,4", result[1].ToString());
            Assert.AreEqual("3,3,4,5", result[2].ToString());
            sql = @"select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            Assert.AreEqual(3, result[0].values_.Count);
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
            var result = ExecuteSQL(sql);
            Assert.AreEqual("2;3", string.Join(";", result));

            sql = "select count(a1) from a where 3>2 or 2<5";
            var answer = @"PhysicHashAgg   (actual rows = 1)
                            Output: {count(a.a1)}[0]
                            Agg Core: count(a.a1[0])
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0]";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual("3", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);
        }

        [TestMethod]
        public void TestCTE()
        {
            var sql = @"with cte1 as (select* from a) select * from a where a1>1;";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,3,4,5", result[0].ToString());
            sql = @"with cte1 as (select* from a) select * from cte1 where a1>1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,3,4,5", result[0].ToString());
            sql = @"with cte1 as (select * from a),cte3 as (select * from cte1) select * from cte3 where a1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,3,4,5", result[0].ToString());
            sql = @"with cte1 as (select b3, max(b2) maxb2 from b where b1<1 group by b3)
                        select a1, maxb2 from a, cte1 where a.a3=cte1.b3 and a1<2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("0,1", result[0].ToString());
            sql = @"with cte1 as (select* from a),	cte2 as (select* from b),
                    	cte3 as (with cte31 as (select* from c)
                                select* from cte2 , cte31 where b1 = c1)
                    select max(cte3.b1) from cte3;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2", result[0].ToString());
        }

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var sql = "select * from a;";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(4, result[1].values_.Count);
            Assert.AreEqual(1, result[0].values_[1]);
            Assert.AreEqual(2, result[1].values_[1]);
            Assert.AreEqual(3, result[2].values_[1]);
            sql = "select a1+a2,a1-a2,a1*a2 from a;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            sql = "select a1 from a where a2>1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 or a3> 3;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;2", string.Join(";", result));
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL("select a1 from a where a2>2");
            Assert.AreEqual("2", string.Join(";", result));
            sql = "select a1,a1,a3,a3 from a where a1>1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2,2,4,4", string.Join(";", result));
            sql = "select a1,a1,a4,a4 from a where a1+a2>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1,1,4,4;2,2,5,5", string.Join(";", result));
            sql = "select a1,a1,a3,a3 from a where a1+a2+a3>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,0,2,2", result[0].ToString());
            Assert.AreEqual("1,1,3,3", result[1].ToString());
            Assert.AreEqual("2,2,4,4", result[2].ToString());
            sql = "select a1 from a where a1+a2>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;2", string.Join(";", result));
        }

        [TestMethod]
        public void TestExecResult()
        {
            string sql = "select 2+6*3+2*6";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(32, result[0].values_[0]);
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
            int i; Assert.AreEqual(1, result[0].values_.Count);
            for (i = 0; i < 3; i++) Assert.AreEqual(1, result[i].values_[0]);
            for (; i < 6; i++) Assert.AreEqual(3, result[i].values_[0]);
            for (; i < 9; i++) Assert.AreEqual(5, result[i].values_[0]);
            sql = "select b.a1 + a2 from (select a1,a3,a2 from a, c) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual(9, result.Count);
            Assert.AreEqual(1, result[0].values_.Count);
            for (i = 0; i < 3; i++) Assert.AreEqual(1, result[i].values_[0]);
            for (; i < 6; i++) Assert.AreEqual(3, result[i].values_[0]);
            for (; i < 9; i++) Assert.AreEqual(5, result[i].values_[0]);
            sql = "select b.a1 + a2 from (select a1,a2,a4,a2,a1 from a, c) b";
            result = ExecuteSQL(sql);
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));
        }

        [TestMethod]
        public void TestFromQueryRemoval()
        {
            var sql = "select b1+b1 from (select b1 from b) a";
            var stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); var phyplan = stmt.physicPlan_;
            var answer = @"PhysicFromQuery <a>  (actual rows = 3)
                            Output: a.b1[0]+a.b1[0]
                            -> PhysicScanTable b  (actual rows = 3)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); phyplan = stmt.physicPlan_;    // FIXME: filter is still there
            answer = @"PhysicFilter   (actual rows = 3)
                        Output: {a.b1+c.c1}[0]
                        Filter: c.c1[1]>1
                        -> PhysicNLJoin   (actual rows = 9)
                            Output: a.b1[0]+c.c1[1],c.c1[1]
                            -> PhysicFromQuery <a>  (actual rows = 3)
                                Output: a.b1[0]
                                -> PhysicScanTable b  (actual rows = 3)
                                    Output: b.b1[0]
                            -> PhysicFromQuery <c>  (actual rows = 9)
                                Output: c.c1[0]
                                -> PhysicScanTable c  (actual rows = 9)
                                    Output: c.c1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            var result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("4", result[2].ToString());
            sql = "select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2-b1>1";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin   (actual rows = 3)
                        Output: a.b1[0]+c.c1[1]
                        Filter: c.c2[2]-a.b1[0]>1
                        -> PhysicFromQuery <a>  (actual rows = 3)
                            Output: a.b1[0]
                            -> PhysicScanTable b  (actual rows = 3)
                                Output: b.b1[0]
                        -> PhysicFromQuery <c>  (actual rows = 9)
                            Output: c.c1[0],c.c2[1]
                            -> PhysicScanTable c  (actual rows = 9)
                                Output: c.c1[0],c.c2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            result = ExecuteSQL(sql);
            Assert.AreEqual("1;2;3", string.Join(";", result));
        }

        [TestMethod]
        public void TestJoin()
        {
            var sql = "select a.a1, b.b1 from a join b on a.a1=b.b1;";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashJoin   (actual rows = 3)
                            Output: a.a1[0],b.b1[1]
                            Filter: a.a1[0]=b.b1[1]
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0]
                            -> PhysicScanTable b  (actual rows = 3)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual("0,0;1,1;2,2", string.Join(";", result));
            sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2=c.c2;";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicHashJoin   (actual rows = 3)
                        Output: a.a1[1],b.b1[2],a.a2[3],c.c2[0]
                        Filter: a.a2[3]=c.c2[0]
                        -> PhysicScanTable c  (actual rows = 3)
                            Output: c.c2[1]
                        -> PhysicHashJoin   (actual rows = 3)
                            Output: a.a1[0],b.b1[2],a.a2[1]
                            Filter: a.a1[0]=b.b1[2]
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0],a.a2[1]
                            -> PhysicScanTable b  (actual rows = 3)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,0,1,1", result[0].ToString());
            Assert.AreEqual("1,1,2,2", result[1].ToString());
            Assert.AreEqual("2,2,3,3", result[2].ToString());
            sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2<c.c3;";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicNLJoin   (actual rows = 6)
                        Output: a.a1[2],b.b1[3],a.a2[4],c.c2[0]
                        Filter: a.a2[4]<c.c3[1]
                        -> PhysicScanTable c  (actual rows = 3)
                            Output: c.c2[1],c.c3[2]
                        -> PhysicHashJoin   (actual rows = 9)
                            Output: a.a1[0],b.b1[2],a.a2[1]
                            Filter: a.a1[0]=b.b1[2]
                            -> PhysicScanTable a  (actual rows = 9)
                                Output: a.a1[0],a.a2[1]
                            -> PhysicScanTable b  (actual rows = 9)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual(6, result.Count);

            // hash join 
            sql = "select count(*) from a join b on a1 = b1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "PhysicHashJoin"));
            Assert.AreEqual("3", string.Join(";", result));
            sql = "select count(*) from a join b on a1 = b1 and a2 = b2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "Filter: a.a1[1]=b.b1[3] and a.a2[2]=b.b2[4]"));
            Assert.AreEqual("3", string.Join(";", result));
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TestHelper.CountStringOccurrences(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where a1+b1=c1+d1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TestHelper.CountStringOccurrences(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);

            // FIXME: becuase join order prevents push down - comparing below 2 cases
            sql = "select * from a, b, c where a1 = b1 and b2 = c2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(2, TestHelper.CountStringOccurrences(phyplan, "PhysicHashJoin"));
            Assert.AreEqual("0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", string.Join(";", result));
            sql = "select * from a, b, c where a1 = b1 and a1 = c1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "HashJoin"));
            Assert.AreEqual(1, TestHelper.CountStringOccurrences(phyplan, "Filter: a.a1[0]=b.b1[4] and a.a1[0]=c.c1[8]"));
            Assert.AreEqual("0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", string.Join(";", result));

            // FAILED
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1+d1"; // FIXME
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1 and a2+b2=d2;";
        }

        [TestMethod]
        public void TestAggregation()
        {
            var sql = "select a1, sum(a1) from a group by a2";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));

            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashAgg   (actual rows = 2)
                            Output: 7,{4-a.a3/2}[0]*2+1+{sum(a.a1)}[1],{sum(a.a1)}[1]+{sum(a.a1+a.a2)}[2]*2
                            Agg Core: sum(a.a1[0]), sum(a.a1[0]+a.a2[2])
                        Group by: 4-a.a3[3]/2
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual("7,3,2;7,4,19", string.Join(";", result));
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1,3,4,2;0,2,6,18", string.Join(";", result));
            sql = "select sum(b1) from b where b3>1000;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(0, result.Count);   // FIXME: shall be a null
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual("0,1;1,2", string.Join(";", result));
            sql = "select a2, sum(a1) from a where a1>0 group by a2";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2,1;3,2", string.Join(";", result));
            sql = "select a3/2*2 from a group by 1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("2;4", string.Join(";", result));
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2>1) a;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("7", string.Join(";", result));
        }

        [TestMethod]
        public void TestSort()
        {
           var sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by a3";
           var result = ExecuteSQL(sql); Assert.IsNull(result);
           Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));

            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by 1";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicOrder   (actual rows = 2)
                            Output: {4-a.a3/2}[0],{4-a.a3/2*2+1+min(a.a1)}[1],{avg(a.a4)+count(a.a1)}[2],{max(a.a1)+sum(a.a1+a.a2)*2}[3]
                            Order by: {4-a.a3/2}[0]
                            -> PhysicHashAgg   (actual rows = 2)
                                Output: {4-a.a3/2}[0],{4-a.a3/2}[0]*2+1+{min(a.a1)}[1],{avg(a.a4)}[2]+{count(a.a1)}[3],{max(a.a1)}[4]+{sum(a.a1+a.a2)}[5]*2
                                Agg Core: min(a.a1[1]), avg(a.a4[2]), count(a.a1[1]), max(a.a1[1]), sum(a.a1[1]+a.a2[4])
                                Group by: {4-a.a3/2}[0]
                                -> PhysicScanTable a  (actual rows = 3)
                                    Output: 4-a.a3[2]/2,a.a1[0],a.a4[3],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql);
            Assert.AreEqual("1,3,4,2;0,2,6,18", string.Join(";", result));
            sql = "select * from a where a1>0 order by a1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual("1,2,3,4;2,3,4,5", string.Join(";", result));
        }

        [TestMethod]
        public void TestPushdown()
        {
            OptimizeOption option = new OptimizeOption();
            var sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1 and a1+b3>2";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicNLJoin   (actual rows = 3)
                        Output: a.a2[0],a.a3[1],a.a1[2]+b.b2[3]
                        Filter: a.a1[2]+b.b3[4]>2
                        -> PhysicScanTable a  (actual rows = 1)
                            Output: a.a2[1],a.a3[2],a.a1[0]
                            Filter: a.a1[0]>1
                        -> PhysicScanTable b  (actual rows = 3)
                            Output: b.b2[1],b.b3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan);

            // FIXME: you can see c1+b1>2 is not pushed down
            sql = "select a1,b1,c1 from a,b,c where a1+b1+c1>5 and c1+b1>2";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicNLJoin   (actual rows = 1)
                        Output: a.a1[0],b.b1[1],c.c1[2]
                        Filter: a.a1[0]+b.b1[1]+c.c1[2]>5
                        -> PhysicScanTable a  (actual rows = 3)
                            Output: a.a1[0]
                        -> PhysicNLJoin   (actual rows = 9)
                            Output: b.b1[1],c.c1[0]
                            Filter: c.c1[0]+b.b1[1]>2
                            -> PhysicScanTable c  (actual rows = 9)
                                Output: c.c1[0]
                            -> PhysicScanTable b  (actual rows = 27)
                                Output: b.b1[0]";
            Assert.AreEqual("2,2,2", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            option.enable_subquery_to_markjoin_ = false;
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicScanTable a  (actual rows = 0)
                        Output: 1
                        Filter: a.a1[0]>@1
                        <ScalarSubqueryExpr> 1
                            -> PhysicScanTable b  (actual rows = 0)
                                Output: b.b1[0],#b.b2[1]
                                Filter: b.b2[1]>@2 and b.b1[0]>@3
                                <ScalarSubqueryExpr> 2
                                    -> PhysicScanTable c  (actual rows = 9)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]
                                <ScalarSubqueryExpr> 3
                                    -> PhysicScanTable c  (actual rows = 9)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicScanTable a  (actual rows = 0)
                        Output: 1
                        Filter: a.a1[0]>@1
                        <ScalarSubqueryExpr> 1
                            -> PhysicFilter   (actual rows = 0)
                                Output: b.b1[1]
                                Filter: {#marker}[0] and b.b2[2]>c.c2[3] and b.b1[1]>@3
                                <ScalarSubqueryExpr> 3
                                    -> PhysicScanTable c  (actual rows = 9)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]
                                -> PhysicSingleMarkJoin   (actual rows = 9)
                                    Output: #marker,b.b1[0],b.b2[1],c.c2[2]
                                    Filter: c.c2[2]=?b.b2[1]
                                    -> PhysicScanTable b  (actual rows = 9)
                                        Output: b.b1[0],b.b2[1]
                                    -> PhysicScanTable c  (actual rows = 27)
                                        Output: c.c2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan);

            // b3+c2 as a whole push to the outer join side
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter   (actual rows = 27)
                        Output: {b.b3+c.c2}[1]
                        Filter: {#marker}[0] and a.a1[2]>=b.b1[3] and a.a2[4]>=@2
                        <ScalarSubqueryExpr> 2
                            -> PhysicScanTable c  (actual rows = 27)
                                Output: c.c2[1]
                                Filter: c.c1[0]=?a.a1[0]
                        -> PhysicSingleMarkJoin   (actual rows = 27)
                            Output: #marker,{b.b3+c.c2}[0],a.a1[1],b.b1[3],a.a2[2]
                            Filter: b.b1[3]=?a.a1[0]
                            -> PhysicNLJoin   (actual rows = 27)
                                Output: {b.b3+c.c2}[2],a.a1[0],a.a2[1]
                                -> PhysicScanTable a  (actual rows = 3)
                                    Output: a.a1[0],a.a2[1]
                                -> PhysicNLJoin   (actual rows = 27)
                                    Output: b.b3[1]+c.c2[0]
                                    -> PhysicScanTable c  (actual rows = 9)
                                        Output: c.c2[1]
                                    -> PhysicScanTable b  (actual rows = 27)
                                        Output: b.b3[2]
                            -> PhysicScanTable b  (actual rows = 81)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan);

            // key here is bo.b3=a3 show up in 3rd subquery
            sql = @"select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 
                        and bo.b3 = a3 and b3> 1) and b2<3);";
            option.enable_subquery_to_markjoin_ = false;
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicScanTable a  (actual rows = 2)
                        Output: a.a1[0],#a.a2[1],#a.a3[2]
                        Filter: a.a1[0]=@1
                        <ScalarSubqueryExpr> 1
                            -> PhysicScanTable b as bo  (actual rows = 2)
                                Output: bo.b1[0],#bo.b3[2]
                                Filter: bo.b2[1]=?a.a2[1] and bo.b1[0]=@2 and bo.b2[1]<3
                                <ScalarSubqueryExpr> 2
                                    -> PhysicScanTable b  (actual rows = 3)
                                        Output: b.b1[0]
                                        Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[2]>1";
            Assert.AreEqual("0;1", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter   (actual rows = 2)
                        Output: a.a1[1]
                        Filter: {#marker}[0] and a.a1[1]=bo.b1[2]
                        -> PhysicSingleMarkJoin   (actual rows = 3)
                            Output: #marker,a.a1[0],bo.b1[3]
                            Filter: bo.b2[4]=?a.a2[1] and bo.b1[3]=@2 and bo.b2[4]<3
                            <ScalarSubqueryExpr> 2
                                -> PhysicScanTable b
                                    Output: b.b1[0]
                                    Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[2]>1
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0],#a.a2[1],#a.a3[2]
                            -> PhysicScanTable b as bo  (actual rows = 9)
                                Output: bo.b1[0],bo.b2[1],#bo.b3[2]";
                                Assert.AreEqual("0;1", string.Join(";", result));
            Assert.AreEqual("0;1", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);

            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            option.enable_subquery_to_markjoin_ = false;
            result = TestHelper.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicNLJoin   (actual rows = 3)
                        Output: a.a1[2]
                        Filter: b.b2[3]=c.c2[0]
                        -> PhysicScanTable c  (actual rows = 3)
                            Output: c.c2[1],#c.c3[2]
                        -> PhysicHashJoin   (actual rows = 3)
                            Output: a.a1[2],b.b2[0]
                            Filter: a.a1[2]=b.b1[1]
                            -> PhysicScanTable b  (actual rows = 9)
                                Output: b.b2[1],b.b1[0]
                            -> PhysicScanTable a  (actual rows = 3)
                                Output: a.a1[0],#a.a2[1],#a.a3[2]
                                Filter: a.a1[0]=@1 and a.a2[1]=@3
                                <ScalarSubqueryExpr> 1
                                    -> PhysicFilter   (actual rows = 3)
                                        Output: bo.b1[0]
                                        Filter: bo.b2[1]=?a.a2[1] and bo.b2[1]<5 and bo.b1[0]=@2
                                        <ScalarSubqueryExpr> 2
                                            -> PhysicScanTable b  (actual rows = 27)
                                                Output: b.b1[0]
                                                Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b.b3[2]>1
                                        -> PhysicFromQuery <bo>  (actual rows = 81)
                                            Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                            -> PhysicNLJoin   (actual rows = 81)
                                                Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                                -> PhysicScanTable b as b_1  (actual rows = 27)
                                                    Output: b_1.b2[1],b_1.b3[2]
                                                -> PhysicScanTable b as b_2  (actual rows = 81)
                                                    Output: b_2.b1[0]
                                <ScalarSubqueryExpr> 3
                                    -> PhysicScanTable b as bo  (actual rows = 9)
                                        Output: bo.b2[1],#bo.b3[2]
                                        Filter: bo.b1[0]=?a.a1[0] and bo.b2[1]=@4 and ?c.c3[2]<5
                                        <ScalarSubqueryExpr> 4
                                            -> PhysicScanTable b  (actual rows = 9)
                                                Output: b.b2[1]
                                                Filter: b.b4[3]=?a.a3[2]+1 and ?bo.b3[2]=?a.a3[2] and b.b3[2]>0";
            Assert.AreEqual("0;1;2", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);
            // run again with subquery expansion enabled
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter   (actual rows = 3)
                        Output: a.a1[1]
                        Filter: {#marker}[0] and a.a1[1]=b.b1[2] and b.b2[3]=c.c2[4] and a.a1[1]=bo.b1[5] and a.a2[6]=@3
                        <ScalarSubqueryExpr> 3
                            -> PhysicFilter   (actual rows = 27)
                                Output: bo.b2[1]
                                Filter: {#marker}[0] and bo.b1[2]=?a.a1[0] and bo.b2[1]=b.b2[3] and ?c.c3[2]<5
                                -> PhysicSingleMarkJoin   (actual rows = 81)
                                    Output: #marker,bo.b2[0],bo.b1[1],b.b2[3]
                                    Filter: b.b4[4]=?a.a3[2]+1 and ?bo.b3[2]=?a.a3[2] and b.b3[5]>0
                                    -> PhysicScanTable b as bo  (actual rows = 81)
                                        Output: bo.b2[1],bo.b1[0],#bo.b3[2]
                                    -> PhysicScanTable b  (actual rows = 243)
                                        Output: b.b2[1],b.b4[3],b.b3[2]
                        -> PhysicSingleMarkJoin   (actual rows = 27)
                            Output: #marker,a.a1[0],b.b1[1],b.b2[2],c.c2[3],bo.b1[5],a.a2[4]
                            Filter: bo.b2[6]=?a.a2[1] and bo.b1[5]=@2 and bo.b2[6]<5
                            <ScalarSubqueryExpr> 2
                                -> PhysicScanTable b
                                    Output: b.b1[0]
                                    Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b.b3[2]>1
                            -> PhysicNLJoin   (actual rows = 27)
                                Output: a.a1[2],b.b1[3],b.b2[4],c.c2[0],a.a2[5]
                                -> PhysicScanTable c  (actual rows = 3)
                                    Output: c.c2[1],#c.c3[2]
                                -> PhysicNLJoin   (actual rows = 27)
                                    Output: a.a1[2],b.b1[0],b.b2[1],a.a2[3]
                                    -> PhysicScanTable b  (actual rows = 9)
                                        Output: b.b1[0],b.b2[1]
                                    -> PhysicScanTable a  (actual rows = 27)
                                        Output: a.a1[0],a.a2[1],#a.a3[2]
                            -> PhysicFromQuery <bo>  (actual rows = 243)
                                Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                -> PhysicNLJoin   (actual rows = 243)
                                    Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                    -> PhysicScanTable b as b_1  (actual rows = 81)
                                        Output: b_1.b2[1],b_1.b3[2]
                                    -> PhysicScanTable b as b_2  (actual rows = 243)
                                        Output: b_2.b1[0]";
            Assert.AreEqual("0;1;2", string.Join(";", result));
            TestHelper.PlanAssertEqual(answer, phyplan);
        }
    }
}

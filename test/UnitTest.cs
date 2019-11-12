using adb;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

namespace test
{
    public class TestHelper
    {
        static internal string error_ = null;
        static internal List<Row> ExecuteSQL(string sql)
        {
            try
            {
                error_ = null;

                var stmt = RawParser.ParseSqlStatement(sql);
                return stmt.Exec();
            }
            catch (Exception e)
            {
                error_ = e.Message;
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
    }

    [TestClass]
    public class UtilsTest
    {
        [TestMethod]
        public void TestCSVReader()
        {
            List<string> r = new List<string>();
            Utils.ReadCsvLine(@"d:\test.csv",
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
    }

    [TestClass]
    public class TpchTest
    {
        [TestMethod]
        public void TestTpch()
        {
            var files = Directory.GetFiles(@"../../../tpch");
            Array.Sort(files);
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var stmt = RawParser.ParseSqlStatement(sql);
                Console.WriteLine(sql);
            }
            Assert.AreEqual(22, files.Length);
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
            string filename = @"'d:\test.csv'";
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
            ColExpr col = new ColExpr(null, "a", "a1");
            Assert.AreEqual("a.a1[0]", col.ToString());
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
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("5", result[0].ToString());
            // failed tests:
            // alias not handled well: c(b1), a(b1)
            //                        sql = "select a.b1+c.b1 from (select count(*) as b1 from b) a, (select c1 b1 from c) c where c.b1>1;";
        }
    }

    [TestClass]
    public class OptimizerTest
    {
        private TestContext testContextInstance;

        internal List<Row> ExecuteSQL(string sql)
        {
            return TestHelper.ExecuteSQL(sql);
        }

        /// <summary>
        ///  Gets or sets the test context which provides
        ///  information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get { return testContextInstance; }
            set { testContextInstance = value; }
        }

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
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("1", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("5", result[2].ToString());
            sql = "select a3 from (select a1,a3 from a) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("4", result[2].ToString());
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("4", result[2].ToString());
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
            Assert.AreEqual($"2,2,4,4,{Int64.MaxValue}", result[0].ToString());

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
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,2", result[0].ToString());
            Assert.AreEqual("1,3", result[1].ToString());
            Assert.AreEqual("2,4", result[2].ToString());
            // test3: deep vars
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0", result[0].ToString());
            Assert.AreEqual("1", result[1].ToString());
            Assert.AreEqual("2", result[2].ToString());
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
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("3", result[0].ToString());
            Assert.AreEqual("4", result[1].ToString());
            Assert.AreEqual("5", result[2].ToString());
            sql = @"select a1 from a, b where a1=b1 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and b3<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0", result[0].ToString());
            Assert.AreEqual("1", result[1].ToString());
            Assert.AreEqual("2", result[2].ToString());
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0", result[0].ToString());
            Assert.AreEqual("1", result[1].ToString());
            Assert.AreEqual("2", result[2].ToString());
            // failed due to fixcolumnordinal can't do expression as a whole (instead it can only do colref)
            //sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            //result = ExecuteSQL(sql);
            //Assert.AreEqual(1, result.Count);
            //Assert.AreEqual("5", result[0].ToString());
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
            Assert.AreEqual(2, result.Count);
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            result = ExecuteSQL("select a1 from a where a2>2");
            Assert.AreEqual(1, result.Count);
            sql = "select a1,a1,a3,a3 from a where a1>1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,2,4,4", result[0].ToString());
            sql = "select a1,a1,a4,a4 from a where a1+a2>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,1,4,4", result[0].ToString());
            Assert.AreEqual("2,2,5,5", result[1].ToString());
            sql = "select a1,a1,a3,a3 from a where a1+a2+a3>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,0,2,2", result[0].ToString());
            Assert.AreEqual("1,1,3,3", result[1].ToString());
            Assert.AreEqual("2,2,4,4", result[2].ToString());
            sql = "select a1 from a where a1+a2>2;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1", result[0].ToString());
            Assert.AreEqual("2", result[1].ToString());
        }

        [TestMethod]
        public void TestExecResult()
        {
            string sql = "select 2+6*3+2*6";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(32, result[0].values_[0]);
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
            var answer = @"PhysicFromQuery <a>  (rows = 3)
                            Output: a.b1[0]+a.b1[0]
                            -> PhysicScanTable b  (rows = 3)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); phyplan = stmt.physicPlan_;    // FIXME: filter is still there
            answer = @"PhysicFilter   (rows = 3)
                        Output: {a.b1+c.c1}[0]
                        Filter: c.c1[1]>1
                        -> PhysicNLJoin   (rows = 9)
                            Output: a.b1[0]+c.c1[1],c.c1[1]
                            -> PhysicFromQuery <a>  (rows = 3)
                                Output: a.b1[0]
                                -> PhysicScanTable b  (rows = 3)
                                    Output: b.b1[0]
                            -> PhysicFromQuery <c>  (rows = 9)
                                Output: c.c1[0]
                                -> PhysicScanTable c  (rows = 9)
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
            answer = @"PhysicNLJoin   (rows = 3)
                        Output: a.b1[0]+c.c1[1]
                        Filter: c.c2[2]-a.b1[0]>1
                        -> PhysicFromQuery <a>  (rows = 3)
                            Output: a.b1[0]
                            -> PhysicScanTable b  (rows = 3)
                                Output: b.b1[0]
                        -> PhysicFromQuery <c>  (rows = 9)
                            Output: c.c1[0],c.c2[1]
                            -> PhysicScanTable c  (rows = 9)
                                Output: c.c1[0],c.c2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("1", result[0].ToString());
            Assert.AreEqual("2", result[1].ToString());
            Assert.AreEqual("3", result[2].ToString());
        }

        [TestMethod]
        public void TestAggregation()
        {
            var sql = "select a1, sum(a1) from a group by a2";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));

            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            var stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); var phyplan = stmt.physicPlan_;
            var answer = @"PhysicHashAgg   (rows = 2)
                            Output: 7,{4-a.a3/2}[0]*2+1+{sum(a.a1)}[1],{sum(a.a1)}[1]+{sum(a.a1+a.a2)}[2]*2
                            Agg Core: sum(a.a1[0]), sum(a.a1[0]+a.a2[2])
                        Group by: 4-a.a3[3]/2
                            -> PhysicScanTable a  (rows = 3)
                                Output: a.a1[0],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("7,3,2", result[0].ToString());
            Assert.AreEqual("7,4,19", result[1].ToString());
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,3,4,2", result[0].ToString());
            Assert.AreEqual("0,2,6,18", result[1].ToString());
            sql = "select sum(b1) from b where b3>1000;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(0, result.Count);   // FIXME: shall be a null
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("0,1", result[0].ToString());
            Assert.AreEqual("1,2", result[1].ToString());
            sql = "select a2, sum(a1) from a where a1>0 group by a2";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("2,1", result[0].ToString());
            Assert.AreEqual("3,2", result[1].ToString());
            sql = "select a3/2*2 from a group by 1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("4", result[1].ToString());
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2>1) a;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("7", result[0].ToString());
        }

        [TestMethod]
        public void TestSort()
        {
           var sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by a3";
           var result = ExecuteSQL(sql); Assert.IsNull(result);
           Assert.IsTrue(TestHelper.error_.Contains("SemanticAnalyzeException"));

            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by 1";
            var stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true); var phyplan = stmt.physicPlan_;
            var answer = @"PhysicOrder   (rows = 2)
                            Output: {4-a.a3/2}[0],{4-a.a3/2*2+1+min(a.a1)}[1],{avg(a.a4)+count(a.a1)}[2],{max(a.a1)+sum(a.a1+a.a2)*2}[3]
                            Order by: {4-a.a3/2}[0]
                            -> PhysicHashAgg   (rows = 2)
                                Output: {4-a.a3/2}[0],{4-a.a3/2}[0]*2+1+{min(a.a1)}[1],{avg(a.a4)}[2]+{count(a.a1)}[3],{max(a.a1)}[4]+{sum(a.a1+a.a2)}[5]*2
                                Agg Core: min(a.a1[1]), avg(a.a4[2]), count(a.a1[1]), max(a.a1[1]), sum(a.a1[1]+a.a2[4])
                                Group by: {4-a.a3/2}[0]
                                -> PhysicScanTable a  (rows = 3)
                                    Output: 4-a.a3[2]/2,a.a1[0],a.a4[3],a.a1[0]+a.a2[1],a.a2[1],a.a3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,3,4,2", result[0].ToString());
            Assert.AreEqual("0,2,6,18", result[1].ToString());
            sql = "select * from a where a1>0 order by a1;";
            result = ExecuteSQL(sql);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,2,3,4", result[0].ToString());
            Assert.AreEqual("2,3,4,5", result[1].ToString());
        }

        [TestMethod]
        public void TestPushdown()
        {
            string sql = "select a.a1,a.a1+a.a2 from a where a.a2 > 3";
            var stmt = RawParser.ParseSqlStatement(sql);
            stmt.Bind(null);
            stmt.CreatePlan();
            var plan = stmt.Optimize();
            var answer = @"LogicScanTable a
                                Output: a.a1[0],a.a1[0]+a.a2[1]
                                Filter: a.a2[1]>3";
            TestHelper.PlanAssertEqual(answer, plan.PrintString(0));

            sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1 and a1+b3>2";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            var phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin   (rows = 3)
                        Output: a.a2[0],a.a3[1],a.a1[2]+b.b2[3]
                        Filter: a.a1[2]+b.b3[4]>2
                        -> PhysicScanTable a  (rows = 1)
                            Output: a.a2[1],a.a3[2],a.a1[0]
                            Filter: a.a1[0]>1
                        -> PhysicScanTable b  (rows = 3)
                            Output: b.b2[1],b.b3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            // FIXME: you can see c1+b1>2 is not pushed down
            sql = "select a1,b1,c1 from a,b,c where a1+b1+c1>5 and c1+b1>2";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin   (rows = 1)
                        Output: a.a1[0],b.b1[1],c.c1[2]
                        Filter: a.a1[0]+b.b1[1]+c.c1[2]>5 and c.c1[2]+b.b1[1]>2
                        -> PhysicScanTable a  (rows = 3)
                            Output: a.a1[0]
                        -> PhysicNLJoin   (rows = 27)
                            Output: b.b1[1],c.c1[0]
                            -> PhysicScanTable c  (rows = 9)
                                Output: c.c1[0]
                            -> PhysicScanTable b  (rows = 27)
                                Output: b.b1[0]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            phyplan = stmt.physicPlan_;
            answer = @"PhysicScanTable a  (rows = 0)
                        Output: 1
                        Filter: a.a1[0]>@1
                        <SubqueryExpr> 1
                            -> PhysicScanTable b  (rows = 0)
                                Output: b.b1[0],#b.b2[1]
                                Filter: b.b2[1]>@2 and b.b1[0]>@3
                                <SubqueryExpr> 2
                                    -> PhysicScanTable c  (rows = 9)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]
                                <SubqueryExpr> 3
                                    -> PhysicScanTable c  (rows = 9)
                                        Output: c.c2[1]
                                        Filter: c.c2[1]=?b.b2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            // b3+c2 as a whole push to the outer join side
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin   (rows = 27)
                        Output: {b.b3+c.c2}[1]
                        -> PhysicScanTable a  (rows = 3)
                            Output: #a.a1[0]
                            Filter: a.a1[0]>=@1 and a.a2[1]>=@2
                            <SubqueryExpr> 1
                                -> PhysicScanTable b  (rows = 3)
                                    Output: b.b1[0]
                                    Filter: b.b1[0]=?a.a1[0]
                            <SubqueryExpr> 2
                                -> PhysicScanTable c  (rows = 3)
                                    Output: c.c2[1]
                                    Filter: c.c1[0]=?a.a1[0]
                        -> PhysicNLJoin   (rows = 27)
                            Output: b.b3[1]+c.c2[0]
                            -> PhysicScanTable c  (rows = 9)
                                Output: c.c2[1]
                            -> PhysicScanTable b  (rows = 27)
                                Output: b.b3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            // key here is bo.b3=a3 show up in 3rd subquery
            sql = @"select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 
                        and bo.b3 = a3 and b3> 1) and b2<3);";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            phyplan = stmt.physicPlan_;
            answer = @"PhysicScanTable a  (rows = 2)
                        Output: a.a1[0],#a.a2[1],#a.a3[2]
                        Filter: a.a1[0]=@1
                        <SubqueryExpr> 1
                            -> PhysicScanTable b as bo  (rows = 2)
                                Output: bo.b1[0],#bo.b3[2]
                                Filter: bo.b2[1]=?a.a2[1] and bo.b1[0]=@2 and bo.b2[1]<3
                                <SubqueryExpr> 2
                                    -> PhysicScanTable b  (rows = 3)
                                        Output: b.b1[0]
                                        Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[2]>1";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            stmt = RawParser.ParseSqlStatement(sql);
            stmt.Exec(true);
            phyplan = stmt.physicPlan_;
            answer = @"PhysicNLJoin   (rows = 3)
                        Output: a.a1[2]
                        Filter: a.a1[2]=b.b1[3] and b.b2[4]=c.c2[0]
                        -> PhysicScanTable c  (rows = 3)
                            Output: c.c2[1],#c.c3[2]
                        -> PhysicNLJoin   (rows = 9)
                            Output: a.a1[2],b.b1[0],b.b2[1]
                            -> PhysicScanTable b  (rows = 9)
                                Output: b.b1[0],b.b2[1]
                            -> PhysicScanTable a  (rows = 9)
                                Output: a.a1[0],#a.a2[1],#a.a3[2]
                                Filter: a.a1[0]=@1 and a.a2[1]=@3
                                <SubqueryExpr> 1
                                    -> PhysicFilter   (rows = 9)
                                        Output: bo.b1[0]
                                        Filter: bo.b2[1]=?a.a2[1] and bo.b2[1]<5 and bo.b1[0]=@2
                                        <SubqueryExpr> 2
                                            -> PhysicScanTable b  (rows = 81)
                                                Output: b.b1[0]
                                                Filter: b.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b.b3[2]>1
                                        -> PhysicFromQuery <bo>  (rows = 243)
                                            Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                            -> PhysicNLJoin   (rows = 243)
                                                Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                                -> PhysicScanTable b as b_1  (rows = 81)
                                                    Output: b_1.b2[1],b_1.b3[2]
                                                -> PhysicScanTable b as b_2  (rows = 243)
                                                    Output: b_2.b1[0]
                                <SubqueryExpr> 3
                                    -> PhysicScanTable b as bo  (rows = 27)
                                        Output: bo.b2[1],#bo.b3[2]
                                        Filter: bo.b1[0]=?a.a1[0] and bo.b2[1]=@4 and ?c.c3[2]<5
                                        <SubqueryExpr> 4
                                            -> PhysicScanTable b  (rows = 27)
                                                Output: b.b2[1]
                                                Filter: b.b4[3]=?a.a3[2]+1 and ?bo.b3[2]=?a.a3[2] and b.b3[2]>0";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
        }
    }
}

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using adb;
using System.Collections.Generic;

namespace test
{
    public class PlanCompare {
    }

    public class TestHelper
    {
        static internal string error_ = null;
        static internal List<Row> ExecuteSQL(string sql)
        {
            try
            {
                error_ = null;

                var stmt = RawParser.ParseSQLStatement(sql).Bind(null);
                stmt.CreatePlan();
                stmt.Optimize();
                var result = new PhysicCollect(stmt.physicPlan_);
                result.Exec(new ExecContext(), null);
                return result.rows_;
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
            Utils.ReadCSVLine(@"d:\test.csv", 
                x=> r.Add(string.Join(",", x)));
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("1,2", r[0]);
            Assert.AreEqual("3,4", r[1]);
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
                var l = RawParser.ParseSQLStatement(sql) as CreateTableStmt;
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.Message.Contains("SemanticAnalyzeException"));
            }
            sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a5 numeric(9));";
            var stmt = RawParser.ParseSQLStatement(sql) as CreateTableStmt;
            Assert.AreEqual(5, stmt.cols_.Count);
        }
    }

    [TestClass]
    public class DMLTest
    {
        [TestMethod]
        public void TestInsert()
        {
            var sql = "insert into a values(1+2*3, 'something' ,'2019-09-01', 50.2, 50);";
            var stmt = RawParser.ParseSQLStatement(sql) as InsertStmt;
            Assert.AreEqual(5, stmt.vals_.Count);
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
            var stmt = RawParser.ParseSQLStatement(sql) as SelectStmt;
            Assert.AreEqual(2, stmt.ctes_.Count);
            Assert.AreEqual(2, stmt.setqs_.Count);
            Assert.AreEqual(2, stmt.orders_.Count);
        }
    }

    [TestClass]
    public class OptimizerTest
    {
        private TestContext testContextInstance;

        internal List<Row> ExecuteSQL(string sql) {
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
        public void TestExecCrossJoin()
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

            // subquery in FROM clause
            sql = "select a1,a1,a3,a3, (select b3 from b where b2=2) from a where a1>1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,2,4,4,3", result[0].ToString());

            // scalar subquery
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 3)";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual ("2,4", result[0].ToString());
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
            sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("5", result[0].ToString());
        }

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var result = ExecuteSQL("select * from a");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(4, result[1].values_.Count);
            Assert.AreEqual(1, result[0].values_[1]);
            Assert.AreEqual(2, result[1].values_[1]);
            Assert.AreEqual(3, result[2].values_[1]);
            result = ExecuteSQL("select a1+a2,a1-a2,a1*a2 from a");
            Assert.AreEqual(3, result.Count);
            result = ExecuteSQL("select a1 from a where a2>1");
            Assert.AreEqual(2, result.Count);
            result = ExecuteSQL("select a.a1 from a where a2 > 1 and a3> 3");
            Assert.AreEqual(1, result.Count);
            result = ExecuteSQL("select a1 from a where a2>2");
            Assert.AreEqual(1, result.Count);
            result = ExecuteSQL("select a1,a1,a3,a3 from a where a1>1");
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("2,2,4,4", result[0].ToString());
            result = ExecuteSQL("select a1,a1,a4,a4 from a where a1+a2>2");
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,1,4,4", result[0].ToString());
            Assert.AreEqual("2,2,5,5", result[1].ToString());
            result = ExecuteSQL("select a1,a1,a3,a3 from a where a1+a2+a3>2");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("0,0,2,2", result[0].ToString());
            Assert.AreEqual("1,1,3,3", result[1].ToString());
            Assert.AreEqual("2,2,4,4", result[2].ToString());
            result = ExecuteSQL("select a1 from a where a1+a2>2");
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1", result[0].ToString());
            Assert.AreEqual("2", result[1].ToString());
        }

        [TestMethod]
        public void TestExecResult() {
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
        public void TestPushdown()
        {
            string sql = "select a.a1,a.a1+a.a2 from a where a.a2 > 3";
            var stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            var plan = stmt.Optimize();
            var answer = @"LogicGet a
                                Output: a.a1[0],a.a1[0]+a.a2[1]
                                Filter: a.a2[1]>3";
            TestHelper.PlanAssertEqual(answer,  plan.PrintString(0));

            sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            var phyplan = stmt.physicPlan_;
            answer = @"PhysicCrossJoin
                        Output: a.a2[0],a.a3[1],a.a1[2]+b.b2[3]
                      -> PhysicGet a
                          Output: a.a2[1],a.a3[2],a.a1[0]
                          Filter: a.a1[0]>1
                      -> PhysicGet b
                          Output: b.b2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.physicPlan_;
            answer = @"PhysicGet a
                        Output: 1
                        Filter: a.a1[0]>@1
                        <SubLink> 1
                        -> PhysicFilter
                            Output: b.b1[0]
                            Filter: b.b2[1]>@2 and b.b1[0]>@3
                            <SubLink> 2
                            -> PhysicFilter
                                Output: c.c2[0]
                                Filter: c.c2[0]=?b.b2[1]
                              -> PhysicGet c
                                  Output: c.c2[1]
                            <SubLink> 3
                            -> PhysicFilter
                                Output: c.c2[0]
                                Filter: c.c2[0]=?b.b2[1]
                              -> PhysicGet c
                                  Output: c.c2[1]
                          -> PhysicGet b
                              Output: b.b1[0],b.b2[1]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            sql = "select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 3) and b2<3);";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.physicPlan_;
            answer = @"PhysicGet a
                        Output: a.a1[0],#a.a2[1],#a.a3[2]
                        Filter: a.a1[0]=@1
                        <SubLink> 1
                        -> PhysicFilter
                            Output: bo.b1[0]
                            Filter: bo.b2[1]=?a.a2[1] and bo.b1[0]=@2 and bo.b2[1]<3
                            <SubLink> 2
                            -> PhysicFilter
                                Output: b.b1[0]
                                Filter: b.b3[1]=?a.a3[2] and ?bo.b3[2]=?a.a3[2] and b.b3[1]>3
                              -> PhysicGet b
                                  Output: b.b1[0],b.b3[2]
                          -> PhysicGet b as bo
                              Output: bo.b1[0],bo.b2[1],#bo.b3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));

            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.physicPlan_;
            answer = @"PhysicFilter
                        Output: a.a1[0]
                        Filter: a.a1[0]=b.b1[1] and b.b2[2]=c.c2[3]
                        -> PhysicCrossJoin
                            Output: a.a1[2],b.b1[3],b.b2[4],c.c2[0]
                            -> PhysicGet c
                                Output: c.c2[1],#c.c3[2]
                            -> PhysicCrossJoin
                                Output: a.a1[2],b.b1[0],b.b2[1]
                                -> PhysicGet b
                                    Output: b.b1[0],b.b2[1]
                                -> PhysicGet a
                                    Output: a.a1[0],#a.a2[1],#a.a3[2]
                                    Filter: a.a1[0]=@1 and a.a2[1]=@3
                                    <SubLink> 1
                                        -> PhysicFilter
                                            Output: bo.b1[0]
                                            Filter: bo.b2[1]=?a.a2[1] and bo.b1[0]=@2 and bo.b2[1]<5
                                            <SubLink> 2
                                                -> PhysicFilter
                                                    Output: b.b1[0]
                                                    Filter: b.b3[1]=?a.a3[2] and ?bo.b3[2]=?c.c3[2] and b.b3[1]>1
                                                    -> PhysicGet b
                                                        Output: b.b1[0],b.b3[2]
                                            -> PhysicFromQuery <bo>
                                                Output: bo.b1[0],bo.b2[1],#bo.b3[2]
                                                -> PhysicCrossJoin
                                                    Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                                                    -> PhysicGet b as b_1
                                                        Output: b_1.b2[1],b_1.b3[2]
                                                    -> PhysicGet b as b_2
                                                        Output: b_2.b1[0]
                                    <SubLink> 3
                                        -> PhysicFilter
                                            Output: bo.b2[0]
                                            Filter: bo.b1[1]=?a.a1[0] and bo.b2[0]=@4 and ?c.c3[2]<5
                                            <SubLink> 4
                                                -> PhysicFilter
                                                    Output: b.b2[0]
                                                    Filter: b.b4[1]=?a.a3[2]+1 and ?bo.b3[2]=?a.a3[2] and b.b3[2]>0
                                                    -> PhysicGet b
                                                        Output: b.b2[1],b.b4[3],b.b3[2]
                                            -> PhysicGet b as bo
                                                Output: bo.b2[1],bo.b1[0],#bo.b3[2]";
            TestHelper.PlanAssertEqual(answer, phyplan.PrintString(0));
        }
    }
}

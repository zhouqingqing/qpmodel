using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using adb;
using System.Collections.Generic;


namespace test
{
    public class PlanCompare {
        static public void AreEqual(string l, string r) {
            char[] splitters = {' ', '\t', '\r', '\n'};
            var lw = l.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
            var rw = r.Split(splitters, StringSplitOptions.RemoveEmptyEntries);

            Assert.AreEqual(lw.Length, rw.Length);
            for (int i = 0; i < lw.Length; i++)
                Assert.AreEqual(lw[i], rw[i]);
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
            string sql = "with cte1 as (select * from a), cte2 as (select * from b) select a1,a1+a2 from cte1 where a1<6 group by a1, a1+a2 " +
                                "union select b2, b3 from cte2 where b2 > 3 group by b1, b1+b2 " +
                                "order by 2, 1 desc";
            var stmt = RawParser.ParseSQLStatement(sql) as SelectStmt;
            Assert.AreEqual(2, stmt.ctes_.Count);
            Assert.AreEqual(2, stmt.cores_.Count);
            Assert.AreEqual(2, stmt.orders_.Count);
        }
    }

    [TestClass]
    public class OptimizerTest
    {
        private TestContext testContextInstance;

        string error_ = null;
        internal List<Row> ExecuteSQL(string sql) {
            try
            {
                error_ = null;

                var stmt = RawParser.ParseSQLStatement(sql).Bind(null);
                stmt.CreatePlan();
                stmt.Optimize();
                var result = new PhysicCollect(stmt.GetPhysicPlan());
                result.Exec(null);
                return result.rows_;
            }
            catch (Exception e) {
                error_ = e.Message;
                return null;
            }
        }

        internal Tuple<List<Row>, PhysicNode> ExecuteSQL2(string sql)
        {
            try
            {
                error_ = null;

                var stmt = RawParser.ParseSQLStatement(sql).Bind(null);
                stmt.CreatePlan();
                var phyplan = stmt.Optimize().DirectToPhysical();
                var result = new PhysicCollect(phyplan);
                result.Exec(null);
                return new Tuple<List<Row>, PhysicNode>(result.rows_, phyplan);
            }
            catch (Exception e)
            {
                error_ = e.Message;
                return null;
            }
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
            var result2 = ExecuteSQL2(sql);
            Assert.AreEqual(9, result2.Item1.Count);
            Assert.AreEqual("0,1,2,0,1,2", result2.Item1[0].ToString());
            Assert.AreEqual("2,3,4,2,3,4", result2.Item1[8].ToString());
            sql = "select * from a, (select * from b where b2>2) c";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            sql = "select b.a1 + b.a2 from (select a1 from a) b";
            result = ExecuteSQL(sql);
            Assert.IsNull(result);
            Assert.IsTrue(error_.Contains("SemanticAnalyzeException"));
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
            Assert.IsTrue(error_.Contains("SemanticAnalyzeException"));
            sql = "select a1, a2  from a where a.a1 = (select b1 from b)";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(error_.Contains("SemanticExecutionException"));
            sql = "select a1,a1,a3,a3, (select * from b where b2=2) from a where a1>1"; // * handling
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(error_.Contains("SemanticAnalyzeException"));

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
            //sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3)";
            //result = ExecuteSQL(sql);
            //Assert.AreEqual(1, result.Count);
            //Assert.AreEqual("0,2", result[0].ToString());
        }

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var result = ExecuteSQL("select * from a");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(3, result[1].values_.Count);
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
            result = ExecuteSQL("select a1,a1,a3,a3 from a where a1+a2>2");
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("1,1,3,3", result[0].ToString());
            Assert.AreEqual("2,2,4,4", result[1].ToString());
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
            sql = "select b.a1 + a2 from (select a3,a1,a2,a3,a2,a1,a1 from a, c) b";
            result = ExecuteSQL(sql);
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(error_.Contains("SemanticAnalyzeException"));
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
            PlanCompare.AreEqual (answer,  plan.PrintString(0));

            sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            var phyplan = stmt.GetPhysicPlan();
            answer = @"PhysicCrossJoin
                        Output: a.a2[0],a.a3[1],a.a1[2]+b.b2[3]
                      -> PhysicGet a
                          Output: a.a2[1],a.a3[2],a.a1[0]
                          Filter: a.a1[0]>1
                      -> PhysicGet b
                          Output: b.b2[1]";
            PlanCompare.AreEqual(answer, phyplan.PrintString(0));

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.GetPhysicPlan();
            answer = @"PhysicGet a
                        Output: 1
                        Filter: a.a1[0]>@1
                        <SubLink> 1
                        -> PhysicFilter
                            Output: b.b1[0],b.b2[1]
                            Filter: b.b2[1]>@2 and b.b1[0]>@3
                            <SubLink> 2
                            -> PhysicFilter
                                Output: c.c2[0]
                                Filter: c.c2[0]=?b.b2[-1]
                              -> PhysicGet c
                                  Output: c.c2[1]
                            <SubLink> 3
                            -> PhysicFilter
                                Output: c.c2[0]
                                Filter: c.c2[0]=?b.b2[-1]
                              -> PhysicGet c
                                  Output: c.c2[1]
                          -> PhysicGet b
                              Output: b.b1[0],b.b2[1]";
            PlanCompare.AreEqual(answer, phyplan.PrintString(0));

            sql = "select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 3) and b3<3);";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.GetPhysicPlan();
            answer = @"PhysicGet a
                        Output: a.a1[0]
                        Filter: a.a1[0]=@1
                        <SubLink> 1
                        -> PhysicFilter
                            Output: bo.b1[0],bo.b2[1],bo.b3[2]
                            Filter: bo.b2[1]=?a.a2[-1] and bo.b1[0]=@2 and bo.b3[2]<3
                            <SubLink> 2
                            -> PhysicFilter
                                Output: b.b1[0],b.b3[1]
                                Filter: b.b3[1]=?a.a3[-1] and ?bo.b3[1]=?a.a3[-1] and b.b3[1]>3
                              -> PhysicGet b
                                  Output: b.b1[0],b.b3[2]
                          -> PhysicGet b as bo
                              Output: bo.b1[0],bo.b2[1],bo.b3[2]";
            PlanCompare.AreEqual(answer, phyplan.PrintString(0));

            sql = @"select a1,a2,a3  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 3) and b3<3)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<2);";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            stmt.Optimize();
            phyplan = stmt.GetPhysicPlan();
            answer = @"PhysicGet a
                        Output: a.a1[0],a.a2[1],a.a3[2]
                        Filter: a.a1[0]=@1 and a.a2[1]=@3
                        <SubLink> 1
                        -> PhysicFilter
                            Output: bo.b1[0],bo.b2[1],bo.b3[2]
                            Filter: bo.b2[1]=?a.a2[-1] and bo.b1[0]=@2 and bo.b3[2]<3
                            <SubLink> 2
                            -> PhysicFilter
                                Output: b.b1[0],b.b3[1]
                                Filter: b.b3[1]=?a.a3[-1] and ?bo.b3[1]=?a.a3[-1] and b.b3[1]>3
                              -> PhysicGet b
                                  Output: b.b1[0],b.b3[2]
                          -> PhysicGet b as bo
                              Output: bo.b1[0],bo.b2[1],bo.b3[2]
                        <SubLink> 3
                        -> PhysicFilter
                            Output: bo.b2[0],bo.b1[1],bo.b3[2]
                            Filter: bo.b1[1]=?a.a1[-1] and bo.b2[0]=@4 and bo.b3[2]<2
                            <SubLink> 4
                            -> PhysicFilter
                                Output: b.b2[0],b.b3[1]
                                Filter: b.b3[1]=?a.a3[-1] and ?bo.b3[1]=?a.a3[-1] and b.b3[1]>1
                              -> PhysicGet b
                                  Output: b.b2[1],b.b3[2]
                          -> PhysicGet b as bo
                              Output: bo.b2[1],bo.b1[0],bo.b3[2]";
            PlanCompare.AreEqual(answer, phyplan.PrintString(0));
        }
    }
}

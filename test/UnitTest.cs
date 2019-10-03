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
            Assert.AreEqual("a.a1", col.ToString());
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
                var phyplan = stmt.Optimize().SimpleConvertPhysical();
                var result = new PhysicCollect(phyplan);
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
                var phyplan = stmt.Optimize().SimpleConvertPhysical();
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
        }

        [TestMethod]
        public void TestExecSubFrom()
        {
            var sql = "select * from a, (select * from b) c";
            var result2 = ExecuteSQL2(sql);
            Assert.AreEqual(9, result2.Item1.Count);
            Assert.IsTrue(result2.Item2.PrintOutput(0).Contains("a.a1,a.a2,a.a3"));
            Assert.IsTrue(result2.Item2.PrintOutput(0).Contains("b.b1,b.b2,b.b3"));
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
            sql = "select a1 from (select a1,a3 from a) b";
            result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
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
        }

        [TestMethod]
        public void TestPushdown()
        {
            string sql = "select a.a1,a.a1+a.a2 from a where a.a2 > 3";
            var stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            stmt.CreatePlan();
            var plan = stmt.Optimize();
            var answer = @"LogicGet a
                                Output: a.a1,a.a1+a.a2,a.a2
                                Filter: a.a2>3";
            PlanCompare.AreEqual (answer,  plan.PrintString(0));

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)))";
            stmt = RawParser.ParseSQLStatement(sql).Bind(null);
            plan = stmt.CreatePlan();
            answer = @"LogicFilter
                        Output: 1
                        Filter: a.a1>@0
                        <SubLink> 0
                        -> LogicFilter
                            Output: b.b1
                            Filter: b.b2>@1 and b.b3>@2
                            <SubLink> 1
                            -> LogicFilter
                                Output: c.c2
                                Filter: c.c2=?b.b3
                              -> LogicGet c
                                  Output: c.c2
                            <SubLink> 2
                            -> LogicFilter
                                Output: c.c2
                                Filter: c.c3=?b.b2
                              -> LogicGet c
                                  Output: c.c2,c.c3
                          -> LogicGet b
                              Output: b.b1,b.b2,b.b3
                      -> LogicGet a
                          Output: 1,a.a1";
            PlanCompare.AreEqual(answer, plan.PrintString(0));
        }
    }
}

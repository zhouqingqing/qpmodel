using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using adb;
using System.Collections.Generic;


namespace test
{
    public class PlanCompare {
        static public void AreEqual(string l, string r) {
            char[] splitters = {' ', '\r', '\n'};
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
    }

    [TestClass]
    public class OptimizerTest
    {
        private TestContext testContextInstance;

        internal List<Row> ExecuteSQL(string sql) {
            var stmt = RawParser.ParseSelect(sql).Bind(null);
            var phyplan = stmt.Optimize(stmt.CreatePlan()).SimpleConvertPhysical();
            var result = new PhysicCollect(phyplan);
            result.Exec(null);

            return result.rows_;
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
            var result = ExecuteSQL("select a.a1 from a, b where a2>1");
            Assert.AreEqual(2 * 3, result.Count);
            result = ExecuteSQL("select a.a1 from a, b where a2>2");
            Assert.AreEqual(1 * 3, result.Count);
        }

        [TestMethod]
        public void TestExecSubFrom()
        {
            var result = ExecuteSQL("select * from a, (select * from b) c");
            Assert.AreEqual(9, result.Count);
            result = ExecuteSQL("select * from a, (select * from b where b2>2) c;");
            Assert.AreEqual(3, result.Count);
        }

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var result = ExecuteSQL("select * from a");
            Assert.AreEqual(3, result.Count);
            result = ExecuteSQL("select a1 from a");
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
            string sql = "select 2+6*3+2*6;";
            var result = ExecuteSQL(sql); 
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(32, result[0].values_[0]);
        }

        [TestMethod]
        public void TestPushdown()
        {
            string sql = "select a.a1 from a where a.a2 > 3";
            var stmt = RawParser.ParseSelect(sql).Bind(null);
            var plan = stmt.Optimize(stmt.CreatePlan());
            var answer = @"LogicGet a
                             Filter: a.a2>3";
            PlanCompare.AreEqual (answer,  plan.PrintString(0));

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)));";
            stmt = RawParser.ParseSelect(sql).Bind(null);
            plan = stmt.CreatePlan();
            answer = @"LogicFilter
                        Filter: a.a1>@0
                        <SubLink> 0
                        -> LogicFilter
                            Filter: b.b2>@1 and b.b3>@2
                            <SubLink> 1
                            -> LogicFilter
                                Filter: c.c2=b.b3
                                -> LogicGet c
                            <SubLink> 2
                            -> LogicFilter
                                Filter: c.c3=b.b2
                                -> LogicGet c
                            -> LogicGet b
                        -> LogicGet a";
            PlanCompare.AreEqual(answer, plan.PrintString(0));
        }
    }
}

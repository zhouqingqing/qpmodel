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

        internal Row Row0(IEnumerable<Row> rows) {
            foreach (var r in rows)
                return r;
            return null;
        }

        internal int RowCount(IEnumerable<Row> rows)
        {
            int cnt = 0;
            foreach (var r in rows)
                cnt++;
            return cnt;
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
        public void TestExecResult() {
            string sql = "select 2+6*3+2*6;";
            var stmt = RawParser.ParseSelect(sql).Bind(null);
            var result = stmt.Optimize(stmt.CreatePlan()).SimpleConvertPhysical().Next();
            Assert.AreEqual(1, RowCount(result));
            Assert.AreEqual(32, Row0(result).values_[0]);
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

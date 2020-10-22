﻿/*
 * The MIT License (MIT)
 *
 * Copyright (c) 2020 Futurewei Corp.
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;

using qpmodel.physic;
using qpmodel.utils;
using qpmodel.logic;
using qpmodel.sqlparser;
using qpmodel.optimizer;
using qpmodel.test;
using qpmodel.expr;
using qpmodel.dml;
using qpmodel.tools;
using qpmodel.stat;

namespace qpmodel.unittest
{
    // Test Utils
    public class TU
    {
        [ThreadStatic]
        static internal string error_ = null;
        static internal List<Row> ExecuteSQL(string sql) => ExecuteSQL(sql, out _);

        static internal List<Row> ExecuteSQL(string sql, out string physicplan, QueryOption option = null)
        {
            var results = SQLStatement.ExecSQL(sql, out physicplan, out error_, option);
            Console.WriteLine(physicplan);
            return results;
        }
        static internal List<Row> ExecuteSQL(string sql, out SQLStatement stmt, out string physicplan, QueryOption option = null)
        {
            var results = SQLStatement.ExecSQL(sql, out stmt, out physicplan, out error_, option);
            Console.WriteLine(physicplan);
            return results;
        }
        static internal void ExecuteSQL(string sql, string expectedResults)
        {
            var result = ExecuteSQL(sql);
            Assert.AreEqual(expectedResults, string.Join(";", result));
        }
        static internal void ExecuteSQL(string sql, string expectedResults, out string physicplan, QueryOption option = null)
        {
            var result = SQLStatement.ExecSQL(sql, out physicplan, out error_, option);
            Assert.AreEqual(expectedResults, string.Join(";", result));
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

        public static bool CheckPlanOrder(PhysicNode physic, List<string> patternlist)
        {
            bool FindInLevel(List<PhysicNode> curlevel, string pattern, out List<PhysicNode> nextlevel)
            {
                nextlevel = new List<PhysicNode>();
                bool output = false;
                foreach (var node in curlevel)
                {
                    if (node.GetType().ToString() == "qpmodel.physic." + pattern) output = true;
                    nextlevel.AddRange(node.children_);
                }
                return output;
            }

            Assert.IsNotNull(physic);
            List<PhysicNode> curlevel = new List<PhysicNode> { physic };
            foreach (var pattern in patternlist)
            {
                List<PhysicNode> nextlevel;
                while (!FindInLevel(curlevel, pattern, out nextlevel))
                {
                    curlevel = nextlevel;
                    if (nextlevel.Count == 0) return false;
                }
            }
            return true;
        }

        // for unit test consistancy
        // it should be call if the unitest reuse some table 
        // especially you expect get the same "rows" in physicplans 
        public static void ClearTableStatsInCatalog(List<String> tabNameList)
        {
            foreach (String tabName in tabNameList)
            {
                List<ColumnStat> stats = new List<ColumnStat>();
                stats.AddRange(Catalog.sysstat_.GetOrCreateTableStats(tabName, true));
                if (stats.Count != 0)//exist logs
                {
                    Catalog.sysstat_.RemoveRecords(tabName);
                }
            }
        }
    }

    [TestClass]
    public class UtilsTest
    {
        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            Catalog.Init();
        }

        [TestMethod]
        public void TestCSVReader()
        {
            List<string> r = new List<string>();
            Utils.ReadCsvLine(@"../../../../data/test.tbl",
                x => r.Add(string.Join(",", x)));
            Assert.AreEqual(3, r.Count);
            Assert.AreEqual("1,2,3,4", r[0]);
            Assert.AreEqual("2,2,3,4", r[1]);
            Assert.AreEqual("5,6,7,8", r[2]);
        }

        [TestMethod]
        public void TestStringLike()
        {
            Debug.Assert(Utils.StringLike("ABCDEF", "a%") == false);
            Debug.Assert(Utils.StringLike("ABCDEF", "A%") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "%A%") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "A") == false);
            Debug.Assert(Utils.StringLike("ABCDEF", "%EF") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "%DE") == false);
            Debug.Assert(Utils.StringLike("ABCDEF", "A_C%") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "A_C") == false);
            Debug.Assert(Utils.StringLike("ABCDEF", "A__D%") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "_%") == true);
            Debug.Assert(Utils.StringLike("ABCDEF", "A%B%") == true);
        }
    }

    [TestClass]
    public class CodeGen
    {
        [TestMethod]
        public void TestSimpleSlect()
        {
            // you may encounter an error saying can't find roslyn/csc.exe
            // one work around is to copy the folder there.
            //
            string sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2;";
            QueryOption option = new QueryOption();
            option.profile_.enabled_ = true;
            option.optimize_.enable_subquery_unnest_ = false;
            option.optimize_.use_codegen_ = true;

            option.optimize_.enable_streamagg_ = true;
            TU.ExecuteSQL(sql, "4,1;6,4", out string phyplan, option);

            option.optimize_.enable_streamagg_ = false;
            sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2 order by a2 desc;";
            TU.ExecuteSQL(sql, "6,4;4,1", out phyplan, option);

            sql = "select a2*2, count(a1) from a, b, c where a1=b1 and a2=c2 group by a2 limit 2;";
            TU.ExecuteSQL(sql, "2,1;4,1", out phyplan, option);

            sql = "select a1, b1 from a left join b on a1>b1;";
            TU.ExecuteSQL(sql, "0,;1,0;2,0;2,1", out phyplan, option);
            sql = "select a1 from a except select b3 from b;";
            TU.ExecuteSQL(sql, "0;1", out phyplan, option);
            TU.ExecuteSQL("select count(1) from a;", "3", out phyplan, option);

            // demonstrate we can fallback to any non-codegen execution
            sql = "select a2*a1, repeat('a', a2) from a where a1>= (select b1 from b where a2=b2);";
            TU.ExecuteSQL(sql, "0,a;2,aa;6,aaa", out phyplan, option);
        }
    }

    [TestClass]
    public class DDL
    {
        [TestMethod]
        public void TestTable()
        {
            var sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a4 numeric(9));";
            try
            {
                var l = RawParser.ParseSqlStatements(sql);
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.Message.Contains("duplicated"));
            }
            sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), " +
                "a5 numeric(9), a6 double, a7 date, a8 varchar(100), primary key (a1));";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as CreateTableStmt;
            Assert.AreEqual(8, stmt.cols_.Count);
            Assert.AreEqual(1, stmt.cons_.Count);
        }

        [TestMethod]
        public void TestIndex()
        {
            var sql = "create index tt2 on test(t2);";
            var result = TU.ExecuteSQL(sql); Assert.IsNotNull(result);
            sql = "create unique index tt2 on test(t2);";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("name"));
            sql = "create unique index u_tt2 on test(t2);";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("duplicated"));
            sql = "drop index u_tt2;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("exists"));
            sql = "drop index tt2;";
            result = TU.ExecuteSQL(sql); Assert.IsNotNull(result);
        }

        [TestMethod]
        public void TestAnalyze()
        {
            var sql = "analyze a;";
            SQLStatement.ExecSQL(sql, out _, out _);

            sql = "analyze a tablesample row (15)";
            SQLStatement.ExecSQL(sql, out _, out _);
        }
    }

    [TestClass]
    public class UBenchmarks
    {
        public class QueryVerify
        {
            static bool resultVerify(string resultFn, string expectFn)
            {
                // read  strings from result file and expected file
                string expectText = File.ReadAllText(expectFn).Replace("\r\n", "\n");
                string resultText = File.ReadAllText(resultFn).Replace("\r\n", "\n");

                // compare the test result with the expected result
                if (string.Compare(resultText, expectText) != 0)
                {
                    return false;
                }

                return true;
            }

            public string SQLQueryVerify(string sql_dir_fn, string write_dir_fn, string expect_dir_fn, string[] badQueries, bool explainOnly)
            {
                string result = null;
                QueryOption option = new QueryOption();
                option.optimize_.TurnOnAllOptimizations();
                option.optimize_.remove_from_ = false;

                option.explain_.show_output_ = true;
                option.explain_.show_estCost_ = option.optimize_.use_memo_;
                option.explain_.mode_ = explainOnly ? ExplainMode.explain : ExplainMode.full;

                // get a list of sql query fine names from the sql directory
                string[] sqlFiles = Directory.GetFiles(sql_dir_fn, "*.sql");

                // execute the query in each file and and verify the result
                foreach (string sqlFn in sqlFiles)
                {
                    string dbg_name = Path.GetFileNameWithoutExtension(sqlFn);

                    if (badQueries.Contains(dbg_name) == true)
                        continue;

                    // execute query
                    var sql = File.ReadAllText(sqlFn);
                    var test_result = SQLStatement.ExecSQLList(sql, option);

                    // construct file name for result file and write result
                    string f_name = Path.GetFileNameWithoutExtension(sqlFn);
                    string write_fn = $@"{write_dir_fn}/{f_name}.txt";

                    File.WriteAllText(write_fn, test_result);

                    // construct file name of expected result
                    string expect_fn = $@"{expect_dir_fn}/{f_name}.txt";

                    // verify query result against the expected result
                    if (!resultVerify(write_fn, expect_fn))
                    {
                        result += write_fn + ";";
                    }
                }
                return result;
            }
        }

        [TestMethod]
        public void TestJobench()
        {
            var stats_fn = "../../../../jobench/statistics/jobench_stats";

            JOBench.CreateTables();

            Catalog.sysstat_.read_serialized_stats(stats_fn);

            // run tests and compare plan
            string sql_dir_fn = "../../../../jobench";
            string write_dir_fn = $"../../../../test/regress/output/jobench";
            string expect_dir_fn = $"../../../../test/regress/expect/jobench";

            try
            {
                ExplainOption.show_tablename_ = false;
                RunFolderAndVerify(sql_dir_fn, write_dir_fn, expect_dir_fn, new string[] { "" });
            }
            finally
            {
                ExplainOption.show_tablename_ = true;
            }
        }

        [TestMethod]
        public void TestBenchmarks()
        {
            // REMOVE_FROM
            // disable this test in this PR. It will be enabled in a later PR.
            return;

            TestTpcdsPlanOnly();
            TestTpcdsWithData();

            Tpch.CreateTables();
            TestTpchAndComparePlan("1", new string[] { "" });
            TestTpchAndComparePlan("0001", new string[] { "" });
            TestTpchWithData();

            // some primitives neeed tpch data
            Executors.TestPullPushAgg();
        }

        [TestMethod]
        public void TestTpchDistributed()
        {
            var files = Directory.GetFiles(@"../../../../tpch", "*.sql");
            string scale = "0001";

            Tpch.CreateTables(true);
            Tpch.LoadTables(scale);

            Tpch.AnalyzeTables();

            // run tests and compare plan
            string sql_dir_fn = "../../../../tpch";
            string write_dir_fn = $"../../../../test/regress/output/tpch{scale}_d";
            string expect_dir_fn = $"../../../../test/regress/expect/tpch{scale}_d";

            ExplainOption.show_tablename_ = false;
            var badQueries = new string[] { "q07", "q08", "q09", "q13", "q15", "q22" };

            try
            {
                ExplainOption.show_tablename_ = false;
                RunFolderAndVerify(sql_dir_fn, write_dir_fn, expect_dir_fn, badQueries);
            }
            finally
            {
                ExplainOption.show_tablename_ = true;
            }
            List<String> tabNameList = new List<String> { "region", "orders", "part", "partsupp", "lineitem", "supplier", "nation" };
            TU.ClearTableStatsInCatalog(tabNameList);
        }

        // this test can construct own sql using the date of tpch0001
        // TODO the select subquery inccost is not accounted to total
        [TestMethod]
        public void TestUsingTpch0001Data()
        {
            var files = Directory.GetFiles(@"../../../../tpch", "*.sql");
            string scale = "0001";

            Tpch.CreateTables(true);
            Tpch.LoadTables(scale);

            Tpch.AnalyzeTables();

            // run tests and compare plan
            string sql_dir_fn = "../../../../tpch/select";
            string write_dir_fn = $"../../../../test/regress/output/tpch{scale}_select";
            string expect_dir_fn = $"../../../../test/regress/expect/tpch{scale}_select";

            ExplainOption.show_tablename_ = false;
            var badQueries = new string[] {  };

            try
            {
                ExplainOption.show_tablename_ = false;
                RunFolderAndVerify(sql_dir_fn, write_dir_fn, expect_dir_fn, badQueries);
            }
            finally
            {
                ExplainOption.show_tablename_ = true;
            }
            List<String> tabNameList = new List<String> { "region", "orders", "part", "partsupp", "lineitem", "supplier", "nation" };
            TU.ClearTableStatsInCatalog(tabNameList);
        }

        void TestTpcdsWithData()
        {
            // table already created
            Tpcds.LoadTables("tiny");
            Tpcds.AnalyzeTables();

            var files = Directory.GetFiles(@"../../../../tpcds", "*.sql");
            // long time: 4 bad plan
            // 6: distinct not supported, causing wrong result
            // 10: subquery memo not copy out
            // q000: jigzag memory allocation pattern but they are runnable with qpmodel Program.Main()
            //
            string[] runnable = {
                "q1", "q2", "q3", "q7", "q15", "q17", "q19", "q21", "q24", "q25",
                "q26", "q28", "q30", "q32", "q34", "q35", "q37", "q39", "q42", "q43",
                "q45", "q46", "q50", "q52", "q55", "q58", "q59", "q61", "q62", "q00065",
                "q68", "q69", "q71", "q73", "q79", "q81", "q82", "q83", "q00084",
                "q00085",
                "q88", "q90", "q91", "q92", "q94", "q95", "q96", "q99"
            };

            // make sure all queries can generate phase one opt plan
            QueryOption option = new QueryOption();
            option.optimize_.enable_subquery_unnest_ = true;
            option.optimize_.remove_from_ = false;
            option.optimize_.use_memo_ = true;
            foreach (var v in files)
            {
                char[] splits = { '.', '/', '\\' };
                var tokens = v.Split(splits, StringSplitOptions.RemoveEmptyEntries);

                Debug.Assert(tokens[1][0] == 'q');
                if (!runnable.Contains(tokens[1]))
                    continue;

                Debug.Assert(option.optimize_.enable_subquery_unnest_);
                Debug.Assert(option.optimize_.use_memo_);
                var sql = File.ReadAllText(v);

                var result = SQLStatement.ExecSQL(sql, out string phyplan, out _, option);
                Assert.IsNotNull(result);
            }
        }

        void TestTpchAndComparePlan(string scale, string[] badQueries, bool testIndexes = false)
        {
            var files = Directory.GetFiles(@"../../../../tpch", "*.sql");
            if (scale == "1")
            {
                // for 1g scale, we can't do real run, but we'd like to see the plan
                var stats_fn = "../../../../tpch/statistics/sf1";
                Catalog.sysstat_.read_serialized_stats(stats_fn);
            }
            else
            {
                // load data for this cale
                Tpch.LoadTables(scale);

                if (testIndexes)
                    Tpch.CreateIndexes();

                Tpch.AnalyzeTables();
            }

            // run tests and compare plan
            string sql_dir_fn = "../../../../tpch/";
            string write_dir_fn = $"../../../../test/regress/output/tpch{scale}";
            string expect_dir_fn = $"../../../../test/regress/expect/tpch{scale}";
            try
            {
                ExplainOption.show_tablename_ = false;
                RunFolderAndVerify(sql_dir_fn, write_dir_fn, expect_dir_fn, badQueries, scale == "1");
            }
            finally
            {
                ExplainOption.show_tablename_ = true;
            }
        }

        void TestTpcdsPlanOnly()
        {
            var files = Directory.GetFiles(@"../../../../tpcds", "*.sql");
            string stats_dir = "../../../../tpcds/statistics/presto/sf1";

            Tpcds.CreateTables();
            // load persisted stats
            PrestoStatsFormatter.ReadConvertPrestoStats(stats_dir);

            // make sure all queries can generate phase one opt plan
            QueryOption option = new QueryOption();
            option.optimize_.enable_subquery_unnest_ = true;
            option.optimize_.remove_from_ = false;
            option.optimize_.use_memo_ = false;
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var result = TU.ExecuteSQL(sql, out string phyplan, option);
                Assert.IsNotNull(phyplan); Assert.IsNotNull(result);
            }
        }

        // true on success and false on failure
        public static void RunFolderAndVerify(string sql_dir_fn, string write_dir_fn, string expect_dir_fn, string[] badQueries, bool explainOnly = false)
        {
            QueryVerify qv = new QueryVerify();
            var result = qv.SQLQueryVerify(sql_dir_fn, write_dir_fn, expect_dir_fn, badQueries, explainOnly);
            if (result != null) Debug.WriteLine(result);
            Assert.IsNull(result);
        }

        void TestTpchWithData()
        {
            // make sure all queries parsed
            var files = Directory.GetFiles(@"../../../../tpch", "*.sql");
            Array.Sort(files);

            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var stmt = RawParser.ParseSingleSqlStatement(sql);
                stmt.Bind(null);
                Console.WriteLine(stmt.CreatePlan().Explain());
            }
            Assert.AreEqual(22, files.Length);

            // data already loaded by previous test, execute queries
            string phyplan = "";
            var option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;
                Assert.IsTrue(option.optimize_.enable_subquery_unnest_);
                option.optimize_.remove_from_ = true;

                var result = TU.ExecuteSQL(File.ReadAllText(files[0]), out _, option);  // FIXME: projection too deep
                Assert.AreEqual(4, result.Count);
                TU.ExecuteSQL(File.ReadAllText(files[1]), "", out _, option);
                result = TU.ExecuteSQL(File.ReadAllText(files[2]), out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashJoin"));
                Assert.AreEqual(8, result.Count);
                result = TU.ExecuteSQL(File.ReadAllText(files[3]), out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin")); Assert.AreEqual(0, TU.CountStr(phyplan, "Subquery"));
                Assert.AreEqual(5, result.Count);
                Assert.AreEqual("1-URGENT,9;2-HIGH,7;3-MEDIUM,9;4-NOT SPECIFIED,7;5-LOW,12", string.Join(";", result));
                TU.ExecuteSQL(File.ReadAllText(files[4]), "", out phyplan, option);
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                TU.ExecuteSQL(File.ReadAllText(files[5]), "48090.8586", out _, option); // FIXME: sampling estimation
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                TU.ExecuteSQL(File.ReadAllText(files[6]), "", out _, option);
                TU.ExecuteSQL(File.ReadAllText(files[7]), "1995,0;1996,0", out _, option);
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                result = TU.ExecuteSQL(File.ReadAllText(files[8]), out _, option);
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                Assert.AreEqual(60, result.Count);
                Assert.AreEqual("ARGENTINA,1998,17779.0697;ARGENTINA,1997,13943.9538;ARGENTINA,1996,7641.4227;" +
                    "ARGENTINA,1995,20892.7525;ARGENTINA,1994,15088.3526;ARGENTINA,1993,17586.3446;ARGENTINA,1992,28732.4615;" +
                    "ETHIOPIA,1998,28217.16;ETHIOPIA,1996,33970.65;ETHIOPIA,1995,37720.35;ETHIOPIA,1994,37251.01;ETHIOPIA,1993,23782.61;" +
                    "IRAN,1997,23590.008;IRAN,1996,7428.2325;IRAN,1995,21000.9965;IRAN,1994,29408.13;IRAN,1993,49876.415;IRAN,1992,52064.24;" +
                    "IRAQ,1998,11619.9604;IRAQ,1997,47910.246;IRAQ,1996,18459.5675;IRAQ,1995,32782.3701;IRAQ,1994,9041.2317;IRAQ,1993,30687.2625;" +
                    "IRAQ,1992,29098.2557;KENYA,1998,33148.3345;KENYA,1997,54355.0165;KENYA,1996,53607.4854;KENYA,1995,85354.8738;" +
                    "KENYA,1994,102904.2511;KENYA,1993,109310.8084;KENYA,1992,138534.121;MOROCCO,1998,157058.2328;MOROCCO,1997,88669.961;" +
                    "MOROCCO,1996,236833.6672;MOROCCO,1995,381575.8668;MOROCCO,1994,243523.4336;MOROCCO,1993,232196.7803;MOROCCO,1992,347434.1452;" +
                    "PERU,1998,101109.0196;PERU,1997,58073.0866;PERU,1996,30360.5218;PERU,1995,138451.78;PERU,1994,55023.0632;PERU,1993,110409.0863;" +
                    "PERU,1992,70946.1916;UNITED KINGDOM,1998,139685.044;UNITED KINGDOM,1997,183502.0498;UNITED KINGDOM,1996,374085.2884;" +
                    "UNITED KINGDOM,1995,548356.7984;UNITED KINGDOM,1994,266982.768;UNITED KINGDOM,1993,717309.464;UNITED KINGDOM,1992,79540.6016;" +
                    "UNITED STATES,1998,32847.96;UNITED STATES,1997,30849.5;UNITED STATES,1996,56125.46;UNITED STATES,1995,15961.7977;" +
                    "UNITED STATES,1994,31671.2;UNITED STATES,1993,55057.469;UNITED STATES,1992,51970.23",
                    string.Join(";", result));
                result = TU.ExecuteSQL(File.ReadAllText(files[9]), out _, option);
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                Assert.AreEqual(20, result.Count);
                TU.ExecuteSQL(File.ReadAllText(files[10]), "", out _, option);
                if (option.optimize_.use_memo_) Assert.AreEqual(0, TU.CountStr(phyplan, "NLJoin"));
                TU.ExecuteSQL(File.ReadAllText(files[11]), "MAIL,5,5;SHIP,5,10", out _, option);
                // FIXME: agg on agg from
                option.optimize_.remove_from_ = false;
                result = TU.ExecuteSQL(File.ReadAllText(files[12]), out _, option); // FIXME: remove_from
                Assert.AreEqual(27, result.Count);
                option.optimize_.remove_from_ = true;
                result = TU.ExecuteSQL(File.ReadAllText(files[13]), out _, option);
                Assert.AreEqual(1, result.Count);
                Assert.AreEqual(true, result[0].ToString().Contains("15.23"));
                // q15 cte
                result = TU.ExecuteSQL(File.ReadAllText(files[15]), out _, option);
                Assert.AreEqual(34, result.Count);
                TU.ExecuteSQL(File.ReadAllText(files[16]), "", out _, option);
                TU.ExecuteSQL(File.ReadAllText(files[17]), "", out _, option);
                TU.ExecuteSQL(File.ReadAllText(files[18]), out _, option); // FIXME: .. or ... or ...
                TU.ExecuteSQL(File.ReadAllText(files[19]), "", out _, option);
                TU.ExecuteSQL(File.ReadAllText(files[20]), "", out _, option);
                option.optimize_.remove_from_ = false;
                result = TU.ExecuteSQL(File.ReadAllText(files[21]), out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicFromQuery"));
                Assert.AreEqual(7, result.Count);
                option.optimize_.remove_from_ = true;
            }
        }
    }

    [TestClass]
    public class Optimizer
    {
        [TestMethod]
        public void TestJoinSolvers()
        {
            VancePartition.Test();
            SetOp.Test();
            JoinGraph.Test();

            DPBushy.Test();
            DPccp.Test();
            GOO.Test();
            TDBasic.Test();
        }

        [TestMethod]
        public void TestMemo()
        {
            string phyplan = "";
            QueryOption option = new QueryOption();
            option.optimize_.use_memo_ = true;
            option.optimize_.enable_subquery_unnest_ = true;

            var sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            var result = TU.ExecuteSQL(sql, out SQLStatement stmt, out _, option);
            var memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out int tlogics, out int tphysics);
            Assert.AreEqual(11, memo.cgroups_.Count);
            Assert.AreEqual(26, tlogics); Assert.AreEqual(42, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select * from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);";
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            Assert.AreEqual("0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5", string.Join(";", result));
            sql = "select * from b , a where a1=b1 and a1 < (select a2 from a a_1 where a2=b2);";
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            Assert.AreEqual("0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5", string.Join(";", result));

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(15, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,c,b where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";   // FIXME: different #plans
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(17, tlogics); Assert.AreEqual(27, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3";
            option.optimize_.memo_disable_crossjoin_ = false;
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(7, memo.cgroups_.Count);
            Assert.AreEqual(15, tlogics); Assert.AreEqual(25, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            option.optimize_.memo_disable_crossjoin_ = true;
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(5, memo.cgroups_.Count);
            Assert.AreEqual(7, tlogics); Assert.AreEqual(11, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));

            sql = "select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(11, memo.cgroups_.Count);
            Assert.AreEqual(26, tlogics); Assert.AreEqual(42, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            var mstr = stmt.optimizer_.PrintMemo();
            Assert.IsTrue(mstr.Contains("Summary: 26,42"));

            // test join resolver
            option.optimize_.memo_use_joinorder_solver_ = true;
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(5, memo.cgroups_.Count);
            Assert.AreEqual(5, tlogics); Assert.AreEqual(5, tphysics);
            Assert.AreEqual("0;1;2", string.Join(";", result));
            mstr = stmt.optimizer_.PrintMemo();
            Assert.IsTrue(mstr.Contains("Summary: 5,5"));
            option.optimize_.memo_use_joinorder_solver_ = false;

            sql = "select count(b1) from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1";
            result = TU.ExecuteSQL(sql, out stmt, out _, option);
            Assert.AreEqual("3", string.Join(";", result));

            sql = "select count(*) from a where a1 in (select b2 from b where b1 > 0) and a2 in (select b3 from b where b1 > 0);";
            TU.ExecuteSQL(sql, "1", out phyplan, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFilter"));

            sql = "select count(*) from (select b1 from a,b,c,d where b.b2 = a.a2 and b.b3=c.c3 and d.d1 = a.a1 and a1>0) v;";
            TU.ExecuteSQL(sql, "2", out phyplan, option); Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicFilter"));

            sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));";
            TU.ExecuteSQL(sql, "1;2;3", out phyplan, option);
            var answer = @"PhysicScanTable a (actual rows=3)
                            Output: a.a2[1]
                            Filter: a.a3[2]>@1
                            <ScalarSubqueryExpr> cached 1
                                -> PhysicHashAgg  (actual rows=1)
                                    Output: {min(b.b1*2)}[0]
                                    Aggregates: min(b.b1[1]*2)
                                    -> PhysicFilter  (actual rows=3)
                                        Output: {b.b1*2}[0],b.b1[1],2
                                        Filter: b.b2[3]>=(c.c2[4]-1)
                                        -> PhysicSingleJoin Left (actual rows=3)
                                            Output: {b.b1*2}[0],b.b1[1],{2}[2],b.b2[3],c.c2[4]
                                            Filter: c.c2[4]=b.b2[3]
                                            -> PhysicFilter  (actual rows=3)
                                                Output: {b.b1*2}[0],b.b1[1],2,b.b2[3]
                                                Filter: b.b3[4]>c.c2[5]
                                                -> PhysicSingleJoin Left (actual rows=3)
                                                    Output: {b.b1*2}[0],b.b1[1],{2}[2],b.b2[3],b.b3[4],c.c2[5]
                                                    Filter: c.c2[5]=b.b2[3]
                                                    -> PhysicScanTable b (actual rows=3)
                                                        Output: b.b1[0]*2,b.b1[0],2,b.b2[1],b.b3[2]
                                                    -> PhysicScanTable c (actual rows=3, loops=3)
                                                        Output: c.c2[1]
                                            -> PhysicScanTable c (actual rows=3, loops=3)
                                                Output: c.c2[1]";
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select count(*) from a, b,c,d where a1+b1+c1+d1=1;";
            TU.ExecuteSQL(sql, "4", out phyplan, option);
            Assert.AreEqual(0, TU.CountStr(phyplan, "HashJoin"));

            // FIXME: a.a1+b.a1=5-c.a1, a.a1+b.a1+c.a1=5
            sql = "select a.a1,b.a1,c.a1, a.a1+b.a1+c.a1 from a, a b, a c where a.a1=5-b.a1-c.a1;";
            TU.ExecuteSQL(sql, "2,2,1,5;2,1,2,5;1,2,2,5", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "a.a1[0]=((5-b.a1[1])-c.a1[2])"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2;";
            TU.ExecuteSQL(sql, "0,1,2,3;1,2,3,4;2,3,4,5", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "NLJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));

            sql = "select a.* from a join b on a1=b1 or a3=b3 join c on a2=c2 join d on a4=2*d3;";
            TU.ExecuteSQL(sql, "1,2,3,4", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "NLJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: (a.a1[0]=b.b1[4] or a.a3[2]=b.b3[5])"));
            Assert.AreEqual(2, TU.CountStr(phyplan, "HashJoin"));

            Assert.IsTrue(option.optimize_.use_memo_);
        }

        [TestMethod]
        public void TestPropertyEnforcement()
        {
            QueryOption option = new QueryOption();
            option.optimize_.enable_streamagg_ = true;

            string sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2;";
            TU.ExecuteSQL(sql, "4,1;6,4", out string phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicStreamAgg"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicOrder"));

            sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2 order by count(a1) desc;";
            TU.ExecuteSQL(sql, "6,4;4,1", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicStreamAgg"));
            Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicOrder"));

            sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2 order by a2;";
            TU.ExecuteSQL(sql, "4,1;6,4", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicStreamAgg"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicOrder"));

            var result = TU.ExecuteSQL(sql, out SQLStatement stmt, out _, option);
            var memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out int tlogics, out int tphysics);
            Assert.AreEqual(8, memo.cgroups_.Count);
            Assert.AreEqual(16, tlogics); Assert.AreEqual(20, tphysics);
            Assert.AreEqual("4,1;6,4", string.Join(";", result));
            var mstr = stmt.optimizer_.PrintMemo();
            Assert.IsTrue(mstr.Contains("Summary: 16,20"));

            sql = "select a1 from a, b where a1 <= b1 and a2 = 2 group by a1 order by a1";
            result = TU.ExecuteSQL(sql, out stmt, out phyplan, option);
            memo = stmt.optimizer_.memoset_[0];
            memo.CalcStats(out tlogics, out tphysics);
            Assert.AreEqual(4, memo.cgroups_.Count);
            Assert.AreEqual(5, tlogics); Assert.AreEqual(9, tphysics);
            Assert.AreEqual("1", string.Join(";", result));
            mstr = stmt.optimizer_.PrintMemo();
            Assert.AreEqual(7, TU.CountStr(mstr, "property"));
            Assert.IsTrue(TU.CheckPlanOrder(stmt.physicPlan_,
                new List<string> { "PhysicStreamAgg", "PhysicNLJoin", "PhysicOrder" }));
        }

        [TestMethod]
        public void TestSysteMemoViews()
        {
            // run the target query
            var sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2 order by a2;";
            var result = TU.ExecuteSQL(sql, out var stmt, out _);
            stmt.optimizer_.RegisterMemos();

            // query its memo
            sql = "select * from sys_memo_expr e join sys_memo_property p on e.exprid = p.exprid;";
            result = TU.ExecuteSQL(sql);
            Assert.AreEqual(result.Count, 14);
        }
    }

    [TestClass]
    public class Subquery
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestExistsSubquery()
        {
            QueryOption option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;
                // exist-subquery
                var phyplan = "";
                var sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1);";
                TU.ExecuteSQL(sql, "1;2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a2 from a where exists (select * from a);";
                TU.ExecuteSQL(sql, "1;2;3", out phyplan, option);
                Assert.AreEqual(0, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a2 from a where not exists (select * from a b where b.a3>=a.a1+b.a1+1);";
                TU.ExecuteSQL(sql, "3", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a2 from a where not not not not exists (select * from a b where b.a3>=a.a1+b.a1+1) and a2>2;";
                var result = TU.ExecuteSQL(sql, out phyplan);
                Assert.AreEqual(0, result.Count);
                sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2;";
                TU.ExecuteSQL(sql, "1;2;3", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
                TU.ExecuteSQL(sql, "0,1;1,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicMarkJoin"));
                // multiple subquery - not exists ... and ... to test not <logical_expr> precedence
                sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
                     and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1) and a2>1 and a2<4;";
                TU.ExecuteSQL(sql, "2", out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a1 from a where exists (select b.b1 from b where b.b2=a.a1 and exists (select c.c2 from c where c.c1=b.b1))";
                TU.ExecuteSQL(sql, "1;2", out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a1 from a where a1<=3 and exists (select b.b1 from b where b.b2=a.a1 and exists (select c.c2 from c where c.c1=b.b1 and exists (select d.d1 from d where d.d1=c.c1)))";
                TU.ExecuteSQL(sql, "1;2", out phyplan, option);
                Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicMarkJoin"));
                sql = "select a1 from a where exists (select b.b1 from b where b.b2=a.a1 and exists (select c.c2 from c where c.c1=b.b1 and exists (select d.d1 from d where d.d1=c.c1)))";
                TU.ExecuteSQL(sql, "1;2", out phyplan, option);
                Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicMarkJoin"));
            }
        }

        [TestMethod]
        public void TestInSubquery()
        {
            QueryOption option = new QueryOption();

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;
                var phyplan = "";

                // many NOT test, there are only IN and NOT IN supported in SQL. 
                var sql = "select a1 from a where a2 not not in (1,2)";
                var result = ExecuteSQL(sql); Assert.IsNull(result);
                Assert.IsTrue(TU.error_.Contains(@"no viable alternative at input 'a2 not not'"));

                sql = "select a1 from a where a2 not not not in (1,2)";
                result = ExecuteSQL(sql); Assert.IsNull(result);
                Assert.IsTrue(TU.error_.Contains(@"no viable alternative at input 'a2 not not'"));

                // List InSubquery
                sql = "select a1 from a where a2 not in (1,2)";
                TU.ExecuteSQL(sql, "2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "not in"));
                sql = "select a1 from a where a2 in (1,2)";
                TU.ExecuteSQL(sql, "0;1", out phyplan, option);
                Assert.AreEqual(0, TU.CountStr(phyplan, "not in"));

                // non-corelated InSubquery
                sql = "select a1 from a where a2 not in (select b1 from b where b2>1)"; // not in (1,2)
                TU.ExecuteSQL(sql, "2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "not in"));

                sql = "select a1 from a where a2 in (select b1 from b where b2>1)"; // in (1,2)
                TU.ExecuteSQL(sql, "0;1", out phyplan, option);
                Assert.AreEqual(0, TU.CountStr(phyplan, "not in"));

                // corelated InSubquery
                sql = "select a1 from a where a2 in (select b2 from b where b2 = a1)";
                TU.ExecuteSQL(sql, "", out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "#marker"));

                sql = "select a1 from a where a2 not in (select b2 from b where b2 = a1)";
                TU.ExecuteSQL(sql, "0;1;2", out phyplan, option);
                Assert.AreEqual(2, TU.CountStr(phyplan, "#marker"));

                sql = "select a1 from a where a2 in (select b2 from b where b1 = a1 and b3 > 2 ) and a1 > 0";
                TU.ExecuteSQL(sql, "1;2", out phyplan, option);
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
                TU.ExecuteSQL(sql, "0,2;1,3;2,4", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1, a3  from a where a.a2 = (select b1*2 from b where b2 = a2);";
                TU.ExecuteSQL(sql, "1,3", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
                TU.ExecuteSQL(sql, "0,2", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) and a2>1;";
                TU.ExecuteSQL(sql, "1,3", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                and b.b1 > (select c2 / 2 from c where c.c3 = 3);";
                TU.ExecuteSQL(sql, "2", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
                TU.ExecuteSQL(sql, "2", out phyplan, option); Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = @"select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<3);";
                TU.ExecuteSQL(sql, "0;1", out phyplan, option); Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b2 = 2*a1 and b3>1) and b2<3);";
                TU.ExecuteSQL(sql, "1", out phyplan, option); Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1,a2,b2 from b join a on a1=b1 where a1-1 < (select a2/2 from a where a2=b2);";
                TU.ExecuteSQL(sql, "0,1,1;1,2,2", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));

                //  OR condition failed 
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) or a2>1;";
                sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 or b1 = (select b1 from b where b2 = 2*a1 and b3>1) and b2<3);";
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
            Assert.IsTrue(TU.error_.Contains("exists"));
            sql = "select b.a1 + a2 from (select a1,a2 from a) b";
            TU.ExecuteSQL(sql, "1;3;5");
            sql = "select a3 from (select a1,a3 from a) b";
            TU.ExecuteSQL(sql, "2;3;4");
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            TU.ExecuteSQL(sql, "2;3;4");
            sql = "select count(*) from (select * from a where a1 > 1) b;";
            // run without remove_from
            QueryOption option = new QueryOption();
            option.optimize_.remove_from_ = false;
            result = SQLStatement.ExecSQL(sql, out string phyplan, out _, option);
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
            // run with remove_from=true, which is the default.
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicHashAgg  (actual rows=1)
                               Output: {count(*)(0)}[0]
                               Aggregates: count(*)(0)
                               -> PhysicScanTable a (actual rows=1)
                                   Output: 0
                                   Filter: a.a1[0]>1"; // observing no double push down
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select b1, b2 from (select a3, a4 from a) b(b2);";
            result = ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("b1"));
            sql = "select b2 from (select a3, a4 from a) b(b2,b3,b4);";
            result = ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("more"));
            sql = "select sum(a12) from (select a1*a2 a12 from a);";
            TU.ExecuteSQL(sql, "8");
            sql = "select sum(a12) from (select a1*a2 a12 from a) b;";
            TU.ExecuteSQL(sql, "8");
            sql = "select a4 from (select a3, a4 from a) b(a4);";
            TU.ExecuteSQL(sql, "2;3;4");
            sql = "select c.d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1);";
            TU.ExecuteSQL(sql, "8");
            sql = "select sum(e1) from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) d(e1);";
            TU.ExecuteSQL(sql, "8");
            sql = "select e1 from (select * from (select sum(a12) from (select a1*a2 as a12, a1, a2 from a) b) c(d1)) d(e1);";
            TU.ExecuteSQL(sql, "8");
            sql = "select e1 from (select e1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(e1)) d(e1);";
            TU.ExecuteSQL(sql, "8");
            sql = "select e1 from(select d1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            TU.ExecuteSQL(sql, "8");
            sql = " select a1, sum(a12) from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a1) group by a1;";
            TU.ExecuteSQL(sql, "0,0;1,2;2,6");
            sql = "select a1, sum(a12) as a2 from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a12) group by a1;";
            TU.ExecuteSQL(sql, "0,0");
            sql = @"SELECT e1  FROM   (SELECT d1 FROM   (SELECT Sum(ab12) 
                                        FROM   (SELECT e1 * b2 ab12 FROM   (SELECT e1 FROM   (SELECT d1 
                                                                FROM   (SELECT Sum(ab12) 
                                                                        FROM   (SELECT a1 * b2 ab12 FROM  a  JOIN b ON a1 = b1) b) 
                                                                       c(d1)) 
                                                               d(e1)) a JOIN b ON e1 = 8*b1) b) c(d1)) d(e1); ";
            // REMOVE_FROM: disable it for this PR, it will be fixed in a later one.
            // TU.ExecuteSQL(sql, "16");
            sql = "select *, cd.* from (select a.* from a join b on a1=b1) ab , (select c1 , c3 from c join d on c1=d1) cd where ab.a1=cd.c1";
            TU.ExecuteSQL(sql, "0,1,2,3,0,2,0,2;1,2,3,4,1,3,1,3;2,3,4,5,2,4,2,4");
            #endregion

            // these queries we can remove from
            for (int j = 0; j < 2; j++)
            {
                option.optimize_.remove_from_ = j == 0;
                for (int i = 0; i < 2; i++)
                {
                    option.optimize_.use_memo_ = i == 0;

                    sql = "select a1 from(select b1 as a1 from b) c;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select b1 from (select count(*) as b1 from b) a;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select c100 from (select c1 c100 from c) c where c100>1";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select * from (select a1*a2 a12, a1 a7 from a) b(a12);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select * from (select a1*a2 a12, a1 a7 from a) b;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select *, cd.* from (select a.* from a join b on a1=b1) ab , (select c1 , c3 from c join d on c1=d1) cd where ab.a1=cd.c1";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select a12*a12 from (select a1*a2 a12, a1 a7 from a) b;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    sql = "select a2, count(*), sum(a2) from (select a2 from a) b where a2*a2> 1 group by a2;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("2,1,2;3,1,3", string.Join(";", result));
                    sql = "select b1, b2+b2, c100 from (select b1, count(*) as b2 from b group by b1) a, (select c1 c100 from c) c where c100>1;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("0,2,2;1,2,2;2,2,2", string.Join(";", result));
                    sql = "select b1+b1, b2+b2, c100 from (select b1, count(*) as b2 from b group by b1) a, (select c1 c100 from c) c where c100>1;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("0,2,2;2,2,2;4,2,2", string.Join(";", result));
                    sql = "select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("8", string.Join(";", result));
                    sql = "select e1 from (select d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1)) d(e1);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 3, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("8", string.Join(";", result));
                    sql = "select sum(e1+1) from (select a1, a2, a1*a2 a12 from a) b(e1);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("6", string.Join(";", result));
                    sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b ;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("1;1;1", string.Join(";", result));
                    sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("0,1;1,2", string.Join(";", result));
                    sql = "select b4*b1+b2*b3 from (select 1 as b4, b3, count(*) as b1, sum(b1) b2 from b group by b3) a;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("1;4;9", string.Join(";", result));
                    sql = "select sum(a1)+count(a1) from (select sum(a1) from a group by a2) c(a1);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("6", string.Join(";", result));
                    sql = "select sum(c1*c2+c3) from (select a2, sum(a1), count(a1) from a group by a2) c(c1,c2,c3) group by c1;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("1;3;7", string.Join(";", result));
                    sql = "select sum(c1*c2+c3) from (select a2, sum(a1), count(a1) from a group by a2) c(c1,c2,c3);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("11", string.Join(";", result));
                    sql = "select sum(e1+1) from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) b(e1);";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 3, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("9", string.Join(";", result));
                    sql = "select b4*b1+b2*b3 from (select 1 as b4, b3, count(*) as b1, sum(b1) b2 from b group by b3) a;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("1;4;9", string.Join(";", result));
                    sql = "select b1+b2,c100 from (select count(*) as b1, sum(b1) b2 from b) a, (select c1 c100 from c) c where c100>1;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j == 0 ? 0 : 2, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("6,2", string.Join(";", result));
                    sql = "select * from (select max(b3) maxb3 from b group by b3) b where maxb3>1;";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("2;3;4", string.Join(";", result));
                    sql = "select b1+b2+b3 from (select sum(a1), sum(a2), sum(a1+a2)+a3 from a group by a3) b(b1,b2,b3)";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("4;9;14", string.Join(";", result));
                    sql = "select * from (select sum(a1), sum(a2),sum(a1+a2) from a group by a3) b(b1,b2,b3)";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("0,1,1;1,2,3;2,3,5", string.Join(";", result));
                    sql = "select * from (select sum(a1), sum(a2),sum(a1+a2)+a3 from a group by a3) b(b1,b2,b3)";
                    result = SQLStatement.ExecSQL(sql, out phyplan, out _, option); Assert.AreEqual(j, TU.CountStr(phyplan, "PhysicFromQuery"));
                    Assert.AreEqual("0,1,3;1,2,6;2,3,9", string.Join(";", result));
                }
            }

            // FIXME
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;";
            sql = "select a1 from a, (select max(b3) maxb3 from b) b where a1 < maxb3"; // WRONG!
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;"; // WRONG
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where b1>1 and c100>1;"; // ANSWER WRONG
            sql = "select sum(a1) from (select sum(a1) from (select sum(a1) from a )b(a1) )c(a1);"; // WRONG

            // FIXME: if we turn memo on, we have problems resolving columns

            option.optimize_.remove_from_ = true;
            // More count(*) expressions and remove_from set to true.
            sql = "select k1, k2 from (select count(*) k1, count(*) k2 from a, b) x(k1, k2)";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.IsFalse(phyplan.Contains("PhysicFromQuery"));
            Assert.IsTrue(result.Count == 1);
            Assert.AreEqual("9,9", string.Join(";", result));

            sql = "select k1, k2 from (select count(*) + b1 k1, count(*) + a1 k2 from a, b group by a1, b1) x(k1, k2) order by 1, 2;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.IsFalse(phyplan.Contains("PhysicFromQuery"));
            Assert.AreEqual("1,1;1,2;1,3;2,1;2,2;2,3;3,1;3,2;3,3", string.Join(";", result));

            sql = "select k1 + count(*) from (select a1, sum(b1) from (select c1, count(*) from c group by c1) x(a1, b1) group by a1) z(k1, k2) group by k1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.IsFalse(phyplan.Contains("PhysicFromQuery"));
            Assert.AreEqual("1;2;3", string.Join(";", result));
        }

        [TestMethod]
        public void TestExecSubquery()
        {
            var sql = "select a1, a3  from a where a.a1 = (select b1,b2 from b)";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("one"));
            sql = "select a1, a2  from a where a.a1 = (select b1 from b)";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("one"));
            sql = "select a1,a1,a3,a3, (select * from b where b2=2) from a where a1>1"; // * handling
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("one"));
            sql = "select * from a where a1 > (select b2 from b where a1<>b1)";
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("subquery must return only one row"));

            // subquery in selection
            sql = "select a1,a1,a3,a3, (select b3 from b where b2=2) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,3");
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b1 and b2=3) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,4");
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b2 and b2=3) from a where a1>1"; TU.ExecuteSQL(sql, "2,2,4,4,");

            // scalar subquery
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 3)";
            TU.ExecuteSQL(sql, "2,4");
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
            TU.ExecuteSQL(sql, "0,1,2;1,2,3");
            sql = @" select a1+a2+a3  from a where a.a1 = (select b1 from b bo where b4 = a4 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            TU.ExecuteSQL(sql, "6");
            sql = @"select a4  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
            and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            TU.ExecuteSQL(sql, "3;4;5");
            sql = @"select a1 from a, b where a1=b1 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
            and b1 = (select b1 from b where b3 = a3 and bo.b3 = a3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and b3<5);";
            TU.ExecuteSQL(sql, "0;1;2");
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
            and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            TU.ExecuteSQL(sql, "0;1;2");
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

        [TestMethod]
        public void TestSubqueryRewrite()
        {
            QueryOption option = new QueryOption();

            option.optimize_.enable_subquery_unnest_ = true;

            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;

                var phyplan = "";
                var sql = "select a1, 5+(select b2 from b where b1=a1) from a group by 1;";
                TU.ExecuteSQL(sql, "0,6;1,7;2,8", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2)";
                TU.ExecuteSQL(sql, "0,2;1,3;2,4", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) and a2>1;";
                TU.ExecuteSQL(sql, "1,3", out phyplan, option); Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicSingleJoin"));
                Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicFilter"));
                sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
                TU.ExecuteSQL(sql, "2", out phyplan, option); Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicSingleJoin"));

                // runtime error: more than one row inside
                sql = "select a1 from a where a2 > (select b1 from b where b3>=a3);";
                var result = TU.ExecuteSQL(sql, out phyplan, option); Assert.IsTrue(TU.error_.Contains("one row"));
            }
        }
    }

    [TestClass]
    public class DML
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
            string filename = @"'../../../../data/test.tbl'";
            var sql = $"copy test from {filename};";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as CopyStmt;
            Assert.AreEqual(filename, stmt.fileName_);
            sql = $"copy test from {filename} where t1 >1;";
            var result = TU.ExecuteSQL(sql);
        }
    }

    [TestClass]
    public class ParserAnalyzer
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestColExpr()
        {
            ColExpr col = new ColExpr(null, "a", "a1", new IntType());
            Assert.AreEqual("a.a1", col.ToString());
        }

        [TestMethod]
        public void TestCaseSensitivity()
        {
            string createTable = "create table Case_Sensitive_01(Col1 int, COL2 int, \"Col3\" int, \"COL4\" int, \"GROUP BY\" int);";
            // INSERT shoud work
            var stmtResult = TU.ExecuteSQL(createTable);
            Assert.IsNull(stmtResult);
            Assert.AreEqual("", TU.error_);

            // should work
            string insertStmt = "insert into CASE_SENSITIVE_01 Values(1, 2, 3, 4, 5)";
            stmtResult = TU.ExecuteSQL(insertStmt);
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            insertStmt = "insert into CASE_SENSITIVE_01 values(10, 20, 30, 40, 50)";
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            insertStmt = "insert into CASE_SENSITIVE_01 values(100, 200, 300, 400, 500);";
            stmtResult = TU.ExecuteSQL(insertStmt);
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            insertStmt = "insert into case_sensitive_01(COL1, col2, \"Col3\", \"COL4\", \"GROUP BY\") Values(11, 22, 33, 44, 55)";
            stmtResult = TU.ExecuteSQL(insertStmt);
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            insertStmt = "insert into case_sensitive_01(COL1, col2, \"Col3\", \"COL4\", \"GROUP BY\") values(101, 201, 301, 401, 501)";
            stmtResult = TU.ExecuteSQL(insertStmt);
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            insertStmt = "insert into case_sensitive_01(COL1, col2, \"Col3\", \"COL4\", \"GROUP BY\") values(121, 231, 351, 471, 591);";
            stmtResult = TU.ExecuteSQL(insertStmt);
            Assert.AreEqual(0, stmtResult.Count);
            Assert.AreEqual("", TU.error_);

            string select = "select * from case_sensitive_01";
            stmtResult = TU.ExecuteSQL(select);
            Assert.AreEqual(5, stmtResult.Count);

            select = "select sum(col1), \"GROUP BY\" from Case_SENSITIVE_01 group by \"GROUP BY\"";
            stmtResult = TU.ExecuteSQL(select);
            Assert.AreEqual(5, stmtResult.Count);

            select = "select Col1, \"Col3\" from case_sensitive_01 where \"Col3\" + \"COL4\" > \"COL4\" - COL1";
            stmtResult = TU.ExecuteSQL(select);
            Assert.AreEqual(5, stmtResult.Count);

            select = "select \"Col3\" as COL3, \"COL4\" as col4, \"GROUP BY\" as \"ORDER BY\" from case_SENSITIVE_01";
            stmtResult = TU.ExecuteSQL(select);
            Assert.AreEqual(5, stmtResult.Count);

            select = "select \"T1\".\"GROUP BY\" as gb, t2.\"COL4\" as c4 from case_SENSITIVE_01 as \"T1\" join case_sensitive_01 as T2 on (T2.Col1 / 10 = \"T1\".Col1)";
            stmtResult = TU.ExecuteSQL(select);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(5, stmtResult[0][0]);
            Assert.AreEqual(44, stmtResult[0][1]);

            select = "select \"T1\".Col3 as gb, \"T1\".COL4 as c4 from case_SENSITIVE_01 as \"T1\"";
            stmtResult = TU.ExecuteSQL(select);
            Assert.IsNull(stmtResult);
            Assert.IsTrue(TU.error_.Contains("column not exists \"T1\".col3"));
        }

        [TestMethod]
        public void TestSelectStmt()
        {
            var sql = "with cte1 as (select * from a), cte2 as (select * from b) select a1,a1+a2 from cte1 where a1<6 group by a1, a1+a2 " +
                                "union select b2, b3 from cte2 where b2 > 3 group by b1, b1+b2 " +
                                "order by 2, 1 desc";
            var stmt = RawParser.ParseSingleSqlStatement(sql) as SelectStmt;
            Assert.AreEqual(2, stmt.ctes_.Count);
            Assert.IsFalse(stmt.setops_.IsLeaf());
            Assert.AreEqual(2, stmt.orders_.Count);
        }

        [TestMethod]
        public void TestOutputName()
        {
            var sql = "select a1 from(select b1 as a1 from b) c;";
            TU.ExecuteSQL(sql, "0;1;2");
            sql = "select b1 from(select b1 as a1 from b) c;";
            var result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("b1"));
            sql = "select b1 from(select b1 as a1 from b) c(b1);"; TU.ExecuteSQL(sql, "0;1;2");
            // REMOVE_FROM: disable in this PR, it will be fixed in a later one.
            // sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1"; TU.ExecuteSQL(sql, "5");
            sql = "select 5 as a6 from a where a6 > 2;";    // a6 is an output name
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("a6"));
            sql = "select* from(select 5 as a6 from a where a1 > 1)b where a6 > 1;"; TU.ExecuteSQL(sql, "5");

            // REMOVE_FROM: disable in this PR, it will be fixed in a later one.
            // sql = "select a.b1+c.b1 from (select count(*) as b1 from b) a, (select c1 b1 from c) c where c.b1>1;"; TU.ExecuteSQL(sql, "5");
            sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) and b.b1 > (select c2 / 2 from c where c.c2 = b2);"; TU.ExecuteSQL(sql, "2");
            sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c3 = b3) and b.b1 > (select c2 / 2 from c where c.c3 = b3);"; TU.ExecuteSQL(sql, "2");
            sql = "select a1*a2 a12, a1 a6 from a;"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select * from (select a1*a2 a12, a1 a6 from a) b;"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select * from (select a1*a2 a12, a1 a9 from a) b(a12, a9);"; TU.ExecuteSQL(sql, "0,0;2,1;6,2");
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c(c1,c3) where c3>1) a;"; TU.ExecuteSQL(sql, "7");

            sql = "select a1 as a5, a2 from a where a2>2;"; TU.ExecuteSQL(sql, "2,3");

            sql = "select c1 as c2, c3 from c join d on c1 = d1 and c2=d1;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("conflicting"));
            sql = "select c2, c1+c1 as c2, c3 from c join d on c1 = d1 and (c2+c2)>d1;"; TU.ExecuteSQL(sql, "1,0,2;2,2,3;3,4,4");

            // table alias
            sql = "select * from a , a;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("once"));
            sql = "select * from b , a b;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result); Assert.IsTrue(TU.error_.Contains("once"));
            sql = "select a1,a2,b2 from b, a where a1=b1 and a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0,1,1;1,2,2;2,3,3");
        }

        [TestMethod]
        public void TestCanonical()
        {
            var option = new QueryOption();
            option.optimize_.TurnOnAllOptimizations();
            /*
             * Rule1: Constant move. Bring all possible constants together so that later
             *        transformations can simplify or even remove some of the constants.
             *        Some of them will change when other rules are implmented.
             */
            string sql = "select 1 + a1 + 2 + a2 + 3 + 4 + a4 + 5 + 6 from a";

            var result = ExecuteSQL(sql, out string phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (((((a.a1[0]+3)+a.a2[1])+7)+a.a4[3])+11)"));

            sql = "select 10 + a1 + 2 + abs(-10) + round(101.78, 0) + a2 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (((a.a1[0]+22)+102)+a.a2[1])"));

            // Select expr (4 - a3) / 2 * 2 should not be transformed since (4 - a3) / 2 is
            // a grouping expression.
            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 7,(({(4-a.a3)/2}[0]*2+1)+{sum(a.a1)}[1]),({sum(a.a1)}[1]+{sum((a.a1+a.a2))}[2]*2)"));

            // CAST arg: This may be suspicious: DOUBLE should be printed as a double (101.0)
            // sql = "select 1 + a1, 2 + 3 + a2, cast(101.0 + a3 as double) dcol from a;";
            // result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains("Output: a1[0]+1,a2[1]+5,cast(a3[2]+101 to double)"));

            // FUNC arg, two levels deep.
            sql = "select sum(abs(100 + a1)), count(a2) from a;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: {sum(abs((a.a1+100)))}[0],{count(a.a2)}[1]"));

            // FUNC arg, two.
            sql = "select sum(abs(-10.3 * a1)), sum(round(10.7 * a2, 2)) from a;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: {sum(abs(a.a1*-10.3))}[0],{sum(round(a.a2*10.7,2))}[1]"));

            // Inside subquery. Seems like incorrect result, though. c1 should be 30.9 and c2 should be 64.2
            // The subquery does prodcue the correct result on its own.
            sql = "select c1, c2 from (select sum(abs(-10.3 * a1)) c1, sum(round(10.7 * a2, 2)) c2 from a) x;";
            result = ExecuteSQL(sql, out phyplan);
            var answer = @"PhysicHashAgg  (actual rows=1)
                               Output: {sum(abs(a.a1*-10.3))}[0],{sum(round(a.a2*10.7,2))}[1]
                               Aggregates: sum(abs(a.a1[1]*-10.3)), sum(round(a.a2[4]*10.7,2))
                               -> PhysicScanTable a (actual rows=3)
                                   Output: a.a1[0]*-10.3,a.a1[0],-10.3,a.a2[1]*10.7,a.a2[1],10.7,2";
            TU.PlanAssertEqual(answer, phyplan);

            // In WHERE clause.
            sql = "select c1, c1 from (select sum(abs(a1 * -10.3)) c1, sum(round(10.7 * a2, 2)) c2 from a) x where 10 + c1 < c2";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicHashAgg  (actual rows=1)
                           Output: {sum(abs(a.a1*-10.3))}[0],{sum(abs(a.a1*-10.3))}[0]
                           Aggregates: sum(abs(a.a1[1]*-10.3)), sum(round(a.a2[3]*10.7,2))
                           Filter: ({sum(abs(a.a1*-10.3))}[0]+10)<{sum(round(a.a2*10.7,2))}[1]
                           -> PhysicScanTable a (actual rows=3)
                               Output: a.a1[0]*-10.3,a.a1[0],-10.3,a.a2[1]";

            // In Comparision. This is happening without any of the new ruls.
            sql = "select a1, a2 from a where 100 > a1 + a2;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (a.a1[0]+a.a2[1])<100"));

            // Move constant to right side modify the RHS
            sql = "select a1 from a where a1 + 1 < 3";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]<2"));

            sql = "select a1 from a where a1 - 1 < 3";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]<4"));

            // Rule 2: Constant folding. Replace expressions involving constants with the value of that part of the expression.
            //         Some of this is already in place.
            //         Rule 1 runs first and converts 3+a1[0] to a1[0]+3.
            sql = "select 1 + 2 + a1, a2 + 4 + 5 from a;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (a.a1[0]+3),(a.a2[1]+9)"));

            // Scattered constants
            sql = "select 1 + a1 + 2, 100 + a2 + 15 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (a.a1[0]+3),(a.a2[1]+115)"));   // Rule 1 + Rule 2

            // FUNC(const). Some of it is already done.
            sql = "select round(10.245 + a1 + 10, 2), a2 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: round(((a.a1[0]+10.245)+10),2),a.a2[1]"));

            // FUNC(const).
            sql = "select avg(10), a2 from a group by a2";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("10,{a.a2}[0]"));

            // Rule 3: The Arithmetic Simplification. Eliminate unneeded computations.
            // expr + 0, expr - 0, expr * 0
            // BUG: a.a1 * 0 should be reduced to zero.
            sql = "select a1 * 0, a2 + 0, a3 - 0 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0]*0,a.a2[1],a.a3[2]"));

            // expr * 1, expr / 1
            sql = "select a1 * (14 + 17 - 30), a2 / (14 + 17 - 30) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("a.a1[0],a.a2[1]"));

            // expr / 0 => ERROR
            // sql = "select * from a where a1 / 0 = a2 / 0";
            // Assert.IsTrue(error_.Contains("DivideByZeroException:D"));
            // Currently plan contains: "Filter: a1[0]/0=a2[1]/0";
            // Exception is thrown at runtime.

            // Rule 4: Comparison Simplification. Eliminate unneeded comparisons.
            // CONST relop CONST
            // a1 + 1 < a1 4 => 1 < 4 => TRUE => eliminate filter.
            // BUG: Not happenning.
            sql = "select a1 from a where a1 + 1 < a1 + 4";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (a.a1[0]+1)<(a.a1[0]+4)"));

            // NULL comparisons yeild NULL regardless. NULL testing
            // should be done only as X IS NULL and X IS NOT NULL.
            // Return NULL
            sql = "select * from a where a1 = null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where a1 <> null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where a1 < null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where a1 > null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            // Rule 5: CASE simplification.
            sql = "select CASE WHEN a2 + 110 = 100 + 10 + a2 THEN a1 + 201 ELSE a1 + 501 END as C1 from a";
            result = ExecuteSQL(sql, out phyplan);
            // At the moment there is no way to check from outside if this transformation has happened
            // or not. The plan output simply contains " Output: case with 1" regardless.
            // May need to instrument with extra output for CASE expressionsa and add
            // something like "NORMTRAN: Constant case folded"
            // Assert.IsTrue(phyplan.Contains("NORMTRAN: Constant case folded"));
            Assert.IsTrue(phyplan.Contains("Output: case with 0|1|1"));

            sql = "select CASE WHEN 1 = 1 THEN a1 + 1 ELSE a2 + 2 END from a";
            result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains("NORMTRAN: Constant case folded"));
            Assert.IsTrue(phyplan.Contains("Output: case with 0|1|1"));

            sql = "select CASE WHEN 1 = 0 THEN a1 + 1 ELSE a2 + 2 END from a";
            result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains("NORMTRAN: Constant case folded"));
            Assert.IsTrue(phyplan.Contains("Output: case with 0|1|1"));

            sql = "select CASE WHEN NULL > 1 THEN a1 + 1 ELSE a2 + 2 END from a";
            result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains("NORMTRAN: Constant case folded"));
            Assert.IsTrue(phyplan.Contains("Output: case with 0|1|1"));

            // Rule 6: Logical Simplification.
            sql = "select * from a, b where ((a1 = b1) AND (a2 = b2)) OR ((a1 = 2) AND (a3 = b3))";
            result = ExecuteSQL(sql, out phyplan);
            // This is already happenning but the printing is misleading, it should be changed to
            // Filter: a1[0]=b1[4] and (a2[1]=b2[5] or a3[2]=b3[6])
            // INCOMPLETE
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]=b.b1[4] and a.a2[1]=b.b2[5]) or (a.a1[0]=2 and a.a3[2]=b.b3[6]))"));

            sql = "select * from a where (a1 = 1 and a2 = 2) or (a1 = 1 and a3 = 1)";
            result = ExecuteSQL(sql, out phyplan);
            // This is already happenning but the printing is misleading, it should be changed to
            // Filter: a1[0]=b1[4] and (a2[1]=b2[5] or a3[2]=b3[6])
            // INCOMPLETE
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]=1 and a.a2[1]=2) or (a.a1[0]=1 and a.a3[2]=1))"));

            // NOTE: TRUE and FALSE are not supported in the current code base.
            // Simulating TRUE and FALSE
            // X AND TRUE. Drop TRUE.
            // INCOMPLETE
            sql = "select * from a where (a1 = a2) AND (a3 = a3)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (a.a1[0]=a.a2[1] and a.a3[2]=a.a3[2])"));

            // X AND FALSE. Eliminate the predicate
            // INCOMPLETE
            sql = "select * from a where (a1 = a2) AND (a3 <> a3)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (a.a1[0]=a.a2[1] and a.a3[2]<>a.a3[2])"));

            sql = "select * from a where not (a1 = 1 or a3 = 4);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (!a.a1[0]=1 and !a.a3[2]=4)"));

            // TRUE and FALSE
            sql = "select * from a where a1 + a2 <> a4 AND 1 = 0";
            result = ExecuteSQL(sql, out phyplan);
            // BUG: WHERE should be FALSE but we don't accept WHERE FALSE
            // which is an and FALSE is added.
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]+a.a2[1])<>a.a4[3] and False)"));

            /*
            // Rule 7: IN clause simplification. This may not be needed as it is already happenning.
            // It doesn't seem to be applied in many cases but here is one that should happen.
            sql = "select a1 from a where a2 in (0, 1, 3)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a2[1]=0 OR a2[1]=1 OR a2[1]=3)"));

            // Rule 8: ORDER BY negetive oridinal or column reference.
            // Replace, as in this test case, -a1 by a1 DESCENDING.
            // This will address a very specific bug in the current code base.
            // TODO: The plan is not showing the direction of sort, it should be added.
            sql = "select * from a order by -a1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Order by: a1[0] DESC"));
            */

            // Rule 9: DISTINCT elimination from aggregate functions not sensitive to duplicates.
            // MIN and MAX are not sensitive to duplicates, so eliminating DISTINCT is present may
            // give the optimizer a chance eliminate sort operatior, depending on other
            // conditions, of course.
            sql = "select min(distinct a1), max(distinct a2) from a";
            result = ExecuteSQL(sql, out phyplan);
            // Currently the plan looks the same with and without distinct, both do
            // Hash Aggregation. When distinct is removed, I think hashing can be elimated and the table
            // scan may it self be able to handle min, max but I am not sure. Need to figure out.
            // But if the plan looks like the following, we know hashing/sorting was NOT done.
            // The exact wording in the plan is not clear at this time.
            // Assert.IsFalse(phyplan.Contains("PhysicHashAgg"));
            Assert.IsTrue(phyplan.Contains("Output: {min(a.a1)}[0],{max(a.a2)}[1]"));
            Assert.IsTrue(phyplan.Contains("Aggregates: min(a.a1[0]), max(a.a2[1])"));

            /*
            // Rule 10: Multi-valued predicate simplification.
            // This may be already happeinning but the filter is not present in the plan,
            // need to inverstigate what exactly is hapenning.
            sql = "select * from a where (a1 + 10, a2 + 20, a3 * 30) = (11, 22, 90);";
            result = ExecuteSQL(sql, out phyplan);
            // If filter disappeared as part of some optimization it is good and that can be
            // asserted, otherwise the presence of a disjunctive. At the moment, absense of
            // the filter is what I can look for.
            // Actually this may be a bug (Issue 179), the disapperance of the filter is not
            // optimzation and results are incorrect.
            Assert.IsTrue(phyplan.Contains("Filter: Filter: (((a1[0] + 10) = 11) AND ((a2[1] + 20) = 22) AND ((a3[2] * 30) = 90))"));

            // Rule 11: 11) CAST elimination. When possible, eliminate redundant and unneeded
            // CAST.
            sql = "select cast(a1 * 17.678 as double) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Output: cast(a1[0]*17.678 to double"));

            sql = "select (select cast(a1 * 17.678 as double) from a where a1 = (select min(a1) from a)) C1, (select cast(a2 * 27.678 as double) from a where a2 = (select max(a2) from a)) C2 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Output: cast(a"));
            // In addition, Rule 9 may also have been applied and elimiated so that the following may also
            // be asserted
            Assert.IsFalse(phyplan.Contains("PhysicHashAgg"));

            // Rule 12: Common sub expression elimination.
            // This is somewhat unexpcted, usually the result of AVG is not integral even if the argument is
            // of intergal type but the standard does allow implementation defined type. Postgres promotes
            // to NUEMERIC or DECIMAL or DOUBLE, essentially the result is not truncated and not rounded.
            // In the plan the one of the twice occuring {4-a3/2} may be elimiated but it may already be
            // happenning.
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: {4-a3/2}[0],{4-a3/2}[0]*2+1+{min(a1)}[1],{avg(a4)}[2]+{count(a1)}[3],{max(a1)}[4]+{sum(a1+a2)}[5]*2"));
            */

            // Miscillaneous tests, corner case tests: more coming.
            sql = "select * from a where 10 between 1 and 20;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            sql = "select * from a where 10 between 20 and 1;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where 10 between 20 and 30;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where 10 between a2 and 20;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a2[1]<=10"));

            sql = "select * from a where 'd' between 'a' and 'f';";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            // BUG: both master and canonical branch produce no results
            // but the query is equivalent to select * from a.
            sql = "select * from a where a1 is not null or a2 is not null or a3 is null;";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0] is not null or a.a2[1] is not null) or a.a3[2] is null)"));

            sql = "select * from a join b on (a1 <> a1 or b1 <> b1 or 1 <> -10);";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]<>a.a1[0] or b.b1[4]<>b.b1[4]) or True)"));
            // more tests, by code path and functionality.
            sql = "select sum(1), avg(2), min(3), max(4), count(5), count(distinct 6), stddev_samp(7.38) from a";
            TU.ExecuteSQL(sql, "3,2,3,4,3,3,0", out phyplan, option);

            sql = "select min(1), max(6) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Aggregates:"));

            sql = "select min(10) + max(20) + avg(30) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Aggregates:"));

            sql = "select * from a where not (0 + a1 = 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]=1"));

            sql = "select * from a where not (a1 - 0 <> 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]<>1"));

            sql = "select * from a where not (1 * a1 > 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]>1"));

            sql = "select * from a where not (a1 / 1 >= 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]>=1"));

            sql = "select * from a where not (a1 < 1 - 0)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]<1"));

            sql = "select * from a where not (a1 = 1 * 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]=1"));

            sql = "select * from a where not (a1 <> 1 / 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]<>1"));

            sql = "select * from a where not (a1 > 1 + 0)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]>1"));

            sql = "select * from a where not ((10 - 20 + 5 + 5) > a1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]<0"));

            sql = "select * from a where not ((10 + 20 + 6 + 1) * a1 < 1)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: !a.a1[0]*37<1"));

            sql = "select not (a1 = 1 or a2 = 2) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (!a.a1[0]=1 and !a.a2[1]=2)"));

            sql = "select * from a where 10 between 1 and 20";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            sql = "select * from a where 10 between 20 and 1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where 10 between a2 and 20";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a2[1]<=10"));

            sql = "select * from a where 'd' between 'a' and 'f'";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            // Ideally filter should be false.
            sql = "select * from a where a1 = 1 and a1 = 2";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: (a.a1[0]=1 and a.a1[0]=2)"));

            sql = "select * from a where a1 is not null or a2 is not null or a3 is null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0] is not null or a.a2[1] is not null) or a.a3[2] is null)"));

            sql = "select * from a where a1 is not null and a2 is not null or a3 is null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0] is not null and a.a2[1] is not null) or a.a3[2] is null)"));

            sql = "select 'Hello' || ', World' from a where 1 != -10";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            sql = "select 'Hello' || ', World' from a where 1 != -10 and a4 is null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a4[3] is null"));

            sql = "select substring('alcatrez', 1 + 1, 2 + 3)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'lcat'"));

            sql = "select * from a join b on(1 = 1) where a.a1 > null and b.b2 < null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where 1 + a1 = 3";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]=2"));

            sql = "select * from a, b where 1 = 2";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a join b on (a1 <> a1 or b1 <> b1 or 1 <> -10)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]<>a.a1[0] or b.b1[4]<>b.b1[4]) or True)"));

            sql = "select * from a join b on (a1 <> a1 or b1 <> b1 or 10 + -10 <> -20 + 20)";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: ((a.a1[0]<>a.a1[0] or b.b1[4]<>b.b1[4]) or False)"));

            // sql = "select * from (select 1 + 1 x, 1 + 2 y, a1 z from a)";
            // result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains(
            //    @"PhysicFromQuery 1064_1067 <anonymous> (inccost=6, cost=3, rows=3) (actual rows=3)
            // Output: {2}[0],{3}[1],a.a1 (as z)[2]
            // -> PhysicScanTable 1066_1069 a (inccost=3, cost=3, rows=3) (actual rows=3)
            // Output: 2,3,a.a1 (as z)[0]"));

            sql = "select * from a where 1 is null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where 1 is not null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            sql = "select a1 + 0, a1 - 0, a1 * 1, a1 / 1 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0],a.a1[0],a.a1[0],a.a1[0]"));

            sql = "select 3 * (a1 + 10), 5 * (a2 - 7) , 0 / (a1 + 20) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: (a.a1[0]+10)*3,(a.a2[1]-7)*5,0/(a.a1[0]+20)"));

            sql = "select avg(z) from (select sum((a1 + a2 + 6) * 2) z from a) q";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Aggregates: sum(((a.a1[3]+a.a2[4])*6+2))"));

            sql = "select a1, (a1 * 10) * 5, a2, (a2 * 1) + 10, a3, (a3 + 10 - 20) * 20 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0],a.a1[0]*50,a.a2[1],(a.a2[1]+10),a.a3[2],((a.a3[2]+10)-20)*20"));

            sql = "select a1, (a1 + 10) * 5, a2, (a2 - 1) * 10, a3, (a3 + 10 - 20) * 20 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0],(a.a1[0]*10+5),a.a2[1],(a.a2[1]-1)*10,a.a3[2],((a.a3[2]+10)-20)*20"));

            sql = "select a1, (a1 - null) + 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            sql = "select a1, (a1 + null) * 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            sql = "select a1, (a1 - null) * 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            sql = "select a1, (a1 * null) + 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            sql = "select a1, (a1 * null) * 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            sql = "select a1, (a1 * null) + 10 from a";
            TU.ExecuteSQL(sql, "0,;1,;2,", out phyplan, option);

            // BUG: no output in master and canonical
            sql = "select * from a where 'qwerty' like 'qwer%'";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            // BUG: no output in master and canonical
            sql = "select * from a where 'qwerty' like '%rty'";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select avg(2), min(3), max(4) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Aggregates:"));

            // BUG: in master and canonical:
            // Postgres: 3 | 2.0000000000000000 |   3 |   4 |     3 |     1 |           0
            sql = "select sum(1), avg(2), min(3), max(4), count(5), count(distinct 6), stddev_samp(7.38) from a";
            TU.ExecuteSQL(sql, "3,2,3,4,3,3,0", out phyplan, option);

            sql = "select substring('The North Rim', 5, 9) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'North'"));

            sql = "select substring('The North Rim', 5, 9) || ' Pole' from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'North Pole'"));

            sql = "select substring('Pacific South', 9, 13) || ' Pack' || 'ard' from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'South Packard'"));

            sql = "select upper('mat') || upper('he') || upper('mat') || upper('ics') from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'MATHEMATICS'"));

            sql = "select repeat('three blind mice ', 3) from b";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 'three blind mice three blind mice three blind mice '"));

            // sql = "select abs(10 + 20.78 - 10 - 21.78) from a";
            // result = ExecuteSQL(sql, out phyplan);
            // Assert.IsTrue(phyplan.Contains(
            //    @"PhysicScanTable a (actual rows=3)
            // Output: 1"));

            sql = "select round(3.14582, 2) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 3.15"));

            sql = "select null + null, null - null, null * null, null / null from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null,null,null,null"));

            sql = "select null + 10, 20 - null, 30 * null, 40 / null, null / 50 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null,null,null,null"));

            sql = "select a1 - null, null + a2, a3 * null, null / a4 from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null,null,null,null"));

            sql = "select a1 > null, a2 >= null, a3 < null, a4 <= null, a1 + a2 = null, a2 - a4 <> null from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null,null,null,null,null,null"));

            sql = "select * from a where null is not null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: false"));

            sql = "select * from a where null is null";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsFalse(phyplan.Contains("Filter:"));

            sql = "select sum(null), avg(null), min(null), max(null), count(null) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null,null,null,null,null"));

            sql = "select year(null) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null"));

            sql = "select date(null) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null"));

            sql = "select abs(null) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null"));

            sql = "select round(null, 2) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null"));

            sql = "select stddev_samp(null) from a";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: null"));

            sql = "select * from a where 1 + a1 = 2";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]=1"));

            sql = "select * from a where 1 + a1 > 2";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]>1"));

            sql = "select * from a where 0 <= a1 - 1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]>=1"));

            sql = "select * from a where 3 >= 1 + 2 + a1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Filter: a.a1[0]<=0"));
        }
    }

    [TestClass]
    public class Expression
    {
        [TestMethod]
        public void TestConst()
        {
            var phyplan = "";
            var sql = "select repeat('ab', 2) from a;";
            TU.ExecuteSQL(sql, "abab;abab;abab", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "abab"));
            sql = "select 1+2*3, 1+2+a1 from a where a1+2+(1*5+1)>2*3 and 1+2=2+1;";
            TU.ExecuteSQL(sql, "7,3;7,4;7,5", out phyplan);
            Assert.AreEqual(0, TU.CountStr(phyplan, "True"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Output: 7,(a.a1[0]+3)"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: a.a1[0]>-2"));
            sql = "select 1+20*3, 1+2.1+a1 from a where a1+2+(1*5+1)>2*4.6 and 1+2<2+1.4;";
            TU.ExecuteSQL(sql, "61,5.1", out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: 61,(a.a1[0]+3.1)"));
            Assert.IsTrue((phyplan.Contains("Filter: (a.a1[0]+8)>9.2")));
        }

        [TestMethod]
        public void TestExpr()
        {
            string phyplan;
            var sql = "select a2 from a where a1 between 1  and 2;";
            TU.ExecuteSQL(sql, "2;3");
            sql = "select count(a1) from a where 3>2 or 2<5";
            var answer = @"PhysicHashAgg   (actual rows=1)
                            Output: {count(a.a1)}[0]
                               Aggregates: count(a.a1[0])
                            -> PhysicScanTable a  (actual rows=3)
                                Output: a.a1[0]";
            TU.ExecuteSQL(sql, "3", out phyplan);
            TU.PlanAssertEqual(answer, phyplan);

            // type coerce
            sql = "select 1 + 1.5, 1.75+1.5, 1*1.5, 1.75*1.5";
            TU.ExecuteSQL(sql, "2.5,3.25,1.5,2.625");
            // TBD: add numeric types

            // NOT expr
            sql = "select a1 from a where not (a1 = 1)";
            TU.ExecuteSQL(sql, "0;2");
            sql = "select a1 from a where not not (a1 = 1)";
            TU.ExecuteSQL(sql, "1");
            sql = "select a1 from a where not not not (a1 = 1)";
            TU.ExecuteSQL(sql, "0;2");
            sql = "select * from a where not (a1 = 1 or a3 = 4)";
            TU.ExecuteSQL(sql, "0,1,2,3");
            sql = "select a1 from a where not not not a1 in (1)";
            TU.ExecuteSQL(sql, "0;2");

            // From issue #35, some of the failing ones are passing now
            // BUG: Incorrect results but no longer insists that a1 show up in group by list
            sql = "select abs(-a1*2), count() from a group by round(a1, 10);";
            // There are two more bugs: count() should raise error, results should be the following
            // TU.ExecuteSQL(sql, "0,1;4,1,2,1", out phyplan); // correct output
            TU.ExecuteSQL(sql, "0,1;1,1;2,1", out phyplan);     // incorrect output even after changing to count(*), or count(some column)
            Assert.IsTrue(phyplan.Contains("Output: {abs(-a.a1*2)}[0],{count(*)(0)}[1]"));

            // issue #16
            // outputName shall not be allowed in WHERE/HAVING but in GROUP BY/ORDER BY
            sql = "select a.a1 aa1, b.b2 bb2 from a join b on(a.a3 = b.b4) where aa1 = bb2 group by aa1, bb2 order by aa1 desc";
            var result = TU.ExecuteSQL(sql);
            Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("bind column"));

            sql = "select a1 aa1, sum(a2) aa2 from a group by aa1 having aa2 > 2";
            result = TU.ExecuteSQL(sql);
            Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("bind column"));

            // This should work.
            sql = "select a1 aa1, sum(a2) from a group by aa1";
            TU.ExecuteSQL(sql, "0,1;1,2;2,3", out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: {a.a1 (as aa1)}[0],{sum(a.a2)}[1]"));

            sql = "select a1 aa1, sum(a2) aa2 from a group by aa1 order by aa1";
            TU.ExecuteSQL(sql, "0,1;1,2;2,3", out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1 (as aa1)[0],{sum(a.a2)}[1]"));
        }

        [TestMethod]
        public void TestSimpleAndSearchedCase()
        {
            string sql = null;

            // test simple case
            sql = "select case a1 when 0 then 'a' when 1 then 'b' when 2 then 'c' else 'd' end from a;";
            TU.ExecuteSQL(sql, "a;b;c");
            sql = "select case a1 when a1+a2 then a3 else a4 end from a;";
            TU.ExecuteSQL(sql, "3;4;5");

            // test searched case
            sql = "select case when a1=a2 then a3 else a4 end from a;";
            TU.ExecuteSQL(sql, "3;4;5");
            sql = "select case when a1 is not null then a1 else a4 end from a;";
            TU.ExecuteSQL(sql, "0;1;2");

            // test coalesce function.
            // equivalent to "select case when a1 is not null then a1 else a4 end from a;"
            sql = "select coalesce(a1, a4) from a;";
            TU.ExecuteSQL(sql, "0;1;2");
        }

        [TestMethod]
        public void TestCast()
        {
            var expected = new DateTime(2001, 2, 2).ToString();
            var sql = "select cast('2001-01-3' as date) + interval '30' day;"; TU.ExecuteSQL(sql, expected);
            QueryOption option = new QueryOption();
            option.optimize_.use_memo_ = true;
            sql = "select cast('2001-01-3' as date) + 30 days;"; TU.ExecuteSQL(sql, expected, out _, option);
        }

        [TestMethod]
        public void TestNull()
        {
            var phyplan = "";
            var sql = "select count(*) from r;";
            TU.ExecuteSQL(sql, "3", out phyplan);
            sql = "select count(r1) from r;";
            TU.ExecuteSQL(sql, "1", out phyplan);
            sql = "select " +
              "'|r3: null,null,3|', sum(r1), avg(r1), min(r1), max(r1), count(*), count(r1), " +
              "'|r3: 2,null,4|', sum(r3), avg(r3), min(r3), max(r3), count(r3) from r;";
            TU.ExecuteSQL(sql, "|r3: null,null,3|,3,3,3,3,3,1,|r3: 2,null,4|,6,3,2,4,2", out phyplan);
            sql = "select a1, a2, r1 from r join a on a1=r1 or a2=r1;";
            TU.ExecuteSQL(sql, "2,3,3", out phyplan);
            sql = "select a1, a2, r1 from r join a on a2=r1;";
            TU.ExecuteSQL(sql, "2,3,3", out phyplan);
            sql = "select null=null, null<>null, null>null, null<null, null>=null, null<=null, " +
                "null+null, null-null, null*null, null/null, " +
                "null+8, null-8, null*8, null/8, null/8 is null;";
            TU.ExecuteSQL(sql, ",,,,,,,,,,,,,,True", out phyplan);
        }

        [TestMethod]
        public void TestMisc()
        {
            // number section
            var sql = "select round(a1, 10), count(*) from a group by round(a1, 10)"; TU.ExecuteSQL(sql, "0,1;1,1;2,1");
            sql = "select abs(-a1), count(*) from a group by abs(-a1);"; TU.ExecuteSQL(sql, "0,1;1,1;2,1");

            // string section
            sql = "select upper('aBc') || upper('');";
            TU.ExecuteSQL(sql, "ABC");

            // date section
            sql = "select date '2020-07-06';";
            TU.ExecuteSQL(sql, new DateTime(2020, 07, 06).ToString());
            sql = "select date('2020-07-06');";
            TU.ExecuteSQL(sql, new DateTime(2020, 07, 06).ToString());

            // others
            sql = "select coalesce(coalesce(null, 'a'), 'b');";
            TU.ExecuteSQL(sql, "a");
            sql = "select hash(1), hash('abc'), hash(26.33)";
            TU.ExecuteSQL(sql);
        }

        [TestMethod]
        public void TestNestStrConWithFuncExpr()
        {
            string sql = null;

            sql = "select repeat((substring('Pacific South', 9, 13) || ' Pack' || 'ard'), 3) from a;";
            TU.ExecuteSQL(sql, "South PackardSouth PackardSouth Packard;South PackardSouth PackardSouth Packard;South PackardSouth PackardSouth Packard");

            sql = "select substring(upper('mat') || upper('he') || upper('mat') || upper('ics'), 3, 8) from a;";
            TU.ExecuteSQL(sql, "THEMAT;THEMAT;THEMAT");
        }

        [TestMethod]
        public void TestFuncExprWithNull()
        {
            string scale = "0001";
            Tpch.CreateTables();
            Tpch.LoadTables(scale);
            Tpch.AnalyzeTables();

            string sql = null;

            sql = "select substring(null, 1, 4) from lineitem where l_orderkey=1;";
            TU.ExecuteSQL(sql, ";;;;;");

            sql = "select repeat(null, 3) from lineitem where l_orderkey=1;";
            TU.ExecuteSQL(sql, ";;;;;");

            sql = "select upper(null) from lineitem where l_orderkey=1;";
            TU.ExecuteSQL(sql, ";;;;;");
        }
    }

    [TestClass]
    public class Executors
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestExecSelectFilter()
        {
            var sql = "select a1+a2,a1-a2,a1*a2 from a;";
            var result = ExecuteSQL(sql);
            sql = "select a1 from a where a2>1;";
            TU.ExecuteSQL(sql, "1;2");
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            TU.ExecuteSQL(sql, "2");
            sql = "select a.a1 from a where a2 > 1 or a3> 3;";
            TU.ExecuteSQL(sql, "1;2");
            sql = "select a.a1 from a where a2 > 1 and a3> 3;";
            TU.ExecuteSQL("select a1 from a where a2>2", "2");
            sql = "select a1,a1,a3,a3 from a where a1>1;";
            TU.ExecuteSQL(sql, "2,2,4,4");
            sql = "select a1,a1,a4,a4 from a where a1+a2>2;";
            TU.ExecuteSQL(sql, "1,1,4,4;2,2,5,5");
            sql = "select a1,a1,a3,a3 from a where a1+a2+a3>2;";
            TU.ExecuteSQL(sql, "0,0,2,2;1,1,3,3;2,2,4,4");
            sql = "select a1 from a where a1+a2>2;";
            TU.ExecuteSQL(sql, "1;2");
        }

        [TestMethod]
        public void TestExecResult()
        {
            string sql = "select 2+6*3+2*6";
            var result = ExecuteSQL(sql);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(32, result[0][0]);
            sql = "select substring('abc', 1, 2);";
            TU.ExecuteSQL(sql, "ab");
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
            Assert.IsTrue(TU.error_.Contains("ambigous"));
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
            TU.ExecuteSQL(sql, "3,3,4,3;3,3,4,4;3,3,4,5");
            sql = "select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2;";
            TU.ExecuteSQL(sql, "0,1,2;1,1,2;2,1,2;0,2,3;1,2,3;2,2,3;0,3,4;1,3,4;2,3,4");
        }

        [TestMethod]
        public void TestSort()
        {
            var sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by a3";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("appear"));

            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by 1";
            result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicOrder  (actual rows=2)
                            Output: {(4-a.a3)/2}[0],{(((4-a.a3)/2*2+1)+min(a.a1))}[1],{(avg(a.a4)+count(a.a1))}[2],{(max(a.a1)+sum((a.a1+a.a2))*2)}[3]
                            Order by: {(4-a.a3)/2}[0]
                            -> PhysicHashAgg  (actual rows=2)
                                Output: {(4-a.a3)/2}[0],(({(4-a.a3)/2}[0]*2+1)+{min(a.a1)}[1]),({avg(a.a4)}[2]+{count(a.a1)}[3]),({max(a.a1)}[4]+{sum((a.a1+a.a2))}[5]*2)
                                Aggregates: min(a.a1[1]), avg(a.a4[2]), count(a.a1[1]), max(a.a1[1]), sum((a.a1[1]+a.a2[4]))
                                Group by: {(4-a.a3)/2}[0]
                                -> PhysicScanTable a (actual rows=3)
                                    Output: (4-a.a3[2])/2,a.a1[0],a.a4[3],(a.a1[0]+a.a2[1]),a.a2[1],a.a3[2]";
            TU.PlanAssertEqual(answer, phyplan);
            TU.ExecuteSQL(sql, "0,2,6,18;1,3,4,2");
            sql = "select * from a where a1>0 order by a1;";
            TU.ExecuteSQL(sql, "1,2,3,4;2,3,4,5");
            sql = " select count(a2) as ca2 from a group by a1/2 order by count(a2);";
            TU.ExecuteSQL(sql, "1;2");
            sql = " select count(a2) as ca2 from a group by a1/2 order by 1;";
            TU.ExecuteSQL(sql, "1;2");
            sql = "select -a1/2, -a2 from a order by -a1/2 desc, -a2 asc;"; TU.ExecuteSQL(sql, "0,-2;0,-1;-1,-3");
            sql = "select a2*2, count(a1) from a, b, c where a1=b1 and a2=c2 group by a2 order by 1 desc;"; TU.ExecuteSQL(sql, "6,1;4,1;2,1");
        }

        [TestMethod]
        public void TestSetOps()
        {
            var option = new QueryOption();
            option.optimize_.TurnOnAllOptimizations();

            var sql = "select a2,a3 from a union all select b1,b4 from b group by b1;";
            var result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("appear"));
            sql = "select a2,a3 from a union all select b1,b2 from b order by b1;"; // we allow a2
            result = ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("b1"));

            sql = "select * from a union all select * from b union all select * from c;";
            result = TU.ExecuteSQL(sql, out _, option); Assert.AreEqual(9, result.Count);
            sql = "select a2,a3 from a union all select b1,b1 from b;";
            TU.ExecuteSQL(sql, "1,2;2,3;3,4;0,0;1,1;2,2", out _, option);
            sql = "select a2,a3 from a union all select b1/2,b1/2 from b group by b1/2;";
            TU.ExecuteSQL(sql, "1,2;2,3;3,4;0,0;1,1", out _, option);
            sql = "select a2,a3 from a union all select b1/2,b1/2 from b limit 4;";
            TU.ExecuteSQL(sql, "1,2;2,3;3,4;0,0", out _, option);
            sql = "select a2,a3 from a union all select b1,b2 from b order by 1;";
            TU.ExecuteSQL(sql, "0,1;1,2;1,2;2,3;2,3;3,4", out _, option);
            sql = "select a2,a3 from a union all select b1,b2 from b order by a2;";
            TU.ExecuteSQL(sql, "0,1;1,2;1,2;2,3;2,3;3,4", out _, option);
            sql = "select * from a union all select *from b order by 1 limit 2;";
            TU.ExecuteSQL(sql, "0,1,2,3;0,1,2,3", out _, option);
            sql = "select * from a union all select *from b union all select * from c order by 1 limit 2;";
            TU.ExecuteSQL(sql, "0,1,2,3;0,1,2,3", out _, option);

            sql = "select count(c1), sum(c2) from (select * from a union all select * from b) c(c1,c2)";
            // REMOVE_FROM: disable it for this checkin/PR. It will be fixed by a later one.
            // TU.ExecuteSQL(sql, "6,12", out _, option);
            sql = "select * from (select * from a union all select * from b) c(c1,c2) order by 1";
            // REMOVE_FROM: disable this and the next one in this PR, they will be fixed in a later one.
            // TU.ExecuteSQL(sql, "0,1;0,1;1,2;1,2;2,3;2,3", out _, option);
            sql = "select max(c1), min(c2) from(select * from(select * from a union all select *from b) c(c1, c2))d(c1, c2) order by 1;";
            // TU.ExecuteSQL(sql, "2,1", out _, option);

            // union [all]
            sql = "select a1.* from a, a a1 union select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "0,1,2,3;1,2,3,4;2,3,4,5", out _, option);
            sql = "select a1.a4,a1.a3,a1.a2,a1.a1 from a, a a1 union select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "3,2,1,0;4,3,2,1;5,4,3,2;2,3,4,5", out _, option);

            // except [all]
            sql = "select a1.a4,a1.a3,a1.a2,a1.a1 from a, a a1, a a2 except select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "3,2,1,0;4,3,2,1;5,4,3,2", out _, option);
            sql = "select a1.* from a, a a1, a a2 except select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "0,1,2,3;1,2,3,4", out _, option);

            // intersect [all]
            sql = "select a1.a4,a1.a3,a1.a2,a1.a1 from a, a a1 intersect select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "", out _, option);
            sql = "select a1.* from a, a a1, a a2 intersect select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "2,3,4,5", out _, option);

            // mixed
            // we currently does not support bracket or priority, and order based on sequence - so if you try on PG, use bracket
            sql = "select a1.* from a, a a1, a a2 union select a1.* from a, a a1, a a2 intersect select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "2,3,4,5", out _, option);
            sql = "select a1.* from a, a a1, a a2 union select a1.* from a, a a1, a a2 except select *from b where b1 > 1;";
            TU.ExecuteSQL(sql, "0,1,2,3;1,2,3,4", out _, option);
        }

        [TestMethod]
        public void TestJoin()
        {
            var sql = "select a.a1, b.b1 from a join b on a.a1=b.b1;";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicHashJoin  (actual rows=3)
                            Output: a.a1[0],b.b1[1]
                            Filter: a.a1[0]=b.b1[1]
                            -> PhysicScanTable a (actual rows=3)
                                Output: a.a1[0]
                            -> PhysicScanTable b (actual rows=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);
            TU.ExecuteSQL(sql, "0,0;1,1;2,2");
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
            sql = "select count(a.a1) from a, (select * from b, c) d where a2 > 1";
            TU.ExecuteSQL(sql, "18");

            // hash join 
            sql = "select count(*) from a join b on a1 = b1;";
            TU.ExecuteSQL(sql, "3", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicHashJoin"));
            sql = "select count(*) from a join b on a1 = b1 and a2 = b2;";
            TU.ExecuteSQL(sql, "3", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "HashJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: (a.a1[1]=b.b1[3] and a.a2[2]=b.b2[4])"));
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where a1+b1=c1+d1";
            result = ExecuteSQL(sql, out phyplan);
            Assert.AreEqual(3, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(3, result.Count);

            // Before MEMO, becuase join order prevents push down - comparing below 2 cases. MEMO can resolve their difference.
            var option = new QueryOption();
            sql = "select * from a, b, c where a1 = b1 and b2 = c2;";
            TU.ExecuteSQL(sql, "0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", out phyplan);
            Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashJoin"));
            sql = "select * from a, b, c where a1 = b1 and a1 = c1;";
            option.optimize_.use_memo_ = false;
            TU.ExecuteSQL(sql, "0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", out phyplan, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicHashJoin"));
            Assert.AreEqual(1, TU.CountStr(phyplan, "Filter: (a.a1[0]=b.b1[4] and a.a1[0]=c.c1[8])"));
            option.optimize_.use_memo_ = true;
            TU.ExecuteSQL(sql, "0,1,2,3,0,1,2,3,0,1,2,3;1,2,3,4,1,2,3,4,1,2,3,4;2,3,4,5,2,3,4,5,2,3,4,5", out phyplan, option);
            Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashJoin"));

            // these queries depends on we can decide left/right side parameter dependencies
            option.optimize_.enable_subquery_unnest_ = false;
            sql = "select a1+b1 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0;2;4", out _, option);
            sql = "select a1+b1 from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);"; TU.ExecuteSQL(sql, "0;2;4", out _, option);
            sql = "select a2+c3 from a join c on a1=c1 where a1 < (select b2 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3)"; TU.ExecuteSQL(sql, "3;5;7", out _, option);
            sql = "select a2+c3 from c join a on a1=c1 where a1 < (select b2 from b join a on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3)"; TU.ExecuteSQL(sql, "3;5;7", out _, option);

            // left join
            sql = "select a1,b3 from a left join b on a.a1<b.b1;";
            TU.ExecuteSQL(sql, "0,3;0,4;1,4;2,");

            // FAILED
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1+d1"; // FIXME
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1 and a2+b2=d2;";

            // COUNT(*): remove_from requires naming the derived table columns in most cases, and this is one of them.
            sql = "select * from (select count(*) from a, b where a1 <> b1 and a2 <> b2) s1(s1c), (select count(*) from a, b where a1 <> b3 and a2 <> b4) s2(s2c), (select count(*) from a, b where a1 < b1 and a2 < b2) s3(s3c), (select count(*) from a, b where a1 <> b1 and a2 <> b2) s4(s4c)";
            TU.ExecuteSQL(sql, "6,8,3,6", out phyplan, option);
            Assert.IsTrue(phyplan.Contains("Output: {count(*)(0)}[0],{count(*)(0)}[1],{count(*)(0)}[2],{count(*)(0)}[3]"));

            // Assert fails in master, fixed code doesn't assert but there is no output
            sql = "select (select a1 from a order by -a1 limit 1), count(a1) from a group by (select a1 from a order by -a1 limit 1)";
            TU.ExecuteSQL(sql, out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: {@1}[0],{count(a.a1)}[1]"));

            // in both master and the fix branch (count_star), from command line
            // the out is incorrect {2,1,1}, {0,,}, {1,,} but from this framework
            // it is correct. It is wierder , non deterministic.
            // When "2,1,1;0,,;1,," is used as expected result, actual result is "0,0,0;1,0,0;2,1,1"
            // and when "0,0,0;1,0,0;2,1,1" is used as expected result, actual result is "2,1,1;0,,;1,,"
            // disabling this for now.
            // sql = "select a1, (select count(b2) from b where b1=a1 and b2>2), (select count(b3) from b where b1=a1 and b3>3) from a";
            // TU.ExecuteSQL(sql, "2,1,1;0,,;1,,", out phyplan, option);    // incorrect results
            // TU.ExecuteSQL(sql, "0,0,0;1,0,0;2,1,1"); // correct results
            // Assert.IsTrue(phyplan.Contains(" Output: a.a1[0],{count(b.b2)}[1],{count(b.b3)}[2]"));

            // Asserts in master
            sql = "select a1, (select count(*) from b where b1=a1) from a";
            TU.ExecuteSQL(sql, "0,1;1,1;2,1", out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0],{count(*)(0)}[1]"));

            // Asserts in master
            sql = "select a1, (select count(b1) from b where b1=a1) from a;";
            TU.ExecuteSQL(sql, "0,1;1,1;2,1", out phyplan);
            Assert.IsTrue(phyplan.Contains("Output: a.a1[0],{count(b.b1)}[1]"));
        }

        [TestMethod]
        public void TestLimit()
        {
            var sql = "select a1,a1 from a limit 2;";
            TU.ExecuteSQL(sql, "0,0;1,1");
        }

        [TestMethod]
        public void TestIndex()
        {
            string phyplan;
            var option = new QueryOption();

            var sql = "select * from d where 1*3-1=d1;";
            var result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("2,2,,5", string.Join(";", result));
            sql = "select * from d where 2<d1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("3,3,5,6", string.Join(";", result));
            sql = "select * from d where 2<=d1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("2,2,,5;3,3,5,6", string.Join(";", result));
            sql = "select * from d where 2>d1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("0,1,2,3;1,2,,4", string.Join(";", result));
            sql = "select * from d where 1>=d1;";
            result = SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicIndexSeek"));
            Assert.AreEqual("0,1,2,3;1,2,,4", string.Join(";", result));
            // TODO: not support 2<d1 AND d1<5
        }

        [TestMethod]
        public void TestAggregation()
        {
            var sql = "select a1, sum(a1) from a group by a2";
            var result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("appear"));
            sql = "select max(sum(a)+1) from a;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("nested"));
            sql = "select a1, sum(a1) from a group by a1 having sum(a2) > a3;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("appear"));
            sql = "select * from a having sum(a2) > a1;";
            result = TU.ExecuteSQL(sql); Assert.IsNull(result);
            Assert.IsTrue(TU.error_.Contains("appear"));

            sql = "select 'one', count(b1), count(*), avg(b1), min(b4), count(*), min(b2)+max(b3), sum(b2) from b where b3>1000;";
            TU.ExecuteSQL(sql, "one,0,0,,,0,,");
            sql = "select 'one', count(b1), count(*), avg(b1) from b where b3>1000 having avg(b2) is not null;";
            TU.ExecuteSQL(sql, "");

            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            TU.ExecuteSQL(sql, "7,3,2;7,4,19", out string phyplan);
            var answer = @"PhysicHashAgg  (actual rows=2)
                               Output: 7,(({(4-a.a3)/2}[0]*2+1)+{sum(a.a1)}[1]),({sum(a.a1)}[1]+{sum((a.a1+a.a2))}[2]*2)
                               Aggregates: sum(a.a1[0]), sum((a.a1[0]+a.a2[2]))
                               Group by: (4-a.a3[3])/2
                               -> PhysicScanTable a (actual rows=3)
                                   Output: a.a1[0],(a.a1[0]+a.a2[1]),a.a2[1],a.a3[2]
                         ";
            TU.PlanAssertEqual(answer, phyplan);
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1";
            TU.ExecuteSQL(sql, "1,3,4,2;0,2,6,18");
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            TU.ExecuteSQL(sql, "0,1;1,2");
            sql = "select a2, sum(a1) from a where a1>0 group by a2";
            TU.ExecuteSQL(sql, "2,1;3,2");
            sql = "select a3/2*2, sum(a3), count(a3), stddev_samp(a3) from a group by 1;";
            TU.ExecuteSQL(sql, "2,5,2,0.7071;4,4,1,");
            sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2>1) a;";
            TU.ExecuteSQL(sql, "7");
            sql = "select d1, sum(d2) from (select c1/2, sum(c1) from (select b1, count(*) as a1 from b group by b1)c(c1, c2) group by c1/2) d(d1, d2) group by d1;";
            TU.ExecuteSQL(sql, "0,1;1,2");
            sql = "select b1+b1 from b group by b1;";
            TU.ExecuteSQL(sql, "0;2;4");
            sql = "select sum(b1+b1) from b group by b1;";
            TU.ExecuteSQL(sql, "0;2;4");
            sql = "select 2+b1+b1+b2 from b group by b1,b2;";
            TU.ExecuteSQL(sql, "3;6;9");
            sql = "select sum(2+b1+b1+b2) from b group by b1,b2;";
            TU.ExecuteSQL(sql, "3;6;9");
            sql = "select max(b1) from (select sum(a1) from a)b(b1);";
            TU.ExecuteSQL(sql, "3");
            sql = "select sum(e1+e1*3) from (select sum(a1) a12 from a) d(e1);";
            TU.ExecuteSQL(sql, "12");
            sql = "select a1 from a group by a1 having sum(a2) > 2;";
            TU.ExecuteSQL(sql, "2");
            sql = "select a1, sum(a1) from a group by a1 having sum(a2) > 2;";
            TU.ExecuteSQL(sql, "2,2");
            sql = "select max(b1) from b having max(b1)>1;";
            TU.ExecuteSQL(sql, "2");
            sql = "select a3, sum(a1) from a group by a3 having sum(a2) > a3/2;";
            TU.ExecuteSQL(sql, "3,1;4,2");
            sql = "select 'a'||'b' as k, count(*) from a group by k";
            TU.ExecuteSQL(sql, "ab,3");
            sql = "select 'a'||'b' as k, count(*) from a group by 1";
            TU.ExecuteSQL(sql, "ab,3");

            // subquery as group by expr
            sql = "select count(a1) from a group by (select max(a1) from a);";
            TU.ExecuteSQL(sql, "3");

            // stream aggregation
            sql = "";

            // failed:
            // sql = "select a1, sum(a1) from a group by a1 having sum(a2) > a3;";
            // sql = "select * from a having sum(a2) > 1;";
        }

        public static void TestPullPushAgg()
        {
            var sql = "select count(*) from lineitem, partsupp where l_partkey=ps_suppkey group by ps_availqty>100";
            TU.ExecuteSQL(sql, "23294;226", out string phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "PhysicHashAgg"));
            sql = "select sum(c) from lineitem, (select ps_suppkey, ps_availqty>100, count(*) from partsupp group by ps_suppkey, ps_availqty>100) ps(ps_suppkey, ps_availqty100, c)"
                + " where ps_suppkey=l_partkey group by ps_availqty100;";
            TU.ExecuteSQL(sql, "23294;226", out phyplan);
            Assert.AreEqual(2, TU.CountStr(phyplan, "PhysicHashAgg"));
        }
    }

    [TestClass]
    public class General
    {
        internal List<Row> ExecuteSQL(string sql) => TU.ExecuteSQL(sql);
        internal List<Row> ExecuteSQL(string sql, out string physicplan) => TU.ExecuteSQL(sql, out physicplan);

        [TestMethod]
        public void TestCTE()
        {
            QueryOption option = new QueryOption();
            for (int i = 0; i < 2; i++)
            {
                option.optimize_.use_memo_ = i == 0;
                for (int j = 0; j < 2; j++)
                {
                    option.optimize_.enable_cte_plan_ = j == 0;

                    var sql = "with cte1 as (select* from a) select * from a where a1>1;"; TU.ExecuteSQL(sql, "2,3,4,5", out _, option);
                    sql = "with cte1 as (select* from a) select * from cte1 where a1>1;"; TU.ExecuteSQL(sql, "2,3,4,5", out _, option);
                    sql = "with cte1 as (select * from a),cte3 as (select * from cte1) select * from cte3 where a1>1"; TU.ExecuteSQL(sql, "2,3,4,5", out _, option);
                    sql = @"with cte1 as (select b3, max(b2) maxb2 from b where b1<1 group by b3)
                        select a1, maxb2 from a, cte1 where a.a3=cte1.b3 and a1<2;"; TU.ExecuteSQL(sql, "0,1");
                    sql = @"with cte1 as (select* from a),	cte2 as (select* from b),
                    	cte3 as (with cte31 as (select* from c)
                                select* from cte2 , cte31 where b1 = c1)
                    select max(cte3.b1) from cte3;"; TU.ExecuteSQL(sql, "2", out _, option);
                    sql = "with cte as (select * from a) select * from cte cte1, cte cte2 where cte1.a2=cte2.a3 and cte1.a1> 0;";
                    TU.ExecuteSQL(sql, "1,2,3,4,0,1,2,3;2,3,4,5,1,2,3,4", out _, option);

                    sql = "with cte as (select * from a) select cte1.a1, cte2.a2 from cte cte1, cte cte2 where cte2.a3<3";
                    TU.ExecuteSQL(sql, "0,1;1,1;2,1", out _, option);
                    sql = "with cte as (select * from a where a1=1) select * from cte cte1, cte cte2;";
                    TU.ExecuteSQL(sql, "1,2,3,4,1,2,3,4", out _, option);
                    sql = "select ab.a1, cd.c1 from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
                    TU.ExecuteSQL(sql, "0,0;1,1;2,2", out _, option);
                    sql = "with cte as (select avg(a2) from a join b on a1=b1) select * from cte cte1, cte cte2;";
                    TU.ExecuteSQL(sql, "2,2", out _, option);
                    sql = "with cte as (select count(*) from a join b on a1=b1) select * from cte cte1, cte cte2;";
                    TU.ExecuteSQL(sql, "3,3", out _, option);
                }
            }
        }

        [TestMethod]
        public void TestFromQueryRemoval()
        {
           
            QueryOption option = new QueryOption();
            option.optimize_.remove_from_ = false;
            var sql = "select b1+b1 from (select b1 from b) a";
            var stmt = RawParser.ParseSingleSqlStatement(sql);
            SQLStatement.ExecSQL(sql, out string phyplan, out _, option);
            var answer = @"PhysicFromQuery <a>  (actual rows=3)
                            Output: (a.b1[0]+a.b1[0])
                            -> PhysicScanTable b  (actual rows=3)
                                Output: b.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);

            sql = "select b1+b1 from (select b1 from b) a";
            stmt = RawParser.ParseSingleSqlStatement(sql);
            Debug.Assert(stmt.queryOpt_.optimize_.remove_from_ == true);
            SQLStatement.ExecSQL(sql, out phyplan, out _);
            answer = @"PhysicScanTable b (actual rows=3)
                               Output: (b.b1[0]+b.b1[0])";
            TU.PlanAssertEqual(answer, phyplan);
            sql = "select b1+c1 from (select b1 from b) a, (select c1 from c) c where c1>1";
            // without remove_from
            SQLStatement.ExecSQL(sql, out phyplan, out _, option); // FIXME: filter is still there
            answer = @"PhysicFilter  (actual rows=3)
                        Output: {(a.b1+c.c1)}[0]
                        Filter: c.c1[1]>1
                        -> PhysicNLJoin  (actual rows=9)
                            Output: (a.b1[0]+c.c1[1]),c.c1[1]
                            -> PhysicFromQuery <a> (actual rows=3)
                                Output: a.b1[0]
                                -> PhysicScanTable b (actual rows=3)
                                    Output: b.b1[0]
                            -> PhysicFromQuery <c> (actual rows=3, loops=3)
                                Output: c.c1[0]
                                -> PhysicScanTable c (actual rows=3, loops=3)
                                    Output: c.c1[0]";
            // with remove_from
            stmt = RawParser.ParseSingleSqlStatement(sql);
            SQLStatement.ExecSQL(sql, out phyplan, out _); // FIXME: filter is still there
            answer = @"PhysicNLJoin  (actual rows=3)
                           Output: (b.b1[0]+c.c1[1])
                           -> PhysicScanTable b (actual rows=3)
                               Output: b.b1[0]
                           -> PhysicScanTable c (actual rows=1, loops=3)
                               Output: c.c1[0]
                               Filter: c.c1[0]>1";
            TU.PlanAssertEqual(answer, phyplan);
            var result = ExecuteSQL(sql);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("2", result[0].ToString());
            Assert.AreEqual("3", result[1].ToString());
            Assert.AreEqual("4", result[2].ToString());
            sql = "select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c where c2-b1>1";
            stmt = RawParser.ParseSingleSqlStatement(sql);
            // without remove_from
            SQLStatement.ExecSQL(sql, out phyplan, out _, option);
            answer = @"PhysicNLJoin  (actual rows=3)
                        Output: (a.b1[0]+c.c1[1])
                        Filter: (c.c2[2]-a.b1[0])>1
                        -> PhysicFromQuery <a> (actual rows=3)
                            Output: a.b1[0]
                            -> PhysicScanTable b (actual rows=3)
                                Output: b.b1[0]
                        -> PhysicFromQuery <c> (actual rows=3, loops=3)
                            Output: c.c1[0],c.c2[1]
                            -> PhysicScanTable c (actual rows=3, loops=3)
                                Output: c.c1[0],c.c2[1]";
            // with remove_from
            stmt = RawParser.ParseSingleSqlStatement(sql);
            SQLStatement.ExecSQL(sql, out phyplan, out _);
            answer = @"PhysicNLJoin  (actual rows=3)
                           Output: (b.b1[0]+c.c1[1])
                           Filter: (c.c2[2]-b.b1[0])>1
                           -> PhysicScanTable b (actual rows=3)
                               Output: b.b1[0]
                           -> PhysicScanTable c (actual rows=3, loops=3)
                               Output: c.c1[0],c.c2[1]";
            TU.PlanAssertEqual(answer, phyplan);
            TU.ExecuteSQL(sql, "1;2;3");
        }

        [TestMethod]
        public void TestDataSet()
        {
            SQLContext sqlContext = new SQLContext();

            // register c#'s sqrt as an external function
            string sqroot(double d) => Math.Sqrt(d).ToString("#.###");
            SQLContext.Register<double, string>("sqroot", sqroot);

            var sql = "SELECT a1, sqroot(b1*a1+2) from a join b on b2=a2 where a1>1";
            TU.ExecuteSQL(sql, "2,2.449");

            // above query in DataSet form
            var a = sqlContext.Read("a");
            var b = sqlContext.Read("b");
            var rows = a.filter("a1>1").join(b, "b2=a2").select("a1", "sqroot(b1*a1+2)").show();
            Assert.AreEqual(string.Join(",", rows), "2,2.449");

            // Pi Monte-carlo evaluation 
            Random rand = Catalog.rand_;
            int inside(int d)
            {
                var x = rand.NextDouble();
                var y = rand.NextDouble();
                var ret = ((x * x) + (y * y)) <= 1 ? 1 : 0;
                return ret;
            }

            SQLContext.Register<int, int>("inside", inside);
            sql = "SELECT 4.0*sum(inside(a1.a1))/count(*) from a a1, a a2, a a3, a a4, a a5, a a6, a a7, a a8, a a9, a a10";
            rows = SQLStatement.ExecSQL(sql, out _, out _);
            Assert.IsTrue((double)rows[0][0] > 3.12 && (double)rows[0][0] < 3.16);
        }

        [TestMethod]
        public void TestPushdown()
        {
            var option = new QueryOption();
            var sql = "select a.a2,a3,a.a1+b2 from a,b where a.a1 > 1 and a1+b3>2";
            var result = ExecuteSQL(sql, out string phyplan);
            var answer = @"PhysicNLJoin   (actual rows=3)
                        Output: a.a2[0],a.a3[1],(a.a1[2]+b.b2[3])
                        Filter: (a.a1[2]+b.b3[4])>2
                        -> PhysicScanTable a  (actual rows=1)
                            Output: a.a2[1],a.a3[2],a.a1[0]
                            Filter: a.a1[0]>1
                        -> PhysicScanTable b  (actual rows=3)
                            Output: b.b2[1],b.b3[2]";
            TU.PlanAssertEqual(answer, phyplan);

            // FIXME: you can see c1+b1>2 is not pushed down
            sql = "select a1,b1,c1 from a,b,c where a1+b1+c1>5 and c1+b1>2";
            TU.ExecuteSQL(sql, "2,2,2", out phyplan);
            answer = @"PhysicNLJoin  (actual rows=1)
    Output: a.a1[0],b.b1[1],c.c1[2]
    Filter: ((a.a1[0]+b.b1[1])+c.c1[2])>5
    -> PhysicScanTable a (actual rows=3)
        Output: a.a1[0]
    -> PhysicNLJoin  (actual rows=3, loops=3)
        Output: b.b1[1],c.c1[0]
        Filter: (c.c1[0]+b.b1[1])>2
        -> PhysicScanTable c (actual rows=3, loops=3)
            Output: c.c1[0]
        -> PhysicScanTable b (actual rows=3, loops=9)
            Output: b.b1[0]";

            TU.PlanAssertEqual(answer, phyplan);

            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            option.optimize_.enable_subquery_unnest_ = false;
            result = TU.ExecuteSQL(sql, out phyplan, option);
            answer = @"PhysicScanTable a (actual rows=0)
    Output: 1
    Filter: a.a1[0]>@1
    <ScalarSubqueryExpr> cached 1
    -> PhysicScanTable b (actual rows=0)
         Output: b.b1[0],#b.b2[1]
         Filter: (b.b2[1]>@2 and b.b1[0]>@3)
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
                                Output: b.b1[0]
                                Filter: b.b2[1]>c.c2[2]
                                -> PhysicSingleJoin Left (actual rows=0)
                                    Output: b.b1[0],b.b2[1],c.c2[2]
                                    Filter: c.c2[2]=b.b2[1]
                                    -> PhysicFilter  (actual rows=0)
                                        Output: b.b1[0],b.b2[1]
                                        Filter: b.b1[0]>c.c2[2]
                                        -> PhysicSingleJoin Left (actual rows=3)
                                            Output: b.b1[0],b.b2[1],c.c2[2]
                                            Filter: c.c2[2]=b.b2[1]
                                            -> PhysicScanTable b (actual rows=3)
                                                Output: b.b1[0],b.b2[1]
                                            -> PhysicScanTable c (actual rows=3, loops=3)
                                                Output: c.c2[1]
                                    -> PhysicScanTable c (actual rows=0)
                                        Output: c.c2[1]";
            TU.PlanAssertEqual(answer, phyplan);
            sql = "select 1 from a where a.a1 >= (select b1 from b where b.b2 >= (select c2 from c where c.c2=b2) and b.b1*2 = ((select c2 from c where c.c2=b2)))";
            TU.ExecuteSQL(sql, "1;1", out phyplan);

            // b3+c2 as a whole push to the outer join side
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            result = ExecuteSQL(sql, out phyplan);
            answer = @"PhysicFilter  (actual rows=27)
    Output: {(b.b3+c.c2)}[0]
    Filter: a.a1[1]>=b__1.b1[2]
    -> PhysicSingleJoin Left (actual rows=27)
        Output: {(b.b3+c.c2)}[0],a.a1[1],b__1.b1[2]
        Filter: b__1.b1[2]=a.a1[1]
        -> PhysicFilter  (actual rows=27)
            Output: {(b.b3+c.c2)}[0],a.a1[1]
            Filter: a.a2[2]>=c__2.c2[3]
            -> PhysicSingleJoin Left (actual rows=27)
                Output: {(b.b3+c.c2)}[0],a.a1[1],a.a2[2],c__2.c2[3]
                Filter: c__2.c1[4]=a.a1[1]
                -> PhysicNLJoin  (actual rows=27)
                    Output: {(b.b3+c.c2)}[2],a.a1[0],a.a2[1]
                    -> PhysicScanTable a (actual rows=3)
                        Output: a.a1[0],a.a2[1]
                    -> PhysicNLJoin  (actual rows=9, loops=3)
                        Output: (b.b3[1]+c.c2[0])
                        -> PhysicScanTable c (actual rows=3, loops=3)
                            Output: c.c2[1]
                        -> PhysicScanTable b (actual rows=3, loops=9)
                            Output: b.b3[2]
                -> PhysicScanTable c as c__2 (actual rows=3, loops=27)
                    Output: c__2.c2[1],c__2.c1[0]
        -> PhysicScanTable b as b__1 (actual rows=3, loops=27)
            Output: b__1.b1[0]";
            TU.PlanAssertEqual(answer, phyplan);

            // key here is bo.b3=a3 show up in 3rd subquery
            sql = @"select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 
                        and bo.b3 = a3 and b3> 1) and b2<3);";
            option.optimize_.enable_subquery_unnest_ = false;
            TU.ExecuteSQL(sql, "0;1", out phyplan, option);
            answer = @"PhysicScanTable a (actual rows=2)
    Output: a.a1[0],#a.a2[1],#a.a3[2]
    Filter: a.a1[0]=@1
    <ScalarSubqueryExpr> 1
        -> PhysicScanTable b as bo (actual rows=0, loops=3)
            Output: bo.b1[0],#bo.b3[2]
            Filter: ((bo.b2[1]=?a.a2[1] and bo.b1[0]=@2) and bo.b2[1]<3)
            <ScalarSubqueryExpr> 2
                -> PhysicScanTable b (actual rows=0, loops=9)
                    Output: b.b1[0]
                    Filter: ((b.b3[2]=?a.a3[2] and ?bo.b3[2]=?a.a3[2]) and b.b3[2]>1)
";
            TU.PlanAssertEqual(answer, phyplan);
            TU.ExecuteSQL(sql, "0;1", out phyplan);
            answer = @"PhysicFilter  (actual rows=2)
    Output: a.a1[0]
    Filter: a.a1[0]=bo.b1[1]
    -> PhysicSingleJoin Left (actual rows=3)
        Output: a.a1[0],bo.b1[3]
        Filter: ((b.b3[4]=a.a3[1] and bo.b3[5]=a.a3[1]) and bo.b2[6]=a.a2[2])
        -> PhysicScanTable a (actual rows=3)
            Output: a.a1[0],a.a3[2],a.a2[1]
        -> PhysicFilter  (actual rows=2, loops=3)
            Output: bo.b1[0],b.b3[1],bo.b3[2],bo.b2[3]
            Filter: bo.b1[0]=b.b1[4]
            -> PhysicSingleJoin Left (actual rows=6, loops=3)
                Output: bo.b1[0],b.b3[3],bo.b3[1],bo.b2[2],b.b1[4]
                -> PhysicScanTable b as bo (actual rows=2, loops=3)
                    Output: bo.b1[0],bo.b3[2],bo.b2[1]
                    Filter: bo.b2[1]<3
                -> PhysicScanTable b (actual rows=3, loops=6)
                    Output: b.b3[2],b.b1[0]
                    Filter: b.b3[2]>1
";
            TU.PlanAssertEqual(answer, phyplan);
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            option.optimize_.enable_subquery_unnest_ = false;
            // REMOVE_FROM: disable these two in this PR, they will be fixed in a later PR.
#if REMOVE_FROM
            TU.ExecuteSQL(sql, "0;1;2", out phyplan, option);
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
            Filter: (a.a1[0]=@1 and a.a2[1]=@3)
            <ScalarSubqueryExpr> 1
                -> PhysicFilter  (actual rows=0, loops=9)
                    Output: bo.b1[0]
                    Filter: ((bo.b2[1]=?a.a2[1] and bo.b2[1]<5) and bo.b1[0]=@2)
                    <ScalarSubqueryExpr> 2
                        -> PhysicScanTable b as b__2 (actual rows=0, loops=81)
                            Output: b__2.b1[0]
                            Filter: ((b__2.b3[2]=?a.a3[2] and ?bo.b3[2]=?c.c3[2]) and b__2.b3[2]>1)
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
                    Filter: ((bo.b1[0]=?a.a1[0] and bo.b2[1]=@4) and ?c.c3[2]<5)
                    <ScalarSubqueryExpr> 4
                        -> PhysicScanTable b as b__4 (actual rows=0, loops=27)
                            Output: b__4.b2[1]
                            Filter: ((b__4.b4[3]=(?a.a3[2]+1) and ?bo.b3[2]=?a.a3[2]) and b__4.b3[2]>0)";
            TU.PlanAssertEqual(answer, phyplan);

            // run again with subquery expansion enabled
            // FIXME: b2<5 is not push down due to FromQuery barrier
            TU.ExecuteSQL(sql, "0;1;2", out phyplan);
            answer = @"PhysicFilter  (actual rows=3)
    Output: a.a1[0]
    Filter: bo.b2[1]<5
    -> PhysicFilter  (actual rows=3)
        Output: a.a1[0],bo.b2[1]
        Filter: a.a1[0]=bo.b1[2]
        -> PhysicSingleJoin Left (actual rows=3)
            Output: a.a1[0],bo.b2[4],bo.b1[5]
            Filter: ((b__2.b3[6]=a.a3[1] and bo.b3[7]=c.c3[2]) and bo.b2[4]=a.a2[3])
            -> PhysicFilter  (actual rows=3)
                Output: a.a1[0],a.a3[1],c.c3[2],a.a2[3]
                Filter: a.a2[3]=bo.b2[4]
                -> PhysicSingleJoin Left (actual rows=3)
                    Output: a.a1[0],a.a3[1],c.c3[2],a.a2[3],bo.b2[4]
                    Filter: ((b__4.b4[5]=(a.a3[1]+1) and bo.b3[6]=a.a3[1]) and bo.b1[7]=a.a1[0])
                    -> PhysicHashJoin  (actual rows=3)
                        Output: a.a1[2],a.a3[3],c.c3[0],a.a2[4]
                        Filter: b.b2[5]=c.c2[1]
                        -> PhysicScanTable c (actual rows=3)
                            Output: c.c3[2],c.c2[1]
                            Filter: c.c3[2]<5
                        -> PhysicHashJoin  (actual rows=3)
                            Output: a.a1[2],a.a3[3],a.a2[4],b.b2[0]
                            Filter: a.a1[2]=b.b1[1]
                            -> PhysicScanTable b (actual rows=3)
                                Output: b.b2[1],b.b1[0]
                            -> PhysicScanTable a (actual rows=3)
                                Output: a.a1[0],a.a3[2],a.a2[1]
                    -> PhysicFilter  (actual rows=3, loops=3)
                        Output: bo.b2[0],b__4.b4[1],bo.b3[2],bo.b1[3]
                        Filter: bo.b2[0]=b__4.b2[4]
                        -> PhysicSingleJoin Left (actual rows=9, loops=3)
                            Output: bo.b2[0],b__4.b4[3],bo.b3[1],bo.b1[2],b__4.b2[4]
                            -> PhysicScanTable b as bo (actual rows=3, loops=3)
                                Output: bo.b2[1],bo.b3[2],bo.b1[0]
                            -> PhysicScanTable b as b__4 (actual rows=3, loops=9)
                                Output: b__4.b4[3],b__4.b2[1]
                                Filter: b__4.b3[2]>0
            -> PhysicFilter  (actual rows=9, loops=3)
                Output: bo.b2[0],bo.b1[1],b__2.b3[2],bo.b3[3]
                Filter: bo.b1[1]=b__2.b1[4]
                -> PhysicSingleJoin Left (actual rows=27, loops=3)
                    Output: bo.b2[0],bo.b1[1],b__2.b3[3],bo.b3[2],b__2.b1[4]
                    -> PhysicFromQuery <bo> (actual rows=9, loops=3)
                        Output: bo.b2[1],bo.b1[0],bo.b3[2]
                        -> PhysicNLJoin  (actual rows=9, loops=3)
                            Output: b_2.b1[2],b_1.b2[0],b_1.b3[1]
                            -> PhysicScanTable b as b_1 (actual rows=3, loops=3)
                                Output: b_1.b2[1],b_1.b3[2]
                            -> PhysicScanTable b as b_2 (actual rows=3, loops=9)
                                Output: b_2.b1[0]
                    -> PhysicScanTable b as b__2 (actual rows=3, loops=27)
                        Output: b__2.b3[2],b__2.b1[0]
                        Filter: b__2.b3[2]>1";
            TU.PlanAssertEqual(answer, phyplan);
#endif
        }
    }

    [TestClass]
    public class Distributed
    {
        [TestMethod]
        public void Gather()
        {
            var phyplan = "";
            var sql = "select a1,a2 from ad order by a1;";
            TU.ExecuteSQL(sql, "0,1;1,2;2,3", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
            sql = "select a1,a2 from ar;";
            TU.ExecuteSQL(sql, "0,1;1,2;2,3", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
            sql = "select a1,a2 from arb order by a1;";
            TU.ExecuteSQL(sql, "0,1;1,2;2,3", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
        }

        [TestMethod]
        public void Redistribute()
        {
            var option = new QueryOption();

            for (int i = 0; i < 3; i++)
            {
                // use broadcast for the last round
                bool enable_bc = i == 2;
                option.optimize_.enable_broadcast_ = enable_bc;
                var phyplan = "";

                // needs order by to force result order
                var sql = "select a1,b1 from ad, b where a1=b1 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));
                sql = "select a1,b1 from ad, bd where a1=b1 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));
                sql = "select a2,b2,c2 from ad, bd, cd where a2=b2 and c2 = b2 order by c2";
                TU.ExecuteSQL(sql, "1,1,1;2,2,2;3,3,3", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 3, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 2 : 0, TU.CountStr(phyplan, "Broadcast"));
                sql = "select a2,b2,c2,d2 from ad, bd, cd, dd where a2=b2 and c2 = b2 and c2=d2 order by b2";
                TU.ExecuteSQL(sql, "1,1,1,1;2,2,2,2;2,2,2,2;3,3,3,3", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 4, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 3 : 0, TU.CountStr(phyplan, "Broadcast"));
                Assert.AreEqual(enable_bc ? 0 : 1, TU.CountStr(phyplan, "threads: 50"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "threads: 40"));

                // ensure redistribution can shuffle by expression
                sql = "select a2, b2 from ad, bd where a2*2+a1=b2 order by a2;";
                TU.ExecuteSQL(sql, "1,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 2, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "Broadcast"));

                // no output if by previous r[0] method for redistribution
                sql = "select d2, a1 from ad, dd where d3=a1 order by d2;";
                TU.ExecuteSQL(sql, "1,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 1, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "Broadcast"));
                sql = "select d2, a2 from ad, dd where d4=a2 order by d2;";
                TU.ExecuteSQL(sql, "1,3", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 2, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "Broadcast"));

                // mixed with replicated table
                sql = "select a1,b1 from ad, br where a1=b1 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));
                sql = "select a1,b1 from ad, br where a2=b2 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));
                sql = "select a1,b1 from ar, br where a2=b2 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));

                // mixed with rounbrobin table
                sql = "select a1,b1 from ad, brb where a1=b1 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(1, TU.CountStr(phyplan, "Redistribute"));
                sql = "select a1,b1 from ad, brb where a2=b2 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 2, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "Broadcast"));
                sql = "select a1,b1 from arb, brb where a2=b2 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(enable_bc ? 0 : 2, TU.CountStr(phyplan, "Redistribute"));
                Assert.AreEqual(enable_bc ? 1 : 0, TU.CountStr(phyplan, "Broadcast"));
                sql = "select a1,b1 from ar, brb where a2=b2 order by a1;";
                TU.ExecuteSQL(sql, "0,0;1,1;2,2", out phyplan, option);
                Assert.AreEqual(1, TU.CountStr(phyplan, "Gather"));
                Assert.AreEqual(0, TU.CountStr(phyplan, "Redistribute"));
            }
        }
    }

    [TestClass]
    public class Streaming
    {
        [TestMethod]
        public void TumbleWindow()
        {
            var phyplan = "";
            var sql = "select count(*) from ast group by tumble(a0, interval '10' second)";
            TU.ExecuteSQL(sql, "2;2;1", out phyplan);
            sql = "select tumble_start(a0, interval '10' second), tumble_end(a0, interval '10' second), " +
                "count(*) from ast group by tumble(a0, interval '10' second)";
            TU.ExecuteSQL(sql, $"{new DateTime(2020, 5, 12, 7, 22, 10).ToString()},{new DateTime(2020, 5, 12, 7, 22, 20).ToString()},2;" +
                $"{new DateTime(2020, 5, 12, 7, 22, 20).ToString()},{new DateTime(2020, 5, 12, 7, 22, 30).ToString()},2;" +
                $"{new DateTime(2020, 5, 12, 7, 22, 50).ToString()},{new DateTime(2020, 5, 12, 7, 23, 0).ToString()},1", out phyplan);
        }

        [TestMethod]
        public void HopWindow()
        {
            var phyplan = "";
            var sql = "select count(*) from ast group by hop(a0, interval '5' second, interval '10' second)";
            TU.ExecuteSQL(sql, "2;4;2;1;1", out phyplan);
            Assert.AreEqual(1, TU.CountStr(phyplan, "ProjectSet"));
        }
    }

    [TestClass]
    public class Cardinality
    {
        internal void CheckTableLoad()
        {
            Tpch.CreateTables();
            Tpch.LoadTables("0001");
            Tpch.AnalyzeTables();
        }

        [TestMethod]
        public void PrimitiveTest()
        {
            CheckTableLoad();

            var option = new QueryOption();
            option.explain_.mode_ = ExplainMode.analyze;
            option.explain_.show_estCost_ = true;

            string allquery = File.ReadAllText("../../../regress/sql/ce.sql");
            string[] listquery = allquery.Split(';');

            List<string> listoutput = new List<string>();

            for (int i = 0; i < listquery.Length; i++)
            {
                string sql = listquery[i].Trim();
                if (sql.Length <= 0) continue;

                var result = SQLStatement.ExecSQL(sql, out string physicplan, out string error_, option);
                Assert.IsNotNull(physicplan);

                listoutput.Add(physicplan);
            }
            string alloutput = string.Join('\n', listoutput);
            File.WriteAllText($"../../../regress/output/ce.out", alloutput);

            string expected = File.ReadAllText($"../../../regress/expect/ce.out").Replace("\r", "");
            Assert.AreEqual(alloutput, expected);
        }

        [TestMethod]
        public void TestStat()
        {
            var hist = Historgram.ConstructFromMinMax(0, 10, 1000);
            Assert.AreEqual(0, hist.buckets_[0]);
            Assert.AreEqual(10, hist.buckets_[10]);
            Assert.AreEqual(11, hist.nbuckets_);
            Assert.IsTrue(hist.depth_ > 90 && hist.depth_ < 91);
            hist = Historgram.ConstructFromMinMax(0, 1000, 10);
            Assert.AreEqual(Historgram.NBuckets_, hist.nbuckets_);
            Assert.IsTrue(hist.depth_ <= ((double)10 / Historgram.NBuckets_) + StatConst.epsilon_);
            hist = Historgram.ConstructFromMinMax(100, 100, 1000);
            Assert.AreEqual(1, hist.nbuckets_);
            Assert.AreEqual(1000, hist.depth_);
            hist = Historgram.ConstructFromMinMax(new DateTime(2000, 1, 1), new DateTime(2000, 10, 1), 1000);
            Assert.AreEqual(Historgram.NBuckets_, hist.nbuckets_);
            hist = Historgram.ConstructFromMinMax(11.22, 110.22, 1000);
            Assert.AreEqual(Historgram.NBuckets_, hist.nbuckets_);
            // diff dividable by NBuckets_ for easier assertion without introduce epsilon
            hist = Historgram.ConstructFromMinMax((decimal)11.22, (decimal)110.22, 1000);
            Assert.AreEqual(Historgram.NBuckets_, hist.nbuckets_);
            Assert.AreEqual((decimal)11.22, hist.buckets_[0]);
            Assert.AreEqual((decimal)110.22, hist.buckets_[hist.nbuckets_ - 1]);

            // exceptional case
            hist = Historgram.ConstructFromMinMax("a", "bc", 1000);
            Assert.IsNull(hist);
        }
    }

    [TestClass]
    public class SyntaxError
    {
        [TestMethod]
        [ExpectedException(typeof(AntlrParserException))]
        public void TestParseError()
        {
            string sql = "this is not a SQL statements.";
            RawParser.ParseSqlStatements(sql);
        }

        [TestMethod]
        [ExpectedException(typeof(AntlrParserException))]
        public void TestOuterError()
        {
            string sql = "select * from a outer join b on(a1 <> b1) where 100 > null;";
            RawParser.ParseSqlStatements(sql);
        }

        [TestMethod]
        [ExpectedException(typeof(AntlrParserException))]
        public void TestInnerError()
        {
            string sql = "select * from a left inner join b on(a1 <> b1) where 100 > null;";
            RawParser.ParseSqlStatements(sql);
        }

        [TestMethod]
        [ExpectedException(typeof(AntlrParserException))]
        public void TestEmptyError()
        {
            string sql = "";
            RawParser.ParseSqlStatements(sql);
        }

        [TestMethod]
        [ExpectedException(typeof(AntlrParserException))]
        public void TestParenthesisError()
        {
            string sql = "select * from a inner join b on(a1 <> b1 where 100 > null;";
            RawParser.ParseSqlStatements(sql);
        }
    }
}

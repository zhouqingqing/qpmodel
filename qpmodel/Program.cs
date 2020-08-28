/*
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

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using IronPython.Hosting;
using System.Diagnostics;

using qpmodel.codegen;
using qpmodel.expr;
using qpmodel.logic;
using qpmodel.physic;
using qpmodel.test;
using qpmodel.sqlparser;
using qpmodel.optimizer;

// Visusal Studio tip: 
//   when autocomplete box is shown, press crtl+alt+space to switch autocompletion mode
//
namespace qpmodel
{
    class Program
    {
        static void TestDataSet()
        {
            // register c#'s sqrt as an external function
            string sqroot(double d) => Math.Sqrt(d).ToString("#.###");

            SQLContext sqlContext = new SQLContext();
            SQLContext.Register<double, string>("sqroot", sqroot);
            var a = sqlContext.Read("a");
            var b = sqlContext.Read("b");

            a.filter("a1>1").join(b, "b2=a2").select("a1", "sqroot(b1*a1+2)").show();
            string s = a.physicPlan_.Explain();
            Console.WriteLine(s);

            var sql = "SELECT a1, sqroot(b1*a1+2) from a join b on b2=a2 where a1>1";
            var rows = SQLStatement.ExecSQL(sql, out string plan, out _);
        }
        static void TestDataSet2()
        {
            Random rand = Catalog.rand_;
            int inside(int d)
            {
                var x = rand.NextDouble();
                var y = rand.NextDouble();
                var ret = x * x + y * y <= 1 ? 1 : 0;
                return ret;
            }

            SQLContext sqlContext = new SQLContext();
            SQLContext.Register<int, int>("inside", inside);
            var sql = "SELECT 4.0*sum(inside(a1.a1))/count(*) from a a1, a a2, a a3, a a4, a a5, a a6, a a7, a a8, a a9, a a10";
            var rows = SQLStatement.ExecSQL(sql, out string plan, out _);
        }
        static void TestJobench()
        {
            var files = Directory.GetFiles(@"../../../../jobench");

            JOBench.CreateTables();

            // make sure all queries can generate phase one opt plan
            QueryOption option = new QueryOption();
            option.optimize_.TurnOnAllOptimizations();
            option.optimize_.memo_use_joinorder_solver_ = true;
            foreach (var v in files)
            {
                var sql = File.ReadAllText(v);
                var result = SQLStatement.ExecSQL(sql, out string phyplan, out _, option);
                Debug.Assert(result != null);
                Debug.Assert(phyplan != null);
                Console.WriteLine(v);
                Console.WriteLine(phyplan);
            }
        }
        static void TestTpcds_LoadData()
        {
            var files = Directory.GetFiles(@"../../../../tpcds", "*.sql");
            string[] norun = { "q1", "q10" };

            Tpcds.CreateTables();
            Tpcds.LoadTables("tiny");
            Tpcds.AnalyzeTables();

            // make sure all queries can generate phase one opt plan
            QueryOption option = new QueryOption();
            option.optimize_.enable_subquery_unnest_ = true;
            option.optimize_.remove_from_ = false;
            option.optimize_.use_memo_ = false;
            foreach (var v in files)
            {
                char[] splits = { '.', '/', '\\' };
                var tokens = v.Split(splits, StringSplitOptions.RemoveEmptyEntries);

                if (norun.Contains(tokens[1]))
                    continue;

                var sql = File.ReadAllText(v);
                var result = SQLStatement.ExecSQL(sql, out string phyplan, out _, option);
            }
        }

        static void RunSQLFromFile(string filename)
        {
            // Not working as expected, need to investigate.
            var option = new QueryOption();
            option.explain_.mode_ = ExplainMode.full;
            option.optimize_.use_memo_ = true;
            option.explain_.show_estCost_ = false;

            string allquery = File.ReadAllText(filename);
            string[] listquery = allquery.Split(';');

            List<string> listoutput = new List<string>();
            int linenum = 0;
            for (int i = 0; i < listquery.Length; ++i)
            {
                linenum = i + 1;
                string sql = listquery[i].Trim();
                if (sql.Length <= 0)
                    continue;
                else if (sql.StartsWith("--"))
                    continue;
                try
                {
                    Console.WriteLine(sql);

                    string outline = linenum.ToString();
                    outline += ": " + sql + "\n";
                    listoutput.Add(outline);
                    var result = SQLStatement.ExecSQL(sql, out string physicplan, out string error_, option);
                    if (physicplan != null)
                    {
                        Console.WriteLine(physicplan);
                        listoutput.Add(physicplan);
                    }

                    if (result != null)
                    {
                        Console.WriteLine(result);
                        listoutput.Add(result.ToString());
                    }

                } catch (Exception e)
                {
                    Console.WriteLine("SQL: " + sql + "\nEXCEPTION: " + e + "\n");
                    continue;
                }
            }
            string alloutput = string.Join('\n', listoutput);
            string outfile = filename + ".out";
            File.WriteAllText(outfile, alloutput);
        }

        static void Main(string[] args)
        {
            Catalog.Init();

            string sql = "";

            if (args.Length != 0)
            {
                sql = args[0];
            }

#pragma warning disable CS0162 // Unreachable code detected
            // The warnings are annoying, so using a pragma to suppress them.
            if (false)
            {
                JOBench.CreateTables();
                var stats_fn = "../../../../jobench/statistics/jobench_stats";
                Catalog.sysstat_.read_serialized_stats(stats_fn);
                sql = File.ReadAllText("../../../../jobench/10a.sql");
                goto doit;
            }

            if (false)
            {
                Tpch.CreateTables();
                Tpch.LoadTables("0001");
                //Tpch.CreateIndexes();
                Tpch.AnalyzeTables();
                sql = File.ReadAllText("../../../../tpch/q20.sql");
                goto doit;
            }

            if (false)
            {
                //84
                sql = File.ReadAllText("../../../../tpcds/q33.sql");
                Tpcds.CreateTables();
                Tpcds.LoadTables("tiny");
                Tpcds.AnalyzeTables();
                // long time: 4 bad plan
                // 6: distinct not supported, causing wrong result
                // q23, q33, q56, q60: in-subquery plan bug
                // 10,11,13, 31, 38, 41, 48, 54, 66, 72, 74: too slow
                goto doit;
            }
#pragma warning restore CS0162 // Unreachable code detected

        doit:
            bool convMode = false;

            // Application Arguments in Project properties seem to be
            // effective only after rebuilding.
            // This is a last ditch effort to be able to debug arbitrary
            // statements without rebuilding the solution.
            string inputFile = "";
            if (sql.Length == 2 && sql == "-i")
                convMode = true;
            else if (sql.Length == 2 && sql.StartsWith("-f"))
                inputFile = args[1];
            else if (sql.Length == 0)
                sql = "select * from a tablesample row (2);";

            do
            {
                if (convMode == true || (sql.Length == 1 && sql.Equals("-")))
                {
                    System.Console.Write("QSQL> ");
                    sql = System.Console.ReadLine();
                    System.Console.WriteLine(sql);
                }

                var datetime = new DateTime();
                datetime = DateTime.Now;

                var stopWatch = new Stopwatch();
                stopWatch.Start();

                if (inputFile.Length != 0)
                {
                    // read the file and execute all statements in it.
                    RunSQLFromFile(inputFile);
                    goto done;
                }

                // query options might be conflicting or incomplete
                Console.WriteLine(sql);
                var a = RawParser.ParseSingleSqlStatement(sql);
                ExplainOption.show_tablename_ = false;
                a.queryOpt_.profile_.enabled_ = true;
                a.queryOpt_.optimize_.enable_subquery_unnest_ = true;
                a.queryOpt_.optimize_.remove_from_ = false;
                a.queryOpt_.optimize_.use_memo_ = true;
                a.queryOpt_.optimize_.enable_cte_plan_ = true;
                a.queryOpt_.optimize_.use_codegen_ = false;
                a.queryOpt_.optimize_.memo_disable_crossjoin_ = false;
                a.queryOpt_.optimize_.memo_use_joinorder_solver_ = false;
                a.queryOpt_.explain_.show_output_ = true;
                a.queryOpt_.explain_.show_id_ = true;
                a.queryOpt_.explain_.show_estCost_ = a.queryOpt_.optimize_.use_memo_;
                a.queryOpt_.explain_.mode_ = ExplainMode.full;

                // -- Semantic analysis:
                //  - bind the query
                a.queryOpt_.optimize_.ValidateOptions();

                if (!(a is SelectStmt))
                {
                    SQLStatement.ExecSQLList(sql);
                    goto done;
                }

                a.Bind(null);

                // -- generate an initial plan
                var rawplan = a.CreatePlan();
                Console.WriteLine("***************** raw plan *************");
                Console.WriteLine(rawplan.Explain());

                // -- optimize the plan
                PhysicNode phyplan = null;
                if (a.queryOpt_.optimize_.use_memo_)
                {
                    Console.WriteLine("***************** optimized plan *************");
                    var optplan = a.SubstitutionOptimize();
                    Console.WriteLine(optplan.Explain(a.queryOpt_.explain_));
                    a.optimizer_ = new Optimizer(a);
                    a.optimizer_.ExploreRootPlan(a);
                    phyplan = a.optimizer_.CopyOutOptimalPlan();
                    Console.WriteLine(a.optimizer_.PrintMemo());
                    Console.WriteLine("***************** Memo plan *************");
                    Console.WriteLine(phyplan.Explain(a.queryOpt_.explain_));
                }
                else
                {
                    // -- optimize the plan
                    Console.WriteLine("-- optimized plan --");
                    var optplan = a.SubstitutionOptimize();
                    Console.WriteLine(optplan.Explain(a.queryOpt_.explain_));

                    // -- physical plan
                    Console.WriteLine("-- physical plan --");
                    phyplan = a.physicPlan_;
                    Console.WriteLine(phyplan.Explain(a.queryOpt_.explain_));
                }

                // -- output profile and query result
                Console.WriteLine("-- profiling plan --");
                var final = new PhysicCollect(phyplan);
                a.physicPlan_ = final;
                ExecContext context = a.CreateExecContext();

                final.ValidateThis();
                if (a is SelectStmt select)
                    select.OpenSubQueries(context);

                final.Open(context);
                final.Exec(null);
                final.Close();

                if (a.queryOpt_.optimize_.use_codegen_)
                {
                    CodeWriter.WriteLine(context.code_);
                    Compiler.Run(Compiler.Compile(), a, context);
                }
                Console.WriteLine(phyplan.Explain(a.queryOpt_.explain_));
            done:
                stopWatch.Stop();
                Console.WriteLine("RunTime: " + stopWatch.Elapsed);
            } while (convMode == true);

            Console.ReadKey();
        }
    }
}

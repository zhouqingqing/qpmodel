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
            Random rand = new Random();
            int inside(int d) {
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
            var files = Directory.GetFiles(@"../../../jobench");

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
            var files = Directory.GetFiles(@"../../../tpcds", "*.sql");
            string[] norun = {"q1", "q10"};

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

        static void Main(string[] args)
        {
            Catalog.Init();

            string sql = "";

            if (false)
            {
                JOBench.CreateTables();
                sql = File.ReadAllText("../../../jobench/10a.sql");
                goto doit;
            }

            if (false)
            {
                Tpch.CreateTables();
                Tpch.LoadTables("0001");
                //Tpch.CreateIndexes();
                Tpch.AnalyzeTables();
                sql = File.ReadAllText("../../../tpch/q20.sql");
                goto doit;
            }

            if (false)
            { 
                Tpcds.CreateTables();
                Tpcds.LoadTables("tiny");
                Tpcds.AnalyzeTables();
                // 1, 2,3,7,10,
                // long time: 4 bad plan
                // 6: distinct not supported, causing wrong result
                sql = File.ReadAllText("../../../tpcds/q7.sql");
                goto doit;
            }

        doit:
            sql = "select a2,b2,c2,d2 from ad, bd, cd, dd where a2=b2 and c2 = b2 and c2=d2 order by a2";
            sql = "select count(*) from ast group by tumble(a0, interval '10' second)";
            sql = "select round(a1, 10), count(*) from a group by round(a1, 10)";
            sql = "select count(*) from a group by round(a1, 10)";
            sql = "select count(*) from ast group by hop(a0, interval '5' second, interval '10' second)";
            sql = "select round(a1, 10) from a group by a1;";
            sql = "select abs(-a1*2), count(*) from a group by round(a1, 10);";
            sql = "select abs(-a1*2), count(*) from a group by a1;";
            sql = "select tumble_start(a0, interval '10' second), tumble_end(a0, interval '10' second), count(*) from ast group by tumble(a0, interval '10' second)";

            var datetime = new DateTime();
            datetime = DateTime.Now;

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            // query options might be conflicting or incomplete
            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            ExplainOption.show_tablename_ = false;
            a.queryOpt_.profile_.enabled_ = true;
            a.queryOpt_.optimize_.enable_subquery_unnest_ = false;
            a.queryOpt_.optimize_.remove_from_ = false;
            a.queryOpt_.optimize_.use_memo_ = true;
            a.queryOpt_.optimize_.enable_cte_plan_ = false;
            a.queryOpt_.optimize_.use_codegen_ = false;
            a.queryOpt_.optimize_.memo_disable_crossjoin_ = false;
            a.queryOpt_.optimize_.memo_use_joinorder_solver_ = false;
            a.queryOpt_.explain_.show_output_ = true;
            a.queryOpt_.explain_.show_id_ = false;
            a.queryOpt_.explain_.mode_ = a.queryOpt_.optimize_.use_memo_ ? ExplainMode.analyze : ExplainMode.plain;

            // -- Semantic analysis:
            //  - bind the query
            a.queryOpt_.optimize_.ValidateOptions();
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
                a.optimizer_.InitRootPlan(a);
                a.optimizer_.OptimizeRootPlan(a, null);
                Console.WriteLine(a.optimizer_.PrintMemo());
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
            var code = final.Open(context);
            code += final.Exec(null);
            code += final.Close();

            if (a.queryOpt_.optimize_.use_codegen_)
            {
                CodeWriter.WriteLine(code);
                Compiler.Run(Compiler.Compile(), a, context);
            }
            Console.WriteLine(phyplan.Explain(a.queryOpt_.explain_));

            stopWatch.Stop();
            Console.WriteLine("RunTime: " + stopWatch.Elapsed); 
            Console.ReadKey();
        }
    }
}

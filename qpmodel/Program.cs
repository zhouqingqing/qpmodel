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
        static void TestDataFrame()
        {
            SQLContext sqlContext = new SQLContext();

            var a = sqlContext.Read("a");
            var b = sqlContext.Read("b");

            a.filter("a1>1").join(b, "b2=a2").select("a1","b1*a1+5").show();
            string s = a.physicPlan_.Explain();
            Console.WriteLine(s);
        }

        static void doPython()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            var libs  = new [] { @"D:\qpmodel\packages\IronPython.2.7.9\lib\net45" };
            engine.SetSearchPaths(libs);
            var ret = engine.ExecuteFile(@"z:/source/naru/train_model.py", scope);
            Console.WriteLine(ret);
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

        static void Main(string[] args)
        {
            Catalog.Init();

            string sql = "";
            //TestDataFrame();
            //TestJobench();
            //return;

            if (false)
            {
                JOBench.CreateTables();
                sql = File.ReadAllText("../../../jobench/29a.sql");
                goto doit;
            }

            if (true)
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
                sql = File.ReadAllText("../../../tpcds/problem_queries/q64.sql");
                sql = File.ReadAllText("../../../tpcds/q1.sql");
                goto doit;
            }

        doit:
            sql = "with cte as (select * from a) select * from cte cte1, cte cte2 where cte1.a2=cte2.a3 and cte1.a1> 0;";

            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            a.queryOpt_.profile_.enabled_ = true;
            a.queryOpt_.optimize_.enable_subquery_unnest_ = true;
            a.queryOpt_.optimize_.remove_from_ = true;
            a.queryOpt_.optimize_.use_memo_ = true;
            a.queryOpt_.optimize_.use_codegen_ = false;

            //a.queryOpt_.optimize_.memo_disable_crossjoin = false;
            //a.queryOpt_.optimize_.use_joinorder_solver = true;

            // -- Semantic analysis:
            //  - bind the query
            a.queryOpt_.optimize_.ValidateOptions();
            a.Bind(null);

            // -- generate an initial plan
            ExplainOption.show_tablename_ = false;
            a.queryOpt_.explain_.show_output_ = true;
            a.queryOpt_.explain_.show_cost_ =  a.queryOpt_.optimize_.use_memo_;
            var rawplan = a.CreatePlan();
            Console.WriteLine("***************** raw plan *************");
            Console.WriteLine(rawplan.Explain(0));

            physic.PhysicNode phyplan = null;
            if (a.queryOpt_.optimize_.use_memo_)
            {
                Console.WriteLine("***************** optimized plan *************");
                var optplan = a.SubstitutionOptimize();
                Console.WriteLine(optplan.Explain(0, a.queryOpt_.explain_));
                Optimizer.InitRootPlan(a);
                Optimizer.OptimizeRootPlan(a, null);
                Console.WriteLine(Optimizer.PrintMemo());
                phyplan = Optimizer.CopyOutOptimalPlan();
                Console.WriteLine(Optimizer.PrintMemo());
                Console.WriteLine("***************** Memo plan *************");
                Console.WriteLine(phyplan.Explain(0, a.queryOpt_.explain_));
            }
            else
            {
                // -- optimize the plan
                Console.WriteLine("-- optimized plan --");
                var optplan = a.SubstitutionOptimize();
                Console.WriteLine(optplan.Explain(0, a.queryOpt_.explain_));

                // -- physical plan
                Console.WriteLine("-- physical plan --");
                phyplan = a.physicPlan_;
                Console.WriteLine(phyplan.Explain(0, a.queryOpt_.explain_));
            }

            Console.WriteLine("-- profiling plan --");
            var final = new PhysicCollect(phyplan);
            a.physicPlan_ = final;
            var context = new ExecContext(a.queryOpt_);

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

            Console.WriteLine(phyplan.Explain(0, a.queryOpt_.explain_));
        }
    }
}

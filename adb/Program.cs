using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using IronPython.Hosting;
using System.Diagnostics;

using adb.codegen;
using adb.expr;
using adb.logic;
using adb.physic;
using adb.test;
using adb.sqlparser;
using adb.optimizer;

// Visusal Studio tip: 
//   when autocomplete box is shown, press crtl+alt+space to switch autocompletion mode
//
namespace adb
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
            var libs  = new [] { @"D:\adb\packages\IronPython.2.7.9\lib\net45" };
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
            option.optimize_.memo_use_joinorder_solver = true;
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

            if (false)
            {
                Tpch.CreateTables();
                Tpch.LoadTables("0001");
                //Tpch.CreateIndexes();
                Tpch.AnalyzeTables();
                sql = File.ReadAllText("../../../tpch/q22.sql");
                goto doit;
            }

            if (false)
            { 
                Tpcds.CreateTables();
                sql = File.ReadAllText("../../../tpcds/problem_queries/q64.sql");
                sql = File.ReadAllText("../../../tpcds/q72.sql");
                goto doit;
            }

        doit:
            //  sql = "select * from nation, region where n_regionkey = r_regionkey";
            sql = "select * from a where a1*2 = (select max(b3) from b where b2=a2);";
            sql = "select a1, (select b3 from b where b2=a2) from a;";
            sql = "select a1, 5+(select b2 from b where b1=a1) from a group by 1;";
            sql = "select a1, (select b2 from b where b1=a1) from a group by 1;";
            sql = "select a1, a3  from a where a.a1 = (select max(b1) from b where b2 = a2)";
            sql = "select a1,a1,a3,a3, (select b3 from b where b2=2) from a where a1>1";
            sql = "select a1,a1,a3,a3, (select b3 from b where a1=b2 and b2=3) from a where a1>1";
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) and a2>1;";
            sql = @" select a1+a2+a3  from a where a.a1 = (select b1 from b bo where b4 = a4 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            sql = "select a1, 5+(select b2 from b where b1=a1) from a group by 1;";
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3) or a2 > 2;";
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<4);";
            sql = @"select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 
                    and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<3);";
            sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
            sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
                     and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
            sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
                     and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
            sql = @" select a1+a2+a3  from a where a.a1 = (select b1 from b bo where b4 = a4 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
            and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            sql = @"select a1  from a where a.a1 = (select c1 from c where c2 = a2 and c1 = (select b1 from b where b2 > c2 and b3=a3));"; // WRONG RESULT
            sql = @"select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) 
                    and b.b1 > (select c2 / 2 from c where c.c3 = b3);";
            sql = @"select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 
                        and bo.b3 = a3 and b3> 1) and b2<3);";
            sql = @"select a1  from a where a.a1 = (select c1 from c where c2 = a2 and c1 = (select b1 from b where b3=a3));";

            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            a.queryOpt_.profile_.enabled_ = true;
            a.queryOpt_.optimize_.enable_subquery_to_markjoin_ = true;
            a.queryOpt_.optimize_.remove_from = false;
            a.queryOpt_.optimize_.use_memo_ = true;
            //a.queryOpt_.optimize_.use_codegen_ = false;

            //a.queryOpt_.optimize_.memo_disable_crossjoin = false;
            //a.queryOpt_.optimize_.use_joinorder_solver = true;

            // -- Semantic analysis:
            //  - bind the query
            a.queryOpt_.optimize_.ValidateOptions();
            a.Bind(null);

            // -- generate an initial plan
            ExplainOption.show_tablename_ = true;
            a.explain_.show_output_ = true;
            a.explain_.show_cost_ =  a.queryOpt_.optimize_.use_memo_;
            var rawplan = a.CreatePlan();
            Console.WriteLine("***************** raw plan *************");
            Console.WriteLine(rawplan.Explain(0));

            physic.PhysicNode phyplan = null;
            if (a.queryOpt_.optimize_.use_memo_)
            {
                Console.WriteLine("***************** optimized plan *************");
                var optplan = a.PhaseOneOptimize();
                Console.WriteLine(optplan.Explain(0, a.explain_));
                Optimizer.InitRootPlan(a);
                Optimizer.OptimizeRootPlan(a, null);
                Console.WriteLine(Optimizer.PrintMemo());
                phyplan = Optimizer.CopyOutOptimalPlan();
                Console.WriteLine(Optimizer.PrintMemo());
                Console.WriteLine("***************** Memo plan *************");
                Console.WriteLine(phyplan.Explain(0, a.explain_));
            }
            else
            {
                // -- optimize the plan
                Console.WriteLine("-- optimized plan --");
                var optplan = a.PhaseOneOptimize();
                Console.WriteLine(optplan.Explain(0, a.explain_));

                // -- physical plan
                Console.WriteLine("-- physical plan --");
                phyplan = a.physicPlan_;
                Console.WriteLine(phyplan.Explain(0, a.explain_));
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

            Console.WriteLine(phyplan.Explain(0, a.explain_));
        }
    }
}

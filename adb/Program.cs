using System;
using System.IO;
using IronPython.Hosting;

using adb.codegen;
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
        private static void doPython()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            var libs  = new [] { @"D:\adb\packages\IronPython.2.7.9\lib\net45" };
            engine.SetSearchPaths(libs);
            var ret = engine.ExecuteFile(@"z:/source/naru/train_model.py", scope);
            Console.WriteLine(ret);
        }

        static void Main(string[] args)
        {
            // doPython();
            Catalog.Init();

            string sql = "";

            if (false)
            {
                JOBench.CreateTables();
                sql = File.ReadAllText("../../../jobench/1a.sql");
                goto doit;
            }

            if (false)
            {
                Tpch.CreateTables();
                Tpch.LoadTables("001");
                Tpch.CreateIndexes();
                Tpch.AnalyzeTables();
                sql = File.ReadAllText("../../../tpch/q22.sql");
                goto doit;
            }

            if (true)
            { 
                Tpcds.CreateTables();
                sql = File.ReadAllText("../../../tpcds/problem_queries/q64.sql");
                sql = File.ReadAllText("../../../tpcds/q72.sql");
                goto doit;
            }

            /*OptimizeOption option = new OptimizeOption();
            option.remove_from = true;
            sql = "select a1 from(select b1 as a1 from b) c;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select b1 from (select count(*) as b1 from b) a;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select c100 from (select c1 c100 from c) c where c100>1";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select * from (select a1*a2 a12, a1 a2 from a) b(a12);";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select * from (select a1*a2 a12, a1 a3 from a) b;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select *, cd.* from (select a.* from a join b on a1=b1) ab , (select c1 , c3 from c join d on c1=d1) cd where ab.a1=cd.c1";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select * from (select * from a join b on a1=b1) ab , (select * from c join d on c1=d1) cd where ab.a1=cd.c1";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select a12*a12 from (select a1*a2 a12, a1 a3 from a) b;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select a2, count(*), sum(a2) from (select a2 from a) b where a2*a2> 1 group by a2;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select b1+b1, b2+b2, c100 from (select b1, count(*) as b2 from b) a, (select c1 c100 from c) c where c100>1;";
            SQLStatement.ExecSQL(sql, out _, out _, option);

            option.remove_from = false;
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select sum(e1) from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) d(e1);";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select d1, sum(d2) from (select c1/2, sum(c1) from (select b1, count(*) as a1 from b group by b1)c(c1, c2) group by c1/2) d(d1, d2) group by d1;";
            SQLStatement.ExecSQL(sql, out _, out _, option);
            sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            sql = "select a2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2;";
            sql = "select a2, count(*) from (select a2 from a) b group by a2;";
          */

            sql = "select count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b;";
            sql = "select count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)) b;";
            sql = "select b1+b1, b2+b2+b1, c100 from (select b1, count(*) as b2 from b) a, (select c1 c100 from c) c where c100>1;";
            sql = "select ca2 from (select sum(a1) as ca2 from a group by a2) b;";
            sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b ;";
            sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b group by ca2;";
            sql = "select a1 from (select a1 from a where a2 > (select max(b1) from b)) c;";
            sql = "select a1, count(a1) from a where exists (select *  from b where b1=a1) group by a1;";
            sql = "select a1 from a where a2 > (select max(b1) from b);";
            sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));";
            sql = "select * from a, (select * from b) c";
            sql = "select b.a1 + b.a2 from (select a1 from a) b";
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where b1>1 and c100>1;"; // ANSWER WRONG

      //  doit:
            // sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
            //          and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
            // sql = " select a1, sum(a12) from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a1) group by a1;";
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));";
            sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2;";
            sql = "select * from a where a1> (select sum(b2) from b where a1=b1);";
            sql = "select * from a where a1> (select b2 from b where a1<>b1);";
            sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2;";

        doit:
            //sql = "select * from d where 3<d1;";

            //sql = "select a2*2, count(a1) from a, b, c where a1=b1 and a2=c2 group by a2 limit 2;";
            //sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2;";
            //sql = "select a1.*, a2.a1,a2.a2 from (select * from a) a1, (select * from a) a2;";
            //sql = "select * from a, b, c where a1=b1 and a2=c2 and b3=c3;";
            sql = "select a2*2, count(a1) from a, b, c where a1>b1 and a2>c2 group by a2;";
            sql = "select a2*a1, repeat('a', a2) from a where a1>= (select b1 from b where a2=b2);";
            sql = "select a2*2 from a, b where a1=b1;";

            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            a.queryOpt_.profile_.enabled_ = true;
            a.queryOpt_.optimize_.enable_subquery_to_markjoin_ = false;
            a.queryOpt_.optimize_.remove_from = true;
            a.queryOpt_.optimize_.use_memo_ = true;
            a.queryOpt_.optimize_.use_codegen_ = true;

            a.queryOpt_.optimize_.enable_nljoin_ = true;

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            ExplainOption.show_tablename_ = false;
            a.explain_.show_output_ = false;
            a.explain_.show_cost_ =  a.queryOpt_.optimize_.use_memo_;
            var rawplan = a.CreatePlan();
            Console.WriteLine(rawplan.Explain(0));

            PhysicNode phyplan = null;
            if (a.queryOpt_.optimize_.use_memo_)
            {
                Console.WriteLine("***************** optimized plan *************");
                var optplan = a.PhaseOneOptimize();
                Optimizer.InitRootPlan(a);
                Optimizer.OptimizeRootPlan(a, null);
                Console.WriteLine(Optimizer.PrintMemo());
                phyplan = Optimizer.CopyOutOptimalPlan();
                Console.WriteLine("***************** Memo plan *************");
                Console.WriteLine(phyplan.Explain(0, a.explain_));
                Optimizer.PrintMemo();
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

            final.Validate();
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

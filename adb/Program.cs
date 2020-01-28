using System;
using System.IO;

using adb.codegen;
using adb.logic;
using adb.physic;
using adb.test;
using adb.sqlparser;
using adb.optimizer;

namespace adb
{
    class Program
    {
        static void Main(string[] args)
        {
            string sql = "";

            JOBench.CreateTables();
            sql = File.ReadAllText("../../../jobench/1a.sql");
            goto doit;

            if (false)
            {
                Tpch.CreateTables();
                Tpch.LoadTables("0001");
                Tpch.CreateIndexes();
                Tpch.AnalyzeTables();
                sql = File.ReadAllText("../../../tpch/q03.sql");
                goto doit;
            }
            else 
            { 
                Tpcds.CreateTables();
                sql = File.ReadAllText("../../../tpcds/problem_queries/q64.sql");
                sql = File.ReadAllText("../../../tpcds/q1.sql");
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
            sql = "select b1 from b where  b.b2 > (select c2 / 2 from c where c.c2 = b2) and b.b1 > (select c2 / 2 from c where c.c2 = b2);";
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));";
            sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            //sql = "select * from a where a2 > (select max(b1) from b where a2=b2);";
            //sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1";
            sql = "select * from b join a on a1=b1 where a1 < (select a2 from a where a2=b2);";
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);"; // FIXME-remove_from
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1+d1"; // FIXME-remove_from
            sql = "select * from (select * from a join b on a1=b1) ab join (select * from c join d on c1=d1) cd on a1+b1=c1 and a2+b2=d2;"; // FIXME-remove_from
            sql = "select a1+b1 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);";
            sql = @"select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 
                    and b1 = (select b1 from b where b3 = a3 and b3>1) and b2<3);";
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            sql = "select a1 from a where a1 < (select max(b2) from b where b2=a2);";
            //sql = "select a1 from a where a1 < (select b2 from b where b2=a2);";
            // sql = "select a1 from a, (select b2, max(b3) maxb3 from b group by b2) b where a.a2 = b.b2 and a1 < maxb3";
            // sql = "select a1 from a, (select b2, max(b3) maxb3 from b group by b2) b where a.a2 = b.b2";
            // sql = "select * from (select max(b3) maxb3 from b) b where maxb3>1";    // WRONG!
            sql = "select a1 from a, (select max(b3) maxb3 from b) b where a1 < maxb3"; // WRONG!
            sql = "select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;"; // WRONG
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b1,b2,c100 from (select count(*) as b1, sum(b1) b2 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b1+b2,c100 from (select count(*) as b1, sum(b1) b2 from b) a, (select c1 c100 from c) c where c100>1;"; // OK
            sql = "select b4*b1+b2*b3 from (select 1 as b4, b3, count(*) as b1, sum(b1) b2 from b group by b3) a;"; // OK
            sql = "select b1,c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where b1>1 and c100>1;"; // ANSWER WRONG

      //  doit:
            // sql = @"select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1)
            //          and a2>1 and not exists (select * from a b where b.a2+7=a.a1+b.a1);";
            // sql = " select a1, sum(a12) from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a1) group by a1;";
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));";
            sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2;";
            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) and a1>2;";
            sql = "select a2 from a where a1 in (select a2 from a a1 where exists (select * from a b where b.a3>=a1.a1+b.a1+1));"; //2,3
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));"; //2,3
            sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>a1+b.a1+1));"; //2,3, ok
            sql = "select a2 from a where a1 in (select a2 from a a1 where exists (select * from a b where b.a3>=a.a1+b.a1+1));"; // 2
            sql = "select a2 from a where a1 in (select a2 from a where exists(select * from a b where b.a3 >= a.a1 + b.a1 + 1));";
            sql = "select a1,a2,b2 from b, a where a1=b1 and a1 < (select a2 from a where a2=b2);";
            sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));";
            sql = "select * from a where a1> (select sum(b2) from b where a1=b1);";
            sql = "select * from a where a1> (select b2 from b where a1<>b1);";

        doit:
            //sql = "select count(*) from lineitem, orders, customer where l_orderkey=o_orderkey and c_custkey = o_custkey;";
            //sql = "select * from a union all select * from b;";
            //sql = "select 1+2*3, 1+2.1+a1 from a where a1+2+(1*5+1)>2*4.6 and 1+2<2+1.4;";
            //sql = "select 1+2+3 from d where 1=d1 and 2<d1";
            //sql = "select * from d where 3<d1;";
            //sql = "select * from a, b, c where a1>b1 and a2>c2;";
            sql = "select a2*2 from a, b, c where a1<=b2 and a2<=c3 limit 10;";

            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            a.queryOpt_.profile_.enabled_ = true;
            a.queryOpt_.optimize_.enable_subquery_to_markjoin_ = true;
            a.queryOpt_.optimize_.remove_from = true;
            a.queryOpt_.optimize_.use_memo_ = false;
            a.queryOpt_.optimize_.use_codegen_ = true;

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            ExplainOption.show_tablename_ = false;
            a.explain_.show_output_ = true;
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

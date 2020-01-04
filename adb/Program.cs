using System;
using System.IO;

// profiling with callback mode - not sure if callback is good for profiling
// output expression by push down from top: a node's output including two parts: 
// 1) everything it needs (say filter)
// 2) everything its parent node need
// A top node's output is defined by query's selection list. So we can decide the 
// output list by push down the request from top. Meanwhile, scalar subquery is 
// handled separately.
//

namespace adb
{
    class Program
    {
        static void Main(string[] args)
        {
            string sql = "";

            sql = "select a1,a1,a3,a3 from a where a2> (select b1 from b where b1=a1 and b2=3);"; // lost a2>@1
            // bad sql = "select a1,a1,a3,a3 from a where a2> (select b1 from b where b1=a1) or a2<=2;"; 
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            sql = "select a1,a1,a3,a3 from a where a2> (select b1 from (select * from b) d,c where b1=c1 and b1=a1 and b2=3);"; // lost a2>@1
            //Tpch.LoadTables("0001");
            //Tpch.AnalyzeTables();

            {
                var files = Directory.GetFiles(@"../../../tpch");

                var v = files[18];
                {
                    sql = File.ReadAllText(v);
                    //goto doit;
                }
            }
            //sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2<c.c3;";
            sql = "select * from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);";
            //sql = "select * from a join c on a1=c1 where a1 < (select b2 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3) x";
            sql = "select a.a1,b.a1,c.a1, a.a1+b.a1+c.a1 from a, a b, a c where a.a1=5-b.a1-c.a1;";
            //sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3 and c.c1 = a.a1";
            //sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            //sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            //sql = "analyze a;";
            sql = "select b2 from (select a3, a4 from a) b(b2);";
            sql = "select sum(a12) from (select a1*a2 a12 from a) b;";
            sql = "select d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1);";
            sql = "select e1 from (select d1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1)) d(e1);";
            sql = "select e1 from (select * from (select sum(a12) from (select a1*a2 a12 from a) b) c(d1)) d(e1);";
            sql = "select e1 from(select d1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            sql = "select e1 from (select e1 from (select sum(a12) from (select a1*a2 a12 from a) b) c(e1)) d;";
            sql = "select e1 from (select d1 from (select sum(a12) from (select a1, a2, a1*a2 a12 from a) b) c(d1)) d(e1);";
            sql = "select a1, sum(a12) as a2 from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a12) group by a1;";
            sql = "select b2 from (select a3, a4 from a) b(b2);";
            sql = "select * from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a12) ;";

            // good
            sql = "select * from (select a1*a2 a12, a1 from a) b;";
            sql = "select a12, a1 from (select a1*a2 a12, a1 from a) b;";
            sql = "select a12,a1 from (select a12, a1 from (select a1*a2 a12, a1 from a) b)c;";
            sql = "select * from (select a12, a1 from (select a1*a2 a12, a1 from a) b)c;";
            sql = "select * from (select a12 from (select a1*a2 a12, a1 from a) b)c;";
            sql = "select * from (select e1 from (select a12 from (select a1*a2 a12 from a) b) c(e1)) d;";
            sql = "select * from (select a1, a1*a2 a12 from a) b where a1 >= (select c1 from c where c1=a12);";
            //sql = "select e1 from(select d1 as e1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            //sql = "select c3 from (select sum(a2) from (select a1*a2 a12, a1 as a2 from a) b(a2, a3)) c(c3);";
            // sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c ) a;";
            //sql = @"with cte1 as (select* from a) select * from cte1 where a1>1;";
            // sql = "select b.a1 , a2 from (select a1,a2 from a, c) b";
            sql = "select e1 from(select d1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            sql = "select b1+b1 from (select a1*2 b1 from a) b where b1 > 2;";
            //sql = "select c2 from (select b1+b1 from (select a1*2 from a) b(b1)) c(c2);";
            //sql = "select count(*)+1 from (select b1+c1 from (select b1 from b) a, (select c1,c2 from c) c(c1,c3) where c3>1) a;";
            sql = "select e1 from(select d1 as e1 from (select sum(ab12) from (select a1* b2 ab12 from a join b on a1= b1) b) c(d1)) d(e1);";
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";

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
            //sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) or a2>2) b group by a2/2;";
            //sql = "select a2/2, count(*) from (select a2 from a where exists (select * from a b where b.a3>=b.a1+1) or a2>2) b group by a2/2;";
            //sql = "select b1+b1, b2+b2, c100 from (select b1, count(*) as b2 from b group by b1) a, (select c1 c100 from c) c where c100>1;";
            //sql = "select count(ca2) from (select count(a2) from a group by a1) b(ca2);";
            //sql = "select ca2, from (select a1, count(a2) as ca2 from a group by a1) b;";
            sql = "select ca2 from (select sum(a1) as ca2 from a group by a2) b;";
            sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b ;";
            sql = "select ca2 from (select count(a2) as ca2 from a group by a1) b group by ca2;";
            sql = "select * from a;";
            sql = "select * from d where d1=2;";

        doit:
            Console.WriteLine(sql);
            var a = RawParser.ParseSingleSqlStatement(sql);
            a.profileOpt_.enabled_ = true;
            a.optimizeOpt_.enable_subquery_to_markjoin_ = true;
            a.optimizeOpt_.remove_from = true;
            a.optimizeOpt_.use_memo_ = false;

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            ExplainOption.costoff_ = false;
            ExplainOption.show_tablename_ = true;
            var rawplan = a.CreatePlan();
            Console.WriteLine(rawplan.PrintString(0));

            ExplainOption.costoff_ = !a.optimizeOpt_.use_memo_;
            PhysicNode phyplan = null;
            if (a.optimizeOpt_.use_memo_)
            {
                Console.WriteLine("***************** optimized plan *************");
                var optplan = a.PhaseOneOptimize();
                Optimizer.InitRootPlan(a);
                Optimizer.OptimizeRootPlan(a, null);
                Console.WriteLine(Optimizer.PrintMemo());
                phyplan = Optimizer.CopyOutOptimalPlan();
                Console.WriteLine("***************** Memo plan *************");
                Console.WriteLine(phyplan.PrintString(0));
                Optimizer.PrintMemo();
            }
            else
            {
                // -- optimize the plan
                Console.WriteLine("-- optimized plan --");
                var optplan = a.PhaseOneOptimize();
                Console.WriteLine(optplan.PrintString(0));

                // -- physical plan
                Console.WriteLine("-- physical plan --");
                phyplan = a.physicPlan_;
                Console.WriteLine(phyplan.PrintString(0));
            }

            Console.WriteLine("-- profiling plan --");
            var final = new PhysicCollect(phyplan);
            final.Open();
            final.Exec(new ExecContext(), null);
            final.Close();
            Console.WriteLine(phyplan.PrintString(0));
        }
    }
}

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

            sql = "select 1 from a where a.a1 = (select b1 from b where b.b2 = a2);";
            //sql = "select b1 from b where b.b2 = a.a2";
            // test candiates ---
            //sql = "select a1,a1,a3,a3 from a where a1+a2+a3>3";
            //sql = "select * from a, (select * from b where b2>2) c;";

            // test1: simple case
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
            //goto doit;
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<4);"; // not working, because a2 is actually b2
            // test2: 2+ variables
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b1 = a1 and b3<5);";
            // test3: deep vars
            sql = "select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b3<3);";
            sql = "select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3 = a3 and b3>1) and b3<4);";
            sql = @"select a4  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
                and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            sql = "select a1,a1,a3,a3 from a where a2> (select b1 from b where b1=a1 and b2=3);"; // lost a2>@1
            // bad sql = "select a1,a1,a3,a3 from a where a2> (select b1 from b where b1=a1) or a2<=2;"; 
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            sql = "select a1,a1,a3,a3 from a where a2> (select b1 from (select * from b) d,c where b1=c1 and b1=a1 and b2=3);"; // lost a2>@1
            //tpch.LoadTables("0001");

            {
                var files = Directory.GetFiles(@"../../../tpch");

                //foreach (var v in files)
                var v = files[16];
                {
                    //if (v.Contains("15"))
                    //    continue;
                    sql = File.ReadAllText(v);
            //        var stmt = RawParser.ParseSqlStatement(sql);
            //        Console.WriteLine(v);
            //        stmt.Bind(null);
            //        Console.WriteLine(stmt.CreatePlan().PrintString(0));
                }
            }
            //sql = "select a.a1, b1, a2, c2 from a join b on a.a1=b.b1 join c on a.a2<c.c3;";
            //sql = "select a1, a3  from a where a.a1 = (select b1,b2 from b)";
            //sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            //sql = "select a2 from a where a1 in (1,2,3);";
            //sql = "select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1) and a3>(select b2 from b where b2=a2);";
            //sql = "select a2 from a where a1 in (select a2 from a where exists (select * from a b where b.a3>=a.a1+b.a1+1));";
            //sql = @"with cte1 as (select a1,a2,a3 from a) select * from cte1 where a1>1;";
            // sql = @"with cte1 as (select* from a), cte2 as (select* from b), cte3 as (select* from cte1 join cte2 on a1 = b1) select* from cte3, cte1 where cte1.a1 = (select max(cte3.b1) from cte3 where cte3.b2 = cte1.a2);";
            // sql = @"with cte1 as (select * from a),cte3 as (select * from cte1) select * from cte3;";
            //sql = @"with cte1 as (select * from a),	cte2 as (select * from b),	cte3 as (select * from cte1 join cte2 on a1=b1) select * from cte3;";
            //sql = @"with cte1 as (select* from a),	cte3 as (select* from cte1) select* from cte3, cte1 where cte1.a1 = (select max(cte3.a1) from cte3 where cte3.a2 = cte1.a2);";
            //sql = @"select * from a, (select max(b2) from b where b1<1)c where a1<2;";
            //sql = @"with cte1 as (select b3, max(b2) maxb2 from b where b1<1 group by b3)select a1, maxb2 from a, cte1 where a.a3=cte1.b3 and a1<2;";
            //sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            sql = "select * from a join b on a1=b1 where a1 < (select a2 from a where a2=b2);";
            //sql = "select * from a join c on a1=c1 where a1 < (select b2 from a join b on a1=b1 where a1 < (select a2 from a where a2=b2) and a3 = c3) x";
            //sql = "select * from (select * from (select * from a)b)c;";
            //sql = "select b1 from a,b where b.b2 = a.a2";
            //sql = "select* from a where a3 > (select max(a2) from a);";
            //sql = "select b1 from a,b,c where b.b2 = a.a2 and b.b3=c.c3";
            //sql = "select b1 from a,b,c,c c1 where b.b2 = a.a2 and b.b3=c.c3 and c1.c1 = a.a1";
            sql = "select 7, (4-a3)/2*2+1+sum(a1), sum(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";

            doit:
            Console.WriteLine(sql);
            var a = RawParser.ParseSqlStatement(sql);

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            a.profileOpt_.enabled_ = true;
            var rawplan = a.CreatePlan();
            Console.WriteLine(rawplan.PrintString(0));

            bool useMemo = true;
            PhysicNode phyplan = null;
            if (useMemo)
            {
                Optimizer.EnqueRootPlan(a);
                var memo = Optimizer.memoset_[0];
                Console.WriteLine(memo.Print());
                Optimizer.SearchOptimal(null);
                Console.WriteLine(memo.Print());
                phyplan = Optimizer.RetrieveOptimalPlan();
                Console.WriteLine(phyplan.PrintString(0));
            }
            else
            {
                // -- optimize the plan
                Console.WriteLine("-- optimized plan --");
                var optplan = a.Optimize();
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

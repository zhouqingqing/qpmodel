using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            //sql = "select 1 from a where a.a1 > (select b.b1 from b) and a.a2 > (select c3 from c);";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3));";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)));";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > 2);";
            //sql = "select 1 from a, (select b1,b2 from b) k where k.b1 > 0 and a.a1 = k.b1"; // bug: can't push filter across subuquery
            //sql = "select a.a1+b.b2 from a, b, c where a3>6 and c2 > 10 and a1=b1 and a2 = c2;";
            //sql = "select f(g(a1)) from a, b where a.a3 = b.b2 and a.a2>1 group by a.a1, a.a2 having sum(a.a1)>0;";
            //sql = "select a1 from a, b where a.a1 = b.b1 and a.a2 > (select c1 from c where c.c1=c.c3);";
            //sql = "select (1+2)*3, 1+f(g(a))+1+2*3, a.i, a.i, i+a.j*2 from a, (select * from b) b where a.i=b.i;";
            //sql = "select a.a1, a.a1+a.a3, a1+b2 from a, b where a1 = b1 and a2>2";
            /////////////// sql = "select a.a1 from a, b where a2>2";
            //sql = "select a.a1 from a where a2>1 and a3>3";
            //sql = "select a1*2+a2*1+3 from (select a1,a2 from a) b;";
            ////////////// sql = "select * from a, (select * from b where b2>2) c;";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)));";
            //sql = "select a.a1 from a, b where a2 > 1";
            //sql = "select c.* from a, (select * from b) c";

            //sql = "select * from a, (select * from b) c";
            //sql = "select b.a1 + a3 from (select a3,a1 from a) b";
            //sql = "select b.a1 + a2 from (select a1,a2 from a, c) b";
            //sql = @"with cte1 as (select * from a), cte2 as (select * from b) select a1,a1+a2 from cte1 where a1<6 group by a1, a1+a2  
            //        union select b2, b3 from cte2 where b2 > 3 group by b1, b1+b2 
            //        order by 2, 1 desc;";
            //sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = 3)";
            //sql = "select a1,a1,a3,a3 from a where a1>1";
            //sql = "select 1,  (select b1 from b) from a where a.a1 = 2;";
            sql = "select a1+b1 from a, b";
            sql = "select a.a1,a.a1+a.a2 from a where a.a2 > 3";
            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)))";

            var a = RawParser.ParseSQLStatement(sql);

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            var rawplan = a.CreatePlan();
            Console.WriteLine(rawplan.PrintString(0));

            // -- optimize the plan
            Console.WriteLine("-- optimized plan --");
            var optplan = a.Optimize();
            Console.WriteLine(optplan.PrintString(0));

            // -- physical plan
            Console.WriteLine("-- physical plan --");
            var phyplan = a.GetPhysicPlan();
            Console.WriteLine(phyplan.PrintString(0));

            var final = new PhysicPrint(phyplan);
            final.Open();
            final.Exec(null);
            final.Close();
        }
    }
}

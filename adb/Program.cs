using System;

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
            sql = "select * from a, (select * from b where b2>2) c;";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)));";
            //sql = "select a.a1 from a, b where a2 > 1";
            sql = "select c.* from a, (select * from b) c";

            //sql = "select * from a, (select * from b) c";
            //sql = "select b.a1 + a3 from (select a3,a1 from a) b";
            //sql = "select b.a1 + a2 from (select a1,a2 from a, c) b";
            //sql = @"with cte1 as (select * from a), cte2 as (select * from b) select a1,a1+a2 from cte1 where a1<6 group by a1, a1+a2  
            //        union select b2, b3 from cte2 where b2 > 3 group by b1, b1+b2 
            //        order by 2, 1 desc;";
            sql = "select a2  from a where a.a1 > (select b1 from b where b4 = a4 and b3<3)";
            //sql = "select a1,a1,a3,a3 from a where a1>1";
            //sql = "select a1,a1,a3,a3, (select b2 from b where b2=2) from a where a1>1";
            //sql = "select 1,  (select b1 from b) from a where a.a1 = 2;";
            //sql = "select a1+b1 from a, b";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b2 > ((select c1 from c where c.c2=b1)))";

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

            // test4: deep/ref 2+ outside vars
            sql = "select a1,a2,a3  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            // test4: deep/ref 2+ outside 2+ vars
            sql = @" select a1,a2,a3  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b3<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 >= (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b3<4);";
            sql = @"select a1  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            // test5: subquery in selection list
            //sql = "select a1, (select b1 from b where b2 = a2) from a;";
            //sql = "select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 2) and b2<4);";
            sql = @"select a1 from a, b where a1=b1 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and b3<5);";

        //sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
        // sql = "create table a (a1 int, a2 char(10), a3 datetime, a4 numeric(9,2), a5 numeric(9));";
        // sql = "insert into a values(5*2+1, 'string' ,'2019-09-01', 50.2, 50);";
        // sql = "insert into a select * from b where b1>1;";
        // string filename = @"'d:\test.csv'"; sql = $"copy a from {filename} where a1>1;";
        //sql = "insert into a values(1,2*5,3,4);";
        sql = "select a1, a2  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
            sql = @"select a4  from a where a.a1 = (select b1 from (select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
                and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b3=a3 and bo.b3 = a3 and b3> 0) and b3<5);";
            sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b2) and b.b1 > ((select c2 from c where c.c2=b2)))";
            sql = @"select a1+b3+c2 from c,a, b where a1=b1 and b2=c2 and 
                    a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                                                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
            //sql = "select a1  from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3 and bo.b3 = a3 and b3> 3) and b2<3);";
            //sql = "select b3+c2 from a, b, c where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";
        sql = "select b3+c2 from a,c,b where (select b1+b2 from b where b1=a1)>4 and (select c2+c3 from c where c1=b1)>6 and c1<1";
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            sql = "select a1, a3  from a where a.a1 = (select b1 from b where b2 = a2 and b3<3);";
            sql = "select min(a1), max(a1)+sum(a1)*2 from a;";
            sql = " select max(a1), sum(a1+a2), max(a1)+sum(a1+a2)*2 from a;";
            sql = "select b3+c2 from a,b,c where a1>= (select b1 from b where b1=a1) and a2 >= (select c2 from c where c1=a1);";
            sql = "select (4-a3)/2*2+1+min(a1), max(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            sql = "select (4-a3)/2*2+1+min(a1), count(a1), max(a1)+sum(a1+a2)*2 from a group by (4-a3)/2;";
            sql = "select 1+min(a1), count(a1), max(a1)+sum(a1+a2)*2 from a group by 1;";
            sql = "select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);";
            sql = "select(4-a3)/2,(4-a3)/2*2 + 1 + min(a1), avg(a4)+count(a1), max(a1) + sum(a1 + a2) * 2 from a group by 1 order by 1";
            sql = "select * from a where a1>0 order by a1;";
            //            sql = "select sum(a1) from a where a1>0 group by a1;";
            // sql = "select a2, sum(a1) from a where a1>0 group by a2";
            sql = "select * from a, (select * from b where b2>2) c";


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

            // -- optimize the plan
            Console.WriteLine("-- optimized plan --");
            var optplan = a.Optimize();
            Console.WriteLine(optplan.PrintString(0));

            // -- physical plan
            Console.WriteLine("-- physical plan --");
            var phyplan = a.physicPlan_;
            Console.WriteLine(phyplan.PrintString(0));

            Console.WriteLine("-- profiling plan --");
            var final = new PhysicCollect(phyplan);
            final.Open();
            final.Exec(new ExecContext(), null);
            final.Close();
            Console.WriteLine(phyplan.PrintString(0));
        }
    }
}

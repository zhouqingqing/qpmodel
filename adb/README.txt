TODO items:
======================================
1. csv reader so we can load TPCH data
2. CREATE TABLE/INSERT  to support data loading
3. tpch data types, sql syntax
4. subquery to join conversion
5. join re-ordering
6. parallel query/ctes etc
7. [Executor] codegen


Known issues:
======================================
1. subquery shall force join order to let referenced tablerefs show up before subquery. Example:

            sql = @"select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";


            sql = @"select a1 from a, b,c where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2 
                and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
                and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);";

	The only difference is the table order in FROM clause. first query works but not second because of table 'c' is outerref'ed as c3<5.
	Try this with  set join_collapse_limit = 1 - you shall see 'c' always show up before its outerref.


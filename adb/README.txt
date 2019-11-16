TODO items:
======================================
1. csv reader so we can load TPCH data
2. CREATE TABLE/INSERT  to support data loading
3. tpch data types, sql syntax, null handling
4. subquery to join conversion
5. join re-ordering
6. parallel query/ctes etc
7. [Executor] codegen

Small items:
P1: 
5. can't push predicate deep enough (q8) - this directly affect usage of hash join
6. hash join is not real

P2:
1. implement sort
2. not in/ not exists etc
3. subquery expansion bound issue
4. q22 print subquery twice issue


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

2. object Equal/Hash semantics

   Equal may have different meaning in term of context. Say "a+b" equals "b+a"? For push down purpose, yes, they are. Currently this problem is most obvious
   with ColumnOrdinalFix expr look up. There are more complicated exmaples like "(a+1)+b" vs. "(b+1) +a" or (select (4-a)/2*2 ... group by (4-a)/2) - if you 
   simplify maths before figure out selection list show up in group by, you will be in trouble.

   Maybe we shall redefine SemanticEqual() interface to avoid confusion.
   Hash is important as well whenever we use any indexed container - they will assume Equal/Hash interface, so if we use SemanticEqual(), we can't use any 
   indexed container.

   ExprRef can further cause confusion with Equal semantics because it is essentially a wrapper of an expression and equal semantics shall be applied to the 
   expressions it represents.

3. How deep expression shall push down?
   Get1
      output: a1, a1+a2
	  filter: (a1+a2)>10 or (a1+a2)<5 or (a1+a2)=100

   Get2
      output: a1
	  filter: a1<2 ((very unlikely) and ((a1+a2)<10 or (a1+a2)=100)

   in Get2, we definitely shall not try to compute a1+a2 with output, but 
   1) we shall avoid repeated computing a1+a2, so a1+a2 compute once shall happen within filter.
   2) we shall do a1+a2 computation on demand, meaning only after a1>=2.

4. tpch issues
   see unittest.
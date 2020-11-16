select a1 from a;
select a1 + a2 from b;
select (a1 + b3) * x3 from q, z where q.a1 > z.b3 and a.a1 + b.x3 < 10;
select a1 + a2
from x, y
where x.a1 < y.a2;
select 10 + a1 from x where 10 > a2;
select b1 from a,b,c,c c1 where b.b2 = a.a2;
select b1 from a,b,c where b.b2 = a.a2;
select b1 from a,b,c where b.b2 = a.a2;
select a.a1,b.a1,c.a1, a.a1+b.a1+c.a1 from a, a b, a c where a.a1=5-b.a1-c.a1;
select a.* from a;
select * from a, b;
select a1 from a, b where a1 <= b1;
select c2 / 2 from c where c.c3 = b3;
select * from case_sensitive_01;
select Col1, "Col3" from case_sensitive_01 where "Col3" + "COL4" > "COL4" - COL1;
select "Col3" as COL3, "COL4" as col4, "GROUP BY" as "ORDER BY" from case_SENSITIVE_01;
select "T1"."GROUP BY" as gb, t2."COL4" as c4 from case_SENSITIVE_01 as "T1" where T2.Col1 / 10 = "T1".Col1;
select "T1".Col3 as gb, "T1".COL4 as c4 from case_SENSITIVE_01 as "T1";
select c2, c1+c1 as c2, c3 from c, d where c2+c2 > d1;
select 1 + a1 + 2 + a2 + 3 + 4 + a4 + 5 + 6 from a;
select a1, a2 from a where 100 > a1 + a2;
select a1 from a where a1 + 1 < 3;
select a1 from a where a1 - 1 < 3;
select 1 + 2 + a1, a2 + 4 + 5 from a;
select 1 + a1 + 2, 100 + a2 + 15 from a;
select a1 * 0, a2 + 0, a3 - 0 from a;
select a1 from a where a1 + 1 < a1 + 4;
select x1, true from q;
select false, true from q;
select false, true from q where false = true;
select a1 + a2 from a where a1 < 10 or a2 > 20;
select (a1 + a2) * a3 from a where (a1 < 10 or a2 > 20) and a4 < 100;

# these three will fail.
select * from a where a1 = null;
select * from a where a1 is null;
select * from a where a1 <> null;

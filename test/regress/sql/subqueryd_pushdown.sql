-- Correlated subquery: unnest ON, pushdown ON (decorrelation + push-down)
set enable_subquery_unnest = true;
set enable_dependent_join_pushdown = true;
set enable_neumann_full_decorrelation = false;

select a1 from a where exists (select 1 from b where b1=a1 and b2>1);
select a1 from a where exists (select 1 from b join c on b2=c2 and c1=a1);
select a1 from a where exists (select 1 from b join c on b2=c2 where b1=a1 and c3>3);
select a1 from a where a1 < (select count(*) from b where b1=a1);
select a1 from a where not exists (select 1 from b join c on b1=c1 and c2=a2);
select a1 from a where exists (
    select 1 from b where b1=a1 and exists (
        select 1 from c where c1=b1 and c2>1));

-- nested scalar subquery with cross-level correlation (bo.b3=a3 in inner subquery)
select a1 from a where a.a1 = (select b1 from b bo where b2 = a2 and b1 = (select b1 from b where b3=a3
    and bo.b3 = a3 and b3> 1) and b2<3);

-- multi-table join with multiple nested scalar subqueries and FromQuery barrier
select a1 from c,a, b where a1=b1 and b2=c2 and a.a1 = (select b1 from(select b_2.b1, b_1.b2, b_1.b3 from b b_1, b b_2) bo where b2 = a2
    and b1 = (select b1 from b where b3 = a3 and bo.b3 = c3 and b3> 1) and b2<5)
    and a.a2 = (select b2 from b bo where b1 = a1 and b2 = (select b2 from b where b4 = a3 + 1 and bo.b3 = a3 and b3> 0) and c3<5);

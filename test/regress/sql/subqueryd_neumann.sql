-- Full Neumann decorrelation: D pushed into subquery, converted to regular joins
set enable_subquery_unnest = true;
set enable_neumann_full_decorrelation = true;

-- q1: EXISTS with correlation in WHERE → semi join
select a1 from a where exists (select 1 from b where b1=a1 and b2>1);
-- q2: EXISTS with correlation in JOIN ON → D pushed into right side of b⋈c
select a1 from a where exists (select 1 from b join c on b2=c2 and c1=a1);
-- q3: join + correlation in WHERE
select a1 from a where exists (select 1 from b join c on b2=c2 where b1=a1 and c3>3);
-- q4: scalar subquery → left outer join
select a1 from a where a1 < (select count(*) from b where b1=a1);
-- q5: NOT EXISTS → anti-semi join
select a1 from a where not exists (select 1 from b join c on b1=c1 and c2=a2);
-- q6: nested correlated subqueries
select a1 from a where exists (
    select 1 from b where b1=a1 and exists (
        select 1 from c where c1=b1 and c2>1));

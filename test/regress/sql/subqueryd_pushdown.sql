-- Correlated subquery: unnest ON, pushdown ON (decorrelation + push-down)
set enable_subquery_unnest = true;
set enable_dependent_join_pushdown = true;

select a1 from a where exists (select 1 from b where b1=a1 and b2>1);
select a1 from a where exists (select 1 from b join c on b2=c2 and c1=a1);
select a1 from a where exists (select 1 from b join c on b2=c2 where b1=a1 and c3>3);
select a1 from a where a1 < (select count(*) from b where b1=a1);
select a1 from a where not exists (select 1 from b join c on b1=c1 and c2=a2);
select a1 from a where exists (
    select 1 from b where b1=a1 and exists (
        select 1 from c where c1=b1 and c2>1));

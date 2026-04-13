-- Issue #271: OR handling in inToMarkJoin and scalarToSingleJoin
set enable_subquery_unnest = true;
set enable_dependent_join_pushdown = false;
set enable_neumann_full_decorrelation = false;

-- correlated IN subquery in OR: IN matches nothing, a2>2 matches a1=2
select a1 from a where a2 in (select b2 from b where b2 = a1) or a2 > 2;

-- NOT IN with OR
select a1 from a where a2 not in (select b2 from b where b2 = a1) or a2 > 2;

-- scalar subquery in OR
select a1, a3 from a where a.a1 = (select b1 from b where b2 = a2 and b3<4) or a2>1;

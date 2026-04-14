-- Issue #272: realize MarkJoin as HashJoin for IN/NOT IN
-- These queries exercise the hash-based execution path in PhysicMarkJoin.
-- The hash is built on correlation (residual) keys, not the IN-list marker.

set enable_subquery_unnest = true;
set enable_dependent_join_pushdown = false;
set enable_neumann_full_decorrelation = false;

-- basic correlated IN: hash on b2=a1 (correlation), marker is a2=b2
select a1 from a where a2 in (select b2 from b where b2 = a1);

-- basic correlated NOT IN: same keys, anti-semi semantics
select a1 from a where a2 not in (select b2 from b where b2 = a1);

-- correlated IN with additional filter on subquery side
select a1 from a where a1 in (select b1 from b where b2 = a2 and b3 > 2);

-- NOT IN with additional filter
select a1 from a where a1 not in (select b1 from b where b2 = a2 and b3 > 2);

-- IN with no correlation predicate: falls back to nested loop
select a1 from a where a2 in (select b2 from b where b3 > 2);

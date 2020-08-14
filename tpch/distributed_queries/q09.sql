-- using default substitutions

select
	nation_r,
	o_year,
	sum(amount) as sum_profit
from
	(
		select
			n_name as nation_r,
			year(o_orderdate) as o_year,
			l_extendedprice * (1 - l_discount) - ps_supplycost * l_quantity as amount
		from
			part_d,
			supplier_d,
			lineitem_d,
			partsupp_d,
			orders_d,
			nation_r
		where
			s_suppkey = l_suppkey
			and ps_suppkey = l_suppkey
			and ps_partkey = l_partkey
			and p_partkey = l_partkey
			and o_orderkey = l_orderkey
			and s_nationkey = n_nationkey
			and p_name like '%green%'
	) as profit
group by
	nation_r,
	o_year
order by
	nation_r,
	o_year desc

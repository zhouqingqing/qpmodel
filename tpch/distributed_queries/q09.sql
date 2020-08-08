-- using default substitutions

select
	nation_rep,
	o_year,
	sum(amount) as sum_profit
from
	(
		select
			n_name as nation_rep,
			year(o_orderdate) as o_year,
			l_extendedprice * (1 - l_discount) - ps_supplycost * l_quantity as amount
		from
			part_dstr,
			supplier_dstr,
			lineitem_dstr,
			partsupp_dstr,
			orders_dstr,
			nation_rep
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
	nation_rep,
	o_year
order by
	nation_rep,
	o_year desc

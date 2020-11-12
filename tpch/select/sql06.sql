select	(select count(O.o_orderkey) 
	 	from orders O 
	 	where O.o_custkey = 37) as OrderCount, 
	c_name,
	(select count(O2.o_custkey) 
		from orders O2
		where O2.o_custkey = 26) as CustPhone
from customer C
where exists
	(
		select *
		from nation N
		where N.n_name like '%A%'
		and exists(
			select * 
			from region R
			where N.n_regionkey = R.r_regionkey
			and R.r_regionkey<2
		)
	)
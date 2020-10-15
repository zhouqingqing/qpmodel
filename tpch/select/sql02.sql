select c_name,
	(select count(O.o_orderkey) 
	 	from orders O 
	 	where O.o_custkey = 37) as OrderCount

from customer C
where exists
	(
		select *
		from nation N
		where C.c_nationkey=N.n_nationkey
		and N.n_regionkey=0
	)
order by c_name
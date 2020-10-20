select
	c_name, 
	(select 
		count(O.o_orderkey) 
	from 
		orders O 
	where O.o_custkey = 37) as OrderCount 
from 
	customer C 
order by c_name



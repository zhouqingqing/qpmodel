select *
from nation N
where N.n_name like '%A%'
and exists(
	select * 
	from region R
	where N.n_regionkey = R.r_regionkey
	and R.r_regionkey<2
)
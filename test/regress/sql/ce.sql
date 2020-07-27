select * from lineitem where l_extendedprice > 25000;
select * from orders where o_orderdate >= date '1993-07-01' and o_orderdate < date '1997-07-01';
select * from lineitem where l_discount between .06 - 0.01  and .06 + 0.01;
select * from lineitem where l_shipmode in ('RAIL', 'TRUCK', 'REG AIR', 'MAIL');
select * from part where p_type like 'MEDIUM%';
select * from lineitem, orders where l_orderkey = o_orderkey;
select * from lineitem, partsupp where ps_suppkey = l_suppkey and ps_partkey = l_partkey;
select * from orders left join customer on o_custkey = c_custkey where o_totalprice > 50000 + 0.01;
select * from partsupp, part, supplier where ps_partkey = p_partkey and s_suppkey = ps_suppkey;
select count(*) from lineitem group by l_partkey;
select count(*) from lineitem group by l_partkey, l_suppkey;
select count(*) from lineitem where l_partkey < 100 group by l_partkey, l_shipmode;

select l_orderkey from (select * from lineitem) l, orders, part 
where l.l_orderkey = o_orderkey and l.l_partkey = p_partkey 
group by l.l_orderkey order by l.l_orderkey;

create index idx_supplier_nation_key on supplier (s_nationkey);
create index idx_partsupp_partkey    on partsupp (ps_partkey);
create index idx_partsupp_suppkey    on partsupp (ps_suppkey);
create index idx_customer_nationkey  on customer (c_nationkey);
create index idx_orders_custkey      on orders (o_custkey);
create index idx_lineitem_orderkey   on lineitem (l_orderkey);
create index idx_lineitem_part_supp  on lineitem (l_partkey,l_suppkey);
create index idx_nation_regionkey    on nation (n_regionkey);
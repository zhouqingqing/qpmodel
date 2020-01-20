-- start query 9 in stream 0 using template query9.tpl
select case when (select count(*) 
                  from store_sales 
                  where ss_quantity between (1 , 20)) > 1071
            then (select avg(ss_ext_tax) 
                  from store_sales 
                  where ss_quantity between (1 , 20)) 
            else (select avg(ss_net_paid_inc_tax)
                  from store_sales
                  where ss_quantity between (1 , 20)) end bucket1 ,
       case when (select count(*)
                  from store_sales
                  where ss_quantity between (21 , 40)) > 39161
            then (select avg(ss_ext_tax)
                  from store_sales
                  where ss_quantity between (21 , 40)) 
            else (select avg(ss_net_paid_inc_tax)
                  from store_sales
                  where ss_quantity between (21 , 40)) end bucket2,
       case when (select count(*)
                  from store_sales
                  where ss_quantity between (41 , 60)) > 29434
            then (select avg(ss_ext_tax)
                  from store_sales
                  where ss_quantity between (41 , 60))
            else (select avg(ss_net_paid_inc_tax)
                  from store_sales
                  where ss_quantity between (41 , 60)) end bucket3,
       case when (select count(*)
                  from store_sales
                  where ss_quantity between (61 , 80)) > 6568
            then (select avg(ss_ext_tax)
                  from store_sales
                  where ss_quantity between (61 , 80))
            else (select avg(ss_net_paid_inc_tax)
                  from store_sales
                  where ss_quantity between (61 , 80)) end bucket4,
       case when (select count(*)
                  from store_sales
                  where ss_quantity between (81 , 100)) > 21216
            then (select avg(ss_ext_tax)
                  from store_sales
                  where ss_quantity between (81 , 100))
            else (select avg(ss_net_paid_inc_tax)
                  from store_sales
                  where ss_quantity between (81 , 100)) end bucket5
from reason
where r_reason_sk = 1
;

-- end query 9 in stream 0 using template query9.tpl

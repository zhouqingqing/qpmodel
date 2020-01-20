-- start query 28 in stream 0 using template query28.tpl
select  *
from (select avg(ss_list_price) B1_LP
            ,count(ss_list_price) B1_CNT
            ,count(distinct ss_list_price) B1_CNTD
      from store_sales
      where ss_quantity between (0 , 5)
        and (ss_list_price between (107 , 107+10 )
             or ss_coupon_amt between (1319 , 1319+1000)
             or ss_wholesale_cost between (60 , 60+20))) B1,
     (select avg(ss_list_price) B2_LP
            ,count(ss_list_price) B2_CNT
            ,count(distinct ss_list_price) B2_CNTD
      from store_sales
      where ss_quantity between (6 , 10)
        and (ss_list_price between (23 , 23+10)
          or ss_coupon_amt between (825 , 825+1000)
          or ss_wholesale_cost between (43 , 43+20))) B2,
     (select avg(ss_list_price) B3_LP
            ,count(ss_list_price) B3_CNT
            ,count(distinct ss_list_price) B3_CNTD
      from store_sales
      where ss_quantity between (11 , 15)
        and (ss_list_price between (74 , 74+10)
          or ss_coupon_amt between (4381 , 4381+1000)
          or ss_wholesale_cost between (57 , 57+20))) B3,
     (select avg(ss_list_price) B4_LP
            ,count(ss_list_price) B4_CNT
            ,count(distinct ss_list_price) B4_CNTD
      from store_sales
      where ss_quantity between (16 , 20)
        and (ss_list_price between (89 , 89+10)
          or ss_coupon_amt between (3117 , 3117+1000)
          or ss_wholesale_cost between (68 , 68+20))) B4,
     (select avg(ss_list_price) B5_LP
            ,count(ss_list_price) B5_CNT
            ,count(distinct ss_list_price) B5_CNTD
      from store_sales
      where ss_quantity between (21 , 25)
        and (ss_list_price between (58 , 58+10)
          or ss_coupon_amt between (9402 , 9402+1000)
          or ss_wholesale_cost between (38 , 38+20))) B5,
     (select avg(ss_list_price) B6_LP
            ,count(ss_list_price) B6_CNT
            ,count(distinct ss_list_price) B6_CNTD
      from store_sales
      where ss_quantity between (26 , 30)
        and (ss_list_price between (64 , 64+10)
          or ss_coupon_amt between (5792 , 5792+1000)
          or ss_wholesale_cost between (73 , 73+20))) B6
limit 100;

-- end query 28 in stream 0 using template query28.tpl

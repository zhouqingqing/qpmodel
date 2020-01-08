-- this script repros builtin a/b/c/d tables using SQL
--

create table a (a1 int, a2 int, a3 int, a4 int);
create table b (b1 int, b2 int, b3 int, b4 int);
create table c (c1 int, c2 int, c3 int, c4 int);
create table d (d1 int, d2 int, d3 int, d4 int);

insert into a values(0,1,2,3);
insert into a values(1,2,3,4);
insert into a values(2,3,4,5);
insert into b select * from a;
insert into c select * from a;
insert into d select * from a;


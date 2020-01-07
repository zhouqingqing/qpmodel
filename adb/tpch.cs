using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace adb
{
    public class Tpch
    {
        static public string[] tabnames_ = { 
            "region", "nation", "part","supplier",
            "partsupp", "customer", "orders", "lineitem"
        };
        static public string[] createtables_ = {
            @"CREATE TABLE region  (
                r_regionkey  INTEGER not null,
                r_name       CHAR(25) not null,
                r_comment    VARCHAR(152));",
            @"CREATE TABLE nation  ( 
                n_nationkey  INTEGER not null,
                n_name       CHAR(25) not null,
                n_regionkey  INTEGER not null,
                n_comment    VARCHAR(152));",
            @"CREATE TABLE part (
                p_partkey     INTEGER not null,
                p_name        VARCHAR(55) not null,
                p_mfgr        CHAR(25) not null,
                p_brand       CHAR(10) not null,
                p_type        VARCHAR(25) not null,
                p_size        INTEGER not null,
                p_container   CHAR(10) not null,
                p_retailprice DOUBLE not null,
                p_comment     VARCHAR(23) not null);",
            @"CREATE TABLE supplier (
                s_suppkey     INTEGER not null,
                s_name        CHAR(25) not null,
                s_address     VARCHAR(40) not null,
                s_nationkey   INTEGER not null,
                s_phone       CHAR(15) not null,
                s_acctbal     DOUBLE not null,
                s_comment     VARCHAR(101) not null);",
            @"CREATE TABLE partsupp(
                ps_partkey     INTEGER not null,
                ps_suppkey     INTEGER not null,
                ps_availqty    INTEGER not null,
                ps_supplycost  DOUBLE not null,
                ps_comment     VARCHAR(199) not null);",
            @"CREATE TABLE customer (
                c_custkey     INTEGER not null,
                c_name        VARCHAR(25) not null,
                c_address     VARCHAR(40) not null,
                c_nationkey   INTEGER not null,
                c_phone       CHAR(15) not null,
                c_acctbal     DOUBLE not null,
                c_mktsegment  CHAR(10) not null,
                c_comment     VARCHAR(117) not null);",
            @"CREATE TABLE orders (
                o_orderkey       INTEGER not null,
                o_custkey        INTEGER not null,
                o_orderstatus    CHAR(1) not null,
                o_totalprice     DOUBLE not null,
                o_orderdate      DATE not null,
                o_orderpriority  CHAR(15) not null,  
                o_clerk          CHAR(15) not null, 
                o_shippriority   INTEGER not null,
                o_comment        VARCHAR(79) not null);",
            @"CREATE TABLE lineitem(
                l_orderkey    INTEGER not null,
                l_partkey     INTEGER not null,
                l_suppkey     INTEGER not null,
                l_linenumber  INTEGER not null,
                l_quantity    DOUBLE PRECISION not null,
                l_extendedprice  DOUBLE PRECISION not null,
                l_discount    DOUBLE PRECISION not null,
                l_tax         DOUBLE PRECISION not null,
                l_returnflag  CHAR(1) not null,
                l_linestatus  CHAR(1) not null,
                l_shipdate    DATE not null,
                l_commitdate  DATE not null,
                l_receiptdate DATE not null,
                l_shipinstruct CHAR(25) not null,
                l_shipmode     CHAR(10) not null,
                l_comment      VARCHAR(44) not null);"
        };
        static public string[] createindexes_ = {
            @"create index idx_supplier_nation_key on supplier (s_nationkey);",
            @"create index idx_partsupp_partkey on partsupp (ps_partkey);",
            @"create index idx_partsupp_suppkey on partsupp (ps_suppkey);",
            @"create index idx_customer_nationkey on customer (c_nationkey);",
            @"create index idx_orders_custkey on orders (o_custkey);",
            @"create index idx_lineitem_orderkey on lineitem (l_orderkey);",
            @"create index idx_lineitem_part_supp on lineitem (l_partkey,l_suppkey);",
            @"create index idx_nation_regionkey on nation (n_regionkey);",
        };

        static public void CreateTables() {
            foreach (var v in createtables_) {
                var stmt = RawParser.ParseSqlStatements(v);
                stmt.Exec();
            }
        }

        static public void LoadTables(string subfolder) {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\tpch\data\{subfolder}";
            foreach(var v in tabnames_)
            {
                string filename = $@"'{folder}\{v}.tbl'";
                var sql = $"copy {v} from {filename};";
                var stmt = RawParser.ParseSqlStatements(sql);
                stmt.Exec();
            }
        }

        static public void CreateIndexes() {
            foreach (var v in createindexes_)
            {
                var stmt = RawParser.ParseSqlStatements(v);
                stmt.Exec();
            }
        }

        static public void AnalyzeTables()
        {
            foreach (var v in tabnames_)
            {
                var sql = $"analyze {v};";
                var stmt = RawParser.ParseSqlStatements(sql);
                stmt.Exec();
            }
        }
    }

    public class Tpcds {
        static public void CreateTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\tpcds";
            string filename = $@"{folder}\tpcds.sql";
            var sql = File.ReadAllText(filename);
            var stmt = RawParser.ParseSqlStatements(sql);
            stmt.Exec();
        }

        static public void LoadTables(string subfolder)
        {
        }

        static public void AnalyzeTables()
        {
        }
    }
}

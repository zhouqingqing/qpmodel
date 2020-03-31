/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

using qpmodel.sqlparser;
using qpmodel.logic;

namespace qpmodel.test
{
    public class Tpch
    {
        static public string[] tabnames_ = { 
            "region", "nation", "part","supplier",
            "partsupp", "customer", "orders", "lineitem"
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
            // hack: drop tpch table customer
            SQLStatement.ExecSQL("drop table customer;", out _, out _);

            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\tpch\create";
            string filename = $@"{folder}\tpch.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }

        static public void LoadTables(string subfolder) {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\tpch\data\{subfolder}";
            foreach(var v in tabnames_)
            {
                string filename = $@"'{folder}\{v}.tbl'";
                var sql = $"copy {v} from {filename};";
                SQLStatement.ExecSQL(sql, out _, out _);
            }
        }

        static public void CreateIndexes() {
            SQLStatement.ExecSQLList(string.Join("", createindexes_));
        }

        static public void AnalyzeTables()
        {
            foreach (var v in tabnames_)
            {
                var sql = $"analyze {v};";
                SQLStatement.ExecSQL(sql, out _, out _);
            }
        }
    }

    public class Tpcds {
        static public void CreateTables()
        {
            // hack: drop tpch table customer
            SQLStatement.ExecSQL("drop table customer;", out _, out _);

            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\tpcds\create";
            string filename = $@"{folder}\tpcds.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }

        static public void LoadTables(string subfolder)
        {
        }

        static public void AnalyzeTables()
        {
        }
    }

    public class JOBench
    {
        static public void CreateTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\jobench\create";
            string filename = $@"{folder}\schema.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }

        static public void LoadTables(string subfolder)
        {
        }

        static public void AnalyzeTables()
        {
        }
    }
}

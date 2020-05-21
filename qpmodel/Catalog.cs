/*
 * The MIT License (MIT)
 *
 * Copyright (c) 2020 Futurewei Corp.
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.IO;

using qpmodel.stat;
using qpmodel.sqlparser;
using qpmodel.expr;
using qpmodel.logic;
using qpmodel.physic;
using qpmodel.index;
using qpmodel.test;

using TableColumn = System.Tuple<string, string>;

namespace qpmodel
{
    public class ColumnDef
    {
        readonly public string name_;
        readonly public ColumnType type_;
        public int ordinal_;

        public ColumnDef(string name, ColumnType type, int ord) { name_ = name; type_ = type; ordinal_ = ord; }
        public ColumnDef(string name, int ord) : this(name, new IntType(), ord) { }

        public override string ToString() => $"{name_} {type_} [{ordinal_}]";
    }

    public class Distribution {
        public TableDef tableDef_;

        public List<Row> heap_ = new List<Row>();
        public List<IndexDef> indexes_ = new List<IndexDef>(); // local indexes
    }

    public class TableDef
    {
        public string name_;
        public Dictionary<string, ColumnDef> columns_;
        public ColumnDef distributedBy_;
        public List<IndexDef> indexes_ = new List<IndexDef>();

        // emulated storage across multiple machines
        public List<Distribution> distributions_ = new List<Distribution>();

        public TableDef(string tabName, List<ColumnDef> columns, string distributedBy)
        {
            int npart = 1;
            Dictionary<string, ColumnDef> cols = new Dictionary<string, ColumnDef>();
            foreach (var c in columns)
                cols.Add(c.name_, c);
            name_ = tabName; columns_ = cols;

            if (distributedBy != null)
            {
                cols.TryGetValue(distributedBy, out var partcol);
                if (partcol is null)
                    throw new SemanticAnalyzeException($"can't find distribution column '{distributedBy}'");
                distributedBy_ = partcol;
                npart = QueryOption.num_machines_;
            }
            for (int i = 0; i < npart; i++)
                distributions_.Add(new Distribution());
        }

        public List<ColumnDef> ColumnsInOrder() {
            var list = columns_.Values.ToList();
            return list.OrderBy(x => x.ordinal_).ToList();
        }

        public ColumnDef GetColumn(string column)
        {
            columns_.TryGetValue(column, out var value);
            return value;
        }

        public IndexDef IndexContains(string column) 
        {
            foreach (var v in indexes_) {
                if (v.columns_.Contains(column))
                    return v;
            }
            return null;
        }
    }

    public class SystemTable
    {
    };

    // format: tableName:key, list of <ColName: Key, Column definition>
    public class SysTable : SystemTable
    {
        readonly Dictionary<string, TableDef> records_ = new Dictionary<string, TableDef>();

        public void CreateTable(string tabName, List<ColumnDef> columns, string distributedBy)
        {
            records_.Add(tabName,
                new TableDef(tabName, columns, distributedBy));
        }
        public void DropTable(string tabName)
        {
            records_.Remove(tabName);
            // FIXME: we shall also remove index etc
        }

        public void CreateIndex(string tabName, IndexDef index)
        {
            records_[tabName].indexes_.Add(index);
        }
        public void DropIndex(string indName) {
            var tab = IndexGetTable(indName);
            if (tab != null)
                tab.indexes_.RemoveAll(x => x.name_.Equals(indName));
        }

        public TableDef TryTable(string tabName) {
            if (records_.TryGetValue(tabName, out TableDef value))
                return value;
            return null;
        }
        public TableDef Table(string tabName)=> records_[tabName];

        public IndexDef Index(string indName) {
            foreach (var v in records_)
            {
                foreach (var i in v.Value.indexes_)
                    if (i.name_.Equals(indName))
                        return i;
            }
            return null;
        }

        public TableDef IndexGetTable(string indName)
        {
            foreach (var v in records_)
            {
                foreach (var i in v.Value.indexes_)
                    if (i.name_.Equals(indName))
                        return v.Value;
            }
            return null;
        }
        public Dictionary<string, ColumnDef> TableCols(string tabName)=> records_[tabName].columns_;
        public ColumnDef Column(string tabName, string colName)=> TableCols(tabName)[colName];
    }

    public static class Catalog
    {
        // list of system tables
        public static SysTable systable_ = new SysTable();
        public static SysStats sysstat_ = new SysStats();

        static void createOptimizerTables()
        {
            List<ColumnDef> cols = new List<ColumnDef> { new ColumnDef("i", 0)};
            for (int i = 0; i < 30; i++)
            {
                Catalog.systable_.CreateTable($"T{i}", cols, null);
                var stat = new ColumnStat();
                stat.n_rows_ = (ulong)(1 + i * 10);
                Catalog.sysstat_.AddOrUpdate($"T{i}", "i", stat);
            }
        }

        static void createBuildInTestTables()
        {
            // create tables
            string[] createtables = {
                @"create table test (a1 int, a2 int, a3 int, a4 int);"
                ,
                @"create table a (a1 int, a2 int, a3 int, a4 int);",
                @"create table b (b1 int, b2 int, b3 int, b4 int);",
                @"create table c (c1 int, c2 int, c3 int, c4 int);",
                @"create table d (d1 int, d2 int, d3 int, d4 int);",
                // nullable tables
                @"create table r (r1 int, r2 int, r3 int, r4 int);",
                // distributed tables
                @"create table ad (a1 int, a2 int, a3 int, a4 int) distributed by a1;",
                @"create table bd (b1 int, b2 int, b3 int, b4 int) distributed by b1;",
                @"create table cd (c1 int, c2 int, c3 int, c4 int) distributed by c1;",
                @"create table dd (d1 int, d2 int, d3 int, d4 int) distributed by d1;",
                // steaming tables
                @"create table ast (a0 datetime, a1 int, a2 int, a3 int, a4 int);",
            };
            SQLStatement.ExecSQLList(string.Join("", createtables));

            // load tables
            var curdir = Directory.GetCurrentDirectory();
            var folder = $@"{curdir}\..\..\..\data";
            var tables = new List<string>() { "a", "b", "c", "d", "r", "ad", "bd", "cd", "dd", "ast"};
            foreach (var v in tables)
            {
                string filename = $@"'{folder}\{v}.tbl'";
                var sql = $"copy {v} from {filename};";
                var result = SQLStatement.ExecSQL(sql, out _, out _);
            }

            // create index
            string[] createindexes = {
                @"create unique index dd1 on d(d1);",
                @"create index dd2 on d(d2);",
            };
            SQLStatement.ExecSQLList(string.Join("", createindexes));

            // analyze tables
            foreach (var v in tables)
            {
                var sql = $"analyze {v};";
                var result = SQLStatement.ExecSQL(sql, out _, out _);
            }
        }

        static Catalog()
        {
        }

        static public void Init() 
        {
            // be careful: any exception happened here will be swallowed without throw any exception
            createBuildInTestTables();
            createOptimizerTables();
        }
    }
}

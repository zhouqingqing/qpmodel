using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.IO;

using TableColumn = System.Tuple<string, string>;

namespace adb
{
    public class ColumnType
    {
        public Type type_;
        public int len_;
        public ColumnType(Type type, int len) { type_ = type; len_ = len; }

        public bool Compatible(ColumnType type)
        {
            return true;
        }
    }

    public class BoolType : ColumnType
    {
        public BoolType() : base(typeof(bool), 1) { }
        public override string ToString() => $"bool";
    }
    public class IntType : ColumnType
    {
        public IntType() : base(typeof(int), 4) { }
        public override string ToString() => $"int";
    }
    public class DoubleType : ColumnType
    {
        public DoubleType() : base(typeof(double), 8) { }
        public override string ToString() => $"double";
    }
    public class DateTimeType : ColumnType
    {
        public DateTimeType() : base(typeof(DateTime), 8) { }
        public override string ToString() => $"datetime";
    }
    public class TimeSpanType : ColumnType
    {
        public TimeSpanType() : base(typeof(TimeSpan), 8) {}
        public override string ToString() => $"interval";
    }
    public class CharType : ColumnType
    {
        public CharType(int len) : base(typeof(string), len) { }
        public override string ToString() => $"char({len_})";
    }
    public class VarCharType : ColumnType
    {
        public VarCharType(int len) : base(typeof(string), len) { }
        public override string ToString() => $"varchar({len_})";
    }
    public class NumericType : ColumnType
    {
        public int scale_;
        public NumericType(int prec, int scale) : base(typeof(decimal), prec) => scale_ = scale;
        public override string ToString() => $"numeric({len_}, {scale_})";
    }

    public class AnyType : ColumnType {
        public AnyType() : base(typeof(object), 8) { }
        public override string ToString() => "anytype";
    }

    public class ColumnDef
    {
        readonly public string name_;
        readonly public ColumnType type_;
        public int ordinal_;

        public ColumnDef(string name, ColumnType type, int ord) { name_ = name; type_ = type; ordinal_ = ord; }
        public ColumnDef(string name, int ord) : this(name, new IntType(), ord) { }

        public override string ToString() => $"{name_} {type_} [{ordinal_}]";
    }

    public class TableDef
    {
        public string name_;
        public Dictionary<string, ColumnDef> columns_;

        // storage
        public List<Row> heap_ = new List<Row>();

        public TableDef(string tabName, List<ColumnDef> columns)
        {
            Dictionary<string, ColumnDef> cols = new Dictionary<string, ColumnDef>();
            foreach (var c in columns)
                cols.Add(c.name_, c);
            name_ = tabName; columns_ = cols;
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
    }

    class SystemTable
    {
    };

    // format: tableName:key, list of <ColName: Key, Column definition>
    class SysTable : SystemTable
    {
        readonly Dictionary<string, TableDef> records_ = new Dictionary<string, TableDef>();

        public void Add(string tabName, List<ColumnDef> columns)
        {
            records_.Add(tabName,
                new TableDef(tabName, columns));
        }

        public TableDef TryTable(string tabName) {
            if (records_.TryGetValue(tabName, out TableDef value))
                return value;
            return null;
        }
        public TableDef Table(string tabName)=> records_[tabName];
        public Dictionary<string, ColumnDef> TableCols(string tabName)=> records_[tabName].columns_;
        public ColumnDef Column(string tabName, string colName)=> TableCols(tabName)[colName];
    }

    // format: (tableName, colName):key, column stat
    partial class SysStats : SystemTable
    {
        readonly Dictionary<TableColumn, ColumnStat> records_ = new Dictionary<TableColumn, ColumnStat>();

        public void AddOrUpdate(string tabName, string colName, ColumnStat stat)
        {
            var tabcol = new TableColumn(tabName, colName);
            if (GetColumnStat(tabName, colName) is null)
                records_.Add(tabcol, stat);
            else
                records_[tabcol] = stat;
        }

        public ColumnStat GetColumnStat(string tabName, string colName)
        {
            if (records_.TryGetValue(new TableColumn(tabName, colName), out ColumnStat value))
                return value;
            return null;
        }
    }

    static class Catalog
    {
        // list of system tables
        public static SysTable systable_ = new SysTable();
        public static SysStats sysstat_ = new SysStats();

        static void createBuildInTestTables()
        {
            string[] ddls = {
                @"create table a (a1 int, a2 int, a3 int, a4 int);",
                @"create table b (b1 int, b2 int, b3 int, b4 int);",
                @"create table c (c1 int, c2 int, c3 int, c4 int);",
                @"create table d (d1 int, d2 int, d3 int, d4 int);",
                // nullable tables
                @"create table r (r1 int, r2 int, r3 int, r4 int);",
            };
            var stmt = RawParser.ParseSqlStatements(string.Join("", ddls));
            stmt.Exec();

            // load r
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\data";
            string filename = $@"'{folder}\r.tbl'";
            var sql = $"copy r from {filename};";
            var result = SQLStatement.ExecSQL(sql, out _, out _);
        }
        static Catalog()
        {
            // be careful: any exception happened here will be swallowed without throw any exception
            createBuildInTestTables();
            Tpch.CreateTables();

            // customer table is dup named with tpch, so we can't load tpcds for now
            // Tpcds.CreateTables();
        }
    }
}

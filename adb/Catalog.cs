using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

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

    class ColumnStat
    {
        public Int64 n_distinct_;
    }

    // format: (tableName, colName):key, column stat
    class SysStats : SystemTable
    {
        readonly Dictionary<TableColumn, ColumnStat> records_ = new Dictionary<TableColumn, ColumnStat>();

        public void Add(string tabName, string colName, ColumnStat stat)
        {
            records_.Add(new TableColumn(tabName, colName), stat);
        }

        public ColumnStat Column(string tabName, string colName)
        {
            return records_[new TableColumn(tabName, colName)];
        }
    }

    static class Catalog
    {
        // list of system tables
        public static SysTable systable_ = new SysTable();

        static void createBuildInTestTables()
        {
            string[] ddls = {
                @"create table a (a1 int, a2 int, a3 int, a4 int);",
                @"create table b (b1 int, b2 int, b3 int, b4 int);",
                @"create table c (c1 int, c2 int, c3 int, c4 int);",
            };
            foreach (var v in ddls) {
                var stmt = RawParser.ParseSqlStatement(v) as CreateTableStmt;
                stmt.Exec();
            }
        }
        static Catalog()
        {
            createBuildInTestTables();
            tpch.CreateTables();
        }
    }
}

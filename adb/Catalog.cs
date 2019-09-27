using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TableColumn = System.Tuple<string, string>;

namespace adb
{
    class ColumnDef {
        public string name_;
        public int ordinal_;

        public ColumnDef(string name, int ord) { name_ = name; ordinal_ = ord; }
    }

    class SystemTable
    {
    };

    // format: tableName:key, list of <ColName: Key, Column definition>
    class SysTable : SystemTable {
        Dictionary<string, Dictionary<string, ColumnDef>> records_ = new Dictionary<string, Dictionary<string, ColumnDef>>();

        public void Add(string tablename, List<ColumnDef> columns) {
            Dictionary<string, ColumnDef> cols = new Dictionary<string, ColumnDef>();
            foreach (var c in columns)
                cols.Add(c.name_, c);
            records_.Add(tablename, cols);
        }

        public Dictionary<string, ColumnDef> Table(string table) {
            return records_[table];
        }
        public ColumnDef Column(string table, string column) {
            return records_[table][column];
        }
        public string ColumnFindTable(string column)
        {
            string r = null;
            foreach (var v in records_)
            {
                // shall check duplicates as well
                ColumnDef value;
                if (v.Value.TryGetValue(column, out value))
                {
                    if (r is null)
                        r = v.Key;
                    else
                        throw new Exception($@"ambigous column {column}");
                }
            }

            return r;
        }
    }

    class ColumnStat {
        public Int64 n_distinct_;
    }

    // format: (tableName, colName):key, column stat
    class SysStats : SystemTable {
        Dictionary<TableColumn, ColumnStat> records_ = new Dictionary<TableColumn, ColumnStat>();

        public void Add(string table, string column, ColumnStat stat) {
            records_.Add(new TableColumn(table, column), stat);
        }

        public ColumnStat Column(string table, string column)
        {
            return records_[new TableColumn(table, column)];
        }
    }

    static class Catalog
    {
        // list of system tables
        public static SysTable systable_ = new SysTable();

        static Catalog() {
            systable_.Add("a", new List<ColumnDef> (){new ColumnDef("a1", 0), new ColumnDef("a2", 1), new ColumnDef("a3", 2) });
            systable_.Add("b", new List<ColumnDef>() { new ColumnDef("b1", 0), new ColumnDef("b2", 1), new ColumnDef("b3", 2) });
            systable_.Add("c", new List<ColumnDef>() { new ColumnDef("c1", 0), new ColumnDef("c2", 1), new ColumnDef("c3", 2) });
        }
    }
}

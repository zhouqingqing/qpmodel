using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TableColumn = System.Tuple<string, string>;

namespace adb
{
    public class ColumnDef {
        public string name_;

        public int ordinal_;

        public ColumnDef(string name, int ord) { name_ = name; ordinal_ = ord; }
    }

    public class TableDef
    {
        public string name_;
        public Dictionary<string, ColumnDef> columns_ = new Dictionary<string, ColumnDef>();

        public TableDef(string tabName, List<ColumnDef> columns) {
            Dictionary<string, ColumnDef> cols = new Dictionary<string, ColumnDef>();
            foreach (var c in columns)
                cols.Add(c.name_, c);
            name_ = tabName; columns_ = cols;
        }

        public ColumnDef GetColumn(string column) {
            ColumnDef value;
            columns_.TryGetValue(column, out value);
            return value;
        }
    }

    class SystemTable
    {
    };

    // format: tableName:key, list of <ColName: Key, Column definition>
    class SysTable : SystemTable {
        Dictionary<string, TableDef> records_ = new Dictionary<string, TableDef>();

        public void Add(string tabName, List<ColumnDef> columns) {
            records_.Add(tabName, 
                new TableDef(tabName, columns));
        }

        public Dictionary<string, ColumnDef> Table(string tabName) {
            return records_[tabName].columns_;
        }
        public ColumnDef Column(string tabName, string colName) {
            return Table(tabName)[colName];
        }
        public TableDef ColumnFindTable(string colName)
        {
            TableDef r = null;
            foreach (var v in records_)
            {
                // shall check duplicates as well
                var value = v.Value.GetColumn(colName);
                if (value != null)
                {
                    if (r is null)
                        r = v.Value;
                    else
                        throw new Exception($@"ambigous column {colName}");
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

        public void Add(string tabName, string colName, ColumnStat stat) {
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

        static Catalog() {
            systable_.Add("a", new List<ColumnDef> (){new ColumnDef("a1", 0), new ColumnDef("a2", 1), new ColumnDef("a3", 2) });
            systable_.Add("b", new List<ColumnDef>() { new ColumnDef("b1", 0), new ColumnDef("b2", 1), new ColumnDef("b3", 2) });
            systable_.Add("c", new List<ColumnDef>() { new ColumnDef("c1", 0), new ColumnDef("c2", 1), new ColumnDef("c3", 2) });
        }
    }
}

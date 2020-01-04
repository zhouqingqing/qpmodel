using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

namespace adb
{
    class Historgram {
    }

    class ColumnStat
    {
        public long n_rows_;
        public long n_distinct_;
        public Historgram hists_;

        public ColumnStat() { }

        public void Increment(Value value) {
            n_rows_++;
        }
    }

    partial class SysStats : SystemTable {
        public List<ColumnStat> GetOrCreateTableStats(string tabName)
        {
            var table = Catalog.systable_.Table(tabName);
            var columns = table.columns_;

            List<ColumnStat> stats = new List<ColumnStat>();
            foreach (var v in columns)
            {
                var colName = v.Value.name_;
                var stat = GetColumnStat(tabName, colName);
                if (stat is null)
                {
                    stat = new ColumnStat();
                    AddOrUpdate(tabName, colName, stat);
                }
                  
                stats.Add(stat);
            }

            Debug.Assert(stats.Count == columns.Count);
            return stats;
        }

        public void Increment(Row r, List<ColumnStat> stats) {
            // A full row is presented here, since we generate per column 
            // stats and full row needed for correlation analysis
            for (int i = 0; i < stats.Count; i++) {
                stats[i].Increment(r[i]);
            }
        }

        // stats getters
        public long NumberOfRows(string tabName) {
            return GetOrCreateTableStats(tabName)[0].n_rows_;
        }
    }

    public class LogicAnalyze : LogicNode { 

        public LogicAnalyze(LogicNode child) => children_.Add(child);

        public BaseTableRef GetTargetTable() => (child_() as LogicScanTable).tabref_;
     }

    public class PhysicAnalyze : PhysicNode {
        internal List<ColumnStat> stats_;

        public PhysicAnalyze(LogicAnalyze logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Open()
        {
            var tabName = (logic_ as LogicAnalyze).GetTargetTable().relname_;
            stats_ = Catalog.sysstat_.GetOrCreateTableStats(tabName);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, r =>
            {
                // TODO: we may consider using a SQL statement to do this
                //  select count(*), count(distint a1), count(distinct a2), build_historgram(a1), ...
                Catalog.sysstat_.Increment(r, stats_);
                return null;
            });
        }
    }
}

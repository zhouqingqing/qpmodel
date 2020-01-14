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
        public double nullfrac_;
        public long n_distinct_;
        public Historgram hists_;

        public ColumnStat() { }

        public void ComputeStats(int index, List<Row> samples) {
            int nNulls = 0;
            List<Value> values = new List<Value>();
            foreach (var r in samples) {
                Value val = r[index];
                if (val is null)
                    nNulls++;

                values.Add(val);
            }

            // now finalize the stats
            n_rows_ = samples.Count;
            Debug.Assert(nNulls<= samples.Count);
            nullfrac_ = nNulls / samples.Count;
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

        public void ComputeStats(List<Row> samples, List<ColumnStat> stats) {
            // A full row is presented here, since we generate per column 
            // stats and full row needed for correlation analysis
            for (int i = 0; i < stats.Count; i++) {
                stats[i].ComputeStats(i, samples);
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

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            List<Row> samples = new List<Row>();
            child_().Exec(context, r =>
            {
                samples.Add(r);
                return null;
            });

            // TODO: we may consider using a SQL statement to do this
            //  select count(*), count(distint a1), count(distinct a2), build_historgram(a1), ...
            Catalog.sysstat_.ComputeStats(samples, stats_);
            return null;
        }
    }
}

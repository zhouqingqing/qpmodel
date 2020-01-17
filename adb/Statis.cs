﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using Value = System.Object;

namespace adb
{
    // There are two major types of histgram:
    //  1. equal-width: all buckets have the same boundary width
    //  2. equal-depth: all buckets have the same number of elements. 
    //     So each bucket has roughly d=|R|/NBukets_ rows.
    //  Equal-depth histogram is adaptive to data skew.
    //
    // To construct a equal-depth histgoram:
    //  1. d = |sample rows|/NBuckets_;
    //  2. sort sampling rows;
    //  3. cut the sorted rows sequence with each d values
    //  say depth_ = 20/4 = 5, sorted values are:
    //       -3,-1,2,3,4,5,5,6,6,11,12,12,13,13,14,15, etc
    //  cut: [-3,-1,2,3,4],[5,5,6,6,11],[12,12,13,13,14],[15, etc
    //  and the boundaries are: 4, 11, 14, etc
    //  and the distincts are: 5, 3, 3, etc
    class Historgram {
        public const int NBuckets_ = 100;

        public int depth_;
        public int nbuckets_;
        public Value[] buckets_ = new Value[NBuckets_];
        public int[] distincts_ = new int[NBuckets_];

        int whichBucket(Value val) {
            dynamic value = val;

            if (((dynamic)buckets_[0]).CompareTo(value) >= 0)
                return 0;
            for (int i = 0; i < nbuckets_ - 1; i++)
            {
                dynamic l = buckets_[i];
                dynamic r = buckets_[i + 1];
                if (l.CompareTo(value) <= 0 && r.CompareTo(value) > 0)
                    return i+1;
            }

            return nbuckets_ - 1;
        }

        public double EstSelectivity(string op, Value val) {
            double selectivity = 1.0;

            if (!new List<String>() { "=", ">", ">=", "<","<=" }.Contains(op))
                return selectivity;

            int which = whichBucket(val);
            switch (op)
            {
                case "=":
                    selectivity = 1.0/(nbuckets_ * distincts_[which]);
                    break;
                case ">":
                case ">=":
                    selectivity = 1.0 * (nbuckets_ - which) / nbuckets_;
                    break;
                case "<":
                case "<=":
                    selectivity = 1.0 * which / nbuckets_;
                    break;
            }

            if (selectivity == 0)
                selectivity = 0.001;
            Estimator.validSelectivity(selectivity);
            return selectivity;
        }
    }

    // per column statistics
    class ColumnStat
    {
        public long n_rows_;                // number of rows
        public double nullfrac_;            // null value percentage
        public Historgram hists_ = new Historgram(); // value historgram

        public ColumnStat() { }

        public void ComputeStats(int index, List<Row> samples)
        {
            int nNulls = 0;
            List<Value> values = new List<Value>();
            foreach (var r in samples)
            {
                Value val = r[index];
                if (val is null)
                    nNulls++;

                values.Add(val);
            }

            // now sort the values and equal-depth buckets
            values.Sort();
            int nbuckets = Math.Min(Historgram.NBuckets_, values.Count);
            int depth = values.Count / nbuckets;
            Debug.Assert(depth >= 1);
            for (int i = 0; i < nbuckets; i++)
            {
                hists_.buckets_[i] = values[(i + 1) * depth - 1];
                hists_.distincts_[i] = values.GetRange(i * depth, depth).Distinct().Count();
                Debug.Assert(hists_.distincts_[i]>0);
            }
            hists_.depth_ = depth;
            hists_.nbuckets_ = nbuckets;

            // finalize the stats
            n_rows_ = samples.Count;
            Debug.Assert(nNulls <= samples.Count);
            nullfrac_ = nNulls / samples.Count;
        }

        public double EstSelectivity(string op, Value val)
        {
            return hists_.EstSelectivity(op, val);
        }
    }

    public static class Estimator
    {
        public static void validSelectivity(double selectivity)
        {
            Debug.Assert(selectivity > 0 && selectivity <= 1);
        }

        static double EstSingleSelectivity(Expr filter)
        {
            double selectivity = 1.0;
            Debug.Assert(filter.FilterToAndList().Count == 1);

            if (filter is BinExpr pred)
            {
                if (pred.l_() is ColExpr pl && pl.tabRef_ is BaseTableRef bpl)
                {
                    if (pred.r_() is LiteralExpr pr)
                    {
                        // a.a1 >= <const>
                        var stat = Catalog.sysstat_.GetColumnStat(bpl.relname_, pl.colName_);
                        return stat.EstSelectivity(pred.op_, pr.val_);
                    }
                }
            }

            return selectivity;
        }

        public static double EstSelectivity(this Expr filter)
        {
            Debug.Assert(filter.IsBoolean());

            double selectivity = 1.0;
            var andlist = filter.FilterToAndList();
            if (andlist.Count == 1)
                return EstSingleSelectivity(filter);
            else
            {
                foreach (var v in andlist)
                    selectivity *= EstSelectivity(v);
            }

            validSelectivity(selectivity);
            return selectivity;
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

        public override string Open(ExecContext context)
        {
            var tabName = (logic_ as LogicAnalyze).GetTargetTable().relname_;
            stats_ = Catalog.sysstat_.GetOrCreateTableStats(tabName);
            return null;
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

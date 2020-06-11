﻿/*
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
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using qpmodel;
using qpmodel.expr;
using qpmodel.logic;
using qpmodel.physic;
using qpmodel.sqlparser;

using Value = System.Object;
using TableColumn = System.Tuple<string, string>;

namespace qpmodel.stat
{
    class StatConst
    {
        public const double zero_ = 0.000000001;
        public const double one_ = 1.0;

        public const double epsilon_ = 0.001;
    }

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
    //
    // Besides these, there are V-optimal and MaxDiff histogram.
    // See Poosala et al SIGMOD 1996.
    //
    public class Historgram
    {
        public const int NBuckets_ = 100;

        public double depth_ { get; set; }
        public int nbuckets_ { get; set; }
        public Value[] buckets_ { get; set; } = new Value[NBuckets_ + 1];

        int whichBucket(Value val)
        {
            dynamic value = val;

            if (((dynamic)buckets_[0]).CompareTo(value) >= 0)
                return 0;
            for (int i = 0; i <= nbuckets_; i++)
            {
                // get the upper bound
                dynamic bound = buckets_[i];
                if (bound.CompareTo(value) > 0)
                    return i;
            }
            return nbuckets_ + 1;
        }
        double getFraction(Value lb, Value ub, Value val)
        {
            // string is not capable of comparing
            dynamic dlb = lb, dub = ub, dval = val;
            double frac;
            if (val is DateTime)
                frac = (double)((dval - dlb).Divide(dub - dlb));
            else
                frac = (double)(dval - dlb) / (double)(dub - dlb);
            Debug.Assert(frac >= 0 && frac < 1.0 + StatConst.epsilon_);
            return frac;
        }
        public double? EstSelectivity(string op, Value val)
        {
            // return the selectivity respect to only the histogram
            double selectivity = StatConst.one_;
            Debug.Assert(new List<String>() { ">", ">=", "<", "<=" }.Contains(op));

            int which = whichBucket(val);
            
            switch (op)
            {
                case ">":
                case ">=":
                    if (which == 0) selectivity = 1.0;
                    else if (which == nbuckets_ + 1) selectivity = 0.0;
                    else
                        selectivity = (1.0 * (nbuckets_ - which) 
                            + 1.0 - getFraction(buckets_[which - 1], buckets_[which], val)) / nbuckets_;
                    break;
                case "<":
                case "<=":
                    if (which == 0) selectivity = 0.0;
                    else if (which == nbuckets_ + 1) selectivity = 1.0;
                    else
                        selectivity = (1.0 * which - 1 + getFraction(buckets_[which - 1], buckets_[which], val))
                            / nbuckets_;
                    break;
            }

            Estimator.validateSelectivity(selectivity);
            return selectivity;
        }
    }

    public class MCVList
    {
        public const int NValues_ = 100;

        public int nvalues_ { get; set; }
        public Value[] values_ { get; set; } = new Value[NValues_];
        public double[] freqs_ { get; set; } = new double[NValues_];
        public double totalfreq_ { get; set; }
        public double otherfreq_ { get; set; } = 0.0;

        internal void validateThis()
        {
            Debug.Assert(nvalues_ <= NValues_);
            double total = 0;
            for (int i = 0; i < nvalues_; i++) total += freqs_[i];
            Debug.Assert(total <= 1 + StatConst.epsilon_);
            Debug.Assert(totalfreq_ <= 1 + StatConst.epsilon_);
        }
        int whichValue(Value val)
            => Array.IndexOf(values_, val);
        double calcTotalFreq(Value val, string op)
        {
            double totfreq = 0.0;
            dynamic value = val;
            for (int i = 0; i < NValues_; i++)
            {
                switch (op)
                {
                    case "<=":
                        if (((dynamic)values_[i]) <= value) totfreq += freqs_[i];
                        break;
                    case "<":
                        if (((dynamic)values_[i]) < value) totfreq += freqs_[i];
                        break;
                    case ">=":
                        if (((dynamic)values_[i]) >= value) totfreq += freqs_[i];
                        break;
                    case ">":
                        if (((dynamic)values_[i]) > value) totfreq += freqs_[i];
                        break;
                }
            }
            return totfreq;
        }
        public double? EstSelectivity(string op, Value val)
        {
            if (!new List<String>() { "=", ">", ">=", "<", "<=" }.Contains(op))
                return null;
            
            if (op == "=")
            {
                int which = whichValue(val);
                if (which == -1) return otherfreq_;
                return freqs_[which];
            }
            
            double selectivity = calcTotalFreq(val, op);
            Estimator.validateSelectivity(selectivity);

            return selectivity;
        }
    }

    // Per column statistics
    //  PostgreSQL maintains most common values (mcv) for frequent values and also histgoram for less-frequent values.
    //  Example: say a a table has 
    //   [1..10]*10, [11..190], [191..200]*2, [201..300] that is 100+180+20+100=400 rows.
    //   - mcv: [1..10; 191..200] with freq: [0.025..0.025; 0.005..0.005] where 0.025 = 10/400, 0.005 = 2/400
    //   - historgram for range [11..190; 201..300]
    //  In this way, it can capture small distinct value set and also large distinct value set.
    //
    public class ColumnStat
    {
        public ulong n_rows_ { get; set; }        // number of rows
        public double nullfrac_ { get; set; }    // null value percentage
        public ulong n_distinct_ { get; set; }
        public Historgram hist_ { get; set; }    // value historgram
        public MCVList mcv_ { get; set; }

        public ColumnStat() { }

        public void ComputeStats(int index, List<Row> samples)
        {
            int nNulls = 0;
            List<Value> values = new List<Value>();
            foreach (var r in samples)
            {
                Value val = r[index];
                if (val is null) 
                {
                    nNulls++;
					continue;
                }

                values.Add(val);
            }

            n_distinct_ = (ulong)values.Distinct().Count();
            // initialize mcv whenever the attr is not unique key
            if (n_distinct_ < (ulong)values.Count())
            {
                mcv_ = new MCVList();
                var groups = from value in values group value by value into newGroup select newGroup;
                
                Dictionary<Value, int> sortgroup = new Dictionary<Value, int>();
                foreach (var g in groups)
                    sortgroup.Add(g.Key, g.Count());
                
                var sorted = from pair in sortgroup orderby pair.Value descending select pair;
                mcv_.nvalues_ = (int)Math.Min(n_distinct_, MCVList.NValues_);

                int i = 0;
                double freq = 0.0;
                foreach (var g in sorted)
                {
                    mcv_.values_[i] = g.Key ;
                    mcv_.freqs_[i] = (1.0 * g.Value) / values.Count();
                    freq += mcv_.freqs_[i];
                    i++;
                    if (i >= mcv_.nvalues_) break;
                }
                if (n_distinct_ > (ulong)mcv_.nvalues_)
                {
                    Debug.Assert(freq > 0 && freq < 1 + StatConst.epsilon_);
                    mcv_.otherfreq_ = (1.0 - freq) / (n_distinct_ - (ulong)mcv_.nvalues_);
                    mcv_.totalfreq_ = freq;
                }
                else
                {
                    mcv_.otherfreq_ = 0;
                    mcv_.totalfreq_ = StatConst.one_;
                }

                mcv_.validateThis();

                // remove all values present in mcv
                values.RemoveAll(x => mcv_.values_.Contains(x));
            }
            // initialize histogram unless all values in mcv
            if (values.Count > 0)
            {
                // now sort the values and create equal-depth historgram
                values.Sort();
                int nbuckets = Math.Min(Historgram.NBuckets_, values.Count);
                double depth = ((double)values.Count) / nbuckets;
                Debug.Assert(depth >= 1);

                hist_ = new Historgram();
                for (int i = 0; i < nbuckets + 1; i++)
                    hist_.buckets_[i] = values[Math.Min((int)(i * depth), values.Count - 1)];
                hist_.depth_ = depth;
                hist_.nbuckets_ = nbuckets;
            }

            // finalize the stats
            n_rows_ = (ulong)samples.Count;
            Debug.Assert(nNulls <= samples.Count);
            if (samples.Count != 0)
                nullfrac_ = nNulls / samples.Count;
        }

        // Follow PostgreSQL's heuristic for LIKE: 
        //      %,_ increase selectivity and other char reduce it
        //
        double EstLikeSelectivity(Value val)
        {
            const double FIXED_CHAR_SEL = 0.2;
            const double FULL_WILDCARD_SEL = 5.0;
            const double ANY_CHAR_SEL = 0.9;

            Debug.Assert(val is string);
            string str = val as string;

            double sel = Math.Pow(FULL_WILDCARD_SEL, str.Count(x => x == '%'));
            sel *= Math.Pow(ANY_CHAR_SEL, str.Count(x => x == '_'));
            sel *= Math.Pow(FIXED_CHAR_SEL, str.Count(x => x != '_' && x != '%'));
            if (sel > 1)
                sel = StatConst.one_;
            return sel;
        }

        public double EstSelectivity(string op, Value val)
        {
            if (op == "like")
                return EstLikeSelectivity(val);
            if (!new List<String>() { "=", ">", ">=", "<", "<=" }.Contains(op))
                return StatConst.one_;

            if (mcv_ is null) 
            {
                if (hist_ is null)
                    return StatConst.one_;
                if (op == "=") // unique
                    return 1.0 / n_rows_;
                else
                    return hist_.EstSelectivity(op, val) ?? StatConst.one_;
            }
            else
            {
                if (op == "=")
                    return mcv_.EstSelectivity(op, val) ?? StatConst.one_;
                else
                    return (mcv_.EstSelectivity(op, val) ?? StatConst.one_)
                        + (1 - mcv_.totalfreq_) * (hist_?.EstSelectivity(op, val) ?? StatConst.one_);
            }
        }
        public ulong EstDistinct()
        {
            Debug.Assert(n_distinct_ >= 0);
            return Math.Max(1, n_distinct_);
        }
    }

    public static class Estimator
    {
        public static void validateSelectivity(double selectivity)
        {
            Debug.Assert(selectivity >= 0 && selectivity <= 1.0 + StatConst.epsilon_);
        }

        static double EstSingleSelectivity(Expr filter)
        {
            double selectivity = 1.0;
            Debug.Assert(filter.FilterToAndOrList().Count == 1);

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
            if (filter is InListExpr inPred)
            {
                // implementation of IN estimation
                if (inPred.children_[0] is ColExpr pl && pl.tabRef_ is BaseTableRef bpl)
                {
                    selectivity = 0.0;
                    var stat = Catalog.sysstat_.GetColumnStat(bpl.relname_, pl.colName_);
                    for (int i = 1; i < inPred.children_.Count; ++i)
                    {
                        if (inPred.children_[i] is LiteralExpr pr)
                            selectivity += stat.EstSelectivity("=", pr.val_);
                    }
                    return Math.Min(1.0, selectivity);
                }
            }
            return selectivity;
        }

        // problems:
        //  1. same column correlation: a1 > 5 && a1 <= 5 (say a1 in [1,10])
        //     we can't use sel(a1>5) * sel(a1<=5) which gives 0.25
        //  2. different column correlation:  country='US' and continent='North America'
        //   
        static ColExpr ExtractColumn(Expr filter)
        {
            if (filter is BinExpr pred)
                if (pred.l_() is ColExpr pl && pl.tabRef_ is BaseTableRef bpl)
                    if (pred.r_() is LiteralExpr pr && new List<String>() { "=", ">", ">=", "<", "<=" }.Contains(pred.op_))
                        return pl;
            return null;
        }
        static double EstColumnSelectivity(List<double> listsel, bool isAnd)
        {
            double selectivity = isAnd ? 1.0 : 0.0;
            double backupsel = 1.0;
            foreach (double sel in listsel)
            {
                selectivity = isAnd ? selectivity - (1.0 - sel) : selectivity + sel;
                backupsel *= sel;
            }
            if (!isAnd) selectivity = Math.Min(selectivity, 1.0);
            if (selectivity < 0 || selectivity > 1.0) selectivity = backupsel;
            return selectivity;
        }
        public static double EstSelectivity(this Expr filter)
        {
            var andorlist = filter.FilterToAndOrList();
            double selectivity = filter is LogicAndExpr ? 1.0 : 0.0;
            if (andorlist.Count == 1)
                return EstSingleSelectivity(filter);
            else
            {
                // combine simple expressions of the same column
                Dictionary<ColExpr, List<double>> colselcombine = new Dictionary<ColExpr, List<double>>();
                foreach (var v in andorlist)
                {
                    ColExpr col = ExtractColumn(v);
                    if (!(col is null))
                    {
                        if (colselcombine.ContainsKey(col))
                            colselcombine[col].Add(EstSelectivity(v));
                        else
                            colselcombine.Add(col, new List<double> { EstSelectivity(v) });
                    }
                    else
                    {
                        double vsel = EstSelectivity(v);
                        selectivity = filter is LogicAndExpr ? selectivity * vsel : selectivity + vsel - vsel * selectivity;
                    }
                }
                foreach (var colexpr in colselcombine)
                {
                    double csel = EstColumnSelectivity(colexpr.Value, filter is LogicAndExpr);
                    selectivity = filter is LogicAndExpr ? selectivity * csel : selectivity + csel - csel * selectivity;
                }
            }

            validateSelectivity(selectivity);
            return selectivity;
        }
    }

    // format: (tableName, colName):key, column stat
    public class SysStats : SystemTable
    {
        readonly Dictionary<string, ColumnStat> records_ = new Dictionary<string, ColumnStat>();

        public void AddOrUpdate(string tabName, string colName, ColumnStat stat)
        {
            string tabcol = tabName + colName;
            SysStatsAddOrUpdate(tabcol, stat);
        }

        void SysStatsAddOrUpdate(string tabcol, ColumnStat stat)
        {
            if (RetrieveColumnStat(tabcol) is null)
                records_.Add(tabcol, stat);
            else
                records_[tabcol] = stat;
        }


        public ColumnStat GetColumnStat(string tabName, string colName)
        {
            return RetrieveColumnStat(tabName + colName);
        }

        ColumnStat RetrieveColumnStat(string tabcol)
        {
            if (records_.TryGetValue(tabcol, out ColumnStat value))
                return value;
            return null;
        }


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

        public void ComputeStats(List<Row> samples, List<ColumnStat> stats)
        {
            // A full row is presented here, since we generate per column 
            // stats and full row needed for correlation analysis
            for (int i = 0; i < stats.Count; i++)
            {
                stats[i].ComputeStats(i, samples);
            }
        }

        // stats getters
        public ulong EstCardinality(string tabName)
        {
            return GetOrCreateTableStats(tabName)[0].n_rows_;
        }

        public dynamic ExtractValue(JsonElement value)
        {
            string date_pattern = @"\d\d\d\d\-\d\d\-\d\dT\d\d\:\d\d\:\d\d";
            dynamic candidate_value;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    candidate_value = (string)value.GetString();
                    Match result = Regex.Match(candidate_value, date_pattern);

                    if (result.Success)
                        return DateTime.Parse(candidate_value);
                    else
                        return candidate_value;

                case JsonValueKind.Number:
                    double double_candidate;
                    int int_candidate;

                    if (value.TryGetInt32(out int_candidate))
                        return int_candidate;
                    if (value.TryGetDouble(out double_candidate))
                        return double_candidate;
                    else
                        return null;

                default:
                    return null;
            }
        }

        public void jsonPostProcess(ColumnStat stat)
        {

            if (stat.hist_ != null)
            {
                for (int i = 0; i < stat.hist_.buckets_.Length; i++)
                {
                    stat.hist_.buckets_[i] = ExtractValue((JsonElement)stat.hist_.buckets_[i]);
                }
            }

            if (stat.mcv_ != null && stat.mcv_.nvalues_ != 0)
            {
                int i = 0;
                while (stat.mcv_.values_[i] != null)
                {
                    stat.mcv_.values_[i] = ExtractValue((JsonElement)stat.mcv_.values_[i]);
                    i++;
                }
            }
        }

        public void read_serialized_stats(string statsFn)
        {
            string jsonStr = File.ReadAllText(statsFn);
            Dictionary<string, ColumnStat> records;

            string trimmedJsonStr = Regex.Replace(jsonStr, "\\n", "");
            trimmedJsonStr = Regex.Replace(trimmedJsonStr, "\\r", "");

            //Catalog.sysstat_ = JsonSerializer.Deserialize<Dictionary<string, ColumnStat>>(jsonStr);
            records = JsonSerializer.Deserialize<Dictionary<string, ColumnStat>>(trimmedJsonStr);

            foreach (KeyValuePair<string, ColumnStat> elem in records)
            {
                jsonPostProcess(elem.Value);
                SysStatsAddOrUpdate(elem.Key, elem.Value);
            }
        }

        public void seriaize_and_write(string stats_output_fn)
        {
            var serial_stats_fmt = JsonSerializer.Serialize<Dictionary<string, ColumnStat>>(records_);

            File.WriteAllText(stats_output_fn, serial_stats_fmt);
        }
    }

    public class LogicAnalyze : LogicNode
    {

        public LogicAnalyze(LogicNode child) => children_.Add(child);

        public BaseTableRef GetTargetTable()
        {
            // its child is a [gather ontop] scan
            var child = child_();

            Debug.Assert(child is LogicGather ||
                         child is LogicScanTable  ||
                         child is LogicSampleScan);

            if (child is LogicGather || child is LogicSampleScan)
                child = child.child_();
            return (child as LogicScanTable).tabref_;
        }
    }

    public class PhysicAnalyze : PhysicNode
    {
        internal List<ColumnStat> stats_;

        public PhysicAnalyze(LogicAnalyze logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string Open(ExecContext context)
        {
            base.Open(context);
            var tabName = (logic_ as LogicAnalyze).GetTargetTable().relname_;
            stats_ = Catalog.sysstat_.GetOrCreateTableStats(tabName);
            return null;
        }

        public override string Exec(Func<Row, string> callback)
        {
            List<Row> samples = new List<Row>();
            child_().Exec(r =>
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

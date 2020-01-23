using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using adb;
using adb.expr;
using adb.logic;
using adb.physic;
using adb.sqlparser;

using Value = System.Object;
using TableColumn = System.Tuple<string, string>;

namespace adb.stat
{
    class StatConst 
    {
        public const double sel_zero = 0.000000001;
        public const double sel_one = 1.0;

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
    public class Historgram {
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
            double selectivity = StatConst.sel_one;

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
                selectivity = StatConst.sel_zero;
            Estimator.validSelectivity(selectivity);
            return selectivity;
        }
    }

    public class MCVList {
        public const int NValues_ = 100;

        public int nvalues_;
        public Value[] values_ = new Value[NValues_];
        public double[] freqs_ = new double[NValues_];

        internal void Validate()
        {
            Debug.Assert(nvalues_ <= NValues_);
            double total = 0;
            for (int i = 0; i < nvalues_; i++) total += freqs_[i];
            Debug.Assert(total <= 1 + StatConst.epsilon_);
        }

        int whichValue(Value val) {
            return Array.IndexOf(values_, val);
        }
        public double EstSelectivity(string op, Value val)
        {
            if (!new List<String>() { "=", ">", ">=", "<", "<=" }.Contains(op))
                return StatConst.sel_one;

            int which = whichValue(val);
            if (which == -1)
                return StatConst.sel_zero;

            double selectivity = 0.0;
            switch (op)
            {
                case "=":
                    selectivity = freqs_[which];
                    break;
                case ">":
                case ">=":
                    int start = which;
                    for (int i = start; i < nvalues_; i++)
                        selectivity += freqs_[i];
                    break;
                case "<":
                case "<=":
                    int end = which;
                    for (int i = 0; i <= end; i++)
                        selectivity += freqs_[i];
                    break;
            }

            if (selectivity == 0)
                selectivity = StatConst.sel_zero;
            Estimator.validSelectivity(selectivity);
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
        public long n_rows_;                // number of rows
        public double nullfrac_;            // null value percentage
        public long n_distinct_;
        public Historgram hist_; // value historgram
        public MCVList mcv_;

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

            n_distinct_ = values.Distinct().Count();
            if (n_distinct_ <= MCVList.NValues_)
            {
                mcv_ = new MCVList();
                var groups = from value in values group value by value into newGroup orderby newGroup.Key select newGroup;
                int i = 0;
                foreach (var g in groups) {
                    mcv_.values_[i] = g.Key;
                    mcv_.freqs_[i] = (1.0 * g.Count())/values.Count();
                    i++;
                }
                mcv_.nvalues_ = i;
                mcv_.Validate();
            }
            else
            {
                // now sort the values and create equal-depth historgram
                values.Sort();
                int nbuckets = Math.Min(Historgram.NBuckets_, values.Count);
                int depth = values.Count / nbuckets;
                Debug.Assert(depth >= 1);

                hist_ = new Historgram();
                for (int i = 0; i < nbuckets; i++)
                {
                    hist_.buckets_[i] = values[(i + 1) * depth - 1];
                    hist_.distincts_[i] = values.GetRange(i * depth, depth).Distinct().Count();
                    Debug.Assert(hist_.distincts_[i] > 0);
                }
                hist_.depth_ = depth;
                hist_.nbuckets_ = nbuckets;
            }

            // finalize the stats
            n_rows_ = samples.Count;
            Debug.Assert(nNulls <= samples.Count);
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
                sel = StatConst.sel_one;
            return sel;
        }

        public double EstSelectivity(string op, Value val)
        {
            if (op == "like")
                return EstLikeSelectivity(val);
            if (mcv_ != null)
                return mcv_.EstSelectivity(op, val);
            else if (hist_ != null)
                return hist_.EstSelectivity(op, val);
            else {
                // only wild guess now
                return StatConst.sel_one;
            }
        }
        public long EstDistinct()
        {
            Debug.Assert(n_distinct_ >= 0);
            return Math.Max(1, n_distinct_);
        }
    }

    public static class Estimator
    {
        public static void validSelectivity(double selectivity)
        {
            Debug.Assert(selectivity > 0 && selectivity <= 1.0 + StatConst.epsilon_);
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

        // problems:
        //  1. same column correlation: a1 > 5 && a1 <= 5 (say a1 in [1,10])
        //     we can't use sel(a1>5) * sel(a1<=5) which gives 0.25
        //  2. different column correlation:  country='US' and continent='North America'
        //   
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

    // format: (tableName, colName):key, column stat
    public class SysStats : SystemTable {
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
        public long EstCardinality(string tabName) {
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

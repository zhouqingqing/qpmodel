using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

namespace adb
{
    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        internal LogicNode logic_;
        internal PhysicProfiling profile_;

        internal double cost_ = double.NaN;

        protected PhysicNode(LogicNode logic)
        {
            // logic node shall not be null unless it is PhysicCollect
            Debug.Assert(logic is null == this is PhysicCollect);
            // logic node shall not memoref unless it is physical memoref
            Debug.Assert(logic is LogicMemoRef == this is PhysicMemoRef);
            logic_ = logic;
        }

        public override string PrintOutput(int depth) => logic_.PrintOutput(depth);
        public override string PrintInlineDetails(int depth) => logic_.PrintInlineDetails(depth);
        public override string PrintMoreDetails(int depth) => logic_.PrintMoreDetails(depth);

        public virtual void Open() => children_.ForEach(x => x.Open());
        public virtual void Close() => children_.ForEach(x => x.Close());
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract void Exec(ExecContext context, Func<Row, string> callback);

        internal Row ExecProject(ExecContext context, Row input)
        {
            var output = logic_.output_;
            Row r = new Row(output.Count);
            for (int i = 0; i < output.Count; i++)
                r[i] = output[i].Exec(context, input);

            return r;
        }

        public virtual double Cost() {
            if (double.IsNaN(cost_))
                cost_ = 10.0;
            return cost_;
        }
    }

    // PhysicMemoRef wrap a LogicMemoRef as a physical node (so LogicMemoRef can be 
    // used in physical tree). Actually we only need LogicMemoRef's memo group.
    //
    public class PhysicMemoRef : PhysicNode
    {
        public PhysicMemoRef(LogicNode logic) : base(logic) { Debug.Assert(logic is LogicMemoRef); }
        public override string ToString() => Logic().ToString();

        public override void Exec(ExecContext context, Func<Row, string> callback) => throw new InvalidProgramException("not executable");
        public override int GetHashCode() => Group().memoid_;
        public override bool Equals(object obj)
        {
            if (obj is PhysicMemoRef lo)
                return Logic().MemoLogicSign() == (lo.logic_ as LogicMemoRef).MemoLogicSign();
            return false;
        }

        public LogicMemoRef Logic() => logic_ as LogicMemoRef;
        internal CMemoGroup Group() => Logic().group_;
        internal double MinCost() => Group().FindMinCostOfGroup();
        public override string PrintMoreDetails(int depth)
        {
            // we want to see what's underneath
            return $"{{{Logic().PrintMoreDetails (depth + 1)}}}";
        }
    }


    public class PhysicScanTable : PhysicNode
    {
        public PhysicScanTable(LogicNode logic) : base(logic) { }
        public override string ToString() => $"PScan({(logic_ as LogicScanTable).tabref_}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var heap = (logic.tabref_).Table().heap_.GetEnumerator();

            Row r = null;
            for (; ; )
            {
                if (heap.MoveNext())
                    r = heap.Current;
                else
                    break;

                if (logic.tabref_.outerrefs_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || filter.Exec(context, r) is true)
                {
                    r = ExecProject(context, r);
                    callback(r);
                }
            }
        }

        public override double Cost()
        {
            var logic = (logic_) as LogicScanTable;
            return logic.EstCardinality() * 1;
        }
    }

    public class PhysicScanFile : PhysicNode
    {
        public PhysicScanFile(LogicNode logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanFile;
            var filename = logic.FileName();
            var columns = logic.tabref_.baseref_.Table().ColumnsInOrder();
            Utils.ReadCsvLine(filename, fields =>
            {
                Row r = new Row(fields.Length);

                int i = 0;
                Array.ForEach(fields, f =>
                {
                    if (f == "") {
                        r[i] = null;
                    }
                    else
                    {
                        switch (columns[i].type_)
                        {
                            case IntType it:
                                r[i] = int.Parse(f);
                                break;
                            case DateTimeType dt:
                                r[i] = DateTime.Parse(f);
                                break;
                            case DoubleType bt:
                                r[i] = Double.Parse(f);
                                break;
                            default:
                                r[i] = f;
                                break;
                        }
                    }

                    i++;
                });
                Debug.Assert(r.ColCount() == columns.Count);

                callback(r);
            });
        }
    }

    public class PhysicNLJoin : PhysicNode
    {
        public PhysicNLJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PNLJ({l_()},{r_()}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var filter = logic.filter_;
            bool semi = type == JoinType.SemiJoin;
            bool antisemi = type == JoinType.AntiSemiJoin;

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    if (!semi || !foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter is null || filter.Exec(context, n) is true)
                        {
                            foundOneMatch = true;
                            if (!antisemi)
                            {
                                n = ExecProject(context, n);
                                callback(n);
                            }
                        }
                    }
                    return null;
                });

                if (antisemi && !foundOneMatch)
                {
                    Row n = new Row(l, null);
                    n = ExecProject(context, n);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = ((l_() as PhysicMemoRef).Logic().EstCardinality() + 10) * 
                    ((r_() as PhysicMemoRef).Logic().EstCardinality() + 10);
            return cost_;
        }
    }

    // Key list is a special row
    internal class KeyList: Row
    {
        public KeyList(int length): base(length){}

        static internal KeyList ComputeKeys(ExecContext context, List<Expr> keys, Row input)
        {
            if (keys != null) {
                var list = new KeyList(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                    list[i] = keys[i].Exec(context, input);
                return list;
            }
            return new KeyList(0);
        }
    };

    public class PhysicHashJoin : PhysicNode
    {
        // ab join cd on c1+d1=a1-b1 and a1+b1=c2+d2;
        //    leftKey_:  a1-b1, a1+b1
        //    rightKey_: c1+d1, c2+d2
        //
        internal List<Expr> leftKeys_ = new List<Expr>();
        internal List<Expr> rightKeys_ = new List<Expr>();

        public PhysicHashJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            Debug.Assert(logic.filter_ != null);
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PHJ({l_()},{r_()}: {Cost()})";

        void getOneKeyList(BinExpr fb) {
            Debug.Assert(fb.op_ == "=");
            var ltabrefs = l_().logic_.InclusiveTableRefs();
            var rtabrefs = r_().logic_.InclusiveTableRefs();
            var lkeyrefs = fb.l_().tableRefs_;
            var rkeyrefs = fb.r_().tableRefs_;
            if (Utils.ListAContainsB(ltabrefs, lkeyrefs))
            {
                leftKeys_.Add(fb.l_());
                rightKeys_.Add(fb.r_());
            }
            else
            {
                // switch side
                Debug.Assert(Utils.ListAContainsB(rtabrefs, lkeyrefs));
                leftKeys_.Add(fb.r_());
                rightKeys_.Add(fb.l_());
            }

            Debug.Assert(leftKeys_.Count == rightKeys_.Count);
        }

        void getKeyList()
        {
            var filter = (logic_ as LogicJoin).filter_;

            Debug.Assert(filter != null);
            var andlist = FilterHelper.FilterToAndList(filter);
            foreach (var v in andlist)
            {
                Debug.Assert(v is BinExpr);
                getOneKeyList(v as BinExpr);
            }
        }

        public override void Open()
        {
            // get the left side and right side key list
            Debug.Assert(leftKeys_.Count == 0);
            getKeyList();
            base.Open();
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var hm = new Dictionary<KeyList, List<Row>>();
            bool semi = type == JoinType.SemiJoin;
            bool antisemi = type == JoinType.AntiSemiJoin;

            // build hash table with left side 
            l_().Exec(context, l => {
                var keys = KeyList.ComputeKeys(context, leftKeys_, l);

                if (hm.TryGetValue(keys, out List<Row> exist))
                {
                    exist.Add(l);
                }
                else
                {
                    List<Row> rows = new List<Row>();
                    rows.Add(l);
                    hm.Add(keys, rows);
                }
                return null;
            });

            // right side probes the hash table
            r_().Exec(context, r =>
            {
                Row fakel = new Row(l_().logic_.output_.Count);
                Row n = new Row(fakel, r);
                var keys = KeyList.ComputeKeys(context, rightKeys_, n);
                bool foundOneMatch = false;

                if (hm.TryGetValue(keys, out List<Row> exist))
                {
                    foundOneMatch = true;
                    foreach (var v in exist)
                    {
                        n = ExecProject(context, new Row(v, r));
                        callback(n);
                        if (semi)
                            break;
                    }
                }
                else
                {
                    // no match
                    if (antisemi && !foundOneMatch)
                    {
                        n = new Row(null, r);
                        n = ExecProject(context, n);
                        callback(n);
                    }
                }
                return null;
            });
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_() as PhysicMemoRef).Logic().EstCardinality() * 2 
                    + (r_() as PhysicMemoRef).Logic().EstCardinality();
            return cost_;
        }
    }

    public class PhysicHashAgg : PhysicNode
    {
        public PhysicHashAgg(LogicAgg logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PHAgg({(logic_ as LogicAgg)}: {Cost()})";

        private Row AggrCoreToRow(ExecContext context, Row input)
        {
            var aggfns = (logic_ as LogicAgg).aggrFns_;
            Row r = new Row(aggfns.Count);
            return r;
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;
            var hm = new Dictionary<KeyList, Row>();

            // aggregation is working on aggCore targets
            child_().Exec(context, l =>
            {
                var keys = KeyList.ComputeKeys(context, logic.keys_, l);

                if (hm.TryGetValue(keys, out Row exist))
                {
                    for (int i = 0; i < aggrcore.Count; i++)
                    {
                        var old = exist[i];
                        var newval = aggrcore[i].Accum(context, old, l);
                        exist[i] = newval;
                    }
                }
                else
                {
                    hm.Add(keys, AggrCoreToRow(context, l));
                    exist = hm[keys];
                    for (int i = 0; i<aggrcore.Count; i++)
                    {
                        var initval = aggrcore[i].Init(context, l);
                        exist[i] = initval;
                    }
                }

                return null;
            });

            // stitch keys+aggcore into final output
            foreach (var v in hm)
            {
                var keys = v.Key;
                Row row = v.Value;
                for (int i = 0; i < aggrcore.Count; i++)
                {
                    if (aggrcore[i] is AggAvg)
                    {
                        var old = row[i];
                        var newval = (old as AggAvg.AvgPair).Compute();
                        row[i] = newval;
                    }
                }
                var w = new Row(keys, row);
                var newr = ExecProject(context, w);
                callback(newr);
            }
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);

        // respect logic.orders_|descends_
        private int compareRow(Row l, Row r)
        {
            var logic = logic_ as LogicOrder;
            var orders = logic.orders_;
            var descends = logic.descends_;

            return l.CompareTo(r);
        }
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicOrder;
            var set = new List<Row>();
            child_().Exec(context, l =>
            {
                set.Add(l);
                return null;
            });
            set.Sort(compareRow);

            // output sorted set
            foreach (var v in set)
                callback(v);
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        public override string ToString() => $"PFrom({(logic_ as LogicFromQuery)}: {Cost()})";

        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicFromQuery;
            child_().Exec(context, l =>
            {
                if (logic.queryRef_.outerrefs_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(context, l);
                callback(r);
                return null;
            });
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string ToString() => $"PFILTER({child_()}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicFilter).filter_;

            child_().Exec(context, l =>
            {
                if (filter is null || filter.Exec(context, l) is true)
                {
                    var r = ExecProject(context, l);
                    callback(r);
                }
                return null;
            });
        }
        public override int GetHashCode()
        {
            Expr filter = (logic_ as LogicFilter).filter_;
            return base.GetHashCode() ^ (filter?.GetHashCode() ?? 0);
        }
        public override bool Equals(object obj)
        {
            Expr filter = (logic_ as LogicFilter).filter_;
            if (obj is PhysicFilter lo)
            {
                return base.Equals(lo) && (filter?.Equals((lo.logic_ as LogicFilter)?.filter_) ?? true);
            }
            return false;
        }
    }

    public class PhysicInsert : PhysicNode
    {
        public PhysicInsert(LogicInsert logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                var table = (logic_ as LogicInsert).targetref_.Table();
                table.heap_.Add(l);
                return null;
            });
        }
    }

    public class PhysicResult : PhysicNode
    {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Row r = ExecProject(context, null);
            callback(r);
        }
    }

    public class PhysicProfiling : PhysicNode
    {
        internal Int64 nrows_;

        public override string ToString() => $"{child_()}";

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                nrows_++;
                callback(l);
                return null;
            });
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child): base(null) => children_.Add(child);
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var child = (child_() is PhysicProfiling) ?
                    child_().child_() : child_();
            var output = child.logic_.output_;
            var ncolumns = output.Count(x => x.isVisible_);

            context.Reset();
            child_().Exec(context, r =>
            {
                Row newr = new Row(ncolumns);
                for (int i = 0; i < output.Count; i++)
                {
                    if (output[i].isVisible_)
                        newr[i] = r[i];
                }
                rows_.Add(newr);
                Console.WriteLine($"{newr}");
                return null;
            });
        }
    }
}

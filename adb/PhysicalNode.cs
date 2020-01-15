using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

namespace adb
{
    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        public LogicNode logic_;
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

        public virtual string Open(ExecContext context){
            string s = ""; children_.ForEach(x => s += x.Open(context)); return s;
        }
        public virtual string Close(ExecContext context) {
            string s = ""; children_.ForEach(x => s += x.Close(context)); return s;
        }
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract string Exec(ExecContext context, Func<Row, string> callback);

        public Row ExecProject(ExecContext context, Row input)
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

        // local varialbes are named locally by append the local hmid. Record r passing
        // across callback boundaries are special: they have to be uniquely named and
        // use consistently.
        //
        internal string nameRbeforeCall() { return $"_r_{ObjectID.newId()}"; }
        internal string getRofCallback() { return $"_r_{ObjectID.curId()}"; }

        internal string codegen_logic_ = "<unset logic node name>";
        internal string codegen_this_ = "<unset physic node name>";
    }

    // PhysicMemoRef wrap a LogicMemoRef as a physical node (so LogicMemoRef can be 
    // used in physical tree). Actually we only need LogicMemoRef's memo group.
    //
    public class PhysicMemoRef : PhysicNode
    {
        public PhysicMemoRef(LogicNode logic) : base(logic) { Debug.Assert(logic is LogicMemoRef); }
        public override string ToString() => Logic().ToString();

        public override string Exec(ExecContext context, Func<Row, string> callback) => throw new InvalidProgramException("not executable");
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

        public override string Open(ExecContext context)
        {
            if (!context.option_.optimize_.use_codegen_)
                return null;

            var logic = logic_ as LogicScanTable;
            var tabalias = logic.tabref_.alias_;
            codegen_logic_ = $"{logic.GetType().Name}_{tabalias}";
            codegen_this_ = $"{this.GetType().Name}_{tabalias}";
            string cs = $@"
                BaseTableRef tabref = new BaseTableRef(""{logic.tabref_.relname_}"");
                LogicScanTable {codegen_logic_} = new LogicScanTable(tabref);
                PhysicScanTable {codegen_this_}= new PhysicScanTable({codegen_logic_});
                ";
            return cs;
        }

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var heap = (logic.tabref_).Table().heap_.GetEnumerator();

            if (context.option_.optimize_.use_codegen_)
            {
                string cs = $@"
                var logic = {codegen_logic_} as LogicScanTable;
                var filter = logic.filter_;
                var heap = (logic.tabref_).Table().heap_.GetEnumerator();

                for (; ; )
                {{
                    Row r = null;
                    if (context.stop_)
                        break;

                    if (heap.MoveNext())
                        r = heap.Current;
                    else
                        break;

                    if (filter is null || filter.Exec(context, r) is true)
                    {{
                        r = {codegen_this_}.ExecProject(context, r);
                        {callback(null)};
                    }}
                }}";

                return cs;
            }
            else
            {
                Row r = null;
                for (; ; )
                {
                    if (context.stop_)
                        break;

                    if (heap.MoveNext())
                        r = heap.Current;
                    else
                        break;

                    if (logic.tabref_.colRefedBySubq_.Count != 0)
                        context.AddParam(logic.tabref_, r);
                    if (filter is null || filter.Exec(context, r) is true)
                    {
                        r = ExecProject(context, r);
                        callback(r);
                    }
                }

                return null;
            }
        }

        public override double Cost()
        {
            var logic = (logic_) as LogicScanTable;
            return logic.EstCardinality() * 1;
        }
    }

    public class PhysicSeekIndex : PhysicNode
    {
        public PhysicSeekIndex(LogicNode logic) : base(logic) { }
        public override string ToString() => $"ISeek({(logic_ as LogicScanTable).tabref_}: {Cost()})";

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var index = (logic.tabref_).Table().indexes_[0].index_;

            bool ok = (filter as BinExpr).r_().TryEvalConst(out Value searchval);
            Debug.Assert(ok);
            KeyList key = new KeyList(1);
            key[0] = searchval;
            var r = index.SearchUnique(key);
            if (r != null)
            {
                if (logic.tabref_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || filter.Exec(context, r) is true)
                {
                    r = ExecProject(context, r);
                    callback(r);
                }
            }

            return null;
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

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanFile;
            var filename = logic.FileName();
            var columns = logic.tabref_.baseref_.Table().ColumnsInOrder();
            Utils.ReadCsvLine(filename, fields =>
            {
                Row r = new Row(fields.Length);

                if (context.stop_)
                    return;

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

            return null;
        }
    }

    public class PhysicNLJoin : PhysicNode
    {
        public PhysicNLJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PNLJ({l_()},{r_()}: {Cost()})";

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var filter = logic.filter_;
            bool semi = type == JoinType.SemiJoin;
            bool antisemi = type == JoinType.AntiSemiJoin;

            l_().Exec(context, l =>
            {
                if (context.stop_)
                    return null;
                
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
            return null;
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
            if (ltabrefs.ContainsList(lkeyrefs))
            {
                leftKeys_.Add(fb.l_());
                rightKeys_.Add(fb.r_());
            }
            else
            {
                // switch side
                Debug.Assert(rtabrefs.ContainsList(lkeyrefs));
                leftKeys_.Add(fb.r_());
                rightKeys_.Add(fb.l_());
            }

            Debug.Assert(leftKeys_.Count == rightKeys_.Count);
        }

        void getKeyList()
        {
            var filter = (logic_ as LogicJoin).filter_;

            Debug.Assert(filter != null);
            var andlist = filter.FilterToAndList();
            foreach (var v in andlist)
            {
                Debug.Assert(v is BinExpr);
                getOneKeyList(v as BinExpr);
            }
        }

        public override string Open(ExecContext context)
        {
            // get the left side and right side key list
            Debug.Assert(leftKeys_.Count == 0);
            getKeyList();
            base.Open(context);
            return null;
        }

        public override string Exec(ExecContext context, Func<Row, string> callback)
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
            if (hm.Count == 0)
                return null;
            r_().Exec(context, r =>
            {
                if (context.stop_)
                    return null;

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

            return null;
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

        //  special case: when there is no key and hm is empty, we shall still return one row
        //    count: 0, avg/min/max/sum: null
        //    but HAVING clause can filter this row out.
        //
        Row AggrHandleEmptyResult(ExecContext context)
        {
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;

            Row aggvals = new Row(aggrcore.Count);
            for (int i = 0; i < aggrcore.Count; i++)
            {
                aggvals[i] = null;
                if (aggrcore[i] is AggCount || aggrcore[i] is AggCountStar)
                {
                    aggvals[i] = 0;
                }
            }
            var w = new Row(null, aggvals);
            if (logic.having_ is null || logic.having_.Exec(context, w) is true)
            {
                var newr = ExecProject(context, w);
                return newr;
            }
            return null;
        }

        public override string Exec(ExecContext context, Func<Row, string> callback)
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
            if (logic.keys_ is null && hm.Count == 0) {
                Row row = AggrHandleEmptyResult(context);
                if (row != null)
                    callback(row);
            }
            foreach (var v in hm)
            {
                if (context.stop_)
                    break;

                var keys = v.Key;
                Row aggvals = v.Value;
                for (int i = 0; i < aggrcore.Count; i++)
                {
                    if (aggrcore[i] is AggAvg)
                    {
                        var old = aggvals[i];
                        var newval = (old as AggAvg.AvgPair).Compute();
                        aggvals[i] = newval;
                    }
                }
                var w = new Row(keys, aggvals);
                if (logic.having_ is null || logic.having_.Exec(context, w) is true)
                {
                    var newr = ExecProject(context, w);
                    callback(newr);
                }
            }
            return null;
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
        public override string Exec(ExecContext context, Func<Row, string> callback)
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
            {
                if (context.stop_)
                    break;
                callback(v);
            }
            return null;
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        public override string ToString() => $"PFrom({(logic_ as LogicFromQuery)}: {Cost()})";

        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicFromQuery;
            child_().Exec(context, l =>
            {
                if (context.stop_)
                    return null;
                
                if (logic.queryRef_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(context, l);
                callback(r);
                return null;
            });
            return null;
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string ToString() => $"PFILTER({child_()}: {Cost()})";

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicFilter).filter_;

            child_().Exec(context, l =>
            {
                if (context.stop_)
                    return null;

                if (filter is null || filter.Exec(context, l) is true)
                {
                    var r = ExecProject(context, l);
                    callback(r);
                }
                return null;
            });
            return null;
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

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                var table = (logic_ as LogicInsert).targetref_.Table();
                table.heap_.Add(l);
                return null;
            });
            return null;
        }
    }

    public class PhysicResult : PhysicNode
    {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            Row r = ExecProject(context, null);
            callback(r);
            return null;
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

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                nrows_++;
                callback(l);
                return null;
            });
            return null;
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child): base(null) => children_.Add(child);

        public override string Open(ExecContext context)
        {
            string s = $@"ExecContext context = new ExecContext(new QueryOption());";
            return s + base.Open(context);
        }

        public override string Close(ExecContext context)
        {
            string s = "}}";
            return base.Close(context) + s;
        }

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var child = (child_() is PhysicProfiling) ?
                    child_().child_() : child_();
            var output = child.logic_.output_;
            var ncolumns = output.Count(x => x.isVisible_);

            CodeWriter.Reset();
            context.Reset();
            string s = child_().Exec(context, r =>
            {
                if (context.option_.optimize_.use_codegen_) {
                    string cs = $@"
                    Row newr = new Row({ncolumns});
                    for (int i = 0; i < {output.Count}; i++)
                    {{
                        if ({child_().codegen_logic_}.output_[i].isVisible_)
                            newr[i] = r[i];
                    }}
                    Console.WriteLine(newr);";
                    return cs;
                }
                else
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
                }
            });

            return s;
        }
    }

    public class PhysicLimit : PhysicNode {

        public PhysicLimit(LogicLimit logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            int nrows = 0;
            int limit = (logic_ as LogicLimit).limit_;

            child_().Exec(context, l =>
            {
                nrows++;
                Debug.Assert(nrows <= limit);
                if (nrows == limit)
                    context.stop_ = true;
                callback(l);
                return null;
            });
            return null;
        }
    }
}

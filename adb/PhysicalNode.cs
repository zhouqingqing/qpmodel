using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using adb.expr;
using adb.logic;
using adb.utils;
using adb.optimizer;
using adb.codegen;

using Value = System.Object;

namespace adb.physic
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
            if (context.option_.optimize_.use_codegen_)
            {
                // default setting of codegen parameters
                _ = ObjectID.NewId().ToString();
                _logic_ = $"{logic_?.GetType().Name}{_}";
                _physic_ = $"{this.GetType().Name}{_}";
            }

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

        // codegen support seciton
        // -----------------------
        // local varialbes are named locally by append the local hmid. Record r passing
        // across callback boundaries are special: they have to be uniquely named and
        // use consistently.
        //
        public PhysicNode Locate(string objectid) {
            PhysicNode target = null;
            VisitEachNodeExists(x =>
            {
                if ((x as PhysicNode)._ == objectid)
                {
                    if (target != null)
                        throw new Exception("no duplicates allowed");
                    target = x as PhysicNode;
                    return false;
                }
                return false;
            });

            Debug.Assert(target != null);
            return target;
        }

        protected string CreateCommonNames(ExecContext context) {
            Debug.Assert(context.option_.optimize_.use_codegen_);

            var phytype = GetType().Name;
            var logtype = logic_?.GetType().Name;
            string s = $@"
                {phytype} {_physic_}  = stmt.physicPlan_.Locate(""{_}"") as {phytype};
                {logtype} {_logic_} = {_physic_}.logic_ as {logtype};
                var filter{_} = {_logic_}.filter_;
                ";
            return s;
        }

        internal string _ = "<codegen: current node id>";
        internal string _logic_ = "<codegen: logic node name>";
        internal string _physic_ = "<codegen: physic node name>";
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
            _ = logic.tabref_.alias_;
            _logic_ = $"{logic.GetType().Name}{_}";
            _physic_ = $"{this.GetType().Name}{_}";
            string cs = CreateCommonNames(context);
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
                var heap{_} = ({_logic_}.tabref_).Table().heap_.GetEnumerator();
                for (; ; )
                {{
                    Row r{_} = null;
                    if (context.stop_)
                        break;

                    if (heap{_}.MoveNext())
                        r{_} = heap{_}.Current;
                    else
                        break;

                    if ({(filter is null).ToStrLower()} || filter{_}.Exec(context, r{_}) is true)
                    {{
                        r{_} = {_physic_}.ExecProject(context, r{_});
                        {callback(null)}
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
            if (double.IsNaN(cost_))
            {
                var logic = (logic_) as LogicScanTable;
                var tablerows = Catalog.sysstat_.EstCardinality(logic.tabref_.relname_);
                cost_ = tablerows * 1.0;
            }
            return cost_;
        }
    }

    public class PhysicIndexSeek : PhysicNode
    {
        public PhysicIndexSeek(LogicNode logic) : base(logic) { }
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
            var rlist = index.Search(key);
            foreach (var r in rlist)
            {
                if (logic.tabref_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || filter.Exec(context, r) is true)
                {
                    var n = ExecProject(context, r);
                    callback(n);
                }
            }

            return null;
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
            {
                // 2 means < 50% selection ratio will pick up index
                var logic = (logic_) as LogicScanTable;
                cost_ = logic.EstCardinality() * 2.0;
            }
            return cost_;
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

        public override string Open(ExecContext context)
        {
            var childcode = base.Open(context);
            if (!context.option_.optimize_.use_codegen_)
                return null;

            var cs = CreateCommonNames(context);
            return childcode + cs;
        }
        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var filter = logic.filter_;
            bool semi = type == JoinType.SemiJoin;
            bool antisemi = type == JoinType.AntiSemiJoin;

            string s = l_().Exec(context, l =>
            {
                string out_code = "";
                if (context.option_.optimize_.use_codegen_)
                {
                    out_code += $@"
                        if (context.stop_)
                            return;";
                }
                else
                {
                    if (context.stop_)
                        return null;
                }
                
                bool foundOneMatch = false;
                out_code += $"bool foundOneMatch{_} = false;";
                out_code += r_().Exec(context, r =>
                {
                    if (context.option_.optimize_.use_codegen_)
                    {
                        string inner_code = $@"
                        if (!{semi.ToStrLower()} || !foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{l_()._}, r{r_()._});
                            if ({(filter is null).ToStrLower()} || filter{_}.Exec(context, r{_}) is true)
                            {{
                                foundOneMatch{_} = true;
                                if (!{antisemi.ToStrLower()})
                                {{
                                    r{_} = {_physic_}.ExecProject(context, r{_});
                                    {callback(null)}
                                }}
                            }}
                        }}";
                        return inner_code;
                    }
                    else
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
                    }
                });

                if (context.option_.optimize_.use_codegen_) 
                {
                    out_code += $@"
                        if ({antisemi.ToStrLower()} && !foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{l_()._}, null);
                            r{_} = {_physic_}.ExecProject(context, r{_});
                            {callback(null)}
                        }}
                        ";
                    return out_code;
                }
                else
                {

                    if (antisemi && !foundOneMatch)
                    {
                        Row n = new Row(l, null);
                        n = ExecProject(context, n);
                        callback(n);
                    }
                    return null;
                }
            });

            return s;
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
        public PhysicHashJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            Debug.Assert(logic.filter_ != null);
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PHJ({l_()},{r_()}: {Cost()})";

        public override string Open(ExecContext context)
        {
            var logic = logic_ as LogicJoin;

            // recreate the left side and right side key list - can't reuse old values 
            // because earlier optimization time keylist may have wrong bindings
            //
            logic.CreateKeyList(false);
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
                var keys = KeyList.ComputeKeys(context, logic.leftKeys_, l);

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
                var keys = KeyList.ComputeKeys(context, logic.rightKeys_, n);
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
            {
                var buildcost = (l_() as PhysicMemoRef).Logic().EstCardinality() * 2.0;
                var probecost = (r_() as PhysicMemoRef).Logic().EstCardinality() * 1.0;
                var outputcost = logic_.EstCardinality() * 1.0;
                cost_ = buildcost + probecost + outputcost;
            }
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
        Row HandleEmptyResult(ExecContext context)
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
            string s = child_().Exec(context, l =>
            {
                var keys = KeyList.ComputeKeys(context, logic.keys_, l);

                if (hm.TryGetValue(keys, out Row exist))
                {
                    for (int i = 0; i < aggrcore.Count; i++)
                    {
                        var old = exist[i];
                        exist[i] = aggrcore[i].Accum(context, old, l);
                    }
                }
                else
                {
                    hm.Add(keys, AggrCoreToRow(context, l));
                    exist = hm[keys];
                    for (int i = 0; i<aggrcore.Count; i++)
                        exist[i] = aggrcore[i].Init(context, l);
                }

                return null;
            });

            // stitch keys+aggcore into final output
            if (logic.keys_ is null && hm.Count == 0) {
                Row row = HandleEmptyResult(context);
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
                    aggvals[i] = aggrcore[i].Finalize(context, aggvals[i]);
                var w = new Row(keys, aggvals);
                if (logic.having_ is null || logic.having_.Exec(context, w) is true)
                {
                    var newr = ExecProject(context, w);
                    callback(newr);
                }
            }
            return s;
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"POrder({child_()}: {Cost()})";

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

        public override double Cost()
        {
            if (double.IsNaN(cost_))
            {
                var rowstosort = (child_() as PhysicMemoRef).Logic().EstCardinality() * 1.0;
                cost_ = rowstosort * (0.1 + Math.Log(rowstosort));
            }
            return cost_;
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
        internal Int64 nrows_ = 0;
        internal Int64 nloops_= 0;

        public override string ToString() => $"${child_()}";

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        public override string Exec(ExecContext context, Func<Row, string> callback)
        {
            nloops_++;
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
            string s = $@"";
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
                        Row newr = new Row({ncolumns});";
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].isVisible_)
                            cs += $"newr[{i}] = r{child_()._}[{i}];";
                    }
                    cs += "Console.WriteLine(newr);";
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
        public override string ToString() => $"PLIMIT({child_()}: {Cost()})";

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

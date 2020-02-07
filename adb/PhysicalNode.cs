using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using adb.expr;
using adb.logic;
using adb.utils;
using adb.optimizer;
using adb.codegen;
using adb.index;
using adb.stat;

using Value = System.Object;
using BitVector = System.Int64;


namespace adb.physic
{
    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        public LogicNode logic_;
        internal PhysicProfiling profile_;

        internal double cost_ = double.NaN;

        internal ExecContext context_;

        protected PhysicNode(LogicNode logic)
        {
            // logic node shall not be null unless it is PhysicCollect
            Debug.Assert(logic is null == this is PhysicCollect);
            // logic node shall not memoref unless it is physical memoref
            Debug.Assert(logic is LogicMemoRef == this is PhysicMemoRef);
            logic_ = logic;
        }

        public override string ExplainOutput(int depth, ExplainOption option) => logic_.ExplainOutput(depth, option);
        public override string ExplainInlineDetails(int depth) => logic_.ExplainInlineDetails(depth);
        public override string ExplainMoreDetails(int depth, ExplainOption option) => logic_.ExplainMoreDetails(depth, option);


        public void Validate()
        {
            VisitEach(x =>
            {
                var phy = x as PhysicNode;
                List<Type> nocheck = new List<Type> {typeof(PhysicCollect), typeof(PhysicProfiling), 
                    typeof(PhysicIndex), typeof(PhysicInsert), typeof(PhysicAnalyze)};
                if (!nocheck.Contains(phy.GetType()))
                {
                    var log = phy.logic_;

                    // we may want to check log.output_.Count != 0 but a special case is cross join 
                    // may have one side output empty row, so let's do a rough check
                    if (log.output_.Count == 0)
                    {
                        if (!VisitEachExists(x => x is PhysicNLJoin))
                            Debug.Assert(false);
                    }
                }
            });
        }

        public virtual string Open(ExecContext context){
            string s = null;
            context_ = context;
            if (context.option_.optimize_.use_codegen_)
            {
                // default setting of codegen parameters
                _ = ObjectID.NewId().ToString();
                _logic_ = $"{logic_?.GetType().Name}{_}";
                _physic_ = $"{this.GetType().Name}{_}";
                s += CreateCommonNames();
            }

            children_.ForEach(x => s += x.Open(context)); return s;
        }

        public virtual string Close() {
            string s = ""; children_.ForEach(x => s += x.Close()); return s;
        }
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract string Exec(Func<Row, string> callback);

        public string ExecProjectCode(string input)
        {
            var output = logic_.output_;
            string s = null;
            s += $@"
            {{
            // projection on {_physic_}: {logic_.ExplainOutput(0, null)} 
            Row rproj = new Row({output.Count});";
            for (int i = 0; i < output.Count; i++)
                s+= $"rproj[{i}] = {output[i].ExecCode(context_, input)};";
            s += $"{input} = rproj;}}";

            return s;
        }
        public Row ExecProject(Row input)
        {
            var output = logic_.output_;
            Row r = new Row(output.Count);
            for (int i = 0; i < output.Count; i++)
                r[i] = output[i].Exec(context_, input);

            return r;
        }

        public virtual double Cost() {
            if (double.IsNaN(cost_))
                cost_ = 10.0;
            return cost_;
        }

        public long Card() => logic_.Card();
        public BitVector tableContained_ { get => logic_.tableContained_; }

        // codegen support seciton
        // -----------------------
        // local varialbes are named locally by append the local hmid. Record r passing
        // across callback boundaries are special: they have to be uniquely named and
        // use consistently.
        //
        public PhysicNode LocateNode(string objectid) {
            PhysicNode target = null;
            VisitEachExists(x =>
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

        protected string CreateCommonNames() {
            Debug.Assert(context_.option_.optimize_.use_codegen_);

            var phytype = GetType().Name;
            string logicfilter = null;
            if (logic_ != null)
            {
                var logtype = logic_.GetType().Name;
                logicfilter = $@"
                    {logtype} {_logic_} = {_physic_}.logic_ as {logtype};
                    var filter{_} = {_logic_}.filter_;
                    var output{_} = {_logic_}.output_;";
            }
            string s = $@"
                {phytype} {_physic_}  = stmt.physicPlan_.LocateNode(""{_}"") as {phytype};
                {logicfilter}";
            return s;
        }

        // @code won't be evaluated unless use_codegen_ is enabled
        protected string codegen(Lazy<string> code)
        {
            if (context_.option_.optimize_.use_codegen_)
                return code.Value;
            return null;
        }
        // a simpler form of codegen but code is evaluated always
        protected string codegen(string code)
        {
            if (context_.option_.optimize_.use_codegen_)
                return code;
            return null;
        }
        protected string codegen_if(bool cond, string code)
        {
            if (context_.option_.optimize_.use_codegen_ && cond)
                return code;
            return null;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)] 
        internal string _ = "<codegen: current node id>";
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string _logic_ = "<codegen: logic node name>";
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string _physic_ = "<codegen: physic node name>";
    }

    // PhysicMemoRef wrap a LogicMemoRef as a physical node (so LogicMemoRef can be 
    // used in physical tree). Actually we only need LogicMemoRef's memo group.
    //
    public class PhysicMemoRef : PhysicNode
    {
        public PhysicMemoRef(LogicNode logic) : base(logic) { Debug.Assert(logic is LogicMemoRef); }
        public override string ToString() => Logic().ToString();

        public override string Exec(Func<Row, string> callback) => throw new InvalidProgramException("not executable");
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
        public override string ExplainMoreDetails(int depth, ExplainOption option)
        {
            // we want to see what's underneath
            return $"{{{Logic().ExplainMoreDetails (depth + 1, option)}}}";
        }
    }


    public class PhysicScanTable : PhysicNode
    {
        public PhysicScanTable(LogicNode logic) : base(logic) { }
        public override string ToString() => $"PScan({(logic_ as LogicScanTable).tabref_}: {Cost()})";

        public override string Open(ExecContext context)
        {
            string cs = null;
            context_ = context;
            if (context.option_.optimize_.use_codegen_)
            {
                // create more meaningful names myself
                var logic = logic_ as LogicScanTable;
                _ = logic.tabref_.alias_;
                _logic_ = $"{logic.GetType().Name}{_}";
                _physic_ = $"{this.GetType().Name}{_}";
                cs += CreateCommonNames();
            }
            children_.ForEach(x => cs += x.Open(context));
            return cs;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var heap = (logic.tabref_).Table().heap_.GetEnumerator();

            string cs = null;
            if (context.option_.optimize_.use_codegen_)
            {
                cs += $@"
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
                    {codegen_if(logic.tabref_.colRefedBySubq_.Count != 0,
                            $@"context.AddParam({_logic_}.tabref_, r{_});")}";
                if (filter != null)
                    cs += $"if ({filter.ExecCode(context, $"r{_}")} is true)";
                cs += $@"
                    {{
                        {ExecProjectCode($"r{_}")}
                        {callback(null)}
                    }}
                }}";
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
                        r = ExecProject(r);
                        callback(r);
                    }
                }
            }

            return cs;
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
        public IndexDef index_;

        public PhysicIndexSeek(LogicNode logic, IndexDef index) : base(logic) { index_ = index; }

        public override string ExplainInlineDetails(int depth) => $"{index_}";
        public override string ToString() => $"ISeek({index_}: {Cost()})";

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_ as BinExpr;
            var index = index_.index_;

            bool ok = filter.r_().TryEvalConst(out Value searchval);
            Debug.Assert(ok);
            KeyList key = new KeyList(1);
            key[0] = searchval;
            var rlist = index.Search(filter.op_, key);
            if (rlist is null)
                return null;
            foreach (var r in rlist)
            {
                if (logic.tabref_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || filter.Exec(context, r) is true)
                {
                    var n = ExecProject(r);
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
                cost_ = logic.Card() * 2.0;
            }
            return cost_;
        }
    }

    public class PhysicScanFile : PhysicNode
    {
        public PhysicScanFile(LogicNode logic) : base(logic) { }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
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

    public abstract class PhysicJoin: PhysicNode {
        public enum Implmentation
        {
            NLJoin,
            HashJoin
        }

        public PhysicJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
            // Debug.Assert(tableContained_ != 0);
        }

        static internal PhysicJoin NewJoinImplmentation(Implmentation impl, PhysicNode left, PhysicNode right)
        {
            var logic = new LogicJoin(left.logic_, right.logic_);
            switch (impl)
            {
                case Implmentation.HashJoin:
                    return new PhysicHashJoin(logic, left, right);
                case Implmentation.NLJoin:
                    return new PhysicNLJoin(logic, left, right);
                default:
                    throw new InvalidProgramException();
            }
        }
    }

    public class PhysicNLJoin : PhysicJoin
    {
        public PhysicNLJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic, l, r) { }
        public override string ToString() => $"PNLJ({l_()},{r_()}: {Cost()})";

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var filter = logic.filter_;
            bool semi = type == JoinType.Semi;
            bool antisemi = type == JoinType.AntiSemi;
            bool left = type == JoinType.Left;

            string s = l_().Exec(l =>
            {
                string out_code = "";
                if (context.stop_)
                    return null;
                out_code += codegen($@"
                    if (context.stop_)
                        return;");

                bool foundOneMatch = false;
                out_code += codegen($"bool foundOneMatch{_} = false;");
                out_code += r_().Exec(r =>
                {
                    string inner_code = null;
                    if (context.option_.optimize_.use_codegen_)
                    {
                        inner_code += $@"
                        if (!{semi.ToLower()} || !foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{l_()._}, r{r_()._});";
                        if (filter != null)
                            inner_code += $"if ({filter.ExecCode(context, $"r{_}")} is true)";
                        inner_code += $@"    
                            {{
                                foundOneMatch{_} = true;
                                {codegen_if(!antisemi, $@"
                                {{
                                   {ExecProjectCode($"r{_}")}
                                   {callback(null)}
                                }}")}
                            }}
                        }}";
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
                                    n = ExecProject(n);
                                    callback(n);
                                }
                            }
                        }
                    }
                    return inner_code;
                });

                if (context.option_.optimize_.use_codegen_)
                {
                    out_code += codegen_if(antisemi, $@"
                    if (!foundOneMatch{_})
                    {{
                        Row r{_} = new Row(r{l_()._}, null);
                        {ExecProjectCode($"r{_}")}
                        {callback(null)}
                    }}");
                    out_code += codegen_if(left, $@"
                    if (!foundOneMatch{_})
                    {{
                        Row r{_} = new Row(r{l_()._}, new Row{r_().logic_.output_.Count});
                        {ExecProjectCode($"r{_}")}
                        {callback(null)}
                    }}");
                }
                else
                {
                    if (antisemi && !foundOneMatch)
                    {
                        Row n = new Row(l, null);
                        n = ExecProject(n);
                        callback(n);
                    }
                    if (left && !foundOneMatch)
                    {
                        Row n = new Row(l, new Row(r_().logic_.output_.Count));
                        n = ExecProject(n);
                        callback(n);
                    }
                }
                return out_code;
            });

            return s;
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_().Card() + 10) * (r_().Card() + 10);
            return cost_;
        }
    }

    // Key list is a special row
    public class KeyList: Row
    {
        public KeyList(int length): base(length){}

        static public KeyList ComputeKeys(ExecContext context, List<Expr> keys, Row input)
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

    public class TaggedRow
    {
        public Row row_;
        public bool matched_ = false;
        public TaggedRow(Row row) { row_ = row; }
    }

    public class PhysicHashJoin : PhysicJoin
    {
        public PhysicHashJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic, l, r) { }
        public override string ToString() => $"PHJ({l_()},{r_()}: {Cost()})";

        public override string Open(ExecContext context)
        {
            string cs = base.Open(context);
            Debug.Assert(logic_.filter_ != null);

            // recreate the left side and right side key list - can't reuse old values 
            // because earlier optimization time keylist may have wrong bindings
            //
            var logic = logic_ as LogicJoin;
            logic.CreateKeyList(false);
            if (context.option_.optimize_.use_codegen_)
            {
                cs += $@"var hm{_} = new Dictionary<KeyList, List<TaggedRow>>();";
            }
            return cs;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var hm = new Dictionary<KeyList, List<TaggedRow>>();
            bool semi = type == JoinType.Semi;
            bool antisemi = type == JoinType.AntiSemi;
            bool right = type == JoinType.Right;
            bool left = type == JoinType.Left;

            // build hash table with left side 
            string s = l_().Exec(l => {
                string buildcode = null;
                if (context.option_.optimize_.use_codegen_)
                {
                    var lname = $"r{l_()._}";
                    buildcode += $@"
                    var keys{_} = KeyList.ComputeKeys(context, {_logic_}.leftKeys_, {lname});
                    if (hm{_}.TryGetValue(keys{_}, out List<TaggedRow> exist))
                    {{
                        exist.Add(new TaggedRow({lname}));
                    }}
                    else
                    {{
                        var rows = new List<TaggedRow>();
                        rows.Add(new TaggedRow({lname}));
                        hm{_}.Add(keys{_}, rows);
                    }}
                    ";
                }
                else
                {
                    var keys = KeyList.ComputeKeys(context, logic.leftKeys_, l);
                    if (hm.TryGetValue(keys, out List<TaggedRow> exist))
                    {
                        exist.Add(new TaggedRow(l));
                    }
                    else
                    {
                        var rows = new List<TaggedRow>();
                        rows.Add(new TaggedRow(l));
                        hm.Add(keys, rows);
                    }
                }
                return buildcode;
            });

            // right side probes the hash table
            if (context.option_.optimize_.use_codegen_)
            {
                s += $@"
                if (hm{_}.Count == 0)
                    return;";
            }
            else
            {
                if (hm.Count == 0)
                    return null;
            }
            s+= r_().Exec(r =>
            {
                string probecode = null;
                if (context.option_.optimize_.use_codegen_) {
                    var rname = $"r{r_()._}";
                    probecode += $@"
                    if (context.stop_)
                        return;

                    Row fakel{_} = new Row({l_().logic_.output_.Count});
                    Row r{_} = new Row(fakel{_}, {rname});
                    var keys{_} = KeyList.ComputeKeys(context, {_logic_}.rightKeys_, r{_});
                    bool foundOneMatch{_} = false;

                    if (hm{_}.TryGetValue(keys{_}, out List<TaggedRow> exist{_}))
                    {{
                        foundOneMatch{_} = true;
                        foreach (var v{_} in exist{_})
                        {{
                            r{_} = new Row(v{_}.row_, {rname});
                            {ExecProjectCode($"r{_}")}
                            {callback(null)}
                            {codegen_if(semi, 
                                "break;")}
                        }}
                    }}
                    else
                    {{
                        // no match for antisemi
                        {codegen_if(antisemi, $@"
                        if (!foundOneMatch{_})
                        {{
                            r{_} = new Row(null, r{_});
                            {ExecProjectCode($"r{_}")}
                            {callback(null)}
                        }}")}
                        {codegen_if(right, $@"
                        if (!foundOneMatch{_})
                        {{
                            r{_} = new Row(new Row{l_().logic_.output_.Count}, r{_});
                            {ExecProjectCode($"r{_}")}
                            {callback(null)}
                        }}")}
                    }}
                    ";
                }
                else
                {
                    if (context.stop_)
                        return null;

                    Row fakel = new Row(l_().logic_.output_.Count);
                    Row n = new Row(fakel, r);
                    var keys = KeyList.ComputeKeys(context, logic.rightKeys_, n);
                    bool foundOneMatch = false;

                    if (hm.TryGetValue(keys, out List<TaggedRow> exist))
                    {
                        foundOneMatch = true;
                        foreach (var v in exist)
                        {
                            if (left)
                                v.matched_ = true;
                            n = ExecProject(new Row(v.row_, r));
                            callback(n);
                            if (semi)
                                break;
                        }
                    }
                    else
                    {
                        // no match for antisemi
                        if (antisemi && !foundOneMatch)
                        {
                            n = new Row(null, r);
                            n = ExecProject(n);
                            callback(n);
                        }
                        if (right && !foundOneMatch)
                        {
                            n = new Row(new Row(l_().logic_.output_.Count), r);
                            n = ExecProject(n);
                            callback(n);
                        }
                    }
                }
                return probecode;
            });

            // left join shall examine hash table and output all non-matched rows
            if (left)
            {
                foreach (var v in hm)
                    foreach (var r in v.Value) 
                    {
                        if (!r.matched_)
                        {
                            var n = new Row(r.row_, new Row(r_().logic_.output_.Count));
                            n = ExecProject(n);
                            callback(n);
                        }
                    }
            }

            return s;
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
            {
                var buildcost = l_().Card() * 2.0;
                var probecost = r_().Card() * 1.0;
                var outputcost = logic_.Card() * 1.0;
                cost_ = buildcost + probecost + outputcost;
            }
            return cost_;
        }
    }

    public class PhysicHashAgg : PhysicNode
    {
        public PhysicHashAgg(LogicAgg logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PHAgg({(logic_ as LogicAgg)}: {Cost()})";

        public Row AggrCoreToRow(Row input)
        {
            var aggfns = (logic_ as LogicAgg).aggrFns_;
            Row r = new Row(aggfns.Count);
            return r;
        }

        public override string Open(ExecContext context)
        {
            string cs = base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                cs += $@"var aggrcore{_} = {_logic_}.aggrFns_;
                   var hm{_} = new Dictionary<KeyList, Row>();";
            }
            return cs;
        }

        //  special case: when there is no key and hm is empty, we shall still return one row
        //    count: 0, avg/min/max/sum: null
        //    but HAVING clause can filter this row out.
        //
        public Row HandleEmptyResult(ExecContext context)
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
                var newr = ExecProject(w);
                return newr;
            }
            return null;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;
            var hm = new Dictionary<KeyList, Row>();

            // aggregation is working on aggCore targets
            string s = child_().Exec(l =>
            {
                string buildcode = null;
                if (context.option_.optimize_.use_codegen_)
                {
                    var lrow = $"r{child_()._}";
                    buildcode += $@" 
                        var keys = KeyList.ComputeKeys(context, {_logic_}.keys_, {lrow});";
                    buildcode += $@"
                        if (hm{_}.TryGetValue(keys, out Row exist))
                        {{
                            for (int i = 0; i < {aggrcore.Count}; i++)
                            {{
                                var old = exist[i];
                                exist[i] = aggrcore{_}[i].Accum(context, old, {lrow});
                            }}
                        }}
                        else
                        {{
                            hm{_}.Add(keys, {_physic_}.AggrCoreToRow({lrow}));
                            exist = hm{_}[keys];
                            for (int i = 0; i < {aggrcore.Count}; i++)
                                exist[i] = aggrcore{_}[i].Init(context, {lrow});
                        }}";
                }
                else
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
                        hm.Add(keys, AggrCoreToRow(l));
                        exist = hm[keys];
                        for (int i = 0; i < aggrcore.Count; i++)
                            exist[i] = aggrcore[i].Init(context, l);
                    }
                }

                return buildcode;
            });

            // stitch keys+aggcore into final output
            if (context.option_.optimize_.use_codegen_)
            {
                if (logic.keys_ is null)
                    s += $@"
                    if (hm{_}.Count == 0)
                    {{
                        Row r{_} = {_physic_}.HandleEmptyResult(context);
                        if (r{_} != null)
                        {{
                            {callback(null)}
                        }}
                    }}
                    ";
                s += $@"
                foreach (var v{_} in hm{_})
                {{
                    if (context.stop_)
                        break;

                    var keys{_} = v{_}.Key;
                    Row aggvals{_} = v{_}.Value;
                    for (int i = 0; i < {aggrcore.Count}; i++)
                        aggvals{_}[i] = aggrcore{_}[i].Finalize(context, aggvals{_}[i]);
                    var r{_} = new Row(keys{_}, aggvals{_});
                    if ({(logic.having_ is null).ToLower()} || {_logic_}.having_.Exec(context, r{_}) is true)
                    {{
                        {ExecProjectCode($"r{_}")}
                        {callback(null)}
                    }}
                }}
                ";
            }
            else
            {
                if (logic.keys_ is null && hm.Count == 0)
                {
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
                        var newr = ExecProject(w);
                        callback(newr);
                    }
                }
            }

            return s;
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"POrder({child_()}: {Cost()})";

        public override string Open(ExecContext context)
        {
            string cs = base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                cs += $@"var set{_} = new List<Row>();";
            }
            return cs;
        }

        // respect logic.orders_|descends_
        public int compareRow(Row l, Row r)
        {
            var logic = logic_ as LogicOrder;
            var orders = logic.orders_;
            var descends = logic.descends_;

            var lkey = KeyList.ComputeKeys(context_, orders, l);
            var rkey = KeyList.ComputeKeys(context_, orders, r);
            return lkey.CompareTo(rkey, descends);
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicOrder;
            var set = new List<Row>();

            string s = child_().Exec(l =>
            {
                string build = null;
                if (context.option_.optimize_.use_codegen_) {
                    build = $@"set{_}.Add(r{child_()._});";
                }
                else
                    set.Add(l);
                return build;
            });
            set.Sort(compareRow);
            s += codegen($@"set{_}.Sort({_physic_}.compareRow);");

            // output sorted set
            if (context.option_.optimize_.use_codegen_)
            {
                s += $@"
                foreach (var rs{_} in set{_})
                {{
                    if (context.stop_)
                        break;
                    var r{_} = {_physic_}.ExecProject(rs{_});
                    {callback(null)}
                }}";
            }
            else
            {
                foreach (var rs in set)
                {
                    if (context.stop_)
                        break;
                    var r = ExecProject(rs);
                    callback(r);
                }
            }
            return s;
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
            {
                var rowstosort = child_().Card() * 1.0;
                cost_ = rowstosort * (0.1 + Math.Log(rowstosort));
            }
            return cost_;
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        public override string ToString() => $"PFrom({(logic_ as LogicFromQuery)}: {Cost()})";

        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicFromQuery;
            child_().Exec(l =>
            {
                if (context.stop_)
                    return null;
                
                if (logic.queryRef_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(l);
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

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            Expr filter = (logic_ as LogicFilter).filter_;

            child_().Exec(l =>
            {
                if (context.stop_)
                    return null;

                if (filter is null || filter.Exec(context, l) is true)
                {
                    var r = ExecProject(l);
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

        public override string Exec(Func<Row, string> callback)
        {
            child_().Exec(l =>
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

        public override string Exec(Func<Row, string> callback)
        {
            Row r = ExecProject(null);
            callback(r);
            return null;
        }
    }

    public class PhysicProfiling : PhysicNode
    {
        public Int64 nrows_ = 0;
        public Int64 nloops_= 0;

        public override string ToString() => $"${child_()}";

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        public override string Exec(Func<Row, string> callback)
        {
            string s = null;
            ExecContext context = context_;

            if (context.option_.optimize_.use_codegen_)
                s = $"{_physic_}.nloops_ ++;";
            else
                nloops_++;
            s += child_().Exec(l =>
            {
                string code = null;
                if (context.option_.optimize_.use_codegen_) {
                    code = $@"
                    {_physic_}.nrows_++;
                    var r{_} = r{child_()._};
                    {callback(null)}";
                }
                else
                {
                    nrows_++;
                    callback(l);
                }
                return code;
            });
            return s;
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child): base(null) => children_.Add(child);

        public override string Close()
        {
            string s = "}}";
            return base.Close() + s;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var child = (child_() is PhysicProfiling) ?
                    child_().child_() : child_();
            var output = child.logic_.output_;
            var ncolumns = output.Count(x => x.isVisible_);

            if (context.option_.optimize_.use_codegen_)
                CodeWriter.Reset();
            context.Reset();
            string s = child_().Exec(r =>
            {
                string cs = null;
                if (context.option_.optimize_.use_codegen_) {
                    cs += $@"
                        Row newr = new Row({ncolumns});";
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].isVisible_)
                            cs += $"newr[{i}] = r{child_()._}[{i}];";
                    }
                    cs += $"{_physic_}.rows_.Add(newr);";
                    cs += $"Console.WriteLine(newr);";
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
                }
                return cs;
            });

            return s;
        }
    }

    public class PhysicAppend: PhysicNode
    {

        public PhysicAppend(LogicAppend logic, PhysicNode l, PhysicNode r) : base(logic) { children_.Add(l); children_.Add(r); }
        public override string ToString() => $"PAPPEND({l_()},{r_()}): {Cost()})";

        public override string Open(ExecContext context)
        {
            string cs = base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
            }
            return cs;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;

            string s = null;
            foreach (var child in children_)
            {
                s += child.Exec(r =>
                {
                    string appendcode = null;
                    if (context.option_.optimize_.use_codegen_)
                    {
                        appendcode += $"{callback(null)}";
                    }
                    else
                    {
                        callback(r);
                    }
                    return appendcode;
                });
            }

            return s;
        }
    }

    public class PhysicLimit : PhysicNode {

        public PhysicLimit(LogicLimit logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PLIMIT({child_()}: {Cost()})";

        public override string Open(ExecContext context)
        {
            string cs = base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                cs += $@"var nrows{_} = 0;";
            }
            return cs;
        }

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            int nrows = 0;
            int limit = (logic_ as LogicLimit).limit_;

            string s = child_().Exec(l =>
            {
                string limitcode = null;
                if (context.option_.optimize_.use_codegen_) {
                    limitcode = $@"                    
                    nrows{_}++;
                    Debug.Assert(nrows{_} <= {limit});
                    if (nrows{_} == {limit})
                        context.stop_ = true;
                    var r{_} = r{child_()._};
                    {callback(null)}";
                }
                else
                {
                    nrows++;
                    Debug.Assert(nrows <= limit);
                    if (nrows == limit)
                        context.stop_ = true;
                    callback(l);
                }

                return limitcode;
            });
            return s;
        }
    }
}

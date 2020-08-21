/*
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
using qpmodel.codegen;
using qpmodel.expr;
using qpmodel.index;
using qpmodel.logic;
using qpmodel.optimizer;
using qpmodel.stat;
using qpmodel.utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BitVector = System.Int64;
using Value = System.Object;

namespace qpmodel.physic
{
    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        public LogicNode logic_;
        internal double cost_ = double.NaN;
        internal ulong memory_ = ulong.MaxValue;
        internal PhysicProfiling profile_;
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
        public override string ExplainInlineDetails() => logic_.ExplainInlineDetails();
        public override string ExplainMoreDetails(int depth, ExplainOption option) => logic_.ExplainMoreDetails(depth, option);

        public void ValidateThis()
        {
            VisitEach(x =>
            {
                var phy = x as PhysicNode;
                var phyType = phy.GetType();
                List<Type> nocheck = new List<Type> {typeof(PhysicCollect), typeof(PhysicProfiling),
                    typeof(PhysicIndex), typeof(PhysicInsert), typeof(PhysicAnalyze)};
                if (!nocheck.Contains(phyType))
                {
                    var log = phy.logic_;

                    // we may want to check log.output_.Count != 0 but special cases inclue:
                    // 1) cross join may have one side output empty row
                    // 2) select 'a' from a, which does not require any columns
                    // 3) intersect op may not require output from one side
                    // so let's do forbid-list check
                    //
                    if (log.output_.Count == 0)
                    {
                        List<Type> mustHaveOutput = new List<Type> { typeof(PhysicHashJoin), typeof(PhysicHashAgg), typeof(PhysicStreamAgg) };
                        Debug.Assert(!mustHaveOutput.Contains(phyType) || VisitEachExists(x => x is PhysicNLJoin));
                    }
                }
            });
        }

        // Preorder traversal is used to generate code from
        // nary-tree to @context when open.
        public virtual void Open(ExecContext context)
        {
            string s = null;

            // open once
            Debug.Assert(context_ is null);
            context_ = context;
            if (context.option_.optimize_.use_codegen_)
            {
                // default setting of codegen parameters
                _logic_ = $"{logic_?.GetType().Name}{_}";
                _physic_ = $"{this.GetType().Name}{_}";
                s += CreateCommonNames();
            }

            context.code_ += s;
            children_.ForEach(x => x.Open(context));
        }

        // Preorder traversal is used to generate code from
        // nary-tree to @context when close.
        public virtual void Close()
        {
            Debug.Assert(context_ != null);
            string s = null;
            context_.code_ += s;
            children_.ForEach(x => x.Close());
            context_ = null;
        }
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract void Exec(Action<Row> callback);

        public string ExecProjectCode(string input)
        {
            var output = logic_.output_;
            string s = null;
            s += $@"
            {{
            // projection on {_physic_}: {logic_.ExplainOutput(0, null)} 
            Row rproj = new Row({output.Count});";
            for (int i = 0; i < output.Count; i++)
                s += $"rproj[{i}] = {output[i].ExecCode(context_, input)};";
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

        #region optimizer
        public ulong Card() => logic_.Card();
        public double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = EstimateCost();
            Debug.Assert(cost_ >= 0);
            return cost_;
        }
        protected virtual double EstimateCost() => double.NaN;

        // inclusive cost summarize its own cost and its children cost. During 
        // optimiztaion it is a dynamic measurement, we do so by summarize its
        // best children's.
        public double InclusiveCost()
        {
            var incCost = 0.0;
            incCost += Cost();
            children_.ForEach(x =>
            {
                if (x is PhysicMemoRef xp)
                    incCost += xp.Group().nullPropertyMinIncCost;
                else
                    incCost += x.InclusiveCost();
            });

            Debug.Assert(incCost > Cost() || children_.Count == 0);
            return incCost;
        }

        public ulong Memory()
        {
            if (memory_ is ulong.MaxValue)
                memory_ = EstimateMemory();
            return memory_;
        }
        protected virtual ulong EstimateMemory() => 0;
        public ulong InclusiveMemory()
        {
            ulong memory = 0;
            memory += Memory();
            children_.ForEach(x =>
            {
                memory += x.InclusiveMemory();
            });

            return memory;
        }

        // interface for physical property
        //
        // determine if a required property can be satisfied through physic node
        // if it can be satisfied, then property requirement on children is returned
        // since for each property and physic node, there are more that one set of child properties
        // there for the output is a list (set) of list (children)
        // example:
        // required: <null, distributed>, node: hashjoin
        // viable child properties:
        // 1) (<null, distributed>, <null, distributed>)
        // 2) (<null, distributed>, <null, replicated>)
        public virtual bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            listchildprops = new List<List<PhysicProperty>>();
            listchildprops.Add(new List<PhysicProperty>());
            // the default is singleton with no order requirement
            // assume all the physic nodes can process singleton
            if (required.IsPropertySupplied(DistributionProperty.singleton))
            {
                for (int i = 0; i < children_.Count; i++)
                    listchildprops[0].Add(DistributionProperty.singleton);
                return true;
            }
            else
            {
                listchildprops = null;
                return false;
            }
        }
        #endregion

        public BitVector tableContained_ { get => logic_.tableContained_; }

        #region codegen support
        // local varialbes are named locally by append the local hmid. Record r passing
        // across callback boundaries are special: they have to be uniquely named and
        // use consistently.
        //
        public PhysicNode LocateNode(string objectid)
        {
            PhysicNode target = null;
            VisitEachExists(x =>
            {
                if ((x as PhysicNode)._ == objectid)
                {
                    Debug.Assert(target is null);
                    target = x as PhysicNode;
                    return false;
                }
                return false;
            });

            Debug.Assert(target != null);
            return target;
        }

        protected string CreateCommonNames()
        {
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
        internal string _logic_ = "<codegen: logic node name>";
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string _physic_ = "<codegen: physic node name>";
        #endregion
    }

    // PhysicMemoRef wrap a LogicMemoRef as a physical node (so LogicMemoRef can be 
    // used in physical tree). Actually we only need LogicMemoRef's memo group.
    //
    public class PhysicMemoRef : PhysicNode
    {
        public PhysicMemoRef(LogicNode logic) : base(logic) { Debug.Assert(logic is LogicMemoRef); }
        public override string ToString() => Logic().ToString();

        public override void Exec(Action<Row> callback) => throw new InvalidProgramException("not executable");
        public override int GetHashCode() => Group().memoid_;
        public override bool Equals(object obj)
        {
            if (obj is PhysicMemoRef lo)
                return Logic().MemoLogicSign() == (lo.logic_ as LogicMemoRef).MemoLogicSign();
            return false;
        }

        public LogicMemoRef Logic() => logic_ as LogicMemoRef;
        internal CMemoGroup Group() => Logic().group_;
        public override string ExplainMoreDetails(int depth, ExplainOption option)
        {
            // we want to see what's underneath
            return $"{{{Logic().ExplainMoreDetails(depth + 1, option)}}}";
        }
    }

    public class PhysicScanTable : PhysicNode
    {
        public PhysicScanTable(LogicNode logic) : base(logic) { }
        public override string ToString() => $"PScan({(logic_ as LogicScanTable).tabref_}: {Cost()})";

        public override void Open(ExecContext context)
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

            context.code_ += cs;
            children_.ForEach(x => x.Open(context));
        }

        List<Row> getHeapRows(int distId)
        {
            var logic = logic_ as LogicScanTable;
            string tablename = null;
            if (logic.tabref_ is BaseTableRef br)
                tablename = br.Table().name_;
            if (tablename == SysMemoExpr.name_)
                return SysMemoExpr.GetHeapRows();
            else if (tablename == SysMemoProperty.name_)
                return SysMemoProperty.GetHeapRows();
            return (logic.tabref_).Table().distributions_[distId].heap_;
        }

        public override void Exec(Action<Row> callback)
        {
            var context = context_;
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var distId = (logic.tabref_).IsDistributed() ? (context as DistributedContext).machineId_ : 0;
            var heap = getHeapRows(distId);

            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"
                var heap{_} = ({_logic_}.tabref_).Table().distributions_[{distId}].heap_;
                foreach (var l{_} in heap{_})
                {{
                    Row r{_} = l{_};
                    if (context.stop_)
                        break;

                    {codegen_if(logic.tabref_.colRefedBySubq_.Count != 0,
                            $@"context.AddParam({_logic_}.tabref_, r{_});")}";

                if (filter != null)
                {
                    context.code_ += $"if ({filter.ExecCode(context, $"r{_}")} is true)";
                }

                context.code_ += $@"
                    {{
                        {ExecProjectCode($"r{_}")}";

                // generate code to @context_code_ in callback
                callback(null);

                context.code_ += $@"
                    }}
                }}";
            }
            else
            {
                foreach (var l in heap)
                {
                    if (context.stop_)
                        break;

                    if (logic.tabref_.colRefedBySubq_.Count != 0)
                        context.AddParam(logic.tabref_, l);
                    if (filter is null || filter.Exec(context, l) is true)
                    {
                        var r = ExecProject(l);
                        callback(r);
                    }
                }
            }
        }

        protected override double EstimateCost()
        {
            var logic = (logic_) as LogicScanTable;
            var tablerows = Math.Max(1,
                        Catalog.sysstat_.EstCardinality(logic.tabref_.relname_));
            return tablerows * 1.0;
        }
        public override bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            // leaf node, no child
            listchildprops = null;

            // does not supply order property by default
            if (required.ordering_.Count > 0)
                return false;

            var logic = logic_ as LogicScanTable;
            var tableref = logic.tabref_;

            switch (required.distribution_.disttype)
            {
                case DistributionType.Singleton:
                    if (!tableref.IsDistributed())
                        return true;
                    else
                        return false;
                case DistributionType.Any:
                    if (tableref.Table().distMethod_ != TableDef.DistributionMethod.Replicated)
                        return true;
                    else
                        return false;
                case DistributionType.Replicated:
                    if (tableref.Table().distMethod_ == TableDef.DistributionMethod.Replicated)
                        return true;
                    else
                        return false;
            }
            // the rest case is specific distributed property
            Debug.Assert(required.distribution_.disttype == DistributionType.Distributed);
            if (tableref.Table().distMethod_ != TableDef.DistributionMethod.Replicated &&
                tableref.IsDistributionMatch(required.distribution_.exprs))
                return true;
            else
                return false;
        }
    }

    public class PhysicIndexSeek : PhysicNode
    {
        public IndexDef index_;

        public PhysicIndexSeek(LogicNode logic, IndexDef index) : base(logic) { index_ = index; }

        public override string ExplainInlineDetails() => $"{index_}";
        public override string ToString() => $"ISeek({index_}: {Cost()})";

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_ as BinExpr;
            var index = index_.index_;

            bool ok = filter.rchild_().TryEvalConst(out Value searchval);
            Debug.Assert(ok);
            KeyList key = new KeyList(1);
            key[0] = searchval;
            var rlist = index.Search(filter.op_, key);
            if (rlist is null)
                return;
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
        }

        protected override double EstimateCost()
        {
            // 1.99 means < 50% selection ratio will pick up index
            var logic = (logic_) as LogicScanTable;
            return logic.Card() * 1.99;
        }
    }

    public class PhysicScanFile : PhysicNode
    {
        public PhysicScanFile(LogicNode logic) : base(logic) { }

        public override void Exec(Action<Row> callback)
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
                    if (f == "")
                    {
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
                            case NumericType nt:
                                r[i] = decimal.Parse(f);
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
        protected override double EstimateCost()
        {
            var logic = (logic_) as LogicScanFile;
            var estRowsize = logic.tabref_.baseref_.Table().EstRowSize();
            var filename = logic.FileName();
            FileInfo fi = new FileInfo(filename);
            var estRowcnt = Math.Max(1, fi.Length / estRowsize);
            return estRowcnt * 1.5;
        }
    }

    public abstract class PhysicJoin : PhysicNode
    {
        public enum Implmentation
        {
            NLJoin,
            HashJoin
        }

        public PhysicJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }

        static internal PhysicJoin CreatePhysicJoin(Implmentation impl, PhysicNode left, PhysicNode right, Expr pred)
        {
            var logic = new LogicJoin(left.logic_, right.logic_, pred);
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
        public override string ToString() => $"PNLJ({lchild_()},{rchild_()}: {Cost()},{InclusiveCost()})";

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicJoin;
            var type = logic.type_;
            var filter = logic.filter_;
            bool semi = type == JoinType.Semi;
            bool antisemi = type == JoinType.AntiSemi;
            bool left = type == JoinType.Left;

            lchild_().Exec(l =>
            {
                if (context.stop_)
                    return;
                context.code_ += codegen($@"
                    if (context.stop_)
                        return;");

                bool foundOneMatch = false;
                context.code_ += codegen($"bool foundOneMatch{_} = false;");

                rchild_().Exec(r =>
                {
                    if (context.option_.optimize_.use_codegen_)
                    {
                        context.code_ += $@"
                        if (!{semi.ToLower()} || !foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{lchild_()._}, r{rchild_()._});";

                        if (filter != null)
                        {
                            context.code_ += $"if ({filter.ExecCode(context, $"r{_}")} is true)";
                        }

                        context.code_ += $@"    
                            {{
                                foundOneMatch{_} = true;";

                        if (!antisemi)
                        {
                            context.code_ += $@"
                                {{
                                   {ExecProjectCode($"r{_}")}";
                            // generate code to @context_code_ in callback
                            callback(null);
                            context.code_ += $@"}}";
                        }

                        context.code_ += $@"
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
                });

                if (context.option_.optimize_.use_codegen_)
                {
                    if (antisemi)
                    {
                        context.code_ += $@"
                        if (!foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{lchild_()._}, null);
                            {ExecProjectCode($"r{_}")}";

                        // generate code to @context_code_ in callback
                        callback(null);

                        context.code_ += $@"
                        }}";
                    }

                    if (left)
                    {
                        context.code_ += $@"
                        if (!foundOneMatch{_})
                        {{
                            Row r{_} = new Row(r{lchild_()._}, new Row({rchild_().logic_.output_.Count}));
                            {ExecProjectCode($"r{_}")}";

                        // generate code to @context_code_ in callback
                        callback(null);

                        context.code_ += $@"
                        }}";
                    }
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
                        Row n = new Row(l, new Row(rchild_().logic_.output_.Count));
                        n = ExecProject(n);
                        callback(n);
                    }
                }
            });
        }

        protected override double EstimateCost()
        {
            double cost = (lchild_().Card() + 10) * (rchild_().Card() + 10);
            return cost;
        }

        public override bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            // TODO: distribution determination, no distribution supplied
            // for NLJoin, consider only singleton situation
            if (!required.IsDistributionSupplied(DistributionProperty.singleton))
            {
                listchildprops = null;
                return false;
            }
            else
            {
                listchildprops = new List<List<PhysicProperty>>();
                if (required.ordering_.Count > 0)
                {
                    var logic = logic_ as LogicJoin;
                    if (IsExprMatch(required, logic.leftKeys_))
                    {
                        var outerreq = new DistributionProperty();
                        outerreq.ordering_ = required.ordering_;
                        listchildprops.Add(new List<PhysicProperty> { outerreq, DistributionProperty.singleton });
                        return true;
                    }
                    listchildprops = null;
                    return false;
                }
                listchildprops.Add(new List<PhysicProperty> { DistributionProperty.singleton, DistributionProperty.singleton });
                return true;
            }
        }

        internal bool IsExprMatch(PhysicProperty sort, List<Expr> exprs)
        {
            return sort.ordering_.Select(x => x.expr).SequenceEqual(exprs);
        }
    }

    // Key list is a special row
    public class KeyList : Row
    {
        public KeyList(int length) : base(length) { }

        static public KeyList ComputeKeys(ExecContext context, List<Expr> keys, Row input)
        {
            if (keys != null)
            {
                var list = new KeyList(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                    list[i] = keys[i].Exec(context, input);
                return list;
            }
            return new KeyList(0);
        }
    }

    public class TaggedRow
    {
        public Row row_;
        public bool matched_ = false;
        public TaggedRow(Row row) { row_ = row; }
    }

    public class PhysicHashJoin : PhysicJoin
    {
        public PhysicHashJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic, l, r) { }
        public override string ToString() => $"PHJ({lchild_()},{rchild_()}: {Cost()},{InclusiveCost()})";

        protected override ulong EstimateMemory()
        {
            var bytes = lchild_().Card() * lchild_().logic_.EstOutputWidth() * 2;
            return bytes;
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            Debug.Assert(logic_.filter_ != null);

            // recreate the left side and right side key list - can't reuse old values 
            // because earlier optimization time keylist may have wrong bindings
            //
            var logic = logic_ as LogicJoin;
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"var hm{_} = new Dictionary<KeyList, List<TaggedRow>>();";
            }
        }

        public override void Exec(Action<Row> callback)
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
            lchild_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    var lname = $"r{lchild_()._}";
                    context.code_ += $@"
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
            });

            // right side probes the hash table
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"
                if (hm{_}.Count == 0)
                    return;";
            }
            else
            {
                if (hm.Count == 0)
                    return;
            }

            rchild_().Exec(r =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    var rname = $"r{rchild_()._}";
                    context.code_ += $@"
                    if (context.stop_)
                        return;

                    Row fakel{_} = new Row({lchild_().logic_.output_.Count});
                    Row r{_} = new Row(fakel{_}, {rname});
                    var keys{_} = KeyList.ComputeKeys(context, {_logic_}.rightKeys_, r{_});
                    bool foundOneMatch{_} = false;

                    if (hm{_}.TryGetValue(keys{_}, out List<TaggedRow> exist{_}))
                    {{
                        foundOneMatch{_} = true;
                        foreach (var v{_} in exist{_})
                        {{
                            r{_} = new Row(v{_}.row_, {rname});
                            {ExecProjectCode($"r{_}")}";

                    // generate code to @context_code_ in callback
                    callback(null);

                    context.code_ += $@"
                    {codegen_if(semi,
                                "break;")}
                        }}
                    }}
                    else
                    {{
                        // no match for antisemi";

                    if (antisemi)
                    {
                        context.code_ += $@"
                        if (!foundOneMatch{_})
                        {{
                            r{_} = new Row(null, r{_});
                            {ExecProjectCode($"r{_}")}";

                        // generate code to @context_code_ in callback
                        callback(null);

                        context.code_ += $@"
                        }}";
                    }

                    if (right)
                    {
                        context.code_ += $@"
                        if (!foundOneMatch{_})
                        {{
                            r{_} = new Row(new Row{lchild_().logic_.output_.Count}, r{_});
                            {ExecProjectCode($"r{_}")}";

                        // generate code to @context_code_ in callback
                        callback(null);

                        context.code_ += $@"
                        }}";
                    }

                    context.code_ += $@"
                    }}
                    ";
                }
                else
                {
                    if (context.stop_)
                        return;

                    Row fakel = new Row(lchild_().logic_.output_.Count);
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
                            n = new Row(new Row(lchild_().logic_.output_.Count), r);
                            n = ExecProject(n);
                            callback(n);
                        }
                    }
                }
            });

            // left join shall examine hash table and output all non-matched rows
            if (left)
            {
                foreach (var v in hm)
                    foreach (var r in v.Value)
                    {
                        if (!r.matched_)
                        {
                            var n = new Row(r.row_, new Row(rchild_().logic_.output_.Count));
                            n = ExecProject(n);
                            callback(n);
                        }
                    }
            }
        }

        protected override double EstimateCost()
        {
            var buildcost = lchild_().Card() * 2.0;
            var probecost = rchild_().Card() * 1.0;
            var outputcost = logic_.Card() * 1.0;
            return buildcost + probecost + outputcost;
        }
        public override bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            // cannot handle order property
            if (required.ordering_.Count > 0)
            {
                listchildprops = null;
                return false;
            }

            var logic = logic_ as LogicJoin;
            logic.CreateKeyList();

            listchildprops = new List<List<PhysicProperty>>();

            switch (required.distribution_.disttype)
            {
                case DistributionType.Singleton:
                    listchildprops.Add(new List<PhysicProperty> { DistributionProperty.singleton, DistributionProperty.singleton });
                    return true;
                case DistributionType.Replicated:
                    listchildprops.Add(new List<PhysicProperty> { DistributionProperty.replicated, DistributionProperty.replicated });
                    return true;
            }
            Debug.Assert(required.distribution_.disttype == DistributionType.Distributed
                || required.distribution_.disttype == DistributionType.Any);
            if (required.IsDistributionSupplied(new DistributionProperty(logic.leftKeys_))
                || required.IsDistributionSupplied(new DistributionProperty(logic.rightKeys_)))
            {
                listchildprops.Add(new List<PhysicProperty> { new DistributionProperty(logic.leftKeys_), new DistributionProperty(logic.rightKeys_) });
                listchildprops.Add(new List<PhysicProperty> { DistributionProperty.singleton, new DistributionProperty(logic.rightKeys_) });
                listchildprops.Add(new List<PhysicProperty> { new DistributionProperty(logic.leftKeys_), DistributionProperty.singleton });
                if (required.distribution_.disttype == DistributionType.Any)
                {
                    listchildprops.Add(new List<PhysicProperty> { DistributionProperty.replicated, PhysicProperty.nullprop });
                    listchildprops.Add(new List<PhysicProperty> { DistributionProperty.singleton, new DistributionProperty(logic.rightKeys_) });
                    listchildprops.Add(new List<PhysicProperty> { new DistributionProperty(logic.leftKeys_), DistributionProperty.singleton });
                }
                return true;
            }
            else
            {
                listchildprops = null;
                return false;
            }
        }
    }

    public abstract class PhysicAgg : PhysicNode
    {
        public PhysicAgg(LogicAgg logic, PhysicNode l) : base(logic) => children_.Add(l);

        public Row AggrCoreToRow(Row input)
        {
            var aggfns = (logic_ as LogicAgg).aggrFns_;
            Row r = new Row(aggfns.Count);
            return r;
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

        public void FinalizeAGroupRow(ExecContext context, Row keys, Row aggvals, Action<Row> callback)
        {
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;

            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"
                    for (int i = 0; i < {aggrcore.Count}; i++)
                        aggvals{_}[i] = aggrcore{_}[i].Finalize(context, aggvals{_}[i]);
                    var r{_} = new Row(keys{_}, aggvals{_});
                    if ({(logic.having_ is null).ToLower()} || {_logic_}.having_.Exec(context, r{_}) is true)
                    {{
                        {ExecProjectCode($"r{_}")}";

                // generate code to @context_code_ in callback
                callback(null);

                context.code_ += $@"
                    }}";
            }
            else
            {
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
    }

    public class PhysicHashAgg : PhysicAgg
    {
        public PhysicHashAgg(LogicAgg logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PHashAgg({child_()}: {Cost()})";

        protected override ulong EstimateMemory()
        {
            var bytes = Card() * logic_.EstOutputWidth() * 2;
            return bytes;
        }

        protected override double EstimateCost()
        {
            return (child_().Card() * 1.0) + (logic_.Card() * 2.0);
        }

        public override bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            listchildprops = new List<List<PhysicProperty>>();
            // hash agg can only support singleton with no order requirement by default
            if (required.Equals(DistributionProperty.singleton))
            {
                listchildprops.Add(new List<PhysicProperty> { DistributionProperty.singleton });
                return true;
            }
            listchildprops = null;
            return false;
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"var aggrcore{_} = {_logic_}.aggrFns_;
                   var hm{_} = new Dictionary<KeyList, Row>();";
            }
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;
            var hm = new Dictionary<KeyList, Row>();

            // aggregation is working on aggCore targets
            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    var lrow = $"r{child_()._}";
                    context.code_ += $@" 
                        var keys = KeyList.ComputeKeys(context, {_logic_}.groupby_, {lrow});";
                    context.code_ += $@"
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
                    var keys = KeyList.ComputeKeys(context, logic.groupby_, l);
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
            });

            // stitch keys+aggcore into final output
            if (context.option_.optimize_.use_codegen_)
            {
                if (logic.groupby_ is null)
                {
                    context.code_ += $@"
                    if (hm{_}.Count == 0)
                    {{
                        Row r{_} = {_physic_}.HandleEmptyResult(context);
                        if (r{_} != null)
                        {{";

                    // generate code to @context_code_ in callback
                    callback(null);

                    context.code_ += $@"
                        }}
                    }}
                    ";
                }
                context.code_ += $@"
                foreach (var v{_} in hm{_})
                {{
                    if (context.stop_)
                        break;

                    var keys{_} = v{_}.Key;
                    Row aggvals{_} = v{_}.Value;";

                FinalizeAGroupRow(context, null, null, callback);

                context.code_ += $@"
                }}";
            }
            else
            {
                if (logic.groupby_ is null && hm.Count == 0)
                {
                    Row row = HandleEmptyResult(context);
                    if (row != null)
                        callback(row);
                }
                foreach (var v in hm)
                {
                    if (context.stop_)
                        break;
                    FinalizeAGroupRow(context, v.Key, v.Value, callback);
                }
            }
        }
    }

    public class PhysicStreamAgg : PhysicAgg
    {
        public PhysicStreamAgg(LogicAgg logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PStreamAgg({child_()}: {Cost()})";
        public override bool IsPropertySatisfied(PhysicProperty required, out List<List<PhysicProperty>> listchildprops)
        {
            listchildprops = new List<List<PhysicProperty>>();

            var exprlist = (logic_ as LogicAgg).groupby_;
            var prop = new SortOrderProperty(exprlist);

            // stream agg can support certain order property
            // but distribution must be singleton
            if (required.distribution_.disttype == DistributionType.Singleton
                && required.IsPropertySupplied(prop))
            {
                listchildprops.Add(new List<PhysicProperty> { prop });
                return true;
            }
            listchildprops = null;
            return false;
        }
        protected override double EstimateCost()
        {
            return logic_.Card() * 2.0;
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"var aggrcore{_} = {_logic_}.aggrFns_;
                    KeyList curGroupKey{_} = null;
                    Row curGroupRow{_} = null;";
            }
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrFns_;
            KeyList curGroupKey = null;
            Row curGroupRow = null;

            // aggregation is working on aggCore targets
            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    var lrow = $"r{child_()._}";
                    context.code_ += $@"
                        var keys = KeyList.ComputeKeys(context, {_logic_}.groupby_, {lrow});";
                    context.code_ += $@"
                        if (context.stop_)
                            return;

                        if (curGroupKey{_} != null && keys.Equals(curGroupKey{_}))
                        {{
                            for (int i = 0; i < {aggrcore.Count}; i++)
                            {{
                                var old = curGroupRow{_}[i];
                                curGroupRow{_}[i] = aggrcore{_}[i].Accum(context, old, {lrow});
                            }}
                        }}
                        else
                        {{
                            if (curGroupKey{_} != null)
                            {{
                                var keys{_} = curGroupKey{_};
                                Row aggvals{_} = curGroupRow{_};";

                    FinalizeAGroupRow(context, null, null, callback);

                    context.code_ += $@"
                            }}
                            curGroupRow{_} = {_physic_}.AggrCoreToRow({lrow});
                            curGroupKey{_} = keys;
                            for (int i = 0; i < {aggrcore.Count}; i++)
                                curGroupRow{_}[i] = aggrcore{_}[i].Init(context, {lrow});
                        }}";
                }
                else
                {
                    if (context.stop_)
                        return;

                    var keys = KeyList.ComputeKeys(context, logic.groupby_, l);
                    if (curGroupKey != null && keys.Equals(curGroupKey))
                    {
                        for (int i = 0; i < aggrcore.Count; i++)
                        {
                            var old = curGroupRow[i];
                            curGroupRow[i] = aggrcore[i].Accum(context, old, l);
                        }
                    }
                    else
                    {
                        // output current grouped row if any
                        if (curGroupKey != null)
                            FinalizeAGroupRow(context, curGroupKey, curGroupRow, callback);

                        // start a new grouped row
                        curGroupRow = AggrCoreToRow(l);
                        curGroupKey = keys;
                        for (int i = 0; i < aggrcore.Count; i++)
                            curGroupRow[i] = aggrcore[i].Init(context, l);
                    }
                }
            });

            // special handling for emtpy resultset and last row
            if (context.option_.optimize_.use_codegen_)
            {
                if (logic.groupby_ is null)
                {
                    context.code_ += $@"
                    if (curGroupRow{_} is null)
                    {{
                        Row r{_} = {_physic_}.HandleEmptyResult(context);
                        if (r{_} != null)
                        {{";

                    // generate code to @context_code_ in callback
                    callback(null);

                    context.code_ += $@"
                        }}
                    }}
                    ";
                }

                context.code_ += $@"
                if (curGroupKey{_} != null && !context.stop_)
                {{
                    var keys{_} = curGroupKey{_};
                    Row aggvals{_} = curGroupRow{_};";

                FinalizeAGroupRow(context, null, null, callback);

                context.code_ += $@"
                }}";
            }
            else
            {
                if (logic.groupby_ is null && curGroupRow is null)
                {
                    Row row = HandleEmptyResult(context);
                    if (row != null)
                        callback(row);
                }
                // when there are still remaining output
                // and output rows is not beyond limit
                if (curGroupKey != null && !context.stop_)
                    FinalizeAGroupRow(context, curGroupKey, curGroupRow, callback);
            }
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"POrder({child_()}: {Cost()},{InclusiveCost()})";

        protected override ulong EstimateMemory()
        {
            var bytes = child_().Card() * logic_.EstOutputWidth();
            return bytes;
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"var set{_} = new List<Row>();";
            }
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

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicOrder;
            var set = new List<Row>();

            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"set{_}.Add(r{child_()._});";
                }
                else
                    set.Add(l);
            });

            set.Sort(compareRow);
            context.code_ += codegen($@"set{_}.Sort({_physic_}.compareRow);");

            // output sorted set
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"
                foreach (var rs{_} in set{_})
                {{
                    if (context.stop_)
                        break;
                    var r{_} = {_physic_}.ExecProject(rs{_});";

                // generate code to @context_code_ in callback
                callback(null);

                context.code_ += $@"
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
        }

        protected override double EstimateCost()
        {
            var rowstosort = child_().Card() * 1.0;
            double cost = rowstosort * (0.1 + Math.Log(rowstosort));
            return cost;
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        List<Row> cteCache_ = null;

        public override string ToString() => $"PFrom({(logic_ as LogicFromQuery)}: {Cost()})";

        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public bool IsCteConsumer(out CTEQueryRef qref) => (logic_ as LogicFromQuery).IsCteConsumer(out qref);

        public override void Open(ExecContext context)
        {
            base.Open(context);
            var logic = logic_ as LogicFromQuery;
            if (logic.IsCteConsumer(out CTEQueryRef qref))
                cteCache_ = context.TryGetCteProducer(qref.cte_.cteName_);
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicFromQuery;

            if (cteCache_ != null)
            {
                ExecAsCteConsumer(callback);
                return;
            }

            child_().Exec(l =>
            {
                if (context.stop_)
                    return;

                if (logic.queryRef_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(l);
                callback(r);
            });
        }

        public void ExecAsCteConsumer(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicFromQuery;

            foreach (Row l in cteCache_)
            {
                if (context.stop_)
                    break;

                if (logic.queryRef_.colRefedBySubq_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(l);
                callback(r);
            }
        }

        protected override double EstimateCost()
        {
            return child_().Card() * 1.0;
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string ToString() => $"PFILTER({child_()}: {Cost()})";

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            Expr filter = (logic_ as LogicFilter).filter_;

            child_().Exec(l =>
            {
                if (context.stop_)
                    return;

                if (filter is null || filter.Exec(context, l) is true)
                {
                    var r = ExecProject(l);
                    callback(r);
                }
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

        protected override double EstimateCost()
        {
            return child_().Card() * 1.0;
        }
    }

    public class PhysicInsert : PhysicNode
    {
        public PhysicInsert(LogicInsert logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(Action<Row> callback)
        {
            var table = (logic_ as LogicInsert).targetref_.Table();
            var nparts = QueryOption.num_machines_;

            child_().Exec(l =>
            {
                // insert data row according to distribution
                if (table.distMethod_ == TableDef.DistributionMethod.Replicated)
                {
                    for (int i = 0; i < nparts; i++)
                        table.distributions_[i].heap_.Add(l);
                }
                else
                {
                    int partid = 0;
                    if (table.distMethod_ == TableDef.DistributionMethod.Roundrobin)
                        partid = Utils.mod(Catalog.rand_.Next(), nparts);
                    else if (table.distMethod_ == TableDef.DistributionMethod.Distributed)
                    {
                        var distrord = table.distributedBy_.ordinal_;
                        partid = Utils.mod(l[distrord].GetHashCode(), nparts);
                    }
                    table.distributions_[partid].heap_.Add(l);
                }
            });
        }
    }

    public class PhysicResult : PhysicNode
    {
        public PhysicResult(LogicResult logic) : base(logic) { }
        public override string ToString() => $"PResult";

        public override void Exec(Action<Row> callback)
        {
            Row r = ExecProject(null);
            callback(r);
        }
        protected override double EstimateCost() => logic_.Card() * 1.0;
    }

    public class PhysicProfiling : PhysicNode
    {
        public Int64 nrows_ = 0;
        public Int64 nloops_ = 0;

        public override string ToString() => $"${child_()}";

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;

            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $"{_physic_}.nloops_ ++;";
            }
            else
            {
                nloops_++;
            }

            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"
                    {_physic_}.nrows_++;
                    var r{_} = r{child_()._};";
                    callback(null);
                }
                else
                {
                    nrows_++;
                    callback(l);
                }
            });
        }

        protected override double EstimateCost() => 0;
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);

        public override void Open(ExecContext context)
        {
            context.Reset();
            base.Open(context);
        }

        public override void Close()
        {
            // @context_ is null after @base.Close()
            // is issued.
            // Keep the reference of @context_ in this
            // stack to append '}}' to @context_.code_
            // after @base.Close() is issued.
            var ctx = context_;
            base.Close();

            string s = "}}";
            ctx.code_ += s;
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var child = (child_() is PhysicProfiling) ?
                    child_().child_() : child_();
            var output = child.logic_.output_;
            var ncolumns = output.Count(x => x.isVisible_);

            if (context.option_.optimize_.use_codegen_)
            {
                string header = $@"
                    /*
                    --- plan ---
                    {this.Explain()}
                    */
                ";
                CodeWriter.Reset(header);
            }

            child_().Exec(r =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"
                        Row newr = new Row({ncolumns});";
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].isVisible_)
                        {
                            context.code_ += $"newr[{i}] = r{child_()._}[{i}];";
                        }
                    }
                    context.code_ += $"{_physic_}.rows_.Add(newr);";
                    context.code_ += $"Console.WriteLine(newr);";
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
                    if (context.option_.explain_.mode_ >= ExplainMode.full)
                        Console.WriteLine($"{newr}");
                }
            });
        }
    }

    public class PhysicAppend : PhysicNode
    {
        public PhysicAppend(LogicAppend logic, PhysicNode l, PhysicNode r) : base(logic) { children_.Add(l); children_.Add(r); }
        public override string ToString() => $"PAPPEND({lchild_()},{rchild_()}): {Cost()})";

        protected override double EstimateCost()
        {
            return logic_.Card() * 1;
        }
        public override void Open(ExecContext context)
        {
            base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
            }
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;

            foreach (var child in children_)
            {
                child.Exec(r =>
                {
                    if (context.option_.optimize_.use_codegen_)
                    {
                        callback(null);
                    }
                    else
                    {
                        callback(r);
                    }
                });
            }
        }
    }

    public class PhysicLimit : PhysicNode
    {
        public PhysicLimit(LogicLimit logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PLIMIT({child_()}: {Cost()})";

        protected override double EstimateCost()
        {
            return logic_.Card() * 1.0;
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"var nrows{_} = 0;";
            }
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            int nrows = 0;
            int limit = (logic_ as LogicLimit).limit_;

            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"                    
                    nrows{_}++;
                    Debug.Assert(nrows{_} <= {limit});
                    if (nrows{_} == {limit})
                        context.stop_ = true;
                    var r{_} = r{child_()._};";
                    callback(null);
                }
                else
                {
                    nrows++;
                    Debug.Assert(nrows <= limit);
                    if (nrows == limit)
                        context.stop_ = true;
                    callback(l);
                }
            });
        }
    }

    public abstract class PhysicRemoteExchange : PhysicNode
    {
        internal bool asConsumer_ { get; set; } = true;
        // consumer only: channel it recieve from
        internal ExchangeChannel channel_ { get; set; }
        // producer only: channels it shall send data to, so the number equals nubmer of machines
        internal List<ExchangeChannel> upChannels_ { get; set; }
        public PhysicRemoteExchange(LogicRemoteExchange logic, PhysicNode l) : base(logic)
        {
            Debug.Assert(asConsumer_); children_.Add(l);
        }

        public override void Close()
        {
            var code = "";

            context_.code_ += code;
            if (!asConsumer_)
            {
                base.Close();
            }
        }

        // this function emulates a full serialization of a given physic node for network
        // transfer purpose, so it shall include every information we'd like the receiver
        // have, including the physic node itself and its logic node and could be more.
        //
        public PhysicNode EmulateSerialization(PhysicNode node)
        {
            var newnode = node.Clone();
            return newnode;
        }
        public virtual string OpenConsumer(ExecContext context) => null;
        public virtual string OpenProducer(ExecContext context) => null;
        protected string createConsumerStartThread(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var planId = _;

            Debug.Assert(context.machineId_ >= 0);
            var machineId = context.machineId_;
            var wo = new WorkerObject(Thread.CurrentThread.Name,
                                    context.machines_,
                                    machineId,
                                    planId,
                                    EmulateSerialization(this),
                                    context.option_);
            var thread = new Thread(new ThreadStart(wo.EntryPoint));
            thread.Name = $"Redis_{planId}@{machineId}";
            thread.Start();

            upChannels_ = null;
            channel_ = new ExchangeChannel(dop);
            context.machines_.RegisterChannel(planId, machineId, channel_);
            return null;
        }
        protected string createProducerAndChannel(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var planId = _;

            // establish all up channels
            Debug.Assert(upChannels_ is null);
            upChannels_ = new List<ExchangeChannel>();
            for (int i = 0; i < dop; i++)
            {
                var channel = context.machines_.WaitForChannelReady(planId, i);
                upChannels_.Add(channel);
            }

            channel_ = null;
            return null;
        }
        public override void Open(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            var code = asConsumer_ ? OpenConsumer(context) : OpenProducer(context);

            // only producer inherits the bottom half of the plan
            if (!asConsumer_)
                base.Open(context);
            else
            {
                Debug.Assert(context_ is null);
                context_ = context;
            }
        }

        public virtual void ExecConsumer(Action<Row> callback) { }
        public virtual void ExecProducer(Action<Row> callback) { }
        protected void redisBroadExec(Action<Row> callback)
        {
            ExecContext context = context_;

            Row r;
            while ((r = channel_.Recv()) != null)
            {
                callback(r);
            }
        }
        protected virtual List<int> machineList(Row r)
        {
            // default is send to all machines (broadcast)
            var outlist = new List<int>();

            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;

            outlist.AddRange(Enumerable.Range(0, dop));
            return outlist;
        }
        protected void sendProducerResult(Action<Row> callback)
        {
            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;

            child_().Exec(r =>
            {
                if (!context.option_.optimize_.use_codegen_)
                {
                    var machines = machineList(r);
                    foreach (var idx in machines)
                    {
                        upChannels_[idx].Send(r);
                    }
                }
            });

            for (int i = 0; i < dop; i++)
                upChannels_[i].MarkSendDone(context.machineId_);
        }

        public override void Exec(Action<Row> callback)
        {
            if (asConsumer_)
            {
                ExecConsumer(callback);
            }
            else
            {
                ExecProducer(callback);
            }
        }

        protected override double EstimateCost()
        {
            return logic_.Card() * 0.1;
        }
    }

    public class PhysicGather : PhysicRemoteExchange
    {
        public PhysicGather(LogicGather logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PGATHER({child_()}: {Cost()})";

        // cache serialized output for looped case
        private List<Row> cache_ { get; set; }

        public override string OpenConsumer(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            var logic = logic_ as LogicGather;
            List<int> targets = logic.producerIds_;
            int dop = targets.Count;

            // create producer threads, establish connections and set up 
            // communication channel etc. It uses threads to emulate execution
            // among a pool of machines.
            //
            channel_ = new ExchangeChannel(dop);

            Debug.Assert(context.machines_ != null);
            List<Thread> workers = new List<Thread>();
            foreach (var machineId in targets)
            {
                var planId = _;
                var wo = new WorkerObject(Thread.CurrentThread.Name,
                                        context.machines_,
                                        machineId,
                                        planId,
                                        EmulateSerialization(this),
                                        context.option_);
                var thread = new Thread(new ThreadStart(wo.EntryPoint));
                thread.Name = $"Gather_{planId}@{machineId}";
                workers.Add(thread);
            }
            workers.ForEach(x => x.Start());

            return null;
        }

        public override string OpenProducer(ExecContext context)
        {
            Debug.Assert(channel_ != null);
            return null;
        }

        public override void ExecConsumer(Action<Row> callback)
        {
            ExecContext context = context_;

            if (cache_ != null)
            {
                foreach (var row in cache_)
                    callback(row);
            }
            else
            {
                cache_ = new List<Row>();
                Row r;
                while ((r = channel_.Recv()) != null)
                {
                    callback(r);
                    cache_.Add(r);
                }
            }
        }

        public override void ExecProducer(Action<Row> callback)
        {
            var context = context_ as DistributedContext;

            child_().Exec(r =>
            {
                if (!context.option_.optimize_.use_codegen_)
                {
                    channel_.Send(r);
                }
            });

            channel_.MarkSendDone(context.machineId_);
        }
        protected override double EstimateCost()
        {
            // penalize gather to discourage serialization at bottom
            return child_().Card() * 10.0;
        }
    }

    public class PhysicBroadcast : PhysicRemoteExchange
    {
        public PhysicBroadcast(LogicBroadcast logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PBROADCAST({child_()}: {Cost()})";

        public override string OpenConsumer(ExecContext context)
            => createConsumerStartThread(context);
        public override string OpenProducer(ExecContext context)
            => createProducerAndChannel(context);

        public override void ExecConsumer(Action<Row> callback)
            => redisBroadExec(callback);
        public override void ExecProducer(Action<Row> callback)
            => sendProducerResult(callback);

        protected override double EstimateCost()
        {
            return child_().Card() * 2.0;
        }
    }

    public class PhysicRedistribute : PhysicRemoteExchange
    {
        public PhysicRedistribute(LogicRedistribute logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PREDISTRIBUTE({child_()}: {Cost()})";

        public override string OpenConsumer(ExecContext context)
            => createConsumerStartThread(context);
        public override string OpenProducer(ExecContext context)
            => createProducerAndChannel(context);

        public override void ExecConsumer(Action<Row> callback)
            => redisBroadExec(callback);
        public override void ExecProducer(Action<Row> callback)
            => sendProducerResult(callback);
        protected override List<int> machineList(Row r)
        {
            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var distributeby = (logic_ as LogicRedistribute).distributeby_;

            var outlist = new List<int>();
            var key = KeyList.ComputeKeys(context_, distributeby, r);
            var sendtoMachine = Utils.mod(key.GetHashCode(), dop);
            outlist.Add(sendtoMachine);

#if debug
                    var tid = Thread.CurrentThread.ManagedThreadId;
                    Console.WriteLine($"{Thread.CurrentThread.Name} by {tid} => {r} => {sendtoMachine}");
#endif
            return outlist;
        }

        protected override double EstimateCost()
        {
            return child_().Card() * 2.0;
        }
    }

    public class PhysicProjectSet : PhysicNode
    {
        public PhysicProjectSet(LogicProjectSet logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PPRJSET({child_()}: {Cost()})";

        protected override double EstimateCost()
        {
            return logic_.Card() * 1.0;
        }

        int theOnlySRFColumn()
        {
            // FIXME: assuming one SRF column
            var output = logic_.output_;
            var srfcol = -1;
            for (int i = 0; i < output.Count; i++)
            {
                if (output[i] is FuncExpr f && f.isSRF_)
                {
                    srfcol = i;
                    break;
                }
            }

            Debug.Assert(srfcol != -1);
            return srfcol;
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var output = logic_.output_;

            child_().Exec(l =>
            {
                var cache = new List<Row>();
                if (!context.option_.optimize_.use_codegen_)
                {
                    // apply the SRF and fill the cache with multi-rows
                    var r = new Row(output.Count);
                    for (int i = 0; i < output.Count; i++)
                        r[i] = output[i].Exec(context_, l);

                    // for only one SRF, expand it and fill other columns with same value 
                    var srfcol = theOnlySRFColumn();
                    dynamic srfvals = r[srfcol];
                    foreach (var v in srfvals)
                    {
                        var newr = new Row(output.Count);
                        for (int j = 0; j < output.Count; j++)
                        {
                            if (j != srfcol)
                                newr[j] = r[j];
                            else
                                newr[j] = v;
                        }
                        cache.Add(newr);
                    }

                    // return one by one from the cache
                    cache.ForEach(r => callback(r));
                }
            });
        }
    }

    public class PhysicSampleScan : PhysicNode
    {
        Random rand_;

        // members for row count sampling
        Row[] array_;
        int target_;
        int curCnt_;
        int curSample_;

        public PhysicSampleScan(LogicSampleScan logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PSAMPLE({child_()}: {Cost()})";

        protected override double EstimateCost()
        {
            return logic_.Card() * 0.5;
        }

        void RowCntSampling(Row l)
        {
            var logic = logic_ as LogicSampleScan;

            // Reservior sampling
            Debug.Assert(l != null);
            curSample_++;
            if (curCnt_ < target_)
                array_[curCnt_++] = l;
            else
            {
                var r = rand_.Next(0, curSample_);
                if (r < target_)
                    array_[r] = l;
            }
        }

        void PercentSampling(Row l) => throw new NotImplementedException();

        public override void Open(ExecContext context)
        {
            rand_ = new Random();
            base.Open(context);
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicSampleScan;

            if (logic.ByRowCount())
            {
                target_ = logic.sample_.rowcnt_;
                array_ = new Row[target_];
                curCnt_ = 0;
                curSample_ = 0;
            }

            child_().Exec(l =>
            {
                var cache = new List<Row>();
                if (!context.option_.optimize_.use_codegen_)
                {
                    if (logic.ByRowCount())
                        RowCntSampling(l);
                    else
                        PercentSampling(l);
                }
            });

            // now output samples to upper layer
            if (logic.ByRowCount())
            {
                for (int i = 0; i < array_.Length; i++)
                    if (array_[i] != null)
                        callback(array_[i]);
            }
        }
    }
}

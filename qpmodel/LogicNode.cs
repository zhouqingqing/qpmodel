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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using qpmodel.stat;
using qpmodel.expr;
using qpmodel.physic;
using qpmodel.index;
using qpmodel.optimizer;
using qpmodel.utils;

using LogicSignature = System.Int64;
using BitVector = System.Int64;

namespace qpmodel.logic
{
    public class SemanticAnalyzeException : Exception
    {
        public SemanticAnalyzeException(string msg) : base(msg) => Console.WriteLine($"ERROR[Optimizer]: {msg }");
    }

    public abstract class LogicNode : PlanNode<LogicNode>
    {
        public const ulong CARD_INVALID = ulong.MaxValue;

        // TODO: we can consider normalize all node specific expressions into List<Expr> 
        // so processing can be generalized - similar to Expr.children_[]
        //
        public Expr filter_ = null;
        public List<Expr> output_ = new List<Expr>();
        public ulong card_ = CARD_INVALID;

        // these fields are used to avoid recompute - be careful with stale caching
        protected List<TableRef> tableRefs_ = null;
        // tables it contained (children recursively inclusive)
        internal BitVector tableContained_ { get; set; }

        // if the node is derived, the output may not match the request from parent
        internal bool isDerived_ = false;

        // it is possible to really have this value but ok to recompute
        protected LogicSignature logicSign_ = -1;

        public List<TableRef> GetTableRef()
        {
            return tableRefs_;
        }

        public override string ExplainMoreDetails(int depth, ExplainOption option) => ExplainFilter(filter_, depth, option);

        public override string ExplainOutput(int depth, ExplainOption option)
        {
            if (output_.Count != 0)
            {
                string r = "Output: " + string.Join(",", output_);
                output_.ForEach(x => r += x.ExplainExprWithSubqueryExpanded(depth, option));
                return r;
            }
            return null;
        }

        public ulong EstOutputWidth()
        {
            ulong bytes = 0;
            foreach (var v in output_)
            {
                Debug.Assert(v.type_.len_ > 0);
                bytes += (ulong)v.type_.len_;
            }

            return bytes;
        }

        // MarkExchange is implmented in a conservative way, meaning it may adds unnessary shuffle node 
        // to the plan. The right plan shall be in the memo optimization with property enforcement.
        //
        public LogicNode MarkExchange(QueryOption option)
        {
            switch (this)
            {
                case LogicJoin lj:
                    LogicNode leftshuffle, rightshuffle;
                    // when distribution match join keys, redistribution is not necessary
                    if (lchild_() is LogicScanTable ls && ls.tabref_.IsDistributionMatch(lj.leftKeys_, option))
                        leftshuffle = lchild_();
                    else
                        leftshuffle = new LogicRedistribute(lchild_().MarkExchange(option), lj.leftKeys_);
                    if (rchild_() is LogicScanTable rs && rs.tabref_.IsDistributionMatch(lj.rightKeys_, option))
                        rightshuffle = rchild_();
                    else
                        rightshuffle = new LogicRedistribute(rchild_().MarkExchange(option), lj.rightKeys_);
                    lj.children_[0] = leftshuffle;
                    lj.children_[1] = rightshuffle;
                    break;
                default:
                    children_.ForEach(x => x.MarkExchange(option));
                    break;
            }

            return this;
        }

        // This is an honest translation from logic to physical plan
        public PhysicNode DirectToPhysical(QueryOption option)
        {
            PhysicNode result;
            PhysicNode phyfirst = null;
            if (children_.Count != 0)
                phyfirst = children_[0].DirectToPhysical(option);

            switch (this)
            {
                case LogicScanTable ln:
                    // if there are indexes can help filter, use them
                    IndexDef index = null;
                    if (ln.filter_ != null)
                    {
                        if (option.optimize_.enable_indexseek_)
                            index = ln.filter_.FilterCanUseIndex(ln.tabref_);
                        ln.filter_.SubqueryDirectToPhysic();
                    }
                    if (index is null)
                        result = new PhysicScanTable(ln);
                    else
                        result = new PhysicIndexSeek(ln, index);
                    break;
                case LogicJoin lc:
                    var phyleft = phyfirst;
                    var phyright = rchild_().DirectToPhysical(option);
                    Debug.Assert(!lchild_().LeftReferencesRight(rchild_()));
                    switch (lc)
                    {
                        case LogicSingleJoin lsmj:
                            result = new PhysicSingleJoin(lsmj, phyleft, phyright);
                            break;
                        case LogicMarkJoin lmj:
                            result = new PhysicMarkJoin(lmj, phyleft, phyright);
                            break;
                        default:
                            // one restriction of HJ is that if build side has columns used by probe side
                            // subquries, we need to use NLJ to pass variables. It is can be fixed by changing
                            // the way runtime pass parameters though.
                            //
                            bool lhasSubqCol = TableRef.HasColsUsedBySubquries(lchild_().InclusiveTableRefs());
                            if (lc.filter_.FilterHashable() && !lhasSubqCol
                                && (lc.type_ == JoinType.Inner || lc.type_ == JoinType.Left))
                                result = new PhysicHashJoin(lc, phyleft, phyright);
                            else
                                result = new PhysicNLJoin(lc, phyleft, phyright);
                            break;
                    }
                    break;
                case LogicResult lr:
                    result = new PhysicResult(lr);
                    break;
                case LogicFromQuery ls:
                    result = new PhysicFromQuery(ls, phyfirst);
                    break;
                case LogicFilter lf:
                    result = new PhysicFilter(lf, phyfirst);
                    if (lf.filter_ != null)
                        lf.filter_.SubqueryDirectToPhysic();
                    break;
                case LogicInsert li:
                    result = new PhysicInsert(li, phyfirst);
                    break;
                case LogicScanFile le:
                    result = new PhysicScanFile(le);
                    break;
                case LogicAgg la:
                    result = new PhysicHashAgg(la, phyfirst);
                    break;
                case LogicOrder lo:
                    result = new PhysicOrder(lo, phyfirst);
                    break;
                case LogicAnalyze lan:
                    result = new PhysicAnalyze(lan, phyfirst);
                    break;
                case LogicIndex lindex:
                    result = new PhysicIndex(lindex, phyfirst);
                    break;
                case LogicLimit limit:
                    result = new PhysicLimit(limit, phyfirst);
                    break;
                case LogicAppend append:
                    result = new PhysicAppend(append, phyfirst, rchild_().DirectToPhysical(option));
                    break;
                case LogicCteProducer cteproducer:
                    result = new PhysicCteProducer(cteproducer, phyfirst);
                    break;
                case LogicSequence sequence:
                    List<PhysicNode> children = sequence.children_.Select(x => x.DirectToPhysical(option)).ToList();
                    result = new PhysicSequence(sequence, children);
                    break;
                case LogicGather gather:
                    result = new PhysicGather(gather, phyfirst);
                    break;
                case LogicBroadcast bcast:
                    result = new PhysicBroadcast(bcast, phyfirst);
                    break;
                case LogicRedistribute dist:
                    result = new PhysicRedistribute(dist, phyfirst);
                    break;
                case LogicProjectSet ps:
                    result = new PhysicProjectSet(ps, phyfirst);
                    break;
                case LogicSampleScan ss:
                    result = new PhysicSampleScan(ss, phyfirst);
                    break;
                default:
                    throw new NotImplementedException();
            }

            if (option.profile_.enabled_)
                result = new PhysicProfiling(result);
            return result;
        }

        public List<TableRef> InclusiveTableRefs(bool refresh = false)
        {
            if (tableRefs_ is null || refresh)
            {
                List<TableRef> refs = new List<TableRef>();
                if (this is LogicScanTable gx)
                    refs.Add(gx.tabref_);
                else if (this is LogicFromQuery fx)
                {
                    refs.Add(fx.queryRef_);
                    refs.AddRange(fx.queryRef_.query_.bindContext_.AllTableRefs());
                }
                else
                {
                    foreach (var v in children_)
                        refs.AddRange(v.InclusiveTableRefs(refresh));
                }

                tableRefs_ = refs;
            }

            return tableRefs_;
        }

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source, bool idonly = false)
        {
            Debug.Assert(toclone.bounded_);
            Debug.Assert(toclone._ != null);
            var clone = toclone.Clone();

            // first try to match the whole expression - don't do this for ColExpr
            // because it has no practial benefits.
            // 
            if (!(clone is ColExpr)&&!(clone is MarkerExpr))
            {
                int ordinal = source.FindIndex(clone.Equals);
                // for derived child node, compare only the expression id
                if (idonly && ordinal == -1)
                    ordinal = source.FindIndex(clone.IDEquals);

                if (ordinal != -1)
                    return new ExprRef(clone, ordinal);
            }

            // the LogicOrExpr with Marker should be consider
            if (clone is LogicOrExpr)
            {
                Expr temp;
                if (clone.lchild_() is ExprRef)
                    temp = clone.lchild_().child_();
                else
                    temp = clone.lchild_();
                int ordinal = source.FindIndex(temp.Equals);
                if (ordinal != -1)
                {
                    var exprRef = new ExprRef(temp, ordinal);
                    clone.children_[0] = exprRef;//left
                }

                if (clone.rchild_() is ExprRef)
                    temp = clone.rchild_().child_();
                else
                    temp = clone.rchild_();
                ordinal = source.FindIndex(temp.Equals);
                if (ordinal != -1)
                {
                    var exprRef = new ExprRef(temp, ordinal);
                    clone.children_[1] = exprRef;//right
                }
            }
            /*
             * We need to resolve the aggregates here or the next loop will descend into aggregates
             * and try to resolve the column arguments and they may not be found in all cases.
             * The example that breaks:
             * select a1, a2  from a where a.a1 = (select sum(b1) from b where b2 = a2 and b3<4);
             * filter: a.a1 = sum(b1), source a1, a2, sum(b1)
             * going into sum, and trying to resolve b1 will fail and the assert
             * source.FindAll(roughNameTest).Count >= 1 will be false. if this assert is ignored, and
             * aggregates are resolved at the end or elsewhere, nothing bad happens but
             * ignoring the assertion means legtimate problems will also go uncaught here and
             * may cause servere problem elsewhere.
             */
            clone = FixAggregateOrdinals(clone, source);

            // we have to use each ColExpr and fix its ordinal
            clone.VisitEachIgnoreRef<ColExpr>(target =>
            {
                // ignore system column, ordinal is known
                if (target is SysColExpr)
                    return;

                Predicate<Expr> roughNameTest;
                roughNameTest = z => target.Equals(z) || target.colName_.Equals(z.outputName_);

                // using source's matching index for ordinal
                // fix colexpr's ordinal - leave the outerref as it is already decided in ColExpr.Bind()
                // During execution, the bottom node will be responsible to fill the value via AddParam().
                //
                if (!target.isParameter_)
                {
                    target.ordinal_ = source.FindIndex(roughNameTest);

                    // we may hit more than one target, say t2.col1 matching {t1.col1, t2.col1}
                    // in this case, we shall redo the mapping with table name
                    //
                    Debug.Assert(source.FindAll(roughNameTest).Count >= 1);
                    if (source.FindAll(roughNameTest).Count > 1)
                    {
                        Predicate<ColExpr> nameAndTableMatch = z =>
                            (target.colName_.Equals(z.outputName_) || target.colName_.Equals(z.colName_))
                                && target.tabRef_.Equals(z.tabRef_);
                        Predicate<Expr> preciseNameTest = z => (z is ColExpr zc && nameAndTableMatch(zc))
                            || (z is ExprRef ze && ze.expr_() is ColExpr zec && nameAndTableMatch(zec));
                        target.ordinal_ = source.FindIndex(preciseNameTest);
                        Debug.Assert(source.FindAll(preciseNameTest).Count == 1);
                    }
                }
                Debug.Assert(target.ordinal_ != -1);
            });

            return clone;
        }

        internal Expr FixAggregateOrdinals(Expr clone, List<Expr> source)
        {
            // let's first fix Aggregation as a whole epxression - there could be some combinations
            // of AggrRef with aggregation keys (ColExpr), so we have to go thorough after it
            //
            clone.VisitEachT<AggrRef>(target =>
            {
                Predicate<Expr> roughNameTest;
                roughNameTest = z => target.Equals(z);
                target.ordinal_ = source.FindIndex(roughNameTest);
                if (target.ordinal_ != -1)
                {
                    Debug.Assert(source.FindAll(roughNameTest).Count == 1);
                }
            });
            clone.VisitEachT<AggFunc>(target =>
            {
                Predicate<Expr> roughNameTest;
                roughNameTest = z => target.Equals(z);
                int ordinal = source.FindIndex(roughNameTest);
                if (ordinal != -1)
                    clone = clone.SearchAndReplace(target, new ExprRef(target, ordinal));
            });

            return clone;
        }

        // output_ may adjust column ordinals after ordinal fixing, so we have to re-register them for codeGen
        protected void RefreshOutputRegisteration()
        {
            output_.ForEach(x => ExprSearch.table_[x._] = x);
        }

        // fix each expression by using source's ordinal and make a copy
        internal List<Expr> CloneFixColumnOrdinal(List<Expr> toclone, List<Expr> source, bool removeRedundant)
        {
            var clone = new List<Expr>();

            bool idonly = false;
            children_.ForEach(x =>
            {
                if (x.isDerived_)
                    idonly = true;
            });

            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source, idonly)));
            Debug.Assert(clone.Count == toclone.Count);

            if (removeRedundant)
                return clone.Distinct().ToList();
            return clone;
        }

        public virtual LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                logicSign_ = GetHashCode();
            return logicSign_;
        }

        // resolve mapping from children output
        // 1. you shall first compute the reqOutput by accouting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        // this parent class implments a default one for using first child output without any change. You can
        // also piggy back in this method to "finalize" your logic node.
        //
        public virtual List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            children_[0].ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_ = children_[0].output_;
            RefreshOutputRegisteration();
            return ordinals;
        }

        public ulong Card()
        {
            if (card_ == CARD_INVALID)
                card_ = EstimateCard();
            Debug.Assert(card_ != CARD_INVALID);
            return card_;
        }

        public ulong EstimateCard()
        {
            return CardEstimator.DoEstimation(this);
        }

        // retrieve all correlated filters on the subtree
        public List<Expr> RetrieveCorrelated(bool returnColExprOnly)
        {
            List<Expr> results = new List<Expr>();
            VisitEach(x =>
            {
                var logic = x as LogicNode;
                var filterExpr = logic.filter_;

                if (filterExpr != null)
                {
                    if (returnColExprOnly)
                        results.AddRange(filterExpr.FilterGetCorrelatedCol());
                    else
                        results.AddRange(filterExpr.FilterGetCorrelatedFilter());
                }
            });

            return results;
        }

        public List<Expr> RetrieveCorrelatedFilters() => RetrieveCorrelated(false);
    }

    public static class LogicHelper
    {
        public static bool LeftReferencesRight(this LogicNode l, LogicNode r)
        {
            var ltables = l.InclusiveTableRefs();
            var rtables = r.InclusiveTableRefs();

            // if right does not even referenced by any subqueries, surely left does not reference right
            bool rhasSubqCol = TableRef.HasColsUsedBySubquries(rtables);
            if (!rhasSubqCol)
                return false;

            // now examine each correlated expr from left, so if it references right
            var listexpr = l.RetrieveCorrelatedFilters();
            foreach (var v in listexpr)
            {
                var refs = v.FilterGetOuterRef();
                if (rtables.Intersect(refs).Any())
                    return true;
            }

            return false;
        }

        public static void SwapJoinSideIfNeeded(this LogicJoin join)
        {
            var oldl = join.lchild_(); var oldr = join.rchild_();
            if (oldl.LeftReferencesRight(oldr))
            {
                join.children_[0] = oldr;
                join.children_[1] = oldl;
            }
        }
    }

    // LogicMemoRef wrap a CMemoGroup as a LogicNode (so CMemoGroup can be used in plan tree)
    //
    public class LogicMemoRef : LogicNode
    {
        public CMemoGroup group_;

        public LogicNode Deref() => child_();
        public T Deref<T>() where T : LogicNode => (T)Deref();

        public LogicMemoRef(CMemoGroup group)
        {
            Debug.Assert(group != null);
            var child = group.exprList_[0].logic_;

            children_.Add(child);
            group_ = group;
            tableContained_ = child.tableContained_;

            Debug.Assert(filter_ is null);
            Debug.Assert(!(Deref() is LogicMemoRef));
            Debug.Assert(group.memo_.LookupCGroup(Deref()) == group);
        }
        public override string ToString() => group_.ToString();

        public override LogicSignature MemoLogicSign() => Deref().MemoLogicSign();
        public override int GetHashCode() => (int)MemoLogicSign();
        public override bool Equals(object obj)
        {
            if (obj is LogicMemoRef lo)
                return lo.MemoLogicSign() == MemoLogicSign();
            return false;
        }
    }

    public enum JoinType
    {
        // ANSI SQL specified join types can show in SQL statement
        Inner,
        Left,
        Right,
        Full,
        Cross
        ,
        // these are used by subquery expansion or optimizations (say PK/FK join)
        Semi,
        AntiSemi,
    };

    public partial class LogicJoin : LogicNode
    {
        public JoinType type_ { get; set; } = JoinType.Inner;

        // dervided information from join condition
        // ab join cd on c1+d1=a1-b1 and a1+b1=c2+d2;
        //    leftKey_:  a1-b1, a1+b1
        //    rightKey_: c1+d1, c2+d2
        //
        public List<Expr> leftKeys_ = new List<Expr>();
        public List<Expr> rightKeys_ = new List<Expr>();
        internal List<string> ops_ = new List<string>();

        public override string ToString() => $"({lchild_()} {type_} {rchild_()})";
        public override string ExplainInlineDetails() { return type_ == JoinType.Inner ? "" : type_.ToString(); }
        public LogicJoin(LogicNode l, LogicNode r)
        {
            children_.Add(l); children_.Add(r);
            if (l != null && r != null)
            {
                Debug.Assert(0 == (l.tableContained_ & r.tableContained_));
                tableContained_ = SetOp.Union(l.tableContained_, r.tableContained_);
            }
        }
        public LogicJoin(LogicNode l, LogicNode r, Expr filter) : this(l, r)
        {
            filter_ = filter;
        }

        public bool IsInnerJoin() => type_ == JoinType.Inner && !(this is LogicMarkJoin);

        public override LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                logicSign_ = lchild_().MemoLogicSign() ^ rchild_().MemoLogicSign() ^ filter_.FilterHashCode();
            return logicSign_;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ (filter_?.GetHashCode() ?? 0);
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicJoin lo)
            {
                return base.Equals(lo) && (filter_?.Equals(lo.filter_) ?? true);
            }
            return false;
        }

        public bool AddFilter(Expr filter)
        {
            filter_ = filter_.AddAndFilter(filter);
            CreateKeyList();
            return true;
        }

        internal void CreateKeyList()
        {
            void createOneKeyList(BinExpr fb)
            {
                var ltabrefs = lchild_().InclusiveTableRefs();
                var rtabrefs = rchild_().InclusiveTableRefs();
                var lkeyrefs = fb.lchild_().tableRefs_;
                var rkeyrefs = fb.rchild_().tableRefs_;
                if (ltabrefs.ContainsList(lkeyrefs))
                {
                    leftKeys_.Add(fb.lchild_());
                    rightKeys_.Add(fb.rchild_());
                    ops_.Add(fb.op_);
                }
                else
                {
                    // switch side if possible
                    //  E.g., a.a1+b.b1 = 3 is not possible to decompose to left right side
                    //  without transform to a.a1=3=b.b1 (hashable)
                    // 
                    // we can't do this early during filter normalization because join side 
                    // is not decided yet that time.
                    //
                    if (rtabrefs.ContainsList(lkeyrefs))
                    {
                        leftKeys_.Add(fb.rchild_());
                        rightKeys_.Add(fb.lchild_());
                        ops_.Add(BinExpr.SwapSideOp(fb.op_));
                    }
                }

                Debug.Assert(leftKeys_.Count == rightKeys_.Count);
            }

            // reset the old values
            leftKeys_.Clear();
            rightKeys_.Clear();
            ops_.Clear();

            // cross join does not have join filter
            if (filter_ != null)
            {
                var andlist = filter_.FilterToAndList();
                foreach (var v in andlist)
                {
                    Debug.Assert(v is BinExpr);
                    createOneKeyList(v as BinExpr);
                }
            }
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>(reqOutput);
            if (filter_ != null)
                reqFromChild.Add(filter_);

            // push to left and right: to which side depends on the TableRef it contains
            var ltables = lchild_().InclusiveTableRefs();
            var rtables = rchild_().InclusiveTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();

            Expr thisReq = null;
            foreach (var v in reqFromChild)
            {
                if(!(v is  MarkerExpr)||!(this is LogicMarkJoin))
                {
                    var tables = v.CollectAllTableRef();
                    if (ltables.ContainsList(tables))
                        lreq.Add(v);
                    else if (rtables.ContainsList(tables))
                        rreq.Add(v);
                    else
                    {
                        // the whole list can't push to the children (Eg. a.a1 + b.b1)
                        // decompose to singleton and push down
                        var colref = v.RetrieveAllColExpr();
                        colref.ForEach(x =>
                        {
                            if (ltables.Contains(x.tableRefs_[0]))
                                lreq.Add(x);
                            else if (rtables.Contains(x.tableRefs_[0]))
                                rreq.Add(x);
                            else
                                throw new InvalidProgramException($"requests contains invalid tableref {x.tableRefs_[0]}");
                        });
                    }

                    // Let the count(*) be counted!
                    // When remove_from is removed a query like
                    // select b1+c100 from (select count(*) as b1 from b) a, (select c1 c100 from c) c where c100>1
                    // count(*) does down to b as required output which is not valid.
                    // The fix is to require (0) from the child referenced by the count(*) here
                    // and include count(*) tablRefs in CollectAllTableRef().
                    var agcs = v.RetrieveAllType<AggCountStar>();
                    agcs.ForEach(y =>
                    {
                        y.tableRefs_.ForEach(z =>
                        {
                            if (ltables.Contains(z))
                                lreq.Add(y);
                            else if (rtables.Contains(z))
                                rreq.Add(y);
                            else
                                throw new InvalidProgramException($"requests contains invalid tableref {z.alias_}");
                        });
                    });
                }
                else
                {
                    thisReq = v;// the lefted markjoin is requested by this
                }
            }
            // get left and right child to resolve columns
            lchild_().ResolveColumnOrdinal(lreq.ToList());
            var lout = lchild_().output_;
            rchild_().ResolveColumnOrdinal(rreq.ToList());
            var rout = rchild_().output_;
            Debug.Assert(lout.Intersect(rout).Count() == 0);

            // assuming left output first followed with right output
            var childrenout = lout.ToList(); childrenout.AddRange(rout.ToList());
            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, childrenout);
            if (this is LogicMarkJoin)
            {
                Debug.Assert(thisReq != null);// there must be a markExpr produce by this LogicMarkJoin
                childrenout.Add(thisReq);
            }
            output_ = CloneFixColumnOrdinal(reqOutput, childrenout, removeRedundant);
           
            RefreshOutputRegisteration();
            CreateKeyList();
            return ordinals;
        }
    }

    public partial class LogicFilter : LogicNode
    {
        public bool movable_ = true;
        public override string ToString() => $"filter({child_()}): {filter_}";

        // public override string ExplainInlineDetails(int depth) => movable_ ? "" : "***";
        public override int GetHashCode() => base.GetHashCode() ^ filter_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LogicFilter lo)
            {
                return base.Equals(lo) && filter_.Equals(lo.filter_);
            }
            return false;
        }

        public LogicFilter(LogicNode child, Expr filter)
        {
            children_.Add(child); filter_ = filter;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // a1 = max(b1) => a1, max(b1)
            void addColumnAndAggFuncs(Expr expr, HashSet<Expr> list)
            {
                if (!(expr is AggFunc))
                {
                    if ((expr is ColExpr ec && !ec.isParameter_)||expr is MarkerExpr)
                        list.Add(expr);
                    foreach (var v in expr.children_)
                        addColumnAndAggFuncs(v, list);
                }
                else
                    list.Add(expr);
            }

            List<int> ordinals = new List<int>();

            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());

            // filter may carray ordinary columns and aggregations - aggregation won't show up
            // by SQL syntax, but subquery transformation may introduce them. We want to all 
            // aggfuncs, all columns except those inside aggfuncs.
            //
            var list = new HashSet<Expr>();
            addColumnAndAggFuncs(filter_, list);
            reqFromChild.AddRange(list);

            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            filter_ = CloneFixColumnOrdinal(filter_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public partial class LogicAgg : LogicNode
    {
        public List<Expr> rawAggrs_; // original parser time aggregations
        public List<Expr> groupby_;
        public Expr having_;

        public bool isLocal_ = false; // default is global agg

        // runtime info: derived from output request, a subset of rawAggrs_[]
        //  Example:
        //      rawAggrs_ may include max(i), max(i), max(i)+sum(j)
        //      aggrFns_ => max(i), sum(j)
        //
        public List<AggFunc> aggrFns_ = new List<AggFunc>();
        public override string ToString() => $"Agg({child_()})";

        // record the derived node expression map
        public Dictionary<Expr, Expr> deriveddict_;

        // derived node must have the same logicSign_ to be in the same group
        public void Overridesign(LogicAgg node)
            => logicSign_ = node.logicSign_;
        public override LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                logicSign_ = (child_().MemoLogicSign() << 32) + ((isLocal_.GetHashCode() ^ 
                    having_.FilterHashCode() ^ rawAggrs_.ListHashCode() ^ groupby_.ListHashCode()) >> 32);
            return logicSign_;
        }
        public override string ExplainMoreDetails(int depth, ExplainOption option)
        {
            string r = null;
            string tabs = Utils.Spaces(depth + 2);
            if (aggrFns_.Count > 0)
                r += $"Aggregates: {string.Join(", ", aggrFns_)}";
            if (groupby_ != null)
                r += $"{(aggrFns_.Count > 0 ? "\n" + tabs : "")}Group by: {string.Join(", ", groupby_)}";
            if (having_ != null)
                r += $"{("\n" + tabs)}{ExplainFilter(having_, depth, option)}";
            return r;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, List<Expr> aggrs, Expr having)
        {
            children_.Add(child); groupby_ = groupby; rawAggrs_ = aggrs; having_ = having;
        }

        // key: b1+b2
        // x: b1+b2+3 => true, b1+b2 => false
        bool exprConsistPureKeys(Expr x, List<Expr> keys)
        {
            var constTrue = ConstExpr.MakeConstBool(true);
            if (keys is null)
                return false;
            if (keys.Contains(x))
                return false;

            Expr xchanged = x.Clone();
            foreach (var v in keys)
                xchanged = xchanged.SearchAndReplace(v, constTrue);
            if (!xchanged.VisitEachExists(
                    e => e.IsLeaf() && !(e is ConstExpr) && !e.Equals(constTrue)))
                return true;
            return false;
        }

        bool ExprIsAggFnOnAggrRef(Expr expr)
        {
            return expr is AggFunc ea && ea.HasAggrRef();
        }

        List<AggFunc> reqlistGetAggrRefs(List<Expr> reqList)
        {
            List<AggFunc> list = new List<AggFunc>();
            foreach (var v in reqList)
            {
                Debug.Assert(!(v is AggrRef));
                v.VisitEachT<AggrRef>(x =>
                {
                    list.Add(x.aggr_() as AggFunc);
                });
            }

            Debug.Assert(!list.Contains(null));
            return list.Distinct().ToList();
        }

        // say 
        //  keys: k1+k2,k2+k3 
        //  reqOutput: (k1+k2)+(k3+k2), count(*), sum(a1*b1),  sum(count(a1)[0] as AggrRef + b3)
        //  => 0; a1, b1; count(a1)[0], b3
        //
        // we don't allow single count(a2)[0] as AggrRef form.
        List<Expr> removeAggFuncAndKeyExprsFromOutput(List<Expr> reqList, List<Expr> keys)
        {
            var reqContainAggs = new List<Expr>();

            var fns = reqlistGetAggrRefs(reqList);

            // first let's find out all elements containing any AggFunc
            reqList.ForEach(x =>
            {
                Debug.Assert(!(x is AggrRef));
                x.VisitEachT<AggFunc>(y =>
                {
                    // 1+abs(min(a))+max(b)
                    reqContainAggs.Add(x);
                });
            });

            // we also remove expressions only with key computations - this non-trivial
            // consider this:
            //    keys: a1+a2, a2+a3
            //    expr: (a1+a2+a2)+a3, sum(a+a2+(a3+a2)), 
            //  with some arrangement, we can map expr to keys
            //
            // the exception is SRF in group by keys, push them down to ProjectSet node
            //
            if (keys?.Count > 0)
            {
                reqList.ForEach(x =>
                {
                    if (exprConsistPureKeys(x, keys))
                        reqContainAggs.Add(x);
                });

                keys.ForEach(x =>
                {
                    if (x is FuncExpr fx && fx.isSRF_)
                    {
                        Debug.Assert(child_() is LogicProjectSet);
                        reqList.Add(x);
                    }
                });
            }

            // now remove AggFunc but add back the dependent exprs, excluding AggrRef
            // sum(b2*sum[0]+count[0]+b3) => b2, b3
            reqContainAggs.ForEach(x =>
            {
                // remove the element from reqList
                reqList.Remove(x);

                // add back the dependent exprs back
                x.VisitEachIgnoreRef<AggFunc>(y =>
                {
                    foreach (var z in y.GetNonFuncExprList())
                    {
                        if (!exprConsistPureKeys(y, keys) && !z.HasAggrRef())
                            reqList.Add(z);
                    }
                });
            });

            reqList = reqList.Distinct().ToList();
            reqList.ForEach(x => Debug.Assert(!x.HasAggFunc()));
            reqList.AddRange(fns);
            return reqList;
        }

        List<Expr> sourceListSearchReplaceWithGroupBy(List<Expr> sourceList)
        {
            bool IsGroupByElement(Expr e) => groupby_.Contains(e) || e is AggFunc;
            Expr RepalceWithGroupbyRef(Expr e)
            {
                if (e is AggFunc)
                {
                    return e;
                }
                else
                {
                    for (int i = 0; i < groupby_.Count; i++)
                    {
                        var g = groupby_[i];
                        e = e.SearchAndReplace(g, new ExprRef(g, i));
                    }
                }

                return e;
            }

            List<Expr> newlist = new List<Expr>();
            sourceList.ForEach(x =>
            {
                x = x.SearchAndReplace<Expr>(IsGroupByElement, RepalceWithGroupbyRef);
                x.ResetAggregateTableRefs();
                newlist.Add(x);
            });
            return newlist;
        }

        // map the original requested expressions to derived expressions
        List<Expr> replaceDerivedExpr(List<Expr> sourceList)
        {
            bool IsAggFuncInDict(Expr e) => deriveddict_.ContainsKey(e);
            Expr ReplaceWithLong(AggFunc agg) => deriveddict_[agg];

            List<Expr> newlist = new List<Expr>();
            sourceList.ForEach(x =>
            {
                x = x.SearchAndReplace<AggFunc>(IsAggFuncInDict, ReplaceWithLong);
                newlist.Add(x);
            });
            return newlist;
        }

        internal List<Expr> GenerateAggrFns(bool isResolveColumnOrdinal = true)
        {
            if (!isResolveColumnOrdinal)
                output_ = rawAggrs_;

            // Bound aggrs to output, so when we computed aggrs, we automatically get output
            // Here is an example:
            //  output_: <literal>, cos(a1*7)+sum(a1),  sum(a1) + sum(a2+a3)*2
            //                       |           \       /          |   
            //                       |            \     /           |   
            //  groupby_:            a1            \   /            |
            //  aggrFns_:                        sum(a1),      sum(a2+a3)
            // =>
            //  output_: <literal>, cos(ref[0]*7)+ref[1],  ref[1]+ref[2]*2
            //
            var nkeys = groupby_?.Count ?? 0;
            var newoutput = new List<Expr>();
            if (groupby_ != null)
            {
                // output_ shall use exprRef of groupby_ expressions but do not do so for any
                // aggregation functions since they directly work on child input row. Same to
                // having_ expressions.
                //
                output_ = sourceListSearchReplaceWithGroupBy(output_);
            }
            output_.ForEach(x =>
            {
                x.VisitEachIgnoreRef<AggFunc>(y =>
                {
                    // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                    if (!aggrFns_.Contains(y))
                        aggrFns_.Add(y);
                    x = x.SearchAndReplace(y, new ExprRef(y, nkeys + aggrFns_.IndexOf(y)));
                });
                newoutput.Add(x);
            });
            if (having_ != null && groupby_ != null)
                having_ = sourceListSearchReplaceWithGroupBy(new List<Expr>() { having_ })[0];
            having_?.VisitEachIgnoreRef<AggFunc>(y =>
            {
                // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                if (!aggrFns_.Contains(y))
                    aggrFns_.Add(y);
                having_ = having_.SearchAndReplace(y, new ExprRef(y, nkeys + aggrFns_.IndexOf(y)));
            });
            Debug.Assert(aggrFns_.Count == aggrFns_.Distinct().Count());
            return newoutput;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            var derivedGlobal = isDerived_ && !isLocal_;
            var processedOutput = derivedGlobal ? replaceDerivedExpr(reqOutput) : reqOutput;

            // reqOutput may contain ExprRef which is generated during FromQuery removal process, remove them
            var reqList = processedOutput.CloneList(new List<Type> { typeof(ConstExpr) });

            // request from child including reqOutput and filter. Don't use whole expression
            // matching push down like k+k => (k+k)[0] instead, we need k[0]+k[0] because 
            // aggregation only projection values from hash table(key, value).
            //
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(removeAggFuncAndKeyExprsFromOutput(reqList, groupby_));

            // Issue exposed by removing remove_from.
            // Remeber the last position of output required by the parent, it is not an error
            // if the offending occures after this position.
            // The query that fails is
            // select d1, sum(d2) from (select c1/2, sum(c1) from (select b1, count(*) as a1 from b group by b1)c(c1, c2)
            // group by c1/2) d(d1, d2) group by d1;
            // LogicPlan will be Agg(Agg(Agg(b)))
            // While resolving second level Agg, group by is b1 / 2, reqOutput is b1/2, sum(b1), b1
            // after removeAggFuncAndKeyExprsFromOutput, the list is b1/2, b1
            // after adding group by expressions/columns it is b1 / 2, b1, b1
            // child output is b1/2, b1
            // after new aggregates are generated, our output is b1/2 {expref}, sum(b1) {expref}, b1 {colref} added by us
            // will not be changed into ExprRef because it is not grouping expression. This sets the offending and
            // raises the error column x must appear in group by clause.
            //
            int grpbyColumnAddPosition = reqFromChild.Count;

            // It is ideal to add keys_ directly to reqFromChild but matching can be harder.
            // Consider the following case:
            //   keys/having: a1+a2, a3+a4
            //   reqOutput: a1+a3+a2+a4
            // Let's fix this later
            //
            if (groupby_ != null)
            {
                if (derivedGlobal) reqFromChild.AddRange(groupby_);
                else reqFromChild.AddRange(groupby_.RetrieveAllColExpr());
            }
            if (having_ != null)
            {
                if (derivedGlobal)
                {
                    reqFromChild.AddRange(removeAggFuncAndKeyExprsFromOutput(new List<Expr> { having_.lchild_() }, groupby_));
                    reqFromChild.AddRange(having_.rchild_().RetrieveAllColExpr());
                }
                else reqFromChild.AddRange(having_.RetrieveAllColExpr());
            }
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            if (groupby_ != null)
                groupby_ = CloneFixColumnOrdinal(groupby_, childout, true);
            if (having_ != null)
                having_ = CloneFixColumnOrdinal(having_, childout);

            output_ = CloneFixColumnOrdinal(processedOutput, childout, removeRedundant);

            var newoutput = GenerateAggrFns();

            // Say invvalid expression means contains colexpr (replaced with ref), then the output shall
            // contains no expression consists invalid expression
            // TODO: This check on offending, and offendingFirstPos are not as good as they
            // should be and therefore some queries will fail to compile or run if remove_from
            // is enabled. This needs to be refined to cover all invalid cases but none that are
            // valid, i.e, don't throw when remove_from is enabled and it looks like there are
            // column references in the group by that are not in group by, aggregates in select list.
            // The reference query:
            // select d1, sum(d2) from (select c1/2, sum(c1) from (select b1, count(*) as a1 from b group by b1)c(c1, c2) group by c1/2) d(d1, d2) group by d1;
            //
            int offendingFirstPos = -1, offendingPos = 0;
            Expr offending = null;
            newoutput.ForEach(x =>
            {
                if (x.VisitEachExists(y => y is ColExpr, new List<Type> { typeof(ExprRef) }))
                {
                    offending = x;
                    if (offendingFirstPos == -1)
                        offendingFirstPos = offendingPos;
                }
                ++offendingPos;
            });
            if (offending != null && offendingFirstPos < grpbyColumnAddPosition)
                    throw new SemanticAnalyzeException($"column {offending} must appear in group by clause");
            output_ = newoutput;
            if (having_?.VisitEachExists(y => y is ColExpr, new List<Type> { typeof(ExprRef) }) ?? false)
                throw new SemanticAnalyzeException($"column {offending} must appear in group by clause");

            RefreshOutputRegisteration();
            return ordinals;
        }

    }

    public class LogicOrder : LogicNode
    {
        internal List<Expr> orders_ = new List<Expr>();
        internal List<bool> descends_ = new List<bool>();

        public override string ToString() => $"Order({child_()})";

        public override string ExplainMoreDetails(int depth, ExplainOption option)
        {
            var r = $"Order by: {string.Join(", ", orders_)}\n";
            return r;
        }

        public LogicOrder(LogicNode child, List<Expr> orders, List<bool> descends)
        {
            children_.Add(child);
            orders_ = orders;
            descends_ = descends;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());

            // do not decompose order_ into ColRefs: the reason is that say GROUP BY 1 ORDER BY 1 where 1 
            // is actully a3+a4, if we decompose it, then Aggregation will see a3 and a4 thus require them
            // in the GROUP BY clause.
            //
            reqFromChild.AddRange(orders_);
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            orders_ = CloneFixColumnOrdinal(orders_, childout, false);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public class LogicFromQuery : LogicNode
    {
        public QueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>({child_()})";
        public override string ExplainInlineDetails()
        {
            string str;
            if (queryRef_ is CTEQueryRef cq)
            {
                str = cq.cte_.cteName_;
                if (!cq.cte_.cteName_.Equals(queryRef_.alias_))
                    str += $" as {queryRef_.alias_}";
            }
            else
                str = queryRef_.alias_;
            return $"<{str}>";
        }

        public bool IsCteConsumer(out CTEQueryRef qref)
        {
            qref = null;
            if (queryRef_ is CTEQueryRef cq)
            {
                qref = cq;
                return true;
            }
            return false;
        }

        public LogicFromQuery(QueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            var query = queryRef_.query_;
            query.logicPlan_.ResolveColumnOrdinal(query.selection_);

            var childout = queryRef_.AllColumnsRefs();
            output_ = CloneFixColumnOrdinal(reqOutput, childout, false);

            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = queryRef_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            RefreshOutputRegisteration();
            return ordinals;
        }

        public override int GetHashCode() => queryRef_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LogicFromQuery fo)
                return fo.queryRef_.Equals(queryRef_);
            return false;
        }
    }

    public class LogicGet<T> : LogicNode where T : TableRef
    {
        public T tabref_;

        public LogicGet(T tab) => tabref_ = tab;
        public override string ToString() => tabref_.ToString();
        public override string ExplainInlineDetails() => ToString();
        public override int GetHashCode() => base.GetHashCode() ^ (filter_?.GetHashCode() ?? 0) ^ tabref_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LogicGet<T> lo)
                return base.Equals(lo) && (filter_?.Equals(lo.filter_) ?? true) && tabref_.Equals(lo.tabref_);
            return false;
        }

        public bool AddFilter(Expr filter)
        {
            // constant filter (true|false) we are ok
            Debug.Assert(filter.CollectAllTableRef(false).Count <= 1);
            filter_ = filter_.AddAndFilter(filter);
            return true;
        }

        void validateReqOutput(List<Expr> reqOutput)
        {
            reqOutput.ForEach(x =>
            {
                x.VisitEach(y =>
                {
                    switch (y)
                    {
                        case ConstExpr ly:    // select 2+3, ...
                        case SubqueryExpr sy:   // select ..., sx = (select b1 from b limit 1) from a;
                        case MarkerExpr my: // markjoin
                            break;
                        default:
                            // aggfunc shall never pushed to me
                            Debug.Assert(!(y is AggFunc));

                            // a single table column ref, or combination of them say "c1+c2+7"
                            Debug.Assert(!y.IsConst());
                            Debug.Assert(y.EqualTableRef(tabref_));
                            break;
                    }
                });
            });
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            List<Expr> columns = tabref_.AllColumnsRefs();

            // Verify it can be an litral, or only uses my tableref
            validateReqOutput(reqOutput);

            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, columns);
            output_ = CloneFixColumnOrdinal(reqOutput, columns, false);

            // Finally, consider outerrefs to this table: if they are not there, add them
            output_ = tabref_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public partial class LogicScanTable : LogicGet<BaseTableRef>
    {
        public LogicScanTable(BaseTableRef tab) : base(tab) { }
    }

    public class LogicScanFile : LogicGet<ExternalTableRef>
    {
        public string FileName() => tabref_.filename_.Trim();
        public LogicScanFile(ExternalTableRef tab) : base(tab) { }
    }

    public class LogicInsert : LogicNode
    {
        public BaseTableRef targetref_;
        public LogicInsert(BaseTableRef targetref, LogicNode child)
        {
            targetref_ = targetref;
            children_.Add(child);
        }
        public override string ToString() => targetref_.ToString();
        public override string ExplainInlineDetails() => ToString();

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            Debug.Assert(output_.Count == 0);

            // insertion is always the top node 
            Debug.Assert(!removeRedundant);
            return children_[0].ResolveColumnOrdinal(reqOutput, removeRedundant);
        }
    }

    public class LogicResult : LogicNode
    {
        public override string ToString() => string.Join(",", output_);
        public LogicResult(List<Expr> exprs) => output_ = exprs;
        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true) => null;
    }

    public class LogicAppend : LogicNode
    {
        public override string ToString() => $"Append({lchild_()},{rchild_()})";

        public LogicAppend(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }

        // LogicAppend only needs to resolve column ordinals of its first child because others
        // already solved in ResolveOrdinals().
    }

    public class LogicLimit : LogicNode
    {

        internal int limit_;

        public override string ToString() => $"Limit({child_()})";
        public override string ExplainInlineDetails() => $"({limit_})";

        public LogicLimit(LogicNode child, Expr expr)
        {
            children_.Add(child);

            Utils.Assumes(expr.IsConst());
            expr.TryEvalConst(out Object val);
            limit_ = (int)val;
            if (limit_ <= 0)
                throw new SemanticAnalyzeException("limit shall be positive");
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // limit is the top node and don't remove redundant
            return base.ResolveColumnOrdinal(reqOutput, false);
        }
    }

    // Remote exchange operators: Gather, Broadcast and Redistribution
    //  Gather: start the execution on many other machines
    //  Redistribution: start execution in current machine with result set redistribution
    //  Broadcast: execution mode is the same as redistribution but broadcast result set
    //
    public class LogicRemoteExchange : LogicNode
    {
        public LogicRemoteExchange(LogicNode child)
        {
            children_.Add(child);
        }
    }

    public class LogicGather : LogicRemoteExchange
    {
        public List<int> producerIds_;

        public LogicGather(LogicNode child, List<int> producerIds = null) : base(child)
        {
            producerIds_ = producerIds;
            if (producerIds_ is null)
                producerIds_ = new List<int>(Enumerable.Range(0, QueryOption.num_machines_));
        }
        public override string ToString() => $"Gather({child_()})";

        int countAllParallelFragments()
        {
            int nthreads = QueryOption.num_machines_;
            int nshuffle = 0;
            VisitEach(x =>
            {
                if (x is LogicRedistribute || x is LogicBroadcast)
                    nshuffle++;
            });
            return nthreads * (1 + nshuffle);
        }

        public override string ExplainInlineDetails() => $"Threads: {countAllParallelFragments()}";
    }

    public class LogicBroadcast : LogicRemoteExchange
    {
        public List<int> consumerIds_;
        public LogicBroadcast(LogicNode child, List<int> consumerIds = null) : base(child)
        {
            consumerIds_ = consumerIds;
            if (consumerIds_ is null)
                consumerIds_ = new List<int>(Enumerable.Range(0, QueryOption.num_machines_));
        }

        public override string ToString() => $"Broadcast({child_()})";
    }

    public class LogicRedistribute : LogicRemoteExchange
    {
        public List<int> consumerIds_;
        public List<Expr> distributeby_ { get; set; }
        public LogicRedistribute(LogicNode child, List<Expr> distributeby, List<int> consumerIds = null) : base(child)
        {
            distributeby_ = distributeby;
            consumerIds_ = consumerIds;
            if (consumerIds_ is null)
                consumerIds_ = new List<int>(Enumerable.Range(0, QueryOption.num_machines_));
        }
        public override string ToString() => $"Redistribute({child_()})";
        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and distributeby
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.AddRange(distributeby_);
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            distributeby_ = CloneFixColumnOrdinal(distributeby_, childout, false);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            RefreshOutputRegisteration();
            return new List<int>();
        }
    }

    // Consider this query:
    //    ... FROM a GROUP BY generate_series(1, a2), generate_series(1, a3)
    // because generate_series() is a SRF, so each row from a, it will generate
    // multiple rows. To implement this, for each row, ProjectSet invokes target
    // functions once, cache the rows into a result set and returning one row
    // each time. The plan looks like this:
    // ---
    //  Aggregate
    //       Group Key: generate_series(1, a.a2), generate_series(1, a.a3)
    //       ->  ProjectSet
    //           Output: generate_series(1, a2), generate_series(1, a3)
    //           ->  Scan table a
    //
    public class LogicProjectSet : LogicNode
    {
        public LogicProjectSet(LogicNode child)
        {
            children_.Add(child);
        }
        public override string ToString() => $"ProjectSet({child_()})";

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            var ordinals = new List<int>();

            var reqFromChild = new List<Expr>();
            foreach (var v in reqOutput)
            {
                if (v is FuncExpr fv && fv.isSRF_)
                    reqFromChild.AddRange(v.RetrieveAllColExpr());
            }

            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public class LogicSampleScan : LogicNode
    {
        internal SelectStmt.TableSample sample_;

        public LogicSampleScan(LogicNode child, SelectStmt.TableSample sample)
        {
            children_.Add(child);
            sample_ = sample;
        }
        public bool ByRowCount() => sample_.percent_ is double.NaN;

        public override string ToString() => $"SampleScan({child_()})";
    }
}

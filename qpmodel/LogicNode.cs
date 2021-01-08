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
using qpmodel.stream;

using LogicSignature = System.Int64;
using BitVector = System.Int64;

namespace qpmodel.logic
{
    public class SemanticAnalyzeException : Exception
    {
        public SemanticAnalyzeException(string msg) : base(msg) => Console.WriteLine($"ERROR[Optimizer]: {msg }");
    }

    // A New framework for ordinal resolution.
    // A per statement global counter of expressions may be needed.
    // At first, only simple column expressions and subquries are going to be experimented with, no joins and no aggregations.
    // The current outputs_ is *** not really *** about where the output of the given expression goes to,
    // it is actually about where the input for that expression comes from. Another way to look at it
    // is to say that where in the children's output is the value for this expression located?
    // A simple query such as:
    //      SELECT * FROM a
    // presents no issues,
    // The child is table scan and creates [a1][a2][a3][a4] and outputs the same, so the
    // parent's outputs_ will is a.a1{0}, a.a2{1}, a.a3{2}, a.a4{3}, meaning, parent has to
    // look at positions 0, 1, 2, and 3 in its input row for values of a1, a2, a3, and a4
    // respectivley.
    //
    // The new framework uses two helper data structures to simplify the ordinal resolution.
    // Every plan node may endup containing these two as members, or just use them locally.
    // (1) ValueIdList keeps distinct expression which the outputs of the node in the order they
    // occur in its required outputs (for top level select, it is simply the selections).
    // initially, the ordinals their ordinal positions.
    // (2) ValueIdSet is for efficient lookup and also for making sure ValueIdList doesn't have
    // duplicates. ValueIdSet keeps references to the actual expressions by mapping their exprid/hash
    // to expressions.
    //
    // select b.b2, sum(c.c3), a.a2, count(*) from c, a, b where a.a4 = b.b4 and a.a4 > 1 and b.b4 < 5 and c2 <> b3 group by a.a2, b.b2;
    // ValueId should also contain the hash or whatever uniqueid of the expression so that
    // it doesn't have to be recomputed everytime.
    internal class ValueId
    {
        Expr expRef_ = null;
        int ordinal_ = -1;

        public ValueId()
        {
            expRef_ = null;
            ordinal_ = -1;
        }
        public ValueId(Expr e, int o)
        {
            expRef_ = e;
            ordinal_ = o;
        }
        public void SetExpr(Expr e) { expRef_ = e; }
        public void SetOrdinal(int o) { ordinal_ = o; }
        public void SetExprOrdinal(Expr e, int o) { expRef_ = e; ordinal_ = o; }
        public Expr GetExpr() { return expRef_; }
        public int GetOrdinal() { return ordinal_; }
    }

    // Encapsulate all the details of ordinal resolution.
    // Keep ValueIdSet, ValuIdList and other artifacts here and offer
    // simple interface.
    internal class OrdinalResolver
    {
        Dictionary<int, ValueId> valueSet;
        List<ValueId> valueList;

        Dictionary<int, ValueId> columnSet;
        List<ValueId> columnList;           // column references in the order of their appearence.

        Dictionary<int, ValueId> fixedChildren = null;
        Expr fixedFilter = null;
        Expr fixedHaving = null;
        List<Expr> fixedGroupby = null;
        List<Expr> fixedOrderby = null;
        List<Expr> fixedOutput = null;

        public OrdinalResolver(List<Expr> requiredOutputs = null)
        {
            valueSet = new Dictionary<int, ValueId>();
            valueList = new List<ValueId>();
            columnSet = new Dictionary<int, ValueId>();
            columnList = new List<ValueId>();

            if (requiredOutputs != null)
                AddRequired(requiredOutputs);
        }

        // Add inputs required by the parent.
        public void AddRequired(List<Expr> exprList)
        {
            int colOrd = 0;
            foreach (var e in exprList)
            {
                int h = e.GetHashCode();
                if (!valueSet.ContainsKey(h))
                {
                    int ord = valueList.Count;
                    ValueId vid = new ValueId(e, ord);
                    valueList.Add(vid);
                    valueSet.Add(h, vid);

                    if (e is ColExpr ce)
                    {
                        AddRequiredColumn(ce);
                    }
                    else e.VisitEach(x =>
                    {
                        if (x is ColExpr cx)
                            AddRequiredColumn(cx);
                    });
                }
            }
        }

        public void SetFixedChildren(List<Expr> childout)
        {
            int i = 0;
            fixedChildren = new Dictionary<int, ValueId>();
            childout.ForEach(x =>
            {
                int h = x.GetHashCode();
                ValueId vid = new ValueId(x, i++);
                if (!fixedChildren.ContainsKey(h))
                    fixedChildren.Add(h, vid);
            });
        }
        public void FixGroupby(List<Expr> groupby)
        {
            if (groupby == null)
                return;
        }

        public void FixHaving(Expr having)
        {
            if (having == null)
                return;
        }

        public void FixOrderby(List<Expr> orderby)
        {
            if (orderby == null)
                return;
        }
        public void FixOutput()
        {
            foreach (var o in valueList)
            {
                Expr e = o.GetExpr();
                int eh = e.GetHashCode();
                ValueId vid;
                if ((vid = FindValueId(eh)) != null)
                {
                    ValueId cid = FindChild(eh);
                    vid.SetOrdinal(cid.GetOrdinal());
                }
                else
                {
                    // it is an expression?
                    // deal with it.
                    Expr ne = FixExpr(e, eh);
                }
            }
        }

        public void FixFilter(Expr filter)
        {
            if (filter == null)
                return;
        }

        // Debug
        public void PrintOrdinals(List<Expr> currOut)
        {
            Console.Write("CUR: ");
            foreach (var c in currOut)
            {
                Console.Write(getExprString(c) + " : ");
            }
            Console.Write("\nNEW: ");

            foreach (var v in valueList)
            {
                Console.Write(getExprString(v.GetExpr()) + "{" + v.GetOrdinal() + "} : ");
            }
            Console.WriteLine("\n");
        }

        public void AssertOrdinals(List<Expr> reqOutput, List<Expr> actOutput)
        {
            // DEBUG stuff {
            Console.WriteLine("SEL:");
            for (int i = 0; i < reqOutput.Count; ++i)
            {
                string str = getExprString(reqOutput[i]);
                Console.Write(str + " : ");
            }

            Console.WriteLine("\n\nACT:");
            for (int i = 0; i < actOutput.Count; ++i)
            {
                string str = getExprString(actOutput[i]);
                Console.Write(str + " : ");
            }

            Console.WriteLine("\n\nEXP:");
            for (int i = 0; i < valueList.Count; ++i)
            {
                string str = getExprString(valueList[i].GetExpr());
                Console.Write("{" + str + ", " + valueList[i].GetOrdinal() + "}, ");
            }
            Console.WriteLine("\n\n");
            foreach (var expr in actOutput)
            {
                bool foundIt = true;
                int h1 = expr.GetHashCode();
                int o1 = -1;
                foundIt = FindExprByHash(h1, out o1);
                Debug.Assert(foundIt == true);

                ValueId vid = valueSet.GetValueOrDefault(h1);
                int h2 = vid.GetExpr().GetHashCode();
                int o2 = vid.GetOrdinal();

                Debug.Assert((o1 < 0 || o1 == o2) && h1 == h2);
            }
        }

        internal void AddRequiredColumn(ColExpr ce)
        {
            int ch = ce.GetHashCode();
            if (!columnSet.ContainsKey(ch))
            {
                ValueId vid = new ValueId(ce, columnList.Count);
                columnSet.Add(ch, vid);
                columnList.Add(vid);
            }
        }

        // Fix one expression. Clone it and fix the ordinals
        // in the whole subexpression tree rooted at expr.
        internal Expr FixExpr(Expr expr, int eh)
        {
            Expr clone = expr.Clone();

            // fix it
            return clone;
        }

        internal string getExprString(Expr e)
        {
            string str;
            if (e is AggrRef ar)
                str = ar.aggr_().ToString();
            else if (e is ExprRef er)
                str = er.expr_().ToString();
            else
                str = e.ToString();

            return str;
        }

        internal ValueId FindValueId(int h)
        {
            ValueId vid = valueSet.GetValueOrDefault(h);
            return vid != null ? vid : null;
        }

        internal ValueId FindChild(int h)
        {
            ValueId vid = fixedChildren.GetValueOrDefault(h);
            return vid != null ? vid : null;
        }
        internal bool FindExprByHash(int h, out int ord)
        {
            for (int i = 0; i < valueList.Count; ++i)
            {
                if (h == valueList[i].GetExpr().GetHashCode())
                {
                    ord = i;
                    return true;
                }
            }

            ord = -1;
            return false;
        }
    }

    public abstract class LogicNode : PlanNode<LogicNode>
    {
        // TODO: we can consider normalize all node specific expressions into List<Expr> 
        // so processing can be generalized - similar to Expr.children_[]
        //
        public Expr filter_ = null;
        public List<Expr> output_ = new List<Expr>();
        public ulong? card_;

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

        // MarkExchange is implemented in a conservative way, meaning it may adds unnecessary shuffle node 
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
                    if (ln is LogicScanStream)
                        result = new PhysicScanStream(ln);
                    else
                    {
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
                    }
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
                            // subqueries, we need to use NLJ to pass variables. It is can be fixed by changing
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

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source, List<Expr> output = null, bool idonly = false)
        {
            Debug.Assert(toclone.bounded_);
            Debug.Assert(toclone._ != null);
            var clone = toclone.Clone();

            // first try to match the whole expression - don't do this for ColExpr
            // because it has no practical benefits.
            // 
            if (!(clone is ColExpr) && !(clone is MarkerExpr))
            {
                int ordinal = source.FindIndex(clone.Equals);
                // for derived child node, compare only the expression id
                if (idonly && ordinal == -1)
                    ordinal = source.FindIndex(clone.IDEquals);

                if (ordinal != -1)
                    return new ExprRef(clone, ordinal);
            }

            // the marker's ordinal should equal to a2, i.e. the frontest index in disappear colExpr
            // a1 a2 b2 
            // a1 marker (a2,b2 disappeared )
            if (clone is MarkerExpr)
            {
                var findExpr = source.Find(x => x._ == clone._);
                var findIndex = source.FindIndex(x => x._ == clone._);
                if (findExpr is ExprRef)
                {
                    return new ExprRef(clone, findIndex);
                }
                else
                {
                    Debug.Assert(this is LogicMarkJoin);
                    int markerOrdinal = source.FindIndex(x => output.Find(y => y._ == x._) == null);
                    Debug.Assert(markerOrdinal != -1);
                    // MarkJoin will project out some columns 
                    // so we set the markExpr's ordinal to smallest of those deleted colExprs
                    return new ExprRef(clone, markerOrdinal);
                }
            }

            // the LogicOrExpr with Marker should be consider
            //input  (mark@1 or mark@2) or mark@3
            if (clone is LogicAndOrExpr)
            {
                clone.VisitEach(x =>
                {
                    if (x is ExprRef xE && xE.child_() is MarkerExpr xEM) // find #marker
                    {
                        int t_ordinal = source.FindIndex(xS =>
                              //find the #marker by subqueryid_ in children's output
                              xS is ExprRef xSE && xSE.child_() is MarkerExpr xSEM && xEM.subqueryid_ == xSEM.subqueryid_);
                        Debug.Assert(t_ordinal > 0); // there must be a marker produced by child
                        clone = clone.SearchAndReplace<ExprRef>(xE, new ExprRef(xEM, t_ordinal));
                    }
                });
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
             * ignoring the assertion means legitimate problems will also go uncaught here and
             * may cause severe problem elsewhere.
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
                // fix colexpr's ordinal - leave the outer ref as it is already decided in ColExpr.Bind()
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
            // let's first fix Aggregation as a whole expression - there could be some combinations
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

            List<Expr> output = new List<Expr>(toclone);
            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source, output, idonly)));
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
        // 1. you shall first compute the reqOutput by accounting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        // this parent class implements a default one for using first child output without any change. You can
        // also piggy back in this method to "finalize" your logic node.
        //
        public virtual List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.RemoveAll(x => x is SubqueryExpr);
            children_[0].ResolveColumnOrdinal(reqFromChild);
            var childout = new List<Expr>(child_().output_);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);

            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(childout);
            ordRes.FixOutput();

            RefreshOutputRegisteration();
            return ordinals;
        }

        public ulong Card()
        {
            if (!card_.HasValue)
                card_ = EstimateCard();
            return card_.Value;
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

        // dervied information from join condition
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

        public bool IsSubSet(List<TableRef> small, List<TableRef> big) => small.All(t => big.Any(b => b == t));

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
            // separate the output to related child respectively
            Debug.Assert((this.lchild_() != null) && (this.rchild_() != null));
            if (this is LogicMarkJoin LMJ)
            {
                var lchild = this.lchild_();
                var rchild = this.rchild_();
                List<Expr> reqFromChildRemove = new List<Expr>();
                // consider (marker@1 or marker@2)
                // a markjoin@1 b markjoin@2 c
                //  
                //  (marker@1,marker@2)   |><|@1
                //                       /    \
                //  (marker@2)        |><|@2    a
                //                     /    \
                //                   b       c
                //
                // if this is the markjoin@1, the marker@1 is produce by this MarkJoin
                // we should keep marker@1, meanwhile, delete it in reqFromChild
                // and should figure out which side has produced marker@2
                // in fact, we always use LeftMarkJoin, so the marker@2 may always drop into left.
                // so the rchild check may be unnecessary
                lchild.VisitEach(x =>
                {
                    if (x is LogicMarkJoin)
                    {
                        foreach (var v in reqFromChild)
                        {
                            if (v is MarkerExpr vM)
                            {
                                var xTableRef = x.GetTableRef();
                                if (IsSubSet(vM.tableRefs_, xTableRef) && !(LMJ.subquery_id_ == vM.subqueryid_))
                                {
                                    lreq.Add(v);
                                    reqFromChildRemove.Add(v);
                                }
                            }
                        }
                    }
                }
                );
                rchild.VisitEach(x =>
                {
                    if (x is LogicMarkJoin)
                    {
                        foreach (var v in reqFromChild)
                        {
                            if (v is MarkerExpr vM)
                            {
                                var xTableRef = x.GetTableRef();
                                if (IsSubSet(v.tableRefs_, xTableRef) && !(LMJ.subquery_id_ == vM.subqueryid_))
                                {
                                    rreq.Add(v);
                                    reqFromChildRemove.Add(v);
                                }
                            }
                        }
                    }
                }
                );
                reqFromChildRemove.ForEach(x =>
                {
                    reqFromChild.Remove(x);
                });
            }
            Expr thisReq = null; // mark-join will produce a Marker Expr
            foreach (var v in reqFromChild)
            {
                if (!(v is MarkerExpr vM) || !(this is LogicMarkJoin lmj))
                {
                    var tables = v.CollectAllTableRef();
                    if (ltables.ContainsList(tables))
                        lreq.Add(v);
                    else if (rtables.ContainsList(tables))
                        rreq.Add(v);
                    else
                    {
                        // the whole list can't push to the children (eg. a.a1 + b.b1)
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
                    if (lmj.subquery_id_ == vM.subqueryid_)
                        thisReq = v;// the left mark-join is requested by this
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

            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(childrenout);
            ordRes.FixFilter(filter_);
            ordRes.FixOutput();
            ordRes.PrintOrdinals(output_);
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
            static void addColumnAndAggFuncs(Expr expr, HashSet<Expr> list)
            {
                if (!(expr is AggFunc))
                {
                    if ((expr is ColExpr ec && !ec.isParameter_) || expr is MarkerExpr)
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

            // filter may carry ordinary columns and aggregations - aggregation won't show up
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

            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(childout);
            ordRes.FixFilter(filter_);
            ordRes.FixOutput();

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
                    // remove the duplicates immediately to avoid wrong ordinal in ExprRef
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
                // remove the duplicates immediately to avoid wrong ordinal in ExprRef
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
            // Aggregates in group by handling. If there are aggregates in
            // group by, collect their arguments (directly contained aggregate
            // functions and those inside AggrRef and other expressions and
            // make this list as required from child. Save the original
            // group by and null it out.
            // After getting the output from the child, restore the original
            // group by and resolve everything as usual.
            List<Expr> newGrpBy = null;
            List<Expr> savedGrpBy = null;

            if (groupby_ != null)
            {
                bool hasAgg = false;
                groupby_.ForEach(x =>
                {
                    if (x.HasAggFunc())
                        hasAgg = true;
                });

                if (hasAgg)
                {
                    newGrpBy = new List<Expr>();
                    savedGrpBy = groupby_.CloneList();
                    for (int i = 0; i < groupby_.Count; ++i)
                    {
                        Expr x = groupby_[i];
                        if (x is AggFunc agf)
                            newGrpBy.Add(x);
                        else if (x is AggrRef agr)
                            newGrpBy.Add(agr.child_());
                        else
                            newGrpBy.Add(x);
                    }
                }
            }

            if (newGrpBy != null)
                groupby_ = null;

            // request from child including reqOutput and filter. Don't use whole expression
            // matching push down like k+k => (k+k)[0] instead, we need k[0]+k[0] because 
            // aggregation only projection values from hash table(key, value).
            //
            List<Expr> reqFromChild = new List<Expr>();
            if (newGrpBy != null)
                reqFromChild.AddRange(newGrpBy);
            else
                reqFromChild.AddRange(removeAggFuncAndKeyExprsFromOutput(reqList, groupby_));

            // Issue exposed by removing remove_from.
            // Remember the last position of output required by the parent, it is not an error
            // if the offending occurs after this position.
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

            if (savedGrpBy != null)
                groupby_ = savedGrpBy;
            if (groupby_ != null)
                groupby_ = CloneFixColumnOrdinal(groupby_, childout, true);
            if (having_ != null)
                having_ = CloneFixColumnOrdinal(having_, childout);

            output_ = CloneFixColumnOrdinal(processedOutput, childout, removeRedundant);

            OrdinalResolver ordRes = new OrdinalResolver(reqList);
            ordRes.SetFixedChildren(childout);
            ordRes.FixGroupby(groupby_);
            ordRes.FixHaving(having_);
            ordRes.FixOutput();

            var newoutput = GenerateAggrFns();

            // Say invalid expression means contains colexpr (replaced with ref), then the output shall
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
            // is actually a3+a4, if we decompose it, then Aggregation will see a3 and a4 thus require them
            // in the GROUP BY clause.
            //
            reqFromChild.AddRange(orders_);

            reqFromChild.RemoveAll(x => x is SubqueryExpr);

            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = new List<Expr>(child_().output_);

            orders_ = CloneFixColumnOrdinal(orders_, childout, false);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);

            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(childout);
            ordRes.FixOrderby(orders_);
            ordRes.FixOutput();
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

            OrdinalResolver ordRes = new OrdinalResolver(query.selection_);
            // TODO: Need to add outerrefs to fixed output
            ordRes.SetFixedChildren(childout);
            ordRes.FixOutput();

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

            // Verify it can be an literal, or only uses my tableref
            validateReqOutput(reqOutput);

            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, columns);
            output_ = CloneFixColumnOrdinal(reqOutput, columns, false);

            // Finally, consider outerrefs to this table: if they are not there, add them
            output_ = tabref_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
#if false
            // base table does not need this, at least for now.
            // When all expr are replaced by a new version of ExpRef (ValueId)
            // then we need to resolve the ordinals of those valueids.
            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(columns);
            ordRes.FixFilter(filter_);
            ordRes.FixOutput();
#endif

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

    // LogicAppend needs extra information to allow remove_from optimization to work
    // correctly on UNION queries inside a FromQuery.
    // Resolve all selects in UNION. When remove_from is true, we don't
    // generate a setop plan and therefore all except the first select remain
    // without ordinals resolved. This leads to their output being null at
    // execution time and the result is null pointer exception.
    // The way to handle the situation is to let each LogicAppend node to know
    // the SetOp tree it is part of. This is done by passing the SetOp tree
    // whose left and right selects correspond to the left and right nodes of this
    // logicAppend node in the constructor.
    // Visit each branch of the SetOp tree and locate the select which corresponds to
    // left and right node and record those select lists as leftExprs_ and rightExprs_.
    //
    // Override ResolveColumnOrdinal in LogicAppend and using the saved
    // selections, resolve the minimum of selections or reqOutput from each child.
    //
    // Set the top level LogicAppend's outputs to those of the first child.
    //
    public class LogicAppend : LogicNode
    {
        public List<Expr> leftExprs_;     // left plan's selection
        public List<Expr> rightExprs_;    // right plan's selection
        public override string ToString() => $"Append({lchild_()},{rchild_()})";

        public LogicAppend(LogicNode l, LogicNode r, SetOpTree setops = null)
        {
            children_.Add(l);
            children_.Add(r);

            // Using the setop tree, find the left and right plan's
            // selections and save them to be used in ordinal resolution of
            // all selects when this is not a top level UNION and remove_from
            // is true.
            if (setops != null)
            {
                setops.VisitEachStatement(x =>
                {
                    if (x.logicPlan_ == lchild_())
                        leftExprs_ = x.selection_;
                    else if (x.logicPlan_ == rchild_())
                        rightExprs_ = x.selection_;
                });
            }
        }

        // Resolve one child's ordinals
        internal void ResolveChild(in LogicNode child, in List<Expr> childExprs, in List<Expr> reqOutput, bool removeRedundant)
        {
            int minReq = Math.Min(reqOutput.Count, childExprs.Count);
            List<Expr> childReq = new List<Expr>();
            for (int i = 0; i < minReq; ++i)
                childReq.Add(rightExprs_[i]);
            child.ResolveColumnOrdinal(childReq, removeRedundant);

            OrdinalResolver ordRes = new OrdinalResolver(childReq);
            ordRes.SetFixedChildren(child.output_);
            ordRes.FixOutput();
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = children_[0].ResolveColumnOrdinal(reqOutput, removeRedundant);

            if (rightExprs_ != null && children_[1].output_.Count == 0)
                ResolveChild(children_[1], rightExprs_, reqOutput, removeRedundant);

            if (leftExprs_ != null && children_[0].output_.Count == 0)
                ResolveChild(children_[0], leftExprs_, reqOutput, removeRedundant);

            // Only the top level LogicAppend will have the output_ still unset,
            // set it after all children has their output set.
            if (output_.Count == 0)
            {
                List<Expr> childout = children_[0].output_;
                output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            }

            return ordinals;
        }
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

            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.RemoveAll(x => x is SubqueryExpr);
            children_[0].ResolveColumnOrdinal(reqFromChild);
            var childout = new List<Expr>(child_().output_);
            // limit is the top node and don't remove redundant
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);

            OrdinalResolver ordRes = new OrdinalResolver(reqFromChild); // Should it be reqOutput?
            ordRes.SetFixedChildren(childout);
            ordRes.FixOutput();

            RefreshOutputRegisteration();
            return ordinals;
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

            OrdinalResolver ordRes = new OrdinalResolver(reqOutput);
            ordRes.SetFixedChildren(childout);
            ordRes.FixOutput();

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

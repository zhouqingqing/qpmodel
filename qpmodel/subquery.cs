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

using qpmodel.expr;
using qpmodel.physic;

using Value = System.Object;

// To remove FromQuery, we essentially remove all references to the related 
// FromQueryRef, which shall include selection (ColExpr, Aggs, Orders etc),
// filters and any constructs may references a TableRef (say subquery outerref).
//
// If we remove FromQuery before binding, we can do it on SQL text level but 
// it is considered very early and error proning. We can do it after binding
// then we need to find out all references to the FromQuery and replace them
// with underlying non-from TableRefs.
//
// FromQuery in subquery is even more complicated, because far away there
// could be some references of its name and we shall fix them. When we remove
// filter, we redo columnordinal fixing but this does not work for FromQuery
// because naming reference. PostgreSQL actually puts a Result node with a 
// name, so it is similar to FromQuery.
//
using LogicSignature = System.Int64;
using qpmodel.optimizer;
using qpmodel.logic;

namespace qpmodel.logic
{
    public class MarkerExpr : Expr
    {
        public int subqueryid_;
        public MarkerExpr(List<TableRef> tr, int subqueryid)
        {
            subqueryid_ = subqueryid;
            tableRefs_ = tr;
            Debug.Assert(Equals(Clone()));
            dummyBind();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            // its values is fed by mark join so non input related
            return null;
        }

        public override string ToString() => $"#marker@{subqueryid_}";
    }

    public partial class SelectStmt : SQLStatement
    {
        // expands [NOT] EXISTS filter to mark join
        //
        //  LogicNode_A
        //     Filter: @1 AND|OR <others1>
        //     <ExistSubqueryExpr> 1
        //          -> LogicNode_B
        //             Filter: b.b1[0]=?a.a1[0] AND|OR <others2>
        // =>
        //    Filter
        //      Filter: #marker AND|OR <others1>
        //      MarkJoin
        //         Filter:  (b.b1[0]=a.a1[0]) AND|OR <others2> as #marker 
        //         LogicNode_A
        //         LogicNode_B
        //
        // further convert DJoin to semi-join here is by decorrelate process
        //

        // check (@1 or (@2 or @3))) if @3 unnested here.
        // (@1 @2)
        bool hasAnyExtraSubqueryExprInOR(Expr x, Expr exsitsExpr)
        {
            bool exprIsNotEqualToCurrentExistsExpr(Expr x, Expr curExistsExpr)
            {
                return x is SubqueryExpr && (!x.Equals(curExistsExpr));
            }
            if (x is LogicOrExpr xOR)
                return hasAnyExtraSubqueryExprInOR(xOR.rchild_(), exsitsExpr) || hasAnyExtraSubqueryExprInOR(xOR.lchild_(), exsitsExpr);
            else
                return x.VisitEachExists(y => exprIsNotEqualToCurrentExistsExpr(y, exsitsExpr));
        }

        // If there is OR in the predicate, can't turn into a filter
        //
        LogicNode existsToMarkJoin(LogicNode nodeA, ExistSubqueryExpr existExpr, ref bool canReplace)
        {
            bool exprIsNotORExprAndEqualsToExistExpr(Expr x, Expr existExpr)
            {
                return (!(x is LogicOrExpr)) && x.Equals(existExpr);
            }

            // nodeB contains the join filter
            var nodeB = existExpr.query_.logicPlan_;
            var nodeBFilter = nodeB.filter_;
            nodeB.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var markerFilter = new ExprRef(new MarkerExpr(nodeBFilter.tableRefs_, existExpr.subqueryid_), 0);
            var nodeAFilter = nodeA.filter_;
            Debug.Assert(!(nodeA is null));

            // a1 > @1 and a2 > @2 and a3 > 2, existExpr = @1
            //   keeplist: a1 > @1 and a3 > 2
            //   andlist after removal: a2 > @2
            //   nodeAFilter = a1 > @1 and a3 > 2
            //   consider a1>0 and (@1 or @2)
            //   a1>0 and (@1 or (@2 and @3)) 

            //(@1 or marker@2) and marker@3 existExpr = @1
            //   keep list (@1 or marker@2)
            //   andlist after remove @3
            //   nodeAFilter = (@1 or marker@2)
            var andlist = nodeAFilter.FilterToAndList();
            var keeplist = andlist.Where(x => x.VisitEachExists(e => e.Equals(existExpr))).ToList();
            andlist.RemoveAll(x => exprIsNotORExprAndEqualsToExistExpr(x, existExpr) ||
                            ((x is LogicOrExpr) && !hasAnyExtraSubqueryExprInOR(x, existExpr)));

            // if there is any (#marker@1 or @2), the root should be replace, 
            // i.e. the (#marker@1 or @2)  keeps at the top for farther unnesting
            canReplace = andlist.Find(x => (x is LogicOrExpr) && hasAnyExtraSubqueryExprInOR(x, existExpr)) != null;

            if (andlist.Count == 0 || canReplace)
                // nodeA is root, a ref parameter. (why it is a ref parameter without "ref" or "out" )
                nodeA.NullifyFilter();
            else
            {
                nodeA.filter_ = andlist.AndListToExpr();
                nodeAFilter = keeplist.Count > 0 ? keeplist.AndListToExpr() : markerFilter;
            }

            // make a mark join
            LogicMarkJoin markjoin;
            if (existExpr.hasNot_)
                markjoin = new LogicMarkAntiSemiJoin(nodeA, nodeB, existExpr.subqueryid_);
            else
                markjoin = new LogicMarkSemiJoin(nodeA, nodeB, existExpr.subqueryid_);

            // make a filter on top of the mark join collecting all filters
            Expr topfilter;
            topfilter = nodeAFilter.SearchAndReplace(existExpr, markerFilter);
            nodeBFilter.DeParameter(nodeA.InclusiveTableRefs());

            // find all expr contains Parameter col and move it to the toper
            var TableRefs = nodeA.InclusiveTableRefs();
            topfilter = topfilter.AddAndFilter(nodeBFilter);
            LogicFilter Filter = new LogicFilter(markjoin, topfilter);
            var notDeparameterExpr = findAndfetchParameterExpr(ref nodeA);
            if (notDeparameterExpr.Count > 0)
            {
                topfilter = notDeparameterExpr.AndListToExpr();
                Filter = new LogicFilter(Filter, topfilter);
            }
            return Filter;
        }

        List<Expr> findAndfetchParameterExpr(ref LogicNode nodeA)
        {
            bool isUnresolvedColExpr(Expr e)
            {
                return e is ColExpr eCE && eCE.isParameter_;
            }
            List<Expr> notDeparameterExpr = new List<Expr>();

            nodeA.VisitEach(x =>
            {
                if (x is LogicFilter)
                {
                    var andList = x.filter_.FilterToAndList();
                    foreach (var e in andList)
                    {
                        e.VisitEach(c =>
                        {
                            if (isUnresolvedColExpr(c))
                                notDeparameterExpr.Add(e);
                        });
                    }
                    var removeList = notDeparameterExpr.Where(x => andList.Contains(x));
                    foreach (var r in removeList)
                    {
                        andList.Remove(r);
                    }
                    if (andList.Count == 0)
                        x.NullifyFilter();
                    else
                        x.filter_ = andList.AndListToExpr();
                }
            });

            if (notDeparameterExpr.Count > 0)
            {
                notDeparameterExpr.Distinct();
            }

            return notDeparameterExpr;
        }

        // expands scalar subquery filter to mark join
        //
        //  LogicNode_A
        //     Filter: a.a2 = @1 AND <others1>
        //     <ExistSubqueryExpr> 1
        //          -> LogicNode_B
        //             Output: singleValueExpr
        //             Filter: b.b1[0]=?a.a1[0] AND <others2>
        // =>
        // LogicFilter-Movable
        //     Filter:  (b.b1[0]=a.a1[0]) AND <others1> <others2>
        //  LogicFilter-NonMovable
        //      Filter: a.a2 = singleValueExpr
        //      SingleJoin
        //          Output: singleValueExpr
        //          LogicNode_A
        //          LogicNode_B
        //
        LogicNode scalarToSingleJoin(LogicNode planWithSubExpr, ScalarSubqueryExpr scalarExpr)
        {
            var newplan = planWithSubExpr;

            // nodeB contains the join filter
            var nodeSubquery = scalarExpr.query_.logicPlan_;

            // make a single join
            //    nodeA 
            //      <Subquery> 
            //            nodeSubquery
            // =>
            //    SingleJoin
            //        nodeA
            //        nodeSubquery
            //
            var singleJoinNode = new LogicSingleJoin(planWithSubExpr,
                                                nodeSubquery);
            switch (nodeSubquery)
            {
                case LogicAgg la:
                    // Filter: a.a1[0] =@1
                    //   < ScalarSubqueryExpr > 1
                    //      ->PhysicHashAgg(rows = 2)
                    //            -> PhysicFilter(rows = 2)
                    //               Filter: b.b2[1]=?a.a2[1] ...
                    newplan = djoinOnRightAggregation(singleJoinNode, scalarExpr);
                    break;
                case LogicFilter lf:
                    newplan = djoinOnRightFilter(singleJoinNode, scalarExpr);
                    break;
                default:
                    break;
            }

            // see if we can convert to an ordinary LOJ
            newplan.VisitEach((parent, index, node) =>
            {
                if (node is LogicSingleJoin sn)
                {
                    Debug.Assert(parent != null);
                    parent.children_[index] = singleJoin2OuterJoin(sn);
                }
            });
            return newplan;
        }

        Expr extractCurINExprFromNodeAFilter(LogicNode nodeA, InSubqueryExpr curInExpr, ExprRef markerFilter)
        {
            var nodeAFilter = nodeA.filter_;
            if (nodeAFilter != null)
            {
                // a1 > @1 and a2 > @2 and a3 > 2, scalarExpr = @1
                //   keeplist: a1 > @1 and a3 > 2
                //   andlist after removal: a2 > @2
                //   nodeAFilter = a1 > @1 and a3 > 2
                //
                var andlist = nodeAFilter.FilterToAndList();
                var keeplist = andlist.Where(x => x.VisitEachExists(e => e.Equals(curInExpr))).ToList();
                andlist.RemoveAll(x => x.VisitEachExists(e => e.Equals(curInExpr)));
                if (andlist.Count == 0)
                    nodeA.NullifyFilter();
                else
                {
                    nodeA.filter_ = andlist.AndListToExpr();
                    if (keeplist.Count > 0)
                        nodeAFilter = keeplist.AndListToExpr();
                    else
                        nodeAFilter = markerFilter;
                }
            }
            return nodeAFilter;
        }

        LogicNode inToMarkJoin(LogicNode planWithSubExpr, InSubqueryExpr inExpr)
        {
            LogicNode nodeA = planWithSubExpr;

            // nodeB contains the join filter
            var nodeB = inExpr.query_.logicPlan_;
            var nodeBFilter = nodeB.filter_;
            nodeB.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var markerFilter = new ExprRef(new MarkerExpr(nodeBFilter.tableRefs_, inExpr.subqueryid_), 0);
            var nodeAFilter = extractCurINExprFromNodeAFilter(nodeA, inExpr, markerFilter);

            // consider SQL ...a1 in select b1 from... 
            // a1 is outerExpr and b1 is selectExpr
            Expr outerExpr = inExpr.child_();
            Debug.Assert(inExpr.query_.selection_.Count == 1);
            Expr selectExpr = inExpr.query_.selection_[0];
            BinExpr inToEqual = BinExpr.MakeBooleanExpr(outerExpr, selectExpr, "=", true);

            // make a mark join
            LogicMarkJoin markjoin;
            if (inExpr.hasNot_)
                markjoin = new LogicMarkAntiSemiJoin(nodeA, nodeB, inExpr.subqueryid_);
            else
                markjoin = new LogicMarkSemiJoin(nodeA, nodeB, inExpr.subqueryid_);

            // make a filter on top of the mark join collecting all filters
            Expr topfilter = nodeAFilter.SearchAndReplace(inExpr, markerFilter);
            nodeBFilter.DeParameter(nodeA.InclusiveTableRefs());
            topfilter = topfilter.AddAndFilter(nodeBFilter);
            // TODO mutiple nested insubquery subquery 
            // seperate the overlapping code with existsToSubquery to a new method
            // when the PR in #support nestted exist subquery pass
            LogicFilter Filter = new LogicFilter(markjoin, topfilter);
            Filter = new LogicFilter(Filter, inToEqual);
            return Filter;
        }

        // A Xs B => A LOJ B if max1row is assured
        LogicJoin singleJoin2OuterJoin(LogicSingleJoin singJoinNode)
        {
            LogicJoin newjoin = singJoinNode;
            if (!singJoinNode.max1rowCheck_)
            {
                newjoin = new LogicJoin(singJoinNode.lchild_(), singJoinNode.rchild_(), singJoinNode.filter_)
                {
                    type_ = JoinType.Left
                };
            }

            return newjoin;
        }

        // D Xs (Filter(T)) => Filter(D Xs T) 
        LogicNode djoinOnRightFilter(LogicSingleJoin singleJoinNode, ScalarSubqueryExpr scalarExpr)
        {
            var nodeLeft = singleJoinNode.lchild_();
            var nodeSubquery = singleJoinNode.rchild_();
            var nodeSubqueryFilter = nodeSubquery.filter_;

            Debug.Assert(scalarExpr.query_.selection_.Count == 1);
            var singleValueExpr = scalarExpr.query_.selection_[0];
            nodeSubquery.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var trueCondition = ConstExpr.MakeConstBool(true);
            var nodeLeftFilter = nodeLeft.filter_;
            if (nodeLeftFilter != null)
            {
                // a1 > @1 and a2 > @2 and a3 > 2, scalarExpr = @1
                //   keeplist: a1 > @1 and a3 > 2
                //   andlist after removal: a2 > @2
                //   nodeAFilter = a1 > @1 and a3 > 2
                //
                var andlist = nodeLeftFilter.FilterToAndList();
                var keeplist = andlist.Where(x => x.VisitEachExists(e => e.Equals(scalarExpr))).ToList();
                andlist.RemoveAll(x => x.VisitEachExists(e => e.Equals(scalarExpr)));
                if (andlist.Count == 0)
                    nodeLeft.NullifyFilter();
                else
                {
                    nodeLeft.filter_ = andlist.AndListToExpr();
                    if (keeplist.Count > 0)
                        nodeLeftFilter = keeplist.AndListToExpr();
                    else
                        nodeLeftFilter = trueCondition;
                }
            }

            // make a non-movable filter on top of the single join to replace a1 > @1
            LogicFilter nonMovableFilter = null;
            if (nodeLeftFilter != null)
            {
                // a1 > @1 => a1 > c1
                var decExpr = nodeLeftFilter.SearchAndReplace(scalarExpr, singleValueExpr);
                nonMovableFilter = new LogicFilter(singleJoinNode, decExpr)
                {
                    movable_ = false
                };
            }

            // b1 = ?outsideRef => b1 = outsideRef
            nodeSubqueryFilter.DeParameter(nodeLeft.InclusiveTableRefs());

            // join filter within sbuquery
            Expr pullupFilter = nodeSubqueryFilter;
            if (nodeLeft.filter_ != null && !nodeLeft.filter_.HasSubQuery())
            {
                pullupFilter = pullupFilter.AddAndFilter(nodeLeft.filter_);
                nodeLeft.NullifyFilter();
            }
            LogicFilter Filter = new LogicFilter(nonMovableFilter != null ?
                                    (LogicNode)nonMovableFilter : singleJoinNode, pullupFilter);
            return Filter;
        }

        // D Xs (Agg (T) group by A) => Agg(D Xs T) group by {A, output(D))
        //  a.i = (select max(b.i) from b where a.j=b.j group by b.k)
        //
        LogicNode djoinOnRightAggregation(LogicSingleJoin singleJoinNode, ScalarSubqueryExpr scalarExpr)
        {
            var nodeLeft = singleJoinNode.lchild_();
            var aggNode = singleJoinNode.rchild_() as LogicAgg;

            // ?a.j = b.j => b.j
            var listexpr = aggNode.RetrieveCorrelatedFilters();
            var extraGroubyVars = new List<Expr>();
            foreach (var v in listexpr)
            {
                var bv = v as BinExpr;

                // if we can't handle, bail out
                if (bv is null)
                    return nodeLeft;
                var lbv = bv.lchild_() as ColExpr;
                var rbv = bv.rchild_() as ColExpr;
                if (lbv is null || rbv is null)
                    return nodeLeft;

                // now we can handle them, take the non-parameter column
                Debug.Assert(!(lbv.isParameter_ && rbv.isParameter_));
                if (!lbv.isParameter_)
                    extraGroubyVars.Add(lbv);
                if (!rbv.isParameter_)
                    extraGroubyVars.Add(rbv);
            }

            // group by b.k => group by b.k, b.j
            Debug.Assert(singleJoinNode.max1rowCheck_);
            if (aggNode.groupby_ is null)
            {
                aggNode.groupby_ = extraGroubyVars;
                singleJoinNode.max1rowCheck_ = false;
            }
            else
                aggNode.groupby_.AddRange(extraGroubyVars);

            // put a filter on the right side and pull up the aggregation
            //
            // agg
            //   filter
            //      ...
            // =>
            // filter
            //    agg
            //      ...
            var filterNode = aggNode.child_() as LogicFilter;
            aggNode.children_[0] = filterNode.child_();
            filterNode.children_[0] = aggNode;
            singleJoinNode.children_[1] = filterNode;

            // now we have convert it to a right filter plan
            var newplan = djoinOnRightFilter(singleJoinNode, scalarExpr);
            return newplan;
        }

        // an outer join can be converted to inner join if join condition is null-rejected.
        // Null-rejected condition meaning it evalues to not true (i.e., false or null) for 
        // any null completed row generated by outer join.
        //
        // a join condition is null if 
        //  https://dev.mysql.com/doc/refman/8.0/en/outer-join-simplification.html
        //  - It is of the form A IS NOT NULL, where A is an attribute of any of the inner tables
        //  - It is a predicate containing a reference to an inner table that evaluates to UNKNOWN 
        //    when one of its arguments is NULL
        //  - It is a conjunction containing a null-rejected condition as a conjunct
        //  - It is a disjunction of null-rejected conditions
        //
        LogicJoin trySimplifyOuterJoin(LogicJoin join, Expr extraFilter)
        {
            bool nullRejectingSingleCondition(Expr condition)
            {
                // FIXME: for now, assuming any predicate is null-rejecting unless it is IS NULL
                Debug.Assert(condition.IsBoolean());
                var bcond = condition as BinExpr;
                if (bcond?.op_ == "is")
                    return false;
                return false;
            }

            // if no extra filters, can't convert
            if (extraFilter is null)
                return join;
            if (join.type_ != JoinType.Left)
                return join;

            // It is a conjunction containing a null-rejected condition as a conjunct
            var andlist = extraFilter.FilterToAndList();
            bool nullreject = andlist.Where(x => nullRejectingSingleCondition(x)).Count() > 0;
            if (nullreject)
                goto convert_inner;

            // It is a conjunction containing a null-rejected condition as a conjunct
            return join;

        convert_inner:
            join.type_ = JoinType.Inner;
            return join;
        }

        // exists|quantified subquery => mark join
        // scalar subquery => single join or LOJ if max1row output is assured
        // 
        LogicNode oneSubqueryToJoin(LogicNode planWithSubExpr, SubqueryExpr subexpr, ref bool canRepalce)
        {
            LogicNode oldplan = planWithSubExpr;
            LogicNode newplan = null;

            if (!subexpr.IsCorrelated())
                return planWithSubExpr;

            switch (subexpr)
            {
                case ExistSubqueryExpr se:
                    newplan = existsToMarkJoin(planWithSubExpr, se, ref canRepalce);
                    break;
                case ScalarSubqueryExpr ss:
                    newplan = scalarToSingleJoin(planWithSubExpr, ss);
                    break;
                case InSubqueryExpr si:
                    newplan = inToMarkJoin(planWithSubExpr, si);
                    break;
                default:
                    break;
            }
            if (oldplan != newplan)
                decorrelatedSubs_.Add(new NamedQuery(subexpr.query_, null, NamedQuery.QueryType.UNSURE));
            return newplan;
        }

        void findCteConsumerInParent(LogicNode parent, ref Stack<int> scie)
        {
            var scieTemp = scie;
            parent.VisitEach(logicNode =>
            {
                if (logicNode is LogicCteConsumer lcc)
                {
                    var cteId = lcc.cteId_;
                    var cteInfoEntry = cteInfo_.GetCteInfoEntryByCteId(cteId);
                    if (scieTemp.Contains(cteId))
                    {
                        cteInfoEntry.refTimes++;
                    }
                    else
                    {
                        cteInfoEntry.refTimes = 1;
                        cteInfoEntry.MarkCTEUsed();
                        scieTemp.Push(cteId);
                    };
                    lcc.refId_ = cteInfoEntry.refTimes;
                    cteInfo_.SetCetInfoEntryByCteId(cteId, cteInfoEntry);
                }
            });
            scie = scieTemp;
        }

        LogicNode cteToAnchor(LogicNode root)
        {
            // find the cte whitch is not used
            // consider "with cte0 as (select * from a),cte1 as (select * from cte0) select * from a
            // although cte0 is used in cte1 , but cte1 is never used, so we have to delete cte1 too 
            //
            Stack<int> scie = new Stack<int>(); // save cteId

            findCteConsumerInParent(root, ref scie);
            foreach (var subq in subQueries_)
            {
                findCteConsumerInParent(subq.query_.logicPlan_, ref scie);
            }
            while (scie.Count() > 0)
            {
                var cteId = scie.Pop();
                var ctePlan = cteInfo_.GetCteInfoEntryByCteId(cteId).plan_;
                findCteConsumerInParent(ctePlan, ref scie);
            }
            // remove not used cte
            var cteIsUsed = ctes_;
            cteIsUsed.RemoveAll(x => !cteInfo_.GetCteInfoEntryByCteId(x.cteId_).IsUsed());

            for (int i = 0; i < cteIsUsed.Count; i++)
            {
                var lca = new LogicCteAnchor(cteInfo_.GetCteInfoEntryByCteId(cteIsUsed[i].cteId_));
                lca.children_.Add(root);
                root = lca;
            }
            return root;
        }
    }

    // mark join is like semi-join form with an extra boolean column ("mark") indicating join 
    // predicate results (true|false|null)
    //
    public class LogicMarkJoin : LogicJoin
    {
        public int subquery_id_;
        public override string ToString() => $"{lchild_()} markX {rchild_()}";
        public LogicMarkJoin(LogicNode l, LogicNode r, int subquery_id) : base(l, r) { type_ = JoinType.Left; subquery_id_ = subquery_id; }
        public LogicMarkJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { type_ = JoinType.Left; }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            var list = base.ResolveColumnOrdinal(reqOutput, removeRedundant);
            return list;
        }
    }
    public class LogicMarkSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{lchild_()} markSemiX {rchild_()}";
        public LogicMarkSemiJoin(LogicNode l, LogicNode r, int subquery_id = 0) : base(l, r, subquery_id) { subquery_id_ = subquery_id; }
        public LogicMarkSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }
    public class LogicMarkAntiSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{lchild_()} markAntisemiX {rchild_()}";
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r, int subquery_id = 0) : base(l, r, subquery_id) { }
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }

    public class LogicSingleJoin : LogicJoin
    {
        // if we can prove at most one row return, it is essentially an ordinary LOJ
        internal bool max1rowCheck_ = true;
        public override string ToString() => $"{lchild_()} singleX {rchild_()}";
        public LogicSingleJoin(LogicNode l, LogicNode r) : base(l, r) { type_ = JoinType.Left; }
        public LogicSingleJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { type_ = JoinType.Left; }
    }

    public class PhysicSingleJoin : PhysicNode
    {
        public PhysicSingleJoin(LogicSingleJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);

            // we can't assert logic filter is not null because in some cases we can't adjust join order
            // then we have to bear with it.
            // Example:
            //     select a1  from a where a.a1 = (
            //            select c1 from c where c2 = a2 and c1 = (select b1 from b where b3=a3));
            //
        }
        public override string ToString() => $"PSingleJOIN({lchild_()},{rchild_()}: {Cost()})";

        // always the first column
        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicSingleJoin;
            var filter = logic.filter_;
            Debug.Assert(logic is LogicSingleJoin);
            bool outerJoin = logic.type_ == JoinType.Left;

            // if max1row is gauranteed, it is converted to regular LOJ
            Debug.Assert(logic.max1rowCheck_);

            lchild_().Exec(l =>
            {
                bool foundOneMatch = false;
                rchild_().Exec(r =>
                {
                    Row n = new Row(l, r);
                    if (filter is null || filter.Exec(context, n) is true)
                    {
                        bool foundDups = foundOneMatch && filter != null;

                        if (foundDups)
                            throw new SemanticExecutionException("subquery must return only one row");
                        foundOneMatch = true;

                        // there is at least one match, mark true
                        n = ExecProject(n);
                        callback(n);
                    }
                });

                // if there is no match, output it if outer join
                if (!foundOneMatch && outerJoin)
                {
                    var nNulls = rchild_().logic_.output_.Count;
                    Row n = new Row(l, new Row(nNulls));
                    n = ExecProject(n);
                    callback(n);
                }
            });
        }

        protected override double EstimateCost()
        {
            double cost = lchild_().Card() * rchild_().Card();
            return cost;
        }
    }

    public class LogicSequence : LogicNode
    {
        public override string ToString() => $"Sequence({children_.Count - 1},{OutputChild()})";

        // last child is the output node
        public LogicNode OutputChild() => children_[children_.Count - 1];

        public LogicCteAnchor cteAnchor_;

        public LogicSequence(LogicNode lchild, LogicNode rchild, LogicCteAnchor cteAnchor)
        {
            children_.Add(lchild); children_.Add(rchild);
            Debug.Assert(children_.Count == 2);
            cteAnchor_ = cteAnchor;
        }
        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            // Sequence not only handle CTEProducer
            for (int i = 0; i < children_.Count - 1; i++)
            {
                var child = children_[i] as LogicCteProducer;
                //             Sequence
                //            /
                //        Producer
                //         /
                //     From
                var from = child.child_() as LogicFromQuery;
                child.ResolveColumnOrdinal(from.queryRef_.query_.selection_, false);
            }
            OutputChild().ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_ = OutputChild().output_;
            RefreshOutputRegisteration();
            return ordinals;
        }

        public override LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                logicSign_ = cteAnchor_.MemoLogicSign(); //anchor + cteId
            return logicSign_;
        }
    }


    // a entry of CTE info
    public class CteInfoEntry
    {
        // if this Cte is Used
        // when a cte1 is used in cte2, but cte2 is never used 
        // we should mark both cte1 and cte2 isUsed = False.
        //
        bool isUsed_;

        bool isInlined_;

        // a cteProducer refer times
        //
        public int refTimes = 0;

        public CteExpr cte_;

        public LogicNode plan_;

        public CteInfoEntry(CteExpr exprCteProducer, LogicNode plan)
        {
            Debug.Assert(exprCteProducer != null);
            cte_ = exprCteProducer;
            var alias = cte_.cteName_;
            plan_ = plan;
            isUsed_ = false;
            isInlined_ = false;
        }

        public CteInfoEntry() { }

        // something to save cte producer
        //
        public void MarkCTEUsed() => isUsed_ = true;

        public bool IsUsed() => isUsed_;

        public void MarkInlined() => isInlined_ = true;

        public bool IsInlined() => isInlined_;
    }

    public class CteInfo
    {
        public int cteCount_;
        // map cteId to Cteinfo
        public Dictionary<int, CteInfoEntry> CteIdToCteInfoEntry_;

        public CteInfo()
        {
            CteIdToCteInfoEntry_ = new Dictionary<int, CteInfoEntry>();
        }

        public CteInfoEntry GetCteInfoEntryByCteId(int cteId)
        {
            var cteInfoEntry = new CteInfoEntry();
            bool isGet = CteIdToCteInfoEntry_.TryGetValue(cteId, out cteInfoEntry);
            Debug.Assert(isGet);
            return cteInfoEntry;
        }

        public List<CteInfoEntry> GetAllCteInfoEntries()
        {
            var cieList = new List<CteInfoEntry>();
            foreach (var kv in CteIdToCteInfoEntry_)
            {
                cieList.Add(kv.Value);
            }
            return cieList;
        }
        public List<CteExpr> GetAllCteExprs()
        {
            var ceList = new List<CteExpr>();
            foreach (var v in GetAllCteInfoEntries())
            {
                ceList.Add(v.cte_);
            }
            return ceList;
        }

        public void SetCetInfoEntryByCteId(int cteId, CteInfoEntry cie) => CteIdToCteInfoEntry_[cteId] = cie;

        public void addCteProducer(int cteId, CteInfoEntry cie)
        {
            Debug.Assert(cteId >= 0);
            Debug.Assert(cie != null);
            CteIdToCteInfoEntry_.Add(cteId, cie);
            cteCount_++;
        }

        // find all CTE Consumer In Parent and push it into stack
    }

    public class LogicCteProducer : LogicNode
    {
        internal CteExpr cte_;

        public CteInfoEntry cteInfoEntry_;
        // represent the id of CTE, it should match the related CteProducer
        public int cteId_;
        public override string ToString() => $"CteProducer({child_()})";

        public LogicCteProducer(CteInfoEntry cteInfoEntry)
        {
            cteInfoEntry_ = cteInfoEntry;
            children_.Add(cteInfoEntry.plan_);
            // CTEProducer has only one child
            Debug.Assert(children_.Count() == 1);
        }

        public override string ExplainInlineDetails() => cteInfoEntry_.cte_.cteName_;
    }

    public class LogicSelectCte : LogicNode
    {
        public CteInfoEntry cteInfoEntry_;

        public LogicCteConsumer logicCteConsumer_;
        public LogicSelectCte(LogicCteConsumer lcc, Memo memo)
        {
            // we have to derive the subplans and add cte id
            //
            LogicNode ctePlanRoot = lcc.cteInfoEntry_.plan_.Clone();
            var cteId = lcc.cteInfoEntry_.cte_.cteId_;
            var cteRefId = lcc.refId_;
            // extract from memoRef
            ctePlanRoot = ctePlanRoot.deRefMemoRef();
            // reset Signature
            ctePlanRoot.VisitEach(x =>
            {
                x.logicNodeCteId_ = cteId;
                x.logicNodeCteRefId_ = String.Format(@"refId_{0}", cteRefId);
                // because they have inlined to a cte consumer, so their signature have to change
            });
            ctePlanRoot.VisitEach(x =>
            {
                x.ResetLogicSign();
            });

            // then add them into the plan
            children_.Add(ctePlanRoot);
            memo.EnquePlan(ctePlanRoot);
            Debug.Assert(children_.Count() == 1);
            cteInfoEntry_ = lcc.cteInfoEntry_;
            logicCteConsumer_ = lcc;
            output_ = lcc.output_;
        }

        // LogicSelectCte has the same LogicSignature with LogciCteConsumer
        public override LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                return logicCteConsumer_.MemoLogicSign();
            return logicSign_;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.RemoveAll(x => x is SubqueryExpr);
            children_[0].ResolveColumnOrdinal(reqFromChild);
            logicCteConsumer_.ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_ = logicCteConsumer_.output_;
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public class LogicCteConsumer : LogicNode
    {
        public QueryRef queryRef_;

        // if there is 2 consumer, then the first on is refId_ = 1 and the secont is refId_ = 2
        public int refId_ = -1;

        // represent the id of CTE, it should match the related CteProducer
        public int cteId_ = -1;
        public override string ToString() => $"LogicCteConsumer({cteId_})";

        public CteInfoEntry cteInfoEntry_;

        // Cte Consumer has no child
        public LogicCteConsumer(CteInfoEntry cteInfo, QueryRef queryRef)
        {
            cteId_ = cteInfo.cte_.cteId_;
            cteInfoEntry_ = cteInfo;
            queryRef_ = queryRef;
        }

        public override LogicSignature MemoLogicSign()
        {
            if (logicSign_ == -1)
                logicSign_ = cteInfoEntry_.GetType().GetHashCode() ^ refId_.ToString().GetHashCode() ^ cteInfoEntry_.cte_.cteId_.GetHashCode();
            return logicSign_;
        }

        // it is similar to from query.
        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            var query = queryRef_.query_;

            // a plan  From Query
            var ctePlan = query.cteInfo_.GetCteInfoEntryByCteId(cteId_).plan_;

            List<Expr> cteProdOut = ctePlan.output_;
            if (cteProdOut.Count == 0)
            {
                ctePlan.ResolveColumnOrdinal(query.selection_);
            }
            var childout = ctePlan.output_;
            output_ = CloneFixColumnOrdinal(reqOutput, childout, false);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            RefreshOutputRegisteration();
            return ordinals;
        }

        public override string ExplainInlineDetails() => "LogicCTEConsumer";
    }

    public class LogicCteAnchor : LogicNode
    {
        // represent the id of CTE, it should match the related CteProducer
        public CteInfoEntry cteInfoEntry_;

        public override string ToString() => $"CteAnchor({cteInfoEntry_.cte_.cteId_})";

        // Cte Consumer has no child
        public LogicCteAnchor(CteInfoEntry cteInfoEntry)
        {
            cteInfoEntry_ = cteInfoEntry;
        }

        public override string ExplainInlineDetails() => "CteAnchor";

        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ cteInfoEntry_.cte_.cteId_.GetHashCode();
        }
    }
}

namespace qpmodel.physic
{
    using ChildrenRequirement = System.Collections.Generic.List<PhysicProperty>;
    public class PhysicSelectCte : PhysicNode
    {
        public override string ToString() => $"SelectCte({string.Join(",", children_)}: {Cost()})";

        // CteAnchor has no operatopm
        public PhysicSelectCte(LogicNode logic, PhysicNode child) : base(logic)
        {
            children_.Add(child);
            Debug.Assert(children_.Count() == 1);
        }

        // FIXME we should remove this
        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            child_().Exec(r =>
            {
                callback(r);
            });
        }
        protected override double EstimateCost()
        {
            // select do no thing 
            // 
            return 0;
        }
    }

    public class PhysicMarkJoin : PhysicNode
    {
        public PhysicMarkJoin(LogicMarkJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PMarkJOIN({lchild_()},{rchild_()}: {Cost()})";

        // find the #marker from children output and located the #marker by subquery_id_.
        void fixMarkerValue(Row r, Value value)
        {
            int ordinal = findMarkerOrdinal();
            r[ordinal] = value;
        }

        int findMarkerOrdinal()
        {
            var output = this.logic_.output_;
            int ordinal = output.FindIndex(x => x is ExprRef xE && xE.child_() is MarkerExpr xEM
                            && logic_ is LogicMarkJoin lm && xEM.subqueryid_ == lm.subquery_id_);
            return ordinal;
        }

        private bool filterHasMarkerBinExpr(Expr filter) => filter.FilterToAndList().Exists(x => x is BinExpr xB && xB.IsMarkerBinExpr());

        public override void Exec(Action<Row> callback)
        {
            var isDerivedFromInClause = filterHasMarkerBinExpr(logic_.filter_);
            Exec(callback, isDerivedFromInClause);
        }
        public void Exec(Action<Row> callback, bool isDerivedFromInClause)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicMarkSemiJoin);
            bool antisemi = (logic_ is LogicMarkAntiSemiJoin);
            bool lIsNull = false; // l represent one row
            bool RHasNull = false; // R represent a set of Row
            bool RisEmpty = true;
            Value marker = false; //false true null
            int markerOrdinal = 0;
            Debug.Assert(filter != null);

            lchild_().Exec(l =>
            {
                lIsNull = l.ColsHasNull();
                marker = false;
                rchild_().Exec(r =>
                {
                    Row n = new Row(l, r);
                    if (isDerivedFromInClause)
                    {
                        if (!(r is null))
                            RHasNull = r.ColsHasNull();
                        var andList = filter.FilterToAndList();

                        // SELECT a1 FROM a WHERE a1 = 3 and a2 NOT IN (SELECT b2 FROM b WHERE a1 < b1);
                        // a1 < b1 will not produce marker 
                        // a2 = b2 will produce marker 
                        // if the markjoin is derived from IN clause, we need to judge if it is a empty
                        //
                        if (andList.Count >= 2)
                        {
                            var markerExpr = andList.Find(x => x is BinExpr xB && xB.IsMarkerBinExpr());
                            andList.Remove(markerExpr);
                            var excludeMarkerExpr = FilterHelper.AndListToExpr(andList);
                            var flagE = excludeMarkerExpr.Exec(context, n);

                            if (flagE is true)
                                RisEmpty = false;
                            else
                                return;

                            // there is at least one match, mark true
                            if (markerExpr.Exec(context, n) is true)
                                marker = true;
                        }
                        else if (filter.Exec(context, n) is true)
                            marker = true;
                    }
                    else if (!(marker is true) && !isDerivedFromInClause)
                    {
                        if (filter.Exec(context, n) is true)
                        {
                            marker = true;
                            n = ExecProject(n);
                            fixMarkerValue(n, semi);
                            callback(n);
                        }
                    }
                });

                if (isDerivedFromInClause)
                {
                    if (marker is false && RHasNull)
                        marker = null;

                    if (lIsNull && RisEmpty)
                        marker = false;

                    Row r = new Row(rchild_().logic_.output_.Count);
                    Row n = new Row(l, r);
                    n = ExecProject(n);

                    markerOrdinal = findMarkerOrdinal();
                    if (marker is null)
                        fixMarkerValue(n, false);
                    else
                    {
                        bool boolMarker = marker is true;
                        fixMarkerValue(n, semi ? boolMarker : !boolMarker);
                    }

                    callback(n);
                }
                else if (!(marker is true) && !isDerivedFromInClause)
                {
                    Row r = new Row(rchild_().logic_.output_.Count);
                    Row n = new Row(l, r);
                    n = ExecProject(n);
                    fixMarkerValue(n, !semi);
                    callback(n);
                }
            });
        }

        protected override double EstimateCost()
        {
            double cost = lchild_().Card() * rchild_().Card();
            return cost;
        }
    }

    // This is a binary operator that executes its children in 
    // order(left to right), and returns the results of the right child.
    // 
    public class PhysicSequence : PhysicNode
    {
        public override string ToString() => $"Sequence({string.Join(",", children_)}: {Cost()})";

        public int cteId_ = -1;

        public PhysicSequence(LogicNode logic, List<PhysicNode> children) : base(logic)
        {
            cteId_ = (logic as LogicSequence).cteAnchor_.cteInfoEntry_.cte_.cteId_;
            Debug.Assert(children.Count == 2);
            children_ = children;
        }

        PhysicNode OutputChild() => children_[children_.Count - 1];

        void ExecNonOutputChildren()
        {
            for (int i = 0; i < children_.Count - 1; i++)
            {
                var child = children_[i];
                child.Exec(null);
            }
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicSequence;

            ExecNonOutputChildren();

            OutputChild().Exec(r =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"";
                }
                else
                {
                    callback(r);
                }
            });
        }
        protected override double EstimateCost()
        {
            return 0;
        }

        public override bool CanProvide(PhysicProperty required, out List<List<PhysicProperty>> listChildReqs, bool isLastNode)
        {
            if (!required.DistributionIsSuppliedBy(DistrProperty.Singleton_(required.cteSpecList_)))
            {
                listChildReqs = null;
                return false;
            }
            else
            {
                listChildReqs = new List<ChildrenRequirement>();
                var leftreq = new DistrProperty();
                leftreq.cteSpecList_.Add((CTEType.CTEProducer, cteId_, true));
                var rightreq = new DistrProperty();
                // order property transfer to right child only, as left child will return no
                // 
                if (required.ordering_.Count > 0)
                {
                    rightreq.ordering_ = required.ordering_;
                }
                rightreq.cteSpecList_.Add((CTEType.CTEConsumer, cteId_, false));
                listChildReqs.Add(new ChildrenRequirement { leftreq, rightreq });
                return true;
            }
        }
    }

    public class PhysicCteAnchor : PhysicNode
    {
        public override string ToString() => $"CteAnchor({string.Join(",", children_)}: {Cost()})";

        // CteAnchor has no operatopm
        public PhysicCteAnchor(LogicNode logic, PhysicNode child) : base(logic)
        {
            children_.Add(child);
        }

        // Anchor has no opration
        // we should remove this
        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            child_().Exec(r =>
            {
                callback(r);
            });
        }
        protected override double EstimateCost()
        {
            // CTEAnchor has no cost
            // FIXME 
            return 0;
        }
    }

    public class PhysicCteConsumer : PhysicNode
    {
        List<Row> cteCache_ = null;

        internal List<Row> heap_ = new List<Row>();

        public PhysicCteConsumer(LogicNode logic) : base(logic)
        {
            // not inlined CteConsumer has no child 
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            var logic = logic_ as LogicCteConsumer;
            cteCache_ = context.GetCteProducer(logic.cteInfoEntry_.cte_.cteName_);
        }

        public override void Exec(Action<Row> callback)
        {
            foreach (Row l in cteCache_)
            {
                var r = ExecProject(l);
                callback(r);
            }
        }
        protected override double EstimateCost()
        {
            return (double)(cteCache_?.Count() ?? 0);
        }
        public override bool CanProvide(PhysicProperty required, out List<ChildrenRequirement> listChildReqs, bool isLastNode)
        {
            // TODO to check if this is a bad plan 
            // it need to get pre-children proper

            var logic = logic_ as LogicCteConsumer;
            var curCteSpec = (CTEType.CTEConsumer, logic.cteId_, true);

            var curProperty = new DistrProperty();
            curProperty.cteSpecList_ = new List<(CTEType ctetype, int cteid, bool optional)>();
            curProperty.cteSpecList_.Add(curCteSpec);

            if (!required.CTEIsSuppliedBy(curProperty))
            {
                listChildReqs = null;
                return false;
            }

            listChildReqs = new List<ChildrenRequirement>();
            listChildReqs.Add(new ChildrenRequirement());
            listChildReqs[0].Add(required);
            return true;
        }
    }

    public class PhysicCteProducer : PhysicNode
    {
        internal List<Row> heap_ = new List<Row>();

        public override string ToString() => $"PCteProducer({child_()}: {Cost()})";
        public PhysicCteProducer(LogicNode logic, PhysicNode child) : base(logic)
        {
            this.children_.Add(child);
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            var logic = logic_ as LogicCteProducer;
            context.RegisterCteProducer(logic.cteInfoEntry_.cte_.cteName_, heap_);
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicCteProducer;

            child_().Exec(r =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"";
                }
                else
                {
                    // cache the results
                    heap_.Add(r);
                }
            });
        }

        protected override double EstimateCost()
        {
            // producer will materialize the rows
            //
            return logic_.Card() * 0.5;
        }

        public override bool CanProvide(PhysicProperty required, out List<ChildrenRequirement> listChildReqs, bool isLastNode)
        {
            var logic = logic_ as LogicCteProducer;

            var curCteSpec = (CTEType.CTEProducer, logic.cteId_, true);
            var curProperty = new DistrProperty();
            curProperty.cteSpecList_ = new List<(CTEType ctetype, int cteid, bool optional)>();
            curProperty.cteSpecList_.Add(curCteSpec);

            if (!required.CTEIsSuppliedBy(curProperty))
            {
                listChildReqs = null;
                return false;
            }

            listChildReqs = new List<ChildrenRequirement>();
            listChildReqs.Add(new ChildrenRequirement());
            listChildReqs[0].Add(required);
            return true;
        }
    }
}
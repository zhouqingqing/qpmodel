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
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

using qpmodel.expr;
using qpmodel.physic;
using qpmodel.utils;

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

namespace qpmodel.logic
{
    public class MarkerExpr : Expr
    {
        public MarkerExpr(List<TableRef> tr)
        {
            tableRefs_ = tr;
            Debug.Assert(Equals(Clone()));
            dummyBind();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            // its values is fed by mark join so non input related
            return null;
        }

        public override string ToString() => $"#marker";
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
        LogicNode existsToMarkJoin(LogicNode nodeA, ExistSubqueryExpr existExpr)
        {
            var nodeAIsOnMarkJoin =
                nodeA is LogicFilter && (nodeA.child_() is LogicMarkJoin || nodeA.child_() is LogicSingleJoin);

            // nodeB contains the join filter
            var nodeB = existExpr.query_.logicPlan_;
            var nodeBFilter = nodeB.filter_;
            nodeB.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var markerFilter = new ExprRef(new MarkerExpr(nodeBFilter.tableRefs_), 0);
            var nodeAFilter = nodeA.filter_;
            if (nodeAIsOnMarkJoin)
                nodeA.filter_ = markerFilter;
            else
            {
                if (nodeAFilter != null)
                {
                    // a1 > @1 and a2 > @2 and a3 > 2, scalarExpr = @1
                    //   keeplist: a1 > @1 and a3 > 2
                    //   andlist after removal: a2 > @2
                    //   nodeAFilter = a1 > @1 and a3 > 2
                    //
                    var andlist = nodeAFilter.FilterToAndList();
                    var keeplist = andlist.Where(x => x.VisitEachExists(e => e.Equals(existExpr))).ToList();
                    andlist.RemoveAll(x => x.VisitEachExists(e => e.Equals(existExpr)));
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
            }
            // make a mark join
            LogicMarkJoin markjoin;
            if (existExpr.hasNot_)
                markjoin = new LogicMarkAntiSemiJoin(nodeA, nodeB);
            else
                markjoin = new LogicMarkSemiJoin(nodeA, nodeB);

            // make a filter on top of the mark join collecting all filters
            Expr topfilter;
            if (nodeAIsOnMarkJoin)
                topfilter = nodeAFilter.SearchAndReplace(existExpr, ConstExpr.MakeConstBool(true));
            else
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
                            if ((c is ColExpr ce) && ce.isParameter_)
                            {
                                notDeparameterExpr.Add(e);
                            }
                        });
                    }
                    var removeList = notDeparameterExpr.Where(x => andList.Contains(x));
                    foreach (var r in removeList)
                    {
                        andList.Remove(r);
                    }
                    if (andList.Count == 0)
                    {
                        x.NullifyFilter();
                    }
                    else
                    {
                        x.filter_ = andList.AndListToExpr();
                    }
                    Console.WriteLine("");
                }
            });

            if (notDeparameterExpr.Count > 1)
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

        // A Xs B => A LOJ B if max1row is assured
        LogicJoin singleJoin2OuterJoin(LogicSingleJoin singJoinNode)
        {
            LogicJoin newjoin = singJoinNode;
            if (!singJoinNode.max1rowCheck_)
            {
                newjoin = new LogicJoin(singJoinNode.lchild_(), singJoinNode.rchild_(), singJoinNode.filter_);
                newjoin.type_ = JoinType.Left;
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
                nonMovableFilter = new LogicFilter(singleJoinNode, decExpr);
                nonMovableFilter.movable_ = false;
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
        LogicNode oneSubqueryToJoin(LogicNode planWithSubExpr, SubqueryExpr subexpr)
        {
            LogicNode oldplan = planWithSubExpr;
            LogicNode newplan = null;

            if (!subexpr.IsCorrelated())
                return planWithSubExpr;

            switch (subexpr)
            {
                case ExistSubqueryExpr se:
                    newplan = existsToMarkJoin(planWithSubExpr, se);
                    break;
                case ScalarSubqueryExpr ss:
                    newplan = scalarToSingleJoin(planWithSubExpr, ss);
                    break;
                default:
                    break;
            }
            if (oldplan != newplan)
                decorrelatedSubs_.Add(new NamedQuery(subexpr.query_, null));
            return newplan;
        }

        LogicNode scalarSubqueryToJoin(LogicNode planWithSubExpr, SubqueryExpr subexpr)
        {
            LogicNode oldplan = planWithSubExpr;
            LogicNode newplan = null;

            Debug.Assert(subexpr is ScalarSubqueryExpr);
            if(subexpr is ScalarSubqueryExpr ss)
            {
                newplan = scalarToSingleJoin(planWithSubExpr, ss);
            }
            if (oldplan != newplan)
                decorrelatedSubs_.Add(new NamedQuery(subexpr.query_, null));
            return newplan;
        }

        // if there is a CteExpr referenced more than once, we can use a sequence plan
        LogicNode tryCteToSequencePlan(LogicNode root)
        {
            if (!queryOpt_.optimize_.enable_cte_plan_)
                return root;

            var list = ctes_.Where(x => x.refcnt_ > 1).ToList();
            if (list.Count == 0)
                return root;

            var seqNode = new LogicSequence();
            seqNode.children_.AddRange(list.Select(x =>
                    new LogicCteProducer(x.query_.logicPlan_, x)));
            seqNode.children_.Add(root);
            return seqNode;
        }
    }

    // mark join is like semi-join form with an extra boolean column ("mark") indicating join 
    // predicate results (true|false|null)
    //
    public class LogicMarkJoin : LogicJoin
    {
        public override string ToString() => $"{lchild_()} markX {rchild_()}";
        public LogicMarkJoin(LogicNode l, LogicNode r) : base(l, r) { type_ = JoinType.Left; }
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
        public LogicMarkSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicMarkSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }
    public class LogicMarkAntiSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{lchild_()} markAntisemiX {rchild_()}";
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }

    public class PhysicMarkJoin : PhysicNode
    {
        public PhysicMarkJoin(LogicMarkJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PMarkJOIN({lchild_()},{rchild_()}: {Cost()})";

        // always the last column, as it is added at the last time 
        void fixMarkerValue(Row r, Value value) => r[r.ColCount()-1] = value;

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicMarkSemiJoin);
            bool antisemi = (logic_ is LogicMarkAntiSemiJoin);

            Debug.Assert(filter != null);

            lchild_().Exec(l =>
            {
                bool foundOneMatch = false;
                rchild_().Exec(r =>
                {
                    if (!foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter.Exec(context, n) is true)
                        {
                            foundOneMatch = true;

                            // there is at least one match, mark true
                            n = ExecProject(n);
                            fixMarkerValue(n, semi ? true : false);
                            callback(n);
                        }
                    }
                });

                // if there is no match, mark false
                if (!foundOneMatch)
                {
                    Row n = ExecProject(l);
                    fixMarkerValue(n, semi ? false : true);
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

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            for (int i = 0; i < children_.Count - 1; i++)
            {
                var child = children_[i] as LogicCteProducer;
                child.ResolveColumnOrdinal(child.cte_.query_.selection_, false);
            }
            OutputChild().ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_ = OutputChild().output_;
            RefreshOutputRegisteration();
            return ordinals;
        }
    }

    public class PhysicSequence : PhysicNode
    {
        public override string ToString() => $"Sequence({string.Join(",", children_)}: {Cost()})";

        public PhysicSequence(LogicNode logic, List<PhysicNode> children) : base(logic)
        {
            Debug.Assert(children.Count >= 2);
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
            return logic_.Card() * 0.5;
        }
    }

    public class LogicCteProducer : LogicNode
    {
        internal CteExpr cte_;
        public override string ToString() => $"CteProducer({child_()})";

        public LogicCteProducer(LogicNode child, CteExpr cte)
        {
            children_.Add(child);
            cte_ = cte;
        }

        public override string ExplainInlineDetails() => cte_.cteName_;
    }

    public class PhysicCteProducer : PhysicNode
    {
        internal List<Row> heap_ = new List<Row>();

        public override string ToString() => $"PCteProducer({child_()}: {Cost()})";
        public PhysicCteProducer(LogicNode logic, PhysicNode child) : base(logic)
        {
            children_.Add(child);
        }

        public override void Open(ExecContext context)
        {
            base.Open(context);
            var logic = logic_ as LogicCteProducer;
            context.RegisterCteProducer(logic.cte_.cteName_, heap_);
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
            return logic_.Card() * 0.5;
        }
    }
}

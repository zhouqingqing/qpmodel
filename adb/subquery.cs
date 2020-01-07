using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;


namespace adb
{
    public class MarkerExpr : Expr
    {
        public MarkerExpr() {
            Debug.Assert(Equals(Clone()));
            type_ = new BoolType();
            bounded_ = true;
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
                nodeA is LogicFilter && nodeA.child_() is LogicMarkJoin;

            // nodeB contains the join filter
            var nodeB = existExpr.query_.logicPlan_;
            var nodeBFilter = nodeB.filter_;
            nodeB.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var markerFilter = new ExprRef(new MarkerExpr(), 0);
            var nodeAFilter = nodeA.filter_;
            if (nodeAIsOnMarkJoin)
                nodeA.filter_ = markerFilter;
            else
                nodeA.NullifyFilter();

            // make a mark join
            LogicMarkJoin markjoin;
            if (existExpr.hasNot_)
                markjoin = new LogicMarkAntiSemiJoin(nodeA, nodeB);
            else
                markjoin = new LogicMarkSemiJoin(nodeA, nodeB);
            markjoin.AddFilter(nodeBFilter);

            // make a filter on top of the mark join
            Expr topfilter;
            if (nodeAIsOnMarkJoin)
                topfilter = nodeAFilter.SearchReplace(existExpr, new LiteralExpr("true", new BoolType()));
            else
                topfilter = nodeAFilter.SearchReplace(existExpr, markerFilter);
            LogicFilter Filter = new LogicFilter(markjoin, topfilter);
            return Filter;
        }

        // expands scalar subquery filter to mark join
        //
        //  LogicNode_A
        //     Filter: a.a2 = @1 AND|OR <others1>
        //     <ExistSubqueryExpr> 1
        //          -> LogicNode_B
        //             Output: singleValueExpr
        //             Filter: b.b1[0]=?a.a1[0] AND|OR <others2>
        // =>
        //  LogicFilter
        //      Filter: (#marker AND a.a2 = singleValueExpr) AND|OR <others1>
        //      MarkJoin
        //          Outpu: singleValueExpr
        //          Filter:  (b.b1[0]=a.a1[0]) AND|OR <others2>
        //          LogicNode_A
        //          LogicNode_B
        //
        LogicNode scalarToMarkJoin(LogicNode nodeA, ScalarSubqueryExpr scalarExpr)
        {
            var nodeAIsOnMarkJoin =
                nodeA is LogicFilter && nodeA.child_() is LogicMarkJoin;

            // FIXME: temp disable it
            if (nodeAIsOnMarkJoin)
                return nodeA;

            // nodeB contains the join filter
            var nodeB = scalarExpr.query_.logicPlan_;
            var nodeBFilter = nodeB.filter_;

            // some plan may hide the correlation filter deep, say:
            // Filter: a.a1[0] =@1
            //   < ScalarSubqueryExpr > 1
            //      ->PhysicHashAgg(rows = 2)
            //            -> PhysicFilter(rows = 2)
            //               Filter: b.b2[1]=?a.a2[1] ...
            // Let's ignore this case for now
            //
            if (nodeBFilter is null)
                return nodeA;

            Debug.Assert(scalarExpr.query_.selection_.Count == 1);
            var singleValueExpr = scalarExpr.query_.selection_[0];
            nodeB.NullifyFilter();

            // nullify nodeA's filter: the rest is push to top filter. However,
            // if nodeA is a Filter|MarkJoin, keep its mark filter.
            var markerFilter = new ExprRef(new MarkerExpr(), 0);
            var nodeAFilter = nodeA.filter_;
            if (nodeAIsOnMarkJoin)
                nodeA.filter_ = markerFilter;
            else
                nodeA.NullifyFilter();

            // make a mark join
            var markjoin = new LogicSingleMarkJoin(nodeA, nodeB);
            markjoin.AddFilter(nodeBFilter);

            // make a filter on top of the mark join
            Expr topfilter;
            if (nodeAIsOnMarkJoin)
                topfilter = nodeAFilter.SearchReplace(scalarExpr, new LiteralExpr($"{int.MinValue}", new IntType()));
            else
            {
                topfilter = markerFilter.AddAndFilter(nodeAFilter);
                topfilter = topfilter.SearchReplace(scalarExpr, singleValueExpr);
            }
            LogicFilter Filter = new LogicFilter(markjoin, topfilter);
            return Filter;
        }

        LogicNode subqueryToMarkJoin(LogicNode plan)
        {
            // before the filter push down, there shall be at most one filter
            // except for from query. 
            var parents = new List<LogicNode>();
            var indexes = new List<int>();
            var filters = new List<LogicFilter>();
            var cntFilter = plan.FindNodeTyped(parents, indexes, filters);
            Debug.Assert(cntFilter <= 1 || plan.CountNodeTyped<LogicFromQuery>() > 0);

            {
                var filter = plan.filter_;
                if (filter != null)
                {
                    foreach (var ef in filter.RetrieveAllType<ExistSubqueryExpr>())
                    {
                        if (ef.IsCorrelated())
                        {
                            var oldplan = plan;
                            plan = existsToMarkJoin(plan, ef);
                            if (oldplan != plan)
                                decorrelatedSubs_.Add(ef.query_);
                        }
                    }
                    foreach (var ef in filter.RetrieveAllType<ScalarSubqueryExpr>())
                    {
                        if (ef.IsCorrelated())
                        {
                            var oldplan = plan;
                            plan = scalarToMarkJoin(plan, ef);
                            if (oldplan != plan)
                                decorrelatedSubs_.Add(ef.query_);
                        }
                    }
                }
            }

            return plan;
        }
    }

    // mark join is like semi-join form with an extra boolean column ("mark") indicating join 
    // predicate results (true|false|null)
    //
    public class LogicMarkJoin : LogicJoin 
    {
        public override string ToString() => $"{l_()} markX {r_()}";
        public LogicMarkJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicMarkJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            var list = base.ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_.Insert(0, new MarkerExpr());
            return list;
        }
    }
    
    public class LogicMarkSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{l_()} markSemiX {r_()}";
        public LogicMarkSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicMarkSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }
    public class LogicMarkAntiSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{l_()} markAntisemiX {r_()}";
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }

    public class LogicSingleMarkJoin : LogicMarkJoin
    {
        public override string ToString() => $"{l_()} singlemarkX {r_()}";
        public LogicSingleMarkJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicSingleMarkJoin(LogicNode l, LogicNode r, Expr f ) : base(l, r, f) { }
    }

    public class PhysicMarkJoin : PhysicNode
    {
        public PhysicMarkJoin(LogicMarkJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PMarkJOIN({l_()},{r_()}: {Cost()})";

        // always the first column
        void fixMarkerValue(Row r, Value value) => r[0] = value;

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicMarkSemiJoin);
            bool antisemi = (logic_ is LogicMarkAntiSemiJoin);

            Debug.Assert(filter != null);

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    if (!foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter.Exec(context, n) is true)
                        {
                            foundOneMatch = true;

                            // there is at least one match, mark true
                            n = ExecProject(context, n);
                            fixMarkerValue(n, semi ? true : false);
                            callback(n);
                        }
                    }
                    return null;
                });

                // if there is no match, mark false
                if (!foundOneMatch)
                {
                    Row n = ExecProject(context, l);
                    fixMarkerValue(n, semi ? false: true);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_() as PhysicMemoRef).MinCost() * (r_() as PhysicMemoRef).MinCost();
            return cost_;
        }
    }

    public class PhysicSingleMarkJoin : PhysicNode
    {
        public PhysicSingleMarkJoin(LogicMarkJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PSingleMarkJOIN({l_()},{r_()}: {Cost()})";

        // always the first column
        void fixMarkerValue(Row r, Value value) => r[0] = value;

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            Debug.Assert(logic is LogicSingleMarkJoin);

            Debug.Assert(filter != null);

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    Row n = new Row(l, r);
                    if (filter.Exec(context, n) is true)
                    {
                        if (foundOneMatch)
                            throw new SemanticExecutionException("more than one row matched");
                        foundOneMatch = true;

                        // there is at least one match, mark true
                        n = ExecProject(context, n);
                        fixMarkerValue(n, true);
                        callback(n);
                    }
                    return null;
                });

                // if there is no match, mark false
                if (!foundOneMatch)
                {
                    var nNulls = r_().logic_.output_.Count;
                    Row n = new Row(l, new Row(nNulls));
                    n = ExecProject(context, n);
                    fixMarkerValue(n, false);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_() as PhysicMemoRef).MinCost() * (r_() as PhysicMemoRef).MinCost();
            return cost_;
        }
    }

    // Single join acts like outer join with an execption that no more than one row matching allowed
    public class LogicSingleJoin : LogicJoin
    {
        public override string ToString() => $"{l_()} singleX {r_()}";
        public LogicSingleJoin(LogicNode l, LogicNode r) : base(l, r) { }
        public LogicSingleJoin(LogicNode l, LogicNode r, Expr f) : base(l, r, f) { }
    }

    public class PhysicSingleJoin : PhysicNode
    {
        public PhysicSingleJoin(LogicSingleJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PSingleJOIN({l_()},{r_()}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicSingleJoin;
            var filter = logic.filter_;

            Debug.Assert(filter != null);

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    Row n = new Row(l, r);
                    if (filter.Exec(context, n) is true)
                    {
                        if (foundOneMatch)
                            throw new SemanticExecutionException("more than one row matched");
                        foundOneMatch = true;

                        n = ExecProject(context, n);
                        callback(n);
                    }
                    return null;
                });

                // if there is no match, mark false
                if (!foundOneMatch)
                {
                    Row n = ExecProject(context, l);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_() as PhysicMemoRef).MinCost() * (r_() as PhysicMemoRef).MinCost();
            return cost_;
        }
    }

}

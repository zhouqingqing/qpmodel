using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

using adb.expr;
using adb.physic;

namespace adb.logic
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

            // make a filter on top of the mark join collecting all filters
            Expr topfilter;
            if (nodeAIsOnMarkJoin)
                topfilter = nodeAFilter.SearchReplace(existExpr, LiteralExpr.MakeLiteral("true", new BoolType()));
            else
                topfilter = nodeAFilter.SearchReplace(existExpr, markerFilter);
            nodeBFilter.DeParameter(nodeA.InclusiveTableRefs());
            topfilter = topfilter.AddAndFilter(nodeBFilter);
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

            // make a filter on top of the mark join collecting all filters
            Expr topfilter;
            if (nodeAIsOnMarkJoin)
                topfilter = nodeAFilter.SearchReplace(scalarExpr, LiteralExpr.MakeLiteral($"{int.MinValue}", new IntType()));
            else
            {
                topfilter = markerFilter.AddAndFilter(nodeAFilter);
                topfilter = topfilter.SearchReplace(scalarExpr, singleValueExpr);
            }
            nodeBFilter.DeParameter(nodeA.InclusiveTableRefs());
            topfilter = topfilter.AddAndFilter(nodeBFilter);
            LogicFilter Filter = new LogicFilter(markjoin, topfilter);
            return Filter;
        }

        LogicNode oneSubqueryToMarkJoin(LogicNode subplan, SubqueryExpr subexpr)
        {
            LogicNode oldplan = subplan;
            LogicNode newplan = null;

            if (!subexpr.IsCorrelated())
                return subplan;

            switch (subexpr) {
                case ExistSubqueryExpr se:
                    newplan = existsToMarkJoin(subplan, se);
                    break;
                case ScalarSubqueryExpr ss:
                    newplan = scalarToMarkJoin(subplan, ss);
                    break;
                default:
                    break;
            }
            if (oldplan != newplan)
                decorrelatedSubs_.Add(subexpr.query_);
            return newplan;
        }

        LogicNode subqueryToMarkJoin(LogicNode plan)
        {
            // before the filter push down, there shall be at most one filter
            // except for from query. 
            var parentNodes = new List<LogicNode>();
            var indexes = new List<int>();
            var logFilterNodes = new List<LogicFilter>();
            var cntFilter = plan.FindNodeTyped(parentNodes, indexes, logFilterNodes);
            Debug.Assert(cntFilter <= 1 || plan.CountNodeTyped<LogicFromQuery>() > 0);

            if (cntFilter == 0)
                return plan;

            if (parentNodes[0] is LogicFromQuery)
                return plan;

            LogicNode subplan = logFilterNodes[0];
            var filterExpr = subplan.filter_;
            if (filterExpr != null)
            {
                foreach (var ef in filterExpr.RetrieveAllType<SubqueryExpr>())
                    subplan = oneSubqueryToMarkJoin(subplan, ef);

                // merge back to the root plan
                if (parentNodes[0] is null)
                    plan = subplan;
                else
                    parentNodes[0].children_[indexes[0]] = subplan;
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

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicMarkSemiJoin);
            bool antisemi = (logic_ is LogicMarkAntiSemiJoin);

            Debug.Assert(filter != null);

            l_().Exec(l =>
            {
                bool foundOneMatch = false;
                r_().Exec(r =>
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
                    return null;
                });

                // if there is no match, mark false
                if (!foundOneMatch)
                {
                    Row n = ExecProject(l);
                    fixMarkerValue(n, semi ? false: true);
                    callback(n);
                }
                return null;
            });
            return null;
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

        public override string Exec(Func<Row, string> callback)
        {
            ExecContext context = context_;
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            Debug.Assert(logic is LogicSingleMarkJoin);

            Debug.Assert(filter != null);

            l_().Exec(l =>
            {
                bool foundOneMatch = false;
                r_().Exec(r =>
                {
                    Row n = new Row(l, r);
                    if (filter.Exec(context, n) is true)
                    {
                        if (foundOneMatch)
                            throw new SemanticExecutionException("more than one row matched");
                        foundOneMatch = true;

                        // there is at least one match, mark true
                        n = ExecProject(n);
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
                    n = ExecProject(n);
                    fixMarkerValue(n, false);
                    callback(n);
                }
                return null;
            });
            return null;
        }

        public override double Cost()
        {
            if (double.IsNaN(cost_))
                cost_ = (l_() as PhysicMemoRef).MinCost() * (r_() as PhysicMemoRef).MinCost();
            return cost_;
        }
    }
}

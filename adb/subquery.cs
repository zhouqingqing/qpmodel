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
        // expands EXISTS filter to mark join
        //
        //  LogicNode_A
        //     Filter: @1 AND|OR <others>
        //     <ExistSubqueryExpr> 1
        //          -> LogicNode_B
        //             Filter: b.b1[0]=?a.a1[0]
        // =>
        //    Filter
        //      Filter: #marker AND|OR <others>
        //      MarkJoin
        //         Filter:  (b.b1[0]=a.a1[0]) as #marker 
        //         LogicNode_A
        //         LogicNode_B
        //
        // further convert DJoin to semi-join here is by decorrelate process
        //
        LogicNode existsToMarkJoin(LogicScanTable plan, ExistSubqueryExpr exists)
        {
            // mark join filter
            var mjfilter = (exists.query_.logicPlan_ as LogicFilter).filter_;

            // nullify plan filter: the rest is push to top filter
            var planfilter = plan.filter_;
            plan.filter_ = null;

            // make a mark join
            LogicMarkJoin djoin;
            if (exists.hasNot_)
                djoin = new LogicMarkAntiSemiJoin(plan,
                                exists.query_.logicPlan_.children_[0]);
            else
                djoin = new LogicMarkSemiJoin(plan,
                                exists.query_.logicPlan_.children_[0]);
            djoin.AddFilter(mjfilter);

            // make a filter on top of the mark join
            Expr topfilter = new ExprRef(new MarkerExpr(), 0);
            topfilter = planfilter.SearchReplace(exists, topfilter);
            LogicFilter top = new LogicFilter(djoin, topfilter);
            return top;
        }

        LogicNode subqueryToMarkJoin(LogicNode plan)
        {
            if (plan is LogicScanTable sp)
            {
                var filter = sp.filter_;
                var exitslist = ExprHelper.RetrieveAllType<ExistSubqueryExpr>(filter);
                if (exitslist.Count == 0)
                    return plan;

                foreach (var ef in exitslist)
                {
                    subqueries_.Remove(ef.query_);
                    return existsToMarkJoin(sp, ef);
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
        public override string ToString() => $"{l_()} dX {r_()}";
        public LogicMarkJoin(LogicNode l, LogicNode r) : base(l, r) { }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            var list = base.ResolveColumnOrdinal(reqOutput, removeRedundant);
            output_.Insert(0, new MarkerExpr());
            return list;
        }
    }
    
    public class LogicMarkSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{l_()} d_X {r_()}";
        public LogicMarkSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
    }
    public class LogicMarkAntiSemiJoin : LogicMarkJoin
    {
        public override string ToString() => $"{l_()} d^_X {r_()}";
        public LogicMarkAntiSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
    }

    public class PhysicMarkJoin : PhysicNode
    {
        public PhysicMarkJoin(LogicMarkJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"P#JOIN({l_()},{r_()}: {Cost()})";

        // always the first column
        void fixMarkerValue(Row r, Value value) => r.values_[0] = value;

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
                        if ((bool)filter.Exec(context, n))
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
            return (l_() as PhysicMemoNode).MinCost() * (r_() as PhysicMemoNode).MinCost();
        }
    }
}

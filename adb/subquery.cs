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
        public MarkerExpr()
        {
        }

        public override Value Exec(ExecContext context, Row input)
        {
            return true;
        }

        public override string ToString() => $"#marker";
    }

    public partial class SelectStmt : SQLStatement
    {
        // expands EXISTS filter to dependent-semi-join
        //
        //  LogicNode_A
        //     Filter: @1 ...<others>
        //     <ExistSubqueryExpr> 1
        //          -> LogicNode_B
        //             Filter: b.b1[0]=?a.a1[0]
        // =>
        //    DJoin
        //      Filter:  (b.b1[0]=a.a1[0]) as marker ... <others> 
        //      LogicNode_A
        //      LogicNode_B
        //
        // further convert DJoin to semi-join here is by decorrelate process
        //
        LogicNode existsToMarkJoin(LogicScanTable plan, ExistSubqueryExpr exists)
        {
            // correlated filter
            var corfilter = (exists.query_.logicPlan_ as LogicFilter).filter_;

            // remove filter
            plan.filter_ = null;

            LogicMarkJoin djoin;
            if (exists.hasNot_)
                djoin = new LogicMarkAntiSemiJoin(plan,
                                exists.query_.logicPlan_.children_[0]);
            else
                djoin = new LogicMarkSemiJoin(plan,
                                exists.query_.logicPlan_.children_[0]);
            djoin.AddFilter(corfilter);
            return djoin;
        }

        LogicNode subqueryToMarkJoin(LogicNode plan)
        {
            if (plan is LogicScanTable sp)
            {
                var filter = sp.filter_;
                var exitslist = ExprHelper.RetrieveAllType<ExistSubqueryExpr>(filter);
                if (exitslist.Count == 0)
                    return plan;

                if (!filter.Equals(exitslist[0]))
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
            //output_.Add(new MarkerExpr());
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
        public override string ToString() => $"PDepJ({children_[0]},{children_[1]}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicMarkJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicMarkSemiJoin);
            bool antisemi = (logic_ is LogicMarkAntiSemiJoin);

            Debug.Assert(filter != null);

            children_[0].Exec(context, l =>
            {
                bool foundOneMatch = false;
                children_[1].Exec(context, r =>
                {
                    if (!semi || !foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if ((bool)filter.Exec(context, n))
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
                });

                if (antisemi && !foundOneMatch)
                {
                    Row n = new Row(l, null);
                    n = ExecProject(context, n);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            return (children_[0] as PhysicMemoNode).MinCost() * (children_[1] as PhysicMemoNode).MinCost();
        }
    }
}

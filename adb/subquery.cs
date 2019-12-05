using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adb
{
    // dependent join is that the inner side depends on input of outer side
    public class LogicDependentJoin : LogicJoin {
        public override string ToString() => $"{l_()} dX {r_()}";
        public LogicDependentJoin(LogicNode l, LogicNode r) : base(l, r) { }
    }
    public class LogicDependentSemiJoin : LogicDependentJoin
    {
        public override string ToString() => $"{l_()} d_X {r_()}";
        public LogicDependentSemiJoin(LogicNode l, LogicNode r) : base(l, r) { }
    }

    public class PhysicDependentJoin : PhysicNode
    {
        public PhysicDependentJoin(LogicDependentJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PDepJ({children_[0]},{children_[1]}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicDependentJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicDependentSemiJoin);

            children_[0].Exec(context, l =>
            {
                bool foundOneMatch = false;
                children_[1].Exec(context, r =>
                {
                    if (!semi || !foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter is null || (bool)filter.Exec(context, n))
                        {
                            foundOneMatch = true;
                            n = ExecProject(context, n);
                            callback(n);
                        }
                    }
                    return null;
                });
                return null;
            });
        }

        public override double Cost()
        {
            return (children_[0] as PhysicMemoNode).MinCost() * (children_[1] as PhysicMemoNode).MinCost();
        }
    }
}

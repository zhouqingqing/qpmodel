using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;


namespace adb
{
    // mark join is a join form with an extra boolean column ("mark") indicating join 
    // predicate results (true|false|null)
    //
    public class LogicMarkJoin : LogicJoin {
        public override string ToString() => $"{l_()} dX {r_()}";
        public LogicMarkJoin(LogicNode l, LogicNode r) : base(l, r) { }
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

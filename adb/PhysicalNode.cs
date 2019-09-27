using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Value = System.Int64;

namespace adb
{
    public class Row {
        public List<Value> values_ = new List<Value>();

        public Row() { }
        public Row(Row l, Row r) {
            values_.AddRange(l.values_);
            values_.AddRange(r.values_);
        }

        public override string ToString()=> string.Join(",", values_);
    }

    public abstract class PhysicNode: PlanNode<PhysicNode>
    {
        internal LogicNode logic_;

        public PhysicNode(LogicNode logic)=>logic_ = logic;
        public virtual void Open() => children_.ForEach(x => x.Open());
        public virtual void Close() => children_.ForEach(x => x.Close());
        public override string PrintInlineDetails(int depth) => logic_.PrintInlineDetails(depth);
        public override string PrintMoreDetails(int depth) => logic_.PrintMoreDetails(depth);
        public abstract IEnumerable<Row> Next();
    }

    public class PhysicGet : PhysicNode{
        int nrows_ = 3;
        public PhysicGet(LogicNode logic): base(logic) { }

        public override IEnumerable<Row> Next()
        {
            for (int i = 0; i < nrows_; i++)
            {
                Row r = new Row();
                r.values_.Add(i);
                r.values_.Add(i+1);
                r.values_.Add(i+2);

                yield return r;
            }
        }
    }

    public class PhysicCrossJoin : PhysicNode {
        public PhysicCrossJoin(LogicCrossJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }

        public override IEnumerable<Row> Next()
        {
            foreach (var l in children_[0].Next())
                foreach (var r in children_[1].Next()) {
                    Row n = new Row(l, r);
                    yield return n;
                }
        }
    }

    public class PhysicResult : PhysicNode {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override IEnumerable<Row> Next()
        {
            Row r = new Row();
            (logic_ as LogicResult).expr_.ForEach(
                            x => r.values_.Add(x.Exec(null)));
            yield return r;
        }
    }

    public class PhysicPrint : PhysicNode {
        public PhysicPrint(PhysicNode child) : base(null) {
            children_.Add(child);
        }

        public override IEnumerable<Row> Next()
        {
            foreach (var r in children_[0].Next())
            {
                Console.WriteLine($"{r}");
            }

            return null;
        }
    }
}

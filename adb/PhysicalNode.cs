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

    public class ExecContext {
        public Row row_;
        public Row parameter_;

        public ExecContext() { }
        public ExecContext(Row row) => row_ = row;
    }

    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        internal LogicNode logic_;

        public PhysicNode(LogicNode logic) => logic_ = logic;

        public override string PrintOutput(int depth)
        {
            string r = "Output: " + string.Join(",", logic_.output_);
            logic_.output_.ForEach(x => r += ExprHelper.PrintExprWithSubqueryExpanded(x, depth));
            return r;
        }
        public override string PrintInlineDetails(int depth) => logic_.PrintInlineDetails(depth);
        public override string PrintMoreDetails(int depth) => logic_.PrintMoreDetails(depth);

        public virtual void Open() => children_.ForEach(x => x.Open());
        public virtual void Close() => children_.ForEach(x => x.Close());
        public abstract void Exec(Func<Row, string> callback);

        internal Row ExecProject(Row input) {
            Row r = new Row();
            logic_.output_.ForEach(x => r.values_.Add(x.Exec(input)));

            return r;
        }
    }

    public class PhysicGet : PhysicNode{
        int nrows_ = 3;
        public PhysicGet(LogicNode logic): base(logic) { }

        public override void Exec(Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicGet).filter_;

            for (int i = 0; i < nrows_; i++)
            {
                Row r = new Row();
                r.values_.Add(i);
                r.values_.Add(i + 1);
                r.values_.Add(i + 2);

                if (filter?.Exec(r) == 0)
                    continue;
                r = ExecProject(r);
                callback(r);
            }
        }
    }

    public class PhysicCrossJoin : PhysicNode {
        public PhysicCrossJoin(LogicCrossJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }

        public override void Exec(Func<Row, string> callback)
        {
            children_[0].Exec(l =>
            {
                children_[1].Exec(r =>
                {
                    Row n = new Row(l, r);
                    n = ExecProject(n);
                    callback(n);
                    return null;
                });
                return null;
            });
        }
    }

    public class PhysicFromQuery : PhysicNode {
        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(Func<Row, string> callback)
        {
            children_[0].Exec(l => {
                var r = ExecProject(l);
                callback(r);
                return null;
            });
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicFilter).filter_;

            children_[0].Exec(l => {
                if (filter is null || filter.Exec(l) == 1)
                {
                    var r = ExecProject(l);
                    callback(r);
                }
                return null;
            });
        }
    }

    public class PhysicResult : PhysicNode {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override void Exec(Func<Row, string> callback)
        {
            Row r = new Row();
            (logic_ as LogicResult).exprs_.ForEach(
                            x => r.values_.Add(x.Exec(null)));
            callback(r);
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);
        public override void Exec(Func<Row, string> callback)
        {
            children_[0].Exec(r =>
            {
                Console.WriteLine($"{r}");
                rows_.Add(r);
                return null;
            });
        }
    }
}

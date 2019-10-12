using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using Value = System.Int64;

namespace adb
{
    public class Row {
        readonly public List<Value> values_ = new List<Value>();

        public Row() { }
        public Row(Row l, Row r) {
            values_.AddRange(l.values_);
            values_.AddRange(r.values_);
        }

        public override string ToString()=> string.Join(",", values_);
    }

    public class Parameter {
        readonly public TableRef tabref_;
        readonly public Row row_;

        public Parameter(TableRef tabref, Row row) { tabref_ = tabref; row_ = row; }
        public override string ToString() => $"?{tabref_}.{row_}";
    }

    public class ExecContext {
        public List<Parameter> params_ = new List<Parameter>();

        public void Reset() { params_.Clear(); }
        public Value GetParam(TableRef tabref, int ordinal)
        {
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count == 1);
            return params_.Find(x => x.tabref_.Equals(tabref)).row_.values_[ordinal];
        }
        public void AddParam(TableRef tabref, Row row)
        {
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count <= 1);
            params_.Remove(params_.Find(x => x.tabref_.Equals(tabref)));
            params_.Add(new Parameter(tabref, row));
        }
    }

    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        readonly internal LogicNode logic_;

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
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract void Exec(ExecContext context, Func<Row, string> callback);

        internal Row ExecProject(ExecContext context, Row input) {
            Row r = new Row();
            logic_.output_.ForEach(x => r.values_.Add(x.Exec(context, input)));

            return r;
        }
    }

    public class PhysicGet : PhysicNode{
        int nrows_ = 3;
        public PhysicGet(LogicNode logic): base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicGet;
            Expr filter = logic.filter_;

            for (int i = 0; i < nrows_; i++)
            {
                Row r = new Row();
                r.values_.Add(i);
                r.values_.Add(i + 1);
                r.values_.Add(i + 2);
                r.values_.Add(i + 3);

                if (logic.tabref_.outerrefs_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter?.Exec(context, r) == 0)
                    continue;
                r = ExecProject(context, r);
                callback(r);
            }
        }
    }

    public class PhysicCrossJoin : PhysicNode {
        public PhysicCrossJoin(LogicCrossJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            children_[0].Exec(context, l =>
            {
                children_[1].Exec(context, r =>
                {
                    Row n = new Row(l, r);
                    n = ExecProject(context, n);
                    callback(n);
                    return null;
                });
                return null;
            });
        }
    }

    public class PhysicFromQuery : PhysicNode {
        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicFromQuery;
            children_[0].Exec(context, l => {
                if (logic.queryRef_.outerrefs_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(context, l);
                callback(r);
                return null;
            });
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicFilter).filter_;

            children_[0].Exec(context, l => {
                if (filter is null || filter.Exec(context, l) == 1)
                {
                    var r = ExecProject(context, l);
                    callback(r);
                }
                return null;
            });
        }
    }

    public class PhysicResult : PhysicNode {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Row r = new Row();
            logic_.output_.ForEach(
                            x => r.values_.Add(x.Exec(context, null)));
            callback(r);
        }
    }

    public class PhysicProfiling : PhysicNode
    {
        Int64 nrows_ = 0;

        public PhysicProfiling(PhysicNode l) : base(null) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            children_[0].Exec(context, l => {
                nrows_++;
                callback(l);
                return null;
            });
        }
    }

    public class PhysicCollect : PhysicNode
    {
        readonly public List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            context.Reset();
            children_[0].Exec(context, r =>
            {
                Row newr = new Row();
                List<Expr> output = children_[0].logic_.output_;
                for (int i = 0; i < output.Count; i++)
                {
                    if (output[i].isVisible_)
                        newr.values_.Add(r.values_[i]);
                }
                rows_.Add(newr);
                Console.WriteLine($"{newr}");
                return null;
            });
        }
    }
}

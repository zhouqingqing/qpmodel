using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Value = System.Object;

namespace adb
{
    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) => Console.WriteLine(msg);
    }

    public class Row
    {
        public readonly List<Value> values_ = new List<Value>();

        public Row() { }
        public Row(List<Value> values) => values_ = values;
        public Row(Row l, Row r)
        {
            values_.AddRange(l.values_);
            values_.AddRange(r.values_);
        }

        public int ColCount() => values_.Count;

        public override string ToString() => string.Join(",", values_);
    }

    public class Parameter
    {
        public readonly TableRef tabref_;   // from which table
        public readonly Row row_;   // what's the value of parameter

        public Parameter(TableRef tabref, Row row) { tabref_ = tabref; row_ = row; }
        public override string ToString() => $"?{tabref_}.{row_}";
    }

    public class ExecContext
    {
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
        internal readonly LogicNode logic_;
        internal PhysicProfiling profile_;

        protected PhysicNode(LogicNode logic) => logic_ = logic;

        public override string PrintOutput(int depth)
        {
            var r = "Output: " + string.Join(",", logic_.output_);
            logic_.output_.ForEach(x => r += ExprHelper.PrintExprWithSubqueryExpanded(x, depth));
            return r;
        }
        public override string PrintInlineDetails(int depth) => logic_.PrintInlineDetails(depth);
        public override string PrintMoreDetails(int depth) => logic_.PrintMoreDetails(depth);

        public virtual void Open() => children_.ForEach(x => x.Open());
        public virtual void Close() => children_.ForEach(x => x.Close());
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract void Exec(ExecContext context, Func<Row, string> callback);

        internal Row ExecProject(ExecContext context, Row input)
        {
            Row r = new Row();
            logic_.output_.ForEach(x => r.values_.Add(x.Exec(context, input)));

            return r;
        }
    }

    public class PhysicScanTable : PhysicNode
    {
        readonly int nrows_ = 3;
        public PhysicScanTable(LogicNode logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;

            for (var i = 0; i < nrows_; i++)
            {
                Row r = new Row();
                r.values_.Add(i);
                r.values_.Add(i + 1);
                r.values_.Add(i + 2);
                r.values_.Add(i + 3);

                if (logic.tabref_.outerrefs_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || (int)filter.Exec(context, r) == 1)
                {
                    r = ExecProject(context, r);
                    callback(r);
                }
            }
        }
    }

    public class PhysicScanFile : PhysicNode
    {
        public PhysicScanFile(LogicNode logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var filename = (logic_ as LogicScanFile).FileName();
            Utils.ReadCsvLine(filename, fields =>
            {
                Row r = new Row();
                Array.ForEach(fields, f => r.values_.Add(Int64.Parse(f)));
                callback(r);
            });
        }
    }

    public class PhysicNLJoin : PhysicNode
    {
        public PhysicNLJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var filter = logic.filter_;

            children_[0].Exec(context, l =>
            {
                children_[1].Exec(context, r =>
                {
                    Row n = new Row(l, r);
                    if (filter is null || (int)filter.Exec(context, n) == 1)
                    {
                        n = ExecProject(context, n);
                        callback(n);
                    }
                    return null;
                });
                return null;
            });
        }
    }

    public class PhysicHashAgg : PhysicNode
    {
        class KeyList
        {
            internal List<Value> keys_ = new List<Value>();

            static internal KeyList ComputeKeys(ExecContext context, LogicAgg agg, Row input)
            {
                var list = new KeyList();
                if (agg.keys_ != null)
                    agg.keys_.ForEach(x => list.keys_.Add(x.Exec(context, input)));
                return list;
            }

            public override string ToString() => string.Join(",", keys_);
            public override int GetHashCode()
            {
                int hashcode = 0;
                keys_.ForEach(x => hashcode ^= x.GetHashCode());
                return hashcode;
            }
            public override bool Equals(object obj)
            {
                var keyl = obj as KeyList;
                Debug.Assert(obj is KeyList);
                Debug.Assert(keyl.keys_.Count == keys_.Count);
                return keys_.SequenceEqual(keyl.keys_);
            }
        };
        public PhysicHashAgg(LogicAgg logic, PhysicNode l) : base(logic) => children_.Add(l);

        Row AggrCoreToRow(ExecContext context, Row input)
        {
            Row r = new Row();
            (logic_ as LogicAgg).aggrCore_.ForEach(x => r.values_.Add(x.Exec(context, input)));
            return r;
        }
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrCore_;
            var hm = new Dictionary<KeyList, Row>();

            // aggregation is working on aggCore targets
            children_[0].Exec(context, l =>
            {
                var keys = KeyList.ComputeKeys(context, logic, l);

                if (hm.TryGetValue(keys, out Row exist))
                {
                    aggrcore.ForEach(x =>
                    {
                        var xa = x as AggFunc;
                        var old = exist.values_[aggrcore.IndexOf(xa)];
                        xa.Accum(context, old, l);
                    });

                    hm[keys] = AggrCoreToRow(context, l);
                }
                else
                {
                    aggrcore.ForEach(x =>
                    {
                        var xa = x as AggFunc;
                        xa.Init(context, l);
                    });

                    hm.Add(keys, AggrCoreToRow(context, l));
                }
                return null;
            });

            // stitch keys+aggcore into final output
            foreach (var v in hm)
            {
                var w = new Row(new Row(v.Key.keys_), v.Value);
                var newr = ExecProject(context, w);
                callback(newr);
            }
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);

        // respect logic.orders_|descends_
        int compareRow(Row l, Row r)
        {
            var logic = logic_ as LogicOrder;
            var orders = logic.orders_;
            var descends = logic.descends_;
            return 0;
        }
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicOrder;
            var set = new List<Row>();
            children_[0].Exec(context, l =>
            {
                set.Add(l);
                return null;
            });
            set.Sort(compareRow);

            // output sorted set
            foreach (var v in set)
                callback(v);
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicFromQuery;
            children_[0].Exec(context, l =>
            {
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

            children_[0].Exec(context, l =>
            {
                if (filter is null || (int)filter.Exec(context, l) == 1)
                {
                    var r = ExecProject(context, l);
                    callback(r);
                }
                return null;
            });
        }
    }

    public class PhysicInsert : PhysicNode
    {
        public PhysicInsert(LogicInsert logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            children_[0].Exec(context, l =>
            {
                Console.WriteLine($"insert {l}");
                return null;
            });
        }
    }

    public class PhysicResult : PhysicNode
    {
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
        internal Int64 nrows_;

        public PhysicProfiling(PhysicNode l) : base(null)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(this.profile_ is null);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            children_[0].Exec(context, l =>
            {
                nrows_++;
                callback(l);
                return null;
            });
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            context.Reset();
            children_[0].Exec(context, r =>
            {
                Row newr = new Row();
                var child = (children_[0] is PhysicProfiling) ?
                        children_[0].children_[0] : children_[0];
                List<Expr> output = child.logic_.output_;
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

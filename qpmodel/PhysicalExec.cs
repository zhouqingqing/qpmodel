/*
 * The MIT License (MIT)
 *
 * Copyright (c) 2020 Futurewei Corp.
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;

using qpmodel.expr;
using qpmodel.logic;

using Value = System.Object;

namespace qpmodel.physic
{
    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) => Console.WriteLine($"ERROR[execution]: {msg}");
    }

    public class Row : IComparable
    {
        protected Value[] values_ = null;

        public Row(List<Value> values) => values_ = values.ToArray();

        // used by outer joins
        public Row(int length)
        {
            Debug.Assert(length >= 0);
            values_ = (Value[])Array.CreateInstance(typeof(Value), length);
            Array.ForEach(values_, x => Debug.Assert(x is null));
        }

        public Row(Row l, Row r)
        {
            // for semi/anti-semi joins, one of them may be null
            Debug.Assert(l != null || r != null);
            int size = l?.ColCount() ?? 0;
            size += r?.ColCount() ?? 0;
            values_ = (Value[])Array.CreateInstance(typeof(Value), size);

            int start = 0;
            if (l != null)
            {
                for (int i = 0; i < l.ColCount(); i++)
                    values_[start + i] = l[i];
                start += l.ColCount();
            }
            if (r != null)
            {
                for (int i = 0; i < r.ColCount(); i++)
                    values_[start + i] = r[i];
                start += r.ColCount();
            }
            Debug.Assert(start == size);
        }

        public Value this[int i]
        {
            get { return values_[i]; }
            set { values_[i] = value; }
        }
        public override int GetHashCode()
        {
            int hashcode = 0;
            Array.ForEach(values_, x => hashcode ^= x?.GetHashCode() ?? 0);
            return hashcode;
        }
        public override bool Equals(object obj)
        {
            var keyl = obj as Row;
            Debug.Assert(obj is Row);
            Debug.Assert(keyl.ColCount() == ColCount());
            return values_.SequenceEqual(keyl.values_);
        }

        public int CompareTo(object obj)
        {
            Debug.Assert(!(obj is null));
            var rrow = obj as Row;
            for (int i = 0; i < ColCount(); i++)
            {
                dynamic l = this[i];
                dynamic r = rrow[i];
                var c = l.CompareTo(r);
                if (c < 0)
                    return -1;
                else if (c == 0)
                    continue;
                else if (c > 0)
                    return 1;
            }
            return 0;
        }

        public int CompareTo(object obj, List<bool> descends)
        {
            Debug.Assert(!(obj is null));
            var rrow = obj as Row;

            Debug.Assert(descends.Count == ColCount());
            for (int i = 0; i < ColCount(); i++)
            {
                dynamic l = this[i];
                dynamic r = rrow[i];
                bool flip = descends[i];
                var c = l.CompareTo(r);
                if (c < 0)
                    return flip?+1:-1;
                else if (c == 0)
                    continue;
                else if (c > 0)
                    return flip?-1:+1;
            }
            return 0;
        }

        public int ColCount() => values_.Length;
        public override string ToString() => string.Join(",", values_.ToList());
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
        public QueryOption option_;

        public bool stop_ = false;

        // subquery parameter passing
        public List<Parameter> params_ = new List<Parameter>();

        // cte resultset passing
        public Dictionary<string, List<Row>> results_ = new Dictionary<string, List<Row>>();

        // emulated distributed environment
        public int machineId_ = 0;

        public ExecContext(QueryOption option) { option_ = option; }

        public void Reset() { params_.Clear(); results_.Clear(); }
        public Value GetParam(TableRef tabref, int ordinal)
        {
        	Debug.Assert (!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count == 1);
            return params_.Find(x => x.tabref_.Equals(tabref)).row_[ordinal];
        }
        public void AddParam(TableRef tabref, Row row)
        {
        	Debug.Assert (!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count <= 1);
            params_.Remove(params_.Find(x => x.tabref_.Equals(tabref)));
            params_.Add(new Parameter(tabref, row));
        }

        public void RegisterCteProducer(string ctename, List<Row> heap)
        {
            results_.Add(ctename, heap);
        }
        public List<Row> TryGetCteProducer(string ctename)
        {
            if (results_.ContainsKey(ctename))
                return results_[ctename];
            return null;
        }
    }

    // Emulate a remote exchange channel
    public class ExchangeChannel
    {
        BlockingCollection<Row> dataBuffer_ = new BlockingCollection<Row>(100);
        int cntDone_ = 0;
        int dop_;

        public ExchangeChannel(int dop){
            cntDone_ = 0;
            dop_ = dop;
        }

        public void Send(Row r)
        {
            dataBuffer_.Add(r);
        }

        public void MarkSendDone(int workerid)
        {
            var newcnt = Interlocked.Increment(ref cntDone_);
            Debug.Assert(newcnt <= dop_);
            if (newcnt == dop_)
                dataBuffer_.CompleteAdding();
        }

        public Row Recv()
        {
            Row r = null;
            if (!dataBuffer_.IsCompleted)
            {
                try
                {
                    r = dataBuffer_.Take();
                    Debug.Assert(r != null);
                }
                catch (InvalidOperationException)
                {
                    // no row in the buffer
                }
            }
            return r;
        }
    }

    // A worker object consists data structures used by a worker thread
    //
    public class WorkerObject
    {
        internal int workerId_;
        internal PhysicNode root_;
        internal QueryOption queryOpt_;

        public WorkerObject(int workerId, PhysicNode root, QueryOption queryOpt)
        {
            workerId_ = workerId;
            root_ = root;
            queryOpt_ = queryOpt;
        }

        public void EntryPoint()
        {
            var context = new ExecContext(queryOpt_);
            context.machineId_ = workerId_;

            // TBD: open subqueries

            var plan = root_;
            (plan as PhysicGather).asConsumer_.Value = false;
            (plan as PhysicGather).workerId_.Value = workerId_;

            var code = plan.Open(context);
            code = plan.Exec(null);
            code = plan.Close();
        }
    }

}

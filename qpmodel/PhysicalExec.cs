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
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;

using qpmodel.codegen;
using qpmodel.expr;
using qpmodel.logic;
using qpmodel.optimizer;
using qpmodel.utils;

using Value = System.Object;

namespace qpmodel.physic
{
    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) : base(msg) => Console.WriteLine($"ERROR[execution]: {msg}");
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
                if (l is null)
                {
                    // null first
                    return flip ? +1 : -1;
                }
                else
                {
                    var c = l.CompareTo(r);
                    if (c < 0)
                        return flip ? +1 : -1;
                    else if (c == 0)
                        continue;
                    else if (c > 0)
                        return flip ? -1 : +1;
                }
            }
            return 0;
        }

        public bool ColsHasNull()
        {
            for (int i = 0; i < ColCount(); i++)
            {
                if (this[i] is null)
                    return true;
            }
            return false;
        }
        public int ColCount() => values_.Length;
        public override string ToString()
        {
            return string.Join(",", values_.Select(x =>
            {
                if (x is double dv)
                    return dv.ToString("0.####");
                else if (x is double df)
                    return df.ToString("0.####");
                else
                    return x;
            }));
        }
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

        // holding generated code
        public string code_;

        public ExecContext(QueryOption option) { option_ = option; }

        public void Reset() { params_.Clear(); results_.Clear(); }
        public Value GetParam(TableRef tabref, int ordinal)
        {
            Debug.Assert(!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count == 1);
            return params_.Find(x => x.tabref_.Equals(tabref)).row_[ordinal];
        }
        public void AddParam(TableRef tabref, Row row)
        {
            Debug.Assert(!stop_);
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count <= 1);
            params_.Remove(params_.Find(x => x.tabref_.Equals(tabref)));
            params_.Add(new Parameter(tabref, row));
        }

        public void RegisterCteProducer(string ctename, List<Row> heap)
        {
            results_.Add(ctename, heap);
        }
        public List<Row> GetCteProducer(string ctename)
        {
            Debug.Assert(results_.ContainsKey(ctename));
            return results_[ctename];
        }
    }

    // Final output node
    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);

        public override void Open(ExecContext context)
        {
            context.Reset();
            base.Open(context);
        }

        public override void Close()
        {
            // @context_ is null after @base.Close()
            // is issued.
            // Keep the reference of @context_ in this
            // stack to append '}}' to @context_.code_
            // after @base.Close() is issued.
            var ctx = context_;
            base.Close();

            string s = "}}";
            ctx.code_ += s;
        }

        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;
            var child = (child_() is PhysicProfiling) ?
                    child_().child_() : child_();
            var output = child.logic_.output_;
            var ncolumns = output.Count(x => x.isVisible_);

            if (context.option_.optimize_.use_codegen_)
            {
                string header = $@"
                    /*
                    --- plan ---
                    {this.Explain()}
                    */
                ";
                CodeWriter.Reset(header);
            }

            child_().Exec(r =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"
                        Row newr = new Row({ncolumns});";
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].isVisible_)
                        {
                            context.code_ += $"newr[{i}] = r{child_()._}[{i}];";
                        }
                    }
                    context.code_ += $"{_physic_}.rows_.Add(newr);";
                    context.code_ += $"Console.WriteLine(newr);";
                }
                else
                {
                    Row newr = new Row(ncolumns);
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].isVisible_)
                            newr[i] = r[i];
                    }
                    rows_.Add(newr);
                    if (context.option_.explain_.mode_ >= ExplainMode.full)
                        Console.WriteLine($"{newr}");
                }
            });
        }
    }

    // Profiling support
    public class PhysicProfiling : PhysicNode
    {
        public Int64 nrows_ = 0;
        public Int64 nloops_ = 0;
        // for distributed execution only
        public PhysicProfiling aggregatedProfile_ = null;

        public override string ToString() => $"${child_()}";

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        // Use atomic variable instead of mutex to ensure that modifications to @nrows_ 
        // and @nloops_ are atomic. This is the simplest lock-free idiom.
        //
        // The advantage is that there is no overhead for thread switching even in distributed 
        // query. The disadvantage is that non-distributed query also has atomic variable
        // synchronization overhead. Since atomic variable does not cause thread switching, overhead
        // is almost negligible.
        //
        // We can also distinguish the current query mode by @context_. If @context_ is
        // DistributedContext, increase @nrows_ and @nloops_. Otherwise increase @nrows_ and
        // @nloops_ automatically. In this situation, duplicate codes are generated.
        //
        public override void Exec(Action<Row> callback)
        {
            ExecContext context = context_;

            if (context.option_.optimize_.use_codegen_)
            {
                context.code_ += $@"
                System.Threading.Interlocked.Increment(ref {_physic_}.nloops_);
                if ({_physic_}.aggregatedProfile_ != null)
                    System.Threading.Interlocked.Increment(ref {_physic_}.aggregatedProfile_.nloops_);";
            }
            else
            {
                Interlocked.Increment(ref nloops_);
                if (aggregatedProfile_ != null)
                    Interlocked.Increment(ref aggregatedProfile_.nloops_);
            }

            child_().Exec(l =>
            {
                if (context.option_.optimize_.use_codegen_)
                {
                    context.code_ += $@"
                    System.Threading.Interlocked.Increment(ref {_physic_}.nrows_);
                    if ({_physic_}.aggregatedProfile_ != null)
                        System.Threading.Interlocked.Increment(ref {_physic_}.aggregatedProfile_.nrows_);
                    var r{_} = r{child_()._};";
                    callback(null);
                }
                else
                {
                    Interlocked.Increment(ref nrows_);
                    if (aggregatedProfile_ != null)
                        Interlocked.Increment(ref aggregatedProfile_.nrows_);
                    callback(l);
                }
            });
        }

        // clone profiling shares aggregated profile
        public override PhysicNode Clone()
        {
            var n = (PhysicProfiling)(base.Clone());
            n.aggregatedProfile_ = aggregatedProfile_ is null ? this : aggregatedProfile_;
            return n;
        }

        protected override double EstimateCost() => 0;
    }

    // PhysicMemoRef wrap a LogicMemoRef as a physical node (so LogicMemoRef can be 
    // used in physical tree). Actually we only need LogicMemoRef's memo group.
    //
    public class PhysicMemoRef : PhysicNode
    {
        public PhysicMemoRef(LogicNode logic) : base(logic) { Debug.Assert(logic is LogicMemoRef); }
        public override string ToString() => Logic().ToString();

        public override void Exec(Action<Row> callback) => throw new InvalidProgramException("not executable");
        public override int GetHashCode() => Group().memoid_;
        public override bool Equals(object obj)
        {
            if (obj is PhysicMemoRef lo)
                return Logic().MemoLogicSign() == (lo.logic_ as LogicMemoRef).MemoLogicSign();
            return false;
        }

        public LogicMemoRef Logic() => logic_ as LogicMemoRef;
        internal CMemoGroup Group() => Logic().group_;
        public override string ExplainMoreDetails(int depth, ExplainOption option)
        {
            // we want to see what's underneath
            return $"{{{Logic().ExplainMoreDetails(depth + 1, option)}}}";
        }
    }

    // Distributed context is an extension of regular execution context
    // for distributed queries. Only remote exchange operators and scan
    // shall be aware of it.
    //
    public class DistributedContext : ExecContext
    {
        public MachinePool machines_ { get; set; } = new MachinePool();

        // which machine this query fragment is executed
        public int machineId_ { get; set; } = -1;

        public DistributedContext(QueryOption option) : base(option) { }
    }

    // Emulate a remote exchange channel
    //   So we say send/recv
    //
    public class ExchangeChannel
    {
        readonly BlockingCollection<Row> dataBuffer_ = new BlockingCollection<Row>();
        int cntDoneProducers_ = 0;
        readonly int cntProducers_;

        public ExchangeChannel(int dop)
        {
            cntDoneProducers_ = 0;
            cntProducers_ = dop;
        }

        public void Send(Row r)
        {
            dataBuffer_.Add(r);
        }

        public void MarkSendDone(int workerid)
        {
            var newcnt = Interlocked.Increment(ref cntDoneProducers_);
            Debug.Assert(newcnt <= cntProducers_);
            if (newcnt == cntProducers_)
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

    public class MachinePool
    {
        public class ChannelId
        {
            internal string planId_;
            internal int machineId_;

            public ChannelId(string planId, int machineId)
            {
                planId_ = planId;
                machineId_ = machineId;
            }

            public override int GetHashCode() => ToString().GetHashCode();
            public override bool Equals(object obj)
            {
                if (obj is ChannelId co)
                    return co.planId_.Equals(planId_) && co.machineId_.Equals(machineId_);
                return false;
            }
            public override string ToString() => $"{planId_}.{machineId_}";
        }

        internal ConcurrentDictionary<ChannelId, ExchangeChannel> channels_ = new ConcurrentDictionary<ChannelId, ExchangeChannel>();
        internal ConcurrentQueue<Thread> threads_ = new ConcurrentQueue<Thread>();
        public MachinePool() { }

        public void RegisterChannel(string planId, int machineId, ExchangeChannel channel)
        {
            bool result = channels_.TryAdd(new ChannelId(planId, machineId), channel);
            if (result != true)
                throw new InvalidProgramException("no conflicts shall expected");
        }

        public ExchangeChannel WaitForChannelReady(string plandId, int machineId)
        {
        restart:
            var channelId = new MachinePool.ChannelId(plandId, machineId);
            if (channels_.TryGetValue(channelId, out ExchangeChannel channel))
                return channel;
            else
            {
                // wait for its ready - a manual event is better
                Thread.Sleep(100);
                goto restart;
            }
        }

        public void RegisterThread(Thread thread)
        {
            threads_.Enqueue(thread);
        }

        public void WaitForAllThreads()
        {
            while (!threads_.IsEmpty)
            {
                Thread thread;
                bool succ = threads_.TryDequeue(out thread);
                if (succ)
                    thread.Join();
            }
        }
    }

    // A worker object consists data structures used by a worker thread
    //
    public class WorkerObject
    {
        // machineId_ + planId_ identifies a worker object
        internal int machineId_;
        internal string planId_;
        internal PhysicNode root_;
        internal MachinePool machines_;
        internal QueryOption queryOpt_;

        // debug info
        internal string parentThread_;

        public WorkerObject(string parentThread, MachinePool machines, int machineId, string planId, PhysicNode root, QueryOption queryOpt)
        {
            parentThread_ = parentThread;
            machines_ = machines;
            machineId_ = machineId;
            planId_ = planId;
            root_ = root;
            queryOpt_ = queryOpt;
        }

        public void EntryPoint()
        {
            var context = new DistributedContext(queryOpt_)
            {
                machineId_ = machineId_,
                machines_ = machines_
            };

            // TBD: open subqueries

            var plan = root_ as PhysicRemoteExchange;
            plan.asConsumer_ = false;

            plan.Open(context);
            plan.Exec(null);
            plan.Close();
        }
    }

    public abstract class PhysicRemoteExchange : PhysicNode
    {
        internal bool asConsumer_ { get; set; } = true;
        // consumer only: channel it recieve from
        internal ExchangeChannel channel_ { get; set; }
        // producer only: channels it shall send data to, so the number equals nubmer of machines
        internal List<ExchangeChannel> upChannels_ { get; set; }
        public PhysicRemoteExchange(LogicRemoteExchange logic, PhysicNode l) : base(logic)
        {
            Debug.Assert(asConsumer_); children_.Add(l);
        }

        public override void Close()
        {
            var code = "";

            context_.code_ += code;
            if (!asConsumer_)
            {
                base.Close();
            }
        }

        // this function emulates a full serialization of a given physic node for network
        // transfer purpose, so it shall include every information we'd like the receiver
        // have, including the physic node itself and its logic node and could be more.
        //
        public PhysicNode EmulateSerialization(PhysicNode node)
        {
            var newnode = node.Clone();
            return newnode;
        }
        public virtual string OpenConsumer(ExecContext context) => null;
        public virtual string OpenProducer(ExecContext context) => null;
        protected string createConsumerStartThread(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var planId = _;

            Debug.Assert(context.machineId_ >= 0);
            var machineId = context.machineId_;
            var wo = new WorkerObject(Thread.CurrentThread.Name,
                                    context.machines_,
                                    machineId,
                                    planId,
                                    EmulateSerialization(this),
                                    context.option_);
            var thread = new Thread(new ThreadStart(wo.EntryPoint))
            {
                Name = $"Redis_{planId}@{machineId}"
            };
            context.machines_.RegisterThread(thread);
            thread.Start();

            upChannels_ = null;
            channel_ = new ExchangeChannel(dop);
            context.machines_.RegisterChannel(planId, machineId, channel_);
            return null;
        }
        protected string createProducerAndChannel(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var planId = _;

            // establish all up channels
            Debug.Assert(upChannels_ is null);
            upChannels_ = new List<ExchangeChannel>();
            for (int i = 0; i < dop; i++)
            {
                var channel = context.machines_.WaitForChannelReady(planId, i);
                upChannels_.Add(channel);
            }

            channel_ = null;
            return null;
        }
        public override void Open(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            var code = asConsumer_ ? OpenConsumer(context) : OpenProducer(context);

            // only producer inherits the bottom half of the plan
            if (!asConsumer_)
                base.Open(context);
            else
            {
                Debug.Assert(context_ is null);
                context_ = context;
            }
        }

        public virtual void ExecConsumer(Action<Row> callback) { }
        public virtual void ExecProducer(Action<Row> callback) { }
        protected void execBroadCast(Action<Row> callback)
        {
            Row r;
            while ((r = channel_.Recv()) != null)
                callback(r);
        }
        protected virtual List<int> machineList(Row r)
        {
            // default is send to all machines (broadcast)
            var outlist = new List<int>();

            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;

            outlist.AddRange(Enumerable.Range(0, dop));
            return outlist;
        }
        protected void sendProducerResult(Action<Row> callback)
        {
            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;

            child_().Exec(r =>
            {
                if (!context.option_.optimize_.use_codegen_)
                {
                    var machines = machineList(r);
                    foreach (var idx in machines)
                    {
                        upChannels_[idx].Send(r);
                    }
                }
            });

            for (int i = 0; i < dop; i++)
                upChannels_[i].MarkSendDone(context.machineId_);
        }

        public override void Exec(Action<Row> callback)
        {
            if (asConsumer_)
                ExecConsumer(callback);
            else
                ExecProducer(callback);
        }

        protected override double EstimateCost()
        {
            return logic_.Card() * 0.1;
        }
    }

    public class PhysicGather : PhysicRemoteExchange
    {
        public PhysicGather(LogicGather logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PGATHER({child_()}: {Cost()})";

        // cache serialized output for looped case
        private List<Row> cache_ { get; set; }

        public override string OpenConsumer(ExecContext econtext)
        {
            var context = econtext as DistributedContext;
            var logic = logic_ as LogicGather;
            List<int> targets = logic.producerIds_;
            int dop = targets.Count;

            // create producer threads, establish connections and set up 
            // communication channel etc. It uses threads to emulate execution
            // among a pool of machines.
            //
            channel_ = new ExchangeChannel(dop);

            Debug.Assert(context.machines_ != null);
            List<Thread> workers = new List<Thread>();
            foreach (var machineId in targets)
            {
                var planId = _;
                var wo = new WorkerObject(Thread.CurrentThread.Name,
                                        context.machines_,
                                        machineId,
                                        planId,
                                        EmulateSerialization(this),
                                        context.option_);
                var thread = new Thread(new ThreadStart(wo.EntryPoint))
                {
                    Name = $"Gather_{planId}@{machineId}"
                };
                workers.Add(thread);
                context.machines_.RegisterThread(thread);
            }
            workers.ForEach(x => x.Start());

            return null;
        }

        public override string OpenProducer(ExecContext context)
        {
            Debug.Assert(channel_ != null);
            return null;
        }

        public override void ExecConsumer(Action<Row> callback)
        {
            if (cache_ != null)
            {
                foreach (var row in cache_)
                {
                    var r = ExecProject(row);
                    callback(r);
                }
            }
            else
            {
                cache_ = new List<Row>();
                Row r;
                while ((r = channel_.Recv()) != null)
                {
                    r = ExecProject(r);
                    callback(r);
                    cache_.Add(r);
                }
            }
        }

        public override void ExecProducer(Action<Row> callback)
        {
            var context = context_ as DistributedContext;

            child_().Exec(r =>
            {
                if (!context.option_.optimize_.use_codegen_)
                {
                    channel_.Send(r);
                }
            });

            channel_.MarkSendDone(context.machineId_);
        }
        protected override double EstimateCost()
        {
            // penalize gather to discourage serialization at bottom
            return child_().Card() * 10.0;
        }
    }

    public class PhysicBroadcast : PhysicRemoteExchange
    {
        public PhysicBroadcast(LogicBroadcast logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PBROADCAST({child_()}: {Cost()})";

        public override string OpenConsumer(ExecContext context)
            => createConsumerStartThread(context);
        public override string OpenProducer(ExecContext context)
            => createProducerAndChannel(context);

        public override void ExecConsumer(Action<Row> callback)
            => execBroadCast(callback);
        public override void ExecProducer(Action<Row> callback)
            => sendProducerResult(callback);

        protected override double EstimateCost()
        {
            return child_().Card() * 2.0;
        }
    }

    public class PhysicRedistribute : PhysicRemoteExchange
    {
        public PhysicRedistribute(LogicRedistribute logic, PhysicNode l) : base(logic, l) { }
        public override string ToString() => $"PREDISTRIBUTE({child_()}: {Cost()})";

        public override string OpenConsumer(ExecContext context)
            => createConsumerStartThread(context);
        public override string OpenProducer(ExecContext context)
            => createProducerAndChannel(context);

        public override void ExecConsumer(Action<Row> callback)
            => execBroadCast(callback);
        public override void ExecProducer(Action<Row> callback)
            => sendProducerResult(callback);
        protected override List<int> machineList(Row r)
        {
            var context = context_ as DistributedContext;
            int dop = context.option_.optimize_.query_dop_;
            var distributeby = (logic_ as LogicRedistribute).distributeby_;

            var outlist = new List<int>();
            var key = KeyList.ComputeKeys(context_, distributeby, r);
            var sendtoMachine = Utils.mod(key.GetHashCode(), dop);
            outlist.Add(sendtoMachine);

#if debug
            var tid = Thread.CurrentThread.ManagedThreadId;
            Console.WriteLine($"{Thread.CurrentThread.Name} by {tid} => {r} => {sendtoMachine}");
#endif
            return outlist;
        }

        protected override double EstimateCost()
        {
            return child_().Card() * 2.0;
        }
    }
}

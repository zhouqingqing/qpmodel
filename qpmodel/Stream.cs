using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using qpmodel.expr;
using qpmodel.physic;
using qpmodel.logic;

namespace qpmodel.stream
{
    // tumble window is also known as fixed window: it cuts stream
    // into non-overlapped fixed window size @interval based on column
    // @ts
    // args: ts, interval
    //
    public class TumbleWindow : FuncExpr
    {
        public TumbleWindow(List<Expr> args) : base("tumble", args)
        {
            argcnt_ = 2;
            type_ = new IntType();
        }

        public override object Exec(ExecContext context, Row input)
        {
            var startts = new DateTime(1980, 1, 1);
            var ts = (DateTime)args_()[0].Exec(context, input);
            var interval = (TimeSpan)args_()[1].Exec(context, input);

            // start from startts date, cut time into interval sized chunks
            // and return the chunk ts falling into
            //
            var span = (ts - startts).TotalSeconds;
            var window = (long)(span / interval.TotalSeconds);
            return window;
        }
    }

    public class TumbleStart : AggFunc
    {
        DateTime startBound_;
        public TumbleStart(List<Expr> args) : base("tumble_start", args)
        {
            argcnt_ = 2;
            type_ = new DateTimeType();
        }

        DateTime computeStartBound(ExecContext context, Row input)
        {
            var startts = new DateTime(1980, 1, 1);
            var ts = (DateTime)args_()[0].Exec(context, input);
            var interval = (TimeSpan)args_()[1].Exec(context, input);

            // round to the start of the window
            var span = (ts - startts).TotalSeconds;
            var window = (long)(span / interval.TotalSeconds);
            var startBound = startts;
            startBound = startBound.AddSeconds(window * interval.TotalSeconds);
            return startBound;
        }

        public override object Init(ExecContext context, Row input)
        {
            startBound_ = computeStartBound(context, input);
            return startBound_;
        }

        public override object Accum(ExecContext context, object old, Row input)
        {
            // any new coming rows must fall into the same fixed window
            Debug.Assert(startBound_.Equals(computeStartBound(context, input)));
            return startBound_;
        }
    }

    public class TumbleEnd : AggFunc
    {
        DateTime endBound_;
        public TumbleEnd(List<Expr> args) : base("tumble_end", args)
        {
            argcnt_ = 2;
            type_ = new DateTimeType();
        }

        DateTime computeEndBound(ExecContext context, Row input)
        {
            var startts = new DateTime(1980, 1, 1);
            var ts = (DateTime)args_()[0].Exec(context, input);
            var interval = (TimeSpan)args_()[1].Exec(context, input);

            // round to the start of the window
            var span = (ts - startts).TotalSeconds;
            var window = (long)(span / interval.TotalSeconds);
            var end = startts;
            end = end.AddSeconds((window + 1) * interval.TotalSeconds);
            return end;
        }

        public override object Init(ExecContext context, Row input)
        {
            endBound_ = computeEndBound(context, input);
            return endBound_;
        }

        public override object Accum(ExecContext context, object old, Row input)
        {
            // any new coming rows must fall into the same fixed window
            Debug.Assert(endBound_.Equals(computeEndBound(context, input)));
            return endBound_;
        }
    }

    // hop window is also known as sliding window: it cuts stream
    // into fixed window size @interval, and each window moves @slide.
    // If @slide=@interval, it degenerates to tumble window; if @slie 
    // is 1/4 of @interval, then each row belongs to 4 sliding windows.
    // So it is a set returning function.
    // args: ts, slide, interval
    //
    public class HopWindow : FuncExpr
    {
        public HopWindow(List<Expr> args) : base("hop", args)
        {
            isSRF_ = true;
            argcnt_ = 3;
            type_ = new IntType();
        }

        public override object Exec(ExecContext context, Row input)
        {
            var startts = new DateTime(1980, 1, 1);
            var ts = (DateTime)args_()[0].Exec(context, input);
            var slide = (TimeSpan)args_()[1].Exec(context, input);
            var interval = (TimeSpan)args_()[2].Exec(context, input);

            // start from startts date, cut time into interval sized chunks
            // and return the chunk + slide falling into
            //
            var span = (ts - startts).TotalSeconds;
            var window = (long)(span / interval.TotalSeconds);
            var nslides = (int)(interval.TotalSeconds / slide.TotalSeconds);
            var results = new List<long>(nslides);
            for (int i = 0; i < nslides; i++)
                results.Add(window + i);
            return results;
        }
    }

    // session window does not have a fixed duration but the window is
    // defined by @inactive. That is, if @inactive = 10 seconds, then if
    // a new row is observed within 10 seconds, it belongs to current window
    // otherwise, start a new window to contain this new row.  
    // args: time_column, inactive
    //
    public class SessionWindow : FuncExpr
    {
        public SessionWindow(List<Expr> args) : base("session", args)
        {
            argcnt_ = 3;
            type_ = new IntType();
        }
    }

    public class LogicScanStream : LogicScanTable
    {
        public LogicScanStream(BaseTableRef tab) : base(tab) { }
    }

    public class PhysicScanStream : PhysicScanTable
    {
        public PhysicScanStream(LogicNode logic) : base(logic) { }
        public override string ToString() => $"PStream({(logic_ as LogicScanStream).tabref_}: {Cost()})";

        List<Row> getSourceIterators(int distId)
        {
            var logic = logic_ as LogicScanTable;
            return (logic.tabref_).Table().distributions_[distId].heap_;
        }

        public override void Exec(Action<Row> callback)
        {
            var context = context_;
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var distId = (logic.tabref_).IsDistributed() ? (context as DistributedContext).machineId_ : 0;
            var source = getSourceIterators(distId);

            if (!context.option_.optimize_.use_codegen_)
            {
            }
        }

        protected override double EstimateCost()
        {
            var logic = (logic_) as LogicScanTable;
            var tablerows = Math.Max(1,
                        Catalog.sysstat_.EstCardinality(logic.tabref_.relname_));
            return tablerows * 1.0;
        }
    }
}

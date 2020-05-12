using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using qpmodel.expr;
using qpmodel.physic;

namespace qpmodel.stream
{
    // tumble window is also known as fixed window: it cuts stream
    // into non-overlapped fixed window size @interval based on column
    // @time_column
    // args: time_column, interval
    //
    public class Tumble : FuncExpr
    {
        public Tumble(List<Expr> args) : base("tumble", args)
        {
            argcnt_ = 2;
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
            var window = (long) (span / interval.TotalSeconds);
            return window;
        }
    }

    // hop window is also known as sliding window: it cuts stream
    // into fixed window size @duration, and each window moves @slide.
    // If @slide=@duration, it degenerates to tumble window; if @slie 
    // is 1/4 of @duration, then each row belongs to 4 sliding windows.
    // args: time_column, slide, duration
    //
    public class Hop : FuncExpr
    {
        public Hop(List<Expr> args) : base("hop", args)
        {
            argcnt_ = 3;
        }
    }

    // session window does not have a fixed duration but the window is
    // defined by @inactive. That is, if @inactive = 10 seconds, then if
    // a new row is observed within 10 seconds, it belongs to current window
    // otherwise, start a new window to contain this new row.  
    // args: time_column, inactive
    //
    public class Session : FuncExpr
    {
        public Session(List<Expr> args) : base("session", args)
        {
            argcnt_ = 3;
        }
    }
}

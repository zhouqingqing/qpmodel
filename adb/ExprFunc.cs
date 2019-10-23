using System;
using System.Collections.Generic;
using System.Linq;
using Value = System.Int64;

namespace adb
{
    public class FuncExpr : Expr
    {
        public string funcName_;
        public List<Expr> args_;
        public static int argcnt_;

        public FuncExpr(string funcName, List<Expr> args)
        {
            funcName_ = funcName;
            args_ = args;
        }

        public override void Bind(BindContext context)
        {
            args_.ForEach(x => {
                x.Bind(context);
                tableRefs_.AddRange(x.tableRefs_);
            });
            tableRefs_ = tableRefs_.Distinct().ToList();
        }

        // sum(min(x)) => x
        public List<Expr> GetNonFuncExprList()
        {
            List<Expr> r = new List<Expr>();
            args_.ForEach(x => {
                x.VisitEachExpr(y => {
                    if (y is FuncExpr yf)
                        r.AddRange(yf.GetNonFuncExprList());
                    else
                        r.Add(y);
                });
            });

            return r;
        }
        static public FuncExpr BuildFuncExpr(string funcName, List<Expr> args)
        {
            FuncExpr r = null;
            var func = funcName.Trim().ToLower();

            switch (func)
            {
                case "sum": r = new AggSum(args[0]); break;
                case "min": r = new AggMin(args[0]); break;
                case "max": r = new AggMax(args[0]); break;
                case "count": r = new AggCount(args[0]);break;
                case "avg": r = new AggAvg(args[0]);break;
                default:
                    r = new FuncExpr(funcName, args);
                    break;
            }

            // verify arguments count
            Utils.Checks(args.Count == FuncExpr.argcnt_, $"{FuncExpr.argcnt_} argument is expected");
            return r;
        }

        public override Expr Clone()
        {
            var n = (FuncExpr)base.Clone();
            var argclone = new List<Expr>();
            args_.ForEach(x => argclone.Add(x.Clone()));
            args_ = argclone;
            return n;
        }
        public override int GetHashCode()
        {
            int hashcode = 0;
            args_.ForEach(x => hashcode ^= x.GetHashCode());
            return funcName_.GetHashCode() ^ hashcode;
        }
        public override bool Equals(object obj)
        {
            if (obj is FuncExpr of)
            {
                return funcName_.Equals(of.funcName_) && args_.SequenceEqual(of.args_);
            }
            return false;
        }
        public override string ToString() => $"{funcName_}({string.Join(",", args_)})";
    }

    public abstract class AggFunc : FuncExpr
    {
        public AggFunc(string func, List<Expr> args) : base(func, args) { argcnt_ = 1;}

        public override Value Exec(ExecContext context, Row input)
        {
            return 0;
        }
        public virtual void Init(ExecContext context, Row input) { }
        public virtual void Accum(ExecContext context, Value old, Row input) { }
    }

    public class AggSum : AggFunc
    {
        // Exec info
        internal Value sum_;
        public AggSum(Expr arg) : base("sum", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input) => sum_ = args_[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input) => sum_ = old + args_[0].Exec(context, input);
        public override Value Exec(ExecContext context, Row input) => sum_;
    }

    public class AggCount : AggFunc
    {
        // Exec info
        internal Value count_;
        public AggCount(Expr arg) : base("count", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input) => count_ = 1;
        public override void Accum(ExecContext context, Value old, Row input) => count_ += 1;
        public override Value Exec(ExecContext context, Row input) => count_;
    }

    public class AggMin : AggFunc
    {
        // Exec info
        Value min_;
        public AggMin(Expr arg) : base("min", new List<Expr> { arg }) { }
        public override void Init(ExecContext context, Row input) => min_ = args_[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_[0].Exec(context, input);
            min_ = old > arg ? arg : old;
        }
        public override Value Exec(ExecContext context, Row input) => min_;
    }

    public class AggMax : AggFunc
    {
        // Exec info
        Value max_;
        public AggMax(Expr arg) : base("max", new List<Expr> { arg }) { }
        public override void Init(ExecContext context, Row input) => max_ = args_[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_[0].Exec(context, input);
            max_ = old > arg ? old : arg;
        }
        public override Value Exec(ExecContext context, Row input) => max_;
    }

    public class AggAvg : AggFunc
    {
        // Exec info
        internal Value sum_;
        internal Value count_;

        public AggAvg(Expr arg) : base("avg", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input)
        {
            sum_ = args_[0].Exec(context, input);
            count_ = 1;
        }
        public override void Accum(ExecContext context, Value old, Row input)
        {
            sum_ = old + args_[0].Exec(context, input);
            count_ += 1;
        }
        public override Value Exec(ExecContext context, Row input) => sum_/count_;
    }
}

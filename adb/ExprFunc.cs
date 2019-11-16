using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Value = System.Object;

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
            args_.ForEach(x =>
            {
                x.Bind(context);
                tableRefs_.AddRange(x.tableRefs_);
            });
            tableRefs_ = tableRefs_.Distinct().ToList();
            bounded_ = true;
            // type is handled by each function
        }

        // sum(min(x)) => x
        public List<Expr> GetNonFuncExprList()
        {
            List<Expr> r = new List<Expr>();
            args_.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
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
                case "avg": r = new AggAvg(args[0]); break;
                case "count":
                    if (args.Count == 0)
                        r = new AggCountStar(null);
                    else
                        r = new AggCount(args[0]);
                    break;
                case "substring": r = new SubstringFunc(args); break;
                case "year": r = new YearFunc(args); break;
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
            args_ = ExprHelper.CloneList(args_);
            Debug.Assert(Equals(n));
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
                return funcName_.Equals(of.funcName_) && args_.SequenceEqual(of.args_);
            else if (obj is ExprRef oe)
                return Equals(oe.expr_);
            return false;
        }
        public override string ToString() => $"{funcName_}({string.Join(",", args_)})";
    }

    public class SubstringFunc : FuncExpr { 
        public SubstringFunc(List<Expr> args): base("substring", args){ 
            argcnt_ = 3; 
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_[0].type_;
            Debug.Assert(type_ is CharType || type_ is VarCharType);
        }
        public override Value Exec(ExecContext context, Row input)
        {
            string str = (string)args_[0].Exec(context, input);
            int start = (int)args_[1].Exec(context, input) - 1;
            int end = (int)args_[2].Exec(context, input) - 1;

            return str.Substring(start, end - start + 1);
        }
    }
    public class YearFunc : FuncExpr
    {
        public YearFunc(List<Expr> args) : base("year", args) { 
            argcnt_ = 1;
            type_ = new DateTimeType();
        }
        public override Value Exec(ExecContext context, Row input)
        {
            return 0;
        }
    }

    public class DateFunc : FuncExpr {
        public DateFunc(List<Expr> args) : base("date", args)
        {
            argcnt_ = 1;
            type_ = new DateTimeType();
        }
        public override Value Exec(ExecContext context, Row input)
        {
            var date = DateTime.Parse((string)args_[0].Exec(context, input));
            return date;
        }
    }

    public abstract class AggFunc : FuncExpr
    {
        public AggFunc(string func, List<Expr> args) : base(func, args) { argcnt_ = 1;}

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_[0].type_;
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
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_[0].Exec(context, input);
            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            dynamic lv = Convert.ChangeType(old, ltype);
            dynamic rv = Convert.ChangeType(arg, rtype);
            sum_ = lv + rv;
        }
        public override Value Exec(ExecContext context, Row input) => sum_;
    }

    public class AggCount : AggFunc
    {
        // Exec info
        internal long count_;
        public AggCount(Expr arg) : base("count", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input) => count_ = 1;
        public override void Accum(ExecContext context, Value old, Row input) => count_ += 1;
        public override Value Exec(ExecContext context, Row input) => count_;
    }
    public class AggCountStar : AggFunc
    {
        // Exec info
        internal long count_;
        public AggCountStar(Expr arg) : base("count(*)", new List<Expr> { new LiteralExpr("0") }) { argcnt_ = 0; }

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

            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            dynamic lv = Convert.ChangeType(old, ltype);
            dynamic rv = Convert.ChangeType(arg, rtype);
            min_ = lv > rv ? arg : old;
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
            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            dynamic lv = Convert.ChangeType(old, ltype);
            dynamic rv = Convert.ChangeType(arg, rtype);
            max_ = lv > rv ? old : arg;
        }
        public override Value Exec(ExecContext context, Row input) => max_;
    }

    public class AggAvg : AggFunc
    {
        // Exec info
        internal Value sum_;
        internal long count_;

        public AggAvg(Expr arg) : base("avg", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input)
        {
            sum_ = args_[0].Exec(context, input);
            count_ = 1;
        }
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_[0].Exec(context, input);
            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            dynamic lv = Convert.ChangeType(old, ltype);
            dynamic rv = Convert.ChangeType(arg, rtype);
            sum_ = lv + rv;
            count_ += 1;
        }
        public override Value Exec(ExecContext context, Row input)
        {
            Type ltype; ltype = typeof(int);
            dynamic lv = Convert.ChangeType(sum_, ltype);
            return lv / count_;
        }
    }
}

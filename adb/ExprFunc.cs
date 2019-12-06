using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Value = System.Object;

namespace adb
{
    public class FuncExpr : Expr
    {
        internal string funcName_;
        internal static int argcnt_;

        internal List<Expr> args_() => children_;
        public FuncExpr(string funcName, List<Expr> args)
        {
            funcName_ = funcName;
            children_.AddRange(args);
        }

        // sum(min(x)) => x
        public List<Expr> GetNonFuncExprList()
        {
            List<Expr> r = new List<Expr>();
            args_().ForEach(x =>
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

        public override int GetHashCode()
        {
            int hashcode = 0;
            args_().ForEach(x => hashcode ^= x.GetHashCode());
            return funcName_.GetHashCode() ^ hashcode;
        }
        public override bool Equals(object obj)
        {
            if (obj is FuncExpr of)
                return funcName_.Equals(of.funcName_) && args_().SequenceEqual(of.args_());
            else if (obj is ExprRef oe)
                return Equals(oe.expr_());
            return false;
        }
        public override string ToString() => $"{funcName_}({string.Join(",", args_())})";
    }

    public class SubstringFunc : FuncExpr { 
        public SubstringFunc(List<Expr> args): base("substring", args){ 
            argcnt_ = 3; 
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_()[0].type_;
            Debug.Assert(type_ is CharType || type_ is VarCharType);
        }
        public override Value Exec(ExecContext context, Row input)
        {
            string str = (string)args_()[0].Exec(context, input);
            int start = (int)args_()[1].Exec(context, input) - 1;
            int end = (int)args_()[2].Exec(context, input) - 1;

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
            var date = DateTime.Parse((string)args_()[0].Exec(context, input));
            return date;
        }
    }

    public abstract class AggFunc : FuncExpr
    {
        public AggFunc(string func, List<Expr> args) : base(func, args) { argcnt_ = 1;}

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_()[0].type_;
        }
        public virtual void Init(ExecContext context, Row input) { }
        public virtual void Accum(ExecContext context, Value old, Row input) { }
    }

    public class AggSum : AggFunc
    {
        // Exec info
        internal Value sum_;
        public AggSum(Expr arg) : base("sum", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input) => sum_ = args_()[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_()[0].Exec(context, input);
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
        public override void Init(ExecContext context, Row input) => min_ = args_()[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_()[0].Exec(context, input);

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
        public override void Init(ExecContext context, Row input) => max_ = args_()[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_()[0].Exec(context, input);
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
        Value sum_;
        long count_;

        public AggAvg(Expr arg) : base("avg", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input)
        {
            sum_ = args_()[0].Exec(context, input);
            count_ = 1;
        }
        public override void Accum(ExecContext context, Value old, Row input)
        {
            var arg = args_()[0].Exec(context, input);
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

    // case <eval>
    //      when <when0> then <then0>
    //      when <when1> then <then1>
    //      else <else>
    //  end;
    public class CaseExpr : Expr
    {
        internal int nEval_ = 0;
        internal int nWhen_;
        internal int nElse_ = 0;

        public override string ToString() => $"case with {when_().Count}";

        internal Expr eval_() => nEval_ != 0 ? children_[0] : null;
        internal List<Expr> when_() => children_.GetRange(nEval_, nWhen_);
        internal List<Expr> then_() => children_.GetRange(nEval_ + nWhen_, nWhen_);
        internal Expr else_() => nElse_ != 0 ? children_[nEval_ + nWhen_ + nWhen_] : null;
        public CaseExpr(Expr eval, List<Expr> when, List<Expr> then, Expr elsee)
        {
            if (eval != null)
            {
                nEval_ = 1;
                children_.Add(eval);   // can be null
            }
            nWhen_ = when.Count;
            children_.AddRange(when);
            children_.AddRange(then);

            if (elsee != null)
            {
                nElse_ = 1;
                children_.Add(elsee);   // can be null
            }
            Debug.Assert(when_().Count == then_().Count);
            Debug.Assert(when_().Count >= 1);
            Debug.Assert(eval_() == eval);
            Debug.Assert(else_() == elsee);
            Debug.Assert(Clone().Equals(this));
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = then_()[0].type_;
            if (else_() != null)
                Debug.Assert(type_.Compatible(else_().type_));
        }


        public override int GetHashCode() => base.GetHashCode();

        public override bool Equals(object obj)
        {
            if (obj is ExprRef or)
                return Equals(or.expr_());
            else if (obj is CaseExpr co)
                return exprEquals(eval_(), co.eval_()) && exprEquals(else_(), co.else_())
                    && exprEquals(when_(), co.when_()) && exprEquals(then_(), co.then_());
            return false;
        }

        public override object Exec(ExecContext context, Row input)
        {
            if (eval_() != null)
            {
                var eval = eval_().Exec(context, input);
                for (int i = 0; i < when_().Count; i++)
                {
                    if (eval.Equals(when_()[i].Exec(context, input)))
                        return then_()[i].Exec(context, input);
                }
            }
            else
            {
                for (int i = 0; i < when_().Count; i++)
                {
                    if ((bool)when_()[i].Exec(context, input))
                        return then_()[i].Exec(context, input);
                }
            }
            if (else_() != null)
                return else_().Exec(context, input);
            return int.MaxValue;
        }
    }

    // we can actually put all binary ops in BinExpr class but we want to keep 
    // some special ones (say AND/OR) so we can coding easier
    //
    public class BinExpr : Expr
    {
        internal string op_;

        internal Expr l_() => children_[0];
        internal Expr r_() => children_[1];
        public override int GetHashCode() => l_().GetHashCode() ^ r_().GetHashCode() ^ op_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return this.Equals(oe.children_[0]);
            else if (obj is BinExpr bo)
            {
                return exprEquals(l_(), bo.l_()) && exprEquals(r_(), bo.r_()) && op_.Equals(bo.op_);
            }
            return false;
        }
        public BinExpr(Expr l, Expr r, string op)
        {
            children_.Add(l);
            children_.Add(r);
            op_ = op;
            Debug.Assert(l_() == l);
            Debug.Assert(r_() == r);
            Debug.Assert(Clone().Equals(this));
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);

            // derive return type
            Debug.Assert(l_().type_ != null && r_().type_ != null);
            switch (op_)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                    type_ = l_().type_;
                    break;
                case ">":
                case ">=":
                case "<":
                case "<=":
                case "=":
                case "<>":
                case " and ":
                case " or ":
                case "like":
                case "notlike":
                case "in":
                    type_ = new BoolType();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public override string ToString() => $"{l_()}{op_}{r_()}";

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic lv = l_().Exec(context, input);
            dynamic rv = r_().Exec(context, input);

            switch (op_)
            {
                case "+": return lv + rv;
                case "-": return lv - rv;
                case "*": return lv * rv;
                case "/": return lv / rv;
                case ">": return lv > rv;
                case ">=": return lv >= rv;
                case "<": return lv < rv;
                case "<=": return lv <= rv;
                case "=": return lv == rv;
                case "<>": return lv != rv;
                case "like": return Utils.StringLike(lv, rv);
                case "notlike": return !Utils.StringLike(lv, rv);
                case " and ": return lv && rv;
                case " or ": return lv || rv;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class LogicAndOrExpr : BinExpr
    {
        public LogicAndOrExpr(Expr l, Expr r, string op) : base(l, r, op)
        {
            Debug.Assert(op.Equals(" and ") || op.Equals(" or "));
            type_ = new BoolType(); Debug.Assert(Clone().Equals(this));
        }
    }
    public class LogicAndExpr : BinExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ") { }
        public List<Expr> BreakToList()
        {
            var andlist = new List<Expr>();
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? l_() : r_();
                if (e is LogicAndExpr ea)
                    andlist.AddRange(ea.BreakToList());
                else
                    andlist.Add(e);
            }

            return andlist;
        }
    }
    public class LogicOrExpr : BinExpr
    {
        public LogicOrExpr(Expr l, Expr r) : base(l, r, " or ") { }
    }
}

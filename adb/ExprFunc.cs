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
        internal int argcnt_;

        internal Expr arg_() { Debug.Assert(argcnt_ == 1); return args_()[0]; }
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
                case "substr": case "substring": r = new SubstringFunc(args); break;
                case "year": r = new YearFunc(args); break;
                case "repeat": r = new RepeatFunc(args); break;
                case "round": r = new RoundFunc(args); break;
                default:
                    r = new FuncExpr(funcName, args);
                    break;
            }

            // verify arguments count
            Utils.Checks(args.Count == r.argcnt_, $"{r.argcnt_} argument is expected");
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

    public class RepeatFunc : FuncExpr {
        public RepeatFunc(List<Expr> args) : base("repeat", args)
        {
            argcnt_ = 2;
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
            int times = (int)args_()[1].Exec(context, input);

            return string.Join("", Enumerable.Repeat(str, times));
        }
    }
    public class RoundFunc : FuncExpr
    {
        public RoundFunc(List<Expr> args) : base("round", args)
        {
            argcnt_ = 2;
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_()[0].type_;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic number = args_()[0].Exec(context, input);
            int decimals = (int)args_()[1].Exec(context, input);

            return Math.Round(number, decimals);
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
            var date = DateTime.Parse((string)arg_().Exec(context, input));
            return date;
        }
    }

    public abstract class AggFunc : FuncExpr
    {
        public AggFunc(string func, List<Expr> args) : base(func, args) { 
            argcnt_ = 1;
            foreach (var v in args) {
                if (v.HasAggFunc())
                    throw new Exception("aggregate functions cannot be nested");
            }
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            if (argcnt_ == 1)
                type_ = arg_().type_;
        }
        public abstract Value Init(ExecContext context, Row input);
        public abstract Value Accum(ExecContext context, Value old, Row input);
    }

    public class AggSum : AggFunc
    {
        // Exec info
        internal Value sum_;
        public AggSum(Expr arg) : base("sum", new List<Expr> { arg }) { }

        public override Value Init(ExecContext context, Row input)
        {
            sum_ = arg_().Exec(context, input);
            return sum_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);
            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            if (old is null)
                sum_ = arg;
            else
            {
                dynamic lv = old;
                if (!(arg is null))
                {
                    dynamic rv = arg;
                    sum_ = lv + rv;
                }
            }

            return sum_;
        }
        public override Value Exec(ExecContext context, Row input) => sum_;
    }

    public class AggCount : AggFunc
    {
        // Exec info
        internal long count_;
        public AggCount(Expr arg) : base("count", new List<Expr> { arg }) { }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = new IntType();
        }

        public override Value Init(ExecContext context, Row input) 
        {
            count_ = 0;
            Accum(context, null, input);
            return count_;
        }

        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);
            if (arg != null)
                count_  = old is null? 1: (long)old + 1;
            return count_;
        }
        public override Value Exec(ExecContext context, Row input) => count_;
    }

    public class AggCountStar : AggFunc
    {
        // Exec info
        internal long count_;
        public AggCountStar(Expr arg) : base("count(*)", new List<Expr> { new LiteralExpr("0", new IntType()) }) { argcnt_ = 0; }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = new IntType();
        }

        public override Value Init(ExecContext context, Row input)
        {
            count_ = 1;
            return count_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            count_ = (long)old + 1;
            return count_;
        }
        public override Value Exec(ExecContext context, Row input) => count_;
    }

    public class AggMin : AggFunc
    {
        // Exec info
        Value min_;
        public AggMin(Expr arg) : base("min", new List<Expr> { arg }) { }
        public override Value Init(ExecContext context, Row input)
        {
            min_ = args_()[0].Exec(context, input);
            return min_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);

            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);
            if (old is null)
                min_ = arg;
            else {
                dynamic lv = old;
                if (!(arg is null))
                {
                    dynamic rv = arg;
                    min_ = lv > rv ? arg : old;
                }
            }

            return min_;
        }
        public override Value Exec(ExecContext context, Row input) => min_;
    }

    public class AggMax : AggFunc
    {
        // Exec info
        Value max_;
        public AggMax(Expr arg) : base("max", new List<Expr> { arg }) { }
        public override Value Init(ExecContext context, Row input)
        {
            max_ = arg_().Exec(context, input);
            return max_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);

            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int); // FIXME
            if (old is null)
                max_ = arg;
            else
            {
                dynamic lv = old;
                if (!(arg is null))
                {
                    dynamic rv = arg;
                    max_ = lv < rv ? arg : old;
                }
            }

            return max_;
        }
        public override Value Exec(ExecContext context, Row input) => max_;
    }

    public class AggAvg : AggFunc
    {
        // Exec info
        public class AvgPair {
            internal Value sum_;
            internal long count_;
            internal Value Compute()
            {
                dynamic lv = sum_;
                if (count_ == 0)
                {
                    Debug.Assert(lv is null);
                    return null;
                }
                Debug.Assert(count_ != 0);
                return lv is null ? null : lv / count_;
            }

            public override string ToString() => $"avg({sum_}/{count_})";
        }
        AvgPair pair_;

        public AggAvg(Expr arg) : base("avg", new List<Expr> { arg }) { }

        public override Value Init(ExecContext context, Row input)
        {
            pair_ = new AvgPair();
            pair_.sum_ = arg_().Exec(context, input);
            pair_.count_ = pair_.sum_ is null? 0: 1;
            return pair_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);
            Type ltype, rtype; ltype = typeof(int); rtype = typeof(int);

            Debug.Assert(old != null);
            AvgPair oldpair = old as AvgPair;
            if (oldpair.sum_ is null)
            {
                pair_.sum_ = arg;
                Debug.Assert(oldpair.count_ == 0);
                if (arg != null)
                    pair_.count_ = 1;
            }
            else
            {
                dynamic lv = oldpair.sum_;
                if (!(arg is null))
                {
                    dynamic rv = arg;
                    pair_.sum_ = lv + rv;
                    pair_.count_ = oldpair.count_ + 1;
                }
            }

            return pair_;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            return pair_.Compute();
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

        public override Value Exec(ExecContext context, Row input)
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
                    if (when_()[i].Exec(context, input) is true)
                        return then_()[i].Exec(context, input);
                }
            }
            if (else_() != null)
                return else_().Exec(context, input);
            return null;
        }
    }

    // we can actually put all binary ops in BinExpr class but we want to keep 
    // some special ones (say AND/OR) so we can coding easier
    //
    public class BinExpr : Expr
    {
        internal string op_;

        public override int GetHashCode() => l_().GetHashCode() ^ r_().GetHashCode() ^ op_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return this.Equals(oe.expr_());
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
                case "not like":
                case "in":
                case "is":
                case "is not":
                    type_ = new BoolType();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public override string ToString() => $"{l_()}{op_}{r_()}{outputName()}";

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic lv = l_().Exec(context, input);
            dynamic rv = r_().Exec(context, input);

            if (op_ != "is" && (lv is null || rv is null))
                return null;

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
                case "not like": return !Utils.StringLike(lv, rv);
                case " and ": return lv && rv;
                case " or ": // null handling is different - handled by itself
                case "is":
                    return lv is null && rv is null;
                case "is not":
                    return !(lv is null) && rv is null;
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
            Debug.Assert(this.IsBoolean());
        }
    }
    public class LogicAndExpr : LogicAndOrExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ")
        {
        }

        public static LogicAndExpr MakeExpr(Expr l, Expr r)
        {
            var and = new LogicAndExpr(l, r);
            and.ResetAggregateTableRefs();
            and.bounded_ = true;
            return and;
        }

        // a AND (b OR c) AND d => [a, b OR c, d]
        //
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

    public class LogicOrExpr : LogicAndOrExpr
    {
        public LogicOrExpr(Expr l, Expr r) : base(l, r, " or ") { }

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic lv = l_().Exec(context, input);
            dynamic rv = r_().Exec(context, input);

            if (lv is null) lv = false;
            if (rv is null) rv = false;
            return lv || rv;
        }
    }

    public class CastExpr : Expr {
        public override string ToString() => $"cast({child_()} to {type_})";
        public CastExpr(Expr child, ColumnType coltype) { children_.Add(child); type_ = coltype; }
        public override Value Exec(ExecContext context, Row input)
        {
            Value to = null;
            dynamic from = child_().Exec(context, input);
            switch (from) {
                case string vs:
                    switch (type_) {
                        case DateTimeType td:
                            to = DateTime.Parse(from);
                            break;
                        default:
                            break;
                    }
                    break;
                default:
                    to = from;
                    break;
            }
            return to;
        }
    }
}

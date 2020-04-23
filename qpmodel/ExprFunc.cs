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
using System.Diagnostics;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;

using Value = System.Object;

namespace qpmodel.expr
{
    static public class ExternalFunctions
    {
        public class FunctionDesc {
            internal int argcnt_;
            internal ColumnType rettype_;
            internal object fn_;
        }
        static public Dictionary<string, FunctionDesc> set_ = new Dictionary<string, FunctionDesc>();

        static ColumnType externalType2ColumnType(Type type)
        {
            ColumnType ctype = null;

            if (type == typeof(double))
                ctype = new DoubleType();
            else if (type == typeof(int))
                ctype = new IntType();
            else if (type == typeof(string))
                ctype = new VarCharType(64*1024);
            else
                throw new NotImplementedException();

            return ctype;
        }
        static public void Register(string name, object fn, int argcnt, Type rettype)
        {
            FunctionDesc desc = new FunctionDesc();

            Utils.Assumes(argcnt <= 3);
            desc.fn_ = fn;
            desc.argcnt_ = argcnt;
            desc.rettype_ = externalType2ColumnType(rettype);
            set_.Add(name, desc);
        }
    }

    public class FuncExpr : Expr
    {
        internal string funcName_;
        internal int argcnt_;

        internal Expr arg_() { Debug.Assert(argcnt_ == 1); return args_()[0]; }
        internal List<Expr> args_() => children_;

        public FuncExpr(string funcName, List<Expr> args) : base()
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
                x.VisitEachIgnoreRef<Expr>(y =>
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
                case "stddev_samp": r = new AggStddevSamp(args[0]); break;
                case "count":
                    if (args.Count == 0)
                        r = new AggCountStar(null);
                    else
                        r = new AggCount(args[0]);
                    break;
                case "substr": case "substring": r = new SubstringFunc(args); break;
                case "upper": r = new UpperFunc(args); break;
                case "year": r = new YearFunc(args); break;
                case "repeat": r = new RepeatFunc(args); break;
                case "round": r = new RoundFunc(args); break;
                case "coalesce": r = new CoalesceFunc(args); break;
                default:
                    if (ExternalFunctions.set_.ContainsKey(funcName))
                        r = new ExternalFunc(func, args);
                    else
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

    // As a wrapper of external functions
    public class ExternalFunc : FuncExpr {
        public ExternalFunc(string funcName, List<Expr> args) : base(funcName, args) {
            // TBD: we shall verify with register signature
            var desc = ExternalFunctions.set_[funcName_];
            if (desc.argcnt_ != args.Count)
                throw new SemanticAnalyzeException("argcnt not match");
            argcnt_ = args.Count;
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            var desc = ExternalFunctions.set_[funcName_];
            type_ = desc.rettype_;
        }

        public override object Exec(ExecContext context, Row input)
        {
            var desc = ExternalFunctions.set_[funcName_];

            Debug.Assert(argcnt_ == desc.argcnt_);
            dynamic fncode = desc.fn_;
            List<dynamic> args = new List<dynamic>();
            for (int i = 0; i < argcnt_; i++)
                args.Add(args_()[i].Exec(context, input));
            switch (argcnt_)
            {
                case 0: return fncode();
                case 1: return fncode(args[0]);
                case 2: return fncode(args[0], args[1]);
                case 3: return fncode(args[0], args[1], args[2]);
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class SubstringFunc : FuncExpr {
        public SubstringFunc(List<Expr> args) : base("substring", args) {
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

            if (str is null)
                return null;
            return str.Substring(start, end - start + 1);
        }
    }
    public class UpperFunc : FuncExpr
    {
        public UpperFunc(List<Expr> args) : base("upper", args)
        {
            argcnt_ = 1;
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
            return str.ToUpper();
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

            if (number is null)
                return null;
            return Math.Round(number, decimals);
        }
    }

    public class CoalesceFunc : FuncExpr
    {
        public CoalesceFunc(List<Expr> args) : base("coalesce", args)
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
            var val = args_()[0].Exec(context, input);
            if (val is null)
                return args_()[1].Exec(context, input);
            return val;
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
            var date = (DateTime)arg_().Exec(context, input);
            return date.Year;
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
        public virtual Value Finalize(ExecContext context, Value old) => old;

        public override object Exec(ExecContext context, Row input) 
            => throw new Exception("aggfn [some] are stateful, they use different set of APIs");
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
                count_ = old is null ? 1 : (long)old + 1;
            return count_;
        }
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
    }

    public class AggAvg : AggFunc
    {
        // Exec info
        public class AvgPair {
            internal Value sum_;
            internal long count_;
            internal Value Finalize()
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
            pair_.count_ = pair_.sum_ is null ? 0 : 1;
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

        public override Value Finalize(ExecContext context, Value old) => (old as AvgPair).Finalize();
    }

    // sqrt(sum((x_i - mean)^2)/(n-1)) where n is sample size
    public class AggStddevSamp: AggFunc
    {
        public class AggStddevValues {
            Value stddev_ = null;
            bool computed_ = false;
            internal List<dynamic> vals_ = new List<dynamic>();
            internal Value Finalize()
            {
                if (!computed_)
                {
                    int n = vals_.Count;
                    if (n == 1)
                        stddev_ = null;
                    else
                    {
                        dynamic sum = 0.0; vals_.ForEach(x => sum += x);
                        var mean = sum / n;

                        dynamic stddev = 0; vals_.ForEach(x => stddev += ((x - mean) * (x - mean)));
                        stddev = Math.Sqrt(stddev / (n - 1));
                        stddev_ = stddev;
                    }

                    computed_ = true;
                }

                return stddev_;
            }
        }
        AggStddevValues values_;

        public AggStddevSamp(Expr arg) : base("stddev_samp", new List<Expr> { arg }) { }

        public override Value Init(ExecContext context, Row input)
        {
            values_ = new AggStddevValues();
            values_.vals_.Add(arg_().Exec(context, input));
            type_ = new DoubleType();
            return values_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);
            AggStddevValues oldlist = old as AggStddevValues;
            oldlist.vals_.Add(arg);
            values_ = oldlist;
            return values_;
        }
        public override Value Finalize(ExecContext context, Value old) => (old as AggStddevValues).Finalize();
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
        public CaseExpr(Expr eval, List<Expr> when, List<Expr> then, Expr elsee) : base()
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

    public class UnaryExpr : Expr
    {
        internal string op_;

        internal Expr arg_() => children_[0];
        public UnaryExpr(string op, Expr expr) : base()
        {
            string[] supportops = { "+", "-" };

            op = op.ToLower();
            if (!supportops.Contains(op))
                throw new SemanticAnalyzeException($"{op} on {expr} is not supported");
            op_ = op;
            children_.Add(expr);
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = arg_().type_;
        }
        public override string ToString() => $"{op_}{arg_()}";
        public override object Exec(ExecContext context, Row input)
        {
            Value arg = arg_().Exec(context, input);
            switch (op_)
            {
                case "-":
                    return -(dynamic)arg;
                default:
                    return arg;
            }
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
                return exprEquals(l_(), bo.l_()) && exprEquals(r_(), bo.r_()) && op_.Equals(bo.op_);
            return false;
        }
        public BinExpr(Expr l, Expr r, string op) : base()
        {
            children_.Add(l);
            children_.Add(r);
            op_ = op.ToLower();
            Debug.Assert(Clone().Equals(this));
        }

        public static BinExpr MakeBooleanExpr(Expr l, Expr r, string op)
        {
            Debug.Assert(l.bounded_ && r.bounded_);
            var expr = new BinExpr(l, r, op);
            expr.ResetAggregateTableRefs();
            expr.markBounded();
            expr.type_ = new BoolType();
            return expr;
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);

            // derive return type
            Debug.Assert(l_().type_ != null && r_().type_ != null);
            switch (op_)
            {
                case "+": case "-": case "*": case "/": case "||":
                    // notice that CoerseType() may change l/r underneath
                    type_ = ColumnType.CoerseType(op_, l_(), r_());
                    break;
                case ">": case ">=": case "<": case "<=": 
                case "=":case "<>": case "!=":
                case " and ": case " or ":
                case "like": case "not like": case "in":
                case "is": case "is not":
                    type_ = new BoolType();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public static string SwapSideOp(string op) {
            switch (op)
            {
                case ">": return "<";
                case ">=": return "<=";
                case "<": return ">";
                case "<=": return ">=";
                case "in":
                    throw new Exception("not switchable");
                default:
                    return op;
            }
        }

        public void SwapSide()
        {
            var oldl = l_();
            children_[0] = r_();
            children_[1] = oldl;
            op_ = SwapSideOp(op_);
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
                case "||": return string.Concat(lv, rv);
                case ">": return lv > rv;
                case ">=": return lv >= rv;
                case "<": return lv < rv;
                case "<=": return lv <= rv;
                case "=": return lv == rv;
                case "<>": case "!=": return lv != rv;
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

        public override string ExecCode(ExecContext context, string input)
        {
            string lv = "(dynamic)" + l_().ExecCode(context, input);
            string rv = "(dynamic)" + r_().ExecCode(context, input);

            string code = null;
            switch (op_)
            {
                case "+": code = $"({lv} + {rv})"; break;
                case "-": code = $"({lv} - {rv})"; break;
                case "*": code = $"({lv} * {rv})"; break;
                case "/": code = $"({lv} / {rv})"; break;
                case ">": code = $"({lv} > {rv})"; break;
                case ">=": code = $"({lv} >= {rv})"; break;
                case "<": code = $"({lv} < {rv})"; break;
                case "<=": code = $"({lv} <= {rv})"; break;
                case "=": code = $"({lv} == {rv})"; break;
                case " and ": code = $"((bool){lv} && (bool){rv})"; break;
                default:
                    throw new NotImplementedException();
            }
            return code;
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
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and "){}

        public static LogicAndExpr MakeExpr(Expr l, Expr r)
        {
            Debug.Assert(l.bounded_ && r.bounded_);
            var and = new LogicAndExpr(l, r);
            and.ResetAggregateTableRefs();
            and.markBounded();
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
        public CastExpr(Expr child, ColumnType coltype) : base() { children_.Add(child); type_ = coltype; }
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

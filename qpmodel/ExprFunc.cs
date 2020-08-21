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
using qpmodel.stream;

using Value = System.Object;
using Microsoft.CodeAnalysis;
using System.ComponentModel;
using System.Collections.Specialized;

namespace qpmodel.expr
{
    static public class ExternalFunctions
    {
        public class FunctionDesc
        {
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
                ctype = new VarCharType(64 * 1024);
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
        internal bool isSRF_ = false; // set returning function

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
                case "sum": r = new AggSum(args); break;
                case "min": r = new AggMin(args); break;
                case "max": r = new AggMax(args); break;
                case "avg": r = new AggAvg(args); break;
                case "stddev_samp": r = new AggStddevSamp(args); break;
                case "count":
                    if (args.Count == 0)
                        r = new AggCountStar(null);
                    else
                        r = new AggCount(args);
                    break;
                case "substr": case "substring": r = new SubstringFunc(args); break;
                case "upper": r = new UpperFunc(args); break;
                case "year": r = new YearFunc(args); break;
                case "date": r = new DateFunc(args); break;
                case "repeat": r = new RepeatFunc(args); break;
                case "abs": r = new AbsFunc(args); break;
                case "round": r = new RoundFunc(args); break;
                case "coalesce": r = new CoalesceFunc(args); break;
                case "hash": r = new HashFunc(args); break;
                case "tumble": r = new TumbleWindow(args); break;
                case "tumble_start": r = new TumbleStart(args); break;
                case "tumble_end": r = new TumbleEnd(args); break;
                case "hop": r = new HopWindow(args); break;
                case "session": r = new SessionWindow(args); break;
                default:
                    if (ExternalFunctions.set_.ContainsKey(funcName))
                        r = new ExternalFunc(func, args);
                    else
                        r = new FuncExpr(funcName, args);
                    break;
            }

            // verify arguments count
            if (args.Count != r.argcnt_)
                throw new SemanticAnalyzeException($"{r.argcnt_} argument is expected");
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();

            if (!x.AllArgsConst())
                return x;
            if (ExternalFunctions.set_.ContainsKey(funcName_))
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            switch (funcName_)
            {
                case "min":
                case "max":
                case "avg":
                case "upper":
                case "year":
                case "date":
                case "abs":
                    // TryEvalConst doesn't work, these are single child
                    // functions, just return the only child.
                    return child_();
                    break;

                default:
                    break;
            }

            return x;
        }
    }

    // As a wrapper of external functions
    public class ExternalFunc : FuncExpr
    {
        public ExternalFunc(string funcName, List<Expr> args) : base(funcName, args)
        {
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
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class SubstringFunc : FuncExpr
    {
        public SubstringFunc(List<Expr> args) : base("substring", args)
        {
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
            // SQL allows substr() function go beyond length, guard it
            return str.Substring(start, Math.Min(end - start + 1, str.Length));
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_); ;
        }
    }

    public class RepeatFunc : FuncExpr
    {
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
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

            // there are multiple Math.Round(), an integer number confuses them
            var type = args_()[0].type_;
            if (type is IntType)
                return Math.Round((decimal)number, decimals);
            else
                return Math.Round((double)number, decimals);
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class AbsFunc : FuncExpr
    {
        public AbsFunc(List<Expr> args) : base("abs", args)
        {
            argcnt_ = 1;
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = args_()[0].type_;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic number = args_()[0].Exec(context, input);

            // there are multiple Math.Abs(), an integer number confuses them
            var type = args_()[0].type_;
            if (type is IntType)
                return Math.Abs((decimal)number);
            else
                return Math.Abs((double)number);
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
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
            type_ = args_()[1].type_;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            var val = args_()[0].Exec(context, input);
            if (val is null)
                return args_()[1].Exec(context, input);
            return val;
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            for (int i = 0; i < children_.Count; ++i)
            {
                if (children_[i] is ConstExpr ce && !ce.IsNull())
                    return ce;
            }

            return x;
        }
    }

    public class YearFunc : FuncExpr
    {
        public YearFunc(List<Expr> args) : base("year", args)
        {
            argcnt_ = 1;
            type_ = new DateTimeType();
        }
        public override Value Exec(ExecContext context, Row input)
        {
            var date = (DateTime)arg_().Exec(context, input);
            return date.Year;
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class DateFunc : FuncExpr
    {
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class HashFunc : FuncExpr
    {
        public HashFunc(List<Expr> args) : base("hash", args)
        {
            argcnt_ = 1;
            type_ = new IntType();
        }
        public override Value Exec(ExecContext context, Row input)
        {
            dynamic val = arg_();
            int hashval = val.GetHashCode();
            return hashval;
        }
    }

    public abstract class AggFunc : FuncExpr
    {
        public AggFunc(string func, List<Expr> args) : base(func, args)
        {
            argcnt_ = 1;
            foreach (var v in args)
            {
                if (v.HasAggFunc())
                    throw new SemanticAnalyzeException("aggregate functions cannot be nested");
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
            => throw new InvalidProgramException("aggfn [some] are stateful, they use different set of APIs");
    }

    public class AggSum : AggFunc
    {
        // Exec info
        internal Value sum_;
        public AggSum(List<Expr> args) : base("sum", args) { }

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
        public AggCount(List<Expr> args) : base("count", args) { }

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
        public AggCountStar(List<Expr> args) : base("count(*)", new List<Expr> { new ConstExpr("0", new IntType()) })
        {
            Debug.Assert(args is null);
            argcnt_ = 0;
        }

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
        public AggMin(List<Expr> args) : base("min", args) { }
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
            else
            {
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
        public AggMax(List<Expr> args) : base("max", args) { }
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
        public class AvgPair
        {
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

        public AggAvg(List<Expr> args) : base("avg", args) { }

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
            pair_ = oldpair;
            Debug.Assert(oldpair.count_ >= 0);
            if (oldpair.sum_ is null)
            {
                if (arg != null)
                {
                    pair_.sum_ = arg;
                    pair_.count_ = 1;
                }
            }
            else
            {
                dynamic lv = oldpair.sum_;
                if (arg != null)
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
    public class AggStddevSamp : AggFunc
    {
        public class AggStddevValues
        {
            Value stddev_ = null;
            bool computed_ = false;
            internal List<dynamic> vals_ = new List<dynamic>();
            internal Value Finalize()
            {
                if (!computed_)
                {
                    stddev_ = null;
                    int n = vals_.Count;
                    if (n != 1)
                    {
                        dynamic sum = 0.0; vals_.ForEach(x => sum += x is null ? 0 : x);
                        if (sum != null)
                        {
                            var mean = sum / n;
                            dynamic stddev = 0; vals_.ForEach(x => stddev +=
                                            x is null ? 0 : ((x - mean) * (x - mean)));
                            if (stddev != null)
                            {
                                stddev = Math.Sqrt(stddev / (n - 1));
                                stddev_ = stddev;
                            }
                        }
                    }

                    computed_ = true;
                }

                return stddev_;
            }
        }
        AggStddevValues values_;

        public AggStddevSamp(List<Expr> args) : base("stddev_samp", args) { }

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
                Debug.Assert(TypeBase.Compatible(type_, else_().type_));
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class UnaryExpr : Expr
    {
        internal string op_;

        internal Expr arg_() => children_[0];
        public UnaryExpr(string op, Expr expr) : base()
        {
            string[] supportops = { "+", "-", "!" };

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
                case "!":
                    return !(bool)arg;
                default:
                    return arg;
            }
        }


        // This is a general purpose helper, so do not use it to make
        // "correct" logical operator node based on the operator, it used
        // here in the context of children already are bound only the
        // new node needs to marked bound. This is to be used only
        // within the context of Normalize method.
        internal Expr makeAnyLogicalExpr(Expr l, Expr r, string op)
        {

            if (op == " and ")
            {
                LogicAndExpr newe = new LogicAndExpr(l, r);
                newe.bounded_ = true;

                return newe;
            }
            else
            {
                LogicOrExpr newe = new LogicOrExpr(l, r);
                newe.bounded_ = true;

                return newe;
            }
        }

public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (x.AllArgsConst())
            {
                Value val = Exec(null, null);

                return ConstExpr.MakeConst(val, type_, outputName_);
            }

            // NOT NOT X => X
            if (op_ == "!") {
                if (child_() is UnaryExpr ue && ue.op_ == "!")
                {
                    return ue.child_();
                }

                // NOT (X AND Y)    => NOT A OR NOT B
                // NOT (X OR Y)     => NOT A AND NOT B
                /*
                 *           NOT                  OR
                 *            |                  /  \
                 *           AND      =>       NOT    NOT
                 *          /    \              |      |
                 *         X      Y             X      Y
                 */

                if (child_() is LogicAndOrExpr le)
                {

                    // Make two Unary expressions for NOT X and NOT Y
                    // Cloning avoids messing with bounded_ flag
                    UnaryExpr nlu = (UnaryExpr)Clone();
                    UnaryExpr nru = (UnaryExpr)Clone();

                    // get the new logical operator, reverse of current one
                    string nop = le.op_ == " and " ? " or " : " and ";

                    // save X, Y
                    Expr ole = le.lchild_();
                    Expr ore = le.rchild_();

                    string leftName = ole.outputName_;
                    string rightName = ore.outputName_;
                    string thisName = outputName_;

                    // New Unary nodes point to old left and right
                    // logical expressions.
                    nlu.children_[0] = ole;
                    nru.children_[0] = ore;

                    return makeAnyLogicalExpr(nlu, nru, nop);
                }
            }

            return x;
        }
    }

    // we can actually put all binary ops in BinExpr class but we want to keep
    // some special ones (say AND/OR) so we can coding easier
    //
    public class BinExpr : Expr
    {
        internal string op_;

        public override int GetHashCode() => lchild_().GetHashCode() ^ rchild_().GetHashCode() ^ op_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return this.Equals(oe.expr_());
            else if (obj is BinExpr bo)
                return exprEquals(lchild_(), bo.lchild_()) && exprEquals(rchild_(), bo.rchild_()) && op_.Equals(bo.op_);
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

        public bool isCommutativeConstOp() => (op_ == "+" || op_ == "*");

        public bool isFoldableConstOp() => (op_ == "+" || op_ == "-" || op_ == "*" || op_ == "/");

        public bool isPlainSwappableConstOp() => (op_ == "+" || op_ == "*" || op_ == "=" ||
            op_ == "<>" || op_ == "!=" || op_ == "<=" || op_ == ">=");

        public bool isChangeSwappableConstOp() => (op_ == "<" || op_ == ">");

        // Looks the same as isFoldableConstOp but the context/purpose is
        // different. If it turns out that they are one and the same, one of
        // them will be removed.
        internal bool IsLogicalOp() => (op_ == " and " || op_ == " or " || op_ == "not");

        internal bool IsArithmeticOp() => (op_ == "+" || op_ == "-" || op_ == "*" || op_ == "/");

        internal bool IsRelOp() => (op_ == "=" || op_ == "<=" || op_ == "<" || op_ == ">=" || op_ == ">" || op_ == "<>" || op_ == "!=");


        public override void Bind(BindContext context)
        {
            base.Bind(context);

            // derive return type
            Debug.Assert(lchild_().type_ != null && rchild_().type_ != null);
            switch (op_)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                case "||":
                    // notice that CoerseType() may change l/r underneath
                    type_ = ColumnType.CoerseType(op_, lchild_(), rchild_());
                    break;
                case ">":
                case ">=":
                case "<":
                case "<=":
                    if (TypeBase.IsNumberType(lchild_().type_))
                        ColumnType.CoerseType(op_, lchild_(), rchild_());
                    type_ = new BoolType();
                    break;
                case "=":
                case "<>":
                case "!=":
                    if (TypeBase.IsNumberType(lchild_().type_))
                        ColumnType.CoerseType(op_, lchild_(), rchild_());
                    type_ = new BoolType();
                    break;
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

        public static string SwapSideOp(string op)
        {
            switch (op)
            {
                case ">": return "<";
                case ">=": return "<=";
                case "<": return ">";
                case "<=": return ">=";
                case "in":
                    throw new InvalidProgramException("not switchable");
                default:
                    return op;
            }
        }

        public void SwapSide()
        {
            var oldl = lchild_();
            children_[0] = rchild_();
            children_[1] = oldl;
            op_ = SwapSideOp(op_);
        }

        public override string ToString()
        {
            bool addParen = false;
            string space = null;

            switch(op_)
            {
                case "like":
                case "not like":
                case "in":
                case "is":
                case "is not":
                    space = " ";
                    break;

                case "+":
                case "-":
                case " or ":
                case " and ":
                    addParen = true;
                    break;
            }

            string str = "";

            if (addParen)
                str += "(";

            str += $"{lchild_()}{space}{op_}{space}{rchild_()}{outputName()}";

            if (addParen)
                str += ")";

            return str;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            string[] nullops = { "is", "||" };
            dynamic lv = lchild_().Exec(context, input);
            dynamic rv = rchild_().Exec(context, input);

            if (!nullops.Contains(op_) && (lv is null || rv is null))
                return null;

            switch (op_)
            {
                // we can do a compile type type coerse for addition/multiply etc to align
                // data types, say double+decimal will require both side become decimal.
                // However, for comparison, we cam't do that, because that depends the actual
                // value: decimal has better precision and double has better range, if double
                // if out of decimal's range, we shall convert both to double; otherwise they
                // shall be converted to decimals.
                //
                // We do a simplification here by forcing type coerse for any op at Bind().
                //
                case "+": return lv + rv;
                case "-": return lv - rv;
                case "*": return lv * rv;
                case "/": return lv / rv;
                case "||": return string.Concat(lv, rv);
                case ">": return Compare(lv, rv) > 0;
                case ">=": return Compare(lv, rv) >= 0;
                case "<": return Compare(lv, rv) < 0;
                case "<=": return Compare(lv, rv) <= 0;
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
        int Compare(dynamic lv, dynamic rv)
        {
            if (lv is string && rv is string)
                return lv.CompareTo(rv);
            else
                return lv == rv ? 0 : lv < rv ? -1 : 1;
        }

        public override string ExecCode(ExecContext context, string input)
        {
            string lv = "(dynamic)" + lchild_().ExecCode(context, input);
            string rv = "(dynamic)" + rchild_().ExecCode(context, input);

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


        internal ColumnType TypeFromOperator(string op, ColumnType inType)
        {
            switch (op)
            {
                case ">":
                case ">=":
                case "<":
                case "<=":
                case "=":
                case "<>":
                case "!=":
                    return new BoolType();
            }

            return inType;
        }

        // Arthmentic Simplification:
        /*
        * +/- zero to a column, or multiply/divide a column by 1
        */
        internal bool IsArithIdentity(ConstExpr lce, ConstExpr rce)
        {
            ConstExpr ve = lce != null ? lce : rce;
            ColExpr ce = (children_[0] is ColExpr) ? (ColExpr)children_[0] : (children_[1] is ColExpr ? (ColExpr)children_[1] : null);

            if (ce == null)
                return false;

            if (!(TypeBase.IsNumberType(ve.type_) && TypeBase.IsNumberType(ce.type_)))
                return false;

            if ((op_ == "+" || op_ == "-") && ve.IsZero())
                return true;

            if ((op_ == "*" || op_ == "/") && ve.IsOne())
                return true;

            return false;
        }

        internal Expr SimplifyArithmetic(ConstExpr lve, ConstExpr rve)
        {
            // we know we have a BinExpr with numeric chhildren
            ConstExpr ve = lve != null ? lve : rve;
            ColExpr ce = (children_[0] is ColExpr) ? (ColExpr)children_[0] : (children_[1] is ColExpr ? (ColExpr)children_[1] : null);

            if (ce is null || ve is null)
                return this;

            // Col + 0, or Col - 0 => return col
            if ((op_ == "+" || op_ == "-") && ve.IsZero())
                return ce;

            if ((op_ == "*" || op_ == "/") && ve.IsOne())
                return ce;

            return this;
        }

        internal Expr SimplifyRelop()
        {
            Expr l = lchild_();
            Expr r = rchild_();

            ConstExpr lce = l is ConstExpr ? (ConstExpr)l : null;
            ConstExpr rce = r is ConstExpr ? (ConstExpr)r : null;

            if (lce == null && rce == null)
            {
                return this;
            }

            if (!(lce is null || rce is null))
            {
                if (lce.IsTrue() && rce.IsTrue())
                    return lce;
                else
                    return lce.IsTrue() ? lce : rce;
            }
            else
            if ((!(lce is null) && (lce.IsNull() || lce.IsFalse())) || (!(rce is null) && (rce.IsNull() || rce.IsFalse())))
            {
                return ConstExpr.MakeConst("false", new BoolType(), outputName_);
            }

            /*
             * X + C1 = C2      => X = C2 - C1
             * X + C1 > C2      => X > C2 - C1
             * X + C1 >= C2     => X >= C2 - C1
             * 
             * X - C1 = C2      => X = C2 + C1
             * X - C1 >= C2     => X >= C2 + C1
             * etc.
             *                                        (relop)
             *                                        /      \
             *                                      arith    const
             *                                     /    \
             *                                    x     const
             */

            if (!(rce is null) && l is BinExpr lbe && lbe.IsArithmeticOp() && lbe.rchild_() is ConstExpr lrc)
            {
                string curOp = op_, nop;
                if (lbe.op_ == "+" || lbe.op_ == "-")
                {
                    if (lbe.op_ == "+")
                        nop = "-";
                    else
                        nop = "+";

                    BinExpr newe = new BinExpr(rce, lrc, nop);
                    newe.type_ = ColumnType.CoerseType(nop, lrc, rce);
                    newe.bounded_ = true;

                    Value val = newe.Exec(null, null);
                    ConstExpr newc = ConstExpr.MakeConst(val, newe.type_);

                    newc.bounded_ = true;
                    children_[0] = l.lchild_();
                    children_[1] = newc;
                }
            }          

            return this;
        }

        /*
         * Apply all possible logical simplification rules.
         * Assumptions:
         *  1) children have been normalized
         *  2) NULL simplification and const move rules have been applied. 
         */

        internal Expr SimplifyLogic()
        {

            Expr l = lchild_();
            Expr r = rchild_();

            // simplify X relop CONST
            if (l is ConstExpr lce && r is ConstExpr rce)
            {
                if ((lce.IsTrue() && rce.IsTrue()) || (lce.IsFalse() && rce.IsFalse()))
                {
                    return lce;
                }

                return lce.IsFalse() ? lce : rce;
            }

            return this;
        }


        public override Expr Normalize()
        {

            // all children get normalized first
            for (int i = 0; i < children_.Count; ++i)
            {
                Expr x = children_[i];
                children_[i] = x.Normalize();
            }

            Expr l = lchild_();
            Expr r = rchild_();

            ConstExpr lce = (l is ConstExpr) ? (ConstExpr)l : null;
            ConstExpr rce = (r is ConstExpr) ? (ConstExpr)r : null;

            switch (op_)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                case ">":
                case ">=":
                case "<":
                case "<=":
                /* LATER: case "||": */
                case "=":
                case "<>":
                case "!=":
                case " and ":
                case " or ":
                    if (!IsRelOp() && (lce != null  && lce.val_ is null) || (rce != null && rce.val_ is null))
                    {
                        // NULL simplification: if operator is not relational, X op NULL is NULL
                        return lce is null ? lce : rce;
                    }

                    if (lce != null && rce != null)
                    {
                        // Simplify Constants: children are not non null constants, evaluate them.
                        Value val = Exec(null, null);

                        return ConstExpr.MakeConst(val, type_, outputName_);
                    }

                    if (lce != null && rce == null)
                    {
                        if (isPlainSwappableConstOp())
                        {
                            SwapSide();
                        }
                    }

                    if ((lce != null || rce != null) && (IsArithIdentity(lce, rce)))
                    {
                        return SimplifyArithmetic(lce, rce);
                    }

                    if (IsLogicalOp())
                    {
                        return SimplifyLogic();
                    }

                    if (IsRelOp())
                    {
                        return SimplifyRelop();
                    }

                    // arithmetic operators?
                    if (l is BinExpr le && le.children_[1].IsConst() && (rce != null) &&
                        isCommutativeConstOp() && le.isCommutativeConstOp() && TypeBase.SameArithType(l.type_, r.type_))
                    {
                        /*
                            * Here root is distributive operator (only * in this context) left is Commutative
                            * operator, +, or * right is constant, furthermore, left's right is a constant
                            * (becuase we swapped cosntant to be on the right).
                            * if be == + and l == +: add left's right value to root's right value,
                            * make  left's left as left of root
                            *
                            * if be == * and l == +: create a expr node as left (x + 10), create (5 * 10)
                            * as right, change operator to +
                            * In either case left and right's children must be nulled out
                            * and since we are going bottom up, this doesn't create any problem.
                            *
                            * Here is a pictorial description:
                            *                         *         root           +
                            *              old left  / \ old right   new left / \  new right
                            *                       /   \                    /   \
                            *                      +     10        =>       *     50
                            *  left of old left   / \                      / \
                            *                    /   \ ROL         LNL    /   \ RNL (right of New Left)
                            *                   x     5                  x     10
                            */

                        /*
                         * Simple case: when current and left are same operators, distributive
                         * opeartion and node creation is uncessary.
                         * ??? What about ordinal positions ???
                         */
                        if ((op_ == "+" && le.op_ == "+") || (op_ == "*" && le.op_ == "*"))
                        {

                            /* create new right node as constant. */
                            Expr tmpexp = Clone();
                            tmpexp.children_[0] = le.children_[1];
                            tmpexp.children_[1] = r;
                            tmpexp.type_ = r.type_;

                            Value val;
                            bool wasConst = tmpexp.TryEvalConst(out val);
                            Expr newr = ConstExpr.MakeConst(val, tmpexp.type_, r.outputName_);

                            // new left is old left's left child
                            // of left will be right of the root.
                            children_[0] = l.children_[0];

                            // new right is the new constant node
                            children_[1] = newr;

                            // be.children_.Clear();
                        }
                        else
                        if (op_ == "*")
                        {
                            /* case of (a + const1) * const2  => (a * const2) + (const1 * const2)) */
                            /* make a newe left node to house (a * const2) */
                            Expr newl = le.Clone();
                            newl.children_[0] = le;
                            newl.children_[1] = r;

                            /* make a const expr node to evaluate and house (const1 op const 2) */
                            Expr tmpexp = Clone();
                            tmpexp.children_[0] = le.children_[1]; // right of left is const
                            tmpexp.children_[1] = r;               // our right is const
                            tmpexp.type_ = r.type_;
                            
                            Value val;
                            tmpexp.TryEvalConst(out val);
                            Expr newr = ConstExpr.MakeConst(val, tmpexp.type_, r.outputName_);

                            /* now make a new root and attach newl and new r to it. */
                            children_[0] = newl;
                            children_[1] = newr;

                            /* swap the operators */
                            string op = op_;
                            op_ = le.op_;

                            BinExpr ble = (BinExpr)children_[1];
                            ble.op_ = op;
                        }
                        /* we can't do any thing about + at the top and * as left child. */
                    }

                    return this;
            }

            return this;
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
        public List<Expr> BreakToList(bool isAnd)
        {
            var andorlist = new List<Expr>();
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? lchild_() : rchild_();
                if (isAnd && e is LogicAndExpr ea)
                    andorlist.AddRange(ea.BreakToList(isAnd));
                else if (!isAnd && e is LogicOrExpr eo)
                    andorlist.AddRange(eo.BreakToList(isAnd));
                else
                    andorlist.Add(e);
            }
            return andorlist;
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (x is null || !x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class LogicAndExpr : LogicAndOrExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ") { }

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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (x is null || !x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class LogicOrExpr : LogicAndOrExpr
    {
        public LogicOrExpr(Expr l, Expr r) : base(l, r, " or ") { }

        public override Value Exec(ExecContext context, Row input)
        {
            dynamic lv = lchild_().Exec(context, input);
            dynamic rv = rchild_().Exec(context, input);

            if (lv is null) lv = false;
            if (rv is null) rv = false;
            return lv || rv;
        }

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (x is null || !x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }

    public class CastExpr : Expr
    {
        public override string ToString() => $"cast({child_()} to {type_})";
        public CastExpr(Expr child, ColumnType coltype) : base() { children_.Add(child); type_ = coltype; }
        public override Value Exec(ExecContext context, Row input)
        {
            Value to = null;
            dynamic from = child_().Exec(context, input);
            switch (from)
            {
                case string vs:
                    switch (type_)
                    {
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

        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            if (x.AnyArgNull())
            {
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);
            }

            Value val = Exec(null, null);

            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }
}

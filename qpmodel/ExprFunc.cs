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
            ColumnType ctype;
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

    public partial class FuncExpr : Expr
    {
        internal string funcName_;
        internal int argcnt_;
        internal bool isSRF_ = false; // set returning function
        // If true, NULL in any argument causes the function to return NULL.
        // Functions like COALESCE override this to false.
        internal bool propagateNull_ = true;

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

        static public FuncExpr BuildFuncExpr(string funcName, List<Expr> args, bool hasStar = false)
        {
            var func = funcName.Trim().ToLower();

            FuncExpr r;
            switch (func)
            {
                case "sum": r = new AggSum(args); break;
                case "min": r = new AggMin(args); break;
                case "max": r = new AggMax(args); break;
                case "avg": r = new AggAvg(args); break;
                case "stddev_samp": r = new AggStddevSamp(args); break;
                case "count":
                    if (args.Count == 0)
                    {
                        if (!hasStar)
                            throw new SemanticAnalyzeException("count() requires an argument or count(*)");
                        r = new AggCountStar(null);
                    }
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
                throw new SemanticAnalyzeException($"{funcName}() expects {r.argcnt_} argument(s), got {args.Count}");

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
            if (obj is ExprRef oe)
                return Equals(oe.expr_());
            if (obj is FuncExpr of)
                return funcName_.Equals(of.funcName_) && args_().SequenceEqual(of.args_());
            return false;
        }
        public override string ToString() => $"{funcName_}({string.Join(",", args_())})";

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
            {
                var val = args_()[i].Exec(context, input);
                if (val is null)
                    return null;
                args.Add(val);
            }
            return argcnt_ switch
            {
                0 => fncode(),
                1 => fncode(args[0]),
                2 => fncode(args[0], args[1]),
                3 => fncode(args[0], args[1], args[2]),
                _ => throw new NotImplementedException(),
            };
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
            Debug.Assert(type_ is CharType || type_ is VarCharType || this.AnyArgNull());
        }

        public override Value Exec(ExecContext context, Row input)
        {
            string str = (string)args_()[0].Exec(context, input);
            Value startVal = args_()[1].Exec(context, input);
            Value endVal = args_()[2].Exec(context, input);

            if (str is null || startVal is null || endVal is null)
                return null;
            int start = (int)startVal - 1;
            int end = (int)endVal - 1;
            // SQL allows substr() function go beyond length, guard it
            if (start < 0) start = 0;
            if (start >= str.Length) return "";
            return str.Substring(start, Math.Min(end - start + 1, str.Length - start));
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
            Debug.Assert(type_ is CharType || type_ is VarCharType || this.AnyArgNull());
        }
        public override Value Exec(ExecContext context, Row input)
        {
            string str = (string)args_()[0].Exec(context, input);
            if (str is null)
                return null;
            return str.ToUpper();
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
            Debug.Assert(type_ is CharType || type_ is VarCharType || this.AnyArgNull());
        }

        public override Value Exec(ExecContext context, Row input)
        {
            string str = (string)args_()[0].Exec(context, input);
            if (str is null)
                return null;
            Value timesVal = args_()[1].Exec(context, input);
            if (timesVal is null)
                return null;
            int times = (int)timesVal;
            if (times <= 0) return "";

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
            Value decimalsVal = args_()[1].Exec(context, input);

            if (number is null || decimalsVal is null)
                return null;
            int decimals = (int)decimalsVal;

            // there are multiple Math.Round(), an integer number confuses them
            var type = args_()[0].type_;
            if (type is IntType)
                return Math.Round((decimal)number, decimals);
            else
                return Math.Round((double)number, decimals);
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
            if (number is null)
                return null;

            // dispatch to the correct Math.Abs overload based on runtime type
            if (number is int i) return Math.Abs(i);
            if (number is long l) return Math.Abs(l);
            if (number is decimal m) return Math.Abs(m);
            return Math.Abs((double)number);
        }
    }

    public partial class CoalesceFunc : FuncExpr
    {
        public CoalesceFunc(List<Expr> args) : base("coalesce", args)
        {
            if (args.Count < 1) throw new SemanticAnalyzeException("coalesce requires at least 1 argument");
            propagateNull_ = false;
            argcnt_ = args.Count;
        }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            // Use the type of the last argument as the return type
            type_ = args_()[args_().Count - 1].type_;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            foreach (var arg in args_())
            {
                var val = arg.Exec(context, input);
                if (val != null)
                    return val;
            }
            return null;
        }
    }

    public class YearFunc : FuncExpr
    {
        public YearFunc(List<Expr> args) : base("year", args)
        {
            argcnt_ = 1;
            type_ = new IntType();
        }
        public override Value Exec(ExecContext context, Row input)
        {
            var val = arg_().Exec(context, input);
            if (val is null)
                return null;
            DateTime date;
            if (val is DateTime dt)
                date = dt;
            else
                date = DateTime.Parse((string)val);
            return date.Year;
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
            var val = arg_().Exec(context, input);
            if (val is null)
                return null;
            if (val is DateTime dt)
                return dt;
            var date = DateTime.Parse((string)val);
            return date;
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
            dynamic val = arg_().Exec(context, input);
            if (val is null)
                return null;
            int hashval = val.GetHashCode();
            return hashval;
        }
    }

    public abstract class AggFunc : FuncExpr
    {
        public bool isDistinct_;

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

        public virtual Expr SplitAgg()
        {
            // this is for sum, count, and countstar
            var child = child_();
            var processed = new AggSum(new List<Expr> { child.Clone() });
            processed.children_[0] = new AggrRef(this.Clone(), -1);
            processed.dummyBind();
            return processed;
        }

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
            if (old is null)
                sum_ = arg;
            else
            {
                if (!(arg is null))
                {
                    dynamic lv = old;
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
        internal HashSet<Value> distinctSet_;
        public AggCount(List<Expr> args) : base("count", args) { }

        // Distributed count(distinct) can't be split into local+global
        public override Expr SplitAgg() => isDistinct_ ? null : base.SplitAgg();

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = new IntType();
        }

        public override Value Init(ExecContext context, Row input)
        {
            count_ = 0;
            if (isDistinct_)
                distinctSet_ = new HashSet<Value>();
            Accum(context, null, input);
            return count_;
        }

        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);
            if (arg != null)
            {
                count_ = old is null ? 1 : (long)old + 1;
            }
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

            // Add the first table in the scope as tableref of count(*)
            // because adding all of them would make the contain expression
            // to appear to require more than one table when that is really
            // not the case and may lead to attempts create a join or push
            // count(*) to both sides of an existing join.
            // This will let count(*) to be pushed to the correct node and side.
            List<TableRef> trefs = context.AllTableRefs();
            if (trefs.Count > 0)
                tableRefs_.Add(trefs[0]);
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

        public override int GetHashCode() => tableRefs_.ListHashCode();

        // Two COUNT(*) are same/equal only if they reference same set of tables.
        //
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return this.Equals(oe.expr_());
            if (obj is AggCountStar ac)
                return Utils.OrderlessEqual(tableRefs_, ac.tableRefs_);
            return false;
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
        public override Expr SplitAgg()
        {
            var child = child_();
            var processed = new AggMin(new List<Expr> { child.Clone() });
            processed.children_[0] = new AggrRef(this.Clone(), -1);
            processed.dummyBind();
            return processed;
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
        public override Expr SplitAgg()
        {
            var child = child_();
            var processed = new AggMax(new List<Expr> { child.Clone() });
            processed.children_[0] = new AggrRef(this.Clone(), -1);
            processed.dummyBind();
            return processed;
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

        public override Expr SplitAgg()
        {
            var child = child_();

            // child of tsum/tcount will be replace to bypass aggfunc child during aggfunc initialization
            var tsum = new AggSum(new List<Expr> { child }); tsum.dummyBind();
            var sumchild = new AggSum(new List<Expr> { child.Clone() }); sumchild.dummyBind();
            var sumchildref = new AggrRef(sumchild, -1);
            tsum.children_[0] = sumchildref;

            var tcount = new AggSum(new List<Expr> { child }); tcount.dummyBind();
            var countchild = new AggCount(new List<Expr> { child.Clone() }); countchild.dummyBind();
            var countchildref = new AggrRef(countchild, -1);
            tcount.children_[0] = countchildref;

            var processed = new BinExpr(tsum, tcount, "/");
            processed.dummyBind();
            return processed;
        }

        public override Value Init(ExecContext context, Row input)
        {
            pair_ = new AvgPair
            {
                sum_ = arg_().Exec(context, input)
            };
            pair_.count_ = pair_.sum_ is null ? 0 : 1;
            return pair_;
        }
        public override Value Accum(ExecContext context, Value old, Row input)
        {
            var arg = arg_().Exec(context, input);

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
                if (arg != null)
                {
                    dynamic lv = oldpair.sum_;
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
                    // Exclude null values from the calculation per SQL standard
                    var nonNullVals = vals_.Where(x => x != null).ToList();
                    int n = nonNullVals.Count;
                    if (n > 1)
                    {
                        dynamic sum = 0.0; nonNullVals.ForEach(x => sum += x);
                        var mean = sum / n;
                        dynamic stddev = 0.0; nonNullVals.ForEach(x => stddev +=
                                        (x - mean) * (x - mean));
                        stddev = Math.Sqrt(stddev / (n - 1));
                        stddev_ = stddev;
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
        public override Expr SplitAgg() => null;
        public override Value Finalize(ExecContext context, Value old) => (old as AggStddevValues).Finalize();
    }

    // case <eval>
    //      when <when0> then <then0>
    //      when <when1> then <then1>
    //      else <else>
    //  end;
    public partial class CaseExpr : Expr
    {
        internal int nEval_ = 0;
        internal int nWhen_;
        internal int nElse_ = 0;

        public override string ToString() => $"case with {nEval_}|{when_().Count}|{nElse_}";
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
                // execute simple case: CASE eval WHEN w1 THEN t1 ...
                // null eval never matches any WHEN (null = anything is unknown)
                var eval = eval_().Exec(context, input);
                if (eval != null)
                {
                    for (int i = 0; i < when_().Count; i++)
                    {
                        if (eval.Equals(when_()[i].Exec(context, input)))
                            return then_()[i].Exec(context, input);
                    }
                }
            }
            else
            {
                // execute searched case
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

    public partial class UnaryExpr : Expr
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
        public override int GetHashCode() => op_.GetHashCode() ^ arg_().GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return Equals(oe.expr_());
            if (obj is UnaryExpr uo)
                return op_.Equals(uo.op_) && arg_().Equals(uo.arg_());
            return false;
        }
        public override object Exec(ExecContext context, Row input)
        {
            Value arg = arg_().Exec(context, input);
            if (arg is null)
                return null;
            return op_ switch
            {
                "-" => -(dynamic)arg,
                "!" => !(bool)arg,
                _ => arg,
            };
        }
    }

    // we can actually put all binary ops in BinExpr class but we want to keep
    // some special ones (say AND/OR) so we can coding easier
    //
    public partial class BinExpr : Expr
    {
        internal string op_;
        internal bool isMarkerBinExpr_ = false;
        public bool IsMarkerBinExpr() => isMarkerBinExpr_;
        public override int GetHashCode() => lchild_().GetHashCode() ^ rchild_().GetHashCode() ^ op_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ExprRef oe)
                return this.Equals(oe.expr_());
            else if (obj is BinExpr bo)
                return exprEquals(lchild_(), bo.lchild_()) && exprEquals(rchild_(), bo.rchild_()) && op_.Equals(bo.op_);
            return false;
        }
        public BinExpr(Expr l, Expr r, string op, bool isMarkerBinExpr = false) : base()
        {
            children_.Add(l);
            children_.Add(r);
            op_ = op.ToLower();
            isMarkerBinExpr_ = isMarkerBinExpr;
            Debug.Assert(Clone().Equals(this));
        }

        public static BinExpr MakeBooleanExpr(Expr l, Expr r, string op, bool isMarkerBinExpr = false)
        {
            Debug.Assert(l.bounded_ && r.bounded_);
            var expr = new BinExpr(l, r, op, isMarkerBinExpr);
            expr.ResetAggregateTableRefs();
            expr.markBounded();
            expr.type_ = new BoolType();
            return expr;
        }

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
                    else if (TypeBase.OnlyOneIsStringType(lchild_().type_, rchild_().type_))
                        throw new SemanticAnalyzeException("no implicit conversion of character type values");
                    type_ = new BoolType();
                    break;
                case "=":
                case "<>":
                case "!=":
                    if (TypeBase.IsNumberType(lchild_().type_))
                        ColumnType.CoerseType(op_, lchild_(), rchild_());
                    else if (TypeBase.OnlyOneIsStringType(lchild_().type_, rchild_().type_))
                        throw new SemanticAnalyzeException("no implicit conversion of character type values");
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
            return op switch
            {
                ">" => "<",
                ">=" => "<=",
                "<" => ">",
                "<=" => ">=",
                "in" => throw new InvalidProgramException("not switchable"),
                _ => op,
            };
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
            switch (op_)
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
            string[] nullops = { "is", "is not" };
            dynamic lv = lchild_().Exec(context, input);
            dynamic rv = rchild_().Exec(context, input);

            if (!nullops.Contains(op_) && (lv is null || rv is null))
                return null;

            switch (op_)
            {
                // we can do a compile type type coerce for addition/multiply etc to align
                // data types, say double+decimal will require both side become decimal.
                // However, for comparison, we can't do that, because that depends the actual
                // value: decimal has better precision and double has better range, if double
                // if out of decimal's range, we shall convert both to double; otherwise they
                // shall be converted to decimals.
                //
                // We do a simplification here by forcing type coerce for any op at Bind().
                //
                case "+": return lv + rv;
                case "-": return lv - rv;
                case "*": return lv * rv;
                case "/":
                    if (rv == 0) throw new SemanticAnalyzeException("division by zero");
                    return lv / rv;
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
                case " or ": return lv || rv;
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
            string code = op_ switch
            {
                "+" => $"({lv} + {rv})",
                "-" => $"({lv} - {rv})",
                "*" => $"({lv} * {rv})",
                "/" => $"({lv} / {rv})",
                ">" => $"({lv} > {rv})",
                ">=" => $"({lv} >= {rv})",
                "<" => $"({lv} < {rv})",
                "<=" => $"({lv} <= {rv})",
                "=" => $"({lv} == {rv})",
                " and " => $"((bool){lv} && (bool){rv})",
                _ => throw new NotImplementedException(),
            };
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

        // SQL three-valued logic for AND:
        //   false AND null = false, true AND null = null, null AND null = null
        public override Value Exec(ExecContext context, Row input)
        {
            Value lv = lchild_().Exec(context, input);
            if (lv is bool lb && !lb) return false;

            Value rv = rchild_().Exec(context, input);
            if (rv is bool rb && !rb) return false;

            if (lv is null || rv is null) return null;
            return (bool)lv && (bool)rv;
        }

        // a AND (b OR c) AND d => [a, b OR c, d]
        //
    }

    public class LogicOrExpr : LogicAndOrExpr
    {
        public LogicOrExpr(Expr l, Expr r) : base(l, r, " or ") { }

        // SQL three-valued logic for OR:
        //   true OR null = true, false OR null = null, null OR null = null
        public override Value Exec(ExecContext context, Row input)
        {
            Value lv = lchild_().Exec(context, input);
            if (lv is bool lb && lb) return true;

            Value rv = rchild_().Exec(context, input);
            if (rv is bool rb && rb) return true;

            if (lv is null || rv is null) return null;
            return (bool)lv || (bool)rv;
        }
    }

    public partial class CastExpr : Expr
    {
        public override string ToString() => $"cast({child_()} to {type_})";
        public CastExpr(Expr child, ColumnType coltype) : base() { children_.Add(child); type_ = coltype; }
        public override Value Exec(ExecContext context, Row input)
        {
            dynamic from = child_().Exec(context, input);
            if (from is null)
                return null;

            switch (type_)
            {
                case IntType _:
                    return Convert.ToInt32(from);
                case DoubleType _:
                    return Convert.ToDouble(from);
                case NumericType _:
                    return Convert.ToDecimal(from);
                case DateTimeType _:
                    if (from is string s)
                        return DateTime.Parse(s);
                    return Convert.ToDateTime(from);
                case CharType _:
                case VarCharType _:
                    return from.ToString();
                case BoolType _:
                    return Convert.ToBoolean(from);
                default:
                    return from;
            }
        }
    }
}

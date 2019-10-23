using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Value = System.Int64;

namespace adb
{
    // It carries info needed by expression binding, it includes
    //  - tablerefs, so we can lookup column names
    //  - parent bind context (if it is a subquery)
    // 
    public class BindContext
    {
        // number of subqueries in the whole query
        internal static int globalSubqCounter_;

        // bounded tables/subqueries: <seq#, tableref>
        internal readonly Dictionary<int, TableRef> boundFrom_ = new Dictionary<int, TableRef>();

        // current statement
        internal readonly SQLStatement stmt_;

        // parent bind context - non-null for subquery only
        internal readonly BindContext parent_;

        public BindContext(SQLStatement current, BindContext parent)
        {
            stmt_ = current;
            parent_ = parent;
            if (parent is null)
                globalSubqCounter_ = 0;
        }

        // table APIs
        //
        public void AddTable(TableRef tab) => boundFrom_.Add(boundFrom_.Count, tab);
        public List<TableRef> AllTableRefs() => boundFrom_.Values.ToList();
        public TableRef Table(string alias) => boundFrom_.Values.FirstOrDefault(x => x.alias_.Equals(alias));
        public int TableIndex(string alias)
        {
            var pair = boundFrom_.FirstOrDefault(x => x.Value.alias_.Equals(alias));
            if (default(KeyValuePair<int, TableRef>).Equals(pair))
                return -1;
            return pair.Key;
        }

        // column APIs
        //
        TableRef locateByColumnName(string colAlias)
        {
            var result = AllTableRefs().FirstOrDefault(x => x.LocateColumn(colAlias) != null);
            if (result != AllTableRefs().LastOrDefault(x => x.LocateColumn(colAlias) != null))
                throw new SemanticAnalyzeException("ambigous column name");
            return result;
        }
        public TableRef GetTableRef(string tabAlias, string colAlias)
        {
            if (tabAlias is null)
                return locateByColumnName(colAlias);
            else
                return Table(tabAlias);
        }
        public int ColumnOrdinal(string tabAlias, string colAlias)
        {
            int r = -1;
            var lc = Table(tabAlias).AllColumnsRefs();
            for (int i = 0; i < lc.Count; i++)
            {
                if (lc[i].alias_.Equals(colAlias))
                {
                    r = i;
                    break;
                }
            }

            if (Table(tabAlias) is BaseTableRef bt)
            {
                Debug.Assert(r == Catalog.systable_.Column(bt.relname_, colAlias).ordinal_);
            }

            if (r != -1)
                return r;
            throw new SemanticAnalyzeException($"column not exists {tabAlias}.{colAlias}");
        }
    }

    public static class ExprHelper
    {
        public static Expr AndListToExpr(List<Expr> andlist)
        {
            Debug.Assert(andlist.Count >= 1);
            if (andlist.Count == 1)
                return andlist[0];
            else
            {
                var andexpr = new LogicAndExpr(andlist[0], andlist[1]);
                for (int i = 2; i < andlist.Count; i++)
                    andexpr.l_ = new LogicAndExpr(andexpr.l_, andlist[i]);
                return andexpr;
            }
        }

        public static List<ColExpr> AllColExpr(Expr expr, bool includingParameters = false)
        {
            var list = new HashSet<ColExpr>();
            expr.VisitEachExpr(x =>
            {
                if (x is ColExpr xc)
                {
                    if (includingParameters || !xc.isOuterRef_)
                        list.Add(xc);
                }
            });

            return list.ToList();
        }

        public static List<ColExpr> AllColExpr(List<Expr> exprs, bool includingParameters = false)
        {
            var list = new List<ColExpr>();
            exprs.ForEach(x => list.AddRange(AllColExpr(x, includingParameters)));
            return list.Distinct().ToList();
        }

        public static List<TableRef> AllTableRef(Expr expr)
        {
            var list = new HashSet<TableRef>();
            expr.VisitEachExpr(x =>
            {
                if (x is ColExpr xc)
                    list.Add(xc.tabRef_);
            });

            return list.ToList();
        }

        public static string PrintExprWithSubqueryExpanded(Expr expr, int depth)
        {
            string r = "";
            // append the subquery plan align with expr
            if (expr.HasSubQuery())
            {
                r += "\n";
                expr.VisitEachExpr(x =>
                {
                    if (x is SubqueryExpr sx)
                    {
                        r += Utils.Tabs(depth + 2) + $"<{sx.GetType().Name}> {sx.subqueryid_}\n";
                        Debug.Assert(sx.query_.bindContext_ != null);
                        if (sx.query_.physicPlan_ != null)
                            r += $"{sx.query_.physicPlan_.PrintString(depth + 4)}";
                        else
                            r += $"{sx.query_.logicPlan_.PrintString(depth + 4)}";
                    }
                });
            }

            return r;
        }

        // this is a hack - shall be removed after all optimization process in place
        public static void SubqueryDirectToPhysic(Expr expr)
        {
            // append the subquery plan align with expr
            expr.VisitEachExpr(x =>
            {
                if (x is SubqueryExpr sx)
                {
                    Debug.Assert(expr.HasSubQuery());
                    var query = sx.query_;
                    ProfileOption poption = query.TopStmt().profileOpt_;
                    query.physicPlan_ = query.logicPlan_.DirectToPhysical(poption);
                }
            });
        }
    }

    public class Expr
    {
        // Expression in selection list can have an alias
        // e.g, a.i+b.i as total
        //
        internal string alias_;

        // we require some columns for query processing but user may not want 
        // them in the final output, so they are marked as invisible.
        // This includes:
        // 1. subquery's outerref 
        //
        internal bool isVisible_ = true;

        // an expression can reference multiple tables
        //      e.g., a.i + b.j > [a.]k => references 2 tables
        // it is a sum of all its children
        //
        public List<TableRef> tableRefs_ = new List<TableRef>();
        internal bool bounded_;

        public int TableRefCount()
        {
            Debug.Assert(tableRefs_.Distinct().Count() == tableRefs_.Count);
            return tableRefs_.Count;
        }
        public bool EqualTableRef(TableRef tableRef)
        {
            Debug.Assert(bounded_);
            Debug.Assert(TableRefCount() == 1);
            return tableRefs_[0].Equals(tableRef);
        }

        public bool EqualTableRefs(List<TableRef> tableRefs)
        {
            Debug.Assert(bounded_);
            Debug.Assert(TableRefCount() <= tableRefs.Count);
            foreach (var v in tableRefs_)
                if (!tableRefs.Contains(v))
                    return false;
            return true;
        }

        // this one uses c# reflection
        // Similar to PlanNode.VisitEachNodeExists()
        //
        public bool VisitEachExprExists(Func<Expr, bool> callback)
        {
            bool r = callback(this);

            if (!r)
            {
                var members = GetType().GetFields();
                foreach (var v in members)
                {
                    if (v.FieldType == typeof(Expr))
                    {
                        var m = v.GetValue(this) as Expr;
                        if (m.VisitEachExprExists(callback))
                            return true;
                    }
                    // if container<Expr> we shall also handle
                }
                return false;
            }
            return true;
        }

        public void VisitEachExpr(Action<Expr> callback)
        {
            callback(this);
            var members = GetType().GetFields();
            foreach (var v in members)
            {
                if (v.FieldType == typeof(Expr))
                {
                    var m = v.GetValue(this) as Expr;
                    m.VisitEachExpr(callback);
                }
                else if (v.FieldType == typeof(List<Expr>))
                {
                    var m = v.GetValue(this) as List<Expr>;
                    m.ForEach(x => x.VisitEachExpr(callback));
                }
                // no other containers currently handled
            }
        }

        // TODO: this is kinda redundant, since this check does not save us any time
        public bool HasSubQuery() => VisitEachExprExists(e => e is SubqueryExpr);
        public bool IsConst()
        {
            return !VisitEachExprExists(e =>
            {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is FuncExpr || e is SubqueryExpr;
                return nonconst;
            });
        }

        // APIs children may implment
        //  - we have to copy out expressions - consider the following query
        // select a2 from(select a3, a1, a2 from a) b
        // PhysicSubquery <b>
        //    Output: b.a2[0]
        //  -> PhysicGet a
        //      Output: a.a2[1]
        // notice b.a2 and a.a2 are the same column but have differenti ordinal.
        // This means we have to copy ColExpr, so its parents, then everything.
        //
        public virtual Expr Clone()
        {
            var n = this.MemberwiseClone();
            Debug.Assert(n.Equals(this));
            // tableRefs_ is not actually cloned but it is ok since nobody changes
            // it after fixed. There are some complications in ColExpr.Bind() though.
            //
            return (Expr)n;
        }

        // In current expression, search and replace @from with @to 
        public Expr SearchReplace(Expr from, Expr to) {
            var clone = Clone();

            if (clone.Equals(from))
                clone = to;
            var members = clone.GetType().GetFields();
            foreach (var v in members)
            {
                if (v.FieldType == typeof(Expr))
                {
                    var m = v.GetValue(this) as Expr;
                    var n = m.SearchReplace(from, to);
                    v.SetValue(clone, n);
                }
                else if (v.FieldType == typeof(List<Expr>))
                {
                    var newl = new List<Expr>();
                    var m = v.GetValue(this) as List<Expr>;
                    m.ForEach(x => newl.Add(x.SearchReplace(from, to)));
                    v.SetValue(clone, newl);
                }
                // no other containers currently handled
            }

            return clone;
        }

        public override int GetHashCode() => tableRefs_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (!(obj is Expr)) 
                return false;
            var n = obj as Expr;
            return tableRefs_.Equals(n.tableRefs_);
        }
        public virtual void Bind(BindContext context) => bounded_ = true;
        public virtual string PrintString(int depth) => ToString();
        public virtual Value Exec(ExecContext context, Row input) => throw new Exception($"{this} subclass shall implment Exec()");
    }

    // Represents "*" or "table.*" - it is not in the tree after Bind(). 
    // To avoid confusion, we implment Expand() instead of Bind().
    //
    public class SelStar : Expr
    {
        internal readonly string tabAlias_;

        public SelStar(string tabAlias) => tabAlias_ = tabAlias;
        public override string ToString() => tabAlias_ + ".*";

        public override void Bind(BindContext context) => throw new InvalidProgramException("shall be expanded already");
        internal List<Expr> Expand(BindContext context)
        {
            var unbounds = new List<Expr>();
            var exprs = new List<Expr>();
            if (tabAlias_ is null)
            {
                // *
                context.AllTableRefs().ForEach(x =>
                {
                    // subquery's shall be bounded already, and only * from basetable 
                    // are not bounded. We don't have to differentitate them, but I 
                    // just try to be strict.
                    //
                    if (x is FromQueryRef)
                        exprs.AddRange(x.AllColumnsRefs());
                    else
                        unbounds.AddRange(x.AllColumnsRefs());
                });
            }
            else
            {
                // table.* - you have to find it in current context
                var x = context.Table(tabAlias_);
                if (x is FromQueryRef)
                    exprs.AddRange(x.AllColumnsRefs());
                else
                    unbounds.AddRange(x.AllColumnsRefs());
            }

            unbounds.ForEach(x => x.Bind(context));
            exprs.AddRange(unbounds);
            return exprs;
        }
    }

    // case 1: [A.a1] select A.a1 from (select ...) A;
    //   -- A.a1 tabRef_ is SubqueryRef
    // [A.a1] select * from A where exists (select ... from B where B.b1 = A.a1); 
    //   -- A.a1 is OuterRef_
    //
    public class ColExpr : Expr
    {
        // parse info
        internal string dbName_;
        internal string tabName_;
        internal readonly string colName_;

        // bound info
        internal TableRef tabRef_;
        internal bool isOuterRef_;
        internal int ordinal_;          // which column in children's output

        // -- execution section --

        public ColExpr(string dbName, string tabName, string colName)
        {
            dbName_ = dbName; tabName_ = tabName; colName_ = colName; alias_ = colName;
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(tabRef_ is null);

            // if table name is not given, search through all tablerefs
            isOuterRef_ = false;
            tabRef_ = context.GetTableRef(tabName_, colName_);

            // we can't find the column in current context, so it could an outer reference
            if (tabRef_ is null)
            {
                // can't find in my current context, try my ancestors
                BindContext parent = context;
                while ((parent = parent.parent_) != null)
                {
                    tabRef_ = parent.GetTableRef(tabName_, colName_);
                    if (tabRef_ != null)
                    {
                        // we are actually switch the context to parent, whichTab_ is not right ...
                        isOuterRef_ = true;
                        tabRef_.outerrefs_.Add(this);
                        context = parent;
                        break;
                    }
                }
                if (tabRef_ is null)
                    throw new Exception($"can't bind column '{colName_}' to table");
            }

            Debug.Assert(tabRef_ != null);
            if (tabName_ is null)
                tabName_ = tabRef_.alias_;
            if (!isOuterRef_) {
                // group by <1> may reference a select item, and it has bound - I try to avoid this
                // by clone the item but Expr.Clone() does not clone TableRef_ as it is not change
                // so let's make a compromise here
                //
                Debug.Assert(tableRefs_.Count == 0 
                    || (tableRefs_.Count == 1 && tabRef_.Equals(tableRefs_[0])));
                tableRefs_.Clear();
                tableRefs_.Add(tabRef_);
            }
            ordinal_ = context.ColumnOrdinal(tabName_, colName_);
            bounded_ = true;
        }

        public override int GetHashCode() => (tabName_ + colName_).GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ColExpr co)
            {
                if (co.tabName_ is null)
                    return tabName_ is null && co.colName_.Equals(colName_);
                else
                    return co.tabName_.Equals(tabName_) && co.colName_.Equals(colName_);
            }
            return false;
        }
        public override string ToString()
        {
            string para = isOuterRef_ ? "?" : "";
            para += isVisible_ ? "" : "#";
            if (colName_.Equals(alias_))
                return $@"{para}{tabName_}.{colName_}[{ordinal_}]";
            return $@"{para}{tabName_}.{colName_} [{alias_}][{ordinal_}]";
        }
        public override Value Exec(ExecContext context, Row input)
        {
            if (isOuterRef_)
                return context.GetParam(tabRef_, ordinal_);
            else
                return input.values_[ordinal_];
        }
    }

    public class FuncExpr : Expr
    {
        public string funcName_;
        public List<Expr> args_;

        public FuncExpr(string funcName, List<Expr> args)
        {
            funcName_ = funcName;
            args_ = args;
        }

        public override void Bind(BindContext context)
        {
            args_.ForEach(x=> {
                x.Bind(context);
                tableRefs_.AddRange(x.tableRefs_);
            });
            tableRefs_ = tableRefs_.Distinct().ToList();
        }

        // sum(min(x)) => x
        public List<Expr> GetNonFuncExprList() {
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
        static public FuncExpr BuildFuncExpr(string funcName, List<Expr> args) {
            FuncExpr r = null;
            var func = funcName.Trim().ToLower();

            switch (func)
            {
                case "sum":
                    Utils.Checks(args.Count == 1, "one argument is expected");
                    r = new AggSum(args[0]);
                    break;
                case "min":
                    Utils.Checks(args.Count == 1, "one argument is expected");
                    r = new AggMin(args[0]);
                    break;
                case "max":
                    Utils.Checks(args.Count == 1, "one argument is expected");
                    r = new AggMax(args[0]);
                    break;
                case "count":
                    Utils.Checks(args.Count == 1, "one argument is expected");
                    r = new AggCount(args[0]);
                    break;
                default:
                    r = new FuncExpr(funcName, args);
                    break;
            }

            return r;
        }

        public override Expr Clone()
        {
            var n = (FuncExpr)base.Clone();
            var argclone = new List<Expr>();
            args_.ForEach(x=>argclone.Add(x.Clone()));
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
            if (obj is FuncExpr of) {
                return funcName_.Equals(of.funcName_) && args_.SequenceEqual(of.args_);
            }
            return false;
        }
        public override string ToString() => $"{funcName_}({string.Join(",",args_)})";
    }

    public abstract class AggFunc : FuncExpr {
        public AggFunc(string func, List<Expr> args) : base(func, args) {}

        public override Value Exec(ExecContext context, Row input)
        {
            return 0;
        }
        public virtual void Init(ExecContext context, Row input) { }
        public virtual void Accum(ExecContext context, Value old, Row input) { }
    }

    public class AggSum : AggFunc {
        // Exec info
        internal Value sum_;
        public AggSum(Expr arg) : base("sum", new List<Expr> { arg}) { }

        public override void Init(ExecContext context, Row input) => sum_ = args_[0].Exec(context, input);
        public override void Accum(ExecContext context, Value old, Row input) => sum_ = old + args_[0].Exec(context, input);
        public override long Exec(ExecContext context, Row input) => sum_;
    }

    public class AggCount : AggFunc
    {
        // Exec info
        internal Value count_;
        public AggCount(Expr arg) : base("count", new List<Expr> { arg }) { }

        public override void Init(ExecContext context, Row input) => count_ = 1;
        public override void Accum(ExecContext context, Value old, Row input) => count_ += 1;
        public override long Exec(ExecContext context, Row input) => count_;
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
        public override long Exec(ExecContext context, Row input) => min_;
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
        public override long Exec(ExecContext context, Row input) => max_;
    }

    // we can actually put all binary ops in BinExpr class but we want to keep 
    // some special ones (say AND/OR) so we can coding easier
    //
    public class BinExpr : Expr
    {
        public Expr l_;
        public Expr r_;
        public string op_;

        public override int GetHashCode() => l_.GetHashCode() ^ r_.GetHashCode() ^ op_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is BinExpr bo) {
                return l_.Equals(bo.l_) && r_.Equals(bo.r_) && op_.Equals(bo.op_);
            }
            return false;
        }
        public override Expr Clone()
        {
            var n = (BinExpr)base.Clone();
            n.l_ = l_.Clone();
            n.r_ = r_.Clone();
            Debug.Assert(n.Equals(this));
            return n;
        }
        public BinExpr(Expr l, Expr r, string op)
        {
            l_ = l; r_ = r; op_ = op;
        }

        public override void Bind(BindContext context)
        {
            l_.Bind(context);
            r_.Bind(context);

            tableRefs_.AddRange(l_.tableRefs_); tableRefs_.AddRange(r_.tableRefs_);
            if (tableRefs_.Count>1)
                tableRefs_ = tableRefs_.Distinct().ToList();
            bounded_ = true;
        }

        public override string ToString() => $"{l_}{op_}{r_}";

        public override Value Exec(ExecContext context, Row input)
        {
            switch (op_)
            {
                case "+": return l_.Exec(context, input) + r_.Exec(context, input);
                case "-": return l_.Exec(context, input) - r_.Exec(context, input);
                case "*": return l_.Exec(context, input) * r_.Exec(context, input);
                case "/": return l_.Exec(context, input) / r_.Exec(context, input);
                case ">": return l_.Exec(context, input) > r_.Exec(context, input) ? 1 : 0;
                case ">=": return l_.Exec(context, input) >= r_.Exec(context, input) ? 1 : 0;
                case "<": return l_.Exec(context, input) < r_.Exec(context, input) ? 1 : 0;
                case "<=": return l_.Exec(context, input) <= r_.Exec(context, input) ? 1 : 0;
                case "=": return l_.Exec(context, input) == r_.Exec(context, input) ? 1 : 0;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    public class LogicAndExpr : BinExpr
    {
        public LogicAndExpr(Expr l, Expr r) : base(l, r, " and ") { }

        public override Value Exec(ExecContext context, Row input)
        {
            Value lv = l_.Exec(context, input);
            Value rv = r_.Exec(context, input);

            if (lv == 1 && rv == 1)
                return 1;
            return 0;
        }

        internal List<Expr> BreakToList()
        {
            var andlist = new List<Expr>();
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? l_ : r_;
                if (e is LogicAndExpr ea)
                    andlist.AddRange(ea.BreakToList());
                else
                    andlist.Add(e);
            }

            return andlist;
        }
    }

    public class SubqueryExpr : Expr
    {
        public SelectStmt query_;
        public int subqueryid_; // bound

        public SubqueryExpr(SelectStmt query) { query_ = query; }
        // don't print the subquery here, it shall be printed by up caller layer for pretty format
        public override string ToString() => $@"@{subqueryid_}";

        public override void Bind(BindContext context)
        {
            // subquery id is global, so accumulating at top
            subqueryid_ = ++BindContext.globalSubqCounter_;

            // query will use a new query context inside
            var mycontext = query_.Bind(context);
            Debug.Assert(query_.parent_ == mycontext.parent_?.stmt_);

            // verify column count after bound because SelStar expansion
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            bounded_ = true;
        }


        public override Value Exec(ExecContext context, Row input)
        {
            Row r = null;
            query_.physicPlan_.Exec(context, l =>
            {
                if (r is null)
                    r = l;
                else
                    throw new SemanticExecutionException("subquery more than one row returned");
                return null;
            });

            if (r is null)
                return Value.MaxValue;
            return r.values_[0];
        }
    }

    public class CteExpr : Expr
    {
        public string tabName_;
        public List<string> colNames_;
        public SQLStatement query_;

        public CteExpr(string tabName, List<string> colNames, SQLStatement query)
        {
            tabName_ = tabName; colNames_ = colNames; query_ = query;
        }
    }

    public class OrderTerm : Expr
    {
        public Expr expr_;
        bool descend_;

        public OrderTerm(Expr expr, bool descend)
        {
            expr_ = expr; descend_ = descend;
        }
    }

    public class LiteralExpr : Expr
    {
        public SQLiteParser.Literal_valueContext val_;

        public LiteralExpr(SQLiteParser.Literal_valueContext val) => val_ = val;
        public override string ToString() => val_.GetText();
        public override Value Exec(ExecContext context, Row input) => Value.Parse(val_.GetText());

        public override int GetHashCode() => val_.GetText().GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LiteralExpr lo) {
                return val_.GetText().Equals(lo.val_.GetText());
            }
            return false;
        }
    }

    // runtime only used to reference an expr as a whole without recomputation
    public class ExprRef : Expr {
        readonly internal Expr expr_;
        readonly internal int ordinal_;

        public override string ToString() => $@"{{{Utils.RemovePositions(expr_.ToString())}}}[{ordinal_}]";
        public ExprRef(Expr expr, int ordinal) {
            expr_ = expr; ordinal_ = ordinal;
        }
        public override Value Exec(ExecContext context, Row input) => input.values_[ordinal_];
    }

}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

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
        public int ColumnOrdinal(string tabAlias, string colAlias, out ColumnType type)
        {
            int r = -1;
            var lc = Table(tabAlias).AllColumnsRefs();
            type = null;
            for (int i = 0; i < lc.Count; i++)
            {
                if (lc[i].alias_.Equals(colAlias))
                {
                    r = i;
                    type = lc[i].type_;
                    break;
                }
            }

            if (Table(tabAlias) is BaseTableRef bt)
                Debug.Assert(r == Catalog.systable_.Column(bt.relname_, colAlias).ordinal_);
            if (r != -1)
            {
                Debug.Assert(type != null);
                return r;
            }
            throw new SemanticAnalyzeException($"column not exists {tabAlias}.{colAlias}");
        }
    }

    public static class ExprHelper
    {
        // a List<Expr> conditions merge into a LogicAndExpr
        public static Expr AndListToExpr(List<Expr> andlist)
        {
            Debug.Assert(andlist.Count >= 1);
            if (andlist.Count == 1)
                return andlist[0];
            else
            {
                var andexpr = new LogicAndExpr(andlist[0], andlist[1]);
                for (int i = 2; i < andlist.Count; i++)
                    andexpr.children_[0] = new LogicAndExpr(andexpr.l_(), andlist[i]);
                return andexpr;
            }
        }

        public static List<Expr> CloneList(List<Expr> source, List<Type> excludes = null)
        {
            var clone = new List<Expr>();
            if (excludes is null)
            {
                source.ForEach(x => clone.Add(x.Clone()));
                Debug.Assert(clone.SequenceEqual(source));
            }
            else
            {
                source.ForEach(x =>
                {
                    if (!excludes.Contains(x.GetType()))
                        clone.Add(x.Clone());
                });
            }
            return clone;
        }

        public static List<ColExpr> RetrieveAllColExpr(Expr expr, bool includingParameters = false)
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

        public static List<T> RetrieveAllType<T>(Expr expr)
        {
            var list = new List<T>();
            expr.VisitEachExpr(x =>
            {
                if (x is T xc)
                {
                    list.Add(xc);
                }
            });

            return list;
        }

        public static List<ColExpr> RetrieveAllColExpr(List<Expr> exprs, bool includingParameters = false)
        {
            var list = new List<ColExpr>();
            exprs.ForEach(x => list.AddRange(RetrieveAllColExpr(x, includingParameters)));
            return list.Distinct().ToList();
        }

		// TODO: what about expr.tableRefs_?
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

    public static class FilterHelper {
        
        // a > 3 or c > 1, b > 5 =>  (a > 3 or c > 1) and (b > 5)
        public static Expr AddAndFilter(Expr filter, Expr newcond) {
            if (filter is null)
                return newcond.Clone();
            return new LogicAndExpr(filter, newcond.Clone());
        }
        public static void NullifyFilter(LogicNode node)
        {
            node.filter_ = null;
            // we have to keep the LogicFilter in various cases for easier handling 
            // and we leave the vacuum job to filter push down
            //
            if (node is LogicFilter)
                node.filter_ = new LiteralExpr("true");
        }

        public static bool FilterIsConst(Expr filter, out bool trueOrfalse)
        {
            trueOrfalse = false;
            if (filter.TryEvalConst(out Value value)) {
                Debug.Assert(value is bool);
                trueOrfalse = (bool)value;
                return true;
            }
            return false;
        }

        // a>5 => [a > 5]
        // a>5 AND c>7 => [a>5, c>7]
        public static List<Expr> FilterToAndList(Expr filter)
        {
            var andlist = new List<Expr>();
            if (filter is LogicAndExpr andexpr)
                andlist = andexpr.BreakToList();
            else
                andlist.Add(filter);

            return andlist;
        }
    }
    public class Expr
    {
        // Expression in selection list can have an alias
        // e.g, a.i+b.i as total
        //
        internal string alias_;

        // subclass shall only use children_[] to contain the expressions used
        internal List<Expr> children_ = new List<Expr>();

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
        internal List<TableRef> tableRefs_ = new List<TableRef>();
        internal bool bounded_;

        // output type of the expression
        internal ColumnType type_;

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

        public bool TableRefsContainedBy(List<TableRef> tableRefs)
        {
            Debug.Assert(bounded_);
            Debug.Assert(TableRefCount() <= tableRefs.Count);
            return Utils.ListAContainsB(tableRefs, tableRefs_);
        }
        public bool TableRefsEquals(List<TableRef> tableRefs)
        {
            Debug.Assert(bounded_);
            Debug.Assert(TableRefCount() <= tableRefs.Count);
            return Utils.ListAEqualsB(tableRefs, tableRefs_);
        }

        public bool VisitEachExprExists(Func<Expr, bool> check, List<Type> excluding = null)
        {
            if (excluding?.Contains(GetType()) ?? false)
                return false;
            bool r = check(this);

            if (!r)
            {
                foreach (var v in children_)
                {
                    if (v.VisitEachExprExists(check, excluding))
                        return true;
                }
                return false;
            }
            return true;
        }

        public void VisitEachExpr(Action<Expr> callback)
        {
            Func<T, bool> wrapCallbackAsCheck<T>(Action<T> callback)
            {
                return a => { callback(a); return false; };
            }
            Func<Expr, bool> check = wrapCallbackAsCheck(callback);
            VisitEachExprExists(check);
        }

        // TODO: this is kinda redundant, since this check does not save us any time
        public bool HasSubQuery() => VisitEachExprExists(e => e is SubqueryExpr);
        public bool HasAggFunc() => VisitEachExprExists(e => e is AggFunc);
        public bool IsConst()
        {
            return !VisitEachExprExists(e =>
            {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is SubqueryExpr;
                return nonconst;
            });
        }

        public bool TryEvalConst(out Value value)
        {
            value = null;
            if (!IsConst())
                return false;
            value = Exec(null, null);
            return true;
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
            Expr n = (Expr)MemberwiseClone();
            n.children_ = ExprHelper.CloneList(children_);
            n.tableRefs_ = new List<TableRef>();
            tableRefs_.ForEach(n.tableRefs_.Add);
            Debug.Assert(Equals(n));
            return n;
        }

        // In current expression, search and replace @from with @to 
        public Expr SearchReplace<T>(T from, Expr to)
        {
            Debug.Assert(from != null);

            var clone = Clone();
            bool equal = false;
            if (from is Expr)
                equal = from.Equals(clone);
            else if (from is string)
                equal = from.Equals(clone.alias_);
            else
                Debug.Assert(false);
            if (equal)
                clone = to.Clone();
            else
            {
                var newl = new List<Expr>();
                clone.children_.ForEach(x => newl.Add(x.SearchReplace(from, to)));
                clone.children_ = newl;
            }

            return clone;
        }

        protected static bool exprEquals(Expr l, Expr r)
        {
            if (l is null && r is null)
                return true;
            if (l is null || r is null)
                return false;

            Expr le = l, re = r;
            if (l is ExprRef lx)
                le = lx.expr_();
            if (r is ExprRef rx)
                re = rx.expr_();
            Debug.Assert(!(le is ExprRef));
            Debug.Assert(!(re is ExprRef));
            return le.Equals(re);
        }
        protected static bool exprEquals(List<Expr> l, List<Expr> r)
        {
            if (l is null && r is null)
                return true;
            if (l is null || r is null)
                return false;
            if (l.Count != r.Count)
                return false;

            for (int i = 0; i < l.Count; i++)
                if (!exprEquals(l[i], r[i]))
                    return false;
            return true;
        }

        protected void markBounded() {
            Debug.Assert(!bounded_);
            bounded_ = true;
        }
        public override int GetHashCode() => Utils.ListHashCode(tableRefs_) ^ Utils.ListHashCode(children_);
        public override bool Equals(object obj)
        {
            if (!(obj is Expr))
                return false;
            var n = obj as Expr;
            return tableRefs_.SequenceEqual(n.tableRefs_) &&
                children_.SequenceEqual(n.children_);
        }

        public virtual void Bind(BindContext context)
        {
            children_.ForEach(x=> {
                x.Bind(context);
                tableRefs_.AddRange(x.tableRefs_);
            });
            if (tableRefs_.Count > 1)
                tableRefs_ = tableRefs_.Distinct().ToList();

            markBounded();
        }

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
                    if (x is QueryRef)
                        exprs.AddRange(x.AllColumnsRefs());
                    else
                        unbounds.AddRange(x.AllColumnsRefs());
                });
            }
            else
            {
                // table.* - you have to find it in current context
                var x = context.Table(tabAlias_);
                if (x is QueryRef)
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
        internal int ordinal_ = -1;          // which column in children's output

        // -- execution section --

        public ColExpr(string dbName, string tabName, string colName, ColumnType type)
        {
            dbName_ = dbName; tabName_ = tabName; colName_ = colName; alias_ = colName; type_ = type;
            Debug.Assert(Clone().Equals(this));
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
                        (context.stmt_ as SelectStmt).isCorrelated = true;
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
            if (!isOuterRef_)
            {
                Debug.Assert(tableRefs_.Count == 0);
                tableRefs_.Add(tabRef_);
            }
            ordinal_ = context.ColumnOrdinal(tabName_, colName_, out ColumnType type);
            type_ = type;
            markBounded();
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
            else if (obj is ExprRef oe)
                return Equals(oe.children_[0]);
            return false;
        }
        public override string ToString()
        {
            string para = isOuterRef_ ? "?" : "";
            para += isVisible_ ? "" : "#";
            if (colName_.Equals(alias_))
                return ordinal_==-1? $@"{para}{tabName_}.{colName_}":
                    $@"{para}{tabName_}.{colName_}[{ordinal_}]";
            return ordinal_ == -1 ? $@"{para}{tabName_}.{colName_} (as {alias_})":
                $@"{para}{tabName_}.{colName_} (as {alias_})[{ordinal_}]";
        }
        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (isOuterRef_)
                return context.GetParam(tabRef_, ordinal_);
            else
                return input.values_[ordinal_];
        }
    }

    public class CteExpr : Expr
    {
        internal SQLStatement query_;
        internal List<string> colNames_;

        public CteExpr(string tabName, List<string> colNames, SQLStatement query)
        {
            alias_ = tabName;
            query_ = query;
            colNames_ = colNames;
        }
    }

    public class OrderTerm : Expr
    {
        internal bool descend_;

        public override string ToString() => $"{children_[0]} {(descend_ ? "[desc]" : "")}";
        public OrderTerm(Expr expr, bool descend)
        {
            children_.Add(expr); descend_ = descend;
        }
    }

    public class UnaryExpr : Expr {
        internal bool hasNot_;

        internal Expr expr_() => children_[0];
        public UnaryExpr(Expr expr, bool hasNot) {
            hasNot_ = hasNot;
            children_.Add(expr);
        }
        public override string ToString() => $"{(hasNot_?"not ":"")}{expr_()}";
    }

    public class LiteralExpr : Expr
    {
        internal string str_;
        internal Value val_;

        public LiteralExpr(string str)
        {
            str_ = str;
            val_ = str_;

            if (str.StartsWith("date'"))
            {
                var datestr = Utils.RetrieveQuotedString(str);
                val_ = (new DateFunc(
                            new List<Expr> { new LiteralExpr(datestr) })).Exec(null, null);
                type_ = new DateTimeType();
            }
            else if (str.StartsWith("interval'"))
            {
                var datestr = Utils.RetrieveQuotedString(str);
                var day = int.Parse(Utils.RemoveStringQuotes(datestr));
                Debug.Assert(str.EndsWith("day") || str.EndsWith("month") || str.EndsWith("year"));
                if (str.EndsWith("month"))
                    day *= 30;  // FIXME
                else if (str.EndsWith("year"))
                    day *= 365;  // FIXME
                val_ = new TimeSpan(day, 0, 0, 0);
                type_ = new TimeSpanType();
            }
            else if (str.Contains("'"))
            {
                str_ = Utils.RemoveStringQuotes(str_);
                val_ = str_;
                type_ = new CharType(str_.Length);
            }
            else if (str.Contains("."))
            {
                if (double.TryParse(str, out double value))
                {
                    type_ = new DoubleType();
                    val_ = value;
                }
                else
                    throw new SemanticAnalyzeException("wrong double precision format");
            }
            else if (str.Equals("true") || str.Equals("false"))
            {
                val_ = bool.Parse(str);
                type_ = new BoolType();
            }
            else
            {
                if (int.TryParse(str, out int value))
                {
                    type_ = new IntType();
                    val_ = value;
                }
                else
                    throw new SemanticAnalyzeException("wrong integer format");
            }

            Debug.Assert(type_ != null);
            Debug.Assert(val_ != null);
        }
        public override string ToString()
        {
            if (val_ is string)
                return $"'{val_}'";
            else
                return str_;
        }
        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            return val_;
        }

        public override int GetHashCode() => str_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LiteralExpr lo)
            {
                return str_.Equals(lo.str_);
            }
            return false;
        }
    }

    // Runtime only used to reference an expr as a whole without recomputation
    // the tricky part of this class is that it is a wrapper, but Equal() shall be taken care
    // so we invent ExprHelper.Equal() for the purpose
    //
    public class ExprRef : Expr
    {
        // children_[0] can't be an ExprRef again
        internal Expr expr_() => children_[0];
        internal int ordinal_;

        public override string ToString() => $@"{{{Utils.RemovePositions(expr_().ToString())}}}[{ordinal_}]";
        public ExprRef(Expr expr, int ordinal)
        {
            if (expr is ExprRef ee)
                expr = ee.expr_();
            Debug.Assert(!(expr is ExprRef));
            children_.Add(expr); ordinal_ = ordinal;
            type_ = expr.type_;
        }

        public override int GetHashCode() => expr_().GetHashCode();
        public override bool Equals(object obj)
        {
            Debug.Assert(!(expr_() is ExprRef));
            if (obj is ExprRef or)
                return expr_().Equals(or.expr_());
            else
                return (obj is Expr oe) ? expr_().Equals(oe) : false;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            return input.values_[ordinal_];
        }
    }
}

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
                throw new SemanticAnalyzeException($"ambigous column name: {colAlias}");
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
                if (lc[i].outputName_.Equals(colAlias))
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
            expr.VisitEach<ColExpr>(x =>
            {
                if (includingParameters || !x.isOuterRef_)
                    list.Add(x);
            });

            return list.ToList();
        }

        public static List<T> RetrieveAllType<T>(Expr expr) where T: Expr
        {
            var list = new List<T>();
            expr.VisitEach<T>(x => list.Add(x));
            return list;
        }

        public static List<ColExpr> RetrieveAllColExpr(List<Expr> exprs, bool includingParameters = false)
        {
            var list = new List<ColExpr>();
            exprs.ForEach(x => list.AddRange(RetrieveAllColExpr(x, includingParameters)));
            return list.Distinct().ToList();
        }

		// TODO: what about expr.tableRefs_?
        //  They shall be equal but our implmentation sometimes get them inconsistent
        //  for example, xc.tabRef_ can contain outerrefs but tableRefs_ does not.
        public static List<TableRef> AllTableRef(Expr expr)
        {
            var list = new HashSet<TableRef>();
            expr.VisitEach<ColExpr>(x => list.Add(x.tabRef_));
            return list.ToList();
        }

        public static string PrintExprWithSubqueryExpanded(Expr expr, int depth)
        {
            string r = "";
            // append the subquery plan align with expr
            if (expr.HasSubQuery())
            {
                r += "\n";
                expr.VisitEach<SubqueryExpr>(x =>
                {
                    r += Utils.Tabs(depth + 2) + $"<{x.GetType().Name}> {x.subqueryid_}\n";
                    Debug.Assert(x.query_.bindContext_ != null);
                    if (x.query_.physicPlan_ != null)
                        r += $"{x.query_.physicPlan_.PrintString(depth + 4)}";
                    else
                        r += $"{x.query_.logicPlan_.PrintString(depth + 4)}";
                });
            }

            return r;
        }

        // this is a hack - shall be removed after all optimization process in place
        public static void SubqueryDirectToPhysic(Expr expr)
        {
            // append the subquery plan align with expr
            expr.VisitEach< SubqueryExpr>(x =>
            {
                Debug.Assert(expr.HasSubQuery());
                var query = x.query_;
                ProfileOption poption = query.TopStmt().profileOpt_;
                query.physicPlan_ = query.logicPlan_.DirectToPhysical(poption);
            });
        }
    }

    public static class FilterHelper {
        
        // a > 3 or c > 1, b > 5 =>  (a > 3 or c > 1) and (b > 5)
        public static Expr AddAndFilter(Expr basefilter, Expr newcond) {
            if (basefilter is null)
                return newcond.Clone();
            return LogicAndExpr.MakeExpr(basefilter, newcond.Clone());
        }
        public static void NullifyFilter(LogicNode node)
        {
            node.filter_ = null;
            // we have to keep the LogicFilter in various cases for easier handling 
            // and we leave the vacuum job to filter push down
            //
            if (node is LogicFilter)
                node.filter_ = new LiteralExpr("true", new BoolType());
        }

        public static bool FilterIsConst(Expr filter, out bool trueOrfalse)
        {
            trueOrfalse = false;
            if (filter.TryEvalConst(out Value value)) {
                Debug.Assert(value is bool);
                trueOrfalse = value is true;
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

        // Join filter pushdown may depends on join order.
        // Consider 
        //    - filter1: a.a1 = c.c1
        //    - filter2: a.a2 = b.b2
        //    - nodeJoin: (A X B) X C
        // filter2 can be pushed to A X B but filter1 has to stay on top join for current plan.
        // if we consider we can reorder join to (A X C) X B, then filter1 can be pushed down
        // but not filter2. Current stage is too early for this purpose since join reordering
        // is happened later. So we only do best efforts here only.
        //
        public static bool PushJoinFilter(LogicNode plan, Expr filter)
        {
            // the filter shall be a join filter
            Debug.Assert(filter.TableRefCount() >= 2);

            return plan.VisitEachNodeExists(n =>
            {
                if (n is LogicJoin nodeJoin)
                {
                    var nodejoinIncl = nodeJoin.InclusiveTableRefs();

                    // if this node contains tables needed by the filter, we know we can at least push 
                    // the filter down to this node. But we want to push deeper. However, the recursion
                    // is in-order, which means the parent node gets visited first. So we have to change
                    // the recursion here to get children try the push down first: if can't push there,
                    // current node will the the stop; otherwise, recursion can stop.
                    //
                    if (filter.TableRefsContainedBy(nodejoinIncl))
                    {
                        if (!PushJoinFilter(nodeJoin.l_(), filter) &&
                            !PushJoinFilter(nodeJoin.r_(), filter))
                            return nodeJoin.AddFilter(filter);
                        else
                            return true;
                    }
                }
                return false;
            });
        }
    }

    public class Expr
    {
        // Expression in selection list can have an output name 
        // e.g, a.i+b.i [[as] total]
        //
        // Rules to use output name:
        //    1. Used in print the column value
        //    2. Used in subquery as seen by parent query
        //    3. If output name is not given: if column is a simple column, use column's name. 
        //       Otherwise, we will generate one for print purpose or parent query can't refer
        //       it except for select * case.
        //    4. Output name can be refered by ORDER BY|GROUP BY but not WHERE|HAVING.
        //
        internal string outputName_;

        // subclass shall only use children_ to contain Expr, otherwise, Bind() etc won't work
        internal List<Expr> children_ = new List<Expr>();

        // we require some columns for query processing but user may not want 
        // them in the final output, so they are marked as invisible.
        // This includes:
        // 1. subquery's outerref 
        // 2. system generated syscolumns (say sysrid_ for indexing)
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

        public bool IsLeaf() => children_.Count == 0;

        // shortcut for conventional names
        public Expr child_() { Debug.Assert(children_.Count == 1); return children_[0]; }
        public Expr l_() { Debug.Assert(children_.Count == 2); return children_[0]; }
        public Expr r_() { Debug.Assert(children_.Count == 2); return children_[1]; }

        protected string outputName() => outputName_ != null ? $"(as {outputName_})" : null;

        public int TableRefCount()
        {
        	Debug.Assert(bounded_);
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
            return Utils.ListAContainsB(tableRefs, tableRefs_);
        }
        public bool TableRefsEquals(List<TableRef> tableRefs)
        {
            Debug.Assert(bounded_);
            return Utils.ListAEqualsB(tableRefs, tableRefs_);
        }

        bool VisitEachExistsT<T>(Func<T, bool> check, List<Type> excluding = null) where T: Expr
        {
            if (excluding?.Contains(GetType()) ?? false)
                return false;

            bool r = check(this as T);
            if (!r)
            {
                foreach (var v in children_)
                {
                    if (v.VisitEachExistsT(check, excluding))
                        return true;
                }
                return false;
            }
            return true;
        }

        public bool VisitEachExprExists(Func<Expr, bool> check, List<Type> excluding = null) 
            => VisitEachExistsT<Expr>(check, excluding);

        public void VisitEach<T>(Action<T> callback) where T: Expr
        {
            Func<T, bool> wrapCallbackAsCheck<T>(Action<T> callback)
            {
                return a => { if (a is T) callback(a); return false; };
            }
            VisitEachExistsT(wrapCallbackAsCheck(callback));
        }
        public void VisitEachExpr(Action<Expr> callback) => VisitEach<Expr>(callback);

        public bool HasSubQuery() => VisitEachExprExists(e => e is SubqueryExpr);
        public bool HasAggFunc() => VisitEachExprExists(e => e is AggFunc);
        public bool IsConst()
        {
            return !VisitEachExprExists(e =>
            {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is SubqueryExpr || e is AggFunc;
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
                equal = from.Equals(clone.outputName_);
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

        // In current expression, search all type T and replace with callback(T)
        public Expr SearchReplace<T>(Func<T, Expr> replacefn) where T: Expr
        {
            bool checkfn(Expr e) => e is T;
            return SearchReplace<T>(checkfn, replacefn);
        }

        // generic form of search with condition @checkfn and replace with @replacefn
        public Expr SearchReplace<T>(Func<Expr, bool> checkfn, Func<T, Expr> replacefn) where T: Expr
        {
            if (checkfn(this))
                return replacefn((T)this);
            else
            {
                for (int i = 0; i < children_.Count; i++)
                {
                    var child = children_[i];
                    children_[i] = child.SearchReplace(checkfn, replacefn);
                }
                return this;
            }
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

		public List<TableRef> ResetAggregateTableRefs ()
		{
            if (children_.Count > 0)
            {
                tableRefs_.Clear();
                children_.ForEach(x =>
                {
                    Debug.Assert(x.bounded_);
                    tableRefs_.AddRange(x.ResetAggregateTableRefs());
                });
                if (tableRefs_.Count > 1)
                    tableRefs_ = tableRefs_.Distinct().ToList();
            }

            return tableRefs_;
		}

        public virtual void Bind(BindContext context)
        {
            for (int i = 0; i < children_.Count; i++) {
                Expr x = children_[i];
                
                x.Bind(context);
                x = x.Simplify();
                children_[i] = x;
            }
			ResetAggregateTableRefs ();

            markBounded();
        }

        public Expr Simplify()
        {
            Debug.Assert(bounded_);

            bool IsConstFn(Expr e) => !(e is LiteralExpr) && e.IsConst();
            Expr EvalConstFn(Expr e)
            {
                e.TryEvalConst(out Value val);
                return LiteralExpr.MakeLiteral(val, type_);
            }

            if (this is LiteralExpr)
                return this;
            else
                return SearchReplace<Expr>(IsConstFn, EvalConstFn);
        }

        public Expr DeQueryRef()
        {
            var expr = SearchReplace<ColExpr>(x => x.ExprOfQueryRef());
            expr.ResetAggregateTableRefs();
            return expr;
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
            var targetrefs = new List<TableRef>();
            if (tabAlias_ is null)
                targetrefs = context.AllTableRefs();
            else { 
                var x = context.Table(tabAlias_);
                targetrefs.Add(x);
            }

            var unbounds = new List<Expr>();
            var exprs = new List<Expr>();
            targetrefs.ForEach(x =>
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
            dbName_ = dbName; tabName_ = tabName; colName_ = colName; outputName_ = colName; type_ = type;
            Debug.Assert(Clone().Equals(this));
        }

        public Expr ExprOfQueryRef()
        {
            Expr expr = this;
            Debug.Assert(bounded_);
            if (tabRef_ is FromQueryRef tf)
            {
                expr = tf.MapOutputName(outputName_);
            }
            return expr;
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(tabRef_ is null);
            Debug.Assert(IsLeaf());

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
                    throw new SemanticAnalyzeException($"can't bind column '{colName_}' to table");
            }

            Debug.Assert(tabRef_ != null);
            if (tabName_ is null)
                tabName_ = tabRef_.alias_;
            if (!isOuterRef_)
            {
                Debug.Assert(tableRefs_.Count == 0);
                tableRefs_.Add(tabRef_);
            }
            // FIXME: we shall not decide ordinal_ so early but if not, hard to handle outerref
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
                return Equals(oe.expr_());
            return false;
        }
        public override string ToString()
        {
            string para = isOuterRef_ ? "?" : "";
            bool showTableName = ExplainOption.show_tablename_;
            if (para != "")
                showTableName = true;
            string tablename = showTableName ? tabName_+"." : "";
            para += isVisible_ ? "" : "#";
            if (colName_.Equals(outputName_))
                return ordinal_==-1? $@"{para}{tablename}{colName_}":
                    $@"{para}{tablename}{colName_}[{ordinal_}]";
            return ordinal_ == -1 ? $@"{para}{tablename}{colName_} (as {outputName_})":
                $@"{para}{tablename}{colName_} (as {outputName_})[{ordinal_}]";
        }
        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (isOuterRef_)
                return context.GetParam(tabRef_, ordinal_);
            else
                return input[ordinal_];
        }
    }

    public class SysColExpr : ColExpr {

        public static List<string> SysCols_ = new List<string>() { "sysrid_" };
        public SysColExpr(string dbName, string tabName, string colName, ColumnType type):base(dbName, tabName, colName, type)
        {
            Debug.Assert(SysCols_.Contains(colName));
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(context.AllTableRefs().Count == 1);
            var tabref = context.AllTableRefs()[0];
            tabName_ = tabref.alias_;
            tabRef_ = tabref;
            tableRefs_.Add(tabref);

            type_ = new RowType();
            ordinal_ = -10;
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            return input;
        }
    }

    public class CteExpr : Expr
    {
        internal string cteName_;
        internal SQLStatement query_;
        internal List<string> colNames_;

        public CteExpr(string cteName, List<string> colNames, SQLStatement query)
        {
            cteName_ = cteName;
            query_ = query;
            colNames_ = colNames;
        }
    }

    public class OrderTerm : Expr
    {
        internal bool descend_;
        internal Expr orderby_() => children_[0];

        public override string ToString() => $"{orderby_()} {(descend_ ? "[desc]" : "")}";
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

        public LiteralExpr(string str, ColumnType type)
        {
            str_ = str;
            type_ = type;

            if (str == "")
            {
                val_ = null;
                type_ = new AnyType();
            }
            else
            {
                switch (type)
                {
                    case IntType it:
                        if (int.TryParse(str, out int value))
                            val_ = value;
                        else
                            throw new SemanticAnalyzeException("wrong integer format");
                        break;
                    case DoubleType dt:
                        if (double.TryParse(str, out double valued))
                            val_ = valued;
                        else
                            throw new SemanticAnalyzeException("wrong double precision format");
                        break;
                    case DateTimeType dtt:
                        var datestr = Utils.RemoveStringQuotes(str);
                        if (DateTime.TryParse(datestr, out DateTime valuedt))
                            val_ = valuedt;
                        else
                            throw new SemanticAnalyzeException("wrong datetime format");
                        break;
                    case CharType ct:
                    case VarCharType vt:
                        str_ = Utils.RemoveStringQuotes(str_);
                        val_ = str_;
                        break;
                    case BoolType bt:
                        val_ = bool.Parse(str);
                        break;
                    case AnyType at:
                        val_ = null;
                        break;
                    default:
                        break;
                }
            }

            Debug.Assert(type_ != null);
            Debug.Assert(val_ != null || type_ is AnyType);
        }

        public LiteralExpr(string interval, string unit)
        {
            int day = 0;
            interval = Utils.RemoveStringQuotes(interval);
            if (int.TryParse(interval, out int value))
                day = value;
            else
                throw new SemanticAnalyzeException("wrong interval format");
            convertToDateTime(day, unit);
        }

        void convertToDateTime(int interval, string unit)
        {
            str_ = interval + unit;
            type_ = new DateTimeType();

            Debug.Assert(unit.Contains("day") || unit.Contains("month") || unit.Contains("year"));
            int day = interval;
            if (unit.Contains("month"))
                day *= 30;  // FIXME
            else if (unit.Contains("year"))
                day *= 365;  // FIXME
            val_ = new TimeSpan(day, 0, 0, 0);
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

        // give @val and perform the necessary type conversion
        // 8.1, int => 8
        //
        public static LiteralExpr MakeLiteral(Value val, ColumnType type)
        {
            LiteralExpr ret = null;
            if (val is string || val is DateTime)
                ret = new LiteralExpr($"'{val}'", type);
            else
                ret = new LiteralExpr($"{val}", type);

            ret.markBounded();
            return ret;
        }
    }

    // Runtime only used to reference an expr as a whole without recomputation
    // the tricky part of this class is that it is a wrapper, but Equal() shall be taken care
    // so we invent ExprHelper.Equal() for the purpose
    //
    public class ExprRef : Expr
    {
        // child_() can't be an ExprRef again
        internal Expr expr_() => child_();
        internal int ordinal_;

        public override string ToString() => $@"{{{Utils.RemovePositions(expr_().ToString())}}}[{ordinal_}]";
        public ExprRef(Expr expr, int ordinal)
        {
            if (expr is ExprRef ee)
                expr = ee.expr_();
            Debug.Assert(!(expr is ExprRef));
            children_.Add(expr); ordinal_ = ordinal;
            type_ = expr.type_;
            bounded_ = expr.bounded_;
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

        public override void Bind(BindContext context) => throw new SemanticExecutionException("ExprRef inherits its expr's bounded_ status");
        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            return input[ordinal_];
        }
    }
}

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
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;
using qpmodel.index;

namespace qpmodel.expr
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
        public void RegisterTable(TableRef tab)
        {
            bool FindSameInParents(TableRef tab)
            {
                var top = parent_;
                while (top != null)
                {
                    // it is ok to have same alias in the virtical (different levels) but not on the same level
                    if (top.boundFrom_.Values.Any(x => x.alias_.Equals(tab.alias_)))
                    {
                        Debug.Assert(top.boundFrom_.Values.Count(x => x.alias_.Equals(tab.alias_)) == 1);
                        return true;
                    }

                    top = top.parent_;
                }
                return false;
            }

            if (FindSameInParents(tab))
                tab.alias_ = $"{tab.alias_}__{globalSubqCounter_}";
            boundFrom_.Add(boundFrom_.Count, tab);
        }
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
        public TableRef GetTableRef(string tabName, string colName)
        {
            TableRef locateByColumnName(string colName)
            {
                var result = AllTableRefs().FirstOrDefault(x => x.LocateColumn(colName) != null);
                if (result != AllTableRefs().LastOrDefault(x => x.LocateColumn(colName) != null))
                    throw new SemanticAnalyzeException($"ambigous column name: {colName}");
                return result;
            }
            if (tabName is null)
                return locateByColumnName(colName);
            else
                return Table(tabName);
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

            if (r != -1)
            {
                if (Table(tabAlias) is BaseTableRef bt)
                    Debug.Assert(r == Catalog.systable_.Column(bt.relname_, colAlias).ordinal_);
                Debug.Assert(type != null);
                return r;
            }
            throw new SemanticAnalyzeException($"column not exists {tabAlias}.{colAlias}");
        }


    }

    // TBD: make it per query
    public class ExprSearch
    {
        public static Dictionary<string, Expr> table_ = new Dictionary<string, Expr>();

        public static Expr Locate(string objectid) => table_[objectid];
    }

    public static class ExprHelper
    {
        public static List<Expr> CloneList(this List<Expr> source, List<Type> excludes = null)
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

        public static List<ColExpr> RetrieveAllColExpr(this Expr expr, bool includingParameters = false)
        {
            var list = new HashSet<ColExpr>();
            expr.VisitEachT<ColExpr>(x =>
            {
                if (includingParameters || !x.isParameter_)
                    list.Add(x);
            });

            return list.ToList();
        }

        public static List<T> RetrieveAllType<T>(this Expr expr) where T : Expr
        {
            var list = new List<T>();
            expr.VisitEachT<T>(x => list.Add(x));
            return list;
        }

        public static List<ColExpr> RetrieveAllColExpr(this List<Expr> exprs, bool includingParameters = false)
        {
            var list = new List<ColExpr>();
            exprs.ForEach(x => list.AddRange(RetrieveAllColExpr(x, includingParameters)));
            return list.Distinct().ToList();
        }

        // TODO: what about expr.tableRefs_?
        //  They shall be equal but our implmentation sometimes get them inconsistent
        //  for example, xc.tabRef_ can contain outerrefs but tableRefs_ does not.
        public static List<TableRef> CollectAllTableRef(this Expr expr, bool includingParameters = true)
        {
            var list = new HashSet<TableRef>();
            expr.VisitEachT<ColExpr>(x =>
            {
                if (!x.isParameter_ || includingParameters)
                    list.Add(x.tabRef_);
            });
            return list.ToList();
        }

        public static string PrintExprWithSubqueryExpanded(this Expr expr, int depth, ExplainOption option)
        {
            string r = "";
            // append the subquery plan align with expr
            if (expr.HasSubQuery())
            {
                r += "\n";
                expr.VisitEachT<SubqueryExpr>(x =>
                {
                    string cached = x.IsCacheable() ? "cached " : "";
                    r += Utils.Tabs(depth + 2) + $"<{x.GetType().Name}> {cached}{x.subqueryid_}\n";
                    Debug.Assert(x.query_.bindContext_ != null);
                    if (x.query_.physicPlan_ != null)
                        r += $"{x.query_.physicPlan_.Explain(depth + 4, option)}";
                    else
                        r += $"{x.query_.logicPlan_.Explain(depth + 4, option)}";
                });
            }

            return r;
        }

        // this is a hack - shall be removed after all optimization process in place
        public static void SubqueryDirectToPhysic(this Expr expr)
        {
            // append the subquery plan align with expr
           expr.VisitEachT<SubqueryExpr>(x =>
           {
               Debug.Assert(expr.HasSubQuery());
               var query = x.query_;
               query.physicPlan_ = query.logicPlan_.DirectToPhysical(query.TopStmt().queryOpt_);
           });
        }

        public static bool IsBoolean(this Expr expr)
        {
            Debug.Assert(expr.type_ != null);
            return expr.type_ is BoolType;
        }

        public static Expr ConstFolding(this Expr expr)
        {
            Debug.Assert(expr.bounded_);
            bool IsConstFn(Expr e) => !(e is LiteralExpr) && e.IsConst();
            Expr EvalConstFn(Expr e)
            {
                e.TryEvalConst(out Value val);
                return LiteralExpr.MakeLiteral(val, expr.type_);
            }

            if (expr is LiteralExpr)
                return expr;
            else
                return expr.SearchReplace<Expr>(IsConstFn, EvalConstFn);
        }
    }

    public static class FilterHelper {

        public static Expr MakeFullComparator(List<Expr> left, List<Expr> right)
        {
            // a rough check here - caller's responsiblity to do a full check
            Debug.Assert(left.Count == right.Count);

            Expr result = BinExpr.MakeBooleanExpr(left[0], right[0], "=");
            for (int i = 1; i < left.Count; i++)
            {
                Expr qual = BinExpr.MakeBooleanExpr(left[i], right[i], "=");
                result = result.AddAndFilter(qual);
            }

            return result;
        }

        public static int FilterHashCode(this Expr filter) {
            // consider the case:
            //   A X (B X C on f3) on f1 AND f2
            // is equal to commutative transformation
            //   (A X B on f1) X C on f3 AND f2
            // The filter signature generation has to be able to accomomdate this difference.
            //
            if (filter is null)
                return 0;
            var andlist = filter.FilterToAndList();
            return andlist.ListHashCode();
        }

        // a List<Expr> conditions merge into a LogicAndExpr
        public static Expr AndListToExpr(this List<Expr> andlist)
        {
            Debug.Assert(andlist.Count >= 1);
            if (andlist.Count == 1)
                return andlist[0];
            else
            {
                var andexpr = LogicAndExpr.MakeExpr(andlist[0], andlist[1]);
                for (int i = 2; i < andlist.Count; i++)
                    andexpr.children_[0] = LogicAndExpr.MakeExpr(andexpr.l_(), andlist[i]);
                return andexpr;
            }
        }

        // a > 3 or c > 1, b > 5 =>  (a > 3 or c > 1) and (b > 5)
        public static Expr AddAndFilter(this Expr basefilter, Expr newcond) {
            Debug.Assert(newcond.IsBoolean());
            if (basefilter is null)
                return newcond.Clone();
            return LogicAndExpr.MakeExpr(basefilter, newcond.Clone());
        }
        public static void NullifyFilter(this LogicNode node)
        {
            node.filter_ = null;
            // we have to keep the LogicFilter in various cases for easier handling 
            // and we leave the vacuum job to filter push down
            //
            if (node is LogicFilter)
                node.filter_ = LiteralExpr.MakeLiteral("true", new BoolType());
        }

        public static bool FilterIsConst(this Expr filter, out bool trueOrfalse)
        {
            Debug.Assert(filter.IsBoolean());
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
        public static List<Expr> FilterToAndList(this Expr filter)
        {
            Debug.Assert(filter.IsBoolean());
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
        public static bool PushJoinFilter(this LogicNode plan, Expr filter)
        {
            // the filter shall be a join filter
            Debug.Assert(filter.IsBoolean());
            Debug.Assert(filter.TableRefCount() >= 2);

            return plan.VisitEachExists(n =>
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
                        if (!nodeJoin.l_().PushJoinFilter(filter) &&
                            !nodeJoin.r_().PushJoinFilter(filter))
                            return nodeJoin.AddFilter(filter);
                        else
                            return true;
                    }
                }
                return false;
            });
        }

        // suppport forms
        //   a.i =|>|< 5
        public static IndexDef FilterCanUseIndex(this Expr filter, BaseTableRef table)
        {
            string[] indexops = { "=", ">=", "<=", ">", "<" };
            Debug.Assert(filter.IsBoolean());

            IndexDef ret = null;
            if (filter is BinExpr fb)
            {
                // expression is already normalized, so no swap side shall considered
                Debug.Assert(!(fb.l_() is LiteralExpr && fb.r_() is ColExpr));
                if (indexops.Contains(fb.op_) && fb.l_() is ColExpr cl && fb.r_() is LiteralExpr)
                {
                    var index = table.Table().IndexContains(cl.colName_);
                    if (index != null) {
                        if (index.columns_[0].Equals(cl.colName_))
                            ret = index;
                    }
                }
            }

            return ret;
        }

        // 2 < a.i => a.i > 2
        public static Expr FilterNormalize(this Expr filter)
        {
            Debug.Assert(filter.IsBoolean());

            filter.VisitEachT<BinExpr>(x =>
            {
                if (x.IsBoolean() && x.l_() is LiteralExpr)
                    x.SwapSide();
            });
            return filter;
        }

        // forms to consider:
        //   a.i = b.j
        //   a.i = b.j and b.l = a.k
        //   (a.i, a.k) = (b.j, b.l)
        //   a.i + b.i = c.i-2*d.i if left side contained a,b and right side c,d
        // but not:
        //   a.i = c.i-2*d.i-b.i if left side contained a,b and right side c,d (we can add later)
        //
        public static bool FilterHashable(this Expr filter)
        {
            bool OneFilterHashable(Expr filter)
            {
                if (filter is BinExpr bf && bf.op_.Equals("="))
                {
                    var ltabrefs = bf.l_().tableRefs_;
                    var rtabrefs = bf.r_().tableRefs_;
                    // TODO: a.i+b.i=0 => a.i=-b.i
                    return ltabrefs.Count > 0 && rtabrefs.Count > 0;
                }
                return false;
            }

            if (filter is null)
                return false;
            var andlist = filter.FilterToAndList();
            foreach (var v in andlist)
            {
                if (!OneFilterHashable(v))
                    return false;
            }
            return andlist.Count >= 1;
        }

        // Simple case: c.c2=?b.b2 and b.b3>2 => c.c2=?b.b2
        // Complex case: a.i > @1 and @1 is a subquery with correlated expr
        //
        static List<Expr> FilterGetCorrelated(this Expr filter, bool shallowColExprOnly)
        {
            List<Expr> results = new List<Expr>();
            Debug.Assert(filter.IsBoolean());
            var andlist = filter.FilterToAndList();
            foreach (var v in andlist)
            {
                v.VisitEach(x => {
                    if (x is SubqueryExpr xs && !shallowColExprOnly)
                        results.AddRange(xs.query_.logicPlan_.RetrieveCorrelated(shallowColExprOnly));
                    if (x is ColExpr xc && xc.isParameter_) {
                        if (shallowColExprOnly)
                            results.Add(xc);
                        else
                            results.Add(v);
                    }
                });
            }
            return results;
        }

        public static List<Expr> FilterGetCorrelatedFilter(this Expr filter) => FilterGetCorrelated(filter, false);
        public static List<Expr> FilterGetCorrelatedCol(this Expr filter) => FilterGetCorrelated(filter, true);

        // c.c2=?b.b2 => b
        public static List<TableRef> FilterGetOuterRef(this Expr filter)
        {
            List<TableRef> refs = new List<TableRef>();
            filter.VisitEachT<ColExpr>(x => {
                if (x.isParameter_)
                    refs.Add(x.tabRef_);
            });

            return refs;
        }

        // deparameterize all ColExpr in @filter if they are contained in @tablerefs
        // e.g., filter: a.a1=?b.b1 and a.a2=?c.c1; tablrefs={b}
        //       then we shall only process 'b.b1'
        //
        public static void DeParameter(this Expr filter, List<TableRef> tablerefs) {
            var cols = filter.FilterGetCorrelatedCol();
            cols.ForEach(x =>
            {
                var xc = x as ColExpr;
                if (tablerefs.Contains(xc.tabRef_))
                    xc.DeParameter();
            });
            filter.ResetAggregateTableRefs();
        }
    }

    public class Expr
    {
        // unique identifier
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string _;

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
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        internal List<Expr> children_ = new List<Expr>();

        // we require some columns for query processing but user may not want 
        // them in the final output, so they are marked as invisible.
        // This includes:
        // 1. subquery's outerref 
        // 2. system generated syscolumns (say sysrid_ for indexing)
        //
        public bool isVisible_ = true;

        // an expression can reference multiple tables
        //      e.g., a.i + b.j > [a.]k => references 2 tables
        // it is a sum of all its children
        //
        internal List<TableRef> tableRefs_ = new List<TableRef>();
        internal bool bounded_;

        // output type of the expression
        internal ColumnType type_;

        // debug info
        internal bool dbg_isCloneCopy_ = false;

        public Expr() { _ = $"{ObjectID.NewId()}"; }

        public bool IsLeaf() => children_.Count == 0;

        // shortcut for conventional names
        public Expr child_() { Debug.Assert(children_.Count == 1); return children_[0]; }
        public Expr l_() { Debug.Assert(children_.Count == 2); return children_[0]; }
        public Expr r_() { Debug.Assert(children_.Count == 2); return children_[1]; }

        protected string outputName() => outputName_ != null ? $"(as {outputName_})" : null;

        void validateAfterBound() {
            Debug.Assert(bounded_);
            Debug.Assert(tableRefs_.Distinct().Count() == tableRefs_.Count);
        }

        public int TableRefCount() {validateAfterBound();return tableRefs_.Count;}
        public bool EqualTableRef(TableRef tableRef){ validateAfterBound();Debug.Assert(TableRefCount() == 1);return tableRefs_[0].Equals(tableRef);}
        public bool TableRefsContainedBy(List<TableRef> tableRefs){validateAfterBound();return tableRefs.ContainsList(tableRefs_);}

        bool VisitEachExistsT<T>(Func<T, bool> check, List<Type> excluding = null) where T : Expr
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
            }
            return r;
        }
        public bool VisitEachExists(Func<Expr, bool> check, List<Type> excluding = null)
            => VisitEachExistsT<Expr>(check, excluding);
        public void VisitEachT<T>(Action<T> callback) where T : Expr
        {
            if (this is T)
                callback(this as T);
            foreach (var v in children_)
                v.VisitEachT<T>(callback);
        }
        public void VisitEach(Action<Expr> callback) => VisitEachT<Expr>(callback);
        public void VisitEachIgnoreRef<T>(Action<T> callback) where T : Expr
            => VisitEachIgnore<ExprRef, T>(callback);

        public void VisitEachIgnore<T1, T2>(Action<T2> callback) where T1: Expr where T2: Expr
        {
            if (!(this is T1))
            {
                if (this is T2)
                    callback(this as T2);
                foreach (var v in children_)
                    v.VisitEachIgnore<T1, T2>(callback);
            }
        }

        public bool HasSubQuery() => VisitEachExists(e => e is SubqueryExpr);
        public bool HasAggFunc() => VisitEachExists(e => e is AggFunc);
        public bool HasAggrRef() => VisitEachExists(e => e is AggrRef);

        public bool IsConst()
        {
            return !VisitEachExists(e =>
            {
                // meaning has non-constantable (or we don't want to waste time try 
                // to figure out if they are constant, say 'select 1' or sin(2))
                //
                bool nonconst = e is ColExpr || e is SubqueryExpr
                                        || e is AggFunc || e is MarkerExpr;
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
        //  Sometimes we have to copy out expressions, consider the following query
        // select a2 from(select a3, a1, a2 from a) b
        // PhysicSubquery <b>
        //    Output: b.a2[0]
        //  -> PhysicGet a
        //      Output: a.a2[1]
        // notice b.a2 and a.a2 are the same column but have different ordinal.
        // This means we have to copy ColExpr, so its parents, then everything.
        //
        public Expr Clone()
        {
            Expr n = (Expr)MemberwiseClone();
            n.children_ = children_.CloneList();
            n.tableRefs_ = new List<TableRef>();
            tableRefs_.ForEach(n.tableRefs_.Add);
            n.dbg_isCloneCopy_ = true;
            n._ = _;
            Debug.Assert(Equals(n));
            return n;
        }

        // In current expression, search and replace @from with @to 
        public Expr SearchReplace<T>(T from, Expr to, bool aggregateTableRefs = true)
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
                clone.children_.ForEach(x => newl.Add(x.SearchReplace(from, to, aggregateTableRefs)));
                clone.children_ = newl;
            }

            if (aggregateTableRefs)
                clone.ResetAggregateTableRefs();
            return clone;
        }

        // In current expression, search all type T and replace with callback(T)
        public Expr SearchReplace<T>(Func<T, Expr> replacefn) where T : Expr
        {
            bool checkfn(Expr e) => e is T;
            return SearchReplace<T>(checkfn, replacefn);
        }

        // generic form of search with condition @checkfn and replace with @replacefn
        public Expr SearchReplace<T>(Func<Expr, bool> checkfn, Func<T, Expr> replacefn) where T : Expr
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

        public override int GetHashCode() => tableRefs_.ListHashCode() ^ children_.ListHashCode();
        public override bool Equals(object obj)
        {
            if (!(obj is Expr))
                return false;
            var n = obj as Expr;
            return object.Equals(_, n._) && tableRefs_.SequenceEqual(n.tableRefs_) &&
                children_.SequenceEqual(n.children_);
        }

        public List<TableRef> ResetAggregateTableRefs()
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

        protected void markBounded()
        {
            Debug.Assert(!bounded_);
            bounded_ = true;
            validateAfterBound();

            // register the expression in the search table
            ExprSearch.table_.Add(_, this);
        }

        public virtual void Bind(BindContext context)
        {
            for (int i = 0; i < children_.Count; i++) {
                Expr x = children_[i];

                x.Bind(context);
                x = x.ConstFolding();
                children_[i] = x;
            }
            ResetAggregateTableRefs();

            markBounded();
        }

        public Expr DeQueryRef()
        {
            bool hasAggFunc = this.HasAggFunc();
            var expr = SearchReplace<ColExpr>(x => x.ExprOfQueryRef(hasAggFunc));
            expr.ResetAggregateTableRefs();
            return expr;
        }

        public virtual string PrintString(int depth) => ToString();
        public virtual Value Exec(ExecContext context, Row input) => throw new Exception($"{this} subclass shall implment Exec()");
        public virtual string ExecCode(ExecContext context, string input) {
            return $@"ExprSearch.Locate(""{_}"").Exec(context, {input}) /*{ToString()}*/";
        }
    }

    // Represents "*" or "table.*" - it is not in the tree after Bind(). 
    // To avoid confusion, we implment Expand() instead of Bind().
    //
    public class SelStar : Expr
    {
        internal readonly string tabAlias_;

        public SelStar(string tabAlias):base() => tabAlias_ = tabAlias;
        public override string ToString() => tabAlias_ + ".*";

        public override void Bind(BindContext context) => throw new InvalidProgramException("shall be expanded already");
        internal List<Expr> ExpandAndDeQuerRef(BindContext context)
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
                // just try to be strict to not binding multiple times. Expanding
                // order is also important.
                //
                var list = x.AllColumnsRefs();
                if (!(x is QueryRef))
                    list.ForEach(x => x.Bind(context));
                else
                {
                    if (context.stmt_.queryOpt_.optimize_.remove_from_ 
                        && x is FromQueryRef xf)
                    {
                        list.Clear();
                        list.AddRange(xf.GetInnerTableExprs());
                    }
                }

                // add to the output
                exprs.AddRange(list);
            });
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
        // parse info - don't change after assignment
        internal readonly string dbName_;
        internal readonly string tabName_;
        internal readonly string colName_;

        // bound info
        internal TableRef tabRef_;
        internal bool isParameter_;
        internal int ordinal_ = -1;          // which column in children's output

        // -- execution section --

        public ColExpr(string dbName, string tabName, string colName, ColumnType type) : base()
        {
            dbName_ = dbName; tabName_ = tabName; colName_ = colName; outputName_ = colName; type_ = type;
            Debug.Assert(Clone().Equals(this));
        }

        public string tableName_() {
            if (bounded_)
                return tabRef_.alias_;
            else
                return tabName_;
        }

        public Expr ExprOfQueryRef(bool hasAggFunc)
        {
            Expr expr = this;
            Debug.Assert(bounded_);
            if (tabRef_ is FromQueryRef tf)
            {
                var repl = tf.MapOutputName(outputName_);
                if (hasAggFunc && repl.HasAggFunc())
                    expr = new AggrRef(repl, -1);
                else
                    expr = repl;
            }
            return expr;
        }

        public void DeParameter() {
            Debug.Assert(isParameter_);
            Debug.Assert(tableRefs_.Count == 0 && tableRefs_ != null);
            isParameter_ = false;
            tableRefs_.Add(tabRef_);
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(IsLeaf());
            Debug.Assert(tabRef_ is null);
            Debug.Assert(tabName_ == tableName_());

            // if table name is not given, search through all tablerefs
            isParameter_ = false;
            tabRef_ = context.GetTableRef(tabName_, colName_);

            // we can't find the column in current context, so it could an outer reference
            if (tabRef_ is null)
            {
                // can't find in my current context, try my ancestors levels up: order is important
                // as we are matching naming with the order closest first
                //    ... from A ... ( from A where exists (... from B where b1 > a.a1)) 
                // so a.a1 matches the inner side A.
                //
                BindContext parent = context;
                while ((parent = parent.parent_) != null)
                {
                    tabRef_ = parent.GetTableRef(tabName_, colName_);
                    if (tabRef_ != null)
                    {
                        // we are actually switch the context to parent, whichTab_ is not right ...
                        isParameter_ = true;
                        tabRef_.colRefedBySubq_.Add(this);

                        // mark myself a correlated query and remember whom I am correlated to
                        var mystmt = context.stmt_ as SelectStmt;
                        mystmt.isCorrelated_ = true;
                        mystmt.correlatedWhich_.Add(parent.stmt_ as SelectStmt);

                        context = parent;
                        break;
                    }
                }
                if (tabRef_ is null)
                    throw new SemanticAnalyzeException($"can't bind column '{colName_}' to table");
            }

            // we have identified the tableRef this belongs to, make sure outputName not conflicting
            // Eg. select c1 as c2, ... from c
            if (!outputName_.Equals(colName_) &&
                tabRef_.LocateColumn(outputName_) != null)
                throw new SemanticAnalyzeException($"conflicting output name {outputName_} is not allowed");

            Debug.Assert(tabRef_ != null);
            if (!isParameter_)
            {
                Debug.Assert(tableRefs_.Count == 0);
                tableRefs_.Add(tabRef_);
            }
            // FIXME: we shall not decide ordinal_ so early but if not, hard to handle outerref
            ordinal_ = context.ColumnOrdinal(tabRef_.alias_, colName_, out ColumnType type);
            type_ = type;
            markBounded();
        }

        public override int GetHashCode() => (tableName_() + colName_).GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is ColExpr co)
            {
                if (co.tableName_() is null)
                    return tableName_() is null && co.colName_.Equals(colName_);
                else
                    return co.tableName_().Equals(tableName_()) && co.colName_.Equals(colName_);
            }
            else if (obj is ExprRef oe)
                return Equals(oe.expr_());
            return false;
        }
        public override string ToString()
        {
            string para = isParameter_ ? "?" : "";
            bool showTableName = ExplainOption.show_tablename_;
            if (para != "")
                showTableName = true;
            string tablename = showTableName ? tableName_() + "." : "";
            para += isVisible_ ? "" : "#";
            if (colName_.Equals(outputName_))
                return ordinal_ == -1 ? $@"{para}{tablename}{colName_}" :
                    $@"{para}{tablename}{colName_}[{ordinal_}]";
            return ordinal_ == -1 ? $@"{para}{tablename}{colName_} (as {outputName_})" :
                $@"{para}{tablename}{colName_} (as {outputName_})[{ordinal_}]";
        }
        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (isParameter_)
                return context.GetParam(tabRef_, ordinal_);
            else
                return input[ordinal_];
        }

        public override string ExecCode(ExecContext context, string input)
        {
            Debug.Assert(type_ != null);
            if (isParameter_)
                return base.ExecCode(context, input);
            else
                return $"{input}[{ordinal_}]";
        }
    }

    public class SysColExpr : ColExpr {

        public static List<string> SysCols_ = new List<string>() { "sysrid_" };
        public SysColExpr(string dbName, string tabName, string colName, ColumnType type) : base(dbName, tabName, colName, type)
        {
            Debug.Assert(SysCols_.Contains(colName));
        }

        public override void Bind(BindContext context)
        {
            Debug.Assert(context.AllTableRefs().Count == 1);
            var tabref = context.AllTableRefs()[0];
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

    // WITH cteName_[colNames_] AS <query_>
    // Example:
    //  WITH cte(a4, a3, a2) from select * from a where a4 > 0 will rename 
    //  (* = a1,a2,a3,a4) to (a4,a3,a2,a4) with a4 show up twice. But notice
    //  this won't change output content but only output names. Say WHERE a4>0
    //  shall give you an error of "ambigous column 'a4'". 
    // 
    public class CteExpr : Expr
    {
        internal string cteName_;
        internal SelectStmt query_;
        internal List<string> colNames_;

        internal int refcnt_;

        public CteExpr(string cteName, List<string> colNames, SQLStatement query) : base()
        {
            Utils.Assumes(query is SelectStmt);

            query_ = query as SelectStmt;
            Debug.Assert(!query_.isCteDefinition_);
            query_.isCteDefinition_ = true;
            cteName_ = cteName;
            colNames_ = colNames;
            refcnt_ = 0;
        }

        public void VerifyColNames()
        {
            // this is done after star expanded
            if (colNames_.Count > query_.selection_.Count)
                throw new SemanticAnalyzeException(
                    $"WITH query '{cteName_}' only have {query_.selection_.Count} columns but {colNames_.Count} specified");
        }
    }

    public class OrderTerm : Expr
    {
        internal bool descend_;
        internal Expr orderby_() => children_[0];

        public override string ToString() => $"{orderby_()} {(descend_ ? "[desc]" : "")}";
        public OrderTerm(Expr expr, bool descend) : base()
        {
            children_.Add(expr); descend_ = descend;
        }
    }

    public class LiteralExpr : Expr
    {
        internal string str_;
        internal Value val_;

        public LiteralExpr(string str, ColumnType type) : base()
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
                        var datestr = str.RemoveStringQuotes();
                        if (DateTime.TryParse(datestr, out DateTime valuedt))
                            val_ = valuedt;
                        else
                            throw new SemanticAnalyzeException("wrong datetime format");
                        break;
                    case CharType ct:
                    case VarCharType vt:
                        str_ = str_.RemoveStringQuotes();
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
            interval = interval.RemoveStringQuotes();
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

        public override string ExecCode(ExecContext context, string input) {
            var str = str_.Replace("'", "\"");
            if (type_ is DateTimeType)
            {
                var date = (DateTime)val_;
                return $"(new DateTime({date.Ticks}))";
            }
            return str;
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
            if (type is CharType || type is VarCharType || type is DateTimeType)
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

        public override string ToString() => $@"{{{expr_().ToString().RemovePositions()}}}[{ordinal_}]";
        public ExprRef(Expr expr, int ordinal): base()
        {
            if (expr is ExprRef ee)
                expr = ee.expr_();
            Debug.Assert(!(expr is ExprRef));
            children_.Add(expr); 
            ordinal_ = ordinal;
            type_ = expr.type_;
            bounded_ = expr.bounded_;

            // reuse underlying expression's id
            _ = expr._;
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

        public override string ExecCode(ExecContext context, string input)
        {
            Debug.Assert(type_ != null);
            return $"{input}[{ordinal_}]";
        }
    }

    // This is a special type of ExprRef: functionalities same as ExprRef but only for AggFunc
    // count(*)[0] as AggrRef
    public class AggrRef : ExprRef
    {
        public Expr aggr_() => child_();

        public override string ToString() => $@"{{{aggr_().ToString().RemovePositions()}}}[{ordinal_}]";
        public AggrRef(Expr expr, int ordinal) : base(expr, ordinal)
        {
           Debug.Assert(expr.HasAggFunc());
        }
    }
}

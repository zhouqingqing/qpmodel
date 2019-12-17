using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

//
// Parser is the only place shall deal with antlr 
// do NOT using any antlr structure here
//

namespace adb
{
    public abstract class SQLStatement
    {
        // bounded context
        internal BindContext bindContext_;

        // logic and physical plans
        public LogicNode logicPlan_;
        public PhysicNode physicPlan_;

        // others
        public ProfileOption profileOpt_ = new ProfileOption();
        public OptimizeOption optimizeOpt_ = new OptimizeOption();

        // DEBUG support
        internal readonly string text_;

        protected SQLStatement(string text) => text_ = text;
        public virtual BindContext Bind(BindContext parent) => null;
        public virtual LogicNode Optimize() => logicPlan_;
        public virtual LogicNode CreatePlan() => logicPlan_;

        public virtual List<Row> Exec(bool enableProfiling = false)
        {
            if (enableProfiling)
                profileOpt_.enabled_ = true;

            Bind(null);
            CreatePlan();
            Optimize();

            var result = new PhysicCollect(physicPlan_);
            result.Open();
            result.Exec(new ExecContext(), null);
            result.Close();
            return result.rows_;
        }
    }

    public partial class SelectStmt : SQLStatement
    {
        // parse info
        // ---------------

        // this section can show up in setops
        internal List<TableRef> from_;
        internal readonly Expr where_;
        internal List<Expr> groupby_;
        internal readonly Expr having_;
        internal readonly List<Expr> selection_;

        // this section can only show up in top query
        public readonly List<CteExpr> ctes_;
        public List<CTEQueryRef> ctefrom_;
        public readonly List<SelectStmt> setqs_;
        public List<Expr> orders_;
        public readonly List<bool> descends_;   // order by DESC|ASC

        // optimizer info
        // ---------------

        // details of outerrefs are recorded in referenced TableRef
        internal SelectStmt parent_;
        // subqueries at my level (children level excluded)
        internal List<SelectStmt> subqueries_ = new List<SelectStmt>();
        internal Dictionary<SelectStmt, LogicFromQuery> fromqueries_ = new Dictionary<SelectStmt, LogicFromQuery>();
        internal bool isCorrelated = false;
        internal bool hasAgg_ = false;
        internal bool bounded_ = false;

        internal SelectStmt TopStmt()
        {
            var top = this;
            while (top.parent_ != null)
                top = top.parent_;
            Debug.Assert(top != null);
            return top;
        }

        // group|order by 2 => selection_[2-1]
        List<Expr> seq2selection(List<Expr> list, List<Expr> selection)
        {
            var converted = new List<Expr>();
            list.ForEach(x =>
            {
                if (x is LiteralExpr xl)
                {
                    // clone is not necessary but we have some assertions to check
                    // redundant processing, say same colexpr bound twice, I'd rather
                    // keep them.
                    //
                    int id = int.Parse(xl.str_);
                    converted.Add(selection[id - 1].Clone());
                }
                else
                    converted.Add(x);
            });
            Debug.Assert(converted.Count == list.Count);
            return converted;
        }

        public SelectStmt(
            // setops ok fields
            List<Expr> selection, List<TableRef> from, Expr where, List<Expr> groupby, Expr having,
            // top query only fields
            List<CteExpr> ctes, List<SelectStmt> setqs, List<OrderTerm> orders,
            string text) : base(text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            having_ = having;
            if (groupby != null)
                groupby_ = seq2selection(groupby, selection);

            ctes_ = ctes;
            setqs_ = setqs;
            if (orders != null)
            {
                orders_ = seq2selection((from x in orders select x.children_[0]).ToList(), selection);
                descends_ = (from x in orders select x.descend_).ToList();
            }
        }

        CTEQueryRef wayUpToFindCte(BindContext context, string alias)
        {
            var parent = context;
            do
            {
                var topctes = (parent.stmt_ as SelectStmt).ctefrom_;
                CTEQueryRef cte;
                if (topctes != null &&
                    null != (cte = topctes.Find(x => x.alias_.Equals(alias))))
                    return cte;
            } while ((parent = parent.parent_) != null);
            return null;
        }

        void bindFrom(BindContext context)
        {
            // We enumerate all CTEs first
            if (ctes_ != null) {
                ctefrom_ = new List<CTEQueryRef>();
                ctes_.ForEach(x=> {
                    var cte = new CTEQueryRef(x.query_ as SelectStmt, x.alias_);
                    ctefrom_.Add(cte);
                });
            }

            // replace any BaseTableRef that can't find in system to CTE
            for (int i = 0; i < from_.Count; i++)
            {
                var x = from_[i];
                if (x is BaseTableRef bref &&
                    Catalog.systable_.TryTable(bref.relname_) is null)
                {
                    from_[i] = wayUpToFindCte(context, bref.alias_);
                    if (from_[i] is null)
                        throw new Exception($@"table {bref.relname_} not exists");
                }
            }

            from_.ForEach(x =>
            {
                switch (x)
                {
                    case BaseTableRef bref:
                        Debug.Assert(Catalog.systable_.TryTable(bref.relname_) != null);
                        context.AddTable(bref);
                        break;
                    case ExternalTableRef eref:
                        if (Catalog.systable_.TryTable(eref.baseref_.relname_) != null)
                            context.AddTable(eref);
                        else
                            throw new Exception($@"base table {eref.baseref_.relname_} not exists");
                        break;
                    case QueryRef qref:
                        if (qref.query_.bindContext_ is null)
                            qref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(qref);
                        break;
                    case JoinQueryRef jref:
                        jref.tables_.ForEach(context.AddTable);
                        jref.constraints_.ForEach(x=>x.Bind(context));
                        break;
                    default:
                        throw new NotImplementedException();
                }
            });
        }

        void bindSelectionList(BindContext context)
        {
            List<SelStar> selstars = new List<SelStar>();
            selection_.ForEach(x =>
            {
                if (x is SelStar xs)
                    selstars.Add(xs);
                else
                {
                    x.Bind(context);
                    if (x.HasAggFunc())
                        hasAgg_ = true;
                }
            });

            // expand * into actual columns
            selstars.ForEach(x =>
            {
                selection_.Remove(x);
                selection_.AddRange(x.Expand(context));
            });
        }

        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);
            parent_ = parent?.stmt_ as SelectStmt;
            bindContext_ = context;

            var ret = BindWithContext(context);
            bounded_ = true;
            return ret;
        }

        // for each expr in @list, if expr has references an alias in selection list, 
        // replace that with the true expression.
        // example:
        //      selection_: a1*5 as alias1, a2, b3
        //      orders_: alias1+b =>  a1*5+b
        //
        List<Expr> replaceOutputNameToExpr(List<Expr> list)
        {
            List<Expr> selection = selection_;

            if (list is null)
                return null;

            var newlist = new List<Expr>();
            foreach (var v in list)
            {
                Expr newv = v;
                foreach (var s in selection)
                {
                    if (s.alias_ != null)
                        newv = newv.SearchReplace(s.alias_, s);
                }
                newlist.Add(newv);
            }

            Debug.Assert(newlist.Count == list.Count);
            return newlist;
        }

        internal BindContext BindWithContext(BindContext context)
        {
            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // rules:
            //  - groupby/orderby may reference selection list's alias, so let's 
            //    expand them first
            //  - from binding shall be the first since it may create new alias
            //
            groupby_ = replaceOutputNameToExpr(groupby_);
            orders_ = replaceOutputNameToExpr(orders_);

            // from binding shall be the first since it may create new alias
            bindFrom(context);
            bindSelectionList(context);
            where_?.Bind(context);
            groupby_?.ForEach(x => x.Bind(context));
            having_?.Bind(context);
            orders_?.ForEach(x => x.Bind(context));

            return context;
        }

        LogicNode transformOneFrom(TableRef tab)
        {
            LogicNode from;
            switch (tab)
            {
                case BaseTableRef bref:
                    from = new LogicScanTable(bref);
                    break;
                case ExternalTableRef eref:
                    from = new LogicScanFile(eref);
                    break;
                case QueryRef sref:
                    var plan = sref.query_.CreatePlan();
                    from = new LogicFromQuery(sref, plan);
                    subqueries_.Add(sref.query_);
                    fromqueries_.Add(sref.query_, from as LogicFromQuery);
                    break;
                case JoinQueryRef jref:
                    LogicJoin subr = new LogicJoin(null, null);
                    for (int i = 0; i < jref.tables_.Count; i++)
                    {
                        LogicNode t = transformOneFrom(jref.tables_[i]);
                        var children = subr.children_;
                        if (children[0] is null)
                            children[0] = t;
                        else {
                            if (children[1] is null)
                                children[1] = t;
                            else
                                subr = new LogicJoin(t, subr);
                            subr.AddFilter(jref.constraints_[i - 1]);
                        }
                    }
                    from = subr;
                    break;
                default:
                    throw new Exception();
            }

            return from;
        }

        // from clause -
        //  pair each from item with cross join, their join conditions will be handled
        //  with where clauss processing.
        //
        LogicNode transformFromClause()
        {
            LogicNode root;

            if (from_.Count >= 2)
            {
                var join = new LogicJoin(null, null);
                var children = join.children_;
                from_.ForEach(x =>
                {
                    LogicNode from = transformOneFrom(x);
                    if (children[0] is null)
                        children[0] = from;
                    else
                        children[1] = (children[1] is null) ? from :
                                        new LogicJoin(from, children[1]);
                });
                root = join;
            }
            else if (from_.Count == 1)
                root = transformOneFrom(from_[0]);
            else
                root = new LogicResult(selection_);

            return root;
        }

        List<SelectStmt> createSubQueryExprPlan(Expr expr)
        {
            var subplans = new List<SelectStmt>();
            expr.VisitEachExpr(x =>
            {
                if (x is SubqueryExpr sx)
                {
                    Debug.Assert(expr.HasSubQuery());
                    sx.query_.CreatePlan();
                    subplans.Add(sx.query_);
                }
            });

            subqueries_.AddRange(subplans);
            return subplans.Count > 0 ? subplans : null;
        }

        // select i, min(i/2), 2+min(i)+max(i) from A group by i
        // => min(i/2), 2+min(i)+max(i)
        List<Expr> getAggregations()
        {
            var r = new List<Expr>();
            selection_.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    if (y is AggFunc)
                        r.Add(x);
                });
            });

            return r.Distinct().ToList();
        }

        /*
        SQL is implemented as if a query was executed in the following order:

            FROM clause
            WHERE clause
            GROUP BY clause
            HAVING clause
            SELECT clause
            ORDER BY clause
        */
        public override LogicNode CreatePlan()
        {
            LogicNode root = transformFromClause();

            // transform where clause
            if (where_ != null)
            {
                createSubQueryExprPlan(where_);
                root = new LogicFilter(root, where_);
            }

            // group by
            if (hasAgg_ || groupby_ != null)
                root = new LogicAgg(root, groupby_, getAggregations(), having_);

            // having
            if (having_ != null)
                createSubQueryExprPlan(having_);

            // order by
            if (orders_ != null)
                root = new LogicOrder(root, orders_, descends_);

            // selection list
            selection_.ForEach(x=>createSubQueryExprPlan(x));

            // resolve the output
            root.ResolveColumnOrdinal(selection_, parent_ != null);

            logicPlan_ = root;
            return root;
        }

        bool pushdownFilter(LogicNode plan, Expr filter)
        {
            switch (filter.TableRefCount())
            {
                case 0:
                    // say ?b.b1 = ?a.a1
                    return plan.VisitEachNodeExists(n =>
                    {
                        if (n is LogicScanTable nodeGet)
                            return nodeGet.AddFilter(filter);
                        return false;
                    });
                case 1:
                    return plan.VisitEachNodeExists(n =>
                    {
                        if (n is LogicScanTable nodeGet &&
                            filter.EqualTableRef(nodeGet.tabref_))
                            return nodeGet.AddFilter(filter);
                        return false;
                    });
                default:
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
                                if (!pushdownFilter(nodeJoin.l_(), filter) &&
                                    !pushdownFilter(nodeJoin.r_(), filter))
                                    return nodeJoin.AddFilter(filter);
                                else
                                    return true;
                            }
                        }
                        return false;
                    });
            }
        }

        // Things to consider to remove FromQuery:
        //  1. we can't simply remove the top FromQuery node because we have to redo
        //     the projection, including ariths and order, etc.
        //  2. FromQuery in subquery is even more complicated, because far away there
        //     could be some references of its name and we shall fix them. When we remove
        //     filter, we redo columnordinal fixing but this does not work for FromQuery
        //     because naming reference. PostgreSQL actually puts a Result node with a 
        //     name, so it is similar to FromQuery.
        //
        //  In short, we shall only focus on remove the top FromQuery because simplier.
        //
        LogicNode removeFromQuery(LogicNode plan)
        {
            return plan;
        }

        LogicNode FilterPushDown(LogicNode plan)
        {
            // locate the all filters
            var parents = new List<LogicNode>();
            var indexes = new List<int>();
            var filters = new List<LogicFilter>();
            var cntFilter = plan.FindNodeTyped(parents, indexes, filters);

            for (int i = 0; i < cntFilter; i++)
            {
                var parent = parents[i];
                var filter = filters[i];
                var index = indexes[i];

                var filterOnMarkJoin = filter.child_() is LogicMarkJoin;
                if (filterOnMarkJoin)
                    continue;

                // we shall ignore FromQuery as it will be optimized by subquery optimization
                // and this will cause double predicate push down (a1>1 && a1 > 1)
                if (parent is LogicFromQuery)
                    return plan;

                if (filter?.filter_ != null)
                {
                    List<Expr> andlist = new List<Expr>();
                    var filterexpr = filter.filter_;

                    // if it is a constant true filer, remove it. If a false filter, we leave 
                    // it there - shall we try hard to stop query early? Nope, it is no deserved
                    // to poke around for this corner case.
                    //
                    var isConst = FilterHelper.FilterIsConst(filterexpr, out bool trueOrFalse);
                    if (isConst)
                    {
                        if (!trueOrFalse)
                            andlist.Add(new LiteralExpr("false"));
                        else
                            Debug.Assert(andlist.Count == 0);
                    }
                    else
                    {
                        // filter push down
                        andlist = FilterHelper.FilterToAndList(filterexpr);
                        andlist.RemoveAll(e => pushdownFilter(plan, e));
                    }

                    // stich the new plan
                    if (andlist.Count == 0)
                    {
                        if (parent is null)
                            // take it out from the tree
                            plan = plan.child_();
                        else
                            parent.children_[index] = filter.child_();
                    }
                    else
                        filter.filter_ = ExprHelper.AndListToExpr(andlist);
                }
            }

            return plan;
        }

        public override LogicNode Optimize()
        {
            LogicNode plan = logicPlan_;

            // decorrelate subqureis - we do it before filter push down because we 
            // have more normalized plan shape before push down. And we may generate
            // some unnecessary filter to clean up.
            //
            if (optimizeOpt_.enable_subquery_to_markjoin_ && subqueries_.Count > 0)
                plan = subqueryToMarkJoin(plan);
            // Console.WriteLine(plan.PrintString(0));

            // push down filters
            plan = FilterPushDown(plan);

            // remove LogicFromQuery node
            plan = removeFromQuery(plan);

            // optimize for subqueries 
            //  fromquery needs some special handling to link the new plan
            subqueries_.ForEach(x => {
                x.optimizeOpt_ = optimizeOpt_;
                x.Optimize();
            });
            foreach (var x in fromqueries_) {
                var stmt = x.Key;
                var newplan = subqueries_.Find(stmt.Equals);
                if (newplan != null)
                    x.Value.children_[0] = newplan.logicPlan_;
            }

            // convert to physical plan
            logicPlan_ = plan;
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            selection_.ForEach(ExprHelper.SubqueryDirectToPhysic);

            // finally we can physically resolve the columns ordinals
            logicPlan_.ResolveColumnOrdinal(selection_, parent_ != null);
            return plan;
        }
    }
}

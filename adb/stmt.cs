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
            result.Exec(new ExecContext(), null);
            return result.rows_;
        }
    }

    public class SelectStmt : SQLStatement
    {
        // parse info
        // ---------------

        // this section can show up in setops
        internal readonly List<TableRef> from_;
        internal readonly Expr where_;
        internal List<Expr> groupby_;
        internal readonly Expr having_;
        internal readonly List<Expr> selection_;

        // this section can only show up in top query
        public readonly List<CteExpr> ctes_;
        public readonly List<SelectStmt> setqs_;
        public List<Expr> orders_;
        public readonly List<bool> descends_;   // order by DESC|ASC

        // optimizer info
        // ---------------

        // details of outerrefs are recorded in referenced TableRef
        internal SelectStmt parent_;
        // subqueries at my level (children level excluded)
        List<SelectStmt> subqueries_ = new List<SelectStmt>();
        bool hasAgg_ = false;

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
                orders_ = seq2selection((from x in orders select x.expr_).ToList(), selection);
                descends_ = (from x in orders select x.descend_).ToList();
            }
        }

        void bindFrom(BindContext context)
        {
            from_.ForEach(x =>
            {
                switch (x)
                {
                    case BaseTableRef bref:
                        if (Catalog.systable_.Table(bref.relname_) != null)
                            context.AddTable(bref);
                        else
                            throw new Exception($@"base table {bref.relname_} not exists");
                        break;
                    case ExternalTableRef eref:
                        if (Catalog.systable_.Table(eref.baseref_.relname_) != null)
                            context.AddTable(eref);
                        else
                            throw new Exception($@"base table {eref.baseref_.relname_} not exists");
                        break;
                    case FromQueryRef sref:
                        sref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(sref);
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

            return BindWithContext(context);
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
                case FromQueryRef sref:
                    subqueries_.Add(sref.query_);
                    from = new LogicFromQuery(sref,
                                    sref.query_.CreatePlan());
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

            // these subqueries will be removed by mark join conversion
            if (!optimizeOpt_.enable_subquery_to_markjoin)
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

            // order by
            if (orders_ != null)
                root = new LogicOrder(root, orders_, descends_);

            // selection list
            selection_.ForEach(x=>createSubQueryExprPlan(x));

            // resolve the output
            root.ResolveChildrenColumns(selection_, parent_ != null);

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
                    // Consider 
                    //  - filter1: a.a1 = c.c1
                    //  - filter2: a.a2 = b.b2
                    //  - nodeJoin: (A X B) X C
                    // filter2 can be pushed to A X B but filter1 has to stay on top join for current plan.
                    // if we consider we can reorder join to (A X C) X B, then filter1 can be pushed down
                    // but not filter1. Current stage is too early fro this purpose since join reordering
                    // is happened later. So we only do best efforts here only.
                    //
                    return plan.VisitEachNodeExists(n =>
                    {
                        if (n is LogicJoin nodeJoin &&
                            filter.EqualTableRefs(nodeJoin.InclusiveTableRefs()))
                            return nodeJoin.AddFilter(filter);
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

        public override LogicNode Optimize()
        {
            LogicNode plan = logicPlan_;

            // locate the only filter
            int cntFilter = 0;
            cntFilter = plan.FindNode(out LogicNode filterparent, out LogicFilter filter);
            Debug.Assert(cntFilter <= 1);
            if (filter?.filter_ is null)
                goto Convert;

            // filter push down
            var filterexpr = filter.filter_;
            List<Expr> andlist = new List<Expr>();
            if (filterexpr is LogicAndExpr andexpr)
                andlist = andexpr.BreakToList();
            else
                andlist.Add(filterexpr);
            andlist.RemoveAll(e => pushdownFilter(plan, e));
            if (andlist.Count == 0)
            {
                if (filterparent is null)
                    // take it out from the tree
                    plan = plan.children_[0];
                else
                    filterparent.children_[0] = filter.children_[0];
            }
            else
                filter.filter_ = ExprHelper.AndListToExpr(andlist);

            // we have to redo the column binding as filter removal might change ordinals underneath
            plan.ClearOutput();
            plan.ResolveChildrenColumns(selection_, parent_ != null);
            logicPlan_ = plan;

            Convert:
            // remove LogicFromQuery node
            plan = removeFromQuery(plan);
            logicPlan_ = plan;

            // optimize for subqueries
            subqueries_.ForEach(x => x.Optimize());

            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            selection_.ForEach(ExprHelper.SubqueryDirectToPhysic);
            return plan;
        }
    }
}

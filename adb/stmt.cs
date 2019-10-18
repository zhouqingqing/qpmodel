using System;
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
        // others
        internal ProfileOption profileOpt_ = new ProfileOption();

        // bounded context
        internal BindContext bindContext_;

        // logic and physical plans
        public LogicNode logicPlan_;
        public PhysicNode physicPlan_;

        // DEBUG support
        internal readonly string text_;

        protected SQLStatement(string text) => text_ = text;
        public virtual BindContext Bind(BindContext parent) => null;
        public virtual LogicNode Optimize() => logicPlan_;
        public virtual LogicNode CreatePlan() => logicPlan_;

        public List<Row> Exec()
        {
            Bind(null);
            CreatePlan();
            Optimize();
            var result = new PhysicCollect(physicPlan_);
            result.Exec(new ExecContext(), null);
            return result.rows_;
        }
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
    public class SelectStmt : SQLStatement
    {
        // this section can show up in setops
        internal readonly List<TableRef> from_;
        internal readonly Expr where_;
        internal readonly List<Expr> groupby_;
        internal readonly Expr having_;
        internal readonly List<Expr> selection_;

        // this section can only show up in top query
        public readonly List<CTExpr> ctes_;
        public readonly List<SelectStmt> setqs_;
        public readonly List<OrderTerm> orders_;

        // details of outerrefs are recorded in referenced TableRef
        internal SelectStmt parent_;

        internal SelectStmt TopStmt()
        {
            var top = this;
            while (top.parent_ != null)
                top = top.parent_;
            Debug.Assert(top != null);
            return top;
        }

        public SelectStmt(
            // setops ok fields
            List<Expr> selection, List<TableRef> from, Expr where, List<Expr> groupby, Expr having,
            // top query only fields
            List<CTExpr> ctes, List<SelectStmt> setqs, List<OrderTerm> orders,
            string text) : base(text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            groupby_ = groupby;
            having_ = having;

            ctes_ = ctes;
            setqs_ = setqs;
            orders_ = orders;
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
                    x.Bind(context);
            });

            // expand * into actual columns
            selstars.ForEach(x =>
            {
                selection_.Remove(x);
                selection_.AddRange(x.Expand(context));
            });
        }

        void bindWhere(BindContext context) => where_?.Bind(context);
        void bindGroupBy(BindContext context) => groupby_?.ForEach(x => x.Bind(context));
        void bindHaving(BindContext context) => having_?.Bind(context);

        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);
            parent_ = parent?.stmt_ as SelectStmt;
            bindContext_ = context;

            return BindWithContext(context);
        }

        internal BindContext BindWithContext(BindContext context)
        {
            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // from binding shall be the first since it may create new alias
            bindFrom(context);
            bindSelectionList(context);
            bindWhere(context);
            bindGroupBy(context);
            bindHaving(context);

            return context;
        }

        LogicNode transformOneFrom(TableRef tab)
        {
            LogicNode from;
            switch (tab)
            {
                case BaseTableRef bref:
                    from = new LogicGetTable(bref);
                    break;
                case ExternalTableRef eref:
                    from = new LogicGetExternal(eref);
                    break;
                case FromQueryRef sref:
                    from = new LogicFromQuery(sref,
                                    sref.query_.CreatePlan());
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
                var join = new LogicCrossJoin(null, null);
                var children = join.children_;
                from_.ForEach(x =>
                {
                    LogicNode from = transformOneFrom(x);
                    if (children[0] is null)
                        children[0] = from;
                    else
                        children[1] = (children[1] is null) ? from :
                                        new LogicCrossJoin(from, children[1]);
                });
                root = join;
            }
            else if (from_.Count == 1)
                root = transformOneFrom(from_[0]);
            else
                root = new LogicResult(selection_);

            return root;
        }

        void createSubQueryExprPlan(Expr expr)
        {
            if (expr.HasSubQuery())
            {
                expr.VisitEachExpr(x =>
                {
                    if (x is SubqueryExpr sx)
                    {
                        sx.query_.CreatePlan();
                    }
                });
            }
        }

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
            if (groupby_ != null)
                root = new LogicAgg(root, groupby_, having_);

            // selection list
            selection_.ForEach(createSubQueryExprPlan);

            // resolve the output
            root.ResolveChildrenColumns(selection_, parent_ != null);

            logicPlan_ = root;
            return root;
        }

        bool pushdownATableFilter(LogicNode plan, Expr filter)
        {
            return plan.VisitEachNodeExists(n =>
            {
                if (n is LogicGetTable nodeGet &&
                    filter.EqualTableRefs(bindContext_, nodeGet.tabref_))
                    return nodeGet.AddFilter(filter);
                return false;
            });
        }

        public override LogicNode Optimize()
        {
            LogicNode plan = logicPlan_;
            var filter = plan as LogicFilter;
            if (filter?.filter_ is null)
                goto Convert;

            // filter push down
            var topfilter = filter.filter_;
            List<Expr> andlist = new List<Expr>();
            if (topfilter is LogicAndExpr andexpr)
                andlist = andexpr.BreakToList();
            else
                andlist.Add(topfilter);
            andlist.RemoveAll(e => pushdownATableFilter(plan, e));
            if (andlist.Count == 0)
                // top filter node is not needed
                plan = plan.children_[0];
            else
                filter.filter_ = ExprHelper.AndListToExpr(andlist);
            // we have to redo the column binding as top filter removal might change ordinals underneath
            plan.ClearOutput();
            plan.ResolveChildrenColumns(selection_, parent_ != null);
            logicPlan_ = plan;

        Convert:

            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            selection_.ForEach(ExprHelper.SubqueryDirectToPhysic);
            return plan;
        }
    }
}

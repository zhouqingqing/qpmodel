using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    /*
    SQL is implemented as if a query was executed in the following order:

        FROM clause
        WHERE clause
        GROUP BY clause
        HAVING clause
        SELECT clause
        ORDER BY clause
    */
    public class SelectCore
    {
        List<TableRef> from_;
        Expr where_;
        List<Expr> groupby_;
        Expr having_;
        List<Expr> selection_;

        // bounded context
        BindContext bindContext_;
        public BindContext BinContext() => bindContext_;

        // plan
        LogicNode plan_;
        public LogicNode GetPlan() => plan_;

        // output
        public List<Expr> Selection() => selection_;

        // debug support
        string text_;

        public SelectCore(List<Expr> selection, List<TableRef> from, Expr where, List<Expr> groupby, Expr having, string text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            groupby_ = groupby;
            having_ = having;

            text_ = text;
        }

        void bindFrom(BindContext context)
        {
            from_.ForEach(x => {
                switch (x)
                {
                    case BaseTableRef bref:
                        if (Catalog.systable_.Table(bref.relname_) != null)
                            context.AddTable(bref);
                        else
                            throw new Exception($@"base table {bref.alias_} not exists");
                        break;
                    case SubqueryRef sref:
                        sref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(sref);
                        break;
                }
            });
        }
        void bindSelectionList(BindContext context) => selection_.ForEach(x => x.Bind(context));
        void bindWhere(BindContext context) => where_?.Bind(context);
        void bindGroupBy(BindContext context)=> groupby_?.ForEach(x => x.Bind(context));
        void bindHaving(BindContext context) => having_?.Bind(context);
        public SelectCore Bind(BindContext parent)
        {
            BindContext context = new BindContext(parent);

            Debug.Assert(plan_ == null);

            // from binding shall be the first since it may create new alias
            bindFrom(context);
            bindSelectionList(context);
            bindWhere(context);
            bindGroupBy(context);
            bindHaving(context);

            bindContext_ = context;
            return this;
        }

        LogicNode transformOneFrom(TableRef tab)
        {
            LogicNode from;
            switch (tab)
            {
                case BaseTableRef bref:
                    from = new LogicGet(bref);
                    break;
                case SubqueryRef sref:
                    from = new LogicSubquery(sref,
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

        public LogicNode CreatePlan()
        {
            LogicNode root = transformFromClause();

            // transform where clause
            if (where_ != null)
            {
                if (where_.HasSubQuery())
                {
                    where_.VisitEachExpr(x =>
                    {
                        if (x is SubqueryExpr sx)
                        {
                            sx.query_.CreatePlan();
                        }
                        return false;
                    });
                }
                root = new LogicFilter(root, where_);
            }

            // group by
            if (groupby_ != null)
                root = new LogicAgg(root, groupby_, having_);

            // resolve the output
            root.ResolveChildrenColumns(selection_);

            plan_ = root;
            return root;
        }

        void breakEachAndExprs(LogicAndExpr andexpr, List<Expr> andlist)
        {
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? andexpr.l_ : andexpr.r_;
                if (e is LogicAndExpr ea)
                    breakEachAndExprs(ea, andlist);
                else
                    andlist.Add(e);
            }
        }

        bool pushdownATableFilter(LogicNode plan, Expr filter)
        {
            return plan.VisitEachNode(n =>
            {
                if (n is LogicGet nodeGet &&
                    filter.EqualTableRefs(bindContext_, nodeGet.tabref_))
                    return nodeGet.AddFilter(filter);
                return false;
            });
        }

        public LogicNode Optimize(LogicNode plan)
        {
            var filter = plan as LogicFilter;
            if (filter is null || filter.filter_ is null)
                return plan;

            // filter push down
            var topfilter = filter.filter_;
            List<Expr> andlist = new List<Expr>();
            if (topfilter is LogicAndExpr andexpr)
                breakEachAndExprs(andexpr, andlist);
            else
                andlist.Add(topfilter);
            andlist.RemoveAll(e => pushdownATableFilter(plan, e));

            if (andlist.Count == 0)
            {
                // top filter node is not needed
                plan = plan.children_[0];
            }
            else 
                filter.filter_ = ExprHelper.AndListToExpr(andlist);

            return plan;
        }
    }
}

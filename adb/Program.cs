using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Antlr4;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using System.Diagnostics;

namespace adb
{
    public class ResultColumn { }

    public class TableRef
    {
        public string alias_;
    }

    public class BaseTableRef : TableRef
    {
        public string relname_;

        public BaseTableRef([NotNull] string name, string alias = null)
        {
            relname_ = name;
            alias_ = alias ?? relname_;
        }

        public override string ToString()
        {
            if (relname_ == alias_)
                return $"{relname_}";
            else
                return $"{relname_} as {alias_}";
        }
    }

    // subquery in FROM clause
    public class SubqueryRef : TableRef
    {
        public SelectCore query_;

        public SubqueryRef(SelectCore query, [NotNull] string alias)
        {
            query_ = query;
            alias_ = alias;
        }
    }

    public class SelectCore
    {
        List<object> selection_;
        List<TableRef> from_;
        Expr where_;
        List<Expr> groupby_;
        Expr having_;

        // bounded context
        public BindContext bindContext_;
        public BindContext Parent() => bindContext_;

        // plan
        public LogicNode plan_;
        public LogicNode GetPlan() => plan_;

        public SelectCore(List<object> selection, List<TableRef> from, Expr where, List<Expr> groupby, Expr having)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            groupby_ = groupby;
            having_ = having;
        }

        void BindFrom(BindContext context)
        {
            foreach (var f in from_)
            {
                switch (f)
                {
                    case BaseTableRef bref:
                        if (Catalog.systable_.Table(bref.relname_) != null)
                            context.AddTable(bref);
                        else
                            throw new Exception($@"table {bref.alias_} not exists");
                        break;
                    case SubqueryRef sref:
                        sref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(sref);
                        break;
                }
            }
        }
        void BindSelectionList(BindContext context)
        {
            foreach (var s in selection_)
            {
                if (s is Expr expr)
                    expr.Bind(context);
                else
                    // handle '*'
                    throw new NotSupportedException();
            }
        }
        void BindWhere(BindContext context) => where_?.Bind(context);
        void BindGroupBy(BindContext context)
        {
            if (groupby_ != null)
            {
                foreach (var v in groupby_)
                    v.Bind(context);
            }
            having_?.Bind(context);
        }
        public SelectCore Bind(BindContext parent)
        {
            BindContext context = new BindContext(parent);

            Debug.Assert(plan_ == null);

            // from binding shall be the first since it may create new alias
            BindFrom(context);
            BindSelectionList(context);
            BindWhere(context);
            BindGroupBy(context);
            bindContext_ = context;
            return this;
        }

        public LogicNode CreatePlan()
        {
            LogicNode root;

            // from clause -
            //  pair each from item with cross join, their join conditions will be handled
            //  with where clauss processing.
            //
            if (from_.Count >= 2)
            {
                var join = new LogicCrossJoin(null, null);
                var children = join.children_;
                foreach (var v in from_)
                {
                    LogicNode from = null;
                    switch (v)
                    {
                        case BaseTableRef bref:
                            from = new LogicGet(bref);
                            break;
                        case SubqueryRef sref:
                            from = new LogicSubquery(sref,
                                            sref.query_.CreatePlan());
                            break;
                    }
                    if (children[0] is null)
                        children[0] = from;
                    else
                        children[1] = (children[1] is null)? from: 
                                        new LogicCrossJoin(from, children[1]);
                }

                root = join;
            }
            else
                root = new LogicGet(from_[0] as BaseTableRef);

            // filter
            if (where_ != null)
            {
                // if where contains subquery, add it to the from level with mark join
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

            plan_ = root;
            return root;
        }

        void reBreakAndExprs(arithandexpr andexpr, List<Expr> andlist)
        {
            for (int i = 0; i < 2; i++)
            {
                Expr e = i == 0 ? andexpr.l_ : andexpr.r_;
                if (e is arithandexpr ea)
                    reBreakAndExprs(ea, andlist);
                else
                    andlist.Add(e);
            }
        }

        bool pushdown_tablefilter(LogicNode plan, Expr filter)
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
            if (topfilter is arithandexpr andexpr)
                reBreakAndExprs(andexpr, andlist);
            else
                andlist.Add(topfilter);
            andlist.RemoveAll(e => pushdown_tablefilter(plan, e));

            if (andlist.Count == 0)
            {
                // top filter node is not needed
                plan = plan.children_[0];
            }
            else
                plan = new LogicFilter(plan.children_[0], 
                                        ExprHelper.listToExpr(andlist));

            return plan;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            string sql;

            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > a.a2);";
            //sql = "select 1 from a where a.a1 > (select b.b1 from b) and a.a2 > (select c3 from c);";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3));";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > (select c2 from c where c.c2=b3) and b.b3 > ((select c2 from c where c.c3=b2)));";
            //sql = "select 1 from a where a.a1 > (select b1 from b where b.b2 > 2);";
            //sql = "select 1 from a, (select b1,b2 from b) k where k.b1 > 0 and a.a1 = k.b1"; // bug: can't push filter across subuquery
            //sql = "select a.a1+b.b2 from a, b, c where a3>6 and c2 > 10 and a1=b1 and a2 = c2;";
            //sql = "select f(g(a1)) from a, b where a.a3 = b.b2 and a.a2>1 group by a.a1, a.a2 having sum(a.a1)>0;";
            //sql = "select a1 from a, b where a.a1 = b.b1 and a.a2 > (select c1 from c where c.c1=c.c3);";
            //sql = "select (1+2)*3, 1+f(g(a))+1+2*3, a.i, a.i, i+a.j*2 from a, (select * from b) b where a.i=b.i;";
            sql = "select a.a1 from a where a2>3";
            //sql = "select a.a1 from a, b where a.a1=b.b1 and a2>3";
            var a = RawParser.ParseSelect(sql);

            // -- Semantic analysis:
            //  - bind the query
            a.Bind(null);

            // -- generate an initial plan
            var p = a.CreatePlan();
            Console.WriteLine(p.PrintString(0));

            // -- optimize the plan
            Console.WriteLine("-- optimized plan --");
            var o = a.Optimize(p);
            Console.WriteLine(o.PrintString(0));

            // -- physical plan
            Console.WriteLine("-- physical plan --");
            var phyroot = o.SimpleConvertPhysical();
            Console.WriteLine(phyroot.PrintString(0));

            var final = new PhysicPrint(phyroot);
            final.Open();
            final.Next();
            final.Close();
        }
    }
}

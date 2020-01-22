using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

using adb.sqlparser;
using adb.expr;
using adb.physic;
using adb.codegen;
using adb.optimizer;

//
// Parser is the only place shall deal with antlr 
// do NOT using any antlr structure here
//

namespace adb.logic
{
    public abstract class SQLStatement
    {
        // bounded context
        internal BindContext bindContext_;

        // logic and physical plans
        public LogicNode logicPlan_;
        public PhysicNode physicPlan_;

        // others
        public bool explainOnly_ = false;
        public ExplainOption explain_ = new ExplainOption();
        public QueryOption queryOpt_ = new QueryOption();

        // DEBUG support
        internal readonly string text_;

        protected SQLStatement(string text) => text_ = text;

        public override string ToString() => text_;
        public virtual BindContext Bind(BindContext parent) => null;
        public virtual LogicNode PhaseOneOptimize() => logicPlan_;
        public virtual LogicNode CreatePlan() => logicPlan_;

        public virtual List<Row> Exec()
        {
            Bind(null);
            CreatePlan();
            PhaseOneOptimize();

            if (queryOpt_.optimize_.use_memo_)
            {
                Optimizer.InitRootPlan(this);
                Optimizer.OptimizeRootPlan(this, null);
                physicPlan_ = Optimizer.CopyOutOptimalPlan();
            }
            if (explainOnly_)
                return null;

            // actual execution is needed
            var result = new PhysicCollect(physicPlan_);
            var context = new ExecContext(queryOpt_);
            var code = result.Open(context);
            code += result.Exec(context, null);
            code += result.Close(context);

            if (queryOpt_.optimize_.use_codegen_)
            {
                CodeWriter.WriteLine(code);
                Compiler.Run(Compiler.Compile(), this, context);
            }
            return result.rows_;
        }

        public static List<Row> ExecSQL(string sql, out string physicplan, out string error, QueryOption option = null)
        {
            try
            {
                var stmt = RawParser.ParseSingleSqlStatement(sql);
                if (option != null)
                    stmt.queryOpt_ = option;
                var result = stmt.Exec();
                physicplan = stmt.physicPlan_.Explain(0);
                error = "";
                return result;
            }
            catch (Exception e)
            {
                error = e.Message;
                Console.WriteLine(error);
                physicplan = null;
                return null;
            }
        }

        public static string ExecSQLList(string sqls, QueryOption option = null)
        {
            StatementList stmts = RawParser.ParseSqlStatements(sqls);
            if (option != null)
                stmts.queryOpt_ = option;
            return stmts.ExecList();
        }
    }

    public class StatementList: SQLStatement
    {
        public List<SQLStatement> list_ = new List<SQLStatement>();

        public StatementList(List<SQLStatement> list, string text) : base(text)
        {
            list_ = list;
        }

        public override List<Row> Exec()
        {
            var result = new List<Row>();
            foreach (var v in list_)
            {
                v.queryOpt_ = queryOpt_;
                result = v.Exec();
            }
            return result;
        }

        public string ExecList()
        {
            string result = "";
            foreach (var v in list_)
            {
                v.queryOpt_ = queryOpt_;
                var rows = ExecSQL(v.text_, out string plan, out _);

                // format: <sql> <plan> <result>
                result += v.text_ + "\n";
                result += plan;
                result += string.Join("\n", rows) + "\n\n";
            }
            return result;
        }
    }

    public partial class SelectStmt : SQLStatement
    {
        // parse info
        // ---------------

        // this section can show up in setops
        internal List<TableRef> from_;
        internal Expr where_;
        internal List<Expr> groupby_;
        internal Expr having_;
        internal List<Expr> selection_;
        internal bool isCtebody_ = false;

        // this section can only show up in top query
        public readonly List<CteExpr> ctes_;
        public List<CTEQueryRef> ctefrom_;
        public readonly List<SelectStmt> setqs_;
        public List<Expr> orders_;
        public readonly List<bool> descends_;   // order by DESC|ASC
        public Expr limit_;

        // optimizer info
        // ---------------

        // details of outerrefs are recorded in referenced TableRef
        internal SelectStmt parent_;
        // subqueries at my level (children level excluded)
        internal List<SelectStmt> subQueries_ = new List<SelectStmt>();
        internal List<SelectStmt> decorrelatedSubs_ = new List<SelectStmt>();
        internal Dictionary<SelectStmt, LogicFromQuery> fromQueries_ = new Dictionary<SelectStmt, LogicFromQuery>();
        internal bool hasAgg_ = false;
        internal bool bounded_ = false;

        internal bool isCorrelated_ = false;
        internal List<SelectStmt> correlatedWhich_ = new List<SelectStmt>();

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
            List<CteExpr> ctes, List<SelectStmt> setqs, List<OrderTerm> orders, Expr limit,
            string text) : base(text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            having_ = having;
            groupby_ = groupby;

            ctes_ = ctes;
            setqs_ = setqs;
            if (orders != null)
            {
                orders_ = (from x in orders select x.orderby_()).ToList();
                descends_ = (from x in orders select x.descend_).ToList();
            }
            limit_ = limit;
        }

        internal List<SelectStmt> InclusiveAllSubquries()
        {
            List<SelectStmt> allsubs = new List<SelectStmt>();
            allsubs.Add(this);
            Subqueries(true).ForEach(x =>
            {
                allsubs.AddRange(x.InclusiveAllSubquries());
            });

            return allsubs;
        }
        bool pushdownFilter(LogicNode plan, Expr filter)
        {
            // don't push down special expressions
            if (filter.VisitEachExprExists(x => x is MarkerExpr))
                return false;

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
					return plan.PushJoinFilter (filter);
            }
        }

        // To remove FromQuery, we essentially remove all references to the related 
        // FromQueryRef, which shall include selection (ColExpr, Aggs, Orders etc),
        // filters and any constructs may references a TableRef (say subquery outerref).
        //
        // If we remove FromQuery before binding, we can do it on SQL text level but 
        // it is considered very early and error proning. We can do it after binding
        // then we need to find out all references to the FromQuery and replace them
        // with underlying non-from TableRefs.
        //
        // FromQuery in subquery is even more complicated, because far away there
        // could be some references of its name and we shall fix them. When we remove
        // filter, we redo columnordinal fixing but this does not work for FromQuery
        // because naming reference. PostgreSQL actually puts a Result node with a 
        // name, so it is similar to FromQuery.
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
                    var isConst = filterexpr.FilterIsConst(out bool trueOrFalse);
                    if (isConst)
                    {
                        if (!trueOrFalse)
                            andlist.Add(LiteralExpr.MakeLiteral("false", new BoolType()));
                        else
                            Debug.Assert(andlist.Count == 0);
                    }
                    else
                    {
                        // filter push down
                        andlist = filterexpr.FilterToAndList();
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
                        filter.filter_ = andlist.AndListToExpr();
                }
            }

            return plan;
        }

        public bool SubqueryIsWithMainQuery(SelectStmt subquery)
        {
            // FromQuery or decorrelated subqueries are merged with main plan
            var r = (fromQueries_.ContainsKey(subquery) ||
                decorrelatedSubs_.Contains(subquery));
            return r;
        }

        public List<SelectStmt> Subqueries(bool excludeFromQuery = false)
        {
            List<SelectStmt> ret = new List<SelectStmt>();
            if (excludeFromQuery)
            {
                foreach (var x in subQueries_)
                    if (!SubqueryIsWithMainQuery(x))
                        ret.Add(x);
            }
            else
                ret = subQueries_;

            return ret;
        }

        bool stmtIsInCTEChain() {
            if ((bindContext_.stmt_ as SelectStmt).isCtebody_)
                return true;
           if (bindContext_.parent_ is null)
                return false;
            else
                return (bindContext_.parent_.stmt_ as SelectStmt).stmtIsInCTEChain();
        }

        public override LogicNode PhaseOneOptimize()
        {
            LogicNode logic = logicPlan_;

            // remove LogicFromQuery node
            logic = removeFromQuery(logic);

            // decorrelate subqureis - we do it before filter push down because we 
            // have more normalized plan shape before push down. And we may generate
            // some unnecessary filter to clean up.
            //
            if (queryOpt_.optimize_.enable_subquery_to_markjoin_ && subQueries_.Count > 0)
                logic = subqueryToMarkJoin(logic);

            // push down filters
            logic = FilterPushDown(logic);

            // optimize for subqueries 
            //  fromquery needs some special handling to link the new plan
            subQueries_.ForEach(x => {
                Debug.Assert (x.queryOpt_ == queryOpt_);
                x.PhaseOneOptimize();
            });
            foreach (var x in fromQueries_) {
                var stmt = x.Key as SelectStmt;
                var fromQuery = x.Value as LogicFromQuery;
                var newplan = subQueries_.Find(stmt.Equals);
                if (newplan != null)
                    fromQuery.children_[0] = newplan.logicPlan_;
            }

            // now we can adjust join order
            logic.TraversEachNode(x => {
                if (x is LogicJoin lx)
                    lx.SwapJoinSideIfNeeded();
            });
            logicPlan_ = logic;

            // convert to physical plan if memo is not used because we don't want
            // to waste time for memo as it will generate physical plan anyway. 
            // CTEQueries share the same physical plan, so we exclude it from assertion. 
            //
            Debug.Assert(physicPlan_ is null || stmtIsInCTEChain());
            physicPlan_ = null;
            if (!queryOpt_.optimize_.use_memo_)
            {
                physicPlan_ = logicPlan_.DirectToPhysical(queryOpt_);
                selection_.ForEach(ExprHelper.SubqueryDirectToPhysic);

                // finally we can physically resolve the columns ordinals
                logicPlan_.ResolveColumnOrdinal(selection_, parent_ != null);
            }

            return logic;
        }
    }
}

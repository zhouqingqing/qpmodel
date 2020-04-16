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
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

using qpmodel.sqlparser;
using qpmodel.expr;
using qpmodel.physic;
using qpmodel.codegen;
using qpmodel.optimizer;

//
// Parser is the only place shall deal with antlr 
// do NOT using any antlr structure here
//

namespace qpmodel.logic
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
        public QueryOption queryOpt_ = new QueryOption();

        // DEBUG support
        internal readonly string text_;

        protected SQLStatement(string text) => text_ = text;

        public override string ToString() => text_;
        public virtual BindContext Bind(BindContext parent) => null;
        public virtual LogicNode SubstitutionOptimize() => logicPlan_;
        public virtual LogicNode CreatePlan() => logicPlan_;

        public virtual List<Row> Exec()
        {
            Bind(null);
            CreatePlan();
            SubstitutionOptimize();

            if (queryOpt_.optimize_.use_memo_)
            {
                Optimizer.InitRootPlan(this);
                Optimizer.OptimizeRootPlan(this, null);
                physicPlan_ = Optimizer.CopyOutOptimalPlan();
            }
            if (explainOnly_)
                return null;

            // actual execution is needed
            var finalplan = new PhysicCollect(physicPlan_);
            physicPlan_ = finalplan;
            var context = new ExecContext(queryOpt_);

            finalplan.ValidateThis();
            if (this is SelectStmt select)
                select.OpenSubQueries(context);
            var code = finalplan.Open(context);
            code += finalplan.Exec(null);
            code += finalplan.Close();

            if (queryOpt_.optimize_.use_codegen_)
            {
                CodeWriter.WriteLine(code);
                Compiler.Run(Compiler.Compile(), this, context);
            }
            return finalplan.rows_;
        }

        public static List<Row> ExecSQL(string sql, out string physicplan, out string error, QueryOption option = null)
        {
            try
            {
                var stmt = RawParser.ParseSingleSqlStatement(sql);
                if (option != null)
                    stmt.queryOpt_ = option;
                var result = stmt.Exec();
                physicplan = "";
                if (stmt.physicPlan_ != null)
                    physicplan = stmt.physicPlan_.Explain(0, option?.explain_);
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

        // This function can also be used to execute a single SQL statement
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
                var rows = ExecSQL(v.text_, out string plan, out _, queryOpt_);

                // format: <sql> <plan> <result>
                result += v.text_ + "\n";
                result += plan;
                if (rows != null)
                    result += string.Join("\n", rows) + "\n\n";
            }
            return result;
        }
    }

    // setops are commutative but not associative in general
    //    e.g., A UION ALL (B UNION C) not equal to (A UNION ALL B) UION C
    // So we can use a tree structure to represent their relationship.
    //
    public class SetOpTree
    {
        // either as non-leaf
        internal string op_;
        internal SetOpTree left_;
        internal SetOpTree right_;
        // or as leaf
        internal SelectStmt stmt_;

        // first stmt is special: column resolution is aligned with it.
        // example: select a1 union select b1 order by a1 works but
        // not with 'b1'. The follows PostgreSQL tradition.
        //
        internal SelectStmt first_ { get; }

        public SetOpTree(SelectStmt stmt) {
            first_ = stmt;
            stmt_ = stmt;
            Debug.Assert(IsLeaf());
        }

        public bool IsEmpty() => stmt_ is null && op_ is null;
        public bool IsLeaf()
        {
            Debug.Assert(!IsEmpty());
            if (stmt_ != null) {
                Debug.Assert(op_ is null && left_ is null && right_ is null);
                return true;
            }
            else
            {
                Debug.Assert(op_ != null && left_ != null && right_ != null);
                return false;
            }
        }

        public void Add(string op, SelectStmt newstmt) {
            List<string> allowed = new List<string> 
                {"union", "unionall", "except", "exceptall", "intersect", "intersectall"};
            Debug.Assert(allowed.Contains(op) || op is null);
            Debug.Assert(newstmt != null);

            if (IsLeaf())
            {
                left_ = new SetOpTree(stmt_);
                stmt_ = null;
                right_ = new SetOpTree(newstmt);
                op_ = op;
            }
            else
            {
                left_ = (SetOpTree)this.MemberwiseClone();
                right_ = new SetOpTree(newstmt);
                op_ = op;
            }
            Debug.Assert(!IsLeaf());
        }

        public void VisitEachStatement(Action<SelectStmt> action)
        {
            if (IsLeaf())
                action(stmt_);
            else
            {
                left_.VisitEachStatement(action);
                right_.VisitEachStatement(action);
            }
        }

        // all statements shall have same number of compatible outputs
        List<Expr> VerifySelection(List<Expr> selection = null)
        {
            if (IsLeaf())
            {
                if (selection != null)
                {
                    // TBD: check data types as well
                    if (stmt_.selection_.Count != selection.Count)
                        throw new SemanticAnalyzeException("setop queries shall have matching column count");
                }
                return stmt_.selection_;
            }
            else
            {
                var lselect = left_.VerifySelection(selection);
                var rselect = right_.VerifySelection(lselect);
                return rselect;
            }
        }

        public LogicNode CreateSetOpPlan(bool top = true)
        {
            if (top)
            {
                // traversal on top node is the time to examine the setop tree
                Debug.Assert(!IsLeaf());
                VerifySelection();
            }

            if (IsLeaf())
                return stmt_.CreatePlan();
            else
            {
                LogicNode plan = null;
                var lplan = left_.CreateSetOpPlan(false);
                var rplan = right_.CreateSetOpPlan(false);

                // try to reuse existing operators to implment because users may write 
                // SQL code like this and this helps reduce optimizer search space
                //
                switch (op_)
                {
                    case "unionall":
                        // union all keeps all rows, including duplicates
                        plan = new LogicAppend(lplan, rplan);
                        break;
                    case "union":
                        // union collect rows from both sides, and remove duplicates
                        plan = new LogicAppend(lplan, rplan);
                        var groupby = new List<Expr>(first_.selection_.CloneList());
                        plan = new LogicAgg(plan, groupby, null, null);
                        break;
                    case "except":
                        // except keeps left rows not found in right
                    case "intersect":
                        // intersect keeps rows found in both sides
                        var filter = FilterHelper.MakeFullComparator(
                                        left_.first_.selection_, right_.first_.selection_);
                        var join = new LogicJoin(lplan, rplan);
                        if (op_.Contains("except"))
                            join.type_ = JoinType.AntiSemi;
                        if (op_.Contains("intersect"))
                            join.type_ = JoinType.Semi;
                        var logfilter = new LogicFilter(join, filter);
                        groupby = new List<Expr>(first_.selection_.CloneList());
                        plan = new LogicAgg(logfilter, groupby, null, null);
                        break;
                    case "exceptall":
                    case "intersectall":
                        // the 'all' semantics is a bit confusing than intuition:
                        //  {1,1,1} exceptall {1,1} => {1}
                        //  {1,1,1} intersectall {1,1} => {1,1}
                        //
                        throw new NotImplementedException();
                    default:
                        throw new InvalidProgramException();
                }

                return plan;
            }
        }

        public List<BindContext> Bind(BindContext parent)
        {
            List<BindContext> list = new List<BindContext>();
            if (IsLeaf())
                list.Add(stmt_.Bind(parent));
            else
            {
                list.AddRange(left_.Bind(parent));
                list.AddRange(right_.Bind(parent));
            }

            return list;
        }
    }

    public partial class SelectStmt : SQLStatement
    {
        // parse info
        // ---------------

        // this section can show up in setops
        //
        internal List<TableRef> from_;
        internal Expr where_;
        internal List<Expr> groupby_;
        internal Expr having_;
        internal List<Expr> selection_;
        internal bool isCteDefinition_ = false;

        // this section can only show up in top query
        //
        // ctes_ are the WITH definitions and cterefs_ are the references
        // there could be 3 ctes_ but 5 cterefs_ but user is allowed to 
        // define a cte but not use it
        //
        public readonly List<CteExpr> ctes_;
        public List<CTEQueryRef> cterefs_;
        public readonly SetOpTree setops_;
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

        // true if this query is a correlated subquery - if later this query is 
        // decorrelated, we don't change this status
        //
        internal bool isCorrelated_ = false;
        internal List<SelectStmt> correlatedWhich_ = new List<SelectStmt>();
        internal bool shallExpandSelection_ = false;

        internal SelectStmt TopStmt()
        {
            var top = this;
            while (top.parent_ != null)
                top = top.parent_;
            Debug.Assert(top != null);
            return top;
        }

        // after all optimization, if the plan still contains correlated subquries
        internal bool PlanContainsCorrelatedSubquery()
        {
            bool hasCorrelated = false;
            Subqueries(excludeFromAndDecorrelated: true).ForEach(x => {
                if (x.isCorrelated_)
                    hasCorrelated = true;
            });

            return hasCorrelated;
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
            List<CteExpr> ctes, SetOpTree setqs, List<OrderTerm> orders, Expr limit,
            string text) : base(text)
        {
            selection_ = selection;
            from_ = from;
            where_ = where;
            having_ = having;
            groupby_ = groupby;

            ctes_ = ctes;
            setops_ = setqs;
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
        bool pushdownFilter(LogicNode plan, Expr filter, bool pushJoinFilter)
        {
            // don't push down special expressions
            if (filter.VisitEachExists(x => x is MarkerExpr))
                return false;

            switch (filter.TableRefCount())
            {
                case 0:
                    // say ?b.b1 = ?a.a1
                    return plan.VisitEachExists(n =>
                    {
                        if (n is LogicScanTable nodeGet)
                            return nodeGet.AddFilter(filter);
                        return false;
                    });
                case 1:
                    return plan.VisitEachExists(n =>
                    {
                        if (n is LogicScanTable nodeGet &&
                            filter.EqualTableRef(nodeGet.tabref_))
                            return nodeGet.AddFilter(filter);
                        return false;
                    });
                default:
                    if (pushJoinFilter)
					    return plan.PushJoinFilter (filter);
                    return false;
            }
        }

        LogicNode FilterPushDown(LogicNode plan, bool pushJoinFilter)
        {
            // locate the all filters
            var parents = new List<LogicNode>();
            var indexes = new List<int>();
            var filters = new List<LogicFilter>();
            var cntFilter = plan.FindNodeTypeMatch(parents, indexes, filters);

            for (int i = 0; i < cntFilter; i++)
            {
                var parent = parents[i];
                var filter = filters[i];
                var index = indexes[i];


                // we shall ignore FromQuery as it will be optimized by subquery optimization
                // and this will cause double predicate push down (a1>1 && a1 > 1)
                if (parent is LogicFromQuery)
                    return plan;

                if (filter?.filter_ != null && filter?.movable_ is true)
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
                        andlist.RemoveAll(e =>
                        {
                            var isConst = e.FilterIsConst(out bool trueOrFalse);
                            if (isConst)
                            {
                                Debug.Assert(trueOrFalse);
                                return true;
                            }
                            return pushdownFilter(plan, e, pushJoinFilter);
                        });
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

        public List<SelectStmt> Subqueries(bool excludeFromAndDecorrelated = false)
        {
            List<SelectStmt> ret = new List<SelectStmt>();
            Debug.Assert(subQueries_.Count >= 
                    fromQueries_.Count  + decorrelatedSubs_.Count);
            if (excludeFromAndDecorrelated)
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
            if ((bindContext_.stmt_ as SelectStmt).isCteDefinition_)
                return true;
           if (bindContext_.parent_ is null)
                return false;
            else
                return (bindContext_.parent_.stmt_ as SelectStmt).stmtIsInCTEChain();
        }

        // a1,5+@1 (select b2 ...) => a1, 5+b2
        List<Expr> selectionRemoveSubquery(List<Expr> selection)
        {
            bool IsSubquery(Expr e) => e is SubqueryExpr;
            Expr RepalceSuquerySelection(Expr e)
            {
                return (e as SubqueryExpr).query_.selection_[0];
            }
            List<Expr> newselection = new List<Expr>();
            selection.ForEach(x =>
            {
                if (x.HasSubQuery())
                    x = x.SearchReplace<Expr>(IsSubquery, RepalceSuquerySelection);
                x.ResetAggregateTableRefs();
                newselection.Add(x);
            });
            return newselection;
        }

        internal void ResolveOrdinals()
        {
            if (setops_ is null)
            {
                if (shallExpandSelection_)
                    selection_ = selectionRemoveSubquery(selection_);
                logicPlan_.ResolveColumnOrdinal(selection_, parent_ != null);
            }
            else
            {
                // resolve each and use the first one to resolve ordinal since all are compatible
                var first = setops_.first_;
                setops_.VisitEachStatement(x =>
                {
                    x.logicPlan_.ResolveColumnOrdinal(x.selection_, false);
                });
                logicPlan_.ResolveColumnOrdinal(first.selection_, parent_ != null);
            }
        }

        LogicNode outerJoinSimplication(LogicNode root)
        {
            Expr extraFilter = null;
            if (root is LogicFilter rf)
                extraFilter = rf.filter_;

            LogicNode ret = root;
            root.VisitEach((parent, index, node) => {
                if (node is LogicJoin) {
                    if (parent != null)
                        parent.children_[index] = trySimplifyOuterJoin(node as LogicJoin, extraFilter);
                    else
                        ret = trySimplifyOuterJoin(node as LogicJoin, extraFilter);
                }
            });

            return ret;
        }

        public override LogicNode SubstitutionOptimize()
        {
            LogicNode logic = logicPlan_;

            // push down filters
            //   join solver will do the join filter push down in its own way
            bool pushJoinFilter = !queryOpt_.optimize_.memo_use_joinorder_solver_;
            logic = FilterPushDown(logic, pushJoinFilter);

            // outerjoin to inner join 
            //   it depends on join filter push to the right place
            if (pushJoinFilter)
                logic = outerJoinSimplication(logic);

            // optimize for subqueries 
            //  fromquery needs some special handling to link the new plan
            subQueries_.ForEach(x => {
                Debug.Assert (x.queryOpt_ == queryOpt_);
                if (!decorrelatedSubs_.Contains(x))
                    x.SubstitutionOptimize();
            });
            foreach (var x in fromQueries_) {
                var stmt = x.Key as SelectStmt;
                var fromQuery = x.Value as LogicFromQuery;
                var newplan = subQueries_.Find(stmt.Equals);
                if (newplan != null)
                    fromQuery.children_[0] = newplan.logicPlan_;
            }

            // now we can adjust join order
            logic.VisitEach(x => {
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
                selection_?.ForEach(ExprHelper.SubqueryDirectToPhysic);

                // finally we can physically resolve the columns ordinals
                ResolveOrdinals();
            }

            return logic;
        }

        internal void OpenSubQueries(ExecContext context) 
        {
            foreach (var v in Subqueries(true))
                v.physicPlan_.Open(context);
            foreach (var v in Subqueries(false))
                v.OpenSubQueries(context);
        }
    }

    public class DataSet
    {
        internal LogicNode logicPlan_;
        internal List<Expr> outputs_ = new List<Expr>();
        internal List<Expr> exprs_ = new List<Expr>();  // this includes outputs_
        internal PhysicNode physicPlan_;

        // helper functions
        Expr parseExpr(string str)
        {
            var expr = RawParser.ParseExpr(str);
            exprs_.Add(expr);
            return expr;
        }

        public DataSet Scan(string tableName)
        {
            Debug.Assert(logicPlan_ is null);
            logicPlan_ = new LogicScanTable(new BaseTableRef(tableName));
            return this;
        }

        public DataSet filter(string condition)
        {
            logicPlan_ = new LogicFilter(logicPlan_, parseExpr(condition));
            return this;
        }

        public DataSet join(DataSet other, string condition)
        {
            logicPlan_ = new LogicJoin(logicPlan_, other.logicPlan_, parseExpr(condition));
            return this;
        }

        public DataSet select(params string[] colNames)
        {
            foreach (var v in colNames)
                outputs_.Add(parseExpr(v));
            return this;
        }

        void bind(BindContext parent)
        {
            BindContext context = new BindContext(null, parent);
            logicPlan_.VisitEach(x =>
            {
                if (x is LogicScanTable xs)
                    context.RegisterTable(xs.tabref_);
            });

            foreach (var v in exprs_)
                v.Bind(context);
        }

        public List<Row> show()
        {
            bind(null);

            // TBD: route to optimizer here
            QueryOption queryOpt = new QueryOption();
            physicPlan_ = logicPlan_.DirectToPhysical(queryOpt);
            logicPlan_.ResolveColumnOrdinal(outputs_);

            // actual execution
            var finalplan = new PhysicCollect(physicPlan_);
            physicPlan_ = finalplan;
            var context = new ExecContext(queryOpt);
            Console.WriteLine(physicPlan_.Explain());

            finalplan.ValidateThis();
            var code = finalplan.Open(context);
            code += finalplan.Exec(null);
            code += finalplan.Close();

            return finalplan.rows_;
        }
    }

    public class SQLContext
    {
        public DataSet Read(string tableName)
        {
            return new DataSet().Scan(tableName);
        }

        public static void Register<T1, TResult>(string name, Func<T1, TResult> fn)
            => ExternalFunctions.Register(name, fn, 1, typeof(TResult));
        public static void Register<T1, T2, TResult>(string name, Func<T1, T2, TResult> fn)
            => ExternalFunctions.Register(name, fn, 2, typeof(TResult));
        public static void Register<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> fn)
            => ExternalFunctions.Register(name, fn, 3, typeof(TResult));
    }
}

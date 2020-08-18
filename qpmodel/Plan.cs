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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using qpmodel.expr;
using qpmodel.physic;
using qpmodel.utils;

namespace qpmodel.logic
{
    public class QueryOption
    {
        public class ProfileOption
        {
            public bool enabled_ { get; set; } = true;

            public ProfileOption Clone() => (ProfileOption)MemberwiseClone();
        }
        public class OptimizeOption
        {
            // rewrite controls
            public bool enable_subquery_unnest_ { get; set; } = true;
            public bool remove_from_ { get; set; } = false;        // make it true by default
            public bool enable_cte_plan_ { get; set; } = false; // make it true by default

            // optimizer controls
            public bool enable_hashjoin_ { get; set; } = true;
            public bool enable_nljoin_ { get; set; } = true;
            public bool enable_streamagg_ { get; set; } = false;
            public bool enable_indexseek_ { get; set; } = true;
            public bool use_memo_ { get; set; } = true;
            public bool memo_disable_crossjoin_ { get; set; } = true;
            public bool memo_use_joinorder_solver_ { get; set; } = false;   // make it true by default

            // distributed query dop is the same as number of distributions
            public int query_dop_ { get; set; } = num_machines_;

            // codegen controls
            public bool use_codegen_ { get; set; } = false;

            public void TurnOnAllOptimizations()
            {
                enable_subquery_unnest_ = true;
                remove_from_ = true;

                enable_hashjoin_ = true;
                enable_nljoin_ = true;
                enable_indexseek_ = true;

                use_memo_ = true;
                memo_disable_crossjoin_ = true;
                memo_use_joinorder_solver_ = false; // FIXME
            }

            public void ValidateOptions()
            {
                if (memo_use_joinorder_solver_)
                    Debug.Assert(use_memo_);
            }
            public OptimizeOption Clone() => (OptimizeOption)MemberwiseClone();
        }

        // user specified options
        public ProfileOption profile_ = new ProfileOption();
        public OptimizeOption optimize_ = new OptimizeOption();
        public ExplainOption explain_ = new ExplainOption();

        // global static variables
        public const int num_machines_ = 10;

        public QueryOption Clone()
        {
            var newoption = new QueryOption();
            newoption.optimize_ = optimize_.Clone();
            newoption.explain_ = explain_.Clone();
            newoption.profile_ = profile_.Clone();
            return newoption;
        }

        // codegen section
        bool saved_use_codegen_;
        public void PushCodeGenDisable()
        {
            saved_use_codegen_ = optimize_.use_codegen_;
            optimize_.use_codegen_ = false;
        }

        public void PopCodeGen() => optimize_.use_codegen_ = saved_use_codegen_;
    }

    public enum ExplainMode
    {
        explain,    // physical plan with estimated cost, execution skipped
        analyze,    // physical plan, estimates, actual cost, results muted
        full,       // analyze mode with results printed
    }

    public class ExplainOption
    {
        [ThreadStatic]
        public static bool show_tablename_ = true;
        public ExplainMode mode_ { get; set; } = ExplainMode.full;
        public bool show_estCost_ { get; set; } = false;
        public bool show_output_ { get; set; } = true;
        public bool show_id_ { get; set; } = false;
        public ExplainOption Clone() => (ExplainOption)MemberwiseClone();
    }

    public abstract class PlanNode<T> : TreeNode<T> where T : PlanNode<T>
    {
        // print utilities
        public virtual string ExplainOutput(int depth, ExplainOption option) => null;
        public virtual string ExplainInlineDetails() => null;
        public virtual string ExplainMoreDetails(int depth, ExplainOption option) => null;
        protected string ExplainFilter(Expr filter, int depth, ExplainOption option)
        {
            string r = null;
            if (filter != null)
            {
                // append the subquery plan align with filter
                r = "Filter: " + filter;
                r += filter.ExplainExprWithSubqueryExpanded(depth, option);
            }
            return r;
        }

        public string Explain(ExplainOption option = null, int depth = 0)
        {
            string r = null;
            bool exp_output = option?.show_output_ ?? true;
            bool exp_showcost = option?.show_estCost_ ?? false;
            bool exp_showactual = option is null ? false : option.mode_ >= ExplainMode.analyze;
            bool exp_id = option?.show_id_ ?? false;

            if (!(this is PhysicProfiling) && !(this is PhysicCollect))
            {
                r = Utils.Spaces(depth);
                if (depth == 0)
                {
                    if (exp_showcost && this is PhysicNode phytop)
                    {
                        var memorycost = "";
                        var memory = phytop.InclusiveMemory();
                        if (memory != 0)
                            memorycost = $", memory={memory}";
                        r += $"Total cost: {Math.Truncate(phytop.InclusiveCost() * 100) / 100}{memorycost}\n";
                    }
                }
                else
                    r += "-> ";

                // print line of <nodeName> : <Estimation> <Actual>
                var id = _ + (clone_.Equals("notclone") ? "" : "_" + clone_);
                r += $"{this.GetType().Name}{(exp_id ? " " + id : "")} {ExplainInlineDetails()}";
                var phynode = this as PhysicNode;
                if (phynode != null && phynode.profile_ != null)
                {
                    if (exp_showcost)
                    {
                        var incCost = Math.Truncate(phynode.InclusiveCost() * 100) / 100;
                        var cost = Math.Truncate(phynode.Cost() * 100) / 100;
                        var memorycost = "";
                        var memory = phynode.Memory();
                        if (memory != 0)
                            memorycost = $", memory={memory}";
                        r += $" (inccost={incCost}, cost={cost}, rows={phynode.logic_.Card()}{memorycost})";
                    }
                    if (exp_showactual)
                    {
                        var profile = phynode.profile_;
                        if (profile.nloops_ == 1 || profile.nloops_ == 0)
                            r += $" (actual rows={profile.nrows_})";
                        else
                            r += $" (actual rows={profile.nrows_ / profile.nloops_}, loops={profile.nloops_})";
                    }
                }
                r += "\n";
                var details = ExplainMoreDetails(depth, option);

                // print current node's output
                var output = exp_output ? ExplainOutput(depth, option) : null;
                if (output != null)
                    r += Utils.Spaces(depth + 2) + output + "\n";
                if (details != null)
                {
                    // remove the last \n in case the details is a subquery
                    var trailing = "\n";
                    if (details[details.Length - 1] == '\n')
                        trailing = "";
                    r += Utils.Spaces(depth + 2) + details + trailing;
                }

                depth += 2;
            }

            // guard against endless plan: 255 is an arbitrary number. Instead of
            // throwing here, we choose to print the plan for debugging.
            //
            if (depth <= 255)
            {
                bool printAsConsumer = false;
                if (!printAsConsumer)
                    children_.ForEach(x => r += x.Explain(option, depth));
            }

            // details of distributed query emulation
            if (this is PhysicGather)
            {
                int nthreads = QueryOption.num_machines_;
                int nshuffle = 0;
                VisitEach(x =>
                {
                    if (x is PhysicRedistribute || x is PhysicBroadcast)
                        nshuffle++;
                });
                r += $"\nEmulated {QueryOption.num_machines_} machines distributed run " +
                     $"with {nthreads * (1 + nshuffle)} threads\n";
            }
            return r;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ children_.ListHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is PlanNode<T> lo)
            {
                if (lo.GetType() != GetType())
                    return false;
                for (int i = 0; i < children_.Count; i++)
                {
                    if (!lo.children_[i].Equals(children_[i]))
                        return false;
                }
                return true;
            }
            return false;
        }
    }

    public partial class SelectStmt : SQLStatement
    {
        // locate subqueries or SRF in given expr and create plan for each
        // subquery, no change on the expr itself.
        //
        LogicNode setReturningExprCreatePlan(LogicNode root, Expr expr)
        {
            var newroot = root;
            var subplans = new List<NamedQuery>();
            expr.VisitEachT<Expr>(e =>
            {
                if (e is SubqueryExpr x)
                {
                    Debug.Assert(expr.HasSubQuery());
                    x.query_.CreatePlan();
                    subplans.Add(new NamedQuery(x.query_, null));

                    // functionally we don't have to do rewrite since above
                    // plan is already runnable
                    if (queryOpt_.optimize_.enable_subquery_unnest_)
                    {
                        // use the plan 'root' containing the subexpr 'x'
                        var replacement = oneSubqueryToJoin(root, x);
                        newroot = (LogicNode)newroot.SearchAndReplace(root,
                                                                replacement);
                    }
                }
                else if (e is FuncExpr f && f.isSRF_)
                {
                    var newchild = new LogicProjectSet(root.child_());
                    root.children_[0] = newchild;
                }
            });

            subQueries_.AddRange(subplans);
            return newroot;
        }

        // select i, min(i/2), 2+min(i)+max(i) from A group by i
        // => min(i/2), 2+min(i)+max(i)
        List<Expr> getAggFuncFromSelection()
        {
            var r = new List<Expr>();
            selection_.ForEach(x =>
            {
                x.VisitEach(y =>
                {
                    if (y is AggFunc)
                        r.Add(x);
                });
            });

            return r.Distinct().ToList();
        }

        // from clause -
        //  pair each from item with cross join, their join conditions will be handled
        //  with where clauss processing.
        //
        LogicNode transformFromClause()
        {
            LogicNode transformOneFrom(TableRef tab)
            {
                LogicNode from;
                switch (tab)
                {
                    case BaseTableRef bref:
                        from = new LogicScanTable(bref);
                        if (bref.tableSample_ != null)
                            from = new LogicSampleScan(from, bref.tableSample_);
                        break;
                    case ExternalTableRef eref:
                        from = new LogicScanFile(eref);
                        break;
                    case QueryRef qref:
                        var plan = qref.query_.CreatePlan();
                        if (qref is FromQueryRef && queryOpt_.optimize_.remove_from_)
                            from = plan;
                        else
                        {
                            string alias = null;
                            if (qref is CTEQueryRef cq)
                                alias = cq.alias_;
                            else if (qref is FromQueryRef fq)
                                alias = fq.alias_;
                            var key = new NamedQuery(qref.query_, alias);
                            from = new LogicFromQuery(qref, plan);
                            subQueries_.Add(key);

                            // if from CTE, then it could be duplicates
                            if (!fromQueries_.ContainsKey(key))
                                fromQueries_.Add(key, from as LogicFromQuery);
                        }
                        break;
                    case JoinQueryRef jref:
                        // We will form join group on all tables and put a filter on top
                        // of the joins as a normalized form for later processing.
                        //
                        //      from a join b on a1=b1 or a3=b3 join c on a2=c2;
                        //   => from a , b, c where  (a1=b1 or a3=b3) and a2=c2;
                        //
                        LogicJoin subjoin = new LogicJoin(null, null);
                        Expr filterexpr = null;
                        for (int i = 0; i < jref.tables_.Count; i++)
                        {
                            LogicNode t = transformOneFrom(jref.tables_[i]);
                            var children = subjoin.children_;
                            if (children[0] is null)
                                children[0] = t;
                            else
                            {
                                if (children[1] is null)
                                    children[1] = t;
                                else
                                    subjoin = new LogicJoin(t, subjoin);
                                subjoin.type_ = jref.joinops_[i - 1];
                                filterexpr = filterexpr.AddAndFilter(jref.constraints_[i - 1]);
                            }
                        }
                        Debug.Assert(filterexpr != null);
                        from = new LogicFilter(subjoin, filterexpr);
                        break;
                    default:
                        throw new InvalidProgramException();
                }

                return from;
            }

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

            // distributed plan is required if any table is distributed, subquery 
            // referenced tables are not counted here. So we may have the query 
            // shape with main queyr not distributed but subquery is.
            //
            bool hasdtable = false;
            bool onlyreplicated = true;
            from_.ForEach(x =>
            {
                if (x is BaseTableRef bx)
                {
                    var method = bx.Table().distMethod_;
                    if (bx.IsDistributed())
                    {
                        hasdtable = true;
                        if (method != TableDef.DistributionMethod.Replicated)
                            onlyreplicated = false;
                    }
                }
            });
            if (hasdtable)
            {
                Debug.Assert(!distributed_);
                distributed_ = true;

                // FIXME: we have to disable memo optimization before property enforcement done
                queryOpt_.optimize_.use_memo_ = false;
                if (onlyreplicated)
                    root = new LogicGather(root, new List<int> { 0 });
                else
                    root = new LogicGather(root);
            }

            return root;
        }

        // select * from (select max(b3) maxb3 from b) b where maxb3>1
        // where => max(b3)>1 and this shall be moved to aggregation node
        //
        Expr moveFilterToInsideAggNode(LogicNode root, Expr filter)
        {
            // first find out the aggregation node shall take the filter
            List<LogicAgg> aggNodes = new List<LogicAgg>();
            if (root.FindNodeTypeMatch<LogicAgg>(aggNodes) > 1)
                throw new NotImplementedException("can handle one aggregation now");
            var aggNode = aggNodes[0];

            // make the filter and add to the node
            var list = filter.FilterToAndList();
            List<Expr> shallmove = new List<Expr>();
            foreach (var v in list)
            {
                if (v.HasAggFunc())
                    shallmove.Add(v);
            }
            var moveExpr = shallmove.AndListToExpr();
            aggNode.having_ = aggNode.having_.AddAndFilter(moveExpr);
            var newfilter = list.Except(shallmove).ToList();
            if (newfilter.Count > 0)
                return newfilter.AndListToExpr();
            else
                return new ConstExpr("true", new BoolType());
        }

        public override LogicNode CreatePlan()
        {
            LogicNode root;
            if (setops_ is null)
                root = CreateSinglePlan();
            else
            {
                root = setops_.CreateSetOpPlan();

                // setops plan can also have CTEs, LIMIT and ORDER support
                // Notes: GROUPBY is with the individual clause
                Debug.Assert(!hasAgg_);

                // order by
                if (orders_ != null)
                    root = new LogicOrder(root, orders_, descends_);

                // limit
                if (limit_ != null)
                    root = new LogicLimit(root, limit_);
            }

            // after this, setops are merged with main plan
            logicPlan_ = root;
            return root;
        }

        /*
            SELECT is implemented as if a query was executed in the following order:
            1. CTEs: every one is evaluated and evaluted once as if it is served as temp table.
            2. FROM clause: every one in from clause evaluted. They together evaluated as a catersian join.
            3. WHERE clause: filters, including joins filters are evaluted.
            4. GROUP BY clause: grouping according to group by clause and filtered by HAVING clause.
            5. SELECT [ALL|DISTINCT] clause: ALL (default) will output every row and DISTINCT removes duplicates.
            6. Set Ops: UION [ALL] | INTERSECT| EXCEPT combines multiple output of SELECT.
            7. ORDER BY clause: sort the results with the specified order.
            8. LIMIT|FETCH|OFFSET clause: restrict amount of results output.
        */
        public LogicNode CreateSinglePlan()
        {
            // we don't consider setops in this level
            Debug.Assert(setops_ is null);

            LogicNode root = transformFromClause();

            // transform where clause - we only want one filter
            if (where_ != null)
            {
                if (!(root is LogicFilter lr))
                    root = new LogicFilter(root, where_);
                else
                    lr.filter_ = lr.filter_.AddAndFilter(where_);
                if (where_ != null && where_.HasAggFunc())
                {
                    where_ = moveFilterToInsideAggNode(root, where_);
                    root = new LogicFilter(root.child_(), where_);
                }

                root = setReturningExprCreatePlan(root, where_);
            }

            // group by / having
            if (hasAgg_)
            {
                root = new LogicAgg(root, groupby_, getAggFuncFromSelection(), having_);
                if (having_ != null)
                    root = setReturningExprCreatePlan(root, having_);
                if (groupby_ != null)
                    groupby_.ForEach(x =>
                    {
                        root = setReturningExprCreatePlan(root, x);
                    });
            }

            // selection list
            selection_.ForEach(x =>
            {
                var oldroot = root;
                root = setReturningExprCreatePlan(root, x);
                shallExpandSelection_ |= root != oldroot;
            });

            // order by
            if (orders_ != null)
                root = new LogicOrder(root, orders_, descends_);

            // limit
            if (limit_ != null)
                root = new LogicLimit(root, limit_);

            // ctes
            if (ctes_ != null)
                root = tryCteToSequencePlan(root);

            // let's make sure the plan is in good shape
            //  - there is no filter except filter node (ok to be multiple)
            root.VisitEach(x =>
            {
                var log = x as LogicNode;
                if (!(x is LogicFilter))
                    Debug.Assert(log.filter_ is null);
            });

            // the plan we get here is able to run (converted to physical) with some exceptions
            //  - the join order might not right if left side has parameter dependency on right side
            //    which is fixed in optimization stage where we get more information to decide it
            //
            logicPlan_ = root;
            return root;
        }

        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);
            parent_ = parent?.stmt_ as SelectStmt;
            bindContext_ = context;

            if (parent_ != null)
                queryOpt_ = parent_.queryOpt_;
            Debug.Assert(!bounded_);
            if (setops_ is null)
                BindWithContext(context);
            else
            {
                // FIXME: we can't enable all optimizations with this mode
                queryOpt_.optimize_.remove_from_ = false;
                queryOpt_.optimize_.use_memo_ = false;

                setops_.Bind(parent);

                // nowe we bound first statement's selection to current because
                // later ORDER etc processing replies on selection list
                Debug.Assert(selection_ is null);
                selection_ = setops_.first_.selection_;

                // setops plan can also have CTEs, LIMIT and ORDER support
                // Notes: GROUPBY is with the individual clause
                Debug.Assert(!hasAgg_);
                if (orders_ != null)
                {
                    orders_ = bindOrderByOrGroupBy(context, orders_);
                }
            }

            bounded_ = true;
            return context;
        }

        // ORDER BY | GROUP BY list both can use sequence number and references selection list
        List<Expr> bindOrderByOrGroupBy(BindContext context, List<Expr> byList)
        {
            byList = replaceOutputNameToExpr(byList);
            byList = seq2selection(byList, selection_);
            byList.ForEach(x =>
            {
                if (!x.bounded_)        // some items already bounded with seq2selection()
                    x.Bind(context);
            });
            if (queryOpt_.optimize_.remove_from_)
            {
                for (int i = 0; i < byList.Count; i++)
                    byList[i] = byList[i].DeQueryRef();
            }

            return byList;
        }

        List<Expr> bindSelectionList(BindContext context)
        {
            // keep the expansion order
            List<Expr> newselection = new List<Expr>();
            for (int i = 0; i < selection_.Count; i++)
            {
                Expr x = selection_[i];
                if (x is SelStar xs)
                {
                    // expand * into actual columns
                    var list = xs.ExpandAndDeQuerRef(context);
                    newselection.AddRange(list);
                }
                else
                {
                    x.Bind(context);
                    if (x.HasAggFunc())
                        hasAgg_ = true;
                    x = x.ConstFolding();
                    if (queryOpt_.optimize_.remove_from_)
                        x = x.DeQueryRef();
                    newselection.Add(x);
                }
            }
            Debug.Assert(newselection.Count(x => x is SelStar) == 0);
            Debug.Assert(newselection.Count >= selection_.Count);
            return newselection;
        }

        internal void BindWithContext(BindContext context)
        {
            // we don't consider setops in this level
            Debug.Assert(setops_ is null);

            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // binding order:
            //  - from binding shall be the first since it may create new alias
            //  - groupby/orderby may reference selection list's alias, so let's 
            //    expand them first, but sequence item is handled after selection list bounded
            //
            bindFrom(context);

            selection_ = bindSelectionList(context);
            
            if (where_ != null)
            {
                where_.Bind(context);
                where_ = where_.FilterNormalize();
                if (!where_.IsBoolean() || where_.HasAggFunc())
                    throw new SemanticAnalyzeException(
                        "WHERE condition must be a blooean expression and no aggregation is allowed");
                if (queryOpt_.optimize_.remove_from_)
                    where_ = where_.DeQueryRef();
            }

            if (groupby_ != null)
            {
                hasAgg_ = true;
                groupby_ = bindOrderByOrGroupBy(context, groupby_);
                if (groupby_.Any(x => x.HasAggFunc()))
                    throw new SemanticAnalyzeException("aggregation functions are not allowed in group by clause");
            }

            if (having_ != null)
            {
                hasAgg_ = true;
                having_.Bind(context);
                having_ = having_.FilterNormalize();
                if (!having_.IsBoolean())
                    throw new SemanticAnalyzeException("HAVING condition must be a blooean expression");
                if (queryOpt_.optimize_.remove_from_)
                    having_ = having_.DeQueryRef();
            }

            if (orders_ != null)
                orders_ = bindOrderByOrGroupBy(context, orders_);

            Normalize();
        }

        void bindTableRef(BindContext context, TableRef table)
        {
            switch (table)
            {
                case BaseTableRef bref:
                    Debug.Assert(Catalog.systable_.TryTable(bref.relname_) != null);
                    context.RegisterTable(bref);
                    break;
                case ExternalTableRef eref:
                    if (Catalog.systable_.TryTable(eref.baseref_.relname_) != null)
                        context.RegisterTable(eref);
                    else
                        throw new SemanticAnalyzeException($@"base table '{eref.baseref_.relname_}' not exists");
                    break;
                case QueryRef qref:
                    if (qref.query_.bindContext_ is null)
                        qref.query_.Bind(context);

                    if (qref is FromQueryRef qf)
                        qf.CreateOutputNameMap();

                    // the subquery itself in from clause can be seen as a new table, so register it here
                    context.RegisterTable(qref);
                    break;
                case JoinQueryRef jref:
                    jref.tables_.ForEach(x => bindTableRef(context, x));
                    jref.constraints_.ForEach(x => x.Bind(context));
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        CTEQueryRef chainUpToFindCte(BindContext context, string ctename, string alias)
        {
            var parent = context;
            do
            {
                CteExpr cte;
                var topctes = (parent.stmt_ as SelectStmt).ctes_;
                if (topctes != null &&
                    (cte = topctes.Find(x => x.cteName_.Equals(ctename))) != null)
                {
                    return new CTEQueryRef(cte, alias);
                }
            } while ((parent = parent.parent_) != null);
            return null;
        }

        void bindFrom(BindContext context)
        {
            // replace any BaseTableRef that can't find in system to CTE
            for (int i = 0; i < from_.Count; i++)
            {
                var x = from_[i];
                if (x is BaseTableRef bref &&
                    Catalog.systable_.TryTable(bref.relname_) is null)
                {
                    from_[i] = chainUpToFindCte(context, bref.relname_, bref.alias_);
                    if (from_[i] is null)
                        throw new SemanticAnalyzeException($@"table '{bref.relname_}' not exists");
                }
            }

            // no duplicated table without alias not allowed
            var query = from_.GroupBy(x => x.alias_).Where(g => g.Count() > 1).Select(y => y.Key).ToList();
            if (query.Count > 0)
                throw new SemanticAnalyzeException($"table name '{query[0]}' specified more than once");
            from_.ForEach(x => bindTableRef(context, x));
        }

        // for each expr in @list, if expr has references an alias in selection list, 
        // replace that with the true expression.
        // example:
        //      selection_: a1*5 as alias1, a2, b3, 12(=3*4, const already fold)
        //      orders_: alias1+b => a1*5+b
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
                    if (s.outputName_ != null)
                        newv = newv.SearchAndReplace(s.outputName_, s, false);
                }
                newlist.Add(newv);
            }

            Debug.Assert(newlist.Count == list.Count);
            return newlist;
        }

        public void Normalize()
        {
            for (int i = 0; i < selection_.Count; ++i)
            {
                Expr x = selection_[i];
                x = x.Normalize();
                selection_[i] = x;
            }

            if (where_ != null)
            {
                Expr x = where_.Normalize();
                if (x is ConstExpr ce && (ce.val_ is null || ce.val_ == "false"))
                {
                    // Normalization elimintaed WHERE which is always FALSE

                    Expr f = ConstExpr.MakeConstBool(false);
                    Expr t = ConstExpr.MakeConstBool(true);
                    where_ = BinExpr.MakeBooleanExpr(f, t, "=");
                }
            }

            if (groupby_ != null)
            {
                // normalize group by
            }

            if (having_ != null)
            {
                // normalize having
            }

            if (orders_ != null)
            {
                // normalize orders
            }
        }
    }
}

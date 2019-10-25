using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace adb
{
    public abstract class PlanNode<T> where T : PlanNode<T>
    {
        public List<T> children_ = new List<T>();

        // print utilities
        public virtual string PrintOutput(int depth) => null;
        public virtual string PrintInlineDetails(int depth) => null;
        public virtual string PrintMoreDetails(int depth) => null;
        protected string PrintFilter (Expr filter, int depth)
        {
            string r = null;
            if (filter != null)
            {
                r = "Filter: " + filter.PrintString(depth);
                // append the subquery plan align with filter
                r += ExprHelper.PrintExprWithSubqueryExpanded(filter, depth);
            }
            return r;
        }

        public string PrintString(int depth)
        {
            string r = null;
            if (!(this is PhysicProfiling))
            {
                r = Utils.Tabs(depth);
                if (depth != 0)
                    r += "-> ";
                r += $"{this.GetType().Name} {PrintInlineDetails(depth)}";
                if (this is PhysicNode && (this as PhysicNode).profile_ != null)
                    r += $"  (rows = {(this as PhysicNode).profile_.nrows_})";
                r += "\n";
                var details = PrintMoreDetails(depth);
                r += Utils.Tabs(depth + 2) + PrintOutput(depth) + "\n";
                if (details != null)
                {
                    // remove the last \n in case the details is a subquery
                    var trailing = "\n";
                    if (details[details.Length - 1] == '\n')
                        trailing = "";
                    r += Utils.Tabs(depth + 2) + details + trailing;
                }

                depth += 2;
            }

            children_.ForEach(x => r += x.PrintString(depth));
            return r;
        }

        // traversal pattern EXISTS
        //  if any visit returns a true, stop recursion. So if you want to
        //  visit all nodes, your callback shall always return false
        //
        public bool VisitEachNodeExists(Func<PlanNode<T>, bool> callback)
        {
            if (callback(this))
                return true;

            foreach (var c in children_)
                if (c.VisitEachNodeExists(callback))
                    return true;
            return false;
        }

        // traversal pattern FOR EACH
        public void ForEachNode(Action<PlanNode<T>> callback)
        {
            callback(this);
            foreach (var c in children_)
                c.ForEachNode(callback);
        }

        public int FindNode<T1, T2>(out T2 parent, out T1 target) where T2: PlanNode<T>
        {
            int cnt = 0;
            T2 p = default(T2); 
            T1 t = default(T1);
            ForEachNode(x =>
            {
                x.children_.ForEach(y =>
                {
                    if (y is T1 yf)
                    {
                        cnt++;
                        t = yf;
                        p = x as T2;
                    }
                });
            });

            if (cnt == 0)
            {
                if (this is T1 yf)
                {
                    cnt++;
                    t = yf;
                    p = default(T2);
                }
            }

            parent = p; target = t;
            return cnt;
        }
    }

    public class ProfileOption
    {
        public bool enabled_ = false;
    }

    public abstract class LogicNode : PlanNode<LogicNode>
    {
        public List<Expr> output_ = new List<Expr>();

        public override string PrintOutput(int depth)
        {
            string r = "Output: " + string.Join(",", output_);
            output_.ForEach(x => r += ExprHelper.PrintExprWithSubqueryExpanded(x, depth));
            return r;
        }

        // This is an honest translation from logic to physical plan
        public PhysicNode DirectToPhysical(ProfileOption profiling)
        {
            PhysicNode root = null;
            ForEachNode(n =>
            {
                PhysicNode phy;
                switch (n)
                {
                    case LogicGetTable ln:
                        phy = new PhysicGetTable(ln);
                        if (ln.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(ln.filter_);
                        break;
                    case LogicJoin lc:
                        phy = new PhysicNLJoin(lc,
                            lc.children_[0].DirectToPhysical(profiling),
                            lc.children_[1].DirectToPhysical(profiling));
                        break;
                    case LogicResult lr:
                        phy = new PhysicResult(lr);
                        break;
                    case LogicFromQuery ls:
                        phy = new PhysicFromQuery(ls, ls.children_[0].DirectToPhysical(profiling));
                        break;
                    case LogicFilter lf:
                        phy = new PhysicFilter(lf, lf.children_[0].DirectToPhysical(profiling));
                        if (lf.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(lf.filter_);
                        break;
                    case LogicInsert li:
                        phy = new PhysicInsert(li, li.children_[0].DirectToPhysical(profiling));
                        break;
                    case LogicGetExternal le:
                        phy = new PhysicGetExternal(le);
                        break;
                    case LogicAgg la:
                        phy = new PhysicHashAgg(la, la.children_[0].DirectToPhysical(profiling));
                        break;
                    case LogicOrder lo:
                        phy = new PhysicOrder(lo, lo.children_[0].DirectToPhysical(profiling));
                        break;
                    default:
                        throw new NotImplementedException();
                }

                if (profiling.enabled_)
                    phy = new PhysicProfiling(phy);

                if (root is null)
                    root = phy;
            });

            return root;
        }

        public virtual List<TableRef> InclusiveTableRefs()
        {
            List<TableRef> refs = new List<TableRef>();
            children_.ForEach(x => refs.AddRange(x.InclusiveTableRefs()));
            return refs;
        }

        // resolve mapping from children output
        // 1. you shall first compute the reqOutput by accouting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        public virtual List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true) => null;
        internal void ClearOutput()
        {
            output_ = new List<Expr>();
            children_.ForEach(x => x.ClearOutput());
        }

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source)
        {
            var clone = toclone.Clone();

            // first try to match the whole expression - don't do this for ColExpr
            // because it has no practial benefits.
            // 
            if (!(clone is ColExpr))
            {
                int ordinal = source.FindIndex(clone.Equals);
                if (ordinal != -1)
                    return new ExprRef(clone, ordinal);
            }

            // we have to use each ColExpr and fix its ordinal
            clone.VisitEachExpr(y =>
            {
                if (y is ColExpr target)
                {
                    Predicate<Expr> nameTest;
                    nameTest = z => target.Equals(z) || y.alias_.Equals(z.alias_);

                    // using source's matching index for ordinal
                    // fix colexpr's ordinal - leave the outerref
                    if (!target.isOuterRef_)
                    {
                        target.ordinal_ = source.FindIndex(nameTest);
                        Debug.Assert(source.FindAll(nameTest).Count == 1);
                    }
                    Debug.Assert(target.ordinal_ != -1);
                }
            });

            return clone;
        }

        // fix each expression by using source's ordinal and make a copy
        internal List<Expr> CloneFixColumnOrdinal(List<Expr> toclone, List<Expr> source)
        {
            var clone = new List<Expr>();
            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source)));
            Debug.Assert(clone.Count == toclone.Count);
            return clone;
        }
    }

    public class LogicJoin : LogicNode
    {
        internal Expr filter_;

        public override string ToString() => $"{children_[0]} X {children_[1]}";
        public LogicJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }
        public override string PrintMoreDetails(int depth) => PrintFilter(filter_, depth);

        public bool AddFilter(Expr filter)
        {
            filter_ = filter_ is null ? filter :
                new LogicAndExpr(filter_, filter);
            return true;
        }

        public override List<TableRef> InclusiveTableRefs()
        {
            List<TableRef> refs = new List<TableRef>();
            ForEachNode(x =>
            {
                if (x is LogicGetTable gx)
                    refs.Add(gx.tabref_);
                else if (x is LogicFromQuery fx)
                    refs.Add(fx.queryRef_);
            });
            return refs;
        }
        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>(reqOutput);
            if (filter_ != null)
                reqFromChild.Add(filter_);

            // push to left and right: to which side depends on the TableRef it contains
            var ltables = children_[0].InclusiveTableRefs();
            var rtables = children_[1].InclusiveTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();
            foreach (var v in reqFromChild)
            {
                var tables = ExprHelper.AllTableRef(v);

                if (Utils.ListAContainsB(ltables, tables))
                    lreq.Add(v);
                else if (Utils.ListAContainsB(rtables, tables))
                    rreq.Add(v);
                else
                {
                    // the whole list can't push to the children (Eg. a.a1 + b.b1)
                    // decompose to singleton and push down
                    var colref = ExprHelper.AllColExpr(v);
                    colref.ForEach(x =>
                    {
                        if (ltables.Contains(x.tabRef_))
                            lreq.Add(x);
                        else if (rtables.Contains(x.tabRef_))
                            rreq.Add(x);
                        else
                            throw new InvalidProgramException("contains invalid tableref");
                    });
                }
            }

            // get left and right child to resolve columns
            children_[0].ResolveChildrenColumns(lreq.ToList());
            var lout = children_[0].output_;
            children_[1].ResolveChildrenColumns(rreq.ToList());
            var rout = children_[1].output_;
            Debug.Assert(lout.Intersect(rout).Count() == 0);

            // assuming left output first followed with right output
            var childrenout = lout.ToList(); childrenout.AddRange(rout.ToList());
            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, childrenout);
            output_ = CloneFixColumnOrdinal(reqOutput, childrenout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicFilter : LogicNode
    {
        internal Expr filter_;

        public override string PrintMoreDetails(int depth) => PrintFilter(filter_, depth);

        public LogicFilter(LogicNode child, Expr filter)
        {
            children_.Add(child); filter_ = filter;
        }

        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(ExprHelper.CloneList(reqOutput));
            reqFromChild.AddRange(ExprHelper.AllColExpr(filter_));
            children_[0].ResolveChildrenColumns(reqFromChild);
            var childout = children_[0].output_;

            filter_ = CloneFixColumnOrdinal(filter_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();

            return ordinals;
        }
    }

    public class LogicAgg : LogicNode
    {
        internal List<Expr> keys_;
        internal Expr having_;

        // runtime info: derived from output request
        internal List<Expr> aggrCore_ = new List<Expr>();

        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (aggrCore_ != null)
                r += $"Agg Core: {string.Join(", ", aggrCore_)}\n";
            if (keys_ != null)
                r += $"Group by: {string.Join(", ", keys_)}\n";
            if (having_ != null)
                r += Utils.Tabs(depth + 2) + $"{PrintFilter(having_, depth)}";
            return r;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, List<Expr> aggrs, Expr having)
        {
            children_.Add(child); keys_ = groupby; having_ = having;
        }

        List<Expr> removeAggFuncFromOutput(List<Expr> reqOutput) {
            var reqList = ExprHelper.CloneList(reqOutput, new List<Type> {typeof(LiteralExpr)});
            var aggs = new List<Expr>();
            reqList.ForEach(x =>
                x.VisitEachExpr(y =>
                {
                    // 1+abs(min(a))+max(b)
                    if (y is AggFunc ay)
                        aggs.Add(x);
                }));

            // aggs remove functions
            aggs.ForEach(x => {
                reqList.Remove(x);
                bool check = false;
                x.VisitEachExpr(y => {
                    if (y is AggFunc ay)
                    {
                        check = true;
                        reqList.AddRange(ay.GetNonFuncExprList());
                    }
                });
                Debug.Assert(check);
            });

            return reqList;
        }

        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            
            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(removeAggFuncFromOutput(reqOutput));
            if (keys_ != null) reqFromChild.AddRange(ExprHelper.AllColExpr(keys_));
            children_[0].ResolveChildrenColumns(reqFromChild);
            var childout = children_[0].output_;

            if (keys_ != null) keys_ = CloneFixColumnOrdinal(keys_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();

            // Bound aggrs to output, so when we computed aggrs, we automatically get output
            // Here is an example:
            //  output_: <literal>, cos(a1*7)+sum(a1),  sum(a1) + sum(a2+a3)*2
            //                       |           \       /          |   
            //                       |            \     /           |   
            //  keys_:               a1            \   /            |
            //  aggrCore_:                        sum(a1),      sum(a2+a3)
            // =>
            //  output_: <literal>, cos(ref[0]*7)+ref[1],  ref[1]+ref[2]*2
            //
            var nkeys = keys_?.Count??0;
            var newoutput = new List<Expr>();
            if (keys_ != null) output_ = Utils.SearchReplace(output_, keys_);
            output_.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    if (y is AggFunc ya)
                    {
                        // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                        if (!aggrCore_.Contains(ya))
                            aggrCore_.Add(ya);
                        x = x.SearchReplace(y, new ExprRef(y, nkeys + aggrCore_.IndexOf(y)));
                    }
                });

                newoutput.Add(x);
            });
            Debug.Assert(aggrCore_.Count == aggrCore_.Distinct().Count());

            // Say invvalid expression means contains colexpr, then the output shall contains
            // no expression consists invalid expression
            //
            Expr offending = null;
            newoutput.ForEach(x=>{
                if (x.VisitEachExprExists(y => y is ColExpr, new List<Type> { typeof(ExprRef)}))
                    offending = x;
            });
            if (offending != null)
                throw new SemanticAnalyzeException($"column {offending} must appear in group by clause");
            output_ = newoutput;

            return ordinals;
        }

    }

    public class LogicOrder : LogicNode {
        internal List<Expr> orders_ = new List<Expr>();
        internal List<bool> descends_ = new List<bool>();
        public override string PrintMoreDetails(int depth)
        {
            var r = $"Order by: {string.Join(", ", orders_)}\n";
            return r;
        }

        public LogicOrder(LogicNode child, List<Expr> orders, List<bool> descends)
        {
            children_.Add(child);
            orders_ = orders;
            descends_ = descends;
        }

        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(ExprHelper.CloneList(reqOutput));
            reqFromChild.AddRange(orders_);
            children_[0].ResolveChildrenColumns(reqFromChild);
            var childout = children_[0].output_;

            orders_ = CloneFixColumnOrdinal(orders_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicFromQuery : LogicNode
    {
        public FromQueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>";
        public override string PrintInlineDetails(int depth) => $"<{queryRef_.alias_}>";
        public LogicFromQuery(FromQueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<TableRef> InclusiveTableRefs() {
            var r = new List<TableRef>();
            r.Add(queryRef_);
            r.AddRange(queryRef_.query_.bindContext_.AllTableRefs());
            return r;
        }
        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            var query = queryRef_.query_;
            query.logicPlan_.ResolveChildrenColumns(query.selection_);

            var childout = queryRef_.AllColumnsRefs();
            output_ = CloneFixColumnOrdinal(reqOutput, childout);

            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = queryRef_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicGet<T> : LogicNode where T : TableRef
    {
        public T tabref_;
        public Expr filter_;

        public LogicGet(T tab) => tabref_ = tab;
        public override string ToString() => tabref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();
        public override string PrintMoreDetails(int depth) => PrintFilter(filter_, depth);
        public bool AddFilter(Expr filter)
        {
            filter_ = filter_ is null ? filter :
                new LogicAndExpr(filter_, filter);
            return true;
        }
        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            // verify it can be an litral, or only uses my tableref
            reqOutput.ForEach(x =>
            {
                x.VisitEachExpr(y => {
                    switch (y)
                    {
                        case LiteralExpr ly:    // select 2+3, ...
                        case SubqueryExpr sy:   // select ..., sx = (select b1 from b limit 1) from a;
                            break;
                        default:
                            // aggfunc shall never pushed to me
                            Debug.Assert(!(y is AggFunc));

                            // it can be a single table, or single table computation say "c1+c2+7"
                            y.EqualTableRef(tabref_);
                            break;
                    }
                });
            });

            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, tabref_.AllColumnsRefs());
            output_ = CloneFixColumnOrdinal(reqOutput, tabref_.AllColumnsRefs());


            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = tabref_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
        public override List<TableRef> InclusiveTableRefs() => new List<TableRef> { tabref_ };
    }

    public class LogicGetTable : LogicGet<BaseTableRef>
    {
        public LogicGetTable(BaseTableRef tab) : base(tab) { }
    }

    public class LogicGetExternal : LogicGet<ExternalTableRef>
    {
        public string FileName() => tabref_.filename_;
        public LogicGetExternal(ExternalTableRef tab) : base(tab) { }
    }

    public class LogicInsert : LogicNode
    {
        public BaseTableRef targetref_;
        public LogicInsert(BaseTableRef targetref, LogicNode child)
        {
            targetref_ = targetref;
            children_.Add(child);
        }
        public override string ToString() => targetref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();

        public override List<int> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            Debug.Assert(output_.Count == 0);

            // insertion is always the top node 
            Debug.Assert(!removeRedundant);
            return children_[0].ResolveChildrenColumns(reqOutput, removeRedundant);
        }
    }

    public class LogicResult : LogicNode
    {
        public override string ToString() => string.Join(",", output_);
        public LogicResult(List<Expr> exprs) => output_ = exprs;
        public override List<TableRef> InclusiveTableRefs() => null;
    }
}

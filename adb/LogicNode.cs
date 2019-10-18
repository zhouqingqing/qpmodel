using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace adb
{
    public abstract class PlanNode<T> where T : PlanNode<T>
    {
        public List<T> children_ = new List<T>();

        // print utilities
        public virtual string PrintOutput(int depth) => null;
        public virtual string PrintInlineDetails(int depth) => null;
        public virtual string PrintMoreDetails(int depth) => null;
        public string PrintString(int depth)
        {
            string r = null;
            if (!(this is PhysicProfiling))
            {
                r = Utils.Tabs(depth);
                if (depth != 0)
                    r += "-> ";
                r += this.GetType().Name + " " + PrintInlineDetails(depth);
                if (this is PhysicNode && (this as PhysicNode).profile_ != null)
                    r += $@"  (rows = {(this as PhysicNode).profile_.nrows_})";
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
    }

    public class ProfileOption
    {
        internal bool enabled_ = false;
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
                    case LogicCrossJoin lc:
                        phy = new PhysicCrossJoin(lc,
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

        public virtual List<TableRef> EnumTableRefs()
        {
            List<TableRef> refs = new List<TableRef>();
            children_.ForEach(x => refs.AddRange(x.EnumTableRefs()));
            return refs;
        }

        // resolve mapping from children output
        // 1. you shall first compute the reqOutput by accouting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        public virtual List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true) => null;
        internal void ClearOutput()
        {
            output_ = new List<Expr>();
            children_.ForEach(x => x.ClearOutput());
        }

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source, bool ignoreTable = true)
        {
            var clone = toclone.Clone();
            clone.VisitEachExpr(y =>
            {
                if (y is ColExpr target)
                {
                    Predicate<Expr> nameTest;
                    if (ignoreTable)
                        nameTest = z => (z as ColExpr)?.colName_.Equals(target.colName_) ?? false;
                    else
                        nameTest = z => z.Equals(target);

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
        internal List<Expr> CloneFixColumnOrdinal(List<Expr> toclone, List<Expr> source, bool ignoreTable = true)
        {
            var clone = new List<Expr>();
            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source, ignoreTable)));
            return clone;
        }
    }

    public class LogicCrossJoin : LogicNode
    {
        public LogicCrossJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }

        public override List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            // push to left and right: to which side depends on the TableRef it contains
            var ltables = children_[0].EnumTableRefs();
            var rtables = children_[1].EnumTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();
            foreach (var v in reqOutput)
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
                    var colref = ExprHelper.AllColExpr(v, false);
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
            var lout = children_[0].ResolveChildrenColumns(lreq.ToList());
            var rout = children_[1].ResolveChildrenColumns(rreq.ToList());
            // assuming left output first followed with right output
            var childrenout = lout.ToList(); childrenout.AddRange(rout.ToList());
            output_ = CloneFixColumnOrdinal(reqOutput, childrenout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return output_;
        }
    }

    public class LogicFilter : LogicNode
    {
        internal Expr filter_;

        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (filter_ != null)
            {
                r += "Filter: " + filter_.PrintString(depth);
                // append the subquery plan align with filter
                r += ExprHelper.PrintExprWithSubqueryExpanded(filter_, depth);
            }
            return r;
        }

        public LogicFilter(LogicNode child, Expr filter)
        {
            children_.Add(child); filter_ = filter;
        }

        public override List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqOutput.ForEach(x => reqFromChild.AddRange(ExprHelper.AllColExpr(x, false)));
            reqFromChild.AddRange(ExprHelper.AllColExpr(filter_, false));
            var childout = children_[0].ResolveChildrenColumns(reqFromChild);

            filter_ = CloneFixColumnOrdinal(filter_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();

            return output_;
        }
    }

    public class LogicAgg : LogicNode
    {
        internal List<Expr> groupby_;
        internal List<Expr> aggr_;
        internal Expr having_;

        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (groupby_ != null)
                r += $"Group by: {string.Join(", ", groupby_)}\n";
            if (having_ != null)
                r += Utils.Tabs(depth + 2) + $"Filter: {having_}";
            return r;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, Expr having)
        {
            children_.Add(child); groupby_ = groupby; having_ = having;
        }
    }

    public class LogicFromQuery : LogicNode
    {
        public FromQueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>";
        public override string PrintInlineDetails(int depth) => $"<{queryRef_.alias_}>";
        public LogicFromQuery(FromQueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<TableRef> EnumTableRefs() => queryRef_.query_.bindContext_.AllTableRefs();
        public override List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            var query = queryRef_.query_;
            var childout = query.logicPlan_.ResolveChildrenColumns(query.selection_);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);

            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = queryRef_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return output_;
        }
    }

    public class LogicGet<T> : LogicNode where T : TableRef
    {
        public T tabref_;
        public Expr filter_;

        public LogicGet(T tab) => tabref_ = tab;
        public override string ToString() => tabref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();
        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (filter_ != null)
            {
                r += "Filter: " + filter_.PrintString(depth);
                // append the subquery plan align with filter
                r += ExprHelper.PrintExprWithSubqueryExpanded(filter_, depth);
            }
            return r;
        }
        public bool AddFilter(Expr filter)
        {
            filter_ = filter_ is null ? filter :
                new LogicAndExpr(filter_, filter);
            return true;
        }
        public override List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            // it can be an litral, or only uses my tableref
            reqOutput.ForEach(x =>
            {
                switch (x)
                {
                    case LiteralExpr lx:
                    case SubqueryExpr sx:
                        break;
                    default:
                        Debug.Assert(ExprHelper.AllTableRef(x).Count == 1);
                        Debug.Assert(ExprHelper.AllTableRef(x)[0].Equals(tabref_));
                        break;
                }
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
            return output_;
        }
        public override List<TableRef> EnumTableRefs() => new List<TableRef> { tabref_ };
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

        public override List<Expr> ResolveChildrenColumns(List<Expr> reqOutput, bool removeRedundant = true)
        {
            children_[0].ResolveChildrenColumns(reqOutput, removeRedundant);
            return output_;
        }
    }

    public class LogicResult : LogicNode
    {
        public override string ToString() => string.Join(",", output_);
        public LogicResult(List<Expr> exprs) => output_ = exprs;
        public override List<TableRef> EnumTableRefs() => null;
    }
}

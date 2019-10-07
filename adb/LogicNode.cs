﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public abstract class PlanNode<T> where T : PlanNode<T>
    {
        public List<T> children_ = new List<T>();

        // print utilities
        internal string tabs(int depth) => new string(' ', depth * 2);
        public virtual string PrintOutput(int depth) { return null; }
        public virtual string PrintInlineDetails(int depth) { return null; }
        public virtual string PrintMoreDetails(int depth) { return null; }
        public string PrintString(int depth)
        {
            string r = tabs(depth);

            if (depth != 0)
                r += "-> ";
            r += this.GetType().Name + " " + PrintInlineDetails(depth) + "\n";
            var details = PrintMoreDetails(depth);
            r += tabs(depth + 2) + PrintOutput(depth) + "\n";
            if (details != null)
            {
                // remove the last \n in case the details is a subquery
                var trailing = "\n";
                if (details[details.Length - 1] == '\n')
                    trailing = "";
                r += tabs(depth + 2) + details + trailing;
            }

            depth++;
            children_.ForEach(x => r += x.PrintString(depth));
            return r;
        }

        // traversal pattern 
        //  if any visit returns a true, stop recursion. So if you want to
        //  visit all nodes, your callback shall always return false
        //
        public bool VisitEachNode(Func<PlanNode<T>, bool> callback)
        {
            bool r = callback(this);

            if (!r)
            {
                foreach (var c in children_)
                    if (c.VisitEachNode(callback))
                        return true;
                return false;
            }
            return true;
        }
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
        public PhysicNode DirectToPhysical()
        {
            PhysicNode root = null;
            VisitEachNode(n =>
            {
                PhysicNode phy = null;
                switch (n)
                {
                    case LogicGet ln:
                        phy = new PhysicGet(ln);
                        if (ln.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(ln.filter_);
                        break;
                    case LogicCrossJoin lc:
                        phy = new PhysicCrossJoin(lc,
                            lc.children_[0].DirectToPhysical(),
                            lc.children_[1].DirectToPhysical());
                        break;
                    case LogicResult lr:
                        phy = new PhysicResult(lr);
                        break;
                    case LogicFromQuery ls:
                        phy = new PhysicFromQuery(ls, ls.children_[0].DirectToPhysical());
                        break;
                    case LogicFilter lf:
                        phy = new PhysicFilter(lf, lf.children_[0].DirectToPhysical());
                        if (lf.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(lf.filter_);
                        break;
                }

                if (root is null)
                    root = phy;
                return false;
            });

            return root;
        }

        public virtual List<TableRef> EnumTableRefs() {
            List<TableRef> refs = new List<TableRef>();
            children_.ForEach(x => refs.AddRange(x.EnumTableRefs()));
            return refs;
        }

        // what columns this node requires from its children
        public virtual void ResolveChildrenColumns(List<Expr> reqOutput) {}
        internal void ClearOutput() {
            output_ = new List<Expr>();
            children_.ForEach(x => x.ClearOutput());
        }
        // fix each expression by using source's ordinal
        internal List<Expr> FixColumnOrdinal(bool ignoreTable, List<Expr> tofix, List<Expr> source)
        {
            tofix.ForEach(x =>
            {
                x.VisitEachExpr(y => {
                    if (y is ColExpr yc)
                    {
                        Predicate<Expr> nameTest;
                        if (ignoreTable)
                            nameTest = z => (z as ColExpr)?.colName_.Equals(yc.colName_)??false;
                        else
                            nameTest = z => z.Equals(yc);

                        // fix colexpr's ordinal
                        yc.ordinal_ = source.FindIndex(nameTest);
                        Debug.Assert(yc.ordinal_ != -1);
                        if (source.FindAll(nameTest).Count > 1)
                            throw new SemanticAnalyzeException("ambigous column name");
                    }
                    return false;
                });
            });

            return tofix;
        }
    }

    public class LogicCrossJoin : LogicNode
    {
        public LogicCrossJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }

        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            // push to left and right: to which side depends on the TableRef it contains
            var lrefs = children_[0].EnumTableRefs();
            var rrefs = children_[1].EnumTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();
            foreach (var v in reqOutput) {
                var refs = ExprHelper.EnumAllTableRef(v);

                if (Utils.ListAContainsB(lrefs, refs))
                    lreq.Add(v);
                else if (Utils.ListAContainsB(rrefs, refs))
                    rreq.Add(v);
                else
                {
                    // the whole list can't push to the children (Eg. a.a1 + b.b1)
                    // decompose to singleton and push down
                    var colref = ExprHelper.EnumAllColExpr(v, false);
                    colref.ForEach(x =>
                    {
                        if (lrefs.Contains((x as ColExpr).tabRef_))
                            lreq.Add(x);
                        else if (rrefs.Contains((x as ColExpr).tabRef_))
                            rreq.Add(x);
                        else
                            throw new InvalidProgramException("contains invalid tableref");
                    });
                }
            }

            // get left and right child to resolve columns
            children_[0].ResolveChildrenColumns(lreq.ToList());
            children_[1].ResolveChildrenColumns(rreq.ToList());
            var newlist = lreq.ToList(); newlist.AddRange(rreq.ToList());
            output_.AddRange(FixColumnOrdinal(true, ExprHelper.CloneExprList(reqOutput), newlist));
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

        public LogicFilter(LogicNode child, Expr filter) {
            children_.Add(child); filter_ = filter;
        }

        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            List<Expr> tofix = new List<Expr>();
            reqOutput.ForEach(x => tofix.AddRange(ExprHelper.CloneExprList(ExprHelper.EnumAllColExpr(x, false))));
            tofix.AddRange(ExprHelper.CloneExprList(ExprHelper.EnumAllColExpr(filter_, false)));
            children_[0].ResolveChildrenColumns(tofix.Distinct().ToList());

            output_.AddRange(FixColumnOrdinal(true, ExprHelper.CloneExprList(tofix), tofix.Distinct().ToList()));
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
                r += tabs(depth +2) + $"Filter: {having_}";
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

        public override List<TableRef> EnumTableRefs() => queryRef_.query_.cores_[0].BinContext().EnumTableRefs();
        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            List<Expr> tofix = new List<Expr>();
            tofix.AddRange(ExprHelper.CloneExprList(reqOutput));

            queryRef_.query_.GetLogicPlan().ResolveChildrenColumns(queryRef_.query_.Selection());
            output_.AddRange(FixColumnOrdinal(true, tofix, queryRef_.query_.Selection()));
        }
    }

    public class LogicGet : LogicNode
    {
        public BaseTableRef tabref_;
        public Expr filter_;

        public LogicGet(BaseTableRef tab) => tabref_ = tab;
        public override string ToString() => tabref_.alias_;
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
            if (filter_ is null)
                filter_ = filter;
            else
                filter_ = new LogicAndExpr(filter_, filter);
            return true;
        }

        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            // it can be an litral, or only uses my tableref
            reqOutput.ForEach(x =>
            {
                switch (x) {
                    case LiteralExpr lx:
                    case SubqueryExpr sx:
                        break;
                    default:
                        Debug.Assert(ExprHelper.EnumAllTableRef(x).Count == 1);
                        Debug.Assert(ExprHelper.EnumAllTableRef(x)[0].Equals(tabref_));
                        break;
                }
            });

            // don't need to include columns it uses (say filter) for output. Also, no need
            // to make copy of reqOutput since it is bottom and won't change anyway.
            //
			output_.AddRange(reqOutput);
        }

        public override List<TableRef> EnumTableRefs() => new List<TableRef>{tabref_};
    }

    public class LogicResult : LogicNode
    {
        internal List<Expr> exprs_;

        public override string ToString()=> string.Join(",", exprs_);
        public LogicResult(List<Expr> exprs) => exprs_ = exprs;
        public override string PrintMoreDetails(int depth) => "Expr: " + ToString();
        public override List<TableRef> EnumTableRefs() => null;
    }
}

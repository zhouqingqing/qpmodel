using System;
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

        public override string PrintOutput(int depth) => "Output: " + string.Join(",", output_);

        // This is an honest translation from logic to physical plan
        public PhysicNode SimpleConvertPhysical()
        {
            PhysicNode root = null;
            VisitEachNode(n =>
            {
                PhysicNode phy = null;
                switch (n)
                {
                    case LogicGet ln:
                        phy = new PhysicGet(ln);
                        break;
                    case LogicCrossJoin lc:
                        phy = new PhysicCrossJoin(lc,
                            lc.children_[0].SimpleConvertPhysical(),
                            lc.children_[1].SimpleConvertPhysical());
                        break;
                    case LogicResult lr:
                        phy = new PhysicResult(lr);
                        break;
                    case LogicSubquery ls:
                        phy = new PhysicSubquery(ls, ls.children_[0].SimpleConvertPhysical());
                        break;
                    case LogicFilter lf:
                        phy = new PhysicFilter(lf, lf.children_[0].SimpleConvertPhysical());
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
        public virtual void ResolveChildrenColumns(List<Expr> reqOutput) {
            output_.AddRange(reqOutput);
            output_ = output_.Distinct().ToList();
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

            foreach (var v in reqOutput) {
                var refs = ExprHelper.EnumAllTableRef(v);

                if (Utils.ListAContainsB(lrefs, refs))
                    children_[0].ResolveChildrenColumns(new List<Expr> { v });
                else if (Utils.ListAContainsB(rrefs, refs))
                    children_[1].ResolveChildrenColumns(new List<Expr> { v });
                else
                {
                    // the whole list can't push to the children (Eg. a.a1 + b.b1)
                    // decompose to singleton and push down
                    var colref = ExprHelper.EnumAllColExpr(v, false);
                    colref.ForEach(x=> {
                        if (lrefs.Contains(x.tabRef_))
                            children_[0].ResolveChildrenColumns(new List<Expr> { x });
                        else if (rrefs.Contains(x.tabRef_))
                            children_[1].ResolveChildrenColumns(new List<Expr> { x });
                        else
                            throw new InvalidProgramException("contains invalid tableref");
                    });

                }
            }

            base.ResolveChildrenColumns(reqOutput);
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
                if (filter_.HasSubQuery())
                {
                    r += "\n";
                    filter_.VisitEachExpr(x =>
                    {
                        if (x is SubqueryExpr sx)
                        {
                            r += tabs(depth + 2) + $"<SubLink> {sx.subqueryid_}\n";
                            Debug.Assert(sx.query_.cores_[0].BinContext() != null);
                            r += $"{sx.query_.GetPlan().PrintString(depth + 2)}";
                        }
                        return false;
                    });

                }
            }
            return r;
        }

        public LogicFilter(LogicNode child, Expr filter) {
            children_.Add(child); filter_ = filter;
        }

        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            List<Expr> newreq = new List<Expr>(reqOutput);
            newreq.AddRange(ExprHelper.EnumAllColExpr(filter_, false));
            children_[0].ResolveChildrenColumns(newreq.Distinct().ToList());

            base.ResolveChildrenColumns(reqOutput);
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

    public class LogicSubquery : LogicNode
    {
        public SubqueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>";
        public LogicSubquery(SubqueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<TableRef> EnumTableRefs() => queryRef_.query_.cores_[0].BinContext().EnumTableRefs();
        public override void ResolveChildrenColumns(List<Expr> reqOutput)
        {
            queryRef_.query_.GetPlan().ResolveChildrenColumns(queryRef_.query_.Selection());
            base.ResolveChildrenColumns(reqOutput);
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
            if (filter_ != null)
                return "Filter: " + filter_.PrintString(depth);
            return null;
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
                        break;
                    default:
                        Debug.Assert(ExprHelper.EnumAllTableRef(x).Count == 1);
                        Debug.Assert(ExprHelper.EnumAllTableRef(x)[0].Equals(tabref_));
                        break;
                }
            });

            // don't need to include columns it uses (say filter) for output
            base.ResolveChildrenColumns(reqOutput);
        }

        public override List<TableRef> EnumTableRefs() => new List<TableRef>{tabref_};
    }

    public class LogicResult : LogicNode
    {
        internal List<Expr> expr_;

        public override string ToString() {
            string r = string.Join(",", expr_);
            return r;
        } 
        public LogicResult(List<Expr> expr) { expr_ = expr; }

        public override string PrintMoreDetails(int depth)
        {
            return "Expr: " + ToString();
        }

        public override List<TableRef> EnumTableRefs() => null;
    }
}

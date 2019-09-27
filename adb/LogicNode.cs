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
        public string tabs(int depth) => new string(' ', depth * 2);
        public virtual string PrintInlineDetails(int depth) { return null; }
        public virtual string PrintMoreDetails(int depth) { return null; }
        public string PrintString(int depth)
        {
            string r = tabs(depth);

            if (depth != 0)
                r += "-> ";
            r += this.GetType().Name + " " + PrintInlineDetails(depth) + "\n";
            var details = PrintMoreDetails(depth);
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
                }

                if (root is null)
                    root = phy;
                return false;
            });

            return root;
        }
    }

    public class LogicCrossJoin : LogicNode
    {
        public LogicCrossJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }
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
                            Debug.Assert(sx.query_.Parent() != null);
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
    }

    public class LogicAgg : LogicNode
    {
        internal List<Expr> groupby_;
        internal List<Expr> aggr_;
        internal Expr having_;

        public override string PrintMoreDetails(int depth)
        {
            if (having_ != null)
                return "Having: " + having_.PrintString(depth);
            return null;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, Expr having)
        {
            children_.Add(child); groupby_ = groupby; having_ = having;
        }
    }

    public class LogicSubquery : LogicNode
    {
        public SubqueryRef query_;

        public override string ToString() => $"<{query_.alias_}>";
        public LogicSubquery(SubqueryRef query, LogicNode child) { query_ = query; children_.Add(child); }
    }

    public class LogicGet : LogicNode
    {
        public BaseTableRef tabref_;
        public Expr filter_;

        public LogicGet(BaseTableRef tab)
        {
            tabref_ = tab;
        }

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
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public class Rule
    {
        public static Rule[] ruleset_ = {
            new JoinAssociativeRule(),
            new JoinCommutativeRule(),
            new Join2NLJoin(),
            new JoinToHashJoin(),
            new Scan2Scan(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Order2Sort(),
            new From2From(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

    public class JoinCommutativeRule : ExplorationRule {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin lj && lj.type_ == JoinType.InnerJoin;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var newjoin = new LogicJoin(join.children_[1], join.children_[0], join.filter_);
            return new CGroupMember(newjoin, expr.group_);
        }
    }

    //  A(BC) => (AB)C
    //
    // 1. There are other equvalent forms and we only use above form.
    // Say (AB)C-> (AC)B which is actually can be transformed via this rule:
    //     (AB)C -> C(AB) -> (CA)B -> (AC)B
    // 2. There are also left or right association but we only use left association 
    //    since the right one can be transformed via commuative first.
    //    (AB)C -> A(BC) ; A(BC) -> (AB)C
    // 3. Join filter shall be considered:
    //      A JOIN (B JOIN C [on bcfilter]) [on abfilter [AND acfilter]]
    //  =>  (A JOIN B [on abfilter]) JOIN C [on bcfilter [AND acfilter]]
    //  we do not generate catersian join unless input is catersian.
    //
    public class JoinAssociativeRule : ExplorationRule
    {
        // a.i=b.i => a.i=b.i
        // a.i=b.i AND a.j=b.j AND a.k=c.k => a.i=b.i AND a.j=b.j
        Expr exactabFilter(Expr abcfilter, LogicMemoNode A, LogicMemoNode B)
        {
            Expr ret = null;
            if (abcfilter is null)
                return null;

            var andlist = FilterHelper.FilterToAndList(abcfilter);
            foreach (var v in andlist)
            {
                var fb = v as BinExpr;
                var ltabrefs = A.logicNode().InclusiveTableRefs();
                ltabrefs.AddRange(B.logicNode().InclusiveTableRefs());
                var keyrefs = fb.tableRefs_;
                if (Utils.ListAContainsB(ltabrefs, keyrefs))
                {
                    ret = FilterHelper.AddAndFilter(ret, fb);
                    Console.WriteLine(fb);
                }
            }

            return ret;
        }
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;

            if (a_bc != null)
            {
                var bc = a_bc.children_[1] as LogicMemoNode;
                var bcfilter = bc.logicNode().filter_;
                if (bc.node_ is LogicJoin) {
                    Expr abcfilter = a_bc.filter_;
                    var abfilter = exactabFilter(abcfilter,
                        a_bc.children_[0] as LogicMemoNode, 
                        bc.logicNode().children_[0] as LogicMemoNode);

                    // if there is no filter at all, we are fine but we don't
                    // allow the case we may generate catersian product
                    if (abfilter != null && bcfilter is null)
                        return false;
                    return true;
                }
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;
            Expr abcfilter = a_bc.filter_;
            LogicJoin bc = (a_bc.children_[1] as LogicMemoNode).logicNode<LogicJoin>();
            Expr bcfilter = bc.filter_;
            var abfilter = exactabFilter(abcfilter,
                a_bc.children_[0] as LogicMemoNode,
                bc.children_[0] as LogicMemoNode);
            var acfilter = exactabFilter(abcfilter,
                a_bc.children_[0] as LogicMemoNode,
                bc.children_[1] as LogicMemoNode);

            var ab_c = new LogicJoin(
                new LogicJoin(a_bc.children_[0], bc.children_[0], abfilter),
                bc.children_[1],
                    acfilter != null? FilterHelper.AddAndFilter(bcfilter, acfilter): bcfilter);
            return new CGroupMember(ab_c, expr.group_);
        }
    }

    public class ImplmentationRule : Rule { }

    public class JoinToHashJoin : ImplmentationRule 
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            return join != null && join.FilterHashable();
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var l = new PhysicMemoNode(join.children_[0]);
            var r = new PhysicMemoNode(join.children_[1]);
            var hashjoin = new PhysicHashJoin(join, l, r);
            return new CGroupMember(hashjoin, expr.group_);
        }
    }

    public class Join2NLJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin log = expr.logic_ as LogicJoin;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin log = expr.logic_ as LogicJoin;
            var l = new PhysicMemoNode(log.children_[0]);
            var r = new PhysicMemoNode(log.children_[1]);
            var nlj = new PhysicNLJoin(log, l, r);
            return new CGroupMember(nlj, expr.group_);
        }
    }

    public class Scan2Scan : ImplmentationRule {
        public override bool Appliable(CGroupMember expr)
        {
            LogicScanTable log = expr.logic_ as LogicScanTable;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicScanTable log = expr.logic_ as LogicScanTable;
            var phy = new PhysicScanTable(log);
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Filter2Filter : ImplmentationRule {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFilter;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFilter;
            var phy = new PhysicFilter(log, new PhysicMemoNode(log.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
    public class Agg2HashAgg: ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicAgg;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicAgg;
            var phy = new PhysicHashAgg(log, new PhysicMemoNode(log.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
    public class Order2Sort : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicOrder;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicOrder;
            var phy = new PhysicOrder(log, new PhysicMemoNode(log.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
    public class From2From : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFromQuery;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFromQuery;
            var phy = new PhysicFromQuery(log, new PhysicMemoNode(log.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
}

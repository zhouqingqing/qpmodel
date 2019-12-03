using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

    public class JoinCommutativeRule : ExplorationRule {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var newjoin = new LogicJoin(join.children_[1], join.children_[0]);
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
    //
    public class JoinAssociativeRule : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            if (join != null)
            {
                var rightchild = join.children_[1];
                if ((rightchild as LogicMemoNode).node_ is LogicJoin)
                    return true;
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            LogicJoin rightjoin = (join.children_[1] as LogicMemoNode).node_ as LogicJoin;
            var newjoin = new LogicJoin(
                new LogicJoin(join.children_[0], rightjoin.children_[0]),
                rightjoin.children_[1]);
            return new CGroupMember(newjoin, expr.group_);
        }
    }

    public class ImplmentationRule : Rule { }

    public class JoinToHashJoin : ImplmentationRule 
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            return join != null;
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
            LogicJoin join = expr.logic_ as LogicJoin;
            return join != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var l = new PhysicMemoNode(join.children_[0]);
            var r = new PhysicMemoNode(join.children_[1]);
            var nlj = new PhysicNLJoin(join, l, r);
            return new CGroupMember(nlj, expr.group_);
        }
    }

    public class Scan2Scan : ImplmentationRule {
        public override bool Appliable(CGroupMember expr)
        {
            LogicScanTable scan = expr.logic_ as LogicScanTable;
            return scan != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicScanTable scan = expr.logic_ as LogicScanTable;
            var phy = new PhysicScanTable(scan);
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Filter2Filter : ImplmentationRule {
        public override bool Appliable(CGroupMember expr)
        {
            var filter = expr.logic_ as LogicFilter;
            return filter != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var filter = expr.logic_ as LogicFilter;
            var phy = new PhysicFilter(filter, new PhysicMemoNode(filter.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
    public class Agg2HashAgg: ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var agg = expr.logic_ as LogicAgg;
            return agg != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var agg = expr.logic_ as LogicAgg;
            var phy = new PhysicHashAgg(agg, new PhysicMemoNode(agg.children_[0]));
            return new CGroupMember(phy, expr.group_);
        }
    }
}

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
            new JoinToNLJoin(),
            new JoinToHashJoin()
        };

        public virtual bool Appliable(CGroupExpr expr) { return false; }
        public virtual CGroupExpr Apply(CGroupExpr expr) { return expr;}

    }

    public class ImplmentationRule : Rule { }
    public class ExplorationRule : Rule { }

    public class JoinToHashJoin : ImplmentationRule 
    {
        public override bool Appliable(CGroupExpr expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            return join != null;
        }

        public override CGroupExpr Apply(CGroupExpr expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var hashjoin = new PhysicHashJoin(join, null, null);
            return new CGroupExpr(hashjoin);
        }
    }

    public class JoinToNLJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupExpr expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            return join != null;
        }

        public override CGroupExpr Apply(CGroupExpr expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var nlj = new PhysicNLJoin(join, null, null);
            return new CGroupExpr(nlj);
        }
    }

    public class Scan2Scan : ImplmentationRule {
        public override bool Appliable(CGroupExpr expr)
        {
            LogicScanTable scan = expr.logic_ as LogicScanTable;
            return scan != null;
        }

        public override CGroupExpr Apply(CGroupExpr expr)
        {
            LogicScanTable scan = expr.logic_ as LogicScanTable;
            var phy = new PhysicScanTable(scan);
            return new CGroupExpr(phy);
        }
    }
}

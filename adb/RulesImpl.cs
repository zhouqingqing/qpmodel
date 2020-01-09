using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adb
{
    public class ImplmentationRule : Rule { }

    public class Join2HashJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            if (join is null || join is LogicMarkJoin)
                return false;

            if (join.filter_.FilterHashable()) {
                bool lhasSubqCol = TableRef.HasColsUsedBySubquries(join.l_().InclusiveTableRefs());
                if (!lhasSubqCol)
                    return true;
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin log = expr.logic_ as LogicJoin;
            var l = new PhysicMemoRef(log.l_());
            var r = new PhysicMemoRef(log.r_());
            var hashjoin = new PhysicHashJoin(log, l, r);
            return new CGroupMember(hashjoin, expr.group_);
        }
    }

    public class Join2NLJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin log = expr.logic_ as LogicJoin;
            if (log is null || log is LogicMarkJoin)
                return false;
            return true;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin log = expr.logic_ as LogicJoin;
            var l = new PhysicMemoRef(log.l_());
            var r = new PhysicMemoRef(log.r_());
            PhysicNode phy = new PhysicNLJoin(log, l, r);
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Join2MarkJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicMarkJoin log = expr.logic_ as LogicMarkJoin;
            return !(log is null);
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicMarkJoin log = expr.logic_ as LogicMarkJoin;
            var l = new PhysicMemoRef(log.l_());
            var r = new PhysicMemoRef(log.r_());
            PhysicNode phy = null;
            switch (log)
            {
                case LogicSingleMarkJoin lsmj:
                    phy = new PhysicSingleMarkJoin(lsmj, l, r);
                    break;
                case LogicMarkJoin lmj:
                    phy = new PhysicMarkJoin(lmj, l, r);
                    break;
                default:
                    phy = null;
                    break;
            }
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Scan2Scan : ImplmentationRule
    {
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

    public class Filter2Filter : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFilter;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicFilter;
            var phy = new PhysicFilter(log, new PhysicMemoRef(log.child_()));
            return new CGroupMember(phy, expr.group_);
        }
    }
    public class Agg2HashAgg : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicAgg;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicAgg;
            var phy = new PhysicHashAgg(log, new PhysicMemoRef(log.child_()));
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
            var phy = new PhysicOrder(log, new PhysicMemoRef(log.child_()));
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
            var phy = new PhysicFromQuery(log, new PhysicMemoRef(log.child_()));
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Limit2Limit: ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicLimit;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicLimit;
            var phy = new PhysicLimit(log, new PhysicMemoRef(log.child_()));
            return new CGroupMember(phy, expr.group_);
        }
    }

}

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
using qpmodel.logic;
using qpmodel.physic;

namespace qpmodel.optimizer
{
    public class ImplmentationRule : Rule { }

    public class NumberArgs
    {
        public class N0 : NumberArgs { }
        public class N1 : NumberArgs { }
        public class N2 : NumberArgs { }
        public class NList : NumberArgs { }
    }

    // A simple rule verify logic is T1 and convert to T2 with T3 specified children
    public class SimpleImplementationRule<T1, T2, T3> : ImplmentationRule where T1 : LogicNode where T2 : PhysicNode where T3 : NumberArgs, new()
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as T1;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as T1;

            Object[] args = null;
            T3 nArgs = new T3();
            switch (nArgs)
            {
                case NumberArgs.N0 n0:
                    args = new Object[] { log };
                    break;
                case NumberArgs.N1 n1:
                    args = new Object[] { log, new PhysicMemoRef(log.child_()) };
                    break;
                case NumberArgs.N2 n2:
                    args = new Object[] { log, new PhysicMemoRef(log.l_()), new PhysicMemoRef(log.r_()) };
                    break;
                case NumberArgs.NList nlist:
                    List<PhysicNode> children = log.children_.Select(x => new PhysicMemoRef(x) as PhysicNode).ToList();
                    args = new Object[] { log, children };
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            var phy = (T2)Activator.CreateInstance(typeof(T2), args);
            return new CGroupMember(phy, expr.group_);
        }
    }

    public class Join2HashJoin : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            if (join is null || join is LogicMarkJoin || join is LogicSingleJoin)
                return false;

            if (join.filter_.FilterHashable())
            {
                var stmt = expr.Stmt() as SelectStmt;
                bool lhasSubqCol = stmt.PlanContainsCorrelatedSubquery() &&
                    TableRef.HasColsUsedBySubquries(join.l_().InclusiveTableRefs());
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
            if (log is null || log is LogicMarkJoin || log is LogicSingleJoin)
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

    public class Scan2IndexSeek : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            LogicScanTable log = expr.logic_ as LogicScanTable;
            if (log != null && log.filter_ != null)
                return true;
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicScanTable log = expr.logic_ as LogicScanTable;
            var index = log.filter_.FilterCanUseIndex(log.tabref_);
            if (index is null)
                return expr;
            else
            {
                var phy = new PhysicIndexSeek(log, index);
                return new CGroupMember(phy, expr.group_);
            }
        }
    }

    public class JoinBLock2Join : ImplmentationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            var log = expr.logic_ as LogicJoinBlock;
            return log != null;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            var log = expr.logic_ as LogicJoinBlock;
            var solver = new DPccp();
            var joinplan = solver.Reset().Run(log.graph_);
            return new CGroupMember(joinplan, expr.group_);
        }
    }

    public class Scan2Scan : SimpleImplementationRule<LogicScanTable, PhysicScanTable, NumberArgs.N0> { }
    public class Filter2Filter : SimpleImplementationRule<LogicFilter, PhysicFilter, NumberArgs.N1> { }
    public class Agg2HashAgg : SimpleImplementationRule<LogicAgg, PhysicHashAgg, NumberArgs.N1> { }
    public class Agg2StreamAgg : SimpleImplementationRule<LogicAgg, PhysicStreamAgg, NumberArgs.N1> { }
    public class Order2Sort : SimpleImplementationRule<LogicOrder, PhysicOrder, NumberArgs.N1> { }
    public class Append2Append : SimpleImplementationRule<LogicAppend, PhysicAppend, NumberArgs.N1> { }
    public class From2From : SimpleImplementationRule<LogicFromQuery, PhysicFromQuery, NumberArgs.N1> { }
    public class Limit2Limit : SimpleImplementationRule<LogicLimit, PhysicLimit, NumberArgs.N1> { }
    public class Join2MarkJoin : SimpleImplementationRule<LogicMarkJoin, PhysicMarkJoin, NumberArgs.N2> { }
    public class Join2SingleJoin : SimpleImplementationRule<LogicSingleJoin, PhysicSingleJoin, NumberArgs.N2> { }
    public class Seq2Seq : SimpleImplementationRule<LogicSequence, PhysicSequence, NumberArgs.NList> { }
    public class CteProd2CteProd : SimpleImplementationRule<LogicCteProducer, PhysicCteProducer, NumberArgs.N1> { }
    public class Gather2Gather : SimpleImplementationRule<LogicGather, PhysicGather, NumberArgs.N1> { }
    public class Bcast2Bcast : SimpleImplementationRule<LogicBroadcast, PhysicBroadcast, NumberArgs.N1> { }
    public class Redis2Redis : SimpleImplementationRule<LogicRedistribute, PhysicRedistribute, NumberArgs.N1> { }
    public class PSet2PSet : SimpleImplementationRule<LogicProjectSet, PhysicProjectSet, NumberArgs.N1> { }
}

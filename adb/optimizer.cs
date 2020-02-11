using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using adb.logic;
using adb.physic;
using adb.expr;

using LogicSignature = System.Int64;

// TODO:
//  - branch and bound prouning
//  - enforcer: by test Join2MJ and indexing
//  - aggregation/ctes: by generate multiple memo
//

namespace adb.optimizer
{
    public class JoinOrderResolver
    {
    }

    public class Property { }

    // ordering, distribution
    public class PhysicProperty : Property { }

    // CGroupMember is a member of CMemoGroup, all these memebers are logically
    // equvalent but different logical/physical implementations
    //
    public class CGroupMember{
        public LogicNode logic_;
        public PhysicNode physic_;

        internal CMemoGroup group_;

        internal QueryOption QueryOption() => group_.memo_.stmt_.queryOpt_;

        internal LogicNode Logic() {
            LogicNode logic;
            if (logic_ != null)
                logic = logic_;
            else
                logic = physic_.logic_;
            Debug.Assert(!(logic is LogicMemoRef));
            return logic;
        }
        internal LogicSignature MemoSignature() => Logic().MemoLogicSign();

        public CGroupMember(LogicNode node, CMemoGroup group) {
            logic_ = node; group_ = group;
            Debug.Assert(!(Logic() is LogicMemoRef));
        }
        public CGroupMember(PhysicNode node, CMemoGroup group) 
        {
            physic_ = node; group_ = group;
            Debug.Assert(!(Logic() is LogicMemoRef));
        }

        public void ValidateMember(bool optimizationDone) {
            Debug.Assert(Logic() != null);

            // TODO: copy out destroy the memo so we can't apply checks here
            bool beforeCopyOut = Optimizer.copyoutCounter_ == 0;
            if (beforeCopyOut)
            {
                // the node itself is a non-memo node
                Debug.Assert(!(Logic() is LogicMemoRef));

                if (group_.exprList_[0].Logic() is LogicNaryJoin)
                    return;

                // all its children shall be memo nodes and can be deref'ed to non-memo node
                Logic().children_.ForEach(x => Debug.Assert(
                        x is LogicMemoRef xl && !(xl.Deref() is LogicMemoRef)));

                if (physic_ != null) {
                    // the physical node itself is non-memo node
                    Debug.Assert(!(physic_ is PhysicMemoRef));

                    // all its children shall be memo nodes and can be deref'ed to non-memo node
                    physic_.children_.ForEach(x => Debug.Assert(
                        x is PhysicMemoRef xp && !(xp.Logic().Deref() is LogicMemoRef)));
                }
            }
        }

        public override string ToString()
        {
            if (logic_ != null)
            {
                Debug.Assert(physic_ is null);
                return logic_.ToString();
            }
            if (physic_ != null)
                return physic_.ToString();
            Debug.Assert(false);
            return null;
        }

        public override int GetHashCode() {
            if (logic_ != null)
                return logic_.GetHashCode();
            if (physic_ != null)
                return physic_.GetHashCode();
            throw new InvalidProgramException();
        }

        public override bool Equals(object obj)
        {
            if (obj is CGroupMember co) {
                if (logic_ != null)
                    return logic_.Equals(co.logic_);
                if (physic_ != null)
                    return physic_.Equals(co.physic_);
                throw new InvalidProgramException();
            }
            return false;
        }

        // Apply rule to current members and generate a set of new members for each
        // of the new memberes, find/add itself or its children in the group
        internal List<CGroupMember> OptimizeMember(Memo memo)
        {
            var list = group_.exprList_;
            foreach (var rule in Rule.ruleset_)
            {
                if (rule.Appliable(this))
                {
                    // apply rule: sometimes it may simply returns the old member
                    var newmember = rule.Apply(this);
                    if (newmember == this)
                        continue;

                    // enqueue the new member
                    var newlogic = newmember.Logic();
                    if (!(group_.exprList_[0].logic_ is LogicNaryJoin))
                        memo.EnquePlan(newlogic);
                    if (!list.Contains(newmember))
                        list.Add(newmember);

                    // do some verification: newmember shall have the same signature as old ones
                    if (newlogic.MemoLogicSign() != list[0].MemoSignature())
                    {
                        Console.WriteLine("********* list[0]");
                        Console.WriteLine(list[0].Logic().Explain(0));
                        Console.WriteLine("********* newlogic");
                        Console.WriteLine(newlogic.Explain(0));
                    }
                    Debug.Assert(newlogic.MemoLogicSign() == list[0].MemoSignature());
                }
            }

            return list;
        }
    }

    // A CMemoGroup represents equvalent logical and physical transformations of the same expr.
    // To identify a group, we use a LogicSignature and equalvalent plans shall have the same
    // signature. How to generate it? Though there are physical/logical plans, finally the 
    // problem boiled down to logic plan signature generation since physical can always use 
    // its logic to compute signature.
    // 
    // 1. Signature must consider attributes.
    // Consider the following query:
    //   select * from A where a1 > (select max(a2) from A);
    //
    // There are two A, are they sharing the same group or different groups? Since both are 
    // same, so they can share the same cgroup. However, if one is with a filter, then they
    // are in different cgroup.
    //
    // 2. Signature might compute children in a order insensitive way:
    //    INNERJOIN (A, B) => HJ(A,B), NLJ(A,B)
    //    INNERJOIN (B, A) => HJ(B,A), NLJ(B,A)
    //
    // Is there any compute children in a order sensitive way?
    //    
    // These CGroupMember are in the same group because their logical plan are the same.
    //
    public class CMemoGroup {
        // insertion order in memo
        public int memoid_;

        // signature represents a cgroup, all expression in the cgroup shall compute the
        // same signature though different cgroup ok to compute the same as well
        //
        public LogicSignature logicSign_;
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public List<CGroupMember> exprList_ = new List<CGroupMember>();

        public bool explored_ = false;
        public CGroupMember minMember_;

        // debug info
        internal Memo memo_;

        public CMemoGroup(Memo memo, int groupid, LogicNode subtree) {
            Debug.Assert(!(subtree is LogicMemoRef));
            memo_ = memo;
            memoid_ = groupid;
            explored_ = false;
            logicSign_ = subtree.MemoLogicSign();
            exprList_.Add(new CGroupMember(subtree, this));
        }

        public void ValidateGroup(bool optimizationDone)
        {
            // no duplicated members in the list
            Debug.Assert(exprList_.Distinct().Count() == exprList_.Count);
            Debug.Assert(!optimizationDone || explored_);

            for (int i = 1; i < exprList_.Count; i++)
            {
                // all members within a group are logically equavalent
                var member = exprList_[i];
                member.ValidateMember(optimizationDone);
                Debug.Assert(exprList_[0].MemoSignature() == member.MemoSignature());
            }
        }

        // {1} X {2} -> Scan(A) X Scan(B)
        public LogicNode RetrieveLogicTree(LogicNode logic)
        {
            if (logic.children_.Count > 0)
            {
                var children = new List<LogicNode>();
                foreach (var v in logic.children_)
                {
                    var c = v;
                    if (v is LogicMemoRef lv)
                        c = lv.group_.RetrieveLogicTree(lv.Deref());
                    children.Add(c);
                }
                logic.children_ = children;
            }

            Debug.Assert(!(logic is LogicMemoRef));
            return logic;
        }

        public void CountMembers(out int clogic, out int cphysic) {
            clogic = 0; cphysic = 0;
            foreach (var v in exprList_) {
                if (v.logic_ != null)
                    clogic++;
                else
                    cphysic++;
            }
        }

        public override string ToString() => $"{memoid_}";
        public string Print()
        {
            CountMembers(out int clogics, out int cphysics);
            var str = $"{clogics}, {cphysics}, [{logicSign_}]: ";
            str += string.Join(",", exprList_);
            return str;
        }

        // loop through optimize members of the group
        public void OptimizeGroup(Memo memo, PhysicProperty required) {
            for (int i = 0; i < exprList_.Count; i++)
            {
                CGroupMember member = exprList_[i];

                // optimize the member and it shall generate a set of member
                member.OptimizeMember(memo);
            }

            // mark the group explored
            explored_ = true;
        }

        // scan through the member list and return the least cost member
        public CGroupMember locateMinCostMember() {
            CGroupMember min = null;
            double mincost = double.MaxValue;
            foreach (var v in exprList_)
            {
                var physic = v.physic_;
                if (physic != null && physic.Cost() < mincost)
                {
                    mincost = physic.Cost();
                    min = v;
                }
            }

            Debug.Assert(min.physic_ != null);
            minMember_ = min;
            return min;
        }

        public PhysicNode CopyOutMinLogicPhysicPlan(PhysicNode physic = null)
        {
            if (physic is null)
            {
                // get the lowest cost one from the member list
                CGroupMember minmember = locateMinCostMember();
                physic = minmember.physic_;
            }

            // recursively repeat the process for all its children
            if (physic.children_.Count > 0)
            {
                var phychildren = new List<PhysicNode>();
                var logchildren = new List<LogicNode>();
                foreach (var v in physic.children_)
                {
                    // children shall be min cost
                    PhysicNode phychild;
                    if (!(v is PhysicMemoRef))
                        phychild = CopyOutMinLogicPhysicPlan(v);
                    else
                    {
                        var g = (v as PhysicMemoRef).Group();
                        phychild = g.CopyOutMinLogicPhysicPlan();
                    }
                    phychildren.Add(phychild);
                    logchildren.Add(phychild.logic_);
                }

                // rewrite children
                physic.children_ = phychildren;
                physic.logic_.children_ = logchildren;
            }

            physic.logic_ = RetrieveLogicTree(physic.logic_);
            Debug.Assert(!(physic is PhysicMemoRef));
            Debug.Assert(!(physic.logic_ is LogicMemoRef));
            if (Optimizer.topstmt_.queryOpt_.profile_.enabled_)
                physic = new PhysicProfiling(physic);
            return physic;
        }
    }

    public class Memo {
        public SQLStatement stmt_;
        public CMemoGroup rootgroup_;
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public Dictionary<LogicSignature, CMemoGroup> cgroups_ = new Dictionary<LogicSignature, CMemoGroup>();

        public Stack<CMemoGroup> stack_ = new Stack<CMemoGroup>();

        public Memo(SQLStatement stmt) => stmt_ = stmt;
        public CMemoGroup LookupCGroup(LogicNode subtree) {
            if (subtree is LogicMemoRef sl)
                return sl.group_;

            var signature = subtree.MemoLogicSign();
            if (cgroups_.TryGetValue(signature, out CMemoGroup group))
                return group;
            return null;
        }

        public CMemoGroup TryInsertCGroup(LogicNode subtree)
        {
            var group = LookupCGroup(subtree);
            if (group is null)
                return InsertCGroup(subtree);
            return group;
        }

        public CMemoGroup InsertCGroup(LogicNode subtree)
        {
            var signature = subtree.MemoLogicSign();
            Debug.Assert(LookupCGroup(subtree) is null);
            var group = new CMemoGroup(this, cgroups_.Count(), subtree);
            cgroups_.Add(signature, group);

            stack_.Push(group);
            return group;
        }

        public void CalcStats(out int tlogics, out int tphysics)
        {
            tlogics = 0; tphysics = 0;

            // output by memo insertion order to read easier
            foreach (var v in cgroups_)
            {
                var group = v.Value;
                group.CountMembers(out int clogics, out int cphysics);
                tlogics += clogics; tphysics += cphysics;
            }
        }

        CMemoGroup doEnquePlan(LogicNode plan)
        {
            Debug.Assert(!(plan is LogicMemoRef));

            // bottom up equeue all nodes
            if (!plan.IsLeaf())
            {
                for (int i = 0; i < plan.children_.Count; i++)
                {
                    var child = plan.children_[i];
                    var group = EnquePlan(child);
                    Debug.Assert(group == LookupCGroup(child));

                    // now the child is in the memo, convert the plan with children 
                    // replaced by memo cgroup
                    plan.children_[i] = new LogicMemoRef(group);
                }
            }
            return TryInsertCGroup(plan);
        }

        public CMemoGroup EnquePlan(LogicNode plan)
        {
            // if might be already a memo node
            if (plan is LogicMemoRef lp)
                return lp.group_;

            if (stmt_.queryOpt_.optimize_.use_joinorder_solver)
            {
                // In this mode, we decompose the plan into multiple join graph with
                // other non-join nodes, which includes vertices in join graph and
                // plan node before join filter (say aggregation, sort etc).
                // All non-join nodes are enqueued as usual and join graphs are enqueued
                // as a one memo group disallowing optimization.
                //
                // In this way, we leverage both power of join solver and memo.
                //
                var graph = JoinResolver.ExtractJoinGraph(plan, out LogicFilter joinfilter);

                // if join solver can't handle it, fallback
                if (graph != null)
                {
                    graph.memo_ = this;

                    // though it is not logically needed because we generate join filter according
                    // to the plan on the fly, we still do a join filter push down to keep the 
                    // logic signature consistent
                    //
                    var andlist = joinfilter.filter_.FilterToAndList();
                    andlist.RemoveAll(e => joinfilter.PushJoinFilter(e));
                    Debug.Assert(andlist.Count == 0);
                    joinfilter.NullifyFilter();

                    // enqueue the nary-join as a new node
                    var naryjoin = new LogicNaryJoin(joinfilter.child_() as LogicJoin, graph);
                    joinfilter.children_[0] = naryjoin;
                }
            }

            return doEnquePlan(plan);
        }

        public void ValidateMemo(bool optimizationDone= false) {
            // all groups are different
            Debug.Assert(cgroups_.Distinct().Count() == cgroups_.Count);
            foreach (var v in cgroups_)
            {
                CMemoGroup g = v.Value;
                g.ValidateGroup(optimizationDone);
            }
        }

        public string Print() {
            var str = "\nMemo:\n";
            int tlogics = 0, tphysics = 0;

            // output by memo insertion order to read easier
            var list = cgroups_.OrderBy(x => x.Value.memoid_).ToList();
            foreach (var v in list) {
                var group = v.Value;
                if (group == rootgroup_)
                    str += "*";
                group.CountMembers(out int clogics, out int cphysics);
                tlogics += clogics; tphysics += cphysics;
                str += $"{group}:\t{group.Print()}\n";
            }

            str += "---------------------------\n";
            str += $"Summary: {tlogics},{tphysics}";
            return str;
        }

    }

    public static class Optimizer
    {
        public static List<Memo> memoset_ = new List<Memo>();
        public static SQLStatement topstmt_;
        public static int copyoutCounter_ = 0;

        public static void InitRootPlan(SQLStatement stmt)
        {
            // call once
            Rule.Init(stmt.queryOpt_);
            topstmt_ = stmt;
            memoset_.Clear();
        }

        public static void OptimizeRootPlan(SQLStatement stmt, PhysicProperty required, bool enqueueit = true)
        {
            var select = stmt as SelectStmt;

            // each statement sitting in a new memo
            var memo = new Memo(stmt);
            if (enqueueit) {
                memoset_.Add(memo);

                // the statment shall already have plan generated
                var logicroot = select.logicPlan_;
                memo.rootgroup_ = memo.EnquePlan(logicroot);
            }

            // enqueue the subqueries: fromquery are excluded because different from 
            // other subqueries (IN, EXISTS etc), the subtree of it is actually connected 
            // in the same memo.
            //
            var subqueries = select.Subqueries();
            foreach (var v in subqueries)
            {
                enqueueit = !select.SubqueryIsWithMainQuery(v);
                Optimizer.OptimizeRootPlan(v, required, enqueueit);
            }

            // loop through the stack, optimize each one until empty
            //
            while (memo.stack_.Count > 0)
            {
                var group = memo.stack_.Pop();
                group.OptimizeGroup(memo, required);
            }
            memo.ValidateMemo();
        }

        public static PhysicNode CopyOutOnePlan(SQLStatement stmt, Memo memo)
        {
            var select = stmt as SelectStmt;

            // retrieve the lowest cost plan
            Debug.Assert(stmt.physicPlan_ is null);
            stmt.physicPlan_ = memo.rootgroup_.CopyOutMinLogicPhysicPlan();
            stmt.logicPlan_ = stmt.physicPlan_.logic_;

            // fix fromQueries - the fromQueries are optimized as part of LogicNode tree
            // but we shall reconnect the logic node back to the Select Stmt level as needed
            // by column ordinal resolution. Physical node however is not needed.
            //
            foreach (var v in select.fromQueries_) {
                var fromQuery = (v.Key as SelectStmt);
                var generatedPlan = (v.Value as LogicFromQuery).child_();

                fromQuery.logicPlan_ = generatedPlan;
                Debug.Assert(fromQuery.physicPlan_ is null);
            }

            // finally let's fix the output
            (stmt as SelectStmt).ResolveOrdinals();
            return stmt.physicPlan_;
        }

        public static PhysicNode CopyOutOptimalPlan(SQLStatement stmt, bool dequeueit = true)
        {
            var select = stmt as SelectStmt;
            PhysicNode phyplan = null;
            if (dequeueit)
                phyplan = CopyOutOnePlan(stmt, memoset_[copyoutCounter_++]);
            var subqueries = select.Subqueries();
            foreach (var v in subqueries)
                CopyOutOptimalPlan(v, !select.SubqueryIsWithMainQuery(v));
            return phyplan;
        }

        public static PhysicNode CopyOutOptimalPlan()
        {
            copyoutCounter_ = 0;
            return CopyOutOptimalPlan(topstmt_);
        }

        public static string PrintMemo() {
            string str = "";
            memoset_.ForEach(x => str += x.Print());
            return str;
        }
    }
}

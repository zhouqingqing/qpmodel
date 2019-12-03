using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using LogicSignature = System.Int32;

namespace adb
{
    public class JoinOrderResolver
    {
    }

    public class Property { }

    // ordering, distribution
    public class PhysicProperty : Property { }


    public class CGroupMember{
        public LogicNode logic_;
        public PhysicNode physic_;

        internal CMemoGroup group_;

        internal LogicNode logic() {
            LogicNode logic;
            if (logic_ != null)
                logic = logic_;
            else
                logic = physic_.logic_;
            return logic;
        }
        internal int MemoSignature() => logic().MemoLogicSign();

        public CGroupMember(LogicNode node, CMemoGroup group) {logic_ = node; group_ = group;}
        public CGroupMember(PhysicNode node, CMemoGroup group) {physic_ = node; group_ = group;}
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

        // Apply rule to current node and generate a set of new members for each
        // of the new memberes, find/add itself or its children in the group
        internal List<CGroupMember> Optimize(Memo memo)
        {
            var list = group_.exprList_;
            foreach (var rule in Rule.ruleset_)
            {
                if (rule.Appliable(this))
                {
                    var newmember = rule.Apply(this);
                    var newlogic = newmember.logic();
                    memo.EnquePlan(newlogic);

                    if (!list.Contains(newmember))
                        list.Add(newmember);
                    // newmember shall have the same signature as old ones
                    Debug.Assert(newlogic.MemoLogicSign() == list[0].MemoSignature());
                }
            }

            return list;
        }
    }

    // A cgroup represents equvalent logical and physical transformations of the same expr
    // 
    // 1. CGroup must consider attributes.
    // Consider the following query:
    //   select * from A where a1 > (select max(a2) from A);
    //
    // There are two A, are they sharing the same group or different groups? Since both are 
    // same, so they can share the same cgroup. However, if one is with a filter, then they
    // are in different cgroup.
    //
    // 2. CGroup shall use logical plan with fixed order
    //    INNERJOIN (A, B) => HJ(A,B), HJ(B,A), NLJ(A,B), NLJ(B,A)
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
        public List<CGroupMember> exprList_ = new List<CGroupMember>();

        public bool explored_ = false;

        // debug info
        internal Memo memo_;

        public CMemoGroup(Memo memo, int groupid, LogicNode subtree) {
            Debug.Assert(!(subtree is LogicMemoNode));
            memo_ = memo;
            memoid_ = groupid;
            explored_ = false;
            logicSign_ = subtree.MemoLogicSign();
            exprList_.Add(new CGroupMember(subtree, this));
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

        public override string ToString() => $"{{{memoid_}}}";
        public string Print()
        {
            CountMembers(out int clogics, out int cphysics);
            var str = $"{clogics}, {cphysics}: ";
            str += string.Join(",", exprList_);
            return str;
        }

        // loop through optimize members of the group
        public void Optimize(Memo memo, PhysicProperty required) {
            Console.WriteLine($"opt group {memoid_}");

            for (int i = 0; i < exprList_.Count; i++)
            {
                CGroupMember member = exprList_[i];

                // optimize the member and it shall generate a set of member
                member.Optimize(memo);
            }

            // mark the group explored
            explored_ = true;
        }

        public double MinCost() =>  MinCostMember().physic_.Cost();
        public CGroupMember MinCostMember() {
            CGroupMember minmember = null;
            double mincost = double.MaxValue;
            foreach (var v in exprList_)
            {
                if (v.physic_ != null && v.physic_.Cost() < mincost)
                {
                    mincost = v.physic_.Cost();
                    minmember = v;
                }
            }

            Debug.Assert(minmember.physic_ != null);
            return minmember;
        }

        public PhysicNode MinToPhysicPlan()
        {
            CGroupMember minmember = MinCostMember();
            var children = new List<PhysicNode>();
            foreach (var v in minmember.physic_.children_)
            {
                var g = (v as PhysicMemoNode).Group();
                children.Add(g.MinToPhysicPlan());
            }
            minmember.physic_.children_ = children;

            PhysicNode phy = minmember.physic_;
            if (Optimizer.stmt_.profileOpt_.enabled_)
                phy = new PhysicProfiling(minmember.physic_);
            return phy;
        }
    }

    public class Memo {
        public CMemoGroup rootgroup_;
        public Dictionary<LogicSignature, CMemoGroup> cgroups_ = new Dictionary<LogicSignature, CMemoGroup>();

        public Stack<CMemoGroup> stack_ = new Stack<CMemoGroup>();

        public CMemoGroup LookupCGroup(LogicNode subtree) {
            if (subtree is LogicMemoNode sl)
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

            validateMemo();

            // output by memo insertion order to read easier
            foreach (var v in cgroups_)
            {
                var group = v.Value;
                group.CountMembers(out int clogics, out int cphysics);
                tlogics += clogics; tphysics += cphysics;
            }
        }

        public CMemoGroup EnquePlan(LogicNode plan)
        {
            // bottom up equeue all nodes
            foreach (var v in plan.children_)
            {
                if (v.IsLeaf())
                    TryInsertCGroup(v);
                else
                    EnquePlan(v);
            }

            // now all children in the memo, convert the plan with children 
            // replaced by memo cgroup
            var children = new List<LogicNode>();
            foreach (var v in plan.children_)
            {
                var child = LookupCGroup(v);
                children.Add(new LogicMemoNode(child));
            }
            plan.children_ = children;
            return TryInsertCGroup(plan);
        }

        void validateMemo() {
            // all groups are different
            Debug.Assert(cgroups_.Distinct().Count() == cgroups_.Count);
            foreach (var v in cgroups_)
            {
                CMemoGroup g = v.Value;

                // no duplicated members in the list
                Debug.Assert(g.exprList_.Distinct().Count() == g.exprList_.Count);

                for (int i = 0; i < g.exprList_.Count; i++) {
                    // all members within a group are logically equavalent
                    Debug.Assert(g.exprList_[0].MemoSignature() == g.exprList_[i].MemoSignature());
                }
            }
        }

        public string Print() {
            var str = "Memo:\n";
            int tlogics = 0, tphysics = 0;

            validateMemo();

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
        public static SQLStatement stmt_;

        public static CMemoGroup EnquePlan(Memo memo, LogicNode plan) {
            return memo.EnquePlan(plan);
        }

        public static void EnqueRootPlan(SQLStatement stmt)
        {
            stmt_ = stmt;

            // call once
            Debug.Assert(memoset_.Count == 0);
            var memo = new Memo();
            memoset_.Add(memo);

            // the statment shall already have plan generated
            var logicroot = stmt.logicPlan_;
            memo.rootgroup_ = EnquePlan(memo, logicroot);

            // otpimize the subqueries
            var subqueries = (stmt_ as SelectStmt).subqueries_;
            foreach (var v in subqueries) {
                var submemo = new Memo();
                var subroot = v.logicPlan_;
                submemo.rootgroup_ = EnquePlan(submemo, subroot);
            }
        }

        public static void SearchOptimal(PhysicProperty required)
        {
            // loop through the stack until is empty
            foreach (var memo in memoset_)
            {
                while (memo.stack_.Count > 0)
                {
                    var group = memo.stack_.Pop();
                    group.Optimize(memo, required);
                }
            }
        }

        public static PhysicNode RetrieveOptimalPlan()
        {
            var rootmemo = memoset_[0];
            return rootmemo.rootgroup_.MinToPhysicPlan();
        }
    }
}

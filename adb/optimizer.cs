using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using Signature = System.Int32;

namespace adb
{
    public class JoinOrderResolver
    {
    }

    public class Property { }

    // ordering, distribution
    public class PhysicProperty : Property { }


    public class CGroupExpr{
        public LogicNode logic_;
        public PhysicNode physic_;

        public CGroupExpr(LogicNode node) => logic_ = node;
        public CGroupExpr(PhysicNode node) => physic_ = node;
        public override string ToString()
        {
            if (logic_ != null)
                return logic_.ToString();
            if (physic_ != null)
                return physic_.ToString();
            Debug.Assert(false);
            return null;
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
    // These CGroupExpr are in the same group because their logical plan are the same.
    //
    public class CGroup: LogicNode {
        // insertion order in memo
        public int memoid_;

        // signature represents a cgroup, all expression in the cgroup shall compute the
        // same signature though different cgroup ok to compute the same as well
        //
        public Signature signature_;
        public List<CGroupExpr> exprList_ = new List<CGroupExpr>();

        public bool explored_ = false;

        public CGroup(int groupid, LogicNode subtree) {
            memoid_ = groupid;
            signature_ = subtree.GetHashCode();
            exprList_.Add(new CGroupExpr(subtree));
        }

        public override string ToString() => $"{{{memoid_}}}";
        public string Print() => string.Join(",", exprList_);
        public void Optimize(Stack<CGroup> stack, PhysicProperty required) {
            Console.WriteLine($"opt group {memoid_}");

            Debug.Assert(exprList_.Count == 1);
            CGroupExpr expr = exprList_[0];

            foreach (var rule in Rule.ruleset_)
            {
                if (rule.Appliable(expr))
                {
                    var newtree = rule.Apply(expr);
                    exprList_.Add(newtree);
                }
            }

            var subtree = expr.logic_;
            foreach (var v in subtree.children_)
            {
                var subgroup = v as CGroup;
                if (!subgroup.explored_)
                    stack.Push(subgroup);
            }

            // mark the group explored
            explored_ = true;
        }
    }

    public class Memo {
        internal CGroup root_;
        internal Dictionary<Signature, CGroup> cgroups_ = new Dictionary<Signature, CGroup>();

        public void SetRootGroup(CGroup root) => root_ = root;
        public CGroup LookupCGroup(LogicNode subtree) {
            var signature = subtree.GetHashCode();
            if (cgroups_.TryGetValue(signature, out CGroup group))
                return group;
            return null;
        }

        public CGroup InsertCGroup(LogicNode subtree)
        {
            var signature = subtree.GetHashCode();
            Debug.Assert(LookupCGroup(subtree) is null);
            var group = new CGroup(cgroups_.Count(), subtree);
            cgroups_.Add(signature, group);
            return group;
        }

        public string Print() {
            var str = "Memo:\n";

            // output by memo insertion order to read easier
            var list = cgroups_.OrderBy(x => x.Value.memoid_).ToList();
            foreach (var v in list) {
                var group = v.Value;
                if (group == root_)
                    str += "*";
                str += $"{group}:\t{group.Print()}\n";
            }
            return str;
        }

    }

    public static class Optimizer
    {
        public static Memo memo_ = new Memo();

        public static void EqueuePlan(LogicNode plan) {
            // equeue children first
            foreach (var v in plan.children_) {
                if (v.IsLeaf())
                {
                    if (memo_.LookupCGroup(v) is null)
                        memo_.InsertCGroup(v);
                }
                else
                    EqueuePlan(v);
            }

            // convert the plan with children node replace by memo cgroup
            var children = new List<LogicNode>();
            foreach (var v in plan.children_)
            {
                var child = memo_.LookupCGroup(v);
                Debug.Assert(child != null);
                children.Add(child);
            }
            plan.children_ = children;
            memo_.SetRootGroup(memo_.InsertCGroup(plan));
        }

        public static void SearchOptimal(PhysicProperty required)
        {
            Stack<CGroup> stack = new Stack<CGroup>();

            // push the root into stack
            stack.Push(memo_.root_);

            // loop through the stack until is empty
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                top.Optimize(stack, required);
            }
        }
    }
}

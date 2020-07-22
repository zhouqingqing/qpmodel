﻿/*
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
using System.Diagnostics;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.expr;
using qpmodel.utils;

using LogicSignature = System.Int64;

// TODO:
//  - branch and bound prouning
//  - aggregation/ctes: by generate multiple memo
//

namespace qpmodel.optimizer
{
    public class Property { }

    // ordering, distribution
    public class PhysicProperty : Property
    {
        public static PhysicProperty nullprop = new PhysicProperty();
        // ordering: the ordered expression and whether is descending
        public List<(Expr expr, bool desc)> ordering_ = new List<(Expr expr, bool desc)>();

        public bool IsPropertySupplied(PhysicNode node)
        {
            if (this.Equals(node.SuppiedProperty()))
                return true;
            return false;
        }
        public bool IsPropertyPropagated(PhysicNode node)
        {
            List<PhysicProperty> childprop = node.PropagatedProperty(this);
            foreach (var prop in childprop)
                if (prop != null) return true;
            return false;
        }

        internal PhysicNode OrderEnforcement(PhysicNode node)
        {
            List<Expr> order = new List<Expr>();
            List<bool> desc = new List<bool>();
            foreach (var pair in ordering_)
            {
                order.Add(pair.expr);
                desc.Add(pair.desc);
            }
            var logicnode = new LogicOrder(node.logic_, order, desc);
            return new PhysicOrder(logicnode, node);
        }

        public bool Equals(PhysicProperty other)
        {
            if (other is null || ordering_.Count != other.ordering_.Count)
                return false;
            for (int i = 0; i < ordering_.Count; i++)
            {
                if (!ordering_[i].expr.Equals(other.ordering_[i].expr))
                    return false;
                if (ordering_[i].desc != other.ordering_[i].desc)
                    return false;
            }
            return true;
        }
        public override bool Equals(object obj)
        {
            if (obj.GetType() != this.GetType()) return false;
            return this.Equals(obj as PhysicProperty);
        }
        public override int GetHashCode()
        {
            return ordering_.ListHashCode();
        }
        public override string ToString()
        {
            return string.Join(",", ordering_);
        }
    }

    public class SortOrderProperty : PhysicProperty
    {
        public SortOrderProperty(List<Expr> order, List<bool> desc = null)
        {
            if (desc is null)
                desc = new List<bool>(Enumerable.Repeat(false, order.Count).ToArray());
            Debug.Assert(order.Count == desc.Count);
            ordering_ = new List<(Expr, bool)>();
            for (int i = 0; i < order.Count; i++)
                ordering_.Add((order[i], desc[i]));
        }
    }

    // CGroupMember is a member of CMemoGroup, all these memebers are logically
    // equvalent but different logical/physical implementations
    //
    public class CGroupMember
    {
        public LogicNode logic_;
        public PhysicNode physic_;

        // record the property requirement of children
        public List<PhysicProperty> childProperties_;

        internal CMemoGroup group_;

        internal QueryOption QueryOption() => group_.memo_.stmt_.queryOpt_;
        internal SQLStatement Stmt() => group_.memo_.stmt_;

        internal LogicNode Logic()
        {
            LogicNode logic;
            if (logic_ != null)
                logic = logic_;
            else
                logic = physic_.logic_;
            Debug.Assert(!(logic is LogicMemoRef));
            return logic;
        }
        internal LogicSignature MemoSignature() => Logic().MemoLogicSign();

        public CGroupMember(LogicNode node, CMemoGroup group)
        {
            logic_ = node; group_ = group;
            Debug.Assert(!(Logic() is LogicMemoRef));
        }
        public CGroupMember(PhysicNode node, CMemoGroup group)
        {
            physic_ = node; group_ = group;
            Debug.Assert(!(Logic() is LogicMemoRef));
        }

        public void ValidateMember(bool optimizationDone)
        {
            Debug.Assert(Logic() != null);

            // the node itself is a non-memo node
            Debug.Assert(!(Logic() is LogicMemoRef));

            // solver optimized group is autonomous
            if (group_.IsSolverOptimizedGroup())
                return;

            // all its children shall be memo nodes and can be deref'ed to non-memo node
            Logic().children_.ForEach(x => Debug.Assert(
                    x is LogicMemoRef xl && !(xl.Deref() is LogicMemoRef)));

            if (physic_ != null)
            {
                // the physical node itself is non-memo node
                Debug.Assert(!(physic_ is PhysicMemoRef));

                // all its children shall be memo nodes and can be deref'ed to non-memo node
                physic_.children_.ForEach(x => Debug.Assert(
                    x is PhysicMemoRef xp && !(xp.Logic().Deref() is LogicMemoRef)));
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

        public override int GetHashCode()
        {
            if (logic_ != null)
                return logic_.GetHashCode();
            if (physic_ != null)
                return physic_.GetHashCode();
            throw new InvalidProgramException();
        }

        public override bool Equals(object obj)
        {
            if (obj is CGroupMember co)
            {
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
        internal List<CGroupMember> ExploreMember(Memo memo)
        {
            var list = group_.exprList_;
            foreach (var rule in group_.memo_.optimizer_.ruleset_)
            {
                if (rule.Appliable(this))
                {
                    // apply rule: sometimes it may simply returns the old member
                    var newmember = rule.Apply(this);
                    if (newmember == this)
                        continue;

                    // enqueue the new member - the exception is solver optimized group
                    // they will do the whole optimization themselves without insert
                    // new groups in memo.
                    //
                    var newlogic = newmember.Logic();
                    if (!(group_.IsSolverOptimizedGroup()))
                        memo.EnquePlan(newlogic);
                    if (!list.Contains(newmember))
                        list.Add(newmember);

                    // do some verification: newmember shall have the same signature as old ones
                    if (newlogic.MemoLogicSign() != list[0].MemoSignature())
                    {
                        Console.WriteLine("********* list[0]");
                        Console.WriteLine(list[0].Logic().Explain());
                        Console.WriteLine("********* newlogic");
                        Console.WriteLine(newlogic.Explain());
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
    public class CMemoGroup
    {
        // insertion order in memo
        public int memoid_;

        // signature represents a cgroup, all expression in the cgroup shall compute the
        // same signature though different cgroup ok to compute the same as well
        //
        public LogicSignature logicSign_;
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public List<CGroupMember> exprList_ = new List<CGroupMember>();

        public bool explored_ = false;

        // minMember_ depends on the property: each property has a different inclusive cost min member
        //    0: <no property required>, hash join
        //    1: <order on column a>, NLJ with outer table sorted on a (better than sort on HJ since
        //      the join generate many rows).
        //
        public Dictionary<PhysicProperty, (CGroupMember member, double cost)> minMember_ { get; set; }
            = new Dictionary<PhysicProperty, (CGroupMember, double)>();
        public double nullPropertyMinIncCost
        {
            get { return minMember_[PhysicProperty.nullprop].cost; }
        }


        // debug info
        internal Memo memo_;

        public CMemoGroup(Memo memo, int groupid, LogicNode subtree)
        {
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

        public void CountMembers(out int clogic, out int cphysic)
        {
            clogic = 0; cphysic = 0;
            foreach (var v in exprList_)
            {
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
            var str = $"{clogics}, {cphysics}, [{logicSign_}][{nullPropertyMinIncCost}]: ";
            str += string.Join(",", exprList_);

            // add property optimal member
            if (minMember_.Count > 0)
            {
                List<string> l = new List<string>();
                foreach (var pair in minMember_)
                    l.Add($"property:{pair.Key}, member:{pair.Value.member}, cost:{pair.Value.cost.ToString("0.##")}");
                str += "\n\t";
                str += string.Join("\n\t", l);
            }

            return str;
        }

        // Solver (say DPccp) optimized group don't expand in MEMO
        internal bool IsSolverOptimizedGroup()
        {
            return exprList_[0].Logic() is LogicJoinBlock;
        }

        internal void OptimizeSolverOptimizedGroupChildren(Memo memo)
        {
            Debug.Assert(IsSolverOptimizedGroup());
            CGroupMember member = exprList_[0];
            var joinblock = member.Logic() as LogicJoinBlock;

            // optimize all children group and newly generated groups
            var prevGroupCount = memo.cgroups_.Count;
            joinblock.children_.ForEach(x =>
                    (x as LogicMemoRef).group_.ExploreGroup(memo));
            while (memo.stack_.Count > prevGroupCount)
                memo.stack_.Pop().ExploreGroup(memo);

            // calculate the lowest inclusive members
            foreach (var c in joinblock.children_)
                (c as LogicMemoRef).group_.CalculateMinInclusiveCostMember();
        }

        // loop through and explore members of the group
        public void ExploreGroup(Memo memo)
        {
            // solver group shall optimize all its children before it can start
            if (IsSolverOptimizedGroup())
                OptimizeSolverOptimizedGroupChildren(memo);

            for (int i = 0; i < exprList_.Count; i++)
            {
                CGroupMember member = exprList_[i];

                // optimize the member and it shall generate a set of member
                member.ExploreMember(memo);
            }

            // mark the group explored
            explored_ = true;
        }

        // scan through the member list and return the least inclusive cost member
        //  since this procedure is for leaf nodes only, inclsuve cost equals cost
        //
        public void locateMinCostMemberForLeafNodes(PhysicProperty required)
        {
            double mincost = 0, propmincost = 0;

            if (required != null)
                propmincost = double.MaxValue;
            // calculate null requirement min cost if not available
            if (minMember_.Count == 0)
                mincost = double.MaxValue;

            foreach (var v in exprList_)
            {
                var physic = v.physic_;
                if (physic is null)
                    continue;

                // make sure childproperties is not null if there is children (for join resolver)
                v.childProperties_ = new List<PhysicProperty>(new PhysicProperty[physic.children_?.Count ?? 0]);

                // always update the null property min member
                if (physic.Cost() < mincost)
                {
                    mincost = physic.Cost();
                    minMember_[PhysicProperty.nullprop] = (v, mincost);
                }

                // directly supply the property
                if (required?.IsPropertySupplied(physic) ?? true && physic.Cost() < propmincost)
                {
                    propmincost = physic.Cost();
                    minMember_[required] = (v, propmincost);
                }
                // property is enforced
                else if (required != null && physic.Cost() + required.OrderEnforcement(physic).Cost() < propmincost)
                {
                    propmincost = physic.Cost() + required.OrderEnforcement(physic).Cost();
                    var enforced = new CGroupMember(required.OrderEnforcement(physic), this);
                    minMember_[required] = (enforced, propmincost);
                }
            }

            Debug.Assert(IsSolverOptimizedGroup() ||
                minMember_[PhysicProperty.nullprop].member.physic_.InclusiveCost() == minMember_[PhysicProperty.nullprop].cost);
        }

        // calculate the costs of the particular member
        // in general, there are two kinds of cost:
        // cost0 - no property required on children
        // cost1 - some property required on children, either propagated or directly required
        // correspondingly, two lists are maintained throught this function:
        // list0 - no required property on members
        // list1 - required property supplied by members
        //
        internal void CalculateMemberCosts(PhysicProperty required, CGroupMember member,
            List<(CGroupMember member, double cost)> supplied,
            List<(CGroupMember member, double cost)> nullprop)
        {
            var physic = member.physic_;
            var propagated = required?.IsPropertyPropagated(physic) ?? false;

            double cost0 = physic.Cost();
            double cost1 = physic.Cost();

            // initialize the childproperties corresponding to cost0 and cost1
            var nullchildprop = new List<PhysicProperty>(new PhysicProperty[physic.children_.Count]);
            var propchildprop = new List<PhysicProperty>(new PhysicProperty[physic.children_.Count]);

            for (int i = 0; i < physic.children_.Count; i++)
            {
                var child = physic.children_[i];
                var childgroup = (child as PhysicMemoRef).Group();

                // cost0 has no property required on children
                cost0 += childgroup.minMember_[PhysicProperty.nullprop].cost;

                // cost1 require subproperty on children
                // either propagated from required or required by physic node
                var subprop = physic.RequiredProperty() ?? physic.PropagatedProperty(required)[i];
                cost1 += childgroup.minMember_[subprop ?? PhysicProperty.nullprop].cost;
                propchildprop[i] = subprop;
            }

            // initialize the default for cost and member childproperties
            var cost = cost0;
            member.childProperties_ = nullchildprop;

            // when subproperty is required by physic node
            if (physic.RequiredProperty() != null)
            {
                member.childProperties_ = propchildprop;
                cost = cost1;
            }
            // the plain member with no additional requirement is added to list0
            nullprop.Add((member, cost));

            // when required is supplied by the current member
            // or supplied through propagation, add to list 1
            if (required != null && required.IsPropertySupplied(physic))
                supplied.Add((member, cost));
            if (propagated)
            {
                var clonemember = new CGroupMember(member.physic_, member.group_);
                clonemember.childProperties_ = propchildprop;
                supplied.Add((clonemember, cost1));
            }
        }

        // compute min member for null property
        internal void GetNullRequireMinCostTuple(List<(CGroupMember member, double cost)> nullprop)
        {
            double mincost = double.MaxValue;
            CGroupMember minmember = null;
            foreach (var pair in nullprop)
            {
                if (mincost > pair.cost)
                {
                    mincost = pair.cost;
                    minmember = pair.member;
                }
            }
            Debug.Assert(minmember != null);
            minMember_.Add(new PhysicProperty(), (minmember, mincost));
        }

        internal void GetPropertyMinCostTuple(PhysicProperty required, List<(CGroupMember member, double cost)> supplied)
        {
            double mincost = minMember_[PhysicProperty.nullprop].cost;
            CGroupMember minmember = minMember_[PhysicProperty.nullprop].member;
            mincost += required.OrderEnforcement(minmember.physic_).Cost();
            minmember = new CGroupMember(required.OrderEnforcement(minmember.physic_), this);

            foreach (var pair in supplied)
            {
                if (mincost > pair.cost)
                {
                    mincost = pair.cost;
                    minmember = pair.member;
                }
            }
            Debug.Assert(minmember != null);
            minMember_.Add(required, (minmember, mincost));
        }

        public CGroupMember CalculateMinInclusiveCostMember(PhysicProperty required = null, PhysicNode parent = null)
        {
            // inclusive cost is only possible after exploration done
            Debug.Assert(explored_);
            Debug.Assert(exprList_.Count >= 2);

            // if there is parent node requirement, independently do this calculation
            if (parent != null)
                CalculateMinInclusiveCostMember(parent.RequiredProperty());

            // check if we already done the calculation
            if (required == null && minMember_.ContainsKey(new PhysicProperty()))
                return minMember_[PhysicProperty.nullprop].member;
            if (required != null && minMember_.ContainsKey(required))
                return minMember_[required].member;

            if (exprList_[0].Logic().children_.Count == 0 || IsSolverOptimizedGroup())
            {
                // if this group has no children node or a block optimization, simply locate
                // the lowest one
                //
                locateMinCostMemberForLeafNodes(required);
            }
            else
            {
                // there are children. So for each member in the list, we calculate the inclusive
                // cost and return the lowest one
                //
                var supplied = new List<(CGroupMember member, double cost)>();
                var nullprop = new List<(CGroupMember member, double cost)>();

                for (int i = 0; i < exprList_.Count; i++)
                {
                    var physic = exprList_[i].physic_;
                    if (physic != null)
                    {
                        foreach (var child in physic.children_)
                        {
                            var childgroup = (child as PhysicMemoRef).Group();
                            childgroup.CalculateMinInclusiveCostMember(required, physic);
                        }

                        CalculateMemberCosts(required, exprList_[i], supplied, nullprop);
                    }
                }

                // calculate the null requirement min member when it is not already calculated
                if (!minMember_.ContainsKey(PhysicProperty.nullprop))
                    GetNullRequireMinCostTuple(nullprop);

                // calculate the min member for property requirement
                if (required != null)
                    GetPropertyMinCostTuple(required, supplied);
            }
            Debug.Assert(minMember_.ContainsKey(PhysicProperty.nullprop));

            // we filled the dictionary <proerpty, member> during the process, but only return
            // the one with required property, which is convenient for tree root.
            // 
            return minMember_[required ?? new PhysicProperty()].member;
        }

        public PhysicNode CopyOutMinLogicPhysicPlan(PhysicProperty property, PhysicNode knownMinPhysic = null)
        {
            var queryOpt = memo_.stmt_.queryOpt_;

            // if user does not provides a physic node, get the lowest inclusive
            // cost one from the member list. Either way, always use a clone to
            // not change memo itself.
            //
            CGroupMember minmember = null;
            if (knownMinPhysic is null)
            {
                minmember = CalculateMinInclusiveCostMember(property);
                knownMinPhysic = minmember.physic_;
            }
            var phyClone = knownMinPhysic.Clone();

            // recursively repeat the process for all its children
            //
            if (phyClone.children_.Count > 0 && !(phyClone is PhysicProfiling))
            {
                var phychildren = new List<PhysicNode>();
                var logchildren = new List<LogicNode>();
                for (int i = 0; i < phyClone.children_.Count; i++)
                {
                    var v = phyClone.children_[i];
                    // children shall be min cost
                    PhysicNode phychild;
                    if (v is PhysicMemoRef)
                    {
                        var g = (v as PhysicMemoRef).Group();
                        phychild = g.CopyOutMinLogicPhysicPlan(minmember?.childProperties_[i]);
                    }
                    else
                    {
                        // this shall not happen if without join resolver. With join resolver
                        // the plan is already given, so 'v' is the known min physic node
                        // or the member is enforced an order node on top
                        Debug.Assert(queryOpt.optimize_.memo_use_joinorder_solver_ ||
                            phyClone is PhysicOrder);
                        phychild = CopyOutMinLogicPhysicPlan(property, v);
                    }

                    // remount the physic and logic children list
                    phychildren.Add(phychild);
                    logchildren.Add(phychild.logic_);
                }

                // rewrite children
                phyClone.children_ = phychildren;
                phyClone.logic_.children_ = logchildren;
            }

            phyClone.logic_ = RetrieveLogicTree(phyClone.logic_);
            Debug.Assert(!(phyClone is PhysicMemoRef));
            Debug.Assert(!(phyClone.logic_ is LogicMemoRef));

            // enable profiling: we want to do it here instead of mark at the
            // end of plan copy out to avoid revisit plan tree (including expr
            // tree again)
            //
            if (queryOpt.profile_.enabled_)
                phyClone = new PhysicProfiling(phyClone);
            return phyClone;
        }
    }

    public class Memo
    {
        public SQLStatement stmt_;
        public CMemoGroup rootgroup_;
        public PhysicProperty rootProperty_;

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public Dictionary<LogicSignature, CMemoGroup> cgroups_ = new Dictionary<LogicSignature, CMemoGroup>();

        public Stack<CMemoGroup> stack_ = new Stack<CMemoGroup>();
        public Optimizer optimizer_;

        public Memo(SQLStatement stmt, Optimizer optimizer)
        {
            stmt_ = stmt;
            optimizer_ = optimizer;
        }
        public CMemoGroup LookupCGroup(LogicNode subtree)
        {
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

            if (stmt_.queryOpt_.optimize_.memo_use_joinorder_solver_)
            {
                // In this mode, we decompose the plan into multiple join graph with
                // other non-join nodes, which includes vertices in join graph and
                // plan node before join filter (say aggregation, sort etc).
                // All non-join nodes are enqueued as usual and join graphs are enqueued
                // as a one memo group disallowing optimization. In this way, we leverage 
                // both power of join solver and memo.
                //
                JoinGraph graph = null;
                LogicFilter filterNode = null;
                LogicNode filterNodeParent = null; int index = -1;
                graph = JoinResolver.ExtractJoinGraph(plan,
                                        out filterNodeParent, out index, out filterNode);

                // if join solver can't handle it, fallback
                if (graph != null)
                {
                    graph.memo_ = this;

                    // though it is not logically needed because we generate join filter according
                    // to the plan on the fly, we still do a join filter push down to keep the 
                    // logic signature consistent. The join filter can be skipped since all its 
                    // predicates are removed.
                    //
                    var andlist = filterNode.filter_.FilterToAndList();
                    andlist.RemoveAll(e => filterNode.PushJoinFilter(e));
                    Debug.Assert(andlist.Count == 0);

                    // enqueue the joinblock as a new node - need do enqueue nary join now to get the group
                    var joinblock = new LogicJoinBlock(filterNode.child_() as LogicJoin, graph);
                    joinblock.SetGroup(doEnquePlan(joinblock));

                    // stich the whole plan with new joinblock
                    if (filterNodeParent is null)
                        plan = joinblock;
                    else
                        filterNodeParent.children_[index] = joinblock;
                }
            }

            return doEnquePlan(plan);
        }

        public void ValidateMemo(bool optimizationDone = false)
        {
            // all groups are different
            Debug.Assert(cgroups_.Distinct().Count() == cgroups_.Count);
            foreach (var v in cgroups_)
            {
                CMemoGroup g = v.Value;
                g.ValidateGroup(optimizationDone);
            }
        }

        public string Print()
        {
            var str = "\nMemo:\n";
            int tlogics = 0, tphysics = 0;

            // output by memo insertion order to read easier
            var list = cgroups_.OrderBy(x => x.Value.memoid_).ToList();
            foreach (var v in list)
            {
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

    public class Optimizer
    {
        public List<Memo> memoset_ = new List<Memo>();
        public SQLStatement stmt_;
        public SelectStmt select_;
        public List<Rule> ruleset_ = new List<Rule>(Rule.ruleset_);

        public Optimizer(SQLStatement stmt)
        {
            // call once
            Rule.Init(ref ruleset_, stmt.queryOpt_);
            stmt_ = stmt;
            select_ = stmt.ExtractSelect();
            memoset_.Clear();
        }

        internal LogicNode ConvertOrder(LogicNode logicroot, Memo memo)
        {
            if (logicroot is LogicOrder order)
            {
                memo.rootProperty_ = new SortOrderProperty(order.orders_, order.descends_);
                return order.child_();
            }
            else return logicroot;
        }
        public void ExploreRootPlan(SQLStatement stmt, bool enqueueit = true)
        {
            var select = stmt.ExtractSelect();

            // each statement sitting in a new memo
            var memo = new Memo(select, this);
            if (enqueueit)
            {
                memoset_.Add(memo);

                // the statment shall already have plan generated
                var logicroot = select.logicPlan_;
                // convert top order node to requirement and extract root node
                memo.rootgroup_ = memo.EnquePlan(ConvertOrder(logicroot, memo));
            }

            // enqueue the subqueries: fromquery are excluded because different from 
            // other subqueries (IN, EXISTS etc), the subtree of it is actually connected 
            // in the same memo.
            //
            var subqueries = select.Subqueries();
            foreach (var v in subqueries)
            {
                enqueueit = !select.SubqueryIsWithMainQuery(v);
                ExploreRootPlan(v.query_, enqueueit);
            }

            // loop through the stack, explore each group until empty
            //
            while (memo.stack_.Count > 0)
            {
                var group = memo.stack_.Pop();
                group.ExploreGroup(memo);
            }

            memo.ValidateMemo();
        }

        public PhysicNode CopyOutOnePlan(SQLStatement stmt, Memo memo)
        {
            var select = stmt as SelectStmt;

            // retrieve the lowest cost plan
            Debug.Assert(stmt.physicPlan_ is null);
            stmt.physicPlan_ = memo.rootgroup_.CopyOutMinLogicPhysicPlan(memo.rootProperty_);
            stmt.logicPlan_ = stmt.physicPlan_.logic_;

            // fix fromQueries - the fromQueries are optimized as part of LogicNode tree
            // but we shall reconnect the logic node back to the Select Stmt level as needed
            // by column ordinal resolution. Physical node however is not needed.
            //
            foreach (var v in select.fromQueries_)
            {
                var fromQuery = (v.Key.query_ as SelectStmt);
                var generatedPlan = (v.Value as LogicFromQuery).child_();

                fromQuery.logicPlan_ = generatedPlan;
                Debug.Assert(fromQuery.physicPlan_ is null);
            }

            // finally let's fix the output
            (stmt as SelectStmt).ResolveOrdinals();
            return stmt.physicPlan_;
        }

        public PhysicNode CopyOutOptimalPlan(SQLStatement stmt, bool dequeueit = true)
        {
            var select = stmt as SelectStmt;
            PhysicNode phyplan = null;
            if (dequeueit)
            {
                var whichmemo = memoset_.Find(x => x.stmt_ == stmt);
                phyplan = CopyOutOnePlan(stmt, whichmemo);
            }
            var subqueries = select.Subqueries();
            foreach (var v in subqueries)
                CopyOutOptimalPlan(v.query_, !select.SubqueryIsWithMainQuery(v));
            return phyplan;
        }

        public PhysicNode CopyOutOptimalPlan()
        {
            PhysicNode selectplan = CopyOutOptimalPlan(select_);
            return stmt_.InstallSelectPlan(selectplan);
        }

        // Print memo: do not invoke it before copy out
        // This is because before copy out stage, we haven't associated cost for all possible groups. 
        // Calling print memo will print cost and causing assertions with cost is not NaN. Ideally, we
        // shall allow user indicates printCost=true|false but this is troublesome to do as we are
        // printing with ToString() interface which does not take extra argument.
        //
        public string PrintMemo()
        {
            string str = "";
            memoset_.ForEach(x => str += x.Print());
            return str;
        }
    }
}

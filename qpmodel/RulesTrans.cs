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

using System.Collections.Generic;
using System.Diagnostics;

using qpmodel.logic;
using qpmodel.expr;

namespace qpmodel.optimizer
{
    public abstract class Rule
    {
        public static List<Rule> ruleset_ = new List<Rule>() {
            new CteAnchor2CteAnchor(),
            new CteConsumer2CteConsumer(),
            new CteSelect2CteSelect(),
            new JoinAssociativeRule(),
            new JoinCommutativeRule(),
            new AggSplitRule(),
            new Join2NLJoin(),
            new Join2HashJoin(),
            new Join2MarkJoin(),
            new Join2SingleJoin(),
            new Scan2Scan(),
            new Scan2IndexSeek(),
            new ScanFile2ScanFile(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Agg2StreamAgg(),
            new Order2Sort(),
            new From2From(),
            new Limit2Limit(),
            new Seq2Seq(),
            new CteProd2CteProd(),
            new Append2Append(),
            new JoinBLock2Join(),
            new Gather2Gather(),
            new Bcast2Bcast(),
            new Redis2Redis(),
            new PSet2PSet(),
            new Sample2Sample(),
            new Result2Result(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
            new CteAnchor2CteProd(),
            new CteConsumer2CteSelect(),
        };

        // TBD: besides static controls, we can examine the plan and quick trim rules impossible to apply
        public static void Init(ref List<Rule> ruleset, QueryOption option)
        {
            if (!option.optimize_.enable_indexseek_)
                ruleset.RemoveAll(x => x is Scan2IndexSeek);
            if (!option.optimize_.enable_hashjoin_)
                ruleset.RemoveAll(x => x is Join2HashJoin);
            if (!option.optimize_.enable_streamagg_)
                ruleset.RemoveAll(x => x is Agg2StreamAgg);
            if (!option.optimize_.enable_nljoin_)
                ruleset.RemoveAll(x => x is Join2NLJoin);
            if (!option.optimize_.memo_use_remoteexchange_)
                ruleset.RemoveAll(x => x is AggSplitRule);
        }

        public abstract bool Appliable(CGroupMember expr);
        public abstract CGroupMember Apply(CGroupMember expr);
    }

    public abstract class ExplorationRule : Rule { }

    // There are two join exploration rules are used:
    //  1. Join commutative rule: AB => BA
    //  2. Left Join association rule: A(BC) => (AB)C
    //
    // Above two is RS-B1 in https://anilshanbhag.in/static/papers/rsgraph_vldb14.pdf
    // It is complete to exhaust search space but with duplicates with
    // the condition cross-join shall not be suppressed.
    // Another more efficient complete and duplicates free set is RS-B2.
    //
    public class JoinCommutativeRule : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin lj && lj.IsInnerJoin();
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var l = join.lchild_(); var r = join.rchild_(); var f = join.filter_;

            Debug.Assert(!l.LeftReferencesRight(r));
            if (r.LeftReferencesRight(l))
                return expr;

            LogicJoin newjoin = new LogicJoin(r, l, f);
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
    // 3. Join filter shall be is handled by first pull up all join filters 
    //    then push them back to the new join plan.
    //  
    //  we do not generate Cartesian join unless input is Cartesian.
    //
    public class JoinAssociativeRule : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            if (!(expr.logic_ is LogicJoin a_bc) || !a_bc.IsInnerJoin())
                return false;

            var bc = (a_bc.rchild_() as LogicMemoRef).Deref();
            if (bc is LogicJoin bcj)
            {
                if (!bcj.IsInnerJoin())
                    return false;

                // we only reject cases that logically impossible to apply
                // association rule, but leave anything may generate worse
                // plan (say Cartesian joins) to apply stage.
                return true;
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;
            LogicNode a = (a_bc.lchild_() as LogicMemoRef).Deref<LogicNode>();
            LogicJoin bc = (a_bc.rchild_() as LogicMemoRef).Deref<LogicJoin>();
            Expr bcfilter = bc.filter_;
            var ab = new LogicJoin(a_bc.lchild_(), bc.lchild_());
            var c = bc.rchild_();
            var ab_c = new LogicJoin(ab, c);

            Debug.Assert(!a.LeftReferencesRight(bc));
            if (ab.LeftReferencesRight(c))
                return expr;

            // pull up all join filters and re-push them back            
            Expr allfilters = bcfilter;
            if (a_bc.filter_ != null)
                allfilters = allfilters.AddAndFilter(a_bc.filter_);
            if (allfilters != null)
            {
                var andlist = allfilters.FilterToAndList();
                andlist.RemoveAll(e => ab_c.PushJoinFilter(e));
                if (andlist.Count > 0)
                    ab_c.filter_ = andlist.AndListToExpr();
            }

            // Ideally if there is no cross join in the given plan but cross join 
            // in the new plan, we shall return the original plan. However, stop
            // exploration now will prevent generating other promising plans. So 
            // we have to return the new plan.
            //
            if (expr.QueryOption().optimize_.memo_disable_crossjoin_)
            {
                if (a_bc.filter_ != null && bcfilter != null)
                {
                    if (ab_c.filter_ is null || ab.filter_ is null)
                        return expr;
                }
            }

            return new CGroupMember(ab_c, expr.group_);
        }
    }

    public class AggSplitRule : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            if (!(expr.logic_ is LogicAgg agg) || agg.isLocal_ || agg.isDerived_)
                return false;
            return true;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            // for manually binding the expression
            void manualbindexpr(Expr e)
            {
                e.bounded_ = true;
                e.type_ = new BoolType();
            }

            // for transforming original having according to the new agg func
            BinExpr processhaving(Expr e, Dictionary<Expr, Expr> dict)
            {
                var be = e as BinExpr;
                Debug.Assert(be != null);
                bool isreplace = false;
                List<Expr> children = new List<Expr>();
                foreach (var child in be.children_)
                {
                    if (dict.ContainsKey(child))
                    {
                        children.Add(dict[child]);
                        isreplace = true;
                    }
                    else
                        children.Add(child);
                }
                Debug.Assert(isreplace);
                return new BinExpr(children[0], children[1], be.op_);
            }

            LogicAgg origAggNode = (expr.logic_ as LogicAgg);
            var childNode = (origAggNode.child_() as LogicMemoRef).Deref<LogicNode>();

            var groupby = origAggNode.groupby_?.CloneList();
            var having = origAggNode.having_?.Clone();

            // process the aggregation functions
            origAggNode.GenerateAggrFns(false);

            List<AggFunc> aggfns = new List<AggFunc>();
            origAggNode.aggrFns_.ForEach(x => aggfns.Add(x.Clone() as AggFunc));
            // need to make aggrFns_ back to null list
            origAggNode.aggrFns_ = new List<AggFunc>();

            var globalfns = new List<Expr>();
            var localfns = new List<Expr>();

            // record the processed aggregate functions
            var derivedAggFuncDict = new Dictionary<Expr, Expr>();

            foreach (var func in aggfns)
            {
                Expr processed = func.SplitAgg();
                // if splitagg is returning null, end the transformation process
                if (processed is null)
                    return expr;

                // force the id to be equal.
                processed._ = func._;

                globalfns.Add(processed);
                derivedAggFuncDict.Add(func, processed);
            }

            var local = new LogicAgg(childNode, groupby, localfns, null)
            {
                isLocal_ = true
            };

            // having is placed on the global agg and the agg func need to be processed
            var newhaving = having;
            if (having != null)
            {
                newhaving = processhaving(having, derivedAggFuncDict);
                manualbindexpr(newhaving);
            }

            // assuming having is an expression involving agg func,
            // it is only placed on the global agg
            var global = new LogicAgg(local, groupby, globalfns, newhaving)
            {
                isDerived_ = true
            };
            global.Overridesign(origAggNode);
            global.deriveddict_ = derivedAggFuncDict;

            return new CGroupMember(global, expr.group_);
        }
    }

    public class CteAnchor2CteProd : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicCteAnchor;
        }
        public override CGroupMember Apply(CGroupMember expr)
        {
            //LogicJoin log = expr.logic_ as LogicJoin;
            //// we expand the logic plan of CTE
            ///
            LogicCteAnchor cteAnchor = expr.logic_ as LogicCteAnchor;
            CteInfoEntry cteInfoEntry = cteAnchor.cteInfoEntry_;
            LogicCteProducer cteProducer = new LogicCteProducer(cteInfoEntry);
            // left is cteProducer and right is the child of cteAnchor
            LogicSequence ls = new LogicSequence(cteProducer, cteAnchor.child_(), cteAnchor);
            return new CGroupMember(ls, expr.group_);
        }
    }

    public class CteConsumer2CteSelect : ExplorationRule
    {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicCteConsumer;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            // create a group member CteSelect
            var memo = expr.group_.memo_;
            LogicCteConsumer cteConsumer = expr.logic_ as LogicCteConsumer;
            LogicSelectCte logicSelectCte = new LogicSelectCte(cteConsumer, memo);
            return new CGroupMember(logicSelectCte, expr.group_);
        }
    }
}

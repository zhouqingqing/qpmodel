/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using qpmodel.logic;
using qpmodel.expr;
using qpmodel.utils;

namespace qpmodel.optimizer
{
    public class Rule
    {
        public static List<Rule> ruleset_ = new List<Rule>() {
            new JoinAssociativeRule(),
            new JoinCommutativeRule(),
            new Join2NLJoin(),
            new Join2HashJoin(),
            new Join2MarkJoin(),
            new Join2SingleJoin(),
            new Scan2Scan(),
            new Scan2IndexSeek(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Order2Sort(),
            new From2From(),
            new Limit2Limit(),
            new Seq2Seq(),
            new CteProd2CteProd(),
            new Append2Append(),
            new JoinBLock2Join(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        // TBD: besides static controls, we can examine the plan and quick trim rules impossible to apply
        public static void Init(QueryOption option) {
            if (!option.optimize_.enable_indexseek_)
                ruleset_.RemoveAll(x => x is Scan2IndexSeek);
            if (!option.optimize_.enable_hashjoin_)
                ruleset_.RemoveAll(x => x is Join2HashJoin);
            if (!option.optimize_.enable_nljoin_)
                ruleset_.RemoveAll(x => x is Join2NLJoin);
        }

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

    // There are two join exploration rules are used:
    //  1. Join commutative rule: AB => BA
    //  2. Left Join association rule: A(BC) => (AB)C
    //
    // Above two is RS-B1 in https://anilshanbhag.in/static/papers/rsgraph_vldb14.pdf
    // It is complete to exhaust search space but with duplicates with
    // the condition cross-join shall not be suppressed.
    // Another more efficient complete and duplicates free set is RS-B2.
    //
    public class JoinCommutativeRule : ExplorationRule {

        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin lj && lj.IsInnerJoin();
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var l = join.l_(); var r = join.r_(); var f = join.filter_;

            Debug.Assert(!l.LeftReferencesRight(r));
            if (r.LeftReferencesRight(l))
                return expr;

            LogicJoin newjoin = new LogicJoin(r,l,f);
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
    //  we do not generate catersian join unless input is catersian.
    //
    public class JoinAssociativeRule : ExplorationRule
    {
        // Extract filter matching ABC's tablerefs
        // ABC=[a,b]
        //  a.i=b.i AND a.j=b.j AND a.k+b.k=c.k => a.i=b.i AND a.j=b.j
        // ABC=[a,b,c]
        //   a.i=b.i AND a.j=b.j AND a.k+b.k=c.k => a.k+b.k=c.k
        Expr exactFilter(Expr fullfilter, List<LogicNode> ABC)
        {
            Expr ret = null;
            if (fullfilter is null)
                return null;

            List<TableRef> ABCtabrefs = new List<TableRef>();
            foreach (var m in ABC)
                ABCtabrefs.AddRange(m.InclusiveTableRefs());

            var andlist = fullfilter.FilterToAndList();
            foreach (var v in andlist)
            {
                var predicate = v as BinExpr;
                var predicateRefs = predicate.tableRefs_;
                if (ABCtabrefs.ListAEqualsB( predicateRefs))
                {
                    ret = ret.AddAndFilter(predicate);
                }
            }

            return ret;
        }

        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;
            if (a_bc is null || !a_bc.IsInnerJoin())
                return false;

            var bc = (a_bc.r_() as LogicMemoRef).Deref();
            var bcfilter = bc.filter_;
            if (bc is LogicJoin bcj) {
                if (!bcj.IsInnerJoin())
                    return false;

                // we only reject cases that logically impossible to apply
                // association rule, but leave anything may generate worse
                // plan (say catersisan joins) to apply stage.
                return true;
            }
            return false;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;
            LogicNode a = (a_bc.l_() as LogicMemoRef).Deref<LogicNode>();
            LogicJoin bc = (a_bc.r_() as LogicMemoRef).Deref<LogicJoin>();
            Expr bcfilter = bc.filter_;
            var ab = new LogicJoin(a_bc.l_(), bc.l_());
            var c = bc.r_();
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
}

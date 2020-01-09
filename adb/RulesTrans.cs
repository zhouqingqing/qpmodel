using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public class Rule
    {
        public static Rule[] ruleset_ = {
            new JoinAssociativeRule(),
            new JoinCommutativeRule(),
            new Join2NLJoin(),
            new Join2HashJoin(),
            new Join2MarkJoin(),
            new Scan2Scan(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Order2Sort(),
            new From2From(),
            new Limit2Limit(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

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

            if (a_bc != null)
            {
                if (!a_bc.IsInnerJoin())
                    return false;

                var bc = (a_bc.r_() as LogicMemoRef).Deref();
                var bcfilter = bc.filter_;
                if (bc is LogicJoin bcj) {
                    if (!bcj.IsInnerJoin())
                        return false;

                    Expr abcfilter = a_bc.filter_;
                    var abfilter = exactFilter(abcfilter,
                        new List<LogicNode>(){
                            a_bc.l_(), bc.l_()});

                    // if there is no filter at all, we are fine but we don't
                    // allow the case we may generate catersian product
                    if (abfilter != null && bcfilter is null)
                        return false;
                    return true;
                }
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

            return new CGroupMember(ab_c, expr.group_);
        }
    }

}

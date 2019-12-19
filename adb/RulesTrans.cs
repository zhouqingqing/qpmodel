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
            new JoinToHashJoin(),
            new Scan2Scan(),
            new Filter2Filter(),
            new Agg2HashAgg(),
            new Order2Sort(),
            new From2From(),
            new JoinCommutativeRule(),  // intentionally add a duplicated rule
        };

        public virtual bool Appliable(CGroupMember expr) => false;
        public virtual CGroupMember Apply(CGroupMember expr) =>  null;
    }

    public class ExplorationRule : Rule { }

    public class JoinCommutativeRule : ExplorationRule {
        public override bool Appliable(CGroupMember expr)
        {
            return expr.logic_ is LogicJoin lj && lj.type_ == JoinType.InnerJoin;
        }

        public override CGroupMember Apply(CGroupMember expr)
        {
            LogicJoin join = expr.logic_ as LogicJoin;
            var newjoin = new LogicJoin(join.r_(), join.l_(), join.filter_);
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
    // 3. Join filter shall be considered:
    //      A JOIN (B JOIN C [on bcfilter]) [on abfilter [AND acfilter]]
    //  =>  (A JOIN B [on abfilter]) JOIN C [on bcfilter [AND acfilter]]
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

            var andlist = FilterHelper.FilterToAndList(fullfilter);
            foreach (var v in andlist)
            {
                var predicate = v as BinExpr;
                var predicateRefs = predicate.tableRefs_;
                if (Utils.ListAEqualsB(ABCtabrefs, predicateRefs))
                {
                    ret = FilterHelper.AddAndFilter(ret, predicate);
                    Console.WriteLine(predicate);
                }
            }

            return ret;
        }

        public override bool Appliable(CGroupMember expr)
        {
            LogicJoin a_bc = expr.logic_ as LogicJoin;

            if (a_bc != null)
            {
                var bc = (a_bc.r_() as LogicMemoRef).Deref();
                var bcfilter = bc.filter_;
                if (bc is LogicJoin) {
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
            Expr fullfilter = a_bc.filter_;
            LogicJoin bc = (a_bc.r_() as LogicMemoRef).Deref<LogicJoin>();
            Expr bcfilter = bc.filter_;
            var abfilter = exactFilter(fullfilter,
                new List<LogicNode>(){a_bc.l_(), bc.l_()});
            var acfilter = exactFilter(fullfilter,
                new List<LogicNode>(){a_bc.l_(), bc.r_() });
            var abcfilter = exactFilter(fullfilter,
                new List<LogicNode>(){a_bc.l_(), bc});

            var topfilter = bcfilter;
            if (acfilter != null)
                topfilter = FilterHelper.AddAndFilter(topfilter, acfilter);
            if (abcfilter != null)
                topfilter = FilterHelper.AddAndFilter(topfilter, abcfilter);

            var ab_c = new LogicJoin(
                new LogicJoin(a_bc.l_(), bc.l_(), abfilter),
                bc.children_[1], topfilter);
            return new CGroupMember(ab_c, expr.group_);
        }
    }

}

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
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;
using qpmodel.index;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;
using qpmodel.expr;
using System.Reflection.Metadata.Ecma335;

namespace qpmodel.normalizer
{

    public enum NormalizerState
    {
        RuleNormalizerNone
      , RuleSimplifyConstants
      , RuleSimplifyArithmetic
      , RuleSimplifyLogical
      , RuleSimplifyRelational
      , RuleSimplifyCase
      , RuleSimplifyInlist
      , RuleRemoveDistinctAgg
      , RuleRemoveCast
      , RuleRemoveCSE
      , RuleNormalizerAll // All done
    }

    public abstract class NormalizerRule
    {
        public static List<NormalizerRule> ruleset_ = new List<NormalizerRule>()
        {
            new SimplifyConstants()
            /*
            , new SimplifyArithmetic()
            , new SimplifyLogical()
            , new SimplifyRelational()
            , new SimplifyCase()
            , new SimplifyInlist()
            , new RemoveDistinctAgg()
            , new RemoveCast()
            , new RemoveCSE()
            */
        };

        public abstract bool NormalizerRuleApplicable(Expr e);
        public abstract Expr NormalizerRuleApply(Expr e);

        public bool isCommutativeConstOper(BinExpr be) => (be.op_ == "+" || be.op_ == "*");
        public bool isFoldableConstOper(BinExpr be) => (be.op_ == "+" || be.op_ == "-" || be.op_ == "*" || be.op_ == "/");

        public bool isPlainSwappableConstOper(BinExpr be) => (be.op_ == "+" || be.op_ == "*" || be.op_ == "<>" || be.op_ == "!=" || be.op_ == "<=" || be.op_ == ">=");

        public bool isChangeSwappableConstOper(BinExpr be) => (be.op_ == "<" || be.op_ == ">");
    }


    public class SimplifyConstants : NormalizerRule
    {
        private bool DecideConstantMoveFold(Expr e)
        {
            /* more will be added once the simple cases start working. */
            if (e is BinExpr bf)
            {
                switch (bf.op_)
                {
                    case "+":
                    case "*":
                    /*case "||": : WAIT a while */
                    // These two (<> and !=) are here because they don't
                    // require modifying the LSH/RHS value.
                    case "<>":
                    case "!=":
                        // if (e.children_[0] is ConstExpr || e.children_[1] is ConstExpr)
                            return true;
                }
            }

            return false;
        }

        public override bool NormalizerRuleApplicable(Expr e)
        {
            // basically if you are not a const, or colref, you may be const
            // simplified if other conditions permit.
            if (e is ConstExpr || e is ColExpr || !(e is Expr))
                return false;
            return true; // DecideConstantMoveFold(e);
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            return e;
            e.normalizerState_ = NormalizerState.RuleSimplifyConstants;
            
            Expr newexp = e.Clone();

            if (e is null)
            {
                return null;
            }

            if (!NormalizerRuleApplicable(e))
                return e;

            Expr l = null, r = null;
            BinExpr be = null;
            if (e is BinExpr)
            {
                be = (BinExpr)e;
                l = NormalizerRuleApply(be.children_[0]);
                r = NormalizerRuleApply(be.children_[1]);
            }
            else
            {
                /* others will have to wait. */
                return e;
            }

            if (l is ConstExpr && r is ConstExpr)
            {
                if (isFoldableConstOper(be))
                {
                    Value val;
                    bool constResult = be.TryEvalConst(out val);
                    if (constResult)
                    {
                        newexp = ConstExpr.MakeConst(val, be.type_, be.outputName_);                       
                        // be.children_.Clear();
                    }
                }
            }
            else if (l is ConstExpr && !(r is ConstExpr))
            {
                if (isPlainSwappableConstOper(be))
                {
                    newexp = e.Clone();
                    newexp.children_[0] = r;
                    newexp.children_[1] = l;
                    // be.children_.Clear();
                }
                /* deal with Change swappable later */
            }
            else if (l is BinExpr le && le.children_[1].IsConst() && (r is ConstExpr) && isCommutativeConstOper(be) && isCommutativeConstOper(le))
            {
                /*
                    * Here root is distributive operator (only * in this context) left is Commutative
                    * operator, +, or * right is constant, furthermore, left's right is a constant
                    * (becuase we swapped cosntant to be on the right).
                    * if be == + and l == +: add left's right value to root's right value,
                    * make  left's left as left of root
                    * 
                    * if be == * and l == +: create a expr node as left (x + 10), create (5 * 10)
                    * as right, change operator to +
                    * In either case left and right's children must be nulled out
                    * and since we are going bottom up, this doesn't create any problem. 
                    * 
                    * Here is a pictorial description:
                    *                         *         root           +
                    *              old left  / \ old right   new left / \  new right
                    *                       /   \                    /   \
                    *                      +     10        =>       *     50
                    *  left of old left   / \                      / \
                    *                    /   \ ROL         LNL    /   \ RNL (right of New Left)
                    *                   x     5                  x     10
                    */

                   /*
                    * Simple case: when current and left are same operators, distributive
                    * opeartion and node creation is uncessary.
                    * ??? What about ordinal positions ???
                    */
                if ((be.op_ == "+" && le.op_ == "+") || (be.op_ == "*" && le.op_ == "*"))
                {
                    
                    /* create new right node as constant. */
                    Expr tmpexp = be.Clone();
                    tmpexp.children_[0] = le.children_[1];
                    tmpexp.children_[1] = r;
                    Value val;
                    bool wasConst = tmpexp.TryEvalConst(out val);
                    Expr newr = ConstExpr.MakeConst(val, be.type_, r.outputName_);

                    // create new root
                    newexp = be.Clone();

                    // new left is old left's left child
                    // of left will be right of the root.
                    newexp.children_[0] = l.children_[0];

                    // new right is the new constant node
                    newexp.children_[1] = newr;

                    // be.children_.Clear();
                }
                else if (be.op_ == "*")
                {
                    /* case of (a + const1) * const2  => (a * const2) + (const1 * const2)) */
                    /* make a newe left node to house (a * const2) */
                    Expr newl = le.Clone();
                    newl.children_[0] = le;
                    newl.children_[1] = r;

                    /* make a const expr node to evaluate and house (const1 op const 2) */
                    Expr tmpexp = be.Clone();
                    tmpexp.children_[0] = le.children_[1]; // right of left is const
                    tmpexp.children_[1] = r;               // our right is const
                    Value val;

                    tmpexp.TryEvalConst(out val);
                    Expr newr = ConstExpr.MakeConst(val, be.type_, r.outputName_);

                    /* now make a new root and attach newl and new r to it. */
                    newexp = be.Clone();
                    newexp.children_[0] = newl;
                    newexp.children_[1] = newr;
                    string op = be.op_;

                    /* swap the operators */
                    BinExpr bne = (BinExpr)newexp;
                    bne.op_ = le.op_;

                    BinExpr nle = (BinExpr)newl;
                    nle.op_ = op;

                    // l.children_.Clear();
                    // r.children_.Clear();
                    // be.children_.Clear();
                }
                /* we can't do any thing about + at the top and * as left child. */
            }
            else if (l != null && r != null && (be.children_[0] != l || be.children_[1] != r))
            {
                newexp = be.Clone();
                newexp.children_[0] = l;
                newexp.children_[1] = r;
            }

            if (newexp != null)
                return newexp;

            return e;
        }
    }

    public class SimplifyArithmetic : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
                throw new NotImplementedException();
        }
    }
    
    public class SimplifyLogical : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }
    
    public class SimplifyRelational : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }


    public class SimplifyCase : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }

    public class SimplifyInlist : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }
            
    public class RemoveDistinctAgg : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }
            
    public class RemoveCast : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }


    public class RemoveCSE : NormalizerRule
    {
        public override bool NormalizerRuleApplicable(Expr e)
        {
            throw new NotImplementedException();
        }

        public override Expr NormalizerRuleApply(Expr e)
        {
            throw new NotImplementedException();
        }
    }


    public class Normalizer
    {
        internal List<NormalizerRule> ruleset_ = NormalizerRule.ruleset_;

        public Expr normalize(Expr e)
        {
            return e;

            Expr ne = e;
            Expr ecp = e;
            ruleset_.ForEach(r =>
            {
                if (r.NormalizerRuleApplicable(ecp))
                {
                    while (true)
                    {
                        ne = r.NormalizerRuleApply(ecp);
                        if (ne == ecp)
                            break;
                        ecp = ne;
                    }
                }
            });

            return ne;
        }
    }
}

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
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;
using qpmodel.index;
using IronPython.Compiler.Ast;

namespace qpmodel.expr
{

    public partial class Expr
    {
        public virtual Expr Normalize()
        {
            for (int i = 0; i < children_.Count; i++)
            {
                Expr x = children_[i];
                x = x.Normalize();
                children_[i] = x;
            }

            return this;
        }

        public bool AllArgsConst()
        {
            int constCount = 0;
            children_.ForEach(x =>
            {
                if (x is ConstExpr)
                    ++constCount;
            });

            return children_.Count == constCount;
        }

        public bool AnyArgNull()
        {
            for (int i = 0; i < children_.Count; ++i)
            {
                if (children_[i] is ConstExpr ce && ce.IsNull())
                {
                    return true;
                }
            }

            return false;
        }
    }

    public partial class FuncExpr
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst() || ExternalFunctions.set_.ContainsKey(funcName_))
                return this;

            if (x.AnyArgNull())
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);

            switch (funcName_)
            {
                case "min":
                case "max":
                case "avg":
                    return child_();

                case "sum":
                case "count":
                case "count(*)":
                case "coalesce":
                case "tumble":
                case "tumble_start":
                case "tumble_end":
                case "hop":
                case "session":
                case "stddev_samp":
                    return this;

                default:
                    break;
            }

            Value val = Exec(null, null);
            return ConstExpr.MakeConst(val, type_, outputName_);
        }
    }


    public partial class CoalesceFunc
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!x.AllArgsConst())
                return x;

            for (int i = 0; i < children_.Count; ++i)
            {
                if (children_[i] is ConstExpr ce && !ce.IsNull())
                    return ce;
            }

            return x;
        }
    }

    public partial class UnaryExpr
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (x.AllArgsConst())
            {
                Value val = Exec(null, null);
                return ConstExpr.MakeConst(val, type_, outputName_);
            }

            // NOT NOT X => X
            if (op_ == "!")
            {
                if (child_() is UnaryExpr ue && ue.op_ == "!")
                {
                    return ue.child_();
                }

                // NOT (X AND Y)    => NOT A OR NOT B
                // NOT (X OR Y)     => NOT A AND NOT B
                /*
                 *           NOT                  OR
                 *            |                  /  \
                 *           AND      =>       NOT    NOT
                 *          /    \              |      |
                 *         X      Y             X      Y
                 */

                if (child_() is LogicAndOrExpr le)
                {

                    // Make two Unary expressions for NOT X and NOT Y
                    // Cloning avoids messing with bounded_ flag
                    UnaryExpr nlu = (UnaryExpr)Clone();
                    UnaryExpr nru = (UnaryExpr)Clone();

                    // get the new logical operator, reverse of current one
                    string nop = le.op_ == " and " ? " or " : " and ";

                    // save X, Y
                    Expr ole = le.lchild_();
                    Expr ore = le.rchild_();

                    string leftName = ole.outputName_;
                    string rightName = ore.outputName_;
                    string thisName = outputName_;

                    // New Unary nodes point to old left and right
                    // logical expressions.
                    nlu.children_[0] = ole;
                    nru.children_[0] = ore;

                    return makeAnyLogicalExpr(nlu, nru, nop);
                }
            }

            return x;
        }
    }

    public partial class BinExpr
    {
        public override Expr Normalize()
        {
            // all children get normalized first
            for (int i = 0; i < children_.Count; ++i)
            {
                Expr x = children_[i];
                children_[i] = x.Normalize();
            }

            Expr l = lchild_();
            Expr r = rchild_();
            ConstExpr lce = (l is ConstExpr) ? (ConstExpr)l : null;
            ConstExpr rce = (r is ConstExpr) ? (ConstExpr)r : null;

            switch (op_)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                case ">":
                case ">=":
                case "<":
                case "<=":
                case "||":
                case "=":
                case "<>":
                case "!=":
                case " and ":
                case " or ":
                    if (!IsRelOp() && (lce != null && lce.val_ is null) || (rce != null && rce.val_ is null))
                    {
                        // NULL simplification: if operator is not relational, X op NULL is NULL
                        return lce is null ? rce : lce;
                    }

                    if (lce != null && rce != null)
                    {
                        // Simplify Constants: children are not non null constants, evaluate them.
                        Value val = Exec(null, null);
                        return ConstExpr.MakeConst(val, type_, outputName_);
                    }

                    if (lce != null && rce == null)
                    {
                        if (isPlainSwappableConstOp())
                            SwapSide();
                    }

                    if ((lce != null || rce != null) && (IsArithIdentity(lce, rce)))
                        return SimplifyArithmetic(lce, rce);

                    if (IsLogicalOp())
                        return SimplifyLogic();

                    if (IsRelOp())
                        return SimplifyRelop();

                    // arithmetic operators?
                    if (l is BinExpr le && le.children_[1].IsConst() && (rce != null) &&
                        isCommutativeConstOp() && le.isCommutativeConstOp() && TypeBase.SameArithType(l.type_, r.type_))
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
                        if ((op_ == "+" && le.op_ == "+") || (op_ == "*" && le.op_ == "*"))
                        {

                            /* create new right node as constant. */
                            Expr tmpexp = Clone();
                            tmpexp.children_[0] = le.children_[1];
                            tmpexp.children_[1] = r;
                            tmpexp.type_ = r.type_;

                            Value val;
                            bool wasConst = tmpexp.TryEvalConst(out val);
                            Expr newr = ConstExpr.MakeConst(val, tmpexp.type_, r.outputName_);

                            // new left is old left's left child
                            // of left will be right of the root.
                            children_[0] = l.children_[0];

                            // new right is the new constant node
                            children_[1] = newr;
                        }
                        else
                        if (op_ == "*")
                        {
                            /*
                             * case of (a + const1) * const2  => (a * const2) + (const1 * const2))
                             * make a newe left node to house (a * const2)
                             */
                            Expr newl = le.Clone();
                            newl.children_[0] = le;
                            newl.children_[1] = r;

                            /* make a const expr node to evaluate and house (const1 op const 2) */
                            Expr tmpexp = Clone();
                            tmpexp.children_[0] = le.children_[1]; // right of left is const
                            tmpexp.children_[1] = r;               // our right is const
                            tmpexp.type_ = r.type_;

                            Value val;
                            tmpexp.TryEvalConst(out val);
                            Expr newr = ConstExpr.MakeConst(val, tmpexp.type_, r.outputName_);

                            /* now make a new root and attach newl and new r to it. */
                            children_[0] = newl;
                            children_[1] = newr;

                            /* swap the operators */
                            string op = op_;
                            op_ = le.op_;

                            BinExpr ble = (BinExpr)children_[1];
                            ble.op_ = op;
                        }
                        /* we can't do any thing about + at the top and * as left child. */
                    }

                    return this;
            }

            return this;
        }
    }


    public partial class CastExpr
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            Expr arg = x.child_();

            if (arg != null && arg is ConstExpr ce)
            {
                Value val = Exec(null, null);
                return ConstExpr.MakeConst(val, type_, outputName_);
            }

            return this;
        }
    }
}

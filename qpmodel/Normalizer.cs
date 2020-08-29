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
using System.Runtime.CompilerServices;

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
                    return true;
            }

            return false;
        }

        private void FixNewExprTableRefs(Expr e)
        {
            if (e.tableRefs_.Count > 0)
            {
                if (tableRefs_.Count == 0)
                    tableRefs_ = new List<TableRef>();
                e.tableRefs_.ForEach(x => tableRefs_.Add(x));
            }
        }

        // This is not a general purpose helper, so do not use it to make
        // "correct" logical operator node based on the operator, it used
        // here in the context of children already are bound only the
        // new node needs to marked bound. This is to be used only
        // within the context of Normalize method of BinExpr.
        internal Expr makeAnyLogicalExpr(Expr l, Expr r, string op)
        {
            if (op == " and ")
            {
                LogicAndExpr newe = new LogicAndExpr(l, r);
                newe.bounded_ = true;
                newe.FixNewExprTableRefs(l);
                if (r.tableRefs_.Count > 0 && !newe.TableRefsContainedBy(r.tableRefs_))
                    newe.FixNewExprTableRefs(r);
                return newe;
            }
            else
            {
                LogicOrExpr newe = new LogicOrExpr(l, r);
                newe.FixNewExprTableRefs(l);
                if (r.tableRefs_.Count > 0 && !newe.TableRefsContainedBy(r.tableRefs_))
                    newe.FixNewExprTableRefs(r);
                newe.bounded_ = true;

                return newe;
            }
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
        public bool isCommutativeConstOp() =>
            (op_ == "+" || op_ == "*");
        public bool isFoldableConstOp() =>
            (op_ == "+" || op_ == "-" || op_ == "*" || op_ == "/");
        public bool isPlainSwappableConstOp() =>
            (op_ == "+" || op_ == "*" || op_ == "=" || op_ == "<>" || op_ == "!=" || op_ == "<=" || op_ == ">=");
        public bool isChangeSwappableConstOp() =>
            (op_ == "<" || op_ == ">");

        // Looks the same as isFoldableConstOp but the context/purpose is
        // different. If it turns out that they are one and the same, one of
        // them will be removed.
        internal bool IsLogicalOp() =>
            (op_ == " and " || op_ == " or " || op_ == "not");
        internal bool IsArithmeticOp() =>
            (op_ == "+" || op_ == "-" || op_ == "*" || op_ == "/");
        internal bool IsRelOp() =>
            (op_ == "=" || op_ == "<=" || op_ == "<" || op_ == ">=" || op_ == ">" || op_ == "<>" || op_ == "!=" || op_ == "is" || op_ == "is not");

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
                case "is":
                case "is not":
                    if ((lce != null && lce.val_ is null) || (rce != null && rce.val_ is null))
                    {
                        if (IsRelOp())
                        {
                            // needs to be TRUE or FALSE
                            if ((op_ == "is") || (op_ == "is not") || (lce != null && TypeBase.IsNumberType(l.type_)) || (rce != null && TypeBase.IsNumberType(r.type_)))
                                return SimplifyRelop();
                        }

                        // NULL simplification: if operator is not relational, X op NULL is NULL
                        if (lce != null && lce.val_ is null)
                            return lce;

                        if (rce != null && rce.IsNull())
                            return rce;
                    }

                    if (lce != null && rce != null)
                    {
                        // Simplify Constants: children are not non null constants, evaluate them.
                        Value val = Exec(null, null);
                        return ConstExpr.MakeConst(val, type_, outputName_);
                    }

                    if (lce != null && rce == null && isPlainSwappableConstOp())
                        SwapSide();

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
                        if (op_ == "*" && le.rchild_() is ConstExpr lrc && (le.op_ == "+" || le.op_ == "-"))
                        {
                            /*
                             * case of (a + const1) * const2  => (a * const2) + (const1 * const2))
                             * make a newe left node to house (a * const2)
                             *
                             *                          *                    +
                             *                         / \                  / \
                             *                        /   \                /   \ 
                             *                       +     c2     =>      *   c1 * c2
                             *                      / \                  / \
                             *                     /   \                /   \
                             *                    X     c1             X     c2
                             *
                             */

                            /* make a const expr node to evaluate const1 * const 2 */
                            Expr tmpexp = Clone();
                            tmpexp.children_[0] = lrc;  // right of left is const
                            tmpexp.children_[1] = r;    // our right is const
                            tmpexp.type_ = r.type_;

                            Value val;
                            tmpexp.TryEvalConst(out val);

                            // set c2 as the value of right child of our left
                            lrc.val_ = rce.val_;

                            // val is c1 * c2, set it as the value of our right child
                            rce.val_ = val;

                            /* swap the operators */
                            string op = op_;
                            op_ = le.op_;
                            le.op_ = op;
                        }
                        /* we can't do any thing about + at the top and * as left child. */
                    }

                    return this;
            }

            return this;
        }

        internal ColumnType TypeFromOperator(string op, ColumnType inType)
        {
            switch (op)
            {
                case ">":
                case ">=":
                case "<":
                case "<=":
                case "=":
                case "<>":
                case "!=":
                case "is":
                case "is not":
                    return new BoolType();
            }

            return inType;
        }

        // Arthmentic Simplification:
        /*
        * +/- zero to a column, or multiply/divide a column by 1
        */
        internal bool IsArithIdentity(ConstExpr lce, ConstExpr rce)
        {
            ConstExpr ve = lce != null ? lce : rce;
            ColExpr ce = (children_[0] is ColExpr) ? (ColExpr)children_[0] : (children_[1] is ColExpr ? (ColExpr)children_[1] : null);

            if (ce == null)
                return false;

            if (!(TypeBase.IsNumberType(ve.type_) && TypeBase.IsNumberType(ce.type_)))
                return false;

            if ((op_ == "+" || op_ == "-") && ve.IsZero())
                return true;

            if ((op_ == "*" || op_ == "/") && ve.IsOne())
                return true;

            return false;
        }

        internal Expr SimplifyArithmetic(ConstExpr lve, ConstExpr rve)
        {
            // we know we have a BinExpr with numeric children
            ConstExpr ve = lve != null ? lve : rve;
            ColExpr ce = (children_[0] is ColExpr) ? (ColExpr)children_[0] : (children_[1] is ColExpr ? (ColExpr)children_[1] : null);

            if (ce is null || ve is null)
                return this;

            // Col + 0, or Col - 0 => return col
            if ((op_ == "+" || op_ == "-") && ve.IsZero())
                return ce;

            if ((op_ == "*" || op_ == "/") && ve.IsOne())
                return ce;

            return this;
        }

        internal Expr SimplifyRelop()
        {
            Expr l = lchild_();
            Expr r = rchild_();
            ConstExpr lce = l is ConstExpr ? (ConstExpr)l : null;
            ConstExpr rce = r is ConstExpr ? (ConstExpr)r : null;

            if (op_ == "is" || op_ == "is not")
            {
                if ((lce == null && rce != null) || (lce != null && rce == null))
                    return this;

                if (((lce == null && rce == null)) || ((lce != null && lce.IsNull()) && (rce != null && rce.IsNull())))
                {
                    if (op_ == "is" || op_ == "is not")
                    {
                        string val = op_ == "is" ? "true" : "false";
                        return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                    }
                }

                if (((lce is null && rce is null)) || ((lce != null && !lce.IsNull()) && (rce != null && !rce.IsNull())))
                {
                    if (op_ == "is" || op_ == "is not")
                    {
                        string val = op_ == "is" ? "false" : "true";
                        return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                    }
                }

                if (lce != null && !lce.IsNull() && rce != null && rce.IsNull())
                {
                    if (op_ == "is" || op_ == "is not")
                    {
                        string val = op_ == "is" ? "false" : "true";
                        return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                    }
                }

                if (((lce != null && lce.IsNull()) || (rce != null && rce.IsNull())))
                {
                    if (op_ == "is" || op_ == "is not")
                    {
                        string val = op_ == "is" ? "true" : "false";
                        return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                    }
                }
            }

            if (lce == null && rce == null)
                return this;

            if (!(lce is null || rce is null))
            {
                if (lce.IsTrue() && rce.IsTrue())
                    return lce;
                else
                    return lce.IsTrue() ? lce : rce;
            }
            else if ((!(lce is null) && (lce.IsNull() || lce.IsFalse())) || (!(rce is null) && (rce.IsNull() || rce.IsFalse())))
                return ConstExpr.MakeConst("false", new BoolType(), outputName_);

            /*
             * X + C1 = C2      => X = C2 - C1
             * X + C1 > C2      => X > C2 - C1
             * X + C1 >= C2     => X >= C2 - C1
             *
             * X - C1 = C2      => X = C2 + C1
             * X - C1 >= C2     => X >= C2 + C1
             * etc.
             *                                        (relop)
             *                                        /      \
             *                                      arith    const2
             *                                     /    \
             *                                    x     const1
             */
            // Only if type of x is the same as that of const2, otherwise it is pretty involved
            // rewrite the operation and preserving the original behavior of the comparision.
            if (!(rce is null) && l is BinExpr lbe && lbe.IsArithmeticOp() && lbe.rchild_() is ConstExpr lrc && TypeBase.SameArithType(lbe.type_, rce.type_))
            {
                if (lbe.op_ == "+" || lbe.op_ == "-")
                {
                    string nop = (lbe.op_ == "+") ? "-" : "+";
                    BinExpr newe = new BinExpr(rce, lrc, nop);
                    newe.type_ = ColumnType.CoerseType(nop, lrc, rce);
                    newe.bounded_ = true;

                    Value val = newe.Exec(null, null);
                    ConstExpr newc = ConstExpr.MakeConst(val, newe.type_);
                    children_[0] = lbe.lchild_();
                    children_[1] = newc;
                }
            }

            return this;
        }

        /*
         * Apply all possible logical simplification rules.
         * Assumptions:
         *  1) children have been normalized
         *  2) NULL simplification and const move rules have been applied.
         */
        internal Expr SimplifyLogic()
        {
            Expr l = lchild_();
            Expr r = rchild_();

            // simplify X relop CONST
            if (l is ConstExpr lce && r is ConstExpr rce)
            {
                if ((lce.IsTrue() && rce.IsTrue()) || (lce.IsFalse() && rce.IsFalse()))
                    return lce;

                return lce.IsFalse() ? lce : rce;
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

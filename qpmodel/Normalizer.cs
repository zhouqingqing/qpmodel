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
                LogicAndExpr newe = new LogicAndExpr(l, r)
                {
                    bounded_ = true
                };
                newe.FixNewExprTableRefs(l);
                if (r.tableRefs_.Count > 0 && !newe.TableRefsContainedBy(r.tableRefs_))
                    newe.FixNewExprTableRefs(r);
                return newe;
            }
            else
            {
                LogicOrExpr newe = new LogicOrExpr(l, r);
                newe.bounded_ = true;  // Must set before TableRefsContainedBy call
                newe.FixNewExprTableRefs(l);
                if (r.tableRefs_.Count > 0 && !newe.TableRefsContainedBy(r.tableRefs_))
                    newe.FixNewExprTableRefs(r);

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

            if (x.AnyArgNull() && propagateNull_)
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

                // NOT (X relop Y) => X negated_relop Y
                if (child_() is BinExpr be && be.IsRelOp())
                {
                    string negated = be.op_ switch
                    {
                        "=" => "<>",
                        "<>" => "=",
                        "!=" => "=",
                        "<" => ">=",
                        ">=" => "<",
                        ">" => "<=",
                        "<=" => ">",
                        "is" => "is not",
                        "is not" => "is",
                        "like" => "not like",
                        "not like" => "like",
                        _ => null
                    };
                    if (negated != null)
                    {
                        be.op_ = negated;
                        return be;
                    }
                }

                // NOT (x IN list) => x NOT IN list; NOT (x NOT IN list) => x IN list
                if (child_() is InListExpr il)
                {
                    il.hasNot_ = !il.hasNot_;
                    return il;
                }
                if (child_() is InSubqueryExpr isq)
                {
                    isq.hasNot_ = !isq.hasNot_;
                    return isq;
                }
                // NOT EXISTS => EXISTS with flipped flag, and vice versa
                if (child_() is ExistSubqueryExpr esq)
                {
                    esq.hasNot_ = !esq.hasNot_;
                    return esq;
                }

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
                    // logical expressions. Normalize them so NOT relop
                    // push-in can happen on the new NOT nodes.
                    nlu.children_[0] = ole;
                    nru.children_[0] = ore;

                    return makeAnyLogicalExpr(nlu.Normalize(), nru.Normalize(), nop);
                }
            }

            return x;
        }
    }

    public partial class InListExpr
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            // If all parts are constant, evaluate at compile time
            if (x is InListExpr ile && ile.AllArgsConst())
            {
                Value val = ile.Exec(null, null);
                if (val is null)
                    return ConstExpr.MakeConst("null", new AnyType(), outputName_);
                return ConstExpr.MakeConst((bool)val ? "true" : "false", new BoolType(), outputName_);
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
            (op_ == "+" || op_ == "-" || op_ == "*" || op_ == "/" || op_ == "%");
        internal bool IsRelOp() =>
            (op_ == "=" || op_ == "<=" || op_ == "<" || op_ == ">=" || op_ == ">" || op_ == "<>" || op_ == "!=" || op_ == "is" || op_ == "is not" || op_ == "like" || op_ == "not like");

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
                case "%":
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
                case "like":
                case "not like":
                    if ((lce != null && lce.val_ is null) || (rce != null && rce.val_ is null))
                    {
                        if (IsRelOp())
                        {
                            // needs to be TRUE or FALSE
                            if ((op_ == "is") || (op_ == "is not") || (lce != null && TypeBase.IsNumberType(l.type_)) || (rce != null && TypeBase.IsNumberType(r.type_)))
                                return SimplifyRelop();
                        }

                        // AND/OR with NULL follow SQL three-valued logic
                        if (op_ == " and " || op_ == " or ")
                        {
                            if (lce != null && rce != null)
                            {
                                // Both constants: evaluate using three-valued logic
                                // false AND null = false, true OR null = true, etc.
                                Value val = Exec(null, null);
                                if (val is null)
                                    return ConstExpr.MakeConst("null", new AnyType(), outputName_);
                                return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                            }
                            // One side is non-const: can't simplify at compile time
                            break;
                        }

                        // NULL simplification: if operator is not relational, X op NULL is NULL
                        if (lce != null && lce.val_ is null)
                            return lce;

                        if (rce != null && rce.IsNull())
                            return rce;
                    }

                    // Division/modulo by zero: check before constant folding to avoid
                    // runtime DivideByZeroException in Exec() (e.g. SELECT 1/0)
                    if ((op_ == "/" || op_ == "%") && rce != null && rce.IsZero())
                        throw new logic.SemanticAnalyzeException("division by zero");

                    if (lce != null && rce != null)
                    {
                        // Simplify Constants: children are not non null constants, evaluate them.
                        Value val = Exec(null, null);
                        return ConstExpr.MakeConst(val, type_, outputName_);
                    }

                    if (lce != null && rce == null && isPlainSwappableConstOp())
                        SwapSide();

                    // Division by zero: expr / 0 => compile-time error
                    if (op_ == "/" && rce != null && rce.IsZero())
                        throw new logic.SemanticAnalyzeException("division by zero");

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
            // After SwapSide, lce/rce may not match children_ positions.
            // Use the actual current children to find the non-const operand.
            Expr other = children_[0] is ConstExpr ? children_[1] : children_[0];

            if (other == null || other is ConstExpr)
                return false;

            if (!(TypeBase.IsNumberType(ve.type_) && TypeBase.IsNumberType(other.type_)))
                return false;

            // expr + 0 => expr, 0 + expr => expr
            if (op_ == "+" && ve.IsZero())
                return true;

            // expr - 0 => expr (but NOT 0 - expr)
            if (op_ == "-" && ve.IsZero() && rce != null)
                return true;

            // NOTE: expr * 0 => 0 is NOT safe because NULL * 0 = NULL in SQL

            // expr * 1 => expr, 1 * expr => expr
            if (op_ == "*" && ve.IsOne())
                return true;

            // expr / 1 => expr (but NOT 1 / expr)
            if (op_ == "/" && ve.IsOne() && rce != null)
                return true;

            return false;
        }

        internal Expr SimplifyArithmetic(ConstExpr lve, ConstExpr rve)
        {
            // we know we have a BinExpr with numeric children
            ConstExpr ve = lve != null ? lve : rve;
            Expr other = children_[0] is ConstExpr ? children_[1] : children_[0];

            if (other is null || ve is null)
                return this;

            // expr + 0 => expr, 0 + expr => expr
            if (op_ == "+" && ve.IsZero())
                return other;

            // expr - 0 => expr (but NOT 0 - expr)
            if (op_ == "-" && ve.IsZero() && rve != null)
                return other;

            // expr * 0 => 0, 0 * expr => 0
            // only safe when both sides are non-null constants (NULL * 0 must stay NULL)
            if (op_ == "*" && ve.IsZero() && lve != null && rve != null
                && !lve.IsNull() && !rve.IsNull())
                return ConstExpr.MakeConst(0, type_, outputName_);

            // expr * 1 => expr, 1 * expr => expr
            if (op_ == "*" && ve.IsOne())
                return other;

            // expr / 1 => expr (but NOT 1 / expr)
            if (op_ == "/" && ve.IsOne() && rve != null)
                return other;

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
                // IS/IS NOT: one side is typically NULL.
                // If one side is non-const, we can't simplify (e.g., expr IS NULL).
                if ((lce == null) != (rce == null))
                    return this;

                // Both sides are constants: determine result based on nullity.
                if (lce != null && rce != null)
                {
                    bool bothNull = lce.IsNull() && rce.IsNull();
                    bool eitherNull = lce.IsNull() || rce.IsNull();
                    // IS: true if both null or both non-null with same value
                    // IS NOT: opposite
                    bool isMatch = bothNull || (!eitherNull && lce.val_.Equals(rce.val_));
                    string val = (op_ == "is") == isMatch ? "true" : "false";
                    return ConstExpr.MakeConst(val, new BoolType(), outputName_);
                }
                // Both non-const: fall through to tautology check below.
            }

            if (lce == null && rce == null)
            {
                // X = X => TRUE, X <> X => FALSE, etc.
                // NOTE: strictly, if X can be NULL then X=X evaluates to NULL
                // (not TRUE) per SQL three-valued logic. A fully correct guard
                // would require nullability tracking on columns, which we don't
                // have yet. For now we apply the optimization unconditionally,
                // matching common SQL engine behavior for non-nullable columns.
                // TODO: guard with nullability check once column NOT NULL
                // constraints are tracked in the catalog.
                if (l.Equals(r))
                {
                    bool tautology = (op_ == "=" || op_ == "<=" || op_ == ">=");
                    bool contradiction = (op_ == "<>" || op_ == "!=" || op_ == "<" || op_ == ">");
                    if (tautology)
                        return ConstExpr.MakeConst("true", new BoolType(), outputName_);
                    if (contradiction)
                        return ConstExpr.MakeConst("false", new BoolType(), outputName_);
                }

                // X + C1 relop X + C2  =>  C1 relop C2  (cancel common variable part)
                // X - C1 relop X - C2  =>  C2 relop C1  (subtraction reverses order)
                if (l is BinExpr lba && r is BinExpr rba
                    && (lba.op_ == "+" || lba.op_ == "-")
                    && lba.op_ == rba.op_
                    && lba.rchild_() is ConstExpr lbc
                    && rba.rchild_() is ConstExpr rbc
                    && lba.lchild_().Equals(rba.lchild_()))
                {
                    if (lba.op_ == "+")
                    {
                        children_[0] = lbc;
                        children_[1] = rbc;
                    }
                    else
                    {
                        // For subtraction: (X-C1) < (X-C2) iff C2 < C1
                        children_[0] = rbc;
                        children_[1] = lbc;
                    }
                    return SimplifyRelop();
                }

                return this;
            }

            // Both sides are constants: evaluate the comparison at compile time
            if (lce != null && rce != null)
            {
                // Any side is NULL => comparison yields NULL (SQL three-valued logic)
                if (lce.IsNull() || rce.IsNull())
                    return ConstExpr.MakeConst("null", new AnyType(), outputName_);

                // Evaluate the constant comparison
                Value val = Exec(null, null);
                return ConstExpr.MakeConst(val, new BoolType(), outputName_);
            }
            // One side is NULL constant => comparison yields NULL (SQL three-valued logic)
            else if ((lce != null && lce.IsNull()) || (rce != null && rce.IsNull()))
                return ConstExpr.MakeConst("null", new AnyType(), outputName_);

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
                    BinExpr newe = new BinExpr(rce, lrc, nop)
                    {
                        type_ = ColumnType.CoerseType(nop, lrc, rce),
                        bounded_ = true
                    };

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

            // one side is constant: X AND FALSE => FALSE, X AND TRUE => X,
            // X OR TRUE => TRUE, X OR FALSE => X
            // null constants cannot be simplified away (null AND X != X)
            bool isAnd = op_ == " and ";
            if (l is ConstExpr lc && !lc.IsNull())
            {
                if (isAnd)
                    return lc.IsFalse() ? lc : r;
                else
                    return lc.IsTrue() ? lc : r;
            }
            if (r is ConstExpr rc && !rc.IsNull())
            {
                if (isAnd)
                    return rc.IsFalse() ? rc : l;
                else
                    return rc.IsTrue() ? rc : l;
            }

            return this;
        }
    }

    public partial class CaseExpr
    {
        public override Expr Normalize()
        {
            Expr x = base.Normalize();
            if (!(x is CaseExpr ce))
                return x;

            // Searched CASE (no eval expression): fold constant WHEN conditions
            if (ce.eval_() == null)
            {
                var whens = ce.when_();
                var thens = ce.then_();
                var newWhens = new List<Expr>();
                var newThens = new List<Expr>();

                for (int i = 0; i < whens.Count; i++)
                {
                    if (whens[i] is ConstExpr wc)
                    {
                        // WHEN TRUE => return the THEN expression directly
                        if (wc.IsTrue())
                        {
                            // This WHEN is always true: if it's the only remaining
                            // branch and there are no prior non-const WHENs, just
                            // return the THEN expression
                            if (newWhens.Count == 0)
                                return thens[i];
                            else
                            {
                                // There are prior non-const branches: keep this as
                                // the final branch (acts like an ELSE for remaining)
                                // Rebuild CASE with prior branches + this as ELSE
                                return rebuildCase(null, newWhens, newThens, thens[i]);
                            }
                        }
                        // WHEN FALSE or WHEN NULL => skip this branch entirely
                        if (wc.IsFalse() || wc.IsNull())
                            continue;
                    }
                    // Non-constant WHEN: keep it
                    newWhens.Add(whens[i]);
                    newThens.Add(thens[i]);
                }

                // All WHEN branches were eliminated => return ELSE (or NULL)
                if (newWhens.Count == 0)
                    return ce.else_() ?? ConstExpr.MakeConst("null", new AnyType(), outputName_);

                // Some branches eliminated: rebuild if changed
                if (newWhens.Count < whens.Count)
                    return rebuildCase(null, newWhens, newThens, ce.else_());
            }
            else if (ce.eval_() is ConstExpr evalConst)
            {
                // Simple CASE with NULL eval: NULL never equals anything,
                // so skip all WHEN clauses and return ELSE directly.
                if (evalConst.IsNull())
                    return ce.else_() ?? ConstExpr.MakeConst("null", new AnyType(), outputName_);

                // Simple CASE with constant eval: compare with constant WHENs
                var whens = ce.when_();
                var thens = ce.then_();
                var newWhens = new List<Expr>();
                var newThens = new List<Expr>();

                for (int i = 0; i < whens.Count; i++)
                {
                    if (whens[i] is ConstExpr wc)
                    {
                        if (evalConst.val_ != null && wc.val_ != null && evalConst.val_.Equals(wc.val_))
                        {
                            // Match found: return this THEN if no prior non-const branches
                            if (newWhens.Count == 0)
                                return thens[i];
                            else
                                return rebuildCase(null, newWhens, newThens, thens[i]);
                        }
                        // No match with this constant WHEN: skip
                        continue;
                    }
                    newWhens.Add(whens[i]);
                    newThens.Add(thens[i]);
                }

                if (newWhens.Count == 0)
                    return ce.else_() ?? ConstExpr.MakeConst("null", new AnyType(), outputName_);

                if (newWhens.Count < whens.Count)
                    return rebuildCase(ce.eval_(), newWhens, newThens, ce.else_());
            }

            return ce;
        }

        private Expr rebuildCase(Expr eval, List<Expr> whens, List<Expr> thens, Expr elsee)
        {
            var result = new CaseExpr(eval, whens, thens, elsee);
            result.type_ = type_;
            result.outputName_ = outputName_;
            result.bounded_ = bounded_;
            result.tableRefs_ = tableRefs_;
            return result;
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

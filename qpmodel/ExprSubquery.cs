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
using System.Linq;
using System.Diagnostics;
using Value = System.Object;

using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;

namespace qpmodel.expr
{
    public abstract class SubqueryExpr : Expr
    {
        internal SelectStmt query_;
        internal int subqueryid_;    // bounded

        // runtime optimization for non-correlated subquery
        internal bool isCacheable_ = false;
        internal bool cachedValSet_ = false;
        internal Value cachedVal_;

        public SubqueryExpr(SelectStmt query)
        {
            query_ = query;
        }

        // don't print the subquery here, it shall be printed by up caller layer for pretty format
        public override string ToString() => $@"@{subqueryid_}";

        protected void bindQuery(BindContext context)
        {
            // subquery id is global, so accumulating at top
            subqueryid_ = ++context.TopContext().globalSubqCounter_;

            // query will use a new query context inside
            var mycontext = query_.Bind(context);
            Debug.Assert(query_.parent_ == mycontext.parent_?.stmt_);

            // verify column count after bound because SelStar expansion
            if (!(this is ScalarSubqueryExpr))
            {
                type_ = new BoolType();
            }
            else
            {
                if (query_.selection_.Count != 1)
                    throw new SemanticAnalyzeException("subquery must return only one column");
                type_ = query_.selection_[0].type_;
            }
        }

        public bool Max1Row()
        {
            var plan = query_.logicPlan_;

            // aggregation without groupby
            if (plan is LogicAgg && query_.groupby_ is null)
                return true;

            // limit 1 query
            List<LogicLimit> limits = new List<LogicLimit>();
            if (plan.FindNodeTypeMatch<LogicLimit>(limits) > 0)
            {
                if (limits[0].limit_ <= 1)
                    return true;
            }
            return false;
        }

        public bool IsCorrelated()
        {
            Debug.Assert(query_.bounded_);
            return query_.isCorrelated_;
        }

        // similar to IsCorrelated() but also consider children. If None is correlated
        // or the correlation does not go outside this expr range, then we can cache
        // the result and reuse it without repeatly execute it.
        //
        // Eg. ... a where a1 in ( ... b where exists (select * from c where c1>=a1))
        // InSubquery ... b is not correlated but its child is correlated to outside 
        // table a, which makes it not cacheable.
        //
        bool SetIsCacheable()
        {
            if (IsCorrelated())
                isCacheable_ = false;
            else
            {
                // collect all subquries within this query, they are ok to correlate
                var queriesOkToRef = query_.InclusiveAllSubquries();

                // if the subquery reference anything beyond the ok-range, we can't cache
                bool childCorrelated = false;
                query_.subQueries_.ForEach(x =>
                {
                    if (x.query_.isCorrelated_)
                    {
                        if (!queriesOkToRef.ContainsList(x.query_.correlatedWhich_))
                            childCorrelated = true;
                    }
                });

                isCacheable_ = !childCorrelated;
            }

            return isCacheable_;
        }

        public override void Bind(BindContext context)
        {
            bindQuery(context);
            SetIsCacheable();
        }

        public virtual Value ExecNonDistributed(ExecContext context, Row input) => null;

        // for distributed execution, we don't copy logic plan which means this expression
        // is also not copied thus multiple threads may racing updating cacheVal_. A simple
        // way is to Lock the code section to prevent it.
        //
        Value ExecDistributed(ExecContext context, Row input)
        {
            lock (this)
            {
                return ExecNonDistributed(context, input);
            }
        }

        public override Value Exec(ExecContext context, Row input)
        {
            if (context is DistributedContext)
                return ExecDistributed(context, input);
            return ExecNonDistributed(context, input);
        }

        public override int GetHashCode() => subqueryid_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is SubqueryExpr os)
                return os.subqueryid_ == subqueryid_;
            return false;
        }
    }

    public class ExistSubqueryExpr : SubqueryExpr
    {
        internal bool hasNot_;

        public ExistSubqueryExpr(bool hasNot, SelectStmt query) : base(query) { hasNot_ = hasNot; }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            type_ = new BoolType();
            markBounded();
        }

        public override Value ExecNonDistributed(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (isCacheable_ && cachedValSet_)
                return cachedVal_;

            Row r = null;
            query_.physicPlan_.Exec(l =>
            {
                // exists check can immediately return after receiving a row
                r = l;
            });

            bool exists = r != null;
            cachedVal_ = hasNot_ ? !exists : exists;
            cachedValSet_ = true;
            return cachedVal_;
        }
    }

    public class ScalarSubqueryExpr : SubqueryExpr
    {
        public ScalarSubqueryExpr(SelectStmt query) : base(query) { }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = query_.selection_[0].type_;
            markBounded();
        }

        public override Value ExecNonDistributed(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (isCacheable_ && cachedValSet_)
                return cachedVal_;

            context.option_.PushCodeGenDisable();
            Row r = null;
            query_.physicPlan_.Exec(l =>
            {
                // exists check can immediately return after receiving a row
                var prevr = r; r = l;
                if (prevr != null)
                    throw new SemanticExecutionException("subquery more than one row returned");
            });
            context.option_.PopCodeGen();

            cachedVal_ = (r != null) ? r[0] : null;
            cachedValSet_ = true;
            return cachedVal_;
        }
    }

    public class InSubqueryExpr : SubqueryExpr
    {
        // children_[0] is the expr of in-query
        internal Expr expr_() => children_[0];

        public override string ToString() => $"{expr_()} in @{subqueryid_}";
        public InSubqueryExpr(Expr expr, SelectStmt query) : base(query) { children_.Add(expr); }

        public override void Bind(BindContext context)
        {
            base.Bind(context);
            expr_().Bind(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = new BoolType();
            markBounded();
        }

        public override Value ExecNonDistributed(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            Value expr = expr_().Exec(context, input);

            // for distributed execution, we don't copy logic plan which means this expression
            // is also not copied thus multiple threads may racing updating cacheVal_. Lock the
            // code section to prevent it. This also redu
            if (isCacheable_ && cachedValSet_)
                return (cachedVal_ as HashSet<Value>).Contains(expr);

            var set = new HashSet<Value>();
            query_.physicPlan_.Exec(l =>
            {
                // it may have hidden columns but that's after [0]
                set.Add(l[0]);
            });

            cachedVal_ = set;
            cachedValSet_ = true;
            return set.Contains(expr); ;
        }
    }

    // In List can be varaibles:
    //      select* from a where a1 in (1, 2, a2);
    //
    public class InListExpr : Expr
    {
        internal Expr expr_() => children_[0];
        internal List<Expr> inlist_() => children_.GetRange(1, children_.Count - 1);
        public InListExpr(Expr expr, List<Expr> inlist)
        {
            children_.Add(expr); children_.AddRange(inlist);
            type_ = new BoolType();
            Debug.Assert(Clone().Equals(this));
        }

        public override int GetHashCode()
        {
            return expr_().GetHashCode() ^ inlist_().ListHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is ExprRef or)
                return Equals(or.expr_());
            else if (obj is InListExpr co)
                return expr_().Equals(co.expr_()) && exprEquals(inlist_(), co.inlist_());
            return false;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            var v = expr_().Exec(context, input);
            if (v is null)
                return null;
            List<Value> inlist = new List<Value>();
            inlist_().ForEach(x => { inlist.Add(x.Exec(context, input)); });
            return inlist.Exists(v.Equals);
        }

        public override string ToString()
        {
            var inlist = inlist_();
            if (inlist_().Count < 5)
                return $"{expr_()} in ({string.Join(",", inlist)})";
            else
            {
                return $"{expr_()} in ({string.Join(",", inlist.GetRange(0, 3))}, ... <Total: {inlist.Count}> )";
            }
        }
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

namespace adb
{
    public abstract class SubqueryExpr : Expr
    {
        internal string subtype_;    // in, exists, scalar

        internal SelectStmt query_;
        internal int subqueryid_;    // bounded

        public SubqueryExpr(SelectStmt query, string subtype)
        {
            query_ = query; subtype_ = subtype;
        }
        // don't print the subquery here, it shall be printed by up caller layer for pretty format
        public override string ToString() => $@"@{subqueryid_}";

        protected void bindQuery(BindContext context)
        {
            // subquery id is global, so accumulating at top
            subqueryid_ = ++BindContext.globalSubqCounter_;

            // query will use a new query context inside
            var mycontext = query_.Bind(context);
            Debug.Assert(query_.parent_ == mycontext.parent_?.stmt_);

            // verify column count after bound because SelStar expansion
            if (subtype_ != "scalar")
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
        public ExistSubqueryExpr(SelectStmt query) : base(query, "exist") { }

        public override void Bind(BindContext context)
        {
            bindQuery(context);
            type_ = new BoolType();
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            Row r = null;
            query_.physicPlan_.Exec(context, l =>
            {
                // exists check can immediately return after receiving a row
                r = l;
                return null;
            });

            return r != null;
        }
    }

    public class ScalarSubqueryExpr : SubqueryExpr
    {
        public ScalarSubqueryExpr(SelectStmt query) : base(query, "scalar") { }

        public override void Bind(BindContext context)
        {
            bindQuery(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = query_.selection_[0].type_;
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            Row r = null;
            query_.physicPlan_.Exec(context, l =>
            {
                // exists check can immediately return after receiving a row
                var prevr = r; r = l;
                if (prevr != null)
                    throw new SemanticExecutionException("subquery more than one row returned");
                return null;
            });

            return r?.values_[0] ?? int.MaxValue;
        }
    }

    public class InSubqueryExpr : SubqueryExpr
    {
        // children_[0] is the expr of in-query
        internal Expr expr_() => children_[0];

        public override string ToString() => $"{expr_()} in @{subqueryid_}";
        public InSubqueryExpr(Expr expr, SelectStmt query) : base(query, "in") { children_.Add(expr); }

        public override void Bind(BindContext context)
        {
            expr_().Bind(context);
            bindQuery(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = new BoolType();
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            var set = new HashSet<Value>();
            query_.physicPlan_.Exec(context, l =>
            {
                // it may have hidden columns but that's after [0]
                set.Add(l.values_[0]);
                return null;
            });

            Value expr = expr_().Exec(context, input);
            return set.Contains(expr);
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
            return expr_().GetHashCode() ^ Utils.ListHashCode(inlist_());
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
            List<Value> inlist = new List<Value>();
            inlist_().ForEach(x => { inlist.Add(x.Exec(context, input)); });
            return inlist.Exists(v.Equals);
        }

        public override string ToString() => $"{expr_()} in ({string.Join(",", inlist_())})";
    }

}

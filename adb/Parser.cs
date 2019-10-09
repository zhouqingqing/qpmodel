using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using System.Diagnostics;

namespace adb
{
    // antlr requires user defined exception with this name
    public class RuntimeException : System.Exception
    {
        public RuntimeException(string msg) {}
    }

    public class SemanticAnalyzeException: System.Exception
    {
        public SemanticAnalyzeException(string ms) { }
    }

    public class SemanticExecutionException : System.Exception
    {
        public SemanticExecutionException(string ms) { }
    }

    public class RawParser
    {
        static public SQLStatement ParseSQLStatement(string sql)
        {
            AntlrInputStream inputStream = new AntlrInputStream(sql);
            SQLiteLexer sqlLexer = new SQLiteLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(sqlLexer);
            SQLiteParser sqlParser = new SQLiteParser(commonTokenStream);
            SQLiteVisitor visitor = new SQLiteVisitor();

            SQLiteParser.Sql_stmtContext stmtCxt = sqlParser.sql_stmt();
            return visitor.VisitSql_stmt(stmtCxt) as SQLStatement;
        }
    }

    public abstract class TableRef
    {
        internal string alias_;
        readonly internal List<ColExpr> outerrefs_ = new List<ColExpr>();

        public override string ToString() => alias_;
        public Expr LocateColumn(string colName)
        {
            var list = AllColumnsRefs();
            foreach (var v in list) {
                if (v.alias_?.Equals(colName)??false)
                    return v;
            }

            return null;
        }

        public List<Expr> AddOuterRefsToOutput(List<Expr> output) {
            outerrefs_.ForEach(x => {
                if (!output.Contains(x))
                {
                    var clone = x.Clone() as ColExpr;
                    clone.isVisible_ = false;
                    clone.isOuterRef_ = false;
                    output.Add(clone);
                }
            });

            return output;
        }

        public abstract List<Expr> AllColumnsRefs();
    }

    // FROM <table>
    public class BaseTableRef : TableRef
    {
        public string relname_;

        public BaseTableRef([NotNull] string name, string alias)
        {
            relname_ = name;
            alias_ = alias ?? relname_;
        }

        public override string ToString() 
            => (relname_.Equals(alias_)) ? $"{relname_}" : $"{relname_} as {alias_}";

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> l = new List<Expr>();
            var columns = Catalog.systable_.Table(relname_);
            foreach (var c in columns) {
                ColumnDef coldef = c.Value;
                l.Add(new ColExpr(null, relname_, coldef.name_));
            }

            return l;
        }
    }

    // FROM <subquery>
    public class FromQueryRef : TableRef
    {
        public SelectStmt query_;

        public FromQueryRef(SelectStmt query, [NotNull] string alias)
        {
            query_ = query;
            alias_ = alias;
        }

        public override List<Expr> AllColumnsRefs() => query_.selection_;
    }

    // antlr visitor pattern parser
    class SQLiteVisitor : SQLiteBaseVisitor<object>
    {
        public override object VisitLiteral_value([NotNull] SQLiteParser.Literal_valueContext context)
            => new LiteralExpr(context);
        public override object VisitBrackexpr([NotNull] SQLiteParser.BrackexprContext context) 
            => Visit(context.expr());
        public override object VisitArithtimesexpr([NotNull] SQLiteParser.ArithtimesexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitArithplusexpr([NotNull] SQLiteParser.ArithplusexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitFuncExpr([NotNull] SQLiteParser.FuncExprContext context)
            => new FuncExpr(context.function_name().GetText());
        public override object VisitColExpr([NotNull] SQLiteParser.ColExprContext context)
            => new ColExpr(context.database_name()?.GetText(),
                context.table_name()?.GetText(),
                context.column_name()?.GetText());
        public override object VisitArithcompexpr([NotNull] SQLiteParser.ArithcompexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitLogicAndExpr([NotNull] SQLiteParser.LogicAndExprContext context)
            => new LogicAndExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        public override object VisitArithequalexpr([NotNull] SQLiteParser.ArithequalexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitSubqueryExpr([NotNull] SQLiteParser.SubqueryExprContext context)
            => new SubqueryExpr(Visit(context.select_stmt()) as SelectStmt);
        public override object VisitTable_or_subquery([NotNull] SQLiteParser.Table_or_subqueryContext context)
            => Visit(context);
        public override object VisitFromSimpleTable([NotNull] SQLiteParser.FromSimpleTableContext context)
            => new BaseTableRef(context.table_name().GetText(), context.table_alias()?.GetText());
        public override object VisitOrdering_term([NotNull] SQLiteParser.Ordering_termContext context)
            => new OrderTerm(Visit(context.expr()) as Expr, (context.K_DESC() is null) ? false : true);
        public override object VisitFromJoinTable([NotNull] SQLiteParser.FromJoinTableContext context)
            => throw new NotImplementedException();

        public override object VisitResult_column([NotNull] SQLiteParser.Result_columnContext context)
        {
            Expr r;
            string alias = context.column_alias()?.GetText();
            if (context.expr() != null)
            {
                r = Visit(context.expr()) as Expr;
                // ColExpr assume alias same as column name if not given
                if (r is ColExpr cr && alias is null)
                    alias = cr.colName_;
            }
            else
            {
                Debug.Assert(alias is null);
                r = new SelStar(context.table_name()?.GetText());
            }

            r.alias_ = alias;
            return r;
        }

        public override object VisitFromSelectStmt([NotNull] SQLiteParser.FromSelectStmtContext context)
        {
            var query = Visit(context.select_stmt()) as SelectStmt;
            if (context.table_alias() is null)
                throw new Exception("subquery in FROM shall have an alias");
            return new FromQueryRef(query, context.table_alias().GetText());
        }

        public override object VisitSelect_core([NotNull] SQLiteParser.Select_coreContext context)
        {
            // -- parser stage
            var resultCols = new List<Expr>();
            foreach (var r in context.result_column())
            {
                var col = VisitResult_column(r) as Expr;
                resultCols.Add(col);
            }

            var resultRels = new List<TableRef>();
            foreach (var r in context.table_or_subquery())
            {
                var tab = VisitTable_or_subquery(r) as TableRef;
                resultRels.Add(tab);
            }

            int whichexpr = 0;
            Expr where = null;
            if (context.K_WHERE() != null)
            {
                where = Visit(context.expr(whichexpr++)) as Expr;
            }
             
            // SQLite grammer mixed group by and having expressions, so reserve
            // the last expr for having clause
            //
            List<Expr> groupby = null;
            int havingexpr = (context.K_HAVING() is null) ? 0 : 1;
            if (context.K_GROUP() != null)
            {
                groupby = new List<Expr>();
                while (whichexpr < context.expr().Length - havingexpr)
                {
                    var e = context.expr(whichexpr++);
                    groupby.Add(Visit(e) as Expr);
                }
            }

            Expr having = null;
            if (havingexpr == 1) {
                having = Visit(context.expr(whichexpr)) as Expr;
            }

            // -- binding stage
            return new SelectStmt(resultCols, resultRels, where, groupby, having, null, null, null, context.GetText());
        }

        public override object VisitCommon_table_expression([NotNull] SQLiteParser.Common_table_expressionContext context)
        {
            List<string> colNames = null;
            if (context.column_name() != null)
            {
                colNames = new List<string>();
                Array.ForEach(context.column_name(), x => colNames.Add(x.GetText()));
            }
            return new CTExpr(context.table_name().GetText(), colNames, Visit(context.select_stmt()) as SQLStatement);
        }

        public override object VisitSelect_stmt([NotNull] SQLiteParser.Select_stmtContext context)
        {
            List<CTExpr> ctes = null;
            if (context.K_WITH() != null) {
                ctes = new List<CTExpr>();
                Array.ForEach(context.common_table_expression(), x => ctes.Add(VisitCommon_table_expression(x) as CTExpr));
            }

            var setqs = new List<SelectStmt>();
            Array.ForEach(context.select_core(), x => setqs.Add(VisitSelect_core(x) as SelectStmt));

            List<OrderTerm> orders = null;
            if (context.K_ORDER() != null)
            {
                Debug.Assert(context.K_BY() != null);
                orders = new List<OrderTerm>();
                Array.ForEach(context.ordering_term(), x=> orders.Add(VisitOrdering_term(x) as OrderTerm));
            }

            // cores_[0] is also expanded to main body
            return new SelectStmt(setqs[0].selection_, setqs[0].from_, setqs[0].where_, setqs[0].groupby_, setqs[0].having_, 
                                    ctes, setqs, orders, context.GetText());
        }

        public override object VisitSql_stmt([NotNull] SQLiteParser.Sql_stmtContext context)
        {
            SQLStatement r = null;
            bool explain = false;

            if (context.K_EXPLAIN() != null)
                explain = true;
            if (context.select_stmt() != null)
                r = Visit(context.select_stmt()) as SQLStatement;

            if (r is null)
                throw new NotImplementedException();
            r.explain_ = explain;
            return r;
        }
    }
}

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
    // Antlr requires user defined exception
    public class RuntimeException : System.Exception
    {
        public RuntimeException(string msg) {}
    }

    public class SemanticAnalyzeException: System.Exception
    {
        public SemanticAnalyzeException(string ms) { }
    }

    public class RawParser
    {
        static public SelectCore ParseSelect(string sql) {
            AntlrInputStream inputStream = new AntlrInputStream(sql);
            SQLiteLexer sqlLexer = new SQLiteLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(sqlLexer);
            SQLiteParser sqlParser = new SQLiteParser(commonTokenStream);
            SQLiteVisitor visitor = new SQLiteVisitor();

            SQLiteParser.Select_coreContext coreCxt = sqlParser.select_core();
            return visitor.VisitSelect_core(coreCxt) as SelectCore;
        }
    }

    public abstract class TableRef
    {
        public string alias_;
        public override string ToString() => alias_;

        public abstract List<Expr> GenerateAllColumnsRefs();
        public Expr LocateColumn(string colName)
        {
            var list = GenerateAllColumnsRefs();
            foreach (var v in list) {
                if (v.alias_?.Equals(colName)??false)
                    return v;
            }

            return null;
        }
    }

    public class BaseTableRef : TableRef
    {
        public string relname_;

        public BaseTableRef([NotNull] string name, string alias = null)
        {
            relname_ = name;
            alias_ = alias ?? relname_;
        }

        public override string ToString()
        {
            if (relname_ == alias_)
                return $"{relname_}";
            else
                return $"{relname_} as {alias_}";
        }

        public override List<Expr> GenerateAllColumnsRefs()
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

    // subquery in FROM clause
    public class SubqueryRef : TableRef
    {
        public SelectCore query_;

        public SubqueryRef(SelectCore query, [NotNull] string alias)
        {
            query_ = query;
            alias_ = alias;
        }

        public override List<Expr> GenerateAllColumnsRefs()
        {
            return query_.Selection();
        }
    }

    class SQLiteVisitor : SQLiteBaseVisitor<object>
    {
        public override object VisitLiteral_value([NotNull] SQLiteParser.Literal_valueContext context)
        {
            return new LiteralExpr(context);
        }

        public override object VisitBrackexpr([NotNull] SQLiteParser.BrackexprContext context)
        {
            return Visit(context.expr());
        }

        public override object VisitArithtimesexpr([NotNull] SQLiteParser.ArithtimesexprContext context)
        {
            Console.WriteLine($@"{context.expr(0).GetText()}{context.op.Type}{context.expr(1).GetText()}");
            return new BinExpr((Expr)Visit(context.expr(0)),
                                    (Expr)Visit(context.expr(1)), context.op.Text);
        }

        public override object VisitArithplusexpr([NotNull] SQLiteParser.ArithplusexprContext context)
        {
            Console.WriteLine($@"{context.expr(0).GetText()}{context.op.Type}{context.expr(1).GetText()}");
            return new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        }

        public override object VisitFuncExpr([NotNull] SQLiteParser.FuncExprContext context)
        {
            return new FuncExpr(context.function_name().GetText());
        }

        // ((database_name '.' )? table_name '.' )? column_name	#colexpr
        public override object VisitColExpr([NotNull] SQLiteParser.ColExprContext context)
        {
            return new ColExpr(context.database_name()?.GetText() ?? null,
                context.table_name()?.GetText() ?? null,
                context.column_name()?.GetText() ?? null);
        }

        public override object VisitArithcompexpr([NotNull] SQLiteParser.ArithcompexprContext context)
        {
            return new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        }

        public override object VisitLogicAndExpr([NotNull] SQLiteParser.LogicAndExprContext context)
        {
            return new LogicAndExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        }

        public override object VisitArithequalexpr([NotNull] SQLiteParser.ArithequalexprContext context)
        {
            return new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        }

        public override object VisitSubqueryExpr([NotNull] SQLiteParser.SubqueryExprContext context)
        {
            var query = Visit(context.select_stmt()) as SelectCore;
            return new SubqueryExpr(query);
        }

        public override object VisitTable_or_subquery([NotNull] SQLiteParser.Table_or_subqueryContext context)
        {
            return Visit(context);
        }

        public override object VisitFromSimpleTable([NotNull] SQLiteParser.FromSimpleTableContext context)
        {
            return new BaseTableRef(context.table_name().GetText());
        }

        public override object VisitFromJoinTable([NotNull] SQLiteParser.FromJoinTableContext context)
        {
            throw new NotImplementedException();
        }

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
            var query = Visit(context.select_stmt()) as SelectCore;
            if (context.table_alias() is null)
                throw new Exception("subquery in FROM shall have an alias");
            return new SubqueryRef(query, context.table_alias().GetText());
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
                var tab = VisitTable_or_subquery(r);
                if (tab is TableRef tabref)
                    resultRels.Add(tabref);
                else
                    throw new Exception();
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
            return new SelectCore(resultCols, resultRels, where, groupby, having, context.GetText());
        }
    }

}

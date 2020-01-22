using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using adb.expr;
using adb.logic;
using adb.index;
using adb.dml;

namespace adb.sqlparser
{
    // antlr requires user defined exception with this name
    public class RuntimeException : Exception
    {
        public RuntimeException(string msg) => Console.WriteLine($"ERROR[Antlr]: {msg }");
    }

    public class RawParser
    {
        // sqlbatch can also be a single sql statement - however, it won't allow you to 
        // directly retrieve phyplan_ etc without go through list
        //
        public static StatementList ParseSqlStatements(string sqlbatch)
        {
            AntlrInputStream inputStream = new AntlrInputStream(sqlbatch);
            SQLiteLexer sqlLexer = new SQLiteLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(sqlLexer);
            SQLiteParser sqlParser = new SQLiteParser(commonTokenStream);
            SQLiteVisitor visitor = new SQLiteVisitor();

            SQLiteParser.Sql_stmt_listContext stmtCxt = sqlParser.sql_stmt_list();
            return visitor.VisitSql_stmt_list(stmtCxt) as StatementList;
        }

        public static SQLStatement ParseSingleSqlStatement(string sql) => ParseSqlStatements(sql).list_[0];
    }

    // antlr visitor pattern parser
    class SQLiteVisitor : SQLiteBaseVisitor<object>
    {
        string GetRawText(ParserRuleContext context)
        {
            int a = context.Start.StartIndex;
            int b = context.Stop.StopIndex;
            Interval interval = new Interval(a, b);
            return context.Start.InputStream.GetText(interval);
        }

        public override object VisitDateStringLiteral([NotNull] SQLiteParser.DateStringLiteralContext context)
            => new LiteralExpr(context.STRING_LITERAL().GetText(), new DateTimeType());
        public override object VisitIntervalLiteral([NotNull] SQLiteParser.IntervalLiteralContext context)
            => new LiteralExpr(context.STRING_LITERAL().GetText(), context.date_unit_single().GetText());
        public override object VisitNumericOrDateLiteral([NotNull] SQLiteParser.NumericOrDateLiteralContext context)
        {
            if (context.date_unit_plural() != null)
                return new LiteralExpr($"'{context.signed_number().GetText()}'", context.date_unit_plural().GetText());
            else
            {
                Debug.Assert(context.signed_number() != null);
                if (context.signed_number().GetText().Contains("."))
                    return new LiteralExpr(context.signed_number().GetText(), new DoubleType());
                else
                    return new LiteralExpr(context.signed_number().GetText(), new IntType());
            }
        }
        public override object VisitCurrentTimeLiteral([NotNull] SQLiteParser.CurrentTimeLiteralContext context)
           =>  throw new NotImplementedException();
        public override object VisitStringLiteral([NotNull] SQLiteParser.StringLiteralContext context)
            => new LiteralExpr(context.GetText(), new CharType(context.GetText().Length));
        public override object VisitNullLiteral([NotNull] SQLiteParser.NullLiteralContext context)
                => new LiteralExpr("null", new AnyType());
        public override object VisitBrackexpr([NotNull] SQLiteParser.BrackexprContext context)
            => Visit(context.expr());
        public override object VisitArithtimesexpr([NotNull] SQLiteParser.ArithtimesexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitArithplusexpr([NotNull] SQLiteParser.ArithplusexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitStringopexpr([NotNull] SQLiteParser.StringopexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);

        public override object VisitBetweenExpr([NotNull] SQLiteParser.BetweenExprContext context)
        {
            var left = new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), ">=");
            var right = new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(2)), "<=");
            return new LogicAndExpr(left, right);
        }
        public override object VisitFuncExpr([NotNull] SQLiteParser.FuncExprContext context)
        {
            List<Expr> args = new List<Expr>();
            foreach (var v in context.expr())
                args.Add(Visit(v) as Expr);
            return FuncExpr.BuildFuncExpr(context.function_name().GetText(), args);
        }
        public override object VisitColExpr([NotNull] SQLiteParser.ColExprContext context)
        {
            var dbname = context.database_name()?.GetText();
            var tabname = context.table_name()?.GetText();
            var colname = context.column_name()?.GetText();
            if (SysColExpr.SysCols_.Contains(colname))
                return new SysColExpr(dbname, tabname, colname, null);
            else
                return new ColExpr(dbname, tabname, colname, null);
        }
        public override object VisitArithcompexpr([NotNull] SQLiteParser.ArithcompexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitLogicAndExpr([NotNull] SQLiteParser.LogicAndExprContext context)
            => new LogicAndExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        public override object VisitLogicOrExpr([NotNull] SQLiteParser.LogicOrExprContext context)
            => new LogicOrExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        public override object VisitBoolEqualexpr([NotNull] SQLiteParser.BoolEqualexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);

        public override object VisitCastExpr([NotNull] SQLiteParser.CastExprContext context)
            => new CastExpr((Expr)Visit(context.expr()), (ColumnType)Visit(context.type_name()));
            public override object VisitSubqueryExpr([NotNull] SQLiteParser.SubqueryExprContext context)
        {
            if (context.K_EXISTS() != null)
                return new ExistSubqueryExpr(Visit(context.select_stmt()) as SelectStmt);
            return new ScalarSubqueryExpr(Visit(context.select_stmt()) as SelectStmt);
        }

        public override object VisitUnaryexpr([NotNull] SQLiteParser.UnaryexprContext context)
        {
            string op = context.unary_operator().GetText();
            var expr = Visit(context.expr()) as Expr;
            if (expr is ExistSubqueryExpr ee)
            {
                // ExistsSubquery needs to get hasNot together for easier processing
                ee.hasNot_ = (context.unary_operator().K_NOT() != null);
                return ee;
            }
            else
                return new UnaryExpr(op, expr);
        }

        public override object VisitInSubqueryExpr([NotNull] SQLiteParser.InSubqueryExprContext context)
        {
            Debug.Assert(context.K_IN() != null);

            SelectStmt select = null;
            List<Expr> inlist = null;
            if (context.select_stmt() != null)
            {
                Debug.Assert(context.expr().Count() == 1);
                select = Visit(context.select_stmt()) as SelectStmt;
                return new InSubqueryExpr(Visit(context.expr(0)) as Expr, select);
            }
            else
            {
                inlist = new List<Expr>();
                foreach (var v in context.expr())
                    inlist.Add(Visit(v) as Expr);
                Expr expr = inlist[0];
                inlist.RemoveAt(0);
                return new InListExpr(expr, inlist);
            }
        }
        public override object VisitCaseExpr([NotNull] SQLiteParser.CaseExprContext context)
        {
            var exprs = new List<Expr>();
            foreach (var v in context.expr())
                exprs.Add(Visit(v) as Expr);
            Expr elsee = null;
            Expr eval = null;
            int start = 0, end = exprs.Count - 1;
            if (context.K_ELSE() != null)
            {
                elsee = exprs[end--];
                if (exprs.Count % 2 == 0)
                {
                    eval = exprs[0];
                    start = 1;
                }
            }
            else
            {
                if (exprs.Count % 2 == 1)
                {
                    eval = exprs[0];
                    start = 1;
                }
            }

            Debug.Assert(end > start && (end - start) % 2 == 1);
            var when = new List<Expr>(); var then = new List<Expr>();
            for (int i = start; i <= end;)
            {
                when.Add(exprs[i++]); then.Add(exprs[i++]);
            }
            return new CaseExpr(eval, when, then, elsee);
        }
        public override object VisitTable_or_subquery([NotNull] SQLiteParser.Table_or_subqueryContext context)
            => Visit(context);

        public override object VisitJoin_clause([NotNull] SQLiteParser.Join_clauseContext context)
        {
            Debug.Assert(!context.IsEmpty);
            var tabrefs = new List<TableRef>();
            foreach (var v in context.table_or_subquery())
                tabrefs.Add(VisitTable_or_subquery(v) as TableRef);
            var joins = new List<string>();
            foreach (var v in context.join_operator())
                joins.Add(v.GetText().ToLower());
            var constraints = new List<Expr>();
            foreach (var v in context.join_constraint())
                constraints.Add(Visit(v.expr()) as Expr);
            return new JoinQueryRef(tabrefs, joins, constraints);
        }
        public override object VisitFromSimpleTable([NotNull] SQLiteParser.FromSimpleTableContext context)
            => new BaseTableRef(context.table_name().GetText(), context.table_alias()?.GetText());
        public override object VisitOrdering_term([NotNull] SQLiteParser.Ordering_termContext context)
            => new OrderTerm(Visit(context.expr()) as Expr, (!(context.K_DESC() is null)));
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

            r.outputName_ = alias;
            return r;
        }

        public override object VisitFromSelectStmt([NotNull] SQLiteParser.FromSelectStmtContext context)
        {
            var query = Visit(context.select_stmt()) as SelectStmt;
            // ANSI requires subquery in FROM shall have an alias - let's relaxed it 
            var alias = "anonymous";
            List<string> colalias = new List<string>();
            var aliascolumns = context.table_alias_with_columns();
            if (aliascolumns != null)
            {
                alias = aliascolumns.table_alias().GetText();
                if (aliascolumns.column_name() != null)
                {
                    foreach (var v in aliascolumns.column_name())
                        colalias.Add(v.GetText());
                }
            }
            return new FromQueryRef(query, alias, colalias);
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

            // table refs
            var resultRels = new List<TableRef>();
            foreach (var r in context.table_or_subquery())
            {
                var tab = VisitTable_or_subquery(r) as TableRef;
                resultRels.Add(tab);
            }
            if (context.join_clause() != null)
            {
                var tab = VisitJoin_clause(context.join_clause()) as TableRef;
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
            if (havingexpr == 1)
            {
                having = Visit(context.expr(whichexpr)) as Expr;
            }

            // -- binding stage
            return new SelectStmt(resultCols, resultRels, where, groupby, having, null, null, null, null, context.GetText());
        }

        public override object VisitCommon_table_expression([NotNull] SQLiteParser.Common_table_expressionContext context)
        {
            List<string> colNames = null;
            if (context.column_name() != null)
            {
                colNames = new List<string>();
                Array.ForEach(context.column_name(), x => colNames.Add(x.GetText()));
            }
            return new CteExpr(context.table_name().GetText(), colNames, Visit(context.select_stmt()) as SQLStatement);
        }

        public override object VisitSelect_stmt([NotNull] SQLiteParser.Select_stmtContext context)
        {
            List<CteExpr> ctes = null;
            if (context.K_WITH() != null)
            {
                ctes = new List<CteExpr>();
                Array.ForEach(context.common_table_expression(), x => ctes.Add(VisitCommon_table_expression(x) as CteExpr));
            }

            // setqs may consists multiple core select statement
            var setqs = new List<SelectStmt>();
            Array.ForEach(context.select_core(), x => setqs.Add(VisitSelect_core(x) as SelectStmt));

            // ORDER BY clause
            List<OrderTerm> orders = null;
            if (context.K_ORDER() != null)
            {
                Debug.Assert(context.K_BY() != null);
                orders = new List<OrderTerm>();
                Array.ForEach(context.ordering_term(), x => orders.Add(VisitOrdering_term(x) as OrderTerm));
            }

            // LIMIT clause
            Expr limit = null;
            if (context.K_LIMIT() != null) {
                limit = Visit(context.expr(0)) as Expr;
            }

            // seqts[0] is also expanded to main body
            return new SelectStmt(setqs[0].selection_, setqs[0].from_, setqs[0].where_, setqs[0].groupby_, setqs[0].having_,
                                    ctes, setqs, orders, limit, GetRawText(context));
        }

        public override object VisitType_name([NotNull] SQLiteParser.Type_nameContext context)
        {
            Dictionary<string, Type> nameMap = new Dictionary<string, Type> {
                {"int", typeof(int)}, {"integer", typeof(int)},
                {"double", typeof(double)},{"double precision", typeof(double)},
                {"char", typeof(string)}, {"varchar", typeof(string)},
                {"datetime", typeof(DateTime)}, {"date", typeof(DateTime)},{"time", typeof(DateTime)},
                {"numeric", typeof(decimal)}, {"decimal", typeof(decimal)},
            };

            string typename = context.name(0).GetText().Trim().ToLower();
            Type type = nameMap[typename];
            if (type == typeof(int))
                return new IntType();
            else if (type == typeof(double))
                return new DoubleType();
            else if (type == typeof(DateTime))
                return new DateTimeType();
            else if (type == typeof(string))
            {
                var numbers = context.signed_number();
                adb.utils.Utils.Checks(numbers.Count() == 1);
                return new CharType(int.Parse(numbers[0].NUMERIC_LITERAL().GetText()));
            }
            else if (type == typeof(decimal))
            {
                var numbers = context.signed_number();
                adb.utils.Utils.Checks(numbers.Any() && numbers.Count() <= 2);
                int prec = int.Parse(context.signed_number()[0].NUMERIC_LITERAL().GetText());
                int scale = 0;
                if (numbers.Count() > 1)
                    scale = int.Parse(context.signed_number()[1].NUMERIC_LITERAL().GetText());
                return new NumericType(prec, scale);
            }

            throw new SemanticAnalyzeException("unknow data type");
        }
        public override object VisitColumn_def([NotNull] SQLiteParser.Column_defContext context)
            => new ColumnDef(context.column_name().GetText(), VisitType_name(context.type_name()) as ColumnType, 0);

        public override object VisitPrimaryKeyConstraint([NotNull] SQLiteParser.PrimaryKeyConstraintContext context)
        {
            if (context.K_PRIMARY() != null) {
                Debug.Assert(context.K_KEY() != null);
            }
            return new PrimaryKeyConstraint();
        }

        public override object VisitCreate_table_stmt([NotNull] SQLiteParser.Create_table_stmtContext context)
        {
            var cols = new List<ColumnDef>();
            foreach (var v in context.column_def())
                cols.Add(VisitColumn_def(v) as ColumnDef);
            var cons = new List<TableConstraint>();
            foreach (var v in context.table_constraint())
                cons.Add(VisitTable_constraint(v) as TableConstraint);
            return new CreateTableStmt(context.table_name().GetText(), cols, cons, context.GetText());
        }

        public override object VisitAnalyze_stmt([NotNull] SQLiteParser.Analyze_stmtContext context)
        {
            var tabref = new BaseTableRef(context.table_name().GetText());
            return new AnalyzeStmt(tabref, context.GetText());
        }

        public override object VisitDrop_table_stmt([NotNull] SQLiteParser.Drop_table_stmtContext context) 
            => new DropTableStmt (context.table_name().GetText(), context.GetText());

        public override object VisitCreate_index_stmt([NotNull] SQLiteParser.Create_index_stmtContext context)
        {
            bool unique = context.K_UNIQUE() is null ? false : true;
            string indexname = context.index_name().GetText();
            var tableref = new BaseTableRef(context.table_name().GetText());
            var where = context.K_WHERE() is null ? null : (Expr)Visit(context.expr());
            var columns = new List<string>();
            foreach (var v in context.indexed_column())
                columns.Add(v.GetText());

            Debug.Assert(columns.Count >= 1);
            return new CreateIndexStmt(indexname, tableref, unique, columns, where, context.GetText());
        }

        public override object VisitInsert_stmt([NotNull] SQLiteParser.Insert_stmtContext context)
        {
            var tabref = new BaseTableRef(context.table_name().GetText());
            var cols = new List<string>();
            foreach (var v in context.column_name())
                cols.Add(v.GetText());
            var vals = new List<Expr>();
            foreach (var v in context.expr())
                vals.Add(Visit(v) as Expr);
            SelectStmt select = null;
            if (context.select_stmt() != null)
                select = Visit(context.select_stmt()) as SelectStmt;
            return new InsertStmt(tabref, cols, vals, select, context.GetText());
        }

        public override object VisitCopy_stmt([NotNull] SQLiteParser.Copy_stmtContext context)
        {
            var tabref = new BaseTableRef(context.table_name().GetText());
            var cols = new List<string>();
            foreach (var v in context.column_name())
                cols.Add(v.GetText());
            Expr where = null;
            if (context.K_WHERE() != null)
                where = Visit(context.expr()) as Expr;
            return new CopyStmt(tabref, cols, context.STRING_LITERAL().GetText(), where, context.GetText());
        }

        public override object VisitSql_stmt([NotNull] SQLiteParser.Sql_stmtContext context)
        {
            SQLStatement r = null;

            bool isExplain = false;
            if (context.K_EXPLAIN() != null)
                isExplain = true;

            if (context.select_stmt() != null)
                r = Visit(context.select_stmt()) as SQLStatement;
            else if (context.create_table_stmt() != null)
                r = Visit(context.create_table_stmt()) as SQLStatement;
            else if (context.drop_table_stmt() != null)
                r = Visit(context.drop_table_stmt()) as DropTableStmt;
            else if (context.insert_stmt() != null)
                r = Visit(context.insert_stmt()) as SQLStatement;
            else if (context.copy_stmt() != null)
                r = Visit(context.copy_stmt()) as SQLStatement;
            else if (context.analyze_stmt() != null)
                r = Visit(context.analyze_stmt()) as SQLStatement;
            else if (context.create_index_stmt() != null)
                r = Visit(context.create_index_stmt()) as SQLStatement;

            if (r is null)
                throw new NotImplementedException();

            Debug.Assert(!r.explainOnly_);
            r.explainOnly_ = isExplain;
            return r;
        }

        public override object VisitSql_stmt_list([NotNull] SQLiteParser.Sql_stmt_listContext context)
        {
            List<SQLStatement> list = new List<SQLStatement>();
            foreach (var v in context.sql_stmt())
            {
                var stmt = Visit(v) as SQLStatement;
                list.Add(stmt);
            }

            return new StatementList(list, GetRawText(context));
        }
    }
}

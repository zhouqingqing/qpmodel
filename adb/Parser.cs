using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace adb
{
    // antlr requires user defined exception with this name
    public class RuntimeException : Exception
    {
        public RuntimeException(string msg) { }
    }

    public class SemanticAnalyzeException : Exception
    {
        public SemanticAnalyzeException(string msg) { }
    }

    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) { }
    }

    public class RawParser
    {
        public static SQLStatement ParseSqlStatement(string sql)
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
        // alias is the first name of the reference
        //  [OK] select b.a1 from a b; 
        //  [FAIL] select a.a1 from a b;
        //
        internal string alias_;

        internal readonly List<ColExpr> outerrefs_ = new List<ColExpr>();

        public override string ToString() => alias_;
        public Expr LocateColumn(string colAlias)
        {
            // TODO: the logic here only uses alias, but not table. Need polish 
            // here to differentiate alias from different tables
            //
            Expr r = null;
            var list = AllColumnsRefs();
            foreach (var v in list)
            {
                if (v.alias_?.Equals(colAlias) ?? false)
                    if (r is null)
                        r = v;
                    else
                        throw new SemanticAnalyzeException($"ambigous column name {colAlias}");
            }

            return r;
        }

        public List<Expr> AddOuterRefsToOutput(List<Expr> output)
        {
            outerrefs_.ForEach(x =>
            {
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

    // FROM <table> [alias]
    public class BaseTableRef : TableRef
    {
        public string relname_;

        public BaseTableRef([NotNull] string name, string alias = null)
        {
            relname_ = name;
            alias_ = alias ?? relname_;
        }

        public override string ToString()
            => (relname_.Equals(alias_)) ? $"{alias_}" : $"{relname_} as {alias_}";

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> l = new List<Expr>();
            var columns = Catalog.systable_.Table(relname_);
            foreach (var c in columns)
            {
                ColumnDef coldef = c.Value;
                l.Add(new ColExpr(null, alias_, coldef.name_));
            }

            return l;
        }
    }

    // FROM <filename>
    public class ExternalTableRef : TableRef
    {
        public string filename_;
        public BaseTableRef baseref_;
        public List<Expr> colrefs_;

        public ExternalTableRef([NotNull] string filename, BaseTableRef baseref, List<Expr> colrefs)
        {
            filename_ = filename.Replace('\'', ' ');
            baseref_ = baseref;
            colrefs_ = colrefs;
            alias_ = baseref.alias_;
        }

        public override string ToString() => filename_;
        public override List<Expr> AllColumnsRefs() => colrefs_;
    }

    // FROM <subquery> [alias]
    public class FromQueryRef : TableRef
    {
        public SelectStmt query_;

        public override string ToString() => $"SELECT ({alias_})";
        public FromQueryRef(SelectStmt query, [NotNull] string alias)
        {
            query_ = query;
            alias_ = alias;
        }

        public override List<Expr> AllColumnsRefs()
        {
            // make a coopy of selection list and replace their tabref as this
            var r = new List<Expr>();
            query_.selection_.ForEach(x=> {
                var y = x.Clone();
                y.VisitEachExpr(z =>
                {
                    if (z is ColExpr cz)
                    {
                        cz.tabRef_ = this;
                        cz.tabName_ = this.alias_;
                    }
                });
                r.Add(y);
            });

            // it is actually a waste to return as many as selection: if selection item is 
            // without an alias, there is no way outer layer can references it, thus no need
            // to output them.
            //
            Debug.Assert(r.Count() == query_.selection_.Count());
            return r;
        }
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
        {
            List<Expr> args = new List<Expr>();
            foreach (var v in context.expr())
                args.Add(Visit(v) as Expr);
            return FuncExpr.BuildFuncExpr(context.function_name().GetText(), args);
        }
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
            if (havingexpr == 1)
            {
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

            List<OrderTerm> orders = null;
            if (context.K_ORDER() != null)
            {
                Debug.Assert(context.K_BY() != null);
                orders = new List<OrderTerm>();
                Array.ForEach(context.ordering_term(), x => orders.Add(VisitOrdering_term(x) as OrderTerm));
            }

            // seqts[0] is also expanded to main body
            return new SelectStmt(setqs[0].selection_, setqs[0].from_, setqs[0].where_, setqs[0].groupby_, setqs[0].having_,
                                    ctes, setqs, orders, context.GetText());
        }

        public override object VisitType_name([NotNull] SQLiteParser.Type_nameContext context)
        {
            Dictionary<string, DType> nameMap = new Dictionary<string, DType> {
                {"int", DType.Int4},
                {"char", DType.Char},
                {"datetime", DType.Datetime},
                {"numeric", DType.Numeric},
            };

            string typename = context.name(0).GetText().Trim().ToLower();
            DType type = nameMap[typename];
            if (type == DType.Int4)
                return new IntType();
            else if (type == DType.Datetime)
                return new DateTimeType();
            else if (type == DType.Char)
            {
                var numbers = context.signed_number();
                Utils.Checks(numbers.Count() == 1);
                return new CharType(int.Parse(numbers[0].NUMERIC_LITERAL().GetText()));
            }
            else if (type == DType.Numeric)
            {
                var numbers = context.signed_number();
                Utils.Checks(numbers.Any() && numbers.Count() <= 2);
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
        public override object VisitCreate_table_stmt([NotNull] SQLiteParser.Create_table_stmtContext context)
        {
            var cols = new List<ColumnDef>();
            foreach (var v in context.column_def())
                cols.Add(VisitColumn_def(v) as ColumnDef);
            return new CreateTableStmt(context.table_name().GetText(), cols, context.GetText());
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

            if (context.select_stmt() != null)
                r = Visit(context.select_stmt()) as SQLStatement;
            else if (context.create_table_stmt() != null)
                r = Visit(context.create_table_stmt()) as SQLStatement;
            else if (context.insert_stmt() != null)
                r = Visit(context.insert_stmt()) as SQLStatement;
            else if (context.copy_stmt() != null)
                r = Visit(context.copy_stmt()) as SQLStatement;

            if (r is null)
                throw new NotImplementedException();
            return r;
        }
    }
}

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
        public RuntimeException(string msg) => Console.WriteLine(msg);
    }

    public class SemanticAnalyzeException : Exception
    {
        public SemanticAnalyzeException(string msg) => Console.WriteLine(msg);
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

    public abstract class TableRef
    {
        // alias is the first name of the reference
        //  [OK] select b.a1 from a b; 
        //  [FAIL] select a.a1 from a b;
        //
        internal string alias_;

		// list of correlated colexpr needs this table
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
	        // for outerrefs, if it is not found in output list, add them there and mark invisible
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

        public static bool HasOuterRefs(List<TableRef> tables) {
            foreach (var v in tables)
            {
                if (v.outerrefs_.Count > 0)
                    return true;
            }
            return false;
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

        public TableDef Table() => Catalog.systable_.Table(relname_);

        public override string ToString()
            => (relname_.Equals(alias_)) ? $"{alias_}" : $"{relname_} as {alias_}";

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> l = new List<Expr>();
            var columns = Catalog.systable_.TableCols(relname_);
            foreach (var c in columns)
            {
                ColumnDef coldef = c.Value;
                l.Add(new ColExpr(null, alias_, coldef.name_, coldef.type_));
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

    public abstract class QueryRef : TableRef
    {
        public SelectStmt query_;

        public QueryRef(SelectStmt query, [NotNull] string alias)
        {
            query_ = query;
            alias_ = alias;
        }

        public override List<Expr> AllColumnsRefs()
        {
            // make a coopy of selection list and replace their tabref as this
            var r = new List<Expr>();
            query_.selection_.ForEach(x =>
            {
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

    // FROM <subquery> [alias]
    public class FromQueryRef : QueryRef
    {
        public override string ToString() => $"FROM ({alias_})";
        public FromQueryRef(SelectStmt query, [NotNull] string alias) : base(query, alias) { }
    }

    // WITH <alias> AS <query>
    public class CTEQueryRef : QueryRef
    {
        public override string ToString() => $"WITH ({alias_})";
        public CTEQueryRef(SelectStmt query, [NotNull] string alias) : base(query, alias) { }
    }

    public class JoinQueryRef : TableRef {
        public List<TableRef> tables_;
        public List<string> joinops_;
        public List<Expr> constraints_;

        public JoinQueryRef(List<TableRef> tables, List<string> joinops, List<Expr> constraints) {
            Debug.Assert(tables.Count == joinops.Count + 1);
            Debug.Assert(constraints.Count == joinops.Count);
            tables.ForEach(x=>Debug.Assert(!(x is JoinQueryRef)));
            tables_ = tables;
            joinops_ = joinops;
            constraints_ = constraints;
        }

        public override List<Expr> AllColumnsRefs()
        {
            List<Expr> r = new List<Expr>();
            tables_.ForEach(x => r.AddRange(x.AllColumnsRefs()));
            return r;
        }
    }

    // antlr visitor pattern parser
    class SQLiteVisitor : SQLiteBaseVisitor<object>
    {
        public override object VisitLiteral_value([NotNull] SQLiteParser.Literal_valueContext context)
            => new LiteralExpr(context.GetText());
        public override object VisitBrackexpr([NotNull] SQLiteParser.BrackexprContext context)
            => Visit(context.expr());
        public override object VisitArithtimesexpr([NotNull] SQLiteParser.ArithtimesexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitArithplusexpr([NotNull] SQLiteParser.ArithplusexprContext context)
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
            => new ColExpr(context.database_name()?.GetText(),
                context.table_name()?.GetText(),
                context.column_name()?.GetText(), null);
        public override object VisitArithcompexpr([NotNull] SQLiteParser.ArithcompexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitLogicAndExpr([NotNull] SQLiteParser.LogicAndExprContext context)
            => new LogicAndExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        public override object VisitLogicOrExpr([NotNull] SQLiteParser.LogicOrExprContext context)
            => new LogicOrExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)));
        public override object VisitBoolEqualexpr([NotNull] SQLiteParser.BoolEqualexprContext context)
            => new BinExpr((Expr)Visit(context.expr(0)), (Expr)Visit(context.expr(1)), context.op.Text);
        public override object VisitSubqueryExpr([NotNull] SQLiteParser.SubqueryExprContext context)
        {
            if (context.K_EXISTS() != null)
                return new ExistSubqueryExpr(Visit(context.select_stmt()) as SelectStmt);
            return new ScalarSubqueryExpr(Visit(context.select_stmt()) as SelectStmt);
        }

        public override object VisitUnaryexpr([NotNull] SQLiteParser.UnaryexprContext context)
        {
            bool hasNot = (context.unary_operator().K_NOT() != null);
            var expr = Visit(context.expr()) as Expr;
            if (expr is ExistSubqueryExpr ee)
            {
                // ExistsSubquery needs to get hasNot together for easier processing
                ee.hasNot_ = hasNot;
                return ee;
            }
            else
                return new UnaryExpr(expr, hasNot);
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

            r.alias_ = alias;
            return r;
        }

        public override object VisitFromSelectStmt([NotNull] SQLiteParser.FromSelectStmtContext context)
        {
            var query = Visit(context.select_stmt()) as SelectStmt;
            // ANSI requires subquery in FROM shall have an alias - let's relaxed it 
            var alias = "unknown";
            if (context.table_alias() != null)
                alias = context.table_alias().GetText();
            return new FromQueryRef(query, alias);
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
                Utils.Checks(numbers.Count() == 1);
                return new CharType(int.Parse(numbers[0].NUMERIC_LITERAL().GetText()));
            }
            else if (type == typeof(decimal))
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
            return new CreateTableStmt(context.table_name().GetText(), cols, context.GetText());
        }

        public override object VisitAnalyze_stmt([NotNull] SQLiteParser.Analyze_stmtContext context)
        {
            var tabref = new BaseTableRef(context.table_name().GetText());
            return new AnalyzeStmt(tabref, context.GetText());
        }

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

            if (context.select_stmt() != null)
                r = Visit(context.select_stmt()) as SQLStatement;
            else if (context.create_table_stmt() != null)
                r = Visit(context.create_table_stmt()) as SQLStatement;
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

            return new StatementList(list, context.GetText());
        }
    }
}

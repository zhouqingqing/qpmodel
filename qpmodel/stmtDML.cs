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
using qpmodel.stat;
using qpmodel.sqlparser;
using qpmodel.expr;
using qpmodel.logic;
using qpmodel.physic;
using qpmodel.utils;

namespace qpmodel.dml
{
    public abstract class TableConstraint
    {
    }

    public class PrimaryKeyConstraint : TableConstraint
    {
        public PrimaryKeyConstraint() { }
    }

    public class CreateTableStmt : SQLStatement
    {
        public readonly string tabName_;
        public readonly List<ColumnDef> cols_;
        public readonly List<TableConstraint> cons_;
        public readonly string distributedBy_;

        public CreateTableStmt(string tabName, List<ColumnDef> cols, List<TableConstraint> cons,
            string distributedBy, string text) : base(text)
        {
            tabName_ = tabName; cols_ = cols;
            int ord = 0; cols_.ForEach(x => x.ordinal_ = ord++);
            if (cols.GroupBy(x => x.name_).Count() < cols.Count)
                throw new SemanticAnalyzeException("duplicated column name");
            cons_ = cons;
            distributedBy_ = distributedBy;
        }
        public override string ToString()
        {
            return $"CREATE {tabName_ }: {string.Join(",", cols_)} :{distributedBy_}";
        }

        public override List<Row> Exec()
        {
            Catalog.systable_.CreateTable(tabName_, cols_, distributedBy_);
            return null;
        }
    }

    public class DropTableStmt : SQLStatement
    {
        public readonly string tabName_;
        public DropTableStmt(string tabName, string text) : base(text)
        {
            tabName_ = tabName;
        }
        public override string ToString() => $"DROP {tabName_}";

        public override List<Row> Exec()
        {
            Catalog.systable_.DropTable(tabName_);
            return null;
        }
    }

    public class AnalyzeStmt : SQLStatement
    {
        public readonly BaseTableRef targetref_;
        public readonly SelectStmt select_;

        public AnalyzeStmt(BaseTableRef target, string text,
                           SelectStmt.TableSample ts) : base(text)
        {
            // SELECT statement is used so later optimizations can be kicked in easier
            targetref_ = target;
            string sql = $"select * from {target.relname_} ";

            if (ts != null)
                sql += $" tablesample row ({ts.rowcnt_})";

            select_ = RawParser.ParseSingleSqlStatement(sql) as SelectStmt;
            // select_ is a different statement, binding their options
            select_.queryOpt_ = queryOpt_;
        }

        public override BindContext Bind(BindContext parent)
        {
            return select_.Bind(parent);
        }

        // It is modeled as a sampling scan
        public override LogicNode CreatePlan()
        {
            // disable memo optimization for it
            queryOpt_.optimize_.use_memo_ = false;

            logicPlan_ = new LogicAnalyze(select_.CreatePlan());
            distributed_ = select_.distributed_;
            return logicPlan_;
        }

        public override LogicNode SubstitutionOptimize()
        {
            Debug.Assert(object.ReferenceEquals(queryOpt_, select_.queryOpt_));
            var scan = select_.SubstitutionOptimize();
            logicPlan_ = new LogicAnalyze(scan);
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(queryOpt_);
            return logicPlan_;
        }
    }

    public class InsertStmt : SQLStatement
    {
        public readonly BaseTableRef targetref_;
        public List<Expr> cols_;

        // vals_ and select_ are mutual exclusive
        public readonly List<Expr> vals_;
        public readonly SelectStmt select_;

        public InsertStmt(BaseTableRef target, List<string> cols, List<Expr> vals, SelectStmt select, string text) : base(text)
        {
            targetref_ = target; cols_ = null; vals_ = vals; select_ = select;
            // select_ is a different statement, binding their options
            if (select_ != null)
                select_.queryOpt_ = queryOpt_;
        }

        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);

            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // verify target table is correct
            if (Catalog.systable_.TryTable(targetref_.relname_) is null)
                throw new SemanticAnalyzeException($@"base table '{targetref_.alias_}' not exists");

            // use selectstmt's target list is not given
            Utils.Assumes(cols_ is null);
            if (select_ is null)
            {
                if (cols_ is null)
                    cols_ = targetref_.AllColumnsRefs();
                // verify selectStmt's selection list is compatible with insert target table's
                if (vals_.Count != cols_.Count)
                    throw new SemanticAnalyzeException("insert has no equal expressions than target columns");
                vals_.ForEach(x => x.Bind(context));
            }
            else
            {
                select_.BindWithContext(context);
                if (cols_ is null)
                    cols_ = select_.selection_;
                Debug.Assert(select_.selection_.Count == cols_.Count);
            }

            bindContext_ = context;
            return context;
        }

        public override LogicNode CreatePlan()
        {
            queryOpt_.optimize_.use_memo_ = false;
            logicPlan_ = select_ is null ?
                new LogicInsert(targetref_, new LogicResult(vals_)) :
                new LogicInsert(targetref_, select_.CreatePlan());
            distributed_ = select_ is null ? false : select_.distributed_;
            return logicPlan_;
        }

        public override LogicNode SubstitutionOptimize()
        {
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(queryOpt_);
            if (select_ != null)
                select_.logicPlan_.ResolveColumnOrdinal(select_.selection_, false);
            return logicPlan_;
        }
    }

    public class CopyStmt : SQLStatement
    {
        public readonly BaseTableRef targetref_;
        public readonly string fileName_;
        public readonly Expr where_;

        internal InsertStmt insert_;

        // copy tab(cols) from <file> <where> =>
        // insert into tab(cols) select * from foreign_table(<file>) <where>
        //
        public CopyStmt(BaseTableRef targetref, List<string> cols, string fileName, Expr where, string text) : base(text)
        {
            cols = cols.Count != 0 ? cols : null;
            targetref_ = targetref; fileName_ = fileName; where_ = where;

            var colrefs = new List<Expr>();
            Utils.Assumes(cols is null);
            if (cols is null)
                colrefs = targetref.AllColumnsRefs();
            ExternalTableRef sourcetab = new ExternalTableRef(fileName, targetref, colrefs);
            SelectStmt select = new SelectStmt(new List<Expr> { new SelStar(null) },
                            new List<TableRef> { sourcetab }, where, null, null, null, null, null, null, text);
            insert_ = new InsertStmt(targetref, cols, null, select, text) { queryOpt_ = queryOpt_ };
        }

        public override BindContext Bind(BindContext parent) => insert_.Bind(parent);
        public override LogicNode CreatePlan()
        {
            queryOpt_.optimize_.use_memo_ = false;
            return insert_.CreatePlan();
        }
        public override LogicNode SubstitutionOptimize()
        {
            logicPlan_ = insert_.SubstitutionOptimize();
            physicPlan_ = insert_.physicPlan_;
            return logicPlan_;
        }
    }
}

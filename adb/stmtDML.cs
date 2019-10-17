using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public class CreateTableStmt : SQLStatement
    {
        readonly public string tabName_;
        readonly public List<ColumnDef> cols_;
        public CreateTableStmt(string tabName, List<ColumnDef> cols, string text) : base(text)
        {
            tabName_ = tabName; cols_ = cols;
            int ord = 0; cols_.ForEach(x => x.ordinal_ = ord++);
            if (cols.GroupBy(x => x.name_).Count() < cols.Count)
                throw new SemanticAnalyzeException("duplicated column name");
        }
        public override string ToString() => $"{tabName_ }: {string.Join(",", cols_)}";
    }

    public class InsertStmt : SQLStatement
    {
        readonly public BaseTableRef targetref_;
        public List<Expr> cols_;
        readonly public List<Expr> vals_;
        readonly public SelectStmt select_;

        public InsertStmt(BaseTableRef target, List<string> cols, List<Expr> vals, SelectStmt select, string text) : base(text)
        {
            targetref_  = target; cols_ = null; vals_ = vals; select_ = select;
        }
        void bindSelectStmt(BindContext context) => select_?.BindWithContext(context);
        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);

            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // verify target table is correct
            if (Catalog.systable_.Table(targetref_.relname_) is null)
                throw new Exception($@"base table {targetref_.alias_} not exists");

            // use selectstmt's target list is not given
            Utils.Assumes(cols_ is null);
            if (cols_ is null)
                cols_ = select_.selection_;
            bindSelectStmt(context);

            // verify selectStmt's selection list is compatible with insert target table's
            var selectlist = select_.selection_;
            if (selectlist.Count != cols_.Count)
                throw new SemanticAnalyzeException("insert has no equal expressions than target columns");

            bindContext_ = context;
            return context;
        }

        public override LogicNode CreatePlan()
        {
            logicPlan_ =  new LogicInsert(targetref_, select_.CreatePlan());
            return logicPlan_;
        }

        public override LogicNode Optimize()
        {
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            return logicPlan_;
        }
    }

    public class CopyStmt : SQLStatement
    {
        readonly public BaseTableRef targetref_;
        readonly public string fileName_;
        readonly public Expr where_;

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
            SelectStmt select = new SelectStmt(new List<Expr>{ new SelStar(null) }, 
                            new List<TableRef> {sourcetab}, where, null, null, null, null, null, text);
            insert_ = new InsertStmt(targetref, cols, null, select, text);
            insert_.profileOpt_ = profileOpt_;
        }

        public override BindContext Bind(BindContext parent)=> insert_.Bind(parent);
        public override LogicNode CreatePlan() =>insert_.CreatePlan();
        public override LogicNode Optimize()
        {
            logicPlan_ = insert_.Optimize();
            physicPlan_ = insert_.physicPlan_;
            return logicPlan_;
        }
    }
}

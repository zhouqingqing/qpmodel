using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

using qpmodel.sqlparser;
using qpmodel.logic;
using qpmodel.physic;
using qpmodel.expr;

namespace qpmodel.index
{
    public class CreateIndexStmt : SQLStatement
    {
        public IndexDef def_;
        public readonly BaseTableRef targetref_;
        public readonly SelectStmt select_;

        public CreateIndexStmt(string indexname, 
            BaseTableRef target, bool unique, List<string> columns, Expr where, string text) : base(text)
        {
            targetref_ = target;
            def_ = new IndexDef();
            def_.name_ = indexname;
            def_.unique_ = unique;
            def_.columns_ = columns;
            def_.table_ = target;
            select_ = RawParser.ParseSingleSqlStatement
                ($"select sysrid_, {string.Join(",", columns)} from {def_.table_.relname_}") as SelectStmt;
        }

        public override BindContext Bind(BindContext parent)
        {
            return select_.Bind(parent);
        }

        // It is modeled as a sample scan
        public override LogicNode CreatePlan()
        {
            // disable memo optimization for it
            queryOpt_.optimize_.use_memo_ = false;

            logicPlan_ = new LogicIndex(select_.CreatePlan(), def_);
            return logicPlan_;
        }

        public override LogicNode SubstitutionOptimize()
        {
            var scan = select_.SubstitutionOptimize();
            logicPlan_ = new LogicIndex(scan, def_);
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(queryOpt_);
            return logicPlan_;
        }
    }

    public class DropIndexStmt : SQLStatement
    {
        public readonly string indName_;
        public DropIndexStmt(string indName, string text) : base(text)
        {
            indName_ = indName;
        }
        public override string ToString() => $"DROP {indName_}";

        public override List<Row> Exec()
        {
            Catalog.systable_.DropIndex(indName_);
            return null;
        }
    }

    public class IndexDef
    {
        public string name_;
        public BaseTableRef table_;
        public bool unique_;
        public List<string> columns_;

        // storage
        internal ISearchIndex index_;

        public override string ToString() => $"{name_}({string.Join(",", columns_)}) on {table_}";
    }

    public class LogicIndex : LogicNode
    {
        internal IndexDef def_;
        public LogicIndex(LogicNode child, IndexDef def)
        {
            children_.Add(child); def_ = def;
        }

        public BaseTableRef GetTargetTable() => (child_() as LogicScanTable).tabref_;
    }

    public class PhysicIndex : PhysicNode
    {
        ISearchIndex index_;

        public PhysicIndex(LogicIndex logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string Open(ExecContext context)
        {
            base.Open(context);
            var logic = (logic_ as LogicIndex);
            var tabName = logic.GetTargetTable().relname_;
            index_ = new MemoryIndex(logic.def_.unique_);
            return null;
        }

        public override string Exec(Func<Row, string> callback)
        {
            child_().Exec(r =>
            {
                var tablerow = r[0];
                Debug.Assert(tablerow != null && tablerow is Row);
                var key = new KeyList(r.ColCount() -1);
                for (int i = 1; i < r.ColCount(); i++)
                    key[i - 1] = r[i];
                index_.Insert(key, tablerow as Row);
                return null;
            });
            return null;
        }

        public override string Close()
        {
            var logic = (logic_ as LogicIndex);
            var def = logic.def_;

            // register the index
            Debug.Assert(def.index_ is null);
            def.index_ = index_;
            Catalog.systable_.CreateIndex(logic.GetTargetTable().relname_, def);
            return null;
        }
    }

    public abstract class ISearchIndex {
        public virtual void Insert(KeyList key, Row r) => throw new NotImplementedException();
        public virtual List<Row> Search(string op, KeyList key) => throw new NotImplementedException();
        public virtual List<Row> Search(KeyList l, KeyList r) => throw new NotImplementedException();
    }

    public class MemoryIndex: ISearchIndex{

        internal bool unique_;
        internal SortedDictionary<KeyList, List<Row>> data_ = new SortedDictionary<KeyList, List<Row>>();

        public MemoryIndex(bool unique) {
            unique_ = unique;
        }

        public override void Insert(KeyList key, Row r) {
            if (data_.TryGetValue(key, out List<Row> l))
            {
                if (unique_) {
                    Debug.Assert(l.Count == 1);
                    throw new SemanticExecutionException(
                        $"try to insert duplicated key: {key}. Existing row: {l}, new row: {r}");
                }
                l.Add(r);
            }
            else
                data_.Add(key, new List<Row>() { r });
        }

        public override List<Row> Search(string op, KeyList key)
        {
            List<Row> rows = new List<Row>();
            switch (op)
            {
                case "=":
                    if (data_.TryGetValue(key, out List<Row> l))
                        return l;
                    break;
                case ">":
                    foreach (var v in data_.Where(x => x.Key.CompareTo(key) > 0))
                        rows.AddRange(v.Value);
                    break;
                case ">=":
                    foreach (var v in data_.Where(x => x.Key.CompareTo(key) >= 0))
                        rows.AddRange(v.Value);
                    break;
                case "<":
                    foreach (var v in data_.Where(x => x.Key.CompareTo(key) < 0))
                        rows.AddRange(v.Value);
                    break;
                case "<=":
                    foreach (var v in data_.Where(x => x.Key.CompareTo(key) <= 0))
                        rows.AddRange(v.Value);
                    break;
                default:
                    throw new NotImplementedException("index search");
            }

            return rows;
        }

        public override List<Row> Search(KeyList l, KeyList r)
        {
            List<Row> res = new List<Row>();
            foreach (var v in data_.Where(x => x.Key.CompareTo(l) > 0 && x.Key.CompareTo(r) < 0))
                res.AddRange(v.Value);
            return res;
        }
    }
}

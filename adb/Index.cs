using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

namespace adb
{
    public class CreateIndexStmt : SQLStatement
    {
        public readonly string indexname_;
        public readonly bool unique_;
        public readonly BaseTableRef targetref_;
        public readonly SelectStmt select_;
        public CreateIndexStmt(string indexname, 
            BaseTableRef target, bool unique, List<string> columns, Expr where, string text) : base(text)
        {
            targetref_ = target;
            unique_ = unique;
            select_ = RawParser.ParseSingleSqlStatement
                ($"select sysrid_, {string.Join(",", columns)} from {target.relname_}") as SelectStmt;
        }
        public override BindContext Bind(BindContext parent)
        {
            return select_.Bind(parent);
        }

        // It is modeled as a sample scan
        public override LogicNode CreatePlan()
        {
            // disable memo optimization for it
            optimizeOpt_.use_memo_ = false;

            logicPlan_ = new LogicIndex(select_.CreatePlan(), unique_);
            return logicPlan_;
        }

        public override LogicNode PhaseOneOptimize()
        {
            var scan = select_.PhaseOneOptimize();
            logicPlan_ = new LogicIndex(scan, unique_);
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            return logicPlan_;
        }
    }

    public class LogicIndex : LogicNode
    {
        internal bool unique_;
        public LogicIndex(LogicNode child, bool unique)
        {
            children_.Add(child); unique_ = unique;
        }

        public BaseTableRef GetTargetTable() => (child_() as LogicScanTable).tabref_;
    }

    public class PhysicIndex : PhysicNode
    {
        ISearchIndex index_;

        public PhysicIndex(LogicIndex logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Open()
        {
            var logic = (logic_ as LogicIndex);
            var tabName = logic.GetTargetTable().relname_;

            if (logic.unique_)
                index_ = new UniqueIndex();
            else
                index_ = new NonUniqueIndex();
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, r =>
            {
                var tablerow = r[0];
                Debug.Assert(tablerow != null && tablerow is Row);
                var key = new KeyList(r.ColCount() -1);
                for (int i = 1; i < r.ColCount(); i++)
                    key[i - 1] = r[i];
                index_.Insert(key, tablerow as Row);
                return null;
            });
        }
    }

    public abstract class ISearchIndex {
        public virtual void Insert(dynamic key, Row r) => throw new NotImplementedException();
        public virtual Row SearchUnique(dynamic key) => throw new NotImplementedException();
        public virtual List<Row> Search(dynamic key) => throw new NotImplementedException();
        public virtual List<Row> Search(dynamic l, dynamic r) => throw new NotImplementedException();
    }

    public class NonUniqueIndex: ISearchIndex{
        internal SortedDictionary<dynamic, List<Row>> data_ = new SortedDictionary<dynamic, List<Row>>();

        public NonUniqueIndex() {
        }

        public override void Insert(dynamic key, Row r) {
            if (data_.TryGetValue(key, out List<Row> l))
                l.Add(r);
            else
                data_.Add(key, new List<Row>() { r });
        }

        public override List<Row> Search(dynamic key)
        {
            if (data_.TryGetValue(key, out List<Row> l))
                return l;
            return null;
        }

        public override List<Row> Search(dynamic l, dynamic r)
        {
            List<Row> res = new List<Row>();
            foreach (var v in data_.Where(x => x.Key >= l && x.Key <= r))
                res.AddRange(v.Value);
            return res;
        }
    }
    public class UniqueIndex : ISearchIndex
    {
        internal SortedDictionary<dynamic, Row> data_ = new SortedDictionary<dynamic, Row>();

        public UniqueIndex()
        {
        }

        public override void Insert(dynamic key, Row r)
        {
            if (data_.TryGetValue(key, out Row l))
                throw new SemanticExecutionException("duplicated key found");
            else
                data_.Add(key,  r);
        }

        public override Row SearchUnique(dynamic key)
        {
            if (data_.TryGetValue(key, out Row l))
                return l;
            return null;
        }

        public override List<Row> Search(dynamic l, dynamic r)
        {
            List<Row> res = new List<Row>();
            foreach (var v in data_.Where(x => x.Key >= l && x.Key <= r))
                res.Add(v.Value);
            return res;
        }
    }
}

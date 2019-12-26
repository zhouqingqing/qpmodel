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
        public readonly BaseTableRef targetref_;
        public readonly SelectStmt select_;
        public CreateIndexStmt(string indexname, BaseTableRef target, bool unique, List<string> columns, Expr where, string text) : base(text)
        {
            targetref_ = target;
            select_ = RawParser.ParseSingleSqlStatement($"select  {string.Join(",", columns)} from {target.relname_}") as SelectStmt;
        }
        public override BindContext Bind(BindContext parent)
        {
            return select_.Bind(parent);
        }

        // It is modeled as a sampling scan
        public override LogicNode CreatePlan()
        {
            // disable memo optimization for it
            optimizeOpt_.use_memo_ = false;

            logicPlan_ = new LogicIndex(select_.CreatePlan());
            return logicPlan_;
        }

        public override LogicNode PhaseOneOptimize()
        {
            var scan = select_.PhaseOneOptimize();
            logicPlan_ = new LogicIndex(scan);
            // convert to physical plan
            physicPlan_ = logicPlan_.DirectToPhysical(profileOpt_);
            return logicPlan_;
        }
    }

    public class LogicIndex : LogicNode
    {

        public LogicIndex(LogicNode child) => children_.Add(child);

        public BaseTableRef GetTargetTable() => (child_() as LogicScanTable).tabref_;
    }

    public class PhysicIndex : PhysicNode
    {
        SearchIndex index_ = new SearchIndex();
        public PhysicIndex(LogicIndex logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Open()
        {
            var tabName = (logic_ as LogicIndex).GetTargetTable().relname_;
            SearchIndex index = new SearchIndex();
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, r =>
            {
                index_.Insert(r, null);
                return null;
            });
        }
    }

    public class SearchIndex{
        internal SortedDictionary<dynamic, List<Row>> data_ = new SortedDictionary<dynamic, List<Row>>();

        public SearchIndex() {
        }

        public void Insert(dynamic key, Row r) {
            if (data_.TryGetValue(key, out List<Row> l))
                l.Add(r);
            else
                data_.Add(key, new List<Row>() { r });
        }

        public List<Row> Search(dynamic key)
        {
            if (data_.TryGetValue(key, out List<Row> l))
                return l;
            return null;
        }

        public List<Row> Search(dynamic l, dynamic r)
        {
            List<Row> res = new List<Row>();
            foreach (var v in data_.Where(x => x.Key >= l && x.Key <= r))
                res.AddRange(v.Value);
            return res;
        }
    }
}

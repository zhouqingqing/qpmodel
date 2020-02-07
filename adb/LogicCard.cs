using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using adb.stat;
using adb.expr;

namespace adb.logic
{
    public partial class LogicFilter {
        public override long EstimateCard()
        {
            var nrows = child_().Card();
            if (filter_ != null)
            {
                var selectivity = filter_.EstSelectivity();
                nrows = (long)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
    }

    public partial class LogicScanTable {
        public override long EstimateCard()
        {
            var nrows = Catalog.sysstat_.EstCardinality(tabref_.relname_);
            if (filter_ != null)
            {
                var selectivity = filter_.EstSelectivity();
                nrows = (long)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
    }

    public partial class LogicAgg {
        public override long EstimateCard()
        {
            if (keys_ is null)
                card_ = 1;
            else
            {
                long distinct = 1;
                foreach (var v in keys_) {
                    long ndistinct = 1;
                    if (v is ColExpr vc && vc.tabRef_ is BaseTableRef bvc)
                    {
                        var stat = Catalog.sysstat_.GetColumnStat(bvc.relname_, vc.colName_);
                        ndistinct = stat.n_distinct_;
                    }
                    distinct *= ndistinct;
                }

                card_ = distinct;
            }

            // it won't go beyond the number of output rows
            return Math.Min(card_, child_().Card());
        }
    }

    partial class LogicJoin {
        // classic formula is:
        //   A X B => |A|*|B|/max(dA, dB) where dA,dB are distinct values of joining columns
        // This however does not consider join key distribution. In SQL Server 2014, it introduced
        // histogram join to better the estimation.
        //
        public override long EstimateCard()
        {
            long card;
            CreateKeyList();
            var cardl = l_().Card();
            var cardr = r_().Card();

            long dl = 0, dr = 0, mindlr = 1;
            for (int i = 0; i < leftKeys_.Count; i++)
            {
                var lv = leftKeys_[i];
                if (lv is ColExpr vl && vl.tabRef_ is BaseTableRef bvl)
                {
                    var stat = Catalog.sysstat_.GetColumnStat(bvl.relname_, vl.colName_);
                    dl = stat.EstDistinct();
                }
                var rv = rightKeys_[i];
                if (rv is ColExpr vr && vr.tabRef_ is BaseTableRef bvr)
                {
                    var stat = Catalog.sysstat_.GetColumnStat(bvr.relname_, vr.colName_);
                    dr = stat.EstDistinct();
                }

                if (ops_[i] != "=")
                {
                    mindlr = 0;
                    break;
                }
                mindlr = mindlr * Math.Min(dl, dr);
            }

            if (mindlr != 0)
                card = Math.Max(1, (cardl * cardr) / mindlr);
            else
                // fall back to the old estimator
                card = base.EstimateCard();
            return card;
        }
    }
}

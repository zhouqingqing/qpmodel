using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace adb
{
    partial class LogicScanTable {
        public override long EstCardinality()
        {
            if (card_ == -1)
            {
                var nrows = Catalog.sysstat_.NumberOfRows(tabref_.relname_);
                if (filter_ != null)
                {
                    var selectivity = filter_.EstSelectivity();
                    nrows = (long)(selectivity * nrows);
                }
                card_ = Math.Max(1, nrows);
            }

            return card_;
        }
    }

    partial class LogicAgg {
        public override long EstCardinality()
        {
            if (card_ == -1)
            {
                if (keys_ is null)
                    card_ = 1;
                else
                    card_ = base.EstCardinality();
            }

            return card_;
        }
    }

    partial class LogicJoin {
        // classic formula is:
        //   A X B => |A|*|B|/max(dA, dB) where dA,dB are distinct values of joining columns
        // This however does not consider join key distribution. In SQL Server 2014, it introduced
        // histogram join to better the estimation.
        //
        public override long EstCardinality()
        {
            if (card_ == -1)
            {
                CreateKeyList();
                var cardl = l_().EstCardinality();
                var cardr = r_().EstCardinality();

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
                    card_ = Math.Max(1, (cardl * cardr) / mindlr);
                else
                    // fall back to the old estimator
                    card_ = base.EstCardinality();
            }

            return card_;
        }
    }
}

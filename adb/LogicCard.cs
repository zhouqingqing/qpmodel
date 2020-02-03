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
        public override long EstCardinality()
        {
            if (card_ is CARD_INVALID)
            {
                var nrows = child_().EstCardinality();
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

    public partial class LogicScanTable {
        public override long EstCardinality()
        {
            if (card_ is CARD_INVALID)
            {
                var nrows = Catalog.sysstat_.EstCardinality(tabref_.relname_);
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

    public partial class LogicAgg {
        public override long EstCardinality()
        {
            if (card_ is CARD_INVALID)
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
            if (card_ is CARD_INVALID)
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

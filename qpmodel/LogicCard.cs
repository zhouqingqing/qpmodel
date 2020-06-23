﻿/*
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using qpmodel.stat;
using qpmodel.expr;
using qpmodel.optimizer;

namespace qpmodel.logic
{
    // One CE engineering challenge is upgrading regression. Here we use two
    // mechanisms to manage it: (1) class inheritance hiearachy; (2) version.
    // Once a binary is released, the related version becomes read-only and 
    // further enhancement shall be start with new versions and it can inherts 
    // previous version. Use can pick up version outside and class inhertiance 
    // tells upgrade provenance.
    //
    public abstract class CardEstimator
    {
        public static Version version_;
        protected ulong DefaultEstimate(LogicNode node)
        {
            ulong card = 1;
            node.children_.ForEach(x => card = Math.Max(x.Card(), card));
            return card;
        }

        public abstract ulong LogicFilterCE(LogicFilter node);
        public abstract ulong LogicScanTableCE(LogicScanTable node);
        public abstract ulong LogicAggCE(LogicAgg node);
        public abstract ulong LogicJoinCE(LogicJoin node);
        public static ulong DoEstimation(LogicNode node)
        {
            CE_10 ce = new CE_10();

            switch (node)
            {
                // these are specially handled by the abstract class
                case LogicMemoRef mn:
                    return mn.Deref().EstimateCard();
                case LogicJoinBlock bn:
                    Debug.Assert(bn.group_.explored_);
                    Debug.Assert(bn.group_.exprList_.Count == 2);
                    return bn.group_.exprList_[1].physic_.logic_.EstimateCard();
                case LogicLimit tn:
                    return (ulong)tn.limit_;

                // these requires derived class implmentation
                case LogicFilter fn:
                    return ce.LogicFilterCE(fn);
                case LogicScanTable sn:
                    return ce.LogicScanTableCE(sn);
                case LogicAgg an:
                    return ce.LogicAggCE(an);
                case LogicAppend la:
                    return la.l_().EstimateCard() + la.r_().EstimateCard();
                case LogicJoin jn:
                    return ce.LogicJoinCE(jn);
                case LogicProjectSet ps:
                    // assuming the set tripples child's row count
                    return ce.DefaultEstimate(node) * 3;

                // it also provides a fallback estimator
                default:
                    return ce.DefaultEstimate(node);
            }
        }
    }

    public class CE_10 : CardEstimator
    {
        public CE_10() { version_ = new Version(1, 0); }

        public override ulong LogicFilterCE(LogicFilter node)
        {
            var nrows = node.child_().Card();
            if (node.filter_ != null)
            {
                var selectivity = node.filter_.EstSelectivity();
                nrows = (ulong)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
        public override ulong LogicScanTableCE(LogicScanTable node)
        {
            var nrows = Catalog.sysstat_.EstCardinality(node.tabref_.relname_);
            if (node.filter_ != null)
            {
                var selectivity = node.filter_.EstSelectivity();
                nrows = (ulong)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
        public override ulong LogicAggCE(LogicAgg node)
        {
            ulong card = 1;
            if (node.groupby_ is null)
                card = 1;
            else
            {
                ulong distinct = 1;
                foreach (var v in node.groupby_)
                {
                    ulong ndistinct = 1;
                    if (v is ColExpr vc && vc.tabRef_ is BaseTableRef bvc)
                    {
                        var stat = Catalog.sysstat_.GetColumnStat(bvc.relname_, vc.colName_);
                        ndistinct = stat.n_distinct_;
                    }

                    // stop accumulating in case of overflow
                    if (distinct * ndistinct > distinct)
                        distinct *= ndistinct;
                }

                card = (ulong)distinct;
            }

            // it won't go beyond the number of output rows
            return Math.Min(card, node.child_().Card());
        }

        // classic formula is:
        //   A X B => |A|*|B|/max(dA, dB) where dA,dB are distinct values of joining columns
        // This however does not consider join key distribution. In SQL Server 2014, it introduced
        // histogram join to better the estimation.
        //
        public override ulong LogicJoinCE(LogicJoin node)
        {
            ulong getDistinct(Expr key)
            {
                if (key is ColExpr col)
                {
                    var tr = col.tabRef_;

                    if (tr is FromQueryRef fqr && fqr.MapOutputName(col.colName_) != null)
                        if (fqr.MapOutputName(col.colName_) is ColExpr ce) tr = ce.tabRef_;

                    if (tr is BaseTableRef btr)
                    {
                        var stats = Catalog.sysstat_.GetColumnStat(btr.relname_, col.colName_);
                        return stats?.EstDistinct() ?? 0;
                    }
                    
                }
                return 0;
            }

            ulong card;
            node.CreateKeyList();
            var cardl = node.l_().Card();
            var cardr = node.r_().Card();

            ulong dl = 0, dr = 0, mindlr = 1;
            for (int i = 0; i < node.leftKeys_.Count; i++)
            {
                var lv = node.leftKeys_[i];
                dl = getDistinct(lv);
                var rv = node.rightKeys_[i];
                dr = getDistinct(rv);

                if (node.ops_[i] != "=")
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
                card = DefaultEstimate(node);
            return card;
        }
    };
}

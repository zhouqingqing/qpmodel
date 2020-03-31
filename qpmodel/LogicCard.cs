/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
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
    public abstract class CardEstimator {
        public static Version version_;
        protected long DefaultEstimate(LogicNode node)
        {
            long card = 1;
            node.children_.ForEach(x => card = Math.Max(x.Card(), card));
            return card;
        }

        public abstract long LogicFilterCE(LogicFilter node);
        public abstract long LogicScanTableCE(LogicScanTable node);
        public abstract long LogicAggCE(LogicAgg node);
        public abstract long LogicJoinCE(LogicJoin node);
        public static long DoEstimation(LogicNode node)
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
                    return tn.limit_;

                // these requires derived class implmentation
                case LogicFilter fn:
                    return ce.LogicFilterCE(fn);
                case LogicScanTable sn:
                    return ce.LogicScanTableCE(sn);
                case LogicAgg an:
                    return ce.LogicAggCE(an);
                case LogicJoin jn:
                    return ce.LogicJoinCE(jn);

                // it also provides a fallback estimator
                default:
                    return ce.DefaultEstimate(node);
            }
        }
    }

    public class CE_10 : CardEstimator{
        public CE_10() { version_ = new Version(1, 0);}

        public override long LogicFilterCE(LogicFilter node)
        {
            var nrows = node.child_().Card();
            if (node.filter_ != null)
            {
                var selectivity = node.filter_.EstSelectivity();
                nrows = (long)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
        public override long LogicScanTableCE(LogicScanTable node)
        {
            var nrows = Catalog.sysstat_.EstCardinality(node.tabref_.relname_);
            if (node.filter_ != null)
            {
                var selectivity = node.filter_.EstSelectivity();
                nrows = (long)(selectivity * nrows);
            }
            return Math.Max(1, nrows);
        }
        public override long LogicAggCE(LogicAgg node)
        {
            long card = 1;
            if (node.groupby_ is null)
                card = 1;
            else
            {
                long distinct = 1;
                foreach (var v in node.groupby_)
                {
                    long ndistinct = 1;
                    if (v is ColExpr vc && vc.tabRef_ is BaseTableRef bvc)
                    {
                        var stat = Catalog.sysstat_.GetColumnStat(bvc.relname_, vc.colName_);
                        ndistinct = stat.n_distinct_;
                    }
                    distinct *= ndistinct;
                }

                card = distinct;
            }

            // it won't go beyond the number of output rows
            return Math.Min(card, node.child_().Card());
        }

        // classic formula is:
        //   A X B => |A|*|B|/max(dA, dB) where dA,dB are distinct values of joining columns
        // This however does not consider join key distribution. In SQL Server 2014, it introduced
        // histogram join to better the estimation.
        //
        public override long LogicJoinCE(LogicJoin node)
        {
            long card;
            node.CreateKeyList();
            var cardl = node.l_().Card();
            var cardr = node.r_().Card();

            long dl = 0, dr = 0, mindlr = 1;
            for (int i = 0; i < node.leftKeys_.Count; i++)
            {
                var lv = node.leftKeys_[i];
                if (lv is ColExpr vl && vl.tabRef_ is BaseTableRef bvl)
                {
                    var stat = Catalog.sysstat_.GetColumnStat(bvl.relname_, vl.colName_);
                    dl = stat.EstDistinct();
                }
                var rv = node.rightKeys_[i];
                if (rv is ColExpr vr && vr.tabRef_ is BaseTableRef bvr)
                {
                    var stat = Catalog.sysstat_.GetColumnStat(bvr.relname_, vr.colName_);
                    dr = stat.EstDistinct();
                }

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

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
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

using qpmodel.stat;

namespace qpmodel.tools
{
    public class PrestoColumnStats
    {
        public float distinctValuesCount_ { get; set; }
        public int nullsCount_ { get; set; }
        public dynamic min_ { get; set; }
        public dynamic max_ { get; set; }
        public int? dataSize_ { get; set; }
    }

    public class PrestoTableStats
    {
        public int rowCount { get; set; }
        public Dictionary<string, PrestoColumnStats> columns { get; set; }

        public PrestoTableStats()
        {
        }
    }

    public class PrestoTable
    {
        public string name;
        public PrestoTableStats contents;

        public PrestoTable(string newName)
        {
            name = newName;
        }
    }

    public class PrestoStatsFormatter
    {
        static Historgram ConstructHistogram(PrestoColumnStats presto_stat, int nRows)
        {
            Historgram hist = null;

            if (nRows == 0)
                nRows = (int) 6;

            int valuesCount = nRows - presto_stat.nullsCount_;
            int nbuckets = (int)Math.Min(Historgram.NBuckets_, valuesCount);
            double depth = ((double)valuesCount) / nbuckets;

            // nbuckets -1 for special case
            // 6 values: 1 through 6
            // 6 buckets
            // (max - min) / nbuckets  -->  (6 - 5) / 6 is less than 1
            double chunkSize = (presto_stat.max_ - presto_stat.min_) / (nbuckets - 1);

            hist = new Historgram();

            for (int i = 0; i<nbuckets; i++)
            {
                if (i != nbuckets - 1)
                    hist.buckets_[i] = presto_stat.min_ + (chunkSize* i);
                else
                    hist.buckets_[i] = presto_stat.max_;
            }

            hist.depth_ = depth;
            hist.nbuckets_ = nbuckets;
            return hist;
        }
static ColumnStat PrestoFormatConvert(PrestoColumnStats stat_in, int nRows)
        {
            ColumnStat stat = new ColumnStat();

            stat.mcv_ = null;
            stat.hist_ = null;

            stat.nullfrac_ = (double)stat_in.nullsCount_ / (double)nRows;
            stat.n_rows_ = (ulong)nRows;
            stat.n_distinct_ = (ulong)stat_in.distinctValuesCount_;

            if ((object)stat_in.min_ == null)
                return stat;

            //Type firstMinType = stat_in.min.GetType();
            stat_in.min_ = Catalog.sysstat_.ExtractValue((JsonElement)stat_in.min_);
            stat_in.max_ = Catalog.sysstat_.ExtractValue((JsonElement)stat_in.max_);

            Type minType = stat_in.min_.GetType();
            Type maxType = stat_in.max_.GetType();

            if (minType == typeof(string) || minType == typeof(char) || minType != maxType)
                return stat;

            if (stat_in.min_ > stat_in.max_)
                    return stat;

            stat.hist_ = ConstructHistogram(stat_in, nRows);

            return stat;
        }

        static public void ReadConvertPrestoStats(string stats_dir_fn)
        {
            string[] statFiles = Directory.GetFiles(stats_dir_fn);

            foreach (string statFn in statFiles)
            {
                PrestoTable currentTable = new PrestoTable(statFn);
                currentTable.name = Path.GetFileNameWithoutExtension(statFn);

                string jsonStr = File.ReadAllText(statFn);
                string trimmedJsonStr = Regex.Replace(jsonStr, "\\n", "");
                trimmedJsonStr = Regex.Replace(trimmedJsonStr, "\\r", "");

                currentTable.contents = JsonSerializer.Deserialize<PrestoTableStats>(trimmedJsonStr);

                foreach (KeyValuePair<string, PrestoColumnStats> kvp in currentTable.contents.columns)
                {
                    ColumnStat stat = PrestoFormatConvert(kvp.Value, currentTable.contents.rowCount);
                    Catalog.sysstat_.AddOrUpdate(currentTable.name, kvp.Key, stat);
                }
            }
        }
    }

    public class StatsSerializer
    {
        static public void SerializeAndWrite(Dictionary<string, ColumnStat> records, string stats_output_fn)
        {
            var serial_stats_fmt = JsonSerializer.Serialize<Dictionary<string, ColumnStat>>(records);

            File.WriteAllText(stats_output_fn, serial_stats_fmt);
        }
    }
}

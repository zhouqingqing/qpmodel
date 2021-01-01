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
        static ColumnStat PrestoFormatConvert(PrestoColumnStats stat_in, int nRows)
        {
            ColumnStat stat = new ColumnStat
            {
                nullfrac_ = (double)stat_in.nullsCount_ / (double)nRows,
                n_rows_ = (ulong)nRows,
                n_distinct_ = (ulong)stat_in.distinctValuesCount_,
                mcv_ = null,
                hist_ = null
            };

            return stat;
        }

        static public void ReadConvertPrestoStats(string stats_dir_fn)
        {
            string[] statFiles = Directory.GetFiles(stats_dir_fn);

            foreach (string statFn in statFiles)
            {
                PrestoTable currentTable = new PrestoTable(statFn)
                {
                    name = Path.GetFileNameWithoutExtension(statFn)
                };

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

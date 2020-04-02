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

using qpmodel;
using qpmodel.stat;


namespace statistics_fmt_cvnt
{
    public class tableStats
    {
        public float distinctValuesCount { get; set; }
        public int nullsCount { get; set; }
        public dynamic min { get; set; }
        public dynamic max { get; set; }
        public int? dataSize { get; set; }
    }

    public class TableContents
    {
        public int rowCount { get; set; }
        public Dictionary<string, tableStats> columns { get; set; }

        public TableContents()
        {
        }
    }

    public class Table
    {
        public string name;
        public TableContents contents;

        public Table(string newName)
        {
            name = newName;
        }

        public Table()
        {
        }
    }

    public class refmt_presto_stats
    {
        static ColumnStat presto_format_convert(tableStats stat_in, int nRows)
        {
            ColumnStat stat = new ColumnStat();

            stat.nullfrac_ = (double)stat_in.nullsCount / (double)nRows;
            stat.n_rows_ = nRows;
            stat.n_distinct_ = (long)stat_in.distinctValuesCount;
            stat.mcv_ = null;
            stat.hist_ = null;

            return stat;
        }

        static public void read_cnvt_presto_stats(string stats_dir_fn)
        {
            string[] statFiles = Directory.GetFiles(stats_dir_fn);

            foreach (string statFn in statFiles)
            {
                Table currentTable = new Table(statFn);
                currentTable.name = Path.GetFileNameWithoutExtension(statFn);

                string jsonStr = File.ReadAllText(statFn);
                string trimmedJsonStr = Regex.Replace(jsonStr, "\\n", "");
                trimmedJsonStr = Regex.Replace(trimmedJsonStr, "\\r", "");

                currentTable.contents = JsonSerializer.Deserialize<TableContents>(trimmedJsonStr);

                foreach (KeyValuePair<string, tableStats> kvp in currentTable.contents.columns)
                {
                    ColumnStat stat = presto_format_convert(kvp.Value, currentTable.contents.rowCount);
                    Catalog.sysstat_.AddOrUpdate(currentTable.name, kvp.Key, stat);
                }
            }
        }
    }

    public class serialize_stats
    {
        static public void seriaize_and_write(Dictionary<string, ColumnStat> records, string stats_output_fn)
        {
            var serial_stats_fmt = JsonSerializer.Serialize<Dictionary<string, ColumnStat>>(records);

            File.WriteAllText(stats_output_fn, serial_stats_fmt);
        }
    }
}

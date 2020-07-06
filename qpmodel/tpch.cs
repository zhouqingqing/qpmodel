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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

using qpmodel.sqlparser;
using qpmodel.logic;

namespace qpmodel.test
{
    public class Tpch
    {
        static public void CreateTables()
        {
            DropTables();

            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpch/sql_scripts";
            string filename = $@"{folder}/tpch.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }

        static public void DropTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpch/sql_scripts";
            string filename = $@"{folder}/DropTables.sql";
            var sql = File.ReadAllText(filename);

            SQLStatement.ExecSQLList(sql);
            System.GC.Collect();
        }
        static public void LoadTables(string subfolder)
        {
            string save_curdir = Directory.GetCurrentDirectory();
            string folder = $@"{save_curdir}/../../../../tpch/sql_scripts";

            Directory.SetCurrentDirectory(folder);
            var sql = File.ReadAllText($@"LoadTables-{subfolder}.sql");
            SQLStatement.ExecSQLList(sql);
            Directory.SetCurrentDirectory(save_curdir);
        }

        static public void CreateIndexes()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpch/sql_scripts";
            string filename = $@"{folder}/TableIndexes.sql";
            var sql = File.ReadAllText(filename);

            SQLStatement.ExecSQLList(sql);
        }

        static public void AnalyzeTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpch/sql_scripts";
            string filename = $@"{folder}/AnalyzeTables.sql";

            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }
    }

    public class Tpcds
    {
        static public void CreateTables()
        {
            DropTables();

            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpcds/sql_scripts";
            string filename = $@"{folder}/tpcds.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }
        static public void DropTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpcds/sql_scripts";
            string filename = $@"{folder}/DropTables.sql";
            var sql = File.ReadAllText(filename);

            SQLStatement.ExecSQLList(sql);
            System.GC.Collect();
        }
        static public void LoadTables(string subfolder)
        {
            string save_curdir = Directory.GetCurrentDirectory();
            string folder = $@"{save_curdir}/../../../../tpcds/sql_scripts";

            Directory.SetCurrentDirectory(folder);
            var sql = File.ReadAllText($@"LoadTables-{subfolder}.sql");
            SQLStatement.ExecSQLList(sql);
            Directory.SetCurrentDirectory(save_curdir);
        }

        static public void AnalyzeTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../tpcds/sql_scripts";
            string filename = $@"{folder}/AnalyzeTables.sql";

            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }
    }

    public class JOBench
    {
        static public void CreateTables()
        {
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}/../../../../jobench/sql_scripts";
            string filename = $@"{folder}/schema.sql";
            var sql = File.ReadAllText(filename);
            SQLStatement.ExecSQLList(sql);
        }

        static public void LoadTables(string subfolder)
        {
        }

        static public void AnalyzeTables()
        {
        }
    }
}

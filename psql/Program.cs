
using System;
using System.IO;

using adb.logic;

namespace psql
{
    public class QueryVerify
    {
        static bool resultVerify(string resultFn, string expectFn)
        {
            // read  strings from result file and expected file
            string expectText = File.ReadAllText(expectFn).Replace("\r\n", "\n");
            string resultText = File.ReadAllText(resultFn).Replace("\r\n", "\n");

            // compare the test result with the expected result
            if (string.Compare(resultText, expectText) != 0)
            {
                return false;
            }

            return true;
        }


        public string SQLQueryVerify(string sql_dir_fn, string write_dir_fn, string expect_dir_fn)
        {
            // get a list of sql query fine names from the sql directory
            string[] sqlFiles = Directory.GetFiles(sql_dir_fn);

            // execute the query in each file and and verify the result
            foreach (string sqlFn in sqlFiles)
            {
                // execute query
                var sql = File.ReadAllText(sqlFn);
                var test_result = SQLStatement.ExecSQLList(sql);

                // construct file name for result file and write result
                string f_name = Path.GetFileNameWithoutExtension(sqlFn);
                string write_fn = $@"{write_dir_fn}\{f_name}.txt";

                File.WriteAllText(write_fn, test_result);

                // construct file name of expected result
                string expect_fn = $@"{expect_dir_fn}\{f_name}.txt";

               // verify query result against the expected result
               if (!resultVerify(write_fn, expect_fn))
               {
                   return write_fn;
               }
            }
            return null;
        }

    }

    static class Program
    {
       static void Main()
        {
    
        }
    }
}


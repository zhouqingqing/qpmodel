
using System;
using System.IO;

using adb.logic;

namespace psql
{
    public class QueryVerify
    {
        static bool resultVerify(string resultFn, string expectFn)
        {
            // get result string length and make sure it's  
            // equal to the length of the expected result

            long result_len = new FileInfo(resultFn).Length;
            long expect_len = new FileInfo(expectFn).Length;

            if (result_len != expect_len)
                return false;

            // read  strings from result file and expected file
            string expectText = File.ReadAllText(expectFn);
            string resultText = File.ReadAllText(resultFn);

            // compare the test result with the expected result
            if (string.Compare(resultText, expectText) != 0)
            {
                return false;
            }

            return true;
        }


        public Boolean SQLQueryVerify(string sql_dir_fn, string write_dir_fn, string expect_dir_fn)
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
                   return false;
               }
            }
            return (true);
        }

    }

    static class Program
    {
       static void Main()
        {
    
        }
    }
}


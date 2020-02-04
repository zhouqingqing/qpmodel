using adb;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;

using adb.physic;
using adb.utils;
using adb.logic;
using adb.sqlparser;
using adb.optimizer;
using adb.test;
using adb.expr;
using adb.dml;



namespace psql
{
    class Program
    {
  
        static bool resultVerify(string resultStr, string expectFn)
        {
            // get result string length and make sure it's  
            // equal to the length of the expected result
            int length = resultStr.Length;

            if (length != (new FileInfo(expectFn).Length))
                return false;

            // open expect file and verify expected result
            using (FileStream stream1 = File.OpenRead(expectFn))
            {
                // read the expected result from file
                string text = File.ReadAllText(expectFn);
                //int text_sz = text.Length;

                // compare the test result with the expected result
                if (string.Compare(resultStr, text) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        
        static void SQLQueryVerify(string op_type, string sql_dir_fn, string write_dir_fn, string expect_dir_fn)
        {
            try
            {
                // get a list of sql query fine names from the sql directory
                DirectoryInfo sql_di = new DirectoryInfo(sql_dir_fn);
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

                    // if the op_type specified is not a simple write verify query
                    if (op_type != "write")
                    {
                        // construct file name of expected result
                        string expect_fn = $@"{expect_dir_fn}\{f_name}.txt";

                        // verify query result against the expected result
                        if (resultVerify(test_result, expect_fn))
                        {
                            Console.WriteLine($"Pass  {sqlFn}");
                        }
                        else
                        {
                            Console.WriteLine($"Fail  {sqlFn}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception caught: {e}");
            }
        }

        static void Usage()
        {
            Console.WriteLine("program [-type <verify> -exp_dir_name <expect dir name> | -type <write> |");
            Console.WriteLine("         -exp_dir_name <expect dir name>]");
            Console.WriteLine("        -sql_dir_name <SQL dir name -write_dir_name <write_dir_name>");

        }

        // input:
        // select * from a;
        // select * from a where a1>1;
        //
        // output:
        // 1,2,3,4
        // ...
        // 
        static void Main(string[] args)
        {
            string sql_dir_fn = "none";
            string write_dir_fn = "none";
            string expect_dir_fn = "none";
            string op_type = "none";

            //
            // read in command line parameters
            //

            if (args.Length < 5)
            {
                Usage();
                return;
            }
            for (int i = 0; i < args.Length; i++)
            {
                string dust = args[i];
                if (args[i] == "-sql_dir_name")
                {
                    ++i;
                    sql_dir_fn = args[i];
                }
                else if (args[i] == "-write_dir_name")
                {
                    ++i;
                    write_dir_fn = args[i];
                }
                else if (args[i] == "-exp_dir_name")
                {
                    ++i;
                    expect_dir_fn = args[i];
                }
                else if (args[i] == "-type")
                {
                    i++;
                    if (args[i] == "write" || args[i] == "verify")                    
                        op_type = args[i];
                    else
                    {
                        Console.WriteLine($"Error: Invalid operation type detected: {args[i]}");
                        Usage();
                        return;
                    }
                }  
            }

            // 
            // display command line parameters
            //
            Console.WriteLine("Parameters:");
            Console.WriteLine($"     op_type:          {op_type}");
            Console.WriteLine($"     sql directory:    {sql_dir_fn}");
            Console.WriteLine($"     write directory:  {write_dir_fn}");
            Console.WriteLine($"     expect directory: {expect_dir_fn}");

            Console.WriteLine("End Parameters\n\n");

            //
            // verify that parameters are valid
            //
            if ( (sql_dir_fn == "none" || write_dir_fn == "none") || ( (expect_dir_fn == "none") && 
                                                                       (op_type == "" || op_type == "verify") ) )                  
            {
                Usage();
                return;
            }

            //
            // verify that specified directories exist
            //
            if (!Directory.Exists(sql_dir_fn))
            {
                Console.WriteLine($@"*** Error: {sql_dir_fn} does not exist");
                return;
            }
            if (!Directory.Exists(write_dir_fn))
            {
                Console.WriteLine($@"*** Error: {write_dir_fn} does not exist");
                return;
            };


            //
            // execute SQL queries and verify results if requested to do so
            SQLQueryVerify(op_type, sql_dir_fn, write_dir_fn, expect_dir_fn);

            return;
        }
    }

}


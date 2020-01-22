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
            string path = @"C:\Users\dmclaugh\Desktop\Archive\tmp\Test.txt";
            //string sql = "select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;";
            string sql = "select a2 from a where a.a3 > (select min(b1*2) from b where b.b2 >= (select c2-1 from c where c.c2=b2) and b.b3 > ((select c2 from c where c.c2=b2)));";


            using (StreamWriter sw = new StreamWriter(path))
            {

                var results = SQLStatement.ExecSQL(sql, out string phyplan, out _);

             
                Console.WriteLine("******************************************")
                Console.WriteLine(phyplan);

                sw.WriteLine(sql);
                sw.WriteLine(phyplan);
            }
                    Console.WriteLine("\n\n\n Begin Test\n\n");

            using (StreamReader sr = new StreamReader(path))
            {
                while ((sql = sr.ReadLine()) != null)
                {
                    string answer = sr.ReadLine();

                    Console.WriteLine(sql);
                    Console.WriteLine(answer);
                    Console.WriteLine("\n\n\n");


                    var results = SQLStatement.ExecSQL(sql, out string phyplan, out _);

                    bool equal = String.Equals(answer, phyplan, StringComparison.InvariantCulture);
                    Console.WriteLine($"The two plans {(equal == true ? "are" : "are not")} the same.");


                    //sql = "select * from d where d1=1;";

                    //results = SQLStatement.ExecSQL(sql, out phyplan, out _);
                    //string banner = "*****Here Comes the Plan * ****";
                    //Console.WriteLine(banner);
                    //Console.WriteLine(sql);
                    //Console.WriteLine(phyplan);


                    //sw.WriteLine(sql);
                    //sw.WriteLine(phyplan);


                }
            }
        }
    }
}

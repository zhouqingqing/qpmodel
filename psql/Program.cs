using System;
using System.Diagnostics;

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
            string sql = "select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;select * from a where a1>1;";
            Console.WriteLine(sql);
            var results = SQLStatement.ExecSQL(sql, out string phyplan, out _);
            Console.WriteLine(phyplan);

            sql = "select * from d where d1=1;";
            Console.WriteLine(sql);
            results = SQLStatement.ExecSQL(sql, out phyplan, out _);
            Console.WriteLine(phyplan);

        }
    }
}

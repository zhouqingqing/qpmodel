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

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace qpmodel.unittest
{
    [TestClass]
    public class StringCompare
    {
        [TestMethod]
        public void TestStringCompare()
        {
            string sql = "create table str1(col1 char(20), col2 varchar(25), col3 int);";
            System.Collections.Generic.List<physic.Row> stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "create table str2(col1 char(25), col2 varchar(35), col3 int);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Hamilton', 'Brisbane', 101);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('pavillion', 'samsung', 11);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Blazer', 'Roger', 201);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('North Rim', 'South Bay', 221);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Logitech', 'Brookstone', 101);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Peru', 'Lama', 501);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Bolivia', 'Lama', 501);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('North Rim', 'Brookstone', 786);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('pavillion', 'Civilian', 786);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Quadruple', 'South Bay', 786);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Jalandhar', 'Beaverton', 601);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Mercury', 'Hercules', 117);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Maryland', 'Palm Beach', 219);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Nicholas', 'Simplex', 769);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Big Basin', 'Victoria', 139);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str1 values('Bolivia', 'South Bay', 801);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Peru', 'Quangos', 501)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Bolivia', 'Frenyando', 1067)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('North Rim', 'Danjamyla', 786)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('thathomadip', 'Beaverton', 876)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Palm Beach', 'Tholapza zuseri', 1786)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Kuymugian', 'Maryland', 7861)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('pendom dedsom', 'Civilian', 786)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Ablutomania', 'Nicholas', 6981)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('pavillion', 'Medallion', 9984)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Bettalian', 'Mandalorian', 1089)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Hercules', 'Radio City', 3012)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Nalaze Simirethy', 'Barcelona', 3012)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Via kawethibun', 'Jalandhar', 3421)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Shangrila', 'Minar Tirth', 4215)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Big Basin', 'Vishaishil', 1089)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Ashekkaza', 'Vishaishil', 1439)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Ashekkaza', 'Santa Rita', 2139)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "insert into str2 values('Kuymugian', 'Kudirosif', 7861)";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "select * from str1 where col1 = 'Hamilton' order by 1;";
            TU.ExecuteSQL(sql, "Hamilton,Brisbane,101");
            Assert.AreEqual("", TU.error_);

            sql = "select * from str1 where col1 >= 'Cashmere' and col2 <= 'Lama' order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(7, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Hamilton");
            Assert.AreEqual(stmtResult[6][0].ToString(), "Peru");

            sql = "select * from str1 where col2 > 'Lama' order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(8, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Big Basin");
            Assert.AreEqual(stmtResult[7][0].ToString(), "Quadruple");

            sql = "select * from str1 where 'Lama' < col2 order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(8, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Big Basin");
            Assert.AreEqual(stmtResult[7][0].ToString(), "Quadruple");

            sql = "select * from str1 where 'Mama' <= 'Lama' order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(0, stmtResult.Count);

            sql = "select * from str1 where 'Mama' >= 'Lama' order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(16, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Big Basin");
            Assert.AreEqual(stmtResult[15][0].ToString(), "Quadruple");

            sql = "select * from str1 where col1 between 'abba' and 'Dhaba' order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Big Basin");
            Assert.AreEqual(stmtResult[3][0].ToString(), "Bolivia");

            sql = "select col1 from str1 where col1 > 'Logic' group by col1 order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(8, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Logitech");
            Assert.AreEqual(stmtResult[7][0].ToString(), "Quadruple");

            sql = "select col1 from str1 where col1 > 'Logic' group by col1 having count(*) > 1 order by 1;";
            TU.ExecuteSQL(sql, "North Rim;pavillion");
            Assert.AreEqual("", TU.error_);

            sql = "select t1.col1, sum(t2.col3) from str1 t1, str2 t2 where t1.col1 = t2.col1 or t1.col2 = t2.col2 group by t1.col1 order by 1";
            TU.ExecuteSQL(sql, "Big Basin,1089;Bolivia,2134;Jalandhar,876;North Rim,1572;pavillion,20754;Peru,501");
            Assert.AreEqual("", TU.error_);

            sql = "select t1.col1, t2.col2, sum(t1.col3 + t2.col3) from str1 t1, str2 t2 where t1.col1 = t2.col1 and t1.col1 > 'Kaiser' and t2.col2 < 'Valhalla' group by t1.col1, t2.col2 order by 1;";
            TU.ExecuteSQL(sql, "North Rim,Danjamyla,2579;pavillion,Medallion,20765;Peru,Quangos,1002");
            Assert.AreEqual("", TU.error_);

            sql = "select t1.col1, count(t1.col1) from str1 t1, str2 t2 where t1.col1 <> t2.col1 and t1.col2 <> t2.col2 group by t1.col1 order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(13, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Big Basin");
            Assert.AreEqual(stmtResult[12][0].ToString(), "Quadruple");

            sql = "select t1.col2, count(t1.col2) from str1 t1, str2 t2 where t1.col1 <> t2.col1 and t1.col2 <> t2.col2 group by t1.col2 order by 1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(12, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Beaverton");
            Assert.AreEqual(stmtResult[11][0].ToString(), "Victoria");

            sql = "select col1 || col2 as col12, col1 || '_Suffix', 'Prefix_' || col2, 'Prefix_' || '_Suffix' from str1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(16, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "HamiltonBrisbane");
            Assert.AreEqual(stmtResult[15][0].ToString(), "BoliviaSouth Bay");

            sql = "select count(col1), count(col2), sum(col3) from str2;";
            TU.ExecuteSQL(sql, "18,18,57905");
            Assert.AreEqual("", TU.error_);
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);

            sql = "select sum(col1) from str1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.IsTrue(stmtResult[0].ToString().Contains("RimLogitechPeruBoliviaNorth"));

            /* Errors. */
#if false
            /* Runtime excptions. */
            sql = "select min(col1) from str1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("Operator '>' cannot be applied to"));

            sql = "select max(col2) from str1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("Operator '>' cannot be applied to"));

            sql = "select avg(col1) from str1;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("Operator '>' cannot be applied to"));
#endif

            sql = "select * from str1 where col1 > 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));

            sql = "select * from str2 where col2 < 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));

            sql = "select * from str2 where col2 = 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));

            sql = "select * from str1 where col2 <> 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));

            sql = "select * from str1 where col2 <= 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));

            sql = "select * from str2 where col2 >= 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("no implicit conversion of"));
        }
    }
}

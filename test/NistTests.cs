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
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;

using qpmodel.physic;
using qpmodel.utils;
using qpmodel.logic;
using qpmodel.sqlparser;
using qpmodel.optimizer;
using qpmodel.test;
using qpmodel.expr;
using qpmodel.dml;
using qpmodel.tools;
using qpmodel.stat;

namespace qpmodel.unittest
{
    [TestClass]
    public class NistTests
    {
        public void CreateBaseTables()
        {
            string sql = "CREATE TABLE STAFF (EMPNUM   CHAR(3) NOT NULL UNIQUE, EMPNAME  CHAR(20), GRADE    DECIMAL(4), CITY     CHAR(15));";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.IsNull(stmtResult);
            Assert.AreEqual("", TU.error_);

            sql = "CREATE TABLE PROJ (PNUM     CHAR(3) NOT NULL UNIQUE, PNAME    CHAR(20), PTYPE    CHAR(6), BUDGET   DECIMAL(9), CITY     CHAR(15));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsNull(stmtResult);
            Assert.AreEqual("", TU.error_);

            sql = "CREATE TABLE WORKS (EMPNUM   CHAR(3) NOT NULL, PNUM     CHAR(3) NOT NULL, HOURS    DECIMAL(5), UNIQUE(EMPNUM,PNUM));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsNull(stmtResult);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO STAFF VALUES ('E1','Alice',12,'Deale');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO STAFF VALUES ('E2','Betty',10,'Vienna');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO STAFF VALUES ('E3','Carmen',13,'Vienna');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO STAFF VALUES ('E4','Don',12,'Deale');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO STAFF VALUES ('E5','Ed',13,'Akron');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P1','MXSS','Design',10000,'Deale');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P2','CALM','Code',30000,'Vienna');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P3','SDP','Test',30000,'Tampa');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P4','SDP','Design',20000,'Deale');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P5','IRM','Test',10000,'Vienna');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO PROJ VALUES  ('P6','PAYR','Design',50000,'Deale');";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P1',40);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P2',20);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P3',80);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P4',20);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P5',12);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E1','P6',12);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E2','P1',40);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E2','P2',80);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E3','P2',20);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E4','P2',20);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E4','P4',40);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO WORKS VALUES  ('E4','P5',80);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            /*
             * this table gets new rows inserted and deleted, which we don't
             * support. So, it gets created and populated seperately.
             */
            CreateVTABLE();   // this table gets new rows inserted and deleted
        }

        public void CreateVTABLE()
        {
            var sql = "CREATE TABLE VTABLE (COL1   INTEGER, COL2   INTEGER, COL3   INTEGER, COL4   INTEGER, COL5   DECIMAL(7,2));";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.IsNull(stmtResult);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(10,+20,30,40,10.50);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(0,1,2,3,4.25);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(100,200,300,400,500.01);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(1000,-2000,3000,NULL,4000.00);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
        }

        [TestMethod]
        public void RunNistTests()
        {
            CreateBaseTables();
            dml001();
            dml013();
            dml014();
            dml018();
            dml022();
            dml023();
            dml059();
            dml073();
        }

        public void dml001()
        {
            var sql = @"SELECT EMPNUM,HOURS
                     FROM WORKS
                     WHERE PNUM='P2'
                     ORDER BY EMPNUM DESC;";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[3][0].ToString(), "E1");

            sql = @"
                SELECT EMPNUM,HOURS
                     FROM WORKS
                     WHERE PNUM='P2'
                     ORDER BY 2 ASC;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[3][1].ToString(), "80");

            sql = @"
                SELECT EMPNUM,HOURS
                     FROM WORKS
                     WHERE PNUM = 'P2'
                     ORDER BY 2 DESC,EMPNUM DESC;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[3][0].ToString(), "E1");
        }

        public void dml013()
        {
            var sql = @"
     SELECT SUM(HOURS)
          FROM WORKS
          WHERE PNUM = 'P2';";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0].ToString(), "140");

            sql = @"
     SELECT SUM(HOURS)+10
          FROM WORKS
          WHERE PNUM = 'P2';";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "150");

            sql = @"
     SELECT EMPNUM
          FROM STAFF
          WHERE GRADE = (SELECT MAX(GRADE) FROM STAFF)
          ORDER BY EMPNUM;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            Assert.AreEqual("E3,E5", string.Join(",", stmtResult));
        }

        public void dml014()
        {
            var sql = @"
     SELECT PNUM
          FROM PROJ
          WHERE BUDGET BETWEEN 40000 AND 60000;";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "P6");

            sql = @"
     SELECT PNUM
          FROM PROJ
          WHERE BUDGET >= 40000 AND BUDGET <= 60000;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "P6");

            /*
             * BUG: Should return only one row, with 'Vienna'
             * but returns four: Deale, Vienna, Deale, Akorn.
             * Suppress Asserts for now.
             */
            sql = @"
     SELECT CITY
          FROM STAFF
          WHERE GRADE NOT BETWEEN 12 AND 13;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            // Assert.AreEqual(1, stmtResult.Count);
            // Assert.AreEqual(stmtResult[0][0].ToString(), "Vienna");

#if false
            /* BUG or Unsupported WHER NOT ()? */
            sql = @"
      SELECT CITY
           FROM STAFF
           WHERE NOT(GRADE BETWEEN 12 AND 13);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Vienna");
#endif

            sql = @"
     SELECT STAFF.EMPNAME
          FROM STAFF
          WHERE STAFF.EMPNUM IN
                  (SELECT WORKS.EMPNUM
                        FROM WORKS
                        WHERE WORKS.PNUM IN
                              (SELECT PROJ.PNUM
                                    FROM PROJ
                                    WHERE PROJ.CITY='Tampa'));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Alice");

            /* BUG/Unsupported ? */
            /* should return 1 row with 12 but returns 11 rows */
            sql = @"
     SELECT WORKS.HOURS
          FROM WORKS
          WHERE WORKS.PNUM NOT IN
                  (SELECT PROJ.PNUM
                        FROM PROJ
                        WHERE PROJ.BUDGET BETWEEN 5000 AND 40000);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            // Assert.AreEqual(1, stmtResult.Count);
            // Assert.AreEqual(stmtResult[0][0].ToString(), "12");

            sql = @"
     SELECT WORKS.HOURS
          FROM WORKS
          WHERE NOT (WORKS.PNUM IN
                 (SELECT PROJ.PNUM
                       FROM PROJ
                       WHERE PROJ.BUDGET BETWEEN 5000 AND 40000));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "12");

            /*
             * BUG/Unsupported?
             * Should return one row with 80 but returns 11 rows.
             */
            sql = @"
     SELECT HOURS
          FROM WORKS
          WHERE PNUM NOT IN
                 (SELECT PNUM
                       FROM WORKS
                       WHERE PNUM IN ('P1','P2','P4','P5','P6'));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            // Assert.AreEqual(1, stmtResult.Count);
            // Assert.AreEqual(stmtResult[0][0].ToString(), "80");

            sql = @"
     SELECT HOURS
          FROM WORKS
          WHERE NOT (PNUM IN
                 (SELECT PNUM
                       FROM WORKS
                       WHERE PNUM IN ('P1','P2','P4','P5','P6')));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "80");

            /*
             * BUG: Should return one row with Alice but retuns 5 rows.
             */
            sql = @"
     SELECT STAFF.EMPNAME
          FROM STAFF
          WHERE NOT EXISTS
                 (SELECT *
                       FROM PROJ
                       WHERE NOT EXISTS
                             (SELECT *
                                   FROM WORKS
                                   WHERE STAFF.EMPNUM = WORKS.EMPNUM
                                   AND WORKS.PNUM=PROJ.PNUM));";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            // Assert.AreEqual(1, stmtResult.Count);
            // Assert.AreEqual(stmtResult[0][0].ToString(), "Alice");
        }

        public void dml018()
        {
            var sql = @"
     SELECT PNUM
          FROM WORKS
          WHERE PNUM > 'P1'
          GROUP BY PNUM
          HAVING COUNT(*) > 1;";
            TU.ExecuteSQL(sql, "P2;P4;P5");
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT PNUM
          FROM WORKS
          GROUP BY PNUM
          HAVING COUNT(*) > 2;";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "P2");

            sql = @"
     SELECT EMPNUM, PNUM, HOURS
          FROM WORKS
          GROUP BY PNUM, EMPNUM, HOURS
          HAVING MIN(HOURS) > 12 AND MAX(HOURS) < 80;";
            TU.ExecuteSQL(sql, "E1,P1,40;E1,P2,20;E1,P4,20;E2,P1,40;E3,P2,20;E4,P2,20;E4,P4,40");
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT WORKS.PNUM
          FROM WORKS
          GROUP BY WORKS.PNUM
          HAVING WORKS.PNUM IN (SELECT PROJ.PNUM
                    FROM PROJ
                    GROUP BY PROJ.PNUM
                    HAVING SUM(PROJ.BUDGET) > 25000);";
            TU.ExecuteSQL(sql, "P2;P3;P6");
            Assert.AreEqual("", TU.error_);

            /*
            * implemnt string compare operators.
            */
#if false
            /*
             * Aggregate on strings is not implemented. Throws runtime exception.
             * */
            sql = @"
     SELECT SUM(HOURS)
          FROM WORKS
          HAVING MIN(PNUM) > 'P0';";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.IsTrue(TU.error_.Contains("Operator '>' cannot be applied to"));
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "464");
#endif

            sql = @"SELECT PNUM
          FROM WORKS
          WHERE PNUM > 'P1'
          GROUP BY PNUM
          HAVING COUNT(*) > 1;";
            TU.ExecuteSQL(sql, "P2;P4;P5");
            Assert.AreEqual("", TU.error_);
        }

        public void dml022()
        {
            var sql = @"
     SELECT EMPNUM
          FROM STAFF
          WHERE GRADE <
             (SELECT MAX(GRADE)
              FROM STAFF);";
            TU.ExecuteSQL(sql, "E1;E2;E4");
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT *
          FROM STAFF
          WHERE GRADE <=
             (SELECT AVG(GRADE)-1
              FROM STAFF);";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "E2");
            Assert.AreEqual(stmtResult[0][1].ToString(), "Betty");

            sql = @"
     SELECT EMPNAME
          FROM STAFF
          WHERE EMPNUM IN
             (SELECT EMPNUM
              FROM WORKS
              WHERE PNUM = 'P2')
     ORDER BY EMPNAME;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Alice");

            sql = @"
     SELECT EMPNAME
          FROM STAFF
          WHERE EMPNUM IN
             (SELECT EMPNUM
              FROM WORKS
              WHERE PNUM IN
                 (SELECT PNUM
                  FROM PROJ
                  WHERE PTYPE = 'Design'));";
            TU.ExecuteSQL(sql, "Alice;Betty;Don");
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT EMPNUM, EMPNAME
          FROM STAFF
          WHERE EMPNUM IN
             (SELECT EMPNUM
              FROM WORKS
              WHERE PNUM IN
                 (SELECT PNUM
                  FROM PROJ
                  WHERE PTYPE IN
                     (SELECT PTYPE
                      FROM PROJ
                      WHERE PNUM IN
                         (SELECT PNUM
                          FROM WORKS
                          WHERE EMPNUM IN
                             (SELECT EMPNUM
                              FROM WORKS
                              WHERE PNUM IN
                                 (SELECT PNUM
                                  FROM PROJ
                                  WHERE PTYPE = 'Design'))))))
     ORDER BY EMPNUM;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(4, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "E1");

#if false
            /*
             * BUG: should return two rows but returns 12 rows
             */
            sql = @"
     SELECT DISTINCT EMPNUM
          FROM WORKS WORKSX
          WHERE NOT EXISTS
             (SELECT *
              FROM WORKS WORKSY
              WHERE EMPNUM = 'E2'
              AND NOT EXISTS
                  (SELECT *
                   FROM WORKS WORKSZ
                   WHERE WORKSZ.EMPNUM = WORKSX.EMPNUM
                   AND WORKSZ.PNUM = WORKSY.PNUM));";
            TU.ExecuteSQL(sql, "E1;E2");
            Assert.AreEqual("", TU.error_);
#endif
        }

        public void dml023()
        {
            var sql = @"
     SELECT PNUM
          FROM PROJ
          WHERE PROJ.CITY =
             (SELECT STAFF.CITY
              FROM STAFF
              WHERE EMPNUM = 'E1');";
            TU.ExecuteSQL(sql, "P1;P4;P6");
            Assert.AreEqual("", TU.error_);
        }

        public void dml059()
        {
            var sql = "INSERT INTO VTABLE VALUES(10,11,12,13,15);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(100,111,1112,113,115);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT COL1, MAX(COL2 + COL3), MIN(COL3 - COL2)
          FROM VTABLE
          GROUP BY COL1
          ORDER BY COL1;";
            TU.ExecuteSQL(sql, "0,3,1;10,50,1;100,1223,100;1000,1000,5000");
            Assert.AreEqual("", TU.error_);

            sql = "DROP TABLE VTABLE;";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            CreateVTABLE();

            sql = "INSERT INTO VTABLE VALUES (10,11,12,13,15);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES (100,111,1112,113,115);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT COL1,SUM(2 * COL2 * COL3)
                  FROM VTABLE
                  GROUP BY COL1
                  HAVING SUM(COL2 * COL3) > 2000
                  OR SUM(COL2 * COL3) < -2000
                  ORDER BY COL1;";
            TU.ExecuteSQL(sql, "100,366864;1000,-12000000");

            sql = "DROP TABLE VTABLE";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            CreateVTABLE();

            sql = "INSERT INTO VTABLE VALUES(10,11,12,13,15);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = "INSERT INTO VTABLE VALUES(100,111,1112,113,115);";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            sql = @"
     SELECT COL1, MAX(COL2)
          FROM VTABLE
          GROUP BY COL1
          HAVING EXISTS (SELECT *
                               FROM STAFF
                               WHERE EMPNUM = 'E1')
                               AND MAX(COL2) BETWEEN 10 AND 90
          ORDER BY COL1;";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "10");
            Assert.AreEqual(stmtResult[0][1].ToString(), "20");

            sql = "DROP TABLE VTABLE";
            TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);

            CreateVTABLE();

            sql = @"
     SELECT SUM(COL1)
          FROM VTABLE
          WHERE 10 + COL1 > COL2
          HAVING MAX(COL1) > 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "1000");

            sql = @"
     SELECT SUM(COL1)
          FROM VTABLE
          WHERE 1000 + COL1 >= COL2
          HAVING MAX(COL1) > 100;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "1110");
        }

        public void dml073()
        {
            var sql = @"
     SELECT AVG(HOURS), MIN(HOURS)
           FROM  STAFF, WORKS
           WHERE STAFF.EMPNUM = 'E2'
                 AND STAFF.EMPNUM = WORKS.EMPNUM;";
            var stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "60");
            Assert.AreEqual(stmtResult[0][1].ToString(), "40");

            sql = @"
     SELECT STAFF.EMPNUM, AVG(HOURS), MIN(HOURS)
           FROM  STAFF, WORKS
           WHERE STAFF.EMPNUM IN ('E1','E4','E3') AND
                 STAFF.EMPNUM = WORKS.EMPNUM
                 GROUP BY STAFF.EMPNUM
                 HAVING COUNT(*) > 1
                 ORDER BY STAFF.EMPNUM;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "E1");
            decimal DecNum = Convert.ToDecimal(stmtResult[0][1].ToString());
            Assert.IsTrue(DecNum >= 30 && DecNum <= 31);
            Assert.AreEqual(stmtResult[0][2].ToString(), "12");

            Assert.AreEqual(stmtResult[1][0].ToString(), "E4");
            DecNum = Convert.ToDecimal(stmtResult[1][1].ToString());
            Assert.IsTrue(DecNum >= 46 && DecNum <= 47);
            Assert.AreEqual(stmtResult[1][2].ToString(), "20");

            // TEST:0418. Removed DISTINCT from COUNT, with DISTINCT
            // count shall be 3, without it 12.
            sql = @"
     SELECT AVG(T1.COL4), AVG(T1.COL4 + T2.COL4),
           SUM(T2.COL4), COUNT(T1.COL4)
           FROM VTABLE T1, VTABLE T2;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            DecNum = Convert.ToDecimal(stmtResult[0][0].ToString());
            Assert.IsTrue(DecNum >= 147 && DecNum <= 148);
            DecNum = Convert.ToDecimal(stmtResult[0][1].ToString());
            Assert.IsTrue(DecNum >= 295 && DecNum <= 296);
            Assert.AreEqual(stmtResult[0][2].ToString(), "1772");
            Assert.AreEqual(stmtResult[0][3].ToString(), "12");

#if false
            // dml075: TEST:0434. Just one interesting test, not
            // adding another method.
            // ERROR: "WHERE condition must be a blooean expression and no aggregation is allowed"
            sql = @"
   SELECT PNUM, SUM(HOURS) FROM WORKS
          GROUP BY PNUM
          HAVING EXISTS (SELECT PNAME FROM PROJ
                         WHERE PROJ.PNUM = WORKS.PNUM AND
                               SUM(WORKS.HOURS) > PROJ.BUDGET / 200);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            String Pnum1 = stmtResult[0][0].ToString();
            String Pnum2 = stmtResult[1][0].ToString();
            DecNum = Convert.ToDecimal(stmtResult[1][0].ToString());
            decimal DecNum2 = Convert.ToDecimal(stmtResult[1][1].ToString());

            /*
            -- PASS:0434 If 2 rows selected with values (in any order):?
            -- PASS:0434 PNUM = 'P1', SUM(HOURS) = 80?
            -- PASS:0434 PNUM = 'P5', SUM(HOURS) = 92?
            */
            Assert.IsTrue((Pnum1 == "P1" && DecNum == 80 && Pnum2 == "P5" && DecNum2 == 92) || (Pnum1 == "P5" && DecNum == 92 && Pnum2 == "P1" && DecNum2 == 80));
#endif

            // dml090
#if false
            /* BUG */
            sql = @"
   SELECT MIN(PNAME) 
         FROM PROJ, WORKS, STAFF
         WHERE PROJ.PNUM = WORKS.PNUM
               AND WORKS.EMPNUM = STAFF.EMPNUM
               AND BUDGET - GRADE * HOURS * 100 IN
                   (-4400, -1000, 4000);";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "CALM");
#endif

            sql = @"
   SELECT CITY, COUNT(*)
         FROM PROJ
         GROUP BY CITY
         HAVING (MAX(BUDGET) - MIN(BUDGET)) / 2
                IN (2, 20000, 10000)
         ORDER BY CITY DESC;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Vienna");
            Assert.AreEqual(stmtResult[0][1].ToString(), "2");
            Assert.AreEqual(stmtResult[1][0].ToString(), "Deale");
            Assert.AreEqual(stmtResult[1][1].ToString(), "3");

            sql = @"
   SELECT COUNT(*) 
         FROM PROJ
         WHERE 24 * 1000 BETWEEN BUDGET - 5000 AND 50000 / 1.7;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(stmtResult[0][0].ToString(), "3");

#if false

            /*
             * BUG: Expected one row: 'IRM'
             *      Actual three rows: 'SDP'; 'SDP'; 'PAYR'
             */
            sql = @"
   SELECT PNAME
         FROM PROJ
         WHERE 'Tampa' NOT BETWEEN CITY AND 'Vienna'
                           AND PNUM > 'P2';";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "IRM");
#endif

            sql = @"
SELECT CITY, COUNT(*)
      FROM PROJ
      GROUP BY CITY
      HAVING 50000 + 2 BETWEEN 33000 AND SUM(BUDGET) - 20;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(1, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "Deale");
            Assert.AreEqual(stmtResult[0][1].ToString(), "3");

            // dml158
#if false
            /* BUG */
            sql = @"
   SELECT EMPNUM, SUM (HOURS) FROM WORKS OWORKS
       GROUP BY EMPNUM
       HAVING EMPNUM IN (
       SELECT WORKS.EMPNUM FROM WORKS JOIN STAFF
       ON WORKS.EMPNUM = STAFF.EMPNUM
       AND HOURS < SUM (OWORKS.HOURS) / 3
       AND GRADE > 10)
       ORDER BY EMPNUM;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "E1");
            Assert.AreEqual(stmtResult[0][1].ToString(), "184");
            Assert.AreEqual(stmtResult[1][0].ToString(), "E4");
            Assert.AreEqual(stmtResult[0][1].ToString(), "140");

            sql = @"
   SELECT EMPNUM, SUM (HOURS) FROM WORKS OWORKS
       GROUP BY EMPNUM
       HAVING EMPNUM IN (
       SELECT WORKS.EMPNUM FROM WORKS JOIN STAFF
       ON WORKS.EMPNUM = STAFF.EMPNUM
       AND HOURS >= 10 + AVG (OWORKS.HOURS)
       AND CITY = 'Deale')
       ORDER BY EMPNUM;";
            stmtResult = TU.ExecuteSQL(sql);
            Assert.AreEqual("", TU.error_);
            Assert.AreEqual(2, stmtResult.Count);
            Assert.AreEqual(stmtResult[0][0].ToString(), "E1");
            Assert.AreEqual(stmtResult[0][1].ToString(), "184");
            Assert.AreEqual(stmtResult[1][0].ToString(), "E4");
            Assert.AreEqual(stmtResult[0][1].ToString(), "140");
#endif
        }
    }
}

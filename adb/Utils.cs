using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics;

namespace adb
{
    static public class Utils
    {
        // this is shortcut for unhandled conditions - they shall be translated to 
        // related exceptional handling code later
        //
        public static void Checks(bool cond) => Debug.Assert(cond);
        public static void Assumes(bool cond) => Debug.Assert(cond);

        // a contains b?
        public static bool ListAContainsB<T>(List<T> a, List<T> b) => !b.Except(a).Any();

        public static void ReadCSVLine(string filepath, Action<string[]> action)
        {
            using (TextFieldParser parser = new TextFieldParser(filepath))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                while (!parser.EndOfData)
                {
                    //Processing row
                    string[] fields = parser.ReadFields();
                    action(fields);
                }
            }
        }
    }
}

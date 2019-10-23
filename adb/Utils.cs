using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace adb
{
    public static class Utils
    {
        internal static string Tabs(int depth) => new string(' ', depth * 2);

        // this is shortcut for unhandled conditions - they shall be translated to 
        // related exceptional handling code later
        //
        public static void Checks(bool cond) => Debug.Assert(cond);
        public static void Assumes(bool cond) => Debug.Assert(cond);
        public static void Checks(bool cond, string message) => Debug.Assert(cond, message);
        public static void Assumes(bool cond, string message) => Debug.Assert(cond, message);

        // a contains b?
        public static bool ListAContainsB<T>(List<T> a, List<T> b) => !b.Except(a).Any();

        // for each element in @source, if there is a matching k in @target of its sub expression, 
        // replace that element as ExprRef(k, index_of_k_in_target)
        //
        public static List<Expr> SearchReplace(List<Expr> source, List<Expr> target) {
            var r = new List<Expr>();
            source.ForEach(x =>
            {
                for (int i = 0; i < target.Count; i++)
                {
                    var e = target[i];
                    r.Add(x.SearchReplace(e, new ExprRef(e, i)));
                }
            });

            Debug.Assert(r.Count == source.Count);
            return r;
        }

        public static void ReadCsvLine(string filepath, Action<string[]> action)
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

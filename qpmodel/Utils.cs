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
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualBasic.FileIO;
using System.Text.RegularExpressions;

using qpmodel.expr;

namespace qpmodel.utils
{
    class ObjectID
    {
        static int id_ = 0;

        internal static int NewId() { return ++id_; }
    }

    // A generic nary-tree node
    //   This serves as basis for both expression and query tree node
    //
    public class TreeNode<T> where T : TreeNode<T>
    {
        // unique identifier
        internal string _ = "uninitialized";
        internal string clone_ = "notclone";
        public bool IsCloneCopy() => clone_ != "notclone";

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public List<T> children_ = new List<T>();
        public bool IsLeaf() => children_.Count == 0;

        // shortcut for conventional names
        public T child_() { Debug.Assert(children_.Count == 1); return children_[0]; }
        public T lchild_() { Debug.Assert(children_.Count == 2); return children_[0]; }
        public T rchild_() { Debug.Assert(children_.Count == 2); return children_[1]; }

        public TreeNode()
        {
            _ = $"{ObjectID.NewId()}";
        }

        // traversal pattern FOR EACH
        public void VisitEachT<T1>(Action<T1> callback) where T1 : T
        {
            if (this is T1)
                callback(this as T1);
            foreach (var v in children_)
                v.VisitEachT<T1>(callback);
        }
        public void VisitEach(Action<T> callback)
              => VisitEachT<T>(callback);

        // FOR EACH with parent-child relationship
        //   can also skip certain parent type and its children recursively
        //
        public void VisitEach(Action<T, int, T> callback, Type skipParentType = null)
        {
            void visitParentAndChildren(T parent,
                        Action<T, int, T> callback, Type skipParentType = null)
            {
                if (parent.GetType() == skipParentType)
                    return;

                if (parent == this)
                    callback(null, -1, (T)this);
                for (int i = 0; i < parent.children_.Count; i++)
                {
                    var child = parent.children_[i];
                    callback(parent, i, child);
                    visitParentAndChildren(child, callback, skipParentType);
                }
            }

            visitParentAndChildren((T)this, callback, skipParentType);
        }

        // traversal pattern EXISTS
        //  if any visit returns a true, stop recursion. So if you want to
        //  visit all nodes regardless, use TraverseEachNode(). 
        // 
        public bool VisitEachExists(Func<T, bool> callback, List<Type> excluding = null)
        {
            if (excluding?.Contains(GetType()) ?? false)
                return false;

            bool exists = callback((T)this);
            if (!exists)
            {
                foreach (var c in children_)
                    if (c.VisitEachExists(callback, excluding))
                        return true;
            }
            return exists;
        }

        public void VisitEachIgnore<T1, T2>(Action<T2> callback) where T2 : TreeNode<T>
        {
            if (!(this is T1))
            {
                if (this is T2)
                    callback(this as T2);
                foreach (var v in children_)
                    v.VisitEachIgnore<T1, T2>(callback);
            }
        }

        // lookup all T1 types in the tree and return the parent-target relationship
        public int FindNodeTypeMatch<T1>(List<T> parents,
            List<int> childIndex, List<T1> targets, Type skipParentType = null) where T1 : TreeNode<T>
        {
            VisitEach((parent, index, child) =>
            {
                if (child is T1 ct)
                {
                    parents?.Add((T)parent);
                    childIndex?.Add(index);
                    targets.Add(ct);

                    // verify the parent-child relationship
                    Debug.Assert(parent is null || parent.children_[index] == child);
                }
            }, skipParentType);

            return targets.Count;
        }

        public int FindNodeTypeMatch<T1>(List<T1> targets) where T1 : TreeNode<T> => FindNodeTypeMatch<T1>(null, null, targets);
        public int CountNodeTypeMatch<T1>() where T1 : TreeNode<T> => FindNodeTypeMatch<T1>(new List<T1>());

        // search @target and replace with @replacement
        public T SearchAndReplace<T1>(T1 target, T replacement) where T1 : T
        {
            bool checkfn(T e) => target == e;
            T replacefn(T e) => replacement;
            return SearchAndReplace<T>(checkfn, replacefn);
        }

        // search any with type @T1 and replace with replacefn(T1)
        public T SearchAndReplace<T1>(Func<T1, T> replacefn) where T1 : T
        {
            bool checkfn(T e) => e is T1;
            return SearchAndReplace<T1>(checkfn, replacefn);
        }

        // generic form of search with condition @checkfn and replace with @replacefn
        public T SearchAndReplace<T1>(Func<T, bool> checkfn, Func<T1, T> replacefn) where T1 : T
        {
            if (checkfn((T)this))
                return replacefn((T1)this);
            else
            {
                for (int i = 0; i < children_.Count; i++)
                {
                    var child = children_[i];
                    children_[i] = child.SearchAndReplace(checkfn, replacefn);
                }
                return (T)this;
            }
        }

        // clone
        public virtual T Clone()
        {
            // clone object have same ID but different cloneID
            var n = (T)MemberwiseClone();
            n.clone_ = $"{ObjectID.NewId()}";

            n.children_ = new List<T>();
            children_.ForEach(x => n.children_.Add(x.Clone()));
            Debug.Assert(Equals(n));
            Debug.Assert(n.IsCloneCopy());
            return n;
        }
    }

    public static class Utils
    {
        internal static string Spaces(int depth) => new string(' ', depth * 2);

        public static void Assumes(bool cond, string message = null)
        {
            if (!cond)
                throw new NotImplementedException(message);
        }

        public static string ToLower(this bool b) => b.ToString().ToLower();

        // a contains b?
        public static bool ContainsList<T>(this List<T> a, List<T> b) => !b.Except(a).Any();

        // order insensitive
        //   if you need the list to be order sensitive compared, do it in Equals()
        public static int ListHashCode<T>(this List<T> l)
        {
            int hash = 0;
            if (l != null)
                l.ForEach(x => hash ^= x.GetHashCode());
            return hash;
        }


        // Do both the lists contain same elements regardless of odrer?
        public static bool OrderlessEqual<T>(List<T> left, List<T> right)
            => left.ContainsList(right) && right.ContainsList(left);

        public static string RemoveStringQuotes(this string str)
        {
            Debug.Assert(str[0] == '\'' && str[str.Length - 1] == '\'');
            var dequote = str.Substring(1, str.Length - 2);
            return dequote;
        }

        // postgreSQL dont support [A-Z]
        public static bool StringLike(this string s, string pattern)
        {
            string regpattern="";
            regpattern = pattern;
            if (!pattern.Contains("%"))
            {
                regpattern = "^" + pattern + "$";
            }
            if (Regex.IsMatch(pattern, "[^%]+%"))
            {
                regpattern = "^" + regpattern;
            }else if (Regex.IsMatch(pattern, "%[^%]+"))
            {
                regpattern = regpattern + "$";
            }
            regpattern =regpattern.Replace("%", ".*");
            regpattern = regpattern.Replace("_", ".{1}");
            return Regex.IsMatch(s, regpattern);
        }

        // a[0]+b[1] => a+b 
        public static string RemovePositions(this string r)
        {
            do
            {
                int start = r.IndexOf('[');
                if (start == -1)
                    break;
                int end = r.IndexOf(']');
                Debug.Assert(end != -1);
                var middle = r.Substring(start + 1, end - start - 1);
                Debug.Assert(int.TryParse(middle, out int result));
                r = r.Replace($"[{middle}]", "");
            } while (r.Length > 0);
            return r;
        }

        public static void ReadCsvLine(string filepath, Action<string[]> action)
        {
            using var parser = new TextFieldParser(filepath);
            int recordCounter = 0;
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters("|");
            while (!parser.EndOfData)
            {
                try
                {
                    recordCounter++;

                    // Processing row
                    string[] fields = parser.ReadFields();
                    if (fields[fields.Length - 1].Equals(""))
                        Array.Resize(ref fields, fields.Length - 1);
                    action(fields);
                }
                catch (Exception e)
                {
                    string fname = Path.GetFileNameWithoutExtension(filepath);
                    Console.WriteLine($"Error Parsing record: {recordCounter} of file: {fname}");
                    Console.WriteLine($"Error was: {e.Message}");
                    Console.WriteLine("+++ StackTrace +++");
                    Console.Error.WriteLine(e.StackTrace);
                    throw e;
                };
            }
        }
        public static string normalizeName(string name) => name.StartsWith('"') ? name : name.ToLower();
        public static int mod(int a, int b) => (a % b + b) % b; // ensure positive
    }
}

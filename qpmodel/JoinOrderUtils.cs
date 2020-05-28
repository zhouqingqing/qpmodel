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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;
using System.Diagnostics;
using qpmodel.optimizer.test;

using qpmodel.expr;
using qpmodel.logic;
using qpmodel.utils;

using BitVector = System.Int64;
using LogicSignature = System.Int64;

namespace qpmodel.optimizer.test
{
    static class Space
    {
        // total number of generated trees for general graph with CP is
        //     n!C(n-1) 
        //   = (2n-2)!/(n-1)! 
        //   = (2n-2)(2n-3)...(n)
        static internal BigInteger Count_General_Bushy_CP(int n)
        {
            BigInteger r = 1;
            for (var l = n; l <= 2 * n - 2; l++)
                r *= l;
            return r;
        }
    }

    abstract class GraphClass
    {
        // return T1, T2, ..., Tn
        protected string[] GenerateTables(int n)
        {
            string[] tables = new string[n];
            for (int i = 1; i <= n; i++)
            {
                string t = "T" + i;
                tables[i - 1] = t;
            }

            return tables;
        }

        // return fisher_yates permutation of 1..n
        protected int[] fisher_yates_shuffle_array(int n)
        {
            var rand = new Random();
            var dstarray = Enumerable.Range(1, n).ToArray<int>();

            for (var i = 0; i < n - 1; i++)
            {
                var r = rand.Next(0, n); // [0, n-1]
                var tmp = dstarray[r];
                dstarray[r] = dstarray[n - r - 1];
                dstarray[n - r - 1] = tmp;
            }
            return dstarray;
        }
        protected Stack<int> fisher_yates_shuffle_stack(int n)
        {
            Stack<int> dst = new Stack<int>();
            int[] dstarray = fisher_yates_shuffle_array(n);
            for (var i = 0; i < n; i++)
                dst.Push(dstarray[i]);
            return dst;
        }

        // all QueryGraph sub-class go through this test
        static protected void DoTest(GraphClass gclass)
        {
            JoinGraph graph;

            graph = gclass.RandomGenerate(1);
            Console.WriteLine(graph);
            graph = gclass.RandomGenerate(5);
            Console.WriteLine(graph);
            graph = gclass.RandomGenerate(10);
            Console.WriteLine(graph);
            graph = gclass.RandomGenerate(50);
            Console.WriteLine(graph);
        }
        internal abstract JoinGraph RandomGenerate(int n);
    }

    // a chain graph is a chain with (n, n-1). Example:
    //    A - B - C - D - E
    //
    class ClassChain : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            var nedges = n - 1;
            var tables = GenerateTables(n);
            var order = fisher_yates_shuffle_array(n);
            string[] joins = new string[nedges];
            for (int i = 0; i < n - 1; i++)
            {
                joins[i] = string.Format("T{0}*T{1}", order[i], order[i + 1]);
            }

            return new JoinGraph(tables, joins);
        }

        internal static void Test()
        {
            DoTest(new ClassChain());
        }
    }

    // a star graph is a center node and all others connected to it with (n, n -1). Example:
    //   E -  A - B
    //        |\ C
    //        D 
    class ClassStar : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            var nedges = n - 1;
            var tables = GenerateTables(n);
            string[] joins = new string[nedges];
            int center = (new Random()).Next(1, n + 1); // [1, n]
            for (int i = 1, e = 0; i <= n; i++)
            {
                if (i != center)
                    joins[e++] = string.Format("T{0}*T{1}", center, i);
            }

            return new JoinGraph(tables, joins);
        }

        internal static void Test()
        {
            DoTest(new ClassStar());
        }
    }

    // a cycle graph is a cycle with (n, n) where n >=3. Example:
    //    A - B - C - D
    //    |___________|
    //
    class ClassCycle : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            bool degenerated = n < 3;
            var nedges = degenerated ? n - 1 : n;
            var tables = GenerateTables(n);
            var order = fisher_yates_shuffle_array(n);
            string[] joins = new string[nedges];
            for (int i = 0; i < n - 1; i++)
            {
                joins[i] = string.Format("T{0}*T{1}", order[i], order[i + 1]);
            }
            if (!degenerated)
                joins[n - 1] = string.Format("T{0}*T{1}", order[0], order[n - 1]);

            return new JoinGraph(tables, joins);
        }

        internal static void Test()
        {
            DoTest(new ClassCycle());
        }
    }

    // a tree graph is a tree with (n, n-1). Example:
    //     A 
    //   / | \
    //  B  C  D - E
    //
    class ClassTree : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            var nedges = n - 1;
            var tables = GenerateTables(n);
            string[] joins = new string[nedges];

            // https://nokyotsu.com/qscripts/2008/05/generating-random-trees-and-connected.html
            // dst := random permutation of all nodes;
            // src.push(dst.pop()); % picks the root
            // while (!dst.empty())
            //      a:= random element from src;
            //      b:= dst.pop();
            //      add the edge(a, b)
            //      src.push(b);
            //
            var rand = new Random();
            Stack<int> dst = fisher_yates_shuffle_stack(n);
            Stack<int> src = new Stack<int>();
            src.Push(dst.Pop());
            int e = 0;
            while (dst.Count != 0)
            {
                int a = src.ToArray()[rand.Next(0, (int)src.Count)];
                int b = dst.Pop();
                joins[e++] = string.Format("T{0}*T{1}", a, b);
                src.Push(b);
            }

            Debug.Assert(e == nedges);
            return new JoinGraph(tables, joins);
        }

        internal static void Test()
        {
            DoTest(new ClassTree());
        }
    }

    // a clique graph is a fully connected graph with (n, n*(n-1)/2). Example:
    //    A - B
    //    | x | 
    //    C - D
    //
    class ClassClique : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            int nedges = n * (n - 1) / 2; // C(n,2)
            var tables = GenerateTables(n);
            string[] joins = new string[nedges];
            for (int i = 0, e = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    joins[e++] = string.Format("T{0}*T{1}", i + 1, j + 1);
                }
            }

            return new JoinGraph(tables, joins);
        }
        internal static void Test()
        {
            DoTest(new ClassClique());
        }
    }

    // a random graph is a combination of previous standard classes with (n, [n-1, n*(n-1)/2]). Example:
    //    A - B
    //    | x   
    //    C - D -E
    //         \ F
    class ClassRandom : GraphClass
    {
        internal override JoinGraph RandomGenerate(int n)
        {
            var rand = new Random();
            int nedges = rand.Next(n - 1, n * (n - 1) / 2 + 1);

            // first construct a random tree, then random add rest edges
            JoinGraph tree = (new ClassTree()).RandomGenerate(n);
            var tables = tree.vertices_.Select(x => (x as LogicScanTable).tabref_.alias_).ToArray();
            var joins = tree.preds_;
            for (int e = tree.preds_.Count; e < nedges;)
            {
                int i1 = rand.Next(0, n);
                int i2 = rand.Next(0, n);

                // no duplicates or self-join
                if (i1 != i2)
                {
                    string j1 = string.Format("{0}*{1}", tree.vertices_[i1], tree.vertices_[i2]);
                    var join1 = JoinGraph.dbg_JoinStringToExpr(tables, j1);
                    string j2 = string.Format("{0}*{1}", tree.vertices_[i2], tree.vertices_[i1]);
                    var join2 = JoinGraph.dbg_JoinStringToExpr(tables, j2);
                    if (!joins.Contains(join1) && !joins.Contains(join2))
                    {
                        joins.Add(join1);
                        e++;
                    }
                }
            }

            return new JoinGraph(tables.Select(x => new LogicScanTable(new BaseTableRef(x)) as LogicNode).ToList(), joins);
        }

        internal static void Test()
        {
            DoTest(new ClassRandom());
        }
    }
}

namespace qpmodel.optimizer
{
    static public class SetOp
    {
        static internal BitVector EmptySet { get { return 0; } }

        static internal BitVector SingletonSet(int table)
        {
            BitVector S = 1 << table;
            Debug.Assert(S != EmptySet);
            return S;
        }

        static internal string ToString(BitVector S)
        {
            string r = "";
            foreach (var t in SetOp.TablesAscending(S))
                r += t + ", ";
            return r;
        }

        // return number of bits set in given n
        static internal int CountSetBits(BitVector S)
        {
            int count = 0;
            while (S > 0)
            {
                count += (int)(S & 1);
                S >>= 1;
            }

            Debug.Assert(count == 0 || S == EmptySet);
            return count;
        }

        // returns interact of set S1 and S2
        static internal BitVector Intersect(BitVector S1, BitVector S2) => S1 & S2;
        // returns union of set S1 and S2
        static internal BitVector Union(BitVector S1, BitVector S2) => S1 | S2;
        // returns S1 \ S2
        //   case 1: if S1 is a cover set of S2 (like genereated by VancePartition), 
        //        eg. S1: 1001101 S2: 1000100 then S1\S2 = S1-S2 = 0001001
        //        This is implmeneted by CoveredSubstract(). We need it by original Vance algorithm.
        //
        //   case 2: general case, where S2 may contain elements S1 does not contain
        //        then S1 \ S2 shall ignore these elements:
        //        eg. S1: 1001101 S2: 0011111 then S1\S2 = 1000000
        //
        static internal BitVector Substract(BitVector S1, BitVector S2) => S1 & (~S2);
        static internal BitVector CoveredSubstract(BitVector S1, BitVector S2)
        {
            Debug.Assert((S1 | S2) == S1);
            var result = S1 - S2;

            Debug.Assert(result == Substract(S1, S2));
            return S1 - S2;
        }

        // enumrate bit set tables from index low to high, zero based
        static internal IEnumerable<int> TablesAscending(BitVector S)
        {
            int i = 0, t = 0;
            BitVector oldS = S;
            while (S > 0)
            {
                if (0 != (S & 1))
                {
                    t++;
                    yield return i;
                }
                S >>= 1;
                i++;
            }

            // finally this number of tables returned
            Debug.Assert(t == CountSetBits(oldS));
        }

        // enumrate bit set tables from index high to low, zero based
        static internal IEnumerable<int> TablesDescending(BitVector S)
        {
            var result = new Stack<int>();
            foreach (var t in TablesAscending(S))
                result.Push(t);

            while (result.Count > 0)
                yield return result.Pop();
        }

        // return minimal index of given S, zero based
        static internal int MinTableIndex(BitVector S)
        {
            Debug.Assert(S != EmptySet);
            foreach (var t in TablesAscending(S))
                return t;
            throw new InvalidProgramException();
        }

        //   return X: {vj|j<=i}
        static internal BitVector OrderBeforeSet(int i)
        {
            BitVector X = 0;
            for (int j = 0; j <= i; j++)
                X |= (long)(1 << j);
            Debug.Assert(CountSetBits(X) == i + 1);
            return X;
        }


        static public void Test()
        {
            BitVector S1 = 0b0011_0010;
            BitVector S2 = 0b0100_0011;

            Debug.Assert(SetOp.Union(S1, S2) == 0b0111_0011);
            Debug.Assert(SetOp.Union(S1, S2) == SetOp.Union(S2, S1));
            Debug.Assert(SetOp.Intersect(S1, S2) == 0b0000_0010);
            Debug.Assert(SetOp.Intersect(S1, S2) == SetOp.Intersect(S2, S1));
            Debug.Assert(SetOp.Substract(S1, S2) == 0b0011_0000);
            Debug.Assert(SetOp.Substract(S2, S1) == 0b0100_0001);

            Debug.Assert(SetOp.CountSetBits(S1) == 3);
            Debug.Assert(SetOp.MinTableIndex(S1) == 1);
            Debug.Assert(SetOp.MinTableIndex(S2) == 0);
            Debug.Assert(SetOp.OrderBeforeSet(3) == 15);

            var l = SetOp.TablesAscending(S2).ToList();
            Debug.Assert(l.SequenceEqual(new List<int>() { 0, 1, 6 }));
            l = SetOp.TablesDescending(S2).ToList();
            Debug.Assert(l.SequenceEqual(new List<int>() { 6, 1, 0 }));
        }
    }

    // B. Vance and D. Maier. Rapid bushy join-order optimization with cartesian
    // products.In Proc. of the ACM SIGMOD Conf.on Management of Data, pages 35-46, 1996.
    //
    // This algorithm actually only works for true integer BitVector, needs to extend
    // 
    public class VancePartition
    {
        // it does not make sense to make S_ wider because it generates 2^S_ numbers
        long S_;
        internal VancePartition(BitVector S)
        {
            Debug.Assert(S != SetOp.EmptySet);
            S_ = S;
        }

        // There are two ways of enumeration:
        //  1. full set is not included
        //  2. full set is included
        //
        internal IEnumerable<BitVector> Next(bool fullsetIncluded = false)
        {
            BitVector S1, S2, S;
            int counter = 0;
            S = S_;
            S1 = 0;
            do
            {
                S1 = S & (S1 - S);
                if (S1 != S || fullsetIncluded)
                {
                    counter++;
                    S2 = SetOp.CoveredSubstract(S, S1);
                    //Console.WriteLine(Convert.ToString(S1, 2).PadLeft(8,'0') + ":" + Convert.ToString(S2, 2).PadLeft(8, '0'));
                    yield return S1;
                }
            } while (S1 != S);

            // result includes all combinations except emtpy and full set (optional)
            Debug.Assert(counter - (fullsetIncluded ? 1 : 0)
                                == (1 << SetOp.CountSetBits(S)) - 2);
        }

        static public void Test()
        {
            VancePartition p;
            ulong c1 = 0;

            c1 = 0;
            p = new VancePartition(0b1000);
            foreach (long l in p.Next(true)) { c1++; }
            Debug.Assert(c1 == 1);

            c1 = 0;
            p = new VancePartition(0b1100);
            foreach (long l in p.Next()) { c1++; }
            Debug.Assert(c1 == 2);

            c1 = 0;
            p = new VancePartition(0b1011);
            foreach (long l in p.Next()) { c1++; }
            Debug.Assert(c1 == 6);

            c1 = 0;
            p = new VancePartition(0b111_1111_1111);
            foreach (long l in p.Next()) { c1++; }
            Debug.Assert(c1 == 2046);
        }
    }

    class JoinContain
    {
        int nvertices_;
        internal BitVector S_;

        internal JoinContain(int nvertices)
        {
            nvertices_ = nvertices;
        }

        // set/get on low to high order
        internal bool this[int bit]
        {
            get
            {
                Debug.Assert(bit >= 0 && bit < 64);
                var v = ((long)1) << bit;
                return 0 != (S_ & v);
            }
            set
            {
                Debug.Assert(bit >= 0 && bit < 64);
                var v = ((long)1) << bit;
                if (value)
                    S_ |= v;
                else
                    S_ &= ~v;

                Debug.Assert(S_ > 0);
            }
        }

        internal IEnumerable<int> Neighbours()
        {
            int i = 0;
            var S = S_;
            while (S > 0)
            {
                if (0 != (S & 1))
                    yield return i;
                S >>= 1;
                i++;
            }
        }

        public override string ToString()
        {
            string result = "";
            result = Convert.ToString(S_, 2).PadLeft(nvertices_, '0');
            Debug.Assert(result.Length == nvertices_);
            return result;
        }
    }

    /*
     * Example join graph:
     *    A - B 
     *     \ C
     *  =>   
     *     vertices_[3] = {A, B, C}
     *     joinbits_[3] = {{110}, {001}, {001}} 
     *     
     *     Notes:
     *     1. joinbits_ is low to high ordered using integer based bit ops
     */
    public class JoinGraph
    {
        // list of tables referenced
        internal List<LogicNode> vertices_ { get; set; }
        // per vertice join relationship
        internal List<JoinContain> joinbits_ { get; set; }
        // per edge vertices coverage
        internal List<BitVector> predContained_ { get; set; }

        // original join predicates list
        internal List<Expr> preds_ { get; set; }

        // optional: memo associated with it
        internal Memo memo_ { get; set; }

        void validateThis()
        {
            // every table has exactly one join bit cover
            Debug.Assert(joinbits_.Count == vertices_.Count);
            Debug.Assert(predContained_.Count == preds_.Count);
        }

        // find the LogicNode the join predicates references
        int[] ParseJoinPredExpr(Expr pred)
        {
            Debug.Assert(pred.IsBoolean());
            int[] index = new int[2];

            Utils.Assumes(pred.l_().tableRefs_.Count == 1);
            var j1 = pred.l_().tableRefs_[0];
            Utils.Assumes(pred.r_().tableRefs_.Count == 1);
            var j2 = pred.r_().tableRefs_[0];
            int i1 = vertices_.FindIndex(x => j1.alias_ == (x as LogicScanTable).tabref_.alias_);
            index[0] = i1;
            int i2 = vertices_.FindIndex(x => j2.alias_ == (x as LogicScanTable).tabref_.alias_);
            index[1] = i2;

            return index;
        }

        // read through join exprs and mark the contain join bits
        //  say T2.a = T5.a
        //  T2 is at tables_[3] and T5 is at tables_[7]
        //  then 
        //      joinbits_[3].bits_.Set(7)
        //      joinbits_[7].bits_.Set(3)
        //
        void markJoinBitsFromJoinPred(List<Expr> preds)
        {
            var npreds = preds.Count;
            predContained_ = new List<BitVector>(npreds);
            for (int i = 0; i < npreds; i++)
            {
                var p = preds[i];
                var i12 = ParseJoinPredExpr(p);
                int i1 = i12[0], i2 = i12[1];

                // there could be multiple join predicates between two relations
                // so no need to verify duplicates here (!joinbits_[i1][i2])
                joinbits_[i1][i2] = true;
                joinbits_[i2][i1] = true;

                // mark predicate containage as well
                predContained_.Add(SetOp.SingletonSet(i1) | SetOp.SingletonSet(i2));
            }
        }

        // return if current join graph is connected
        bool IsConnected()
        {
            int ntables = vertices_.Count;

            // BFS search will returns number of nodes connected
            var nodes = BFS();
            return nodes.Length == ntables;
        }

        // return if the subgraph S is connected
        internal bool IsConnected(BitVector S) => SubGraph(S).IsConnected();
        // given a table, enumerate all its neighbours
        internal IEnumerable<int> NeighboursOf(int table) => joinbits_[table].Neighbours();

        internal JoinGraph(List<LogicNode> vertices, List<Expr> preds)
        {
            var ntables = vertices.Count;

            vertices_ = vertices;
            preds_ = preds;
            joinbits_ = new List<JoinContain>(ntables);
            for (int i = 0; i < ntables; i++)
                joinbits_.Add(new JoinContain(ntables));

            markJoinBitsFromJoinPred(preds);
            validateThis();
        }

        // given a set of tables, returns the set of its neighbours
        BitVector Neighbours(BitVector S)
        {
            BitVector result = 0;
            int ntables = vertices_.Count;

            foreach (var t in SetOp.TablesAscending(S))
            {
                foreach (var n in NeighboursOf(t))
                {
                    result |= (long)(1 << n);
                }
            }

            return result;
        }

        // Similar to Neighbours() but exclusing S itself
        internal BitVector NeighboursExcluding(BitVector S)
        {
            BitVector result = Neighbours(S);
            result = SetOp.Substract(result, S);

            Debug.Assert(SetOp.Intersect(result, S) == SetOp.EmptySet);
            return result;
        }

        // https://en.wikipedia.org/wiki/Breadth-first_search
        //  procedure BFS(G, start_v):
        //      let Q be a queue
        //      label start_v as discovered
        //      Q.enqueue(start_v)
        //      while Q is not empty
        //          v = Q.dequeue()
        //          for all edges from v to w in G.adjacentEdges(v) do
        //             if w is not labeled as discovered:
        //                 label w as discovered
        //                 Q.enqueue(w) 
        int[] BFS()
        {
            int ntables = vertices_.Count;
            var result = new List<int>();
            var discovered = new bool[ntables];

            Queue<int> Q = new Queue<int>();
            int start_v = 0;
            Q.Enqueue(start_v);
            discovered[start_v] = true;
            while (Q.Count != 0)
            {
                var v = Q.Dequeue();
                // Console.WriteLine("{0}:{1}", n++, v);
                result.Add(v);
                foreach (var w in NeighboursOf(v))
                {
                    if (discovered[w] == false)
                    {
                        discovered[w] = true;
                        Q.Enqueue(w);
                    }
                }
            }

            // all tables have been traversed if connected: n < ntables -> not connected
            Debug.Assert(result.Count <= ntables);
            if (result.Count == ntables)
            {
                for (int i = 0; i < ntables; i++)
                    Debug.Assert(discovered[i]);
            }
            return result.ToArray();
        }

        // record the tables per BFS order
        internal void ReorderBFS()
        {
            int ntables = vertices_.Count;
            var order = BFS();

            // it has to be connected
            if (order.Length != ntables)
                throw new InvalidProgramException();

            var newverts = new List<LogicNode>();
            foreach (var o in order)
            {
                newverts.Add(vertices_[o]);
            }
            vertices_ = newverts;
            joinbits_ = new List<JoinContain>();
            for (int i = 0; i < ntables; i++)
                joinbits_.Add(new JoinContain(ntables));

            // need to reprocess the join strings to reposition table references
            markJoinBitsFromJoinPred(preds_);
            validateThis();
        }

        // extra a subgraph for the given nodes set S
        //    (1) we shall also remove both uncovered nodes and edges 
        //    (2) make this as fast as possible as it is tightly tested in 
        //        DP_bushy algorithm
        //    
        // say we have
        //   A - B - C 
        //    \ D
        // SubGraph(ABD) => {A-D and non-connected node C}.
        //
        internal JoinGraph SubGraph(BitVector S)
        {
            var subvert = new List<LogicNode>();

            var subtablelist = SetOp.TablesAscending(S).ToList();
            foreach (var t in subtablelist)
            {
                subvert.Add(vertices_[t]);
            }
            var subjoins = new List<Expr>();
            foreach (var j in preds_)
            {
                var i12 = ParseJoinPredExpr(j);
                int i1 = i12[0], i2 = i12[1];

                if (subtablelist.Contains(i1) && subtablelist.Contains(i2))
                    subjoins.Add(j);
            }
            return new JoinGraph(subvert, subjoins);
        }

        public override string ToString()
        {
            var result = string.Join(",", vertices_);
            result += ";\n";
            result += string.Join(",", joinbits_);

            validateThis();
            return result;
        }

        // Test code interface to convert join string to Expr
        //  with this function, we can write simpler join conditions like "T1*T2"
        //
        // example: {A,B,C}, {A.a=B.b, A.a=C.c}
        internal JoinGraph(string[] vertices, string[] preds) :
            this(vertices.Select(x => new LogicScanTable(new BaseTableRef(x)) as LogicNode).ToList(),
            dbg_JoinStringToExpr(vertices, preds))
        { }
        internal static Expr dbg_JoinStringToExpr(string[] vertices, string v)
        {
            // parse a join predicate like "T1 * T5" and returns index of T1 and T5
            static int[] dbg_ParseJoinPredString(List<string> tables, string j)
            {
                Debug.Assert(j.Contains("*"));

                int[] index = new int[2];
                int split = j.IndexOf("*");
                string j1 = j.Substring(0, split);
                string j2 = j.Substring(split + 1);
                int i1 = tables.IndexOf(j1);
                index[0] = i1;
                int i2 = tables.IndexOf(j2);
                index[1] = i2;

                return index;
            }

            var i12 = dbg_ParseJoinPredString(vertices.ToList(), v);
            int i1 = i12[0], i2 = i12[1];

            ColExpr l = new ColExpr(null, vertices[i1], $"a{i1 + 1}", new IntType());
            var lref = new BaseTableRef(vertices[i1]); l.tabRef_ = lref; l.tableRefs_.Add(lref);
            l.bounded_ = true;
            ColExpr r = new ColExpr(null, vertices[i2], $"a{i2 + 1}", new IntType());
            var rref = new BaseTableRef(vertices[i2]); r.tabRef_ = rref; r.tableRefs_.Add(rref);
            r.bounded_ = true;
            Expr pred = BinExpr.MakeBooleanExpr(l, r, "=");
            return pred;
        }
        internal static List<Expr> dbg_JoinStringToExpr(string[] vertices, string[] joins)
        {
            List<Expr> conds = new List<Expr>();
            foreach (var v in joins)
            {
                conds.Add(dbg_JoinStringToExpr(vertices, v));
            }
            return conds;
        }

        static public void Test()
        {
            JoinGraph graph;

            var tables = new string[] { "A", "B", "C" };
            graph = new JoinGraph(tables, new string[] { "A*B", "A*C" });
            Console.WriteLine(graph);
            tables = new string[] { "A", "B", "C", "D" };
            graph = new JoinGraph(tables, new string[] { "A*B", "A*C", "A*D", "B*C" });
            Console.WriteLine(graph);

            graph = new ClassTree().RandomGenerate(10);
            graph.ReorderBFS();
        }
    }
}
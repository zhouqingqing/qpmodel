using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

using BitVector = System.Int64;

using adb.utils;
using adb.logic;
using adb.physic;
using adb.optimizer;
using adb.stat;
using adb.expr;
using adb.optimizer.test;

// There are serveral major constructs in optimization:
//   1. MEMO: where the plan is decomposed into groups and each group represents logic equal plan fragments.
//   2. Join order resolver: It does not process the full plan but only the join part.
//   So overall the process works in this way:
//     MEMO creates a nary-join covering multiple joins and invoke resolver to solve it. This is together
//     treated as one memo group. Compared to MEMO without join resolver, it will create several groups
//     covering each binary-join and optimize them using join transformation rules.
//
//  Notes:
//     1) what if the join part contains aggregations etc? Optimizer will form different MEMO optimization
//        process with aggregations move around (eager/lazy).
//     2) what if the join part contains subqueries? It is handled separately in another memo already.
//
namespace adb.optimizer
{
    // Dynamic programming BestTree, which maps a BitVector key to a tree (thus the cost)
    public class BestTree
    {
        // key:  the cgroup key
        // value: plan associated for the join
        //
        Dictionary<BitVector, PhysicNode> memo_ = new Dictionary<BitVector, PhysicNode>();
        Dictionary<BitVector, List<PhysicNode>> candidates_ = new Dictionary<BitVector, List<PhysicNode>>();

        internal PhysicNode this[BitVector key]
        {
            get
            {
                // always starting from non-zero
                Debug.Assert(key != 0);

                PhysicNode outvalue;
                if (memo_.TryGetValue(key, out outvalue))
                    return outvalue;
                return null;
            }
            set
            {
                if (memo_.ContainsKey(key))
                    AddToCandidate(key, memo_[key]);
                memo_[key] = value;
            }
        }

        // debug purpose: save the existing one to candidate list
        internal void AddToCandidate(BitVector key, PhysicNode node)
        {
            Debug.Assert(node != null);
            List<PhysicNode> list;
            if (!candidates_.TryGetValue(key, out list))
            {
                list = new List<PhysicNode>();
                candidates_[key] = list;
            }
            list.Add(node);
        }

        public override string ToString()
        {
            return ToString(0);
        }
        internal string ToString(int verbosity)
        {
            string result = "BestTree: " + memo_.Count + "\n";
            foreach (var m in memo_)
            {
                result += Convert.ToString(m.Key, 2).PadLeft(8, '0');
                if (verbosity > 0)
                    result += "\n" + m.Value.ToString() + "\n";
                result += "\tcandidates: "
                    + (candidates_.ContainsKey(m.Key) ? candidates_[m.Key].Count : 0);
                result += "\tcost: " + m.Value.Cost() + "\n";
            }
            return result;
        }
    }

    // S1_ and S2_ are connected non-overlapped connected sub-graphs of join graph
    class CsgCmpPair
    {
        internal BitVector S1_;
        internal BitVector S2_;
        internal BitVector S_;

        internal CsgCmpPair(JoinGraph graph, BitVector S1, BitVector S2)
        {
            S1_ = S1; S2_ = S2;
            S_ = SetOp.Union(S1, S2);

            Verify(graph);
        }

        [ConditionalAttribute("DEBUG")]
        void Verify(JoinGraph graph)
        {
            // not-overlapped
            Debug.Assert(SetOp.Intersect(S1_, S2_) == SetOp.EmptySet);

            // verify S1_, S2_ itself is connected and S1_ and S2_ is connected
            //   we form a small JoinQuery of it and verify that all nodes included
            //
            Debug.Assert(graph.IsConnected(S1_) && graph.IsConnected(S2_) && graph.IsConnected(S_));
        }
    }

    public abstract class JoinResolver
    {
        protected BestTree bestTree_ = new BestTree();

        internal JoinResolver Reset()
        {
            bestTree_ = new BestTree();
            return this;
        }

        // initialization: enqueue all single tables
        protected void InitByInsertBasicTables(JoinGraph graph)
        {
            foreach (var table in graph.tables_)
            {
                BitVector contained = 1 << graph.tables_.IndexOf(table);
                var logic = new LogicScanTable(table as BaseTableRef);
                logic.tableContained_ = contained;
                bestTree_[contained] = new PhysicScanTable(logic);
            }
        }

        internal abstract PhysicNode Run(JoinGraph graph, BigInteger expectC1 = new BigInteger());

        // join T1 and T2 and return the minimal cost join tree
        protected PhysicNode MinimalJoinTree(PhysicNode T1, PhysicNode T2)
        {
            PhysicNode bestJoin = null;

            // for all join implmentations do
            var implmentations = Enum.GetValues(typeof(PhysicJoin.Implmentation));
            foreach (PhysicJoin.Implmentation impl1 in implmentations)
            {
                foreach (PhysicJoin.Implmentation impl2 in implmentations)
                {
                    // right deep tree
                    //   Notes: we are suppposed to verify existing.Card equals plan.Card but
                    //   propogated round error prevent us to do so. 
                    //
                    PhysicNode plan = PhysicJoin.NewJoinImplmentation(impl1, T1, T2);
                    if (bestJoin == null || bestJoin.Cost() > plan.Cost())
                        bestJoin = plan;

                    // left deep tree
                    plan = PhysicJoin.NewJoinImplmentation(impl2, T2, T1);
                    if (bestJoin == null || bestJoin.Cost() > plan.Cost())
                        bestJoin = plan;
                }
            }

            Debug.Assert(bestJoin != null);
            return bestJoin;
        }

        // create join tree of (T1, T2) with commutative transform and record it in bestTree_
        //
        protected PhysicNode RemmberMinimalJoinTree(PhysicNode T1, PhysicNode T2)
        {
            PhysicNode plan = MinimalJoinTree(T1, T2);
            BitVector key = plan.tableContained_;
            PhysicNode existing = bestTree_[key];
            if (existing == null || existing.Cost() > plan.Cost())
                bestTree_[key] = plan;

            return bestTree_[key];
        }

        static protected void DoTest(JoinResolver solver)
        {
            // loop through all graph class
            for (int n = 2; n <= 10; n++)
            {
                JoinGraph graph;
                BigInteger expectC1;

                // expected nubmer are from section 6.9
                expectC1 = (BigInteger.Pow(n, 3) - n) / 6;
                graph = new ClassChain().RandomGenerate(n);
                solver.Reset().Run(graph, expectC1);

                expectC1 = (BigInteger.Pow(n, 3) - 2 * BigInteger.Pow(n, 2) + n) / 2;
                graph = new ClassCycle().RandomGenerate(n);
                solver.Reset().Run(graph, expectC1);

                expectC1 = BigInteger.Pow(2, n - 2) * (n - 1);
                graph = new ClassStar().RandomGenerate(n);
                solver.Reset().Run(graph, expectC1);

                expectC1 = (BigInteger.Pow(3, n) - BigInteger.Pow(2, n + 1) + 1) / 2;
                graph = new ClassClique().RandomGenerate(n);
                solver.Reset().Run(graph, expectC1);

                // no theoretic number
                graph = new ClassTree().RandomGenerate(n);
                solver.Reset().Run(graph);
                graph = new ClassRandom().RandomGenerate(n);
                solver.Reset().Run(graph);
            }
        }
    }

    // DP bushy tree enumerator - CP included
    //   DPSub is with the same c1 counter complexity as DP bushy algorithm in the 
    //   original paper "DP-counter analytics" but the book version is actually 
    //   DPSubAlt which checks connectivity before c1 counter. So we can implment
    //   book version of DPSub here by simply adding the check.
    //
    public class DPBushy : JoinResolver
    {
        override internal PhysicNode Run(JoinGraph graph, BigInteger expectC1)
        {
            int ntables = graph.tables_.Count;

            Console.WriteLine("DP_Bushy #tables: " + ntables);

            // initialization: enqueue all single tables
            InitByInsertBasicTables(graph);

            // loop through all candidates trees, CP included
            ulong c1 = 0, c2 = 0;
            for (BitVector S = 1; S < (1 << ntables); S++)
            {
                if (bestTree_[S] != null)
                    continue;

                // need connected subgraphs if not consider CP
                if (!graph.IsConnected(S))
                {
                    // this partition enumeration is only to record #c2
                    c2 += (ulong)(new VancePartition(S)).Next().ToArray().Length;
                    continue;
                }

                // for all S_1 subset of S do
                VancePartition partitioner = new VancePartition(S);
                foreach (var S1 in partitioner.Next())
                {
                    c2++;
                    BitVector S2 = SetOp.Substract(S, S1);

                    // requires S1 < S2 to avoid commutative duplication
                    Debug.Assert(S1 != S2);
                    if (S1 < S2)
                    {
                        // need connected subgraphs if not consider CP
                        if (!graph.IsConnected(S1) || !graph.IsConnected(S2))
                            continue;

                        c1++;
                        var currTree = RemmberMinimalJoinTree(bestTree_[S1], bestTree_[S2]);
                        Debug.Assert(bestTree_[S].Cost() == currTree.Cost());
                    }
                }
            }

            // verify # loops for enumeration completeness: 
            // 1. mumber of bushy trees
            // 2. expectC2/c2: number of trees DP considered (P68) and number of trees generated
            // 3. expectC1/c1: number of trees considered
            //
            var nbushy = Space.Count_General_Bushy_CP(ntables);
            var expectC2 = BigInteger.Pow(3, ntables) - BigInteger.Pow(2, ntables + 1) + 1;
            Console.WriteLine("bushy: {0}, dp: {1} == c2: {2}; expected c1: {3} == c1: {4}",
                                    nbushy, expectC2, c2,
                                    expectC1, c1);
            Debug.Assert(expectC2 == c2);
            if (!expectC1.IsZero)
                Debug.Assert(c1 == expectC1);

            var result = bestTree_[(1 << ntables) - 1];
            // Console.WriteLine(result);
            Console.WriteLine(bestTree_);
            return result;
        }

        static public void Test()
        {
            DoTest(new DPBushy());
        }
    }

    public class DPccp : JoinResolver
    {
        IEnumerable<BitVector> enumerateCsgRecursive(JoinGraph graph, BitVector S, BitVector X)
        {
            // N = neighbour(S) \ X
            BitVector N = SetOp.Substract(graph.NeighboursExcluding(S), X);
            // Console.WriteLine("N: " + BitHelper.ToString(N));

            // for all non-empty S' subsetof(N), emit (S union S')
            if (N != SetOp.EmptySet)
            {
                VancePartition partitioner = new VancePartition(N);
                foreach (var S_prime in partitioner.Next(true))
                    yield return SetOp.Union(S, S_prime);

                // for all non-empty S' subsetof(N), recursively invoke (graph, (S union S'), (X union N))
                partitioner = new VancePartition(N);
                foreach (var S_prime in partitioner.Next(true))
                    foreach (var v in enumerateCsgRecursive(graph,
                                    SetOp.Union(S, S_prime), SetOp.Union(X, N)))
                        yield return v;
            }
        }

        IEnumerable<BitVector> enumerateCsg(JoinGraph graph)
        {
            int ntables = graph.tables_.Count;

            for (int i = ntables - 1; i >= 0; i--)
            {
                // emit S
                //   S: {vi}
                BitVector VI = SetOp.SingletonSet(i);
                // Console.WriteLine("S: {0}", i);
                yield return VI;

                // EnumerateCsgRec (G, S, X)
                //   X: {vj|j<=i}
                BitVector X = SetOp.OrderBeforeSet(i);
                foreach (var csg in enumerateCsgRecursive(graph, VI, X))
                {
                    yield return csg;
                }
            }
        }

        IEnumerable<BitVector> enumerateCmp(JoinGraph graph, BitVector S1)
        {
            int ntables = graph.tables_.Count;
            Debug.Assert(S1 != SetOp.EmptySet);

            // min(S1) := min({i|v_i \in S1})
            int minS1 = SetOp.MinTableIndex(S1);

            // B_i(W) := {vj |v_j \in W, j <= i}
            // X = union (B_min(S1), S)
            BitVector BminS1 = SetOp.OrderBeforeSet(minS1);
            BitVector X = SetOp.Union(BminS1, S1);

            // N = neighbour(S1) \ X
            BitVector N = SetOp.Substract(graph.NeighboursExcluding(S1), X);

            // for all(vi 2 N by descending i)
            foreach (int vi in SetOp.TablesDescending(N))
            {
                // emit {v_i}
                BitVector VI = SetOp.SingletonSet(vi);
                yield return VI;

                // recursively invoke enumerateCmp(graph, {v_i}, X union (B_i intersect N))
                BitVector Bi = SetOp.OrderBeforeSet(vi);
                BitVector BiN = SetOp.Intersect(Bi, N);
                foreach (var csg in enumerateCsgRecursive(graph,
                                        VI, SetOp.Union(X, BiN)))
                {
                    yield return csg;
                }
            }
        }

        // enumerate all csg-cmp-pairs
        IEnumerable<CsgCmpPair> csg_cmp_pairs(JoinGraph graph)
        {
            foreach (BitVector S1 in enumerateCsg(graph))
            {
                foreach (BitVector S2 in enumerateCmp(graph, S1))
                {
                    // Console.WriteLine("S1:{0}, S2:{1}", BitHelper.ToString(S1), BitHelper.ToString(S2));
                    yield return new CsgCmpPair(graph, S1, S2);
                }
            }
        }

        override internal PhysicNode Run(JoinGraph graph, BigInteger expectC1)
        {
            int ntables = graph.tables_.Count;

            Console.WriteLine("DPccp #tables: " + ntables);

            // prerequisite: sort tables per DFS order
            graph.ReorderBFS();

            // initialization: enqueue all single tables
            InitByInsertBasicTables(graph);

            ulong c1 = 0;
            foreach (var pair in csg_cmp_pairs(graph))
            {
                c1++;
                BitVector S1 = pair.S1_;
                BitVector S2 = pair.S2_;
                BitVector S = pair.S_;

                var currTree = RemmberMinimalJoinTree(bestTree_[S1], bestTree_[S2]);
                Debug.Assert(bestTree_[S].Cost() == currTree.Cost());
            }

            var nbushy = Space.Count_General_Bushy_CP(ntables);
            var ndp = BigInteger.Pow(3, ntables) - BigInteger.Pow(2, ntables + 1) + 1;
            Console.WriteLine("bushy: {0}, expected: {1} == c1: {2}", nbushy, expectC1, c1);
            if (!expectC1.IsZero)
                Debug.Assert(c1 == expectC1);

            var result = bestTree_[(1 << ntables) - 1];
            Console.WriteLine(result.Explain());
            // Console.WriteLine(bestTree_);

            // verify that it generates same tree as DPBushy - we can't verify that the tree are 
            // the same because we may generate two different join trees with the same cost. So
            // we do cost verificaiton here.
            //
            var bushy = (new DPBushy()).Run(graph, expectC1);
            Debug.Assert(bushy.Cost().Equals(result.Cost()));
            return result;
        }

        static public void Test()
        {
            DPccp solver = new DPccp();

            // book figure 3.12
            var tables = new string[] { "T1", "T2", "T3", "T4", "T5" };
            JoinGraph figure312 = new JoinGraph(tables, new string[] { "T1*T2", "T1*T3", "T1*T4", "T3*T4", "T5*T2", "T5*T3", "T5*T4" });
            solver.Reset().Run(figure312);

            // full test
            DoTest(new DPccp());
        }
    }
}

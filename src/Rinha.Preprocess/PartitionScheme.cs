using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rinha.Preprocess;

public struct TreePredicate
{
    public byte Dim;
    public bool Enabled;
    public short Threshold;
}

public sealed class PartitionScheme
{
    public const short SchemeIdLearnedTree = 2;
    private const int LearnedSampleQueries = 1024;
    private const int LearnedTreeDefaultDepth = 8;
    private const int LearnedTreeMaxDepth = 10;

    public string Name { get; }
    public int TreeDepth { get; }
    public List<TreePredicate> TreePredicates { get; private set; } = new();

    public PartitionScheme(string name, int treeDepth)
    {
        Name = name;
        TreeDepth = treeDepth;
    }

    public static PartitionScheme ByName(string name)
    {
        if (name == "tree256")
            return new PartitionScheme("tree256", 8);

        return new PartitionScheme("tree256", 8);
    }

    public short SchemeId => SchemeIdLearnedTree;

    public int KeyBits => Math.Min(TreeDepth, LearnedTreeMaxDepth);

    public void Prepare(List<Reference> references)
    {
        if (TreePredicates.Count == 0)
        {
            TreePredicates = LearnTreePredicates(references, TreeDepth, Name);
        }
    }

    public uint ComputeKey(short[] vector)
    {
        return ComputeTreeKey(vector, TreeDepth, TreePredicates);
    }

    public static uint ComputeTreeKey(short[] vector, int treeDepth, List<TreePredicate> predicates)
    {
        uint key = 0;
        int node = 0;
        int maxDepth = Math.Min(treeDepth, LearnedTreeMaxDepth);

        for (int i = 0; i < maxDepth; i++)
        {
            bool side = false;
            if (node < predicates.Count)
            {
                var predicate = predicates[node];
                side = predicate.Enabled && vector[predicate.Dim] > predicate.Threshold;
            }
            key = (key << 1) | (side ? 1u : 0u);
            node = node * 2 + 1 + (side ? 1 : 0);
        }

        return key;
    }

    private static List<TreePredicate> LearnTreePredicates(
        List<Reference> references,
        int treeDepth,
        string schemeName)
    {
        int maxDepth = Math.Min(treeDepth, LearnedTreeMaxDepth);
        if (references.Count < 6 || maxDepth == 0) // K = 5, K + 1 = 6
        {
            return new List<TreePredicate>();
        }

        int nodeCount = (1 << maxDepth) - 1;
        var tree = new TreePredicate[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            tree[i] = new TreePredicate { Dim = 0, Threshold = 0, Enabled = false };
        }

        var queryIdx = SampleIndices(references.Count, LearnedSampleQueries);
        var neighbors = ExactTopK(references, queryIdx);
        var candidates = CandidatePredicates(references);
        var positions = Enumerable.Range(0, queryIdx.Count).ToList();

        TrainTreeNode(
            references,
            queryIdx,
            neighbors,
            candidates,
            positions,
            0,
            0,
            maxDepth,
            schemeName,
            tree
        );

        return tree.ToList();
    }

    private static void TrainTreeNode(
        List<Reference> references,
        List<int> queryIdx,
        int[][] neighbors,
        List<Candidate> candidates,
        List<int> positions,
        int node,
        int depth,
        int maxDepth,
        string schemeName,
        TreePredicate[] tree)
    {
        if (depth >= maxDepth || node >= tree.Length || positions.Count < 8)
            return;

        double bestScore = double.MinValue;
        Candidate bestCandidate = default;
        bool hasBest = false;

        foreach (var candidate in candidates)
        {
            int left = 0;
            int right = 0;
            double colocSum = 0;

            foreach (var pos in positions)
            {
                int qi = queryIdx[pos];
                bool qSide = PredicateMatches(references[qi].Vector, candidate);
                if (qSide) right++;
                else left++;

                int same = 0;
                foreach (int ni in neighbors[pos])
                {
                    if (PredicateMatches(references[ni].Vector, candidate) == qSide)
                    {
                        same++;
                    }
                }
                colocSum += (double)same / 5.0; // K = 5
            }

            int minSide = Math.Min(left, right);
            if (minSide < 4) continue;

            double n = positions.Count;
            double coloc = colocSum / n;
            double imbalance = Math.Max(0.0, (double)Math.Max(left, right) / n - 0.55);
            double labelSep = LabelSeparation(references, candidate);

            double imbalanceWeight = 0.35;
            double labelWeight = 0.03;
            double score = coloc - imbalanceWeight * imbalance + labelWeight * labelSep;

            if (!hasBest || score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
                hasBest = true;
            }
        }

        if (!hasBest) return;

        bool logEnabled = Environment.GetEnvironmentVariable("RINHA_PARTITION_TRAIN_LOG") == "1";
        if (logEnabled)
        {
            Console.Error.WriteLine($"[{schemeName}] depth={depth} node={node} dim={bestCandidate.Dim} threshold={bestCandidate.Threshold} queries={positions.Count} score={bestScore:F4}");
        }

        tree[node] = new TreePredicate
        {
            Dim = bestCandidate.Dim,
            Threshold = bestCandidate.Threshold,
            Enabled = true
        };

        var leftPositions = new List<int>(positions.Count / 2);
        var rightPositions = new List<int>(positions.Count / 2);
        foreach (var pos in positions)
        {
            int qi = queryIdx[pos];
            if (PredicateMatches(references[qi].Vector, bestCandidate))
            {
                rightPositions.Add(pos);
            }
            else
            {
                leftPositions.Add(pos);
            }
        }

        TrainTreeNode(references, queryIdx, neighbors, candidates, leftPositions, node * 2 + 1, depth + 1, maxDepth, schemeName, tree);
        TrainTreeNode(references, queryIdx, neighbors, candidates, rightPositions, node * 2 + 2, depth + 1, maxDepth, schemeName, tree);
    }

    private struct Candidate : IEquatable<Candidate>
    {
        public byte Dim;
        public short Threshold;

        public bool Equals(Candidate other) => Dim == other.Dim && Threshold == other.Threshold;
        public override bool Equals(object? obj) => obj is Candidate other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Dim, Threshold);
    }

    private static List<Candidate> CandidatePredicates(List<Reference> references)
    {
        var candidates = new List<Candidate>();
        for (int dim = 0; dim < 14; dim++)
        {
            var values = references.Select(r => r.Vector[dim]).Distinct().ToArray();
            Array.Sort(values);
            if (values.Length <= 1) continue;

            if (values.Length <= 64)
            {
                for (int i = 0; i < values.Length - 1; i++)
                {
                    candidates.Add(new Candidate
                    {
                        Dim = (byte)dim,
                        Threshold = Midpoint(values[i], values[i + 1])
                    });
                }
            }
            else
            {
                int n = values.Length;
                for (int q = 1; q < 16; q++)
                {
                    int idx = ((n - 1) * q) / 16;
                    candidates.Add(new Candidate
                    {
                        Dim = (byte)dim,
                        Threshold = values[idx]
                    });
                }
            }
        }

        return candidates
            .OrderBy(c => c.Dim)
            .ThenBy(c => c.Threshold)
            .Distinct()
            .ToList();
    }

    private static short Midpoint(short a, short b)
    {
        return (short)(((int)a + b) / 2);
    }

    private static double LabelSeparation(List<Reference> references, Candidate predicate)
    {
        var pos = new long[2];
        var total = new long[2];
        int step = Math.Max(1, references.Count / 100000);
        for (int i = 0; i < references.Count; i += step)
        {
            var r = references[i];
            int label = r.Label;
            total[label]++;
            if (PredicateMatches(r.Vector, predicate))
            {
                pos[label]++;
            }
        }
        double p0 = total[0] == 0 ? 0.0 : (double)pos[0] / total[0];
        double p1 = total[1] == 0 ? 0.0 : (double)pos[1] / total[1];
        return Math.Abs(p0 - p1);
    }

    private static bool PredicateMatches(short[] vector, Candidate predicate)
    {
        return vector[predicate.Dim] > predicate.Threshold;
    }

    private static List<int> SampleIndices(int n, int maxSamples)
    {
        int sample = Math.Max(1, Math.Min(n, maxSamples));
        int step = Math.Max(1, n / sample);
        var result = new List<int>();
        for (int i = 0; i < n; i += step)
        {
            result.Add(i);
            if (result.Count >= sample)
                break;
        }
        return result;
    }

    private static int[][] ExactTopK(List<Reference> references, List<int> queryIdx)
    {
        var result = new int[queryIdx.Count][];
        Parallel.For(0, queryIdx.Count, i =>
        {
            result[i] = TopKOne(references, queryIdx[i]);
        });
        return result;
    }

    private static int[] TopKOne(List<Reference> references, int qi)
    {
        var q = references[qi].Vector;
        var bestD = new long[5];
        Array.Fill(bestD, long.MaxValue);
        var bestI = new int[5];
        Array.Fill(bestI, -1);

        for (int ri = 0; ri < references.Count; ri++)
        {
            if (ri == qi) continue;

            var rVec = references[ri].Vector;
            long dist = 0;
            for (int d = 0; d < 14; d++)
            {
                long diff = (long)q[d] - rVec[d];
                dist += diff * diff;
            }

            if (dist >= bestD[4]) continue;

            int pos = 4;
            while (pos > 0 && dist < bestD[pos - 1])
            {
                bestD[pos] = bestD[pos - 1];
                bestI[pos] = bestI[pos - 1];
                pos--;
            }
            bestD[pos] = dist;
            bestI[pos] = ri;
        }
        return bestI;
    }
}

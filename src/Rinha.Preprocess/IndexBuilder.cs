using System;
using System.Collections.Generic;
using System.Linq;

namespace Rinha.Preprocess;

public class IndexBuilder
{
    private readonly List<(short[] Vector, byte Label, uint RefIndex)> _allBlocks = new();
    private readonly List<NodeEntry> _nodes = new();

    public byte[] BuildIndex(List<Reference> references, int leafSize, string schemeName)
    {
        leafSize = Math.Clamp(leafSize, Constants.Lanes, 2048);

        var scheme = PartitionScheme.ByName(schemeName);
        scheme.Prepare(references);

        var writer = new IndexWriter();
        writer.WriteHeader(references.Count, scheme);

        var partitions = new Dictionary<uint, List<int>>();
        for (int i = 0; i < references.Count; i++)
        {
            uint key = scheme.ComputeKey(references[i].Vector);
            if (!partitions.TryGetValue(key, out var list))
            {
                list = new List<int>();
                partitions[key] = list;
            }
            list.Add(i);
        }

        var sortedKeys = partitions.Keys.Order().ToList();
        var partitionMeta = new List<(uint key, int root)>();

        string splitStrategy = Environment.GetEnvironmentVariable("RINHA_KD_SPLIT_STRATEGY") ?? "widest";

        foreach (var key in sortedKeys)
        {
            var indices = partitions[key];
            int root = BuildNode(references, indices, leafSize, splitStrategy);
            partitionMeta.Add((key, root));
        }

        writer.WritePartitionCount(partitionMeta.Count);
        writer.WriteNodeCount(_nodes.Count);

        foreach (var (key, root) in partitionMeta)
        {
            var rootNode = _nodes[root];
            writer.WritePartitionEntry(key, root, rootNode.Len, rootNode.Min, rootNode.Max);
        }

        foreach (var node in _nodes)
        {
            int blockStart = node.Start / Constants.Lanes;
            writer.WriteNodeEntry(
                node.Left,
                node.Right,
                blockStart,
                node.Len,
                node.Min,
                node.Max
            );
        }

        int totalBlocks = _allBlocks.Count / Constants.Lanes;
        writer.WriteBlockCount(totalBlocks);

        // Write vectors in pair-SoA AVX2 layout
        for (int b = 0; b < totalBlocks; b++)
        {
            for (int pair = 0; pair < Constants.Dim / 2; pair++)
            {
                for (int l = 0; l < Constants.Lanes; l++)
                {
                    var (vec, _, _) = _allBlocks[b * Constants.Lanes + l];
                    writer.WriteI16(vec[pair * 2]);
                    writer.WriteI16(vec[pair * 2 + 1]);
                }
            }
        }

        // Write labels
        for (int b = 0; b < totalBlocks; b++)
        {
            for (int l = 0; l < Constants.Lanes; l++)
            {
                var label = _allBlocks[b * Constants.Lanes + l].Label;
                writer.WriteU8(label);
            }
        }

        // Align to u32 alignment boundary
        writer.AlignTo(sizeof(uint));

        // Write reference indices
        for (int b = 0; b < totalBlocks; b++)
        {
            for (int l = 0; l < Constants.Lanes; l++)
            {
                var refIndex = _allBlocks[b * Constants.Lanes + l].RefIndex;
                writer.WriteU32(refIndex);
            }
        }

        // Write node class bits
        foreach (var node in _nodes)
        {
            writer.WriteU8(node.ClassBits);
        }

        return writer.IntoBytes();
    }

    private int BuildNode(List<Reference> references, List<int> indices, int leafSize, string splitStrategy)
    {
        var min = new short[Constants.PackedDim];
        var max = new short[Constants.PackedDim];
        for (int d = 0; d < Constants.PackedDim; d++)
        {
            min[d] = short.MaxValue;
            max[d] = short.MinValue;
        }

        foreach (var idx in indices)
        {
            var vec = references[idx].Vector;
            for (int d = 0; d < Constants.PackedDim; d++)
            {
                if (vec[d] < min[d]) min[d] = vec[d];
                if (vec[d] > max[d]) max[d] = vec[d];
            }
        }

        int nodeIdx = _nodes.Count;
        _nodes.Add(new NodeEntry(-1, -1, 0, 0, (short[])min.Clone(), (short[])max.Clone(), 0));

        if (indices.Count <= leafSize)
        {
            int leafStart = _allBlocks.Count;
            int blocks = (indices.Count + Constants.Lanes - 1) / Constants.Lanes;
            byte classBits = 0;

            for (int b = 0; b < blocks; b++)
            {
                for (int l = 0; l < Constants.Lanes; l++)
                {
                    int i = b * Constants.Lanes + l;
                    if (i < indices.Count)
                    {
                        int refIdx = indices[i];
                        var refItem = references[refIdx];
                        classBits |= (byte)(1 << Math.Min((int)refItem.Label, 7));
                        _allBlocks.Add((refItem.Vector, refItem.Label, (uint)refIdx));
                    }
                    else
                    {
                        _allBlocks.Add((new short[Constants.PackedDim], 0, uint.MaxValue));
                    }
                }
            }

            _nodes[nodeIdx] = new NodeEntry(-1, -1, leafStart, indices.Count, (short[])min.Clone(), (short[])max.Clone(), classBits);
            return nodeIdx;
        }

        int splitDim = splitStrategy == "variance"
            ? VarianceDimension(references, indices, min, max)
            : WidestDimension(min, max);

        var sorted = indices.OrderBy(idx => references[idx].Vector[splitDim]).ToList();

        int leftLen = sorted.Count / 2;
        var leftIndices = sorted.Take(leftLen).ToList();
        var rightIndices = sorted.Skip(leftLen).ToList();

        int leftNode = BuildNode(references, leftIndices, leafSize, splitStrategy);
        int rightNode = BuildNode(references, rightIndices, leafSize, splitStrategy);

        var leftInfo = _nodes[leftNode];
        var rightInfo = _nodes[rightNode];

        _nodes[nodeIdx] = new NodeEntry(
            leftNode,
            rightNode,
            leftInfo.Start,
            leftInfo.Len + rightInfo.Len,
            (short[])min.Clone(),
            (short[])max.Clone(),
            (byte)(leftInfo.ClassBits | rightInfo.ClassBits)
        );

        return nodeIdx;
    }

    private static int WidestDimension(ReadOnlySpan<short> min, ReadOnlySpan<short> max)
    {
        int bestDim = 0;
        short bestWidth = short.MinValue;
        for (int d = 0; d < Constants.Dim; d++)
        {
            short width = (short)(max[d] - min[d]);
            if (width > bestWidth)
            {
                bestWidth = width;
                bestDim = d;
            }
        }
        return bestDim;
    }

    private static int VarianceDimension(List<Reference> references, List<int> indices, ReadOnlySpan<short> min, ReadOnlySpan<short> max)
    {
        long n = indices.Count;
        int bestDim = WidestDimension(min, max);
        double bestScore = double.MinValue;

        for (int d = 0; d < Constants.Dim; d++)
        {
            if (min[d] == max[d]) continue;

            long sum = 0;
            long sumSq = 0;
            foreach (int idx in indices)
            {
                long v = references[idx].Vector[d];
                sum += v;
                sumSq += v * v;
            }

            double score = (double)n * sumSq - (double)sum * sum;
            if (score > bestScore)
            {
                bestScore = score;
                bestDim = d;
            }
        }

        return bestDim;
    }

    private readonly record struct NodeEntry(int Left, int Right, int Start, int Len, short[] Min, short[] Max, byte ClassBits);
}

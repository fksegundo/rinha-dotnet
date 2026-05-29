namespace Rinha.Preprocess;

public class IndexBuilder
{
    private readonly List<(short[] Vector, byte Label)> _allBlocks = new();
    private readonly List<NodeEntry> _nodes = new();

    public byte[] BuildIndex(List<Reference> references, int leafSize, int _flatThreshold)
    {
        leafSize = Math.Max(8, leafSize); // Match LANES=8 clamp from Rust

        var cuts = ComputeV0Cuts(references);

        var writer = new IndexWriter();
        writer.WriteHeader(references.Count, cuts);

        var partitions = new Dictionary<uint, List<int>>();
        for (int i = 0; i < references.Count; i++)
        {
            uint key = PartitionKey.Compute(references[i].Vector, cuts);
            if (!partitions.TryGetValue(key, out var list))
            {
                list = new List<int>();
                partitions[key] = list;
            }
            list.Add(i);
        }

        var sortedKeys = partitions.Keys.Order().ToList();
        var partitionMeta = new List<(uint key, int root)>();

        foreach (var key in sortedKeys)
        {
            var indices = partitions[key];
            int root = BuildNode(references, indices, leafSize);
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

        for (int b = 0; b < totalBlocks; b++)
        {
            for (int d = 0; d < Constants.Dim; d++)
            {
                for (int l = 0; l < Constants.Lanes; l++)
                {
                    var vec = _allBlocks[b * Constants.Lanes + l].Vector;
                    writer.WriteI16(vec[d]);
                }
            }
        }

        for (int b = 0; b < totalBlocks; b++)
        {
            for (int l = 0; l < Constants.Lanes; l++)
            {
                var label = _allBlocks[b * Constants.Lanes + l].Label;
                writer.WriteU8(label);
            }
        }

        return writer.IntoBytes();
    }

    private static short[] ComputeV0Cuts(List<Reference> references)
    {
        if (references.Count < 8)
            return new short[7];

        var values = references.Select(r => r.Vector[0]).ToArray();
        Array.Sort(values);

        int n = values.Length;
        var cuts = new short[7];
        for (int i = 0; i < 7; i++)
        {
            int idx = ((i + 1) * n) / 8;
            idx = Math.Min(idx, n - 1);
            cuts[i] = values[idx];
        }
        return cuts;
    }

    private int BuildNode(List<Reference> references, List<int> indices, int leafSize)
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
        _nodes.Add(new NodeEntry(-1, -1, 0, 0, (short[])min.Clone(), (short[])max.Clone()));

        if (indices.Count <= leafSize)
        {
            int leafStart = _allBlocks.Count;
            int blocks = (indices.Count + Constants.Lanes - 1) / Constants.Lanes;

            for (int b = 0; b < blocks; b++)
            {
                for (int l = 0; l < Constants.Lanes; l++)
                {
                    int i = b * Constants.Lanes + l;
                    if (i < indices.Count)
                    {
                        var refItem = references[indices[i]];
                        _allBlocks.Add((refItem.Vector, refItem.Label));
                    }
                    else
                    {
                        _allBlocks.Add((new short[Constants.PackedDim], 0));
                    }
                }
            }

            _nodes[nodeIdx] = new NodeEntry(-1, -1, leafStart, indices.Count, (short[])min.Clone(), (short[])max.Clone());
            return nodeIdx;
        }

        int splitDim = WidestDimension(min, max);
        var sorted = indices.OrderBy(idx => references[idx].Vector[splitDim]).ToList();

        int leftLen = sorted.Count / 2;
        var leftIndices = sorted.Take(leftLen).ToList();
        var rightIndices = sorted.Skip(leftLen).ToList();

        int leftNode = BuildNode(references, leftIndices, leafSize);
        int rightNode = BuildNode(references, rightIndices, leafSize);

        var leftInfo = _nodes[leftNode];
        var rightInfo = _nodes[rightNode];

        _nodes[nodeIdx] = new NodeEntry(
            leftNode,
            rightNode,
            leftInfo.Start,
            leftInfo.Len + rightInfo.Len,
            (short[])min.Clone(),
            (short[])max.Clone()
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

    private readonly record struct NodeEntry(int Left, int Right, int Start, int Len, short[] Min, short[] Max);
}

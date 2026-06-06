using System.Runtime.CompilerServices;

namespace Rinha.Api.Index;

public ref struct PendingSubtrees
{
    public bool Enabled;
    public byte? Label;
    public Span<int> Roots;
    public Span<int> Bounds;
    public int Len;

    public PendingSubtrees(bool enabled, Span<int> roots, Span<int> bounds)
    {
        Enabled = enabled;
        Label = null;
        Roots = roots;
        Bounds = bounds;
        Len = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte? ConsensusLabel(ReadOnlySpan<int> bestDists, ReadOnlySpan<byte> bestLabels)
    {
        if (bestDists[SearchConstants.K - 1] == int.MaxValue)
            return null;

        int sum = 0;
        for (int i = 0; i < SearchConstants.K; i++)
            sum += bestLabels[i];

        if (sum == 0) return 0;
        if (sum == SearchConstants.K) return 1;
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDefer(
        SpecialistIndex index,
        int nodeIdx,
        int bound,
        ReadOnlySpan<int> bestDists,
        ReadOnlySpan<byte> bestLabels)
    {
        if (!Enabled || Len >= SearchConstants.DeferStackCapacity)
            return false;

        var consensus = ConsensusLabel(bestDists, bestLabels);
        if (consensus == null)
            return false;

        byte label = consensus.Value;
        int needed = 1 << (1 - label);
        byte classBits = index.NodeClassBits(nodeIdx);

        if (classBits == 0 || (classBits & needed) != 0)
            return false;

        if (Label == null)
            Label = label;
        else if (Label.Value != label)
            return false;

        Roots[Len] = nodeIdx;
        Bounds[Len] = bound;
        Len++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldReplay(ReadOnlySpan<int> bestDists, ReadOnlySpan<byte> bestLabels)
    {
        return Len > 0 && ConsensusLabel(bestDists, bestLabels) != Label;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Pop(out int root, out int bound)
    {
        if (Len == 0)
        {
            root = 0;
            bound = 0;
            Label = null;
            return false;
        }
        Len--;
        root = Roots[Len];
        bound = Bounds[Len];
        return true;
    }
}

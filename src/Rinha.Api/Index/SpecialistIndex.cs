using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Rinha.Api.Options;
using Rinha.Api.Runtime;

namespace Rinha.Api.Index;

public ref struct PendingSubtrees
{
    public bool Enabled;
    public byte? Label;
    public Span<int> Roots;
    public Span<long> Bounds;
    public int Len;

    public PendingSubtrees(bool enabled, Span<int> roots, Span<long> bounds)
    {
        Enabled = enabled;
        Label = null;
        Roots = roots;
        Bounds = bounds;
        Len = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte? ConsensusLabel(ReadOnlySpan<long> bestDists, ReadOnlySpan<byte> bestLabels)
    {
        if (bestDists[SearchConstants.K - 1] == long.MaxValue)
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
        long bound,
        ReadOnlySpan<long> bestDists,
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
    public bool ShouldReplay(ReadOnlySpan<long> bestDists, ReadOnlySpan<byte> bestLabels)
    {
        return Len > 0 && ConsensusLabel(bestDists, bestLabels) != Label;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Pop(out int root, out long bound)
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

public sealed unsafe class SpecialistIndex : IDisposable
{
    private const int OpenReadOnly = 0;
    private const int ProtRead = 0x1;
    private const int MapPrivate = 0x02;
    private const int MapPopulate = 0x8000;

    private byte* _ptr;
    private nuint _mapLength;
    private int _referenceCount;
    private int _partitionCount;
    private int _nodeCount;
    private int _blockCount;
    private byte* _partitionsPtr;
    private byte* _nodesPtr;
    private byte* _vectorsPtr;
    private byte* _labelsPtr;
    private uint* _refIndicesPtr;
    private byte* _nodeClassBitsPtr;

    private short[] _partitionByKey = [];
    private uint[] _activeKeys = [];
    private int _treeDepth;
    private TreePredicate[] _treePredicates = [];

    private bool _hasAvx2;
    private long _earlyExitThreshold;
    private bool _labelDefer;

    public static SpecialistIndex Open(string path)
    {
        var index = new SpecialistIndex();
        try
        {
            index.OpenInternal(path);
            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    private void OpenInternal(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("SpecialistIndex uses mmap with MAP_POPULATE and requires Linux.");

        _mapLength = checked((nuint)new FileInfo(path).Length);
        if (_mapLength == 0)
            throw new InvalidOperationException($"Index file is empty: {path}");

        _ptr = (byte*)MapFileReadOnly(path, _mapLength);

        var magic = ReadString(_ptr, 0, 8);
        if (magic != IndexFormat.Magic)
            throw new InvalidOperationException($"Invalid magic: {magic}");

        int scale = ReadI32(_ptr, 8);
        if (scale != IndexFormat.Scale)
            throw new InvalidOperationException($"invalid index scale: expected {IndexFormat.Scale}, got {scale}");

        int packedDims = ReadI32(_ptr, 12);
        if (packedDims != IndexFormat.PackedDims)
            throw new InvalidOperationException($"Invalid packed_dims: {packedDims}");

        _referenceCount = ReadI32(_ptr, 16);
        _partitionCount = ReadI32(_ptr, 20);
        _nodeCount = ReadI32(_ptr, 24);
        _blockCount = ReadI32(_ptr, 28);

        short schemeId = ReadI16(_ptr, 32);
        short schemeParam = ReadI16(_ptr, 34); // tree depth
        _treeDepth = schemeParam;

        short amountCutCount = ReadI16(_ptr, 36);
        short dowCutCount = ReadI16(_ptr, 38);
        short predicateCount = ReadI16(_ptr, 40);

        _treePredicates = new TreePredicate[predicateCount];
        int headerCursor = 42;
        for (int i = 0; i < predicateCount; i++)
        {
            byte dim = _ptr[headerCursor];
            bool enabled = _ptr[headerCursor + 1] != 0;
            short threshold = ReadI16(_ptr, headerCursor + 2);
            _treePredicates[i] = new TreePredicate
            {
                Dim = dim,
                Enabled = enabled,
                Threshold = threshold
            };
            headerCursor += 4;
        }

        _partitionsPtr = _ptr + headerCursor;
        _nodesPtr = _partitionsPtr + (_partitionCount * IndexFormat.RecordSize);

        int vectorsOffset = (int)(_nodesPtr - _ptr) + (_nodeCount * IndexFormat.RecordSize);
        int labelsOffset = vectorsOffset + (_blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2);

        _vectorsPtr = _ptr + vectorsOffset;
        _labelsPtr = _ptr + labelsOffset;

        int cursor = labelsOffset + (_blockCount * SearchConstants.Lanes);

        // Align cursor to 4 bytes for ref_indices
        cursor = AlignCursor(cursor, sizeof(uint));
        _refIndicesPtr = (uint*)(_ptr + cursor);

        cursor += _blockCount * SearchConstants.Lanes * sizeof(uint);
        _nodeClassBitsPtr = _ptr + cursor;

        BuildPartitionLookup();

        nint mapLen = (nint)_mapLength;
        IndexMemory.AdviseWillNeed((IntPtr)_ptr, mapLen);
        IndexMemory.AdviseHugePage((IntPtr)_ptr, mapLen);
        IndexMemory.AdviseHugePage((IntPtr)_vectorsPtr, _blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2);
        IndexMemory.AdviseHugePage((IntPtr)_labelsPtr, _blockCount * SearchConstants.Lanes);
        IndexMemory.AdviseHugePage((IntPtr)_refIndicesPtr, _blockCount * SearchConstants.Lanes * sizeof(uint));

        _hasAvx2 = Avx2.IsSupported;
        _earlyExitThreshold = RinhaOptions.EarlyExitThreshold;
        _labelDefer = (Environment.GetEnvironmentVariable("RINHA_LABEL_DEFER") ?? "1") != "0";
    }

    private void BuildPartitionLookup()
    {
        _partitionByKey = new short[IndexFormat.PartitionKeySlots];
        Array.Fill(_partitionByKey, (short)-1);

        var active = new System.Collections.Generic.List<uint>();
        for (int i = 0; i < _partitionCount; i++)
        {
            uint key = (uint)ReadI32(PartitionPtr(i), 0);
            if (key < IndexFormat.PartitionKeySlots)
            {
                _partitionByKey[key] = (short)i;
                active.Add(key);
            }
        }
        _activeKeys = active.ToArray();
    }

    public void MlockMapping()
    {
        if (_ptr == null || _mapLength == 0)
            return;

        MemoryLock.TryLockRegion((IntPtr)_ptr, _mapLength);
    }

    public void PretouchMapping()
    {
        if (_ptr == null || _mapLength == 0) return;

        long capacity = checked((long)_mapLength);
        long checksum = 0;

        for (long i = 0; i < capacity; i += 4096)
        {
            checksum += _ptr[i];
        }

        if (capacity > 0)
        {
            checksum += _ptr[capacity - 1];
        }

        Console.WriteLine($"[Index] Pretouch finished (checksum: {checksum:X})");
    }

    public byte PredictFraudCount(Span<short> query)
    {
        return PredictFraudCountWithEarlyExit(query, _earlyExitThreshold);
    }

    public byte PredictFraudCountExact(Span<short> query)
    {
        return PredictFraudCountWithEarlyExit(query, 0);
    }

    private byte PredictFraudCountWithEarlyExit(Span<short> query, long earlyExitThreshold)
    {
        Span<long> bestDists = stackalloc long[SearchConstants.K];
        Span<byte> bestLabels = stackalloc byte[SearchConstants.K];
        Span<uint> bestIndices = stackalloc uint[SearchConstants.K];
        bestDists.Fill(long.MaxValue);
        bestLabels.Clear();
        bestIndices.Fill(uint.MaxValue);

        Span<int> deferRoots = stackalloc int[SearchConstants.DeferStackCapacity];
        Span<long> deferBounds = stackalloc long[SearchConstants.DeferStackCapacity];
        var pendingSubtrees = new PendingSubtrees(_labelDefer, deferRoots, deferBounds);

        uint queryKey = ComputePartitionKey(query);
        int primaryIdx = PartitionIdxForKey(queryKey);

        if (primaryIdx >= 0)
        {
            byte* primary = PartitionPtr(primaryIdx);
            long bound = LowerBoundBoxRecord(query, primary);
            SearchNodeIterativeFast(
                ReadI32(primary, 4),
                bound,
                query,
                bestDists,
                bestLabels,
                bestIndices,
                ref pendingSubtrees
            );

            ReplayPendingIfNeeded(
                query,
                bestDists,
                bestLabels,
                bestIndices,
                ref pendingSubtrees
            );

            if (earlyExitThreshold > 0 && bestDists[SearchConstants.K - 1] < earlyExitThreshold)
            {
                int count = 0;
                for (int i = 0; i < SearchConstants.K; i++)
                    count += bestLabels[i];
                return (byte)count;
            }
        }

        Span<(long bound, int idx)> partitionEntries = stackalloc (long, int)[SearchConstants.MaxPartitions];
        int partitionLen = 0;

        for (int i = 0; i < _activeKeys.Length; i++)
        {
            uint key = _activeKeys[i];
            if (key == queryKey) continue;

            int idx = _partitionByKey[key];
            long bound = LowerBoundBoxRecord(query, PartitionPtr(idx));
            if (bound < bestDists[SearchConstants.K - 1])
            {
                partitionEntries[partitionLen++] = (bound, idx);
            }
        }

        SortPartitionEntries(partitionEntries, partitionLen);

        for (int i = 0; i < partitionLen; i++)
        {
            var (bound, idx) = partitionEntries[i];
            if (bound >= bestDists[SearchConstants.K - 1])
                break;

            SearchNodeIterativeFast(
                ReadI32(PartitionPtr(idx), 4),
                bound,
                query,
                bestDists,
                bestLabels,
                bestIndices,
                ref pendingSubtrees
            );

            ReplayPendingIfNeeded(
                query,
                bestDists,
                bestLabels,
                bestIndices,
                ref pendingSubtrees
            );

            if (earlyExitThreshold > 0 && bestDists[SearchConstants.K - 1] < earlyExitThreshold)
                break;
        }

        int finalCount = 0;
        for (int i = 0; i < SearchConstants.K; i++)
            finalCount += bestLabels[i];

        return (byte)finalCount;
    }

    private void SearchNodeIterativeFast(
        int root,
        long rootBound,
        Span<short> query,
        Span<long> bestDists,
        Span<byte> bestLabels,
        Span<uint> bestIndices,
        ref PendingSubtrees pendingSubtrees)
    {
        Span<int> stackNodes = stackalloc int[SearchConstants.TreeStackCapacity];
        Span<long> stackBounds = stackalloc long[SearchConstants.TreeStackCapacity];
        int stackLen = 0;

        int current = root;
        long currentBound = rootBound;

        while (true)
        {
            if (currentBound <= bestDists[SearchConstants.K - 1])
            {
                if (pendingSubtrees.TryDefer(this, current, currentBound, bestDists, bestLabels))
                {
                    if (stackLen == 0)
                        break;

                    stackLen--;
                    current = stackNodes[stackLen];
                    currentBound = stackBounds[stackLen];
                    continue;
                }

                byte* node = NodePtr(current);
                int left = ReadI32(node, 0);
                int right = ReadI32(node, 4);

                if (left < 0 || right < 0)
                {
                    ScanLeafFast(current, query, bestDists, bestLabels, bestIndices);
                }
                else
                {
                    if (Sse.IsSupported)
                        Sse.Prefetch0(NodePtr(right));

                    long lb = LowerBoundBoxRecord(query, NodePtr(left));
                    long rb = LowerBoundBoxRecord(query, NodePtr(right));

                    int nearIdx;
                    long nearBound;
                    int farIdx;
                    long farBound;

                    if (lb <= rb)
                    {
                        nearIdx = left;
                        nearBound = lb;
                        farIdx = right;
                        farBound = rb;
                    }
                    else
                    {
                        nearIdx = right;
                        nearBound = rb;
                        farIdx = left;
                        farBound = lb;
                    }

                    if (farBound <= bestDists[SearchConstants.K - 1] && stackLen < SearchConstants.TreeStackCapacity)
                    {
                        stackNodes[stackLen] = farIdx;
                        stackBounds[stackLen] = farBound;
                        stackLen++;
                    }

                    if (nearBound <= bestDists[SearchConstants.K - 1])
                    {
                        current = nearIdx;
                        currentBound = nearBound;
                        continue;
                    }
                }
            }

            if (stackLen == 0)
                break;

            stackLen--;
            current = stackNodes[stackLen];
            currentBound = stackBounds[stackLen];
        }
    }

    private void ScanLeafFast(
        int nodeIdx,
        Span<short> query,
        Span<long> bestDists,
        Span<byte> bestLabels,
        Span<uint> bestIndices)
    {
        byte* node = NodePtr(nodeIdx);
        int startBlock = ReadI32(node, 8);
        int len = ReadI32(node, 12);

        int blocks = (len + SearchConstants.Lanes - 1) / SearchConstants.Lanes;
        Span<int> blockDists = stackalloc int[SearchConstants.Lanes];

        for (int b = 0; b < blocks; b++)
        {
            int blockIdx = startBlock + b;
            int blockBase = blockIdx * SearchConstants.Dims * SearchConstants.Lanes;

            if (Sse.IsSupported && b + 1 < blocks)
            {
                int nextBase = (startBlock + b + 1) * SearchConstants.Dims * SearchConstants.Lanes;
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase * 2));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase * 2 + 64));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase * 2 + 128));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase * 2 + 192));
                Sse.Prefetch0((void*)(_labelsPtr + (startBlock + b + 1) * SearchConstants.Lanes));
            }

            uint mask;
            if (_hasAvx2)
            {
                mask = ScanBlockPairAvx2Bounded(_vectorsPtr, blockBase, query, bestDists[SearchConstants.K - 1], blockDists);
            }
            else
            {
                ScanBlockScalar(_vectorsPtr, blockBase, query, blockDists);
                mask = 0xff;
            }

            if (mask == 0) continue;

            int labelsBase = blockIdx * SearchConstants.Lanes;
            int laneCount = Math.Min(len - b * SearchConstants.Lanes, SearchConstants.Lanes);
            mask &= (uint)((1 << laneCount) - 1);

            while (mask != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;

                long dist = blockDists[i];
                byte label = _labelsPtr[labelsBase + i];
                uint refIdx = _refIndicesPtr[labelsBase + i];

                InsertBestFast(dist, label, refIdx, bestDists, bestLabels, bestIndices);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertBestFast(
        long dist,
        byte label,
        uint refIndex,
        Span<long> bestDists,
        Span<byte> bestLabels,
        Span<uint> bestIndices)
    {
        if (!CandidateBefore(dist, refIndex, bestDists[SearchConstants.K - 1], bestIndices[SearchConstants.K - 1]))
            return;

        int pos = SearchConstants.K - 1;
        while (pos > 0 && CandidateBefore(dist, refIndex, bestDists[pos - 1], bestIndices[pos - 1]))
        {
            bestDists[pos] = bestDists[pos - 1];
            bestLabels[pos] = bestLabels[pos - 1];
            bestIndices[pos] = bestIndices[pos - 1];
            pos--;
        }
        bestDists[pos] = dist;
        bestLabels[pos] = label;
        bestIndices[pos] = refIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CandidateBefore(long dist, uint refIndex, long otherDist, uint otherIndex)
    {
        return dist < otherDist || (dist == otherDist && refIndex < otherIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReplayPendingIfNeeded(
        Span<short> query,
        Span<long> bestDists,
        Span<byte> bestLabels,
        Span<uint> bestIndices,
        ref PendingSubtrees pendingSubtrees)
    {
        if (!pendingSubtrees.ShouldReplay(bestDists, bestLabels))
            return;

        while (pendingSubtrees.Pop(out int root, out long bound))
        {
            if (bound > bestDists[SearchConstants.K - 1])
                continue;

            var dummyPending = new PendingSubtrees(false, Span<int>.Empty, Span<long>.Empty);
            SearchNodeIterativeFast(
                root,
                bound,
                query,
                bestDists,
                bestLabels,
                bestIndices,
                ref dummyPending
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NodeClassBits(int nodeIdx)
    {
        return _nodeClassBitsPtr[nodeIdx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long LowerBoundBoxRecord(Span<short> query, byte* record)
    {
        byte* minPtr = record + IndexFormat.BoundsMinOffset;
        byte* maxPtr = record + IndexFormat.BoundsMaxOffset;
        return _hasAvx2
            ? LowerBoundBoxAvx2Ptr(query, minPtr, maxPtr)
            : LowerBoundBoxScalarPtr(query, minPtr, maxPtr);
    }

    private static void SortPartitionEntries(Span<(long bound, int idx)> entries, int length)
    {
        for (int i = 1; i < length; i++)
        {
            var current = entries[i];
            int j = i - 1;
            while (j >= 0 && entries[j].bound > current.bound)
            {
                entries[j + 1] = entries[j];
                j--;
            }

            entries[j + 1] = current;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long LowerBoundBoxScalarPtr(Span<short> query, byte* minPtr, byte* maxPtr)
    {
        long sum = 0;
        fixed (short* qPtr = query)
        {
            short* loPtr = (short*)minPtr;
            short* hiPtr = (short*)maxPtr;
            for (int d = 0; d < SearchConstants.Dims; d++)
            {
                long q = qPtr[d];
                long lo = loPtr[d];
                long hi = hiPtr[d];
                long diff = q < lo ? lo - q : (q > hi ? q - hi : 0);
                sum += diff * diff;
            }
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long LowerBoundBoxAvx2Ptr(Span<short> query, byte* minPtr, byte* maxPtr)
    {
        fixed (short* qPtr = query)
        {
            Vector256<short> q = Unsafe.ReadUnaligned<Vector256<short>>(qPtr);
            Vector256<short> mn = Unsafe.ReadUnaligned<Vector256<short>>(minPtr);
            Vector256<short> mx = Unsafe.ReadUnaligned<Vector256<short>>(maxPtr);

            Vector256<short> zero = Vector256<short>.Zero;
            Vector256<short> below = Avx2.Max(Avx2.Subtract(mn, q), zero);
            Vector256<short> above = Avx2.Max(Avx2.Subtract(q, mx), zero);
            Vector256<short> diff = Avx2.Max(below, above);

            Vector256<int> sq = Avx2.MultiplyAddAdjacent(diff, diff);

            Vector256<long> lo = Avx2.ConvertToVector256Int64(sq.GetLower());
            Vector256<long> hi = Avx2.ConvertToVector256Int64(sq.GetUpper());

            Vector256<long> sum64 = Avx2.Add(lo, hi);

            Vector128<long> sum128 = Sse2.Add(sum64.GetLower(), sum64.GetUpper());
            long s0 = Sse2.X64.ConvertToInt64(sum128);
            long s1 = Sse2.X64.ConvertToInt64(Sse2.ShiftRightLogical128BitLane(sum128, 8));

            return s0 + s1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ScanBlockScalar(byte* vectors, int blockBase, Span<short> query, Span<int> outDists)
    {
        outDists.Clear();
        short* vPtr = (short*)vectors;
        for (int pair = 0; pair < SearchConstants.Dims / 2; pair++)
        {
            int q0 = query[pair * 2];
            int q1 = query[pair * 2 + 1];
            for (int l = 0; l < SearchConstants.Lanes; l++)
            {
                int baseOffset = blockBase + pair * SearchConstants.Lanes * 2 + l * 2;
                int diff0 = q0 - vPtr[baseOffset];
                int diff1 = q1 - vPtr[baseOffset + 1];
                outDists[l] += diff0 * diff0 + diff1 * diff1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint ScanBlockPairAvx2Bounded(
        byte* vectors,
        int blockBase,
        Span<short> query,
        long limit,
        Span<int> outDists)
    {
        short* basePtr = (short*)vectors + blockBase;
        Vector256<int> acc = Vector256<int>.Zero;

        for (int pair = 0; pair < SearchConstants.Dims / 2; pair++)
        {
            short q0 = query[pair * 2];
            short q1 = query[pair * 2 + 1];
            Vector128<short> q128 = Vector128.Create(q0, q1, q0, q1, q0, q1, q0, q1);
            Vector256<short> q = Vector256.Create(q128, q128);

            Vector256<short> packed = Unsafe.ReadUnaligned<Vector256<short>>(basePtr + pair * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, packed);
            acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(diff, diff));
        }

        fixed (int* outPtr = outDists)
        {
            if (limit < int.MaxValue)
            {
                Vector256<int> below = Avx2.CompareGreaterThan(Vector256.Create((int)limit + 1), acc);
                uint mask = (uint)Avx2.MoveMask(Vector256.AsSingle(below));
                if (mask == 0)
                {
                    return 0;
                }
                Unsafe.WriteUnaligned(outPtr, acc);
                return mask;
            }
            else
            {
                Unsafe.WriteUnaligned(outPtr, acc);
                return 0xff;
            }
        }
    }

    public uint ComputePartitionKey(Span<short> vector)
    {
        uint key = 0;
        int node = 0;
        int maxDepth = Math.Min(_treeDepth, 10);

        for (int i = 0; i < maxDepth; i++)
        {
            bool side = false;
            if (node < _treePredicates.Length)
            {
                var predicate = _treePredicates[node];
                side = predicate.Enabled && vector[predicate.Dim] > predicate.Threshold;
            }
            key = (key << 1) | (side ? 1u : 0u);
            node = node * 2 + 1 + (side ? 1 : 0);
        }

        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PartitionIdxForKey(uint key)
    {
        if (key >= IndexFormat.PartitionKeySlots) return -1;
        return _partitionByKey[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* PartitionPtr(int index) => _partitionsPtr + index * IndexFormat.RecordSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* NodePtr(int index) => _nodesPtr + index * IndexFormat.RecordSize;

    private static string ReadString(byte* ptr, int offset, int length)
    {
        return System.Text.Encoding.ASCII.GetString(ptr + offset, length);
    }

    private static int ReadI32(byte* ptr, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ptr + offset, 4));
    }

    private static short ReadI16(byte* ptr, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(ptr + offset, 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignCursor(int cursor, int align)
    {
        return cursor + ((align - (cursor % align)) % align);
    }

    private static IntPtr MapFileReadOnly(string path, nuint length)
    {
        int fd = open(path, OpenReadOnly);
        if (fd < 0)
            ThrowErrno($"open failed for {path}");

        try
        {
            IntPtr ptr = mmap(IntPtr.Zero, (UIntPtr)length, ProtRead, MapPrivate | MapPopulate, fd, 0);
            if (ptr == new IntPtr(-1))
                ThrowErrno($"mmap failed for {path}");

            return ptr;
        }
        finally
        {
            close(fd);
        }
    }

    private static void ThrowErrno(string message)
    {
        int errno = Marshal.GetLastPInvokeError();
        throw new IOException($"{message}: errno {errno}");
    }

    public void Dispose()
    {
        if (_ptr != null && _mapLength != 0)
        {
            munmap((IntPtr)_ptr, (UIntPtr)_mapLength);
            _ptr = null;
            _mapLength = 0;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, UIntPtr length);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}

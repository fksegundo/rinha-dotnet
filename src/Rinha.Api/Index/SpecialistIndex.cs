using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Rinha.Api.Options;
using Rinha.Api.Runtime;

namespace Rinha.Api.Index;

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

    private short[] _partitionByKey = [];
    private uint[] _activeKeys = [];
    private short[] _partitionCutsV0 = new short[7];

    private bool _hasAvx2;
    private SearchMode _searchMode;
    private int _earlyExitThreshold;

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

        for (int i = 0; i < 7; i++)
            _partitionCutsV0[i] = ReadI16(_ptr, 32 + i * 2);

        _partitionsPtr = _ptr + 32 + 14;
        _nodesPtr = _partitionsPtr + (_partitionCount * IndexFormat.RecordSize);

        int vectorsOffset = (int)(_nodesPtr - _ptr) + (_nodeCount * IndexFormat.RecordSize);
        int labelsOffset = vectorsOffset + (_blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2);

        _vectorsPtr = _ptr + vectorsOffset;
        _labelsPtr = _ptr + labelsOffset;

        BuildPartitionLookup();

        nint mapLen = (nint)_mapLength;
        IndexMemory.AdviseWillNeed((IntPtr)_ptr, mapLen);
        IndexMemory.AdviseHugePage((IntPtr)_ptr, mapLen);
        IndexMemory.AdviseHugePage((IntPtr)_vectorsPtr, _blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2);
        IndexMemory.AdviseHugePage((IntPtr)_labelsPtr, _blockCount * SearchConstants.Lanes);

        _hasAvx2 = Avx2.IsSupported;
        _earlyExitThreshold = (int)RinhaOptions.EarlyExitThreshold;
        _searchMode = (Environment.GetEnvironmentVariable("RINHA_SEARCH_MODE") ?? "key-first") switch
        {
            "exact" => SearchMode.Exact,
            "specialist" => SearchMode.Specialist,
            _ => SearchMode.KeyFirst
        };
    }

    private void BuildPartitionLookup()
    {
        _partitionByKey = new short[IndexFormat.PartitionKeySlots];
        Array.Fill(_partitionByKey, (short)-1);

        var active = new List<uint>();
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

        // Port of pretouch_all: read every 4KiB
        for (long i = 0; i < capacity; i += 4096)
        {
            checksum += _ptr[i];
        }

        // Last byte
        if (capacity > 0)
        {
            checksum += _ptr[capacity - 1];
        }

        Console.WriteLine($"[Index] Pretouch finished (checksum: {checksum:X})");
    }

    public byte PredictFraudCount(Span<short> query)
    {
        Span<int> bestDists = stackalloc int[SearchConstants.K];
        Span<byte> bestLabels = stackalloc byte[SearchConstants.K];
        bestDists.Fill(int.MaxValue);
        bestLabels.Clear();

        switch (_searchMode)
        {
            case SearchMode.Exact:
                SearchExact(query, bestDists, bestLabels);
                break;
            case SearchMode.Specialist:
                SearchSpecialist(query, bestDists, bestLabels);
                break;
            case SearchMode.KeyFirst:
                SearchKeyFirst(query, bestDists, bestLabels);
                break;
        }

        int fraudCount = 0;
        foreach (var label in bestLabels)
            fraudCount += label;

        return (byte)fraudCount;
    }

    public byte PredictFraudCountExact(Span<short> query)
    {
        Span<int> bestDists = stackalloc int[SearchConstants.K];
        Span<byte> bestLabels = stackalloc byte[SearchConstants.K];
        bestDists.Fill(int.MaxValue);
        bestLabels.Clear();
        SearchExact(query, bestDists, bestLabels);

        int fraudCount = 0;
        foreach (var label in bestLabels)
            fraudCount += label;
        return (byte)fraudCount;
    }

    private void SearchExact(Span<short> query, Span<int> bestDists, Span<byte> bestLabels)
    {
        Span<int> blockDists = stackalloc int[SearchConstants.Lanes];

        for (int b = 0; b < _blockCount; b++)
        {
            int blockBase = b * SearchConstants.Dims * SearchConstants.Lanes;

            if (_hasAvx2)
            {
                if (ScanBlockAvx2(_vectorsPtr, blockBase, query, blockDists, bestDists[SearchConstants.K - 1]))
                {
                    int labelsBase = b * SearchConstants.Lanes;
                    int remaining = _referenceCount - b * SearchConstants.Lanes;
                    int laneCount = Math.Min(remaining, SearchConstants.Lanes);

                    for (int i = 0; i < laneCount; i++)
                        InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
                }
            }
            else
            {
                if (ScanBlockScalar(_vectorsPtr, blockBase, query, blockDists, bestDists[SearchConstants.K - 1]))
                {
                    int labelsBase = b * SearchConstants.Lanes;
                    int remaining = _referenceCount - b * SearchConstants.Lanes;
                    int laneCount = Math.Min(remaining, SearchConstants.Lanes);

                    for (int i = 0; i < laneCount; i++)
                        InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
                }
            }
        }
    }

    private void SearchSpecialist(Span<short> query, Span<int> bestDists, Span<byte> bestLabels)
    {
        Span<(int bound, int idx)> partitionEntries = stackalloc (int, int)[SearchConstants.MaxPartitions];
        int partitionLen = 0;

        for (int idx = 0; idx < _partitionCount; idx++)
        {
            int bound = LowerBoundBoxRecord(query, PartitionPtr(idx));
            partitionEntries[partitionLen++] = (bound, idx);
        }

        SortPartitionEntries(partitionEntries, partitionLen);

        for (int i = 0; i < partitionLen; i++)
        {
            var (bound, idx) = partitionEntries[i];
            if (bound >= bestDists[SearchConstants.K - 1])
                break;

            SearchNodeIterative(ReadI32(PartitionPtr(idx), 4), bound, query, bestDists, bestLabels);
        }
    }

    private void SearchKeyFirst(Span<short> query, Span<int> bestDists, Span<byte> bestLabels)
    {
        uint queryKey = ComputePartitionKey(query);
        int primaryIdx = queryKey < IndexFormat.PartitionKeySlots ? _partitionByKey[queryKey] : -1;

        if (primaryIdx >= 0)
        {
            byte* primary = PartitionPtr(primaryIdx);
            int bound = LowerBoundBoxRecord(query, primary);
            if (bound < bestDists[SearchConstants.K - 1])
            {
                SearchNodeIterative(ReadI32(primary, 4), bound, query, bestDists, bestLabels);
                if (ShouldEarlyExit(bestDists))
                    return;
            }
        }

        Span<(int bound, int idx)> partitionEntries = stackalloc (int, int)[SearchConstants.MaxPartitions];
        int partitionLen = 0;

        for (int i = 0; i < _activeKeys.Length; i++)
        {
            uint key = _activeKeys[i];
            int idx = _partitionByKey[key];
            if (idx == primaryIdx)
                continue;

            int bound = LowerBoundBoxRecord(query, PartitionPtr(idx));
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

            SearchNodeIterative(ReadI32(PartitionPtr(idx), 4), bound, query, bestDists, bestLabels);
            if (ShouldEarlyExit(bestDists))
                break;
        }
    }

    private void SearchNodeIterative(int root, int rootBound, Span<short> query, Span<int> bestDists, Span<byte> bestLabels)
    {
        Span<int> stackNodes = stackalloc int[SearchConstants.TreeStackCapacity];
        Span<int> stackBounds = stackalloc int[SearchConstants.TreeStackCapacity];
        int stackLen = 0;

        int current = root;
        int currentBound = rootBound;

        while (true)
        {
            if (currentBound <= bestDists[SearchConstants.K - 1])
            {
                byte* node = NodePtr(current);
                int left = ReadI32(node, 0);
                int right = ReadI32(node, 4);

                if (left < 0 || right < 0)
                {
                    ScanLeaf(ReadI32(node, 8), ReadI32(node, 12), query, bestDists, bestLabels);
                }
                else
                {
                    if (Sse.IsSupported)
                        Sse.Prefetch0(NodePtr(right));

                    int lb = LowerBoundBoxRecord(query, NodePtr(left));
                    int rb = LowerBoundBoxRecord(query, NodePtr(right));

                    int nearIdx;
                    int nearBound;
                    int farIdx;
                    int farBound;

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

    private void ScanLeaf(int startBlock, int len, Span<short> query, Span<int> bestDists, Span<byte> bestLabels)
    {
        int blocks = (len + SearchConstants.Lanes - 1) / SearchConstants.Lanes;
        Span<int> blockDists = stackalloc int[SearchConstants.Lanes];

        for (int b = 0; b < blocks; b++)
        {
            int blockIdx = startBlock + b;
            int blockBase = blockIdx * SearchConstants.Dims * SearchConstants.Lanes;

            if (Sse.IsSupported && b + 1 < blocks)
            {
                int nextBase = (startBlock + b + 1) * SearchConstants.Dims * SearchConstants.Lanes;
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase + 64));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase + 128));
                Sse.Prefetch0((void*)(_vectorsPtr + nextBase + 192));
                Sse.Prefetch0((void*)(_labelsPtr + (startBlock + b + 1) * SearchConstants.Lanes));
            }

            if (_hasAvx2)
            {
                if (ScanBlockAvx2(_vectorsPtr, blockBase, query, blockDists, bestDists[SearchConstants.K - 1]))
                {
                    int labelsBase = blockIdx * SearchConstants.Lanes;
                    int laneCount = Math.Min(len - b * SearchConstants.Lanes, SearchConstants.Lanes);

                    for (int i = 0; i < laneCount; i++)
                        InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
                }
            }
            else
            {
                if (ScanBlockScalar(_vectorsPtr, blockBase, query, blockDists, bestDists[SearchConstants.K - 1]))
                {
                    int labelsBase = blockIdx * SearchConstants.Lanes;
                    int laneCount = Math.Min(len - b * SearchConstants.Lanes, SearchConstants.Lanes);

                    for (int i = 0; i < laneCount; i++)
                        InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LowerBoundBoxRecord(Span<short> query, byte* record)
    {
        byte* minPtr = record + IndexFormat.BoundsMinOffset;
        byte* maxPtr = record + IndexFormat.BoundsMaxOffset;
        return _hasAvx2
            ? LowerBoundBoxAvx2Ptr(query, minPtr, maxPtr)
            : LowerBoundBoxScalarPtr(query, minPtr, maxPtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldEarlyExit(Span<int> bestDists) =>
        _earlyExitThreshold > 0 && bestDists[SearchConstants.K - 1] < _earlyExitThreshold;

    private static void SortPartitionEntries(Span<(int bound, int idx)> entries, int length)
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
    private static void InsertBest(int dist, byte label, Span<int> bestDists, Span<byte> bestLabels)
    {
        if (dist >= bestDists[SearchConstants.K - 1])
            return;

        int pos = SearchConstants.K - 1;
        while (pos > 0 && dist < bestDists[pos - 1])
        {
            bestDists[pos] = bestDists[pos - 1];
            bestLabels[pos] = bestLabels[pos - 1];
            pos--;
        }
        bestDists[pos] = dist;
        bestLabels[pos] = label;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int LowerBoundBoxScalarPtr(Span<short> query, byte* minPtr, byte* maxPtr)
    {
        int sum = 0;
        fixed (short* qPtr = query)
        {
            short* loPtr = (short*)minPtr;
            short* hiPtr = (short*)maxPtr;
            for (int d = 0; d < SearchConstants.Dims; d++)
            {
                int q = qPtr[d];
                int lo = loPtr[d];
                int hi = hiPtr[d];
                int diff = q < lo ? lo - q : (q > hi ? q - hi : 0);
                sum += diff * diff;
            }
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int LowerBoundBoxAvx2Ptr(Span<short> query, byte* minPtr, byte* maxPtr)
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

            Vector128<int> sum128 = Sse2.Add(sq.GetLower(), sq.GetUpper());
            Vector128<int> temp = Sse2.Shuffle(sum128, 0x4E);
            sum128 = Sse2.Add(sum128, temp);
            Vector128<int> temp2 = Sse2.Shuffle(sum128, 0xB1);
            sum128 = Sse2.Add(sum128, temp2);

            return Sse2.ConvertToInt32(sum128);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ScanBlockScalar(byte* vectors, int blockBase, Span<short> query, Span<int> outDists, int limit)
    {
        outDists.Clear();
        short* vPtr = (short*)vectors;

        for (int p = 0; p < 3; p++)
        {
            int q0 = query[p * 2];
            int q1 = query[p * 2 + 1];
            int pairOffset = blockBase + p * SearchConstants.Lanes * 2;
            for (int l = 0; l < SearchConstants.Lanes; l++)
            {
                int diff0 = q0 - vPtr[pairOffset + l * 2];
                int diff1 = q1 - vPtr[pairOffset + l * 2 + 1];
                outDists[l] += diff0 * diff0 + diff1 * diff1;
            }
        }

        bool anyBetter6 = false;
        for (int l = 0; l < SearchConstants.Lanes; l++)
        {
            if (outDists[l] < limit)
            {
                anyBetter6 = true;
                break;
            }
        }
        if (!anyBetter6) return false;

        for (int p = 3; p < 5; p++)
        {
            int q0 = query[p * 2];
            int q1 = query[p * 2 + 1];
            int pairOffset = blockBase + p * SearchConstants.Lanes * 2;
            for (int l = 0; l < SearchConstants.Lanes; l++)
            {
                int diff0 = q0 - vPtr[pairOffset + l * 2];
                int diff1 = q1 - vPtr[pairOffset + l * 2 + 1];
                outDists[l] += diff0 * diff0 + diff1 * diff1;
            }
        }

        bool anyBetter10 = false;
        for (int l = 0; l < SearchConstants.Lanes; l++)
        {
            if (outDists[l] < limit)
            {
                anyBetter10 = true;
                break;
            }
        }
        if (!anyBetter10) return false;

        for (int p = 5; p < 7; p++)
        {
            int q0 = query[p * 2];
            int q1 = query[p * 2 + 1];
            int pairOffset = blockBase + p * SearchConstants.Lanes * 2;
            for (int l = 0; l < SearchConstants.Lanes; l++)
            {
                int diff0 = q0 - vPtr[pairOffset + l * 2];
                int diff1 = q1 - vPtr[pairOffset + l * 2 + 1];
                outDists[l] += diff0 * diff0 + diff1 * diff1;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ScanBlockAvx2(byte* vectors, int blockBase, Span<short> query, Span<int> outDists, int limit)
    {
        Vector256<int> sum = Vector256<int>.Zero;
        short* vPtr = (short*)vectors;

        Vector256<int> limitMinusOne = Vector256.Create(limit - 1);

        // Pair 0
        {
            Vector128<short> q128 = Vector128.Create(
                query[0], query[1],
                query[0], query[1],
                query[0], query[1],
                query[0], query[1]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 0 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }
        // Pair 1
        {
            Vector128<short> q128 = Vector128.Create(
                query[2], query[3],
                query[2], query[3],
                query[2], query[3],
                query[2], query[3]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 1 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }
        // Pair 2
        {
            Vector128<short> q128 = Vector128.Create(
                query[4], query[5],
                query[4], query[5],
                query[4], query[5],
                query[4], query[5]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 2 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }

        // Early pruning after 6 dimensions (3 pairs)
        {
            Vector256<int> cmp = Avx2.CompareGreaterThan(sum, limitMinusOne);
            int mask = Avx2.MoveMask(cmp.AsByte());
            if ((uint)mask == 0xFFFFFFFF)
                return false;
        }

        // Pair 3
        {
            Vector128<short> q128 = Vector128.Create(
                query[6], query[7],
                query[6], query[7],
                query[6], query[7],
                query[6], query[7]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 3 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }
        // Pair 4
        {
            Vector128<short> q128 = Vector128.Create(
                query[8], query[9],
                query[8], query[9],
                query[8], query[9],
                query[8], query[9]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 4 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }

        // Early pruning after 10 dimensions (5 pairs)
        {
            Vector256<int> cmp = Avx2.CompareGreaterThan(sum, limitMinusOne);
            int mask = Avx2.MoveMask(cmp.AsByte());
            if ((uint)mask == 0xFFFFFFFF)
                return false;
        }

        // Pair 5
        {
            Vector128<short> q128 = Vector128.Create(
                query[10], query[11],
                query[10], query[11],
                query[10], query[11],
                query[10], query[11]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 5 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }
        // Pair 6
        {
            Vector128<short> q128 = Vector128.Create(
                query[12], query[13],
                query[12], query[13],
                query[12], query[13],
                query[12], query[13]
            );
            Vector256<short> q = Vector256.Create(q128, q128);
            Vector256<short> v = Unsafe.ReadUnaligned<Vector256<short>>(vPtr + blockBase + 6 * SearchConstants.Lanes * 2);
            Vector256<short> diff = Avx2.Subtract(q, v);
            sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(diff, diff));
        }

        fixed (int* outPtr = outDists)
        {
            Unsafe.WriteUnaligned(outPtr, sum);
        }
        return true;
    }

    public uint ComputePartitionKey(Span<short> vector)
    {
        uint key = 0;

        if (vector[9] > 0)
            key |= 1u << 0;
        if (vector[10] > 0)
            key |= 1u << 1;
        if (vector[11] > 0)
            key |= 1u << 2;
        if (vector[8] > 2048)
            key |= 1u << 3;
        if (vector[2] > 4096)
            key |= 1u << 4;

        uint bucket = 0;
        short v0 = vector[0];
        for (int i = 0; i < _partitionCutsV0.Length; i++)
        {
            if (v0 > _partitionCutsV0[i])
                bucket++;
            else
                break;
        }

        key |= bucket << 5;

        return key;
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

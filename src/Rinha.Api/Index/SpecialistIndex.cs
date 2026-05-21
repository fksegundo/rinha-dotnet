using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32.SafeHandles;

namespace Rinha.Api.Index;

public sealed unsafe class SpecialistIndex : IDisposable
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private SafeMemoryMappedViewHandle? _handle;
    private byte* _ptr;
    private byte* _partitionsPtr;
    private byte* _nodesPtr;
    private int _referenceCount;
    private int _partitionCount;
    private int _nodeCount;
    private int _blockCount;
    private byte* _vectorsPtr;
    private byte* _labelsPtr;

    private short[] _partitionByKey = [];
    private bool _hasAvx2;
    private SearchMode _searchMode;

    public static SpecialistIndex Open(string path)
    {
        var index = new SpecialistIndex();
        index.OpenInternal(path);
        return index;
    }

    private void OpenInternal(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _handle = _accessor.SafeMemoryMappedViewHandle;
        _handle.AcquirePointer(ref _ptr);

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

        _partitionsPtr = _ptr + 32;
        _nodesPtr = _partitionsPtr + (_partitionCount * IndexFormat.RecordSize);

        int vectorsOffset = (int)(_nodesPtr - _ptr) + (_nodeCount * IndexFormat.RecordSize);
        int labelsOffset = vectorsOffset + (_blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2);

        _vectorsPtr = _ptr + vectorsOffset;
        _labelsPtr = _ptr + labelsOffset;

        BuildPartitionLookup();

        IndexMemory.AdviseWillNeed((IntPtr)_ptr, (nint)_accessor!.Capacity);
        IndexMemory.AdviseWillNeed((IntPtr)_vectorsPtr, (nint)(_blockCount * SearchConstants.Dims * SearchConstants.Lanes * 2));
        IndexMemory.AdviseWillNeed((IntPtr)_labelsPtr, (nint)(_blockCount * SearchConstants.Lanes));

        _hasAvx2 = Avx2.IsSupported;
        _searchMode = (Environment.GetEnvironmentVariable("RINHA_SEARCH_MODE") ?? "key-first") switch
        {
            "exact" => SearchMode.Exact,
            "specialist" => SearchMode.Specialist,
            "key-first" or "key_first" => SearchMode.KeyFirst,
            _ => SearchMode.KeyFirst
        };
    }

    private void BuildPartitionLookup()
    {
        _partitionByKey = new short[IndexFormat.PartitionKeySlots];
        Array.Fill(_partitionByKey, (short)-1);

        for (int i = 0; i < _partitionCount; i++)
        {
            uint key = (uint)ReadI32(_partitionsPtr, i * IndexFormat.RecordSize);
            if (key < IndexFormat.PartitionKeySlots)
                _partitionByKey[key] = (short)i;
        }
    }

    public byte PredictFraudCount(Span<short> query)
    {
        Span<long> bestDists = stackalloc long[SearchConstants.K];
        Span<byte> bestLabels = stackalloc byte[SearchConstants.K];
        bestDists.Fill(long.MaxValue);
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
        Span<long> bestDists = stackalloc long[SearchConstants.K];
        Span<byte> bestLabels = stackalloc byte[SearchConstants.K];
        bestDists.Fill(long.MaxValue);
        bestLabels.Clear();
        SearchExact(query, bestDists, bestLabels);

        int fraudCount = 0;
        foreach (var label in bestLabels)
            fraudCount += label;
        return (byte)fraudCount;
    }

    private void SearchExact(Span<short> query, Span<long> bestDists, Span<byte> bestLabels)
    {
        Span<long> blockDists = stackalloc long[SearchConstants.Lanes];

        for (int b = 0; b < _blockCount; b++)
        {
            int blockBase = b * SearchConstants.Dims * SearchConstants.Lanes;

            if (_hasAvx2)
                ScanBlockAvx2(_vectorsPtr, blockBase, query, blockDists);
            else
                ScanBlockScalar(_vectorsPtr, blockBase, query, blockDists);

            int labelsBase = b * SearchConstants.Lanes;
            int remaining = _referenceCount - b * SearchConstants.Lanes;
            int laneCount = Math.Min(remaining, SearchConstants.Lanes);

            for (int i = 0; i < laneCount; i++)
                InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
        }
    }

    private void SearchSpecialist(Span<short> query, Span<long> bestDists, Span<byte> bestLabels)
    {
        Span<(long bound, int idx)> partitionEntries = stackalloc (long, int)[SearchConstants.MaxPartitions];
        int partitionLen = 0;

        for (int idx = 0; idx < _partitionCount; idx++)
        {
            byte* record = _partitionsPtr + (idx * IndexFormat.RecordSize);
            long bound = LowerBoundBoxRecord(query, record);
            partitionEntries[partitionLen++] = (bound, idx);
        }

        partitionEntries.Slice(0, partitionLen).Sort((a, b) => a.bound.CompareTo(b.bound));

        for (int i = 0; i < partitionLen; i++)
        {
            var (bound, idx) = partitionEntries[i];
            if (bound >= bestDists[SearchConstants.K - 1])
                break;

            SearchNodeIterative(ReadI32(_partitionsPtr, idx * IndexFormat.RecordSize + 4), bound, query, bestDists, bestLabels);
        }
    }

    private void SearchKeyFirst(Span<short> query, Span<long> bestDists, Span<byte> bestLabels)
    {
        uint queryKey = ComputePartitionKey(query);
        int primaryIdx = queryKey < IndexFormat.PartitionKeySlots ? _partitionByKey[queryKey] : -1;

        if (primaryIdx >= 0)
        {
            byte* record = _partitionsPtr + (primaryIdx * IndexFormat.RecordSize);
            long bound = LowerBoundBoxRecord(query, record);
            if (bound < bestDists[SearchConstants.K - 1])
                SearchNodeIterative(ReadI32(record, 4), bound, query, bestDists, bestLabels);
        }

        Span<(long bound, int idx)> partitionEntries = stackalloc (long, int)[SearchConstants.MaxPartitions];
        int partitionLen = 0;

        for (int idx = 0; idx < _partitionCount; idx++)
        {
            if (idx == primaryIdx)
                continue;

            byte* record = _partitionsPtr + (idx * IndexFormat.RecordSize);
            long bound = LowerBoundBoxRecord(query, record);
            partitionEntries[partitionLen++] = (bound, idx);
        }

        partitionEntries.Slice(0, partitionLen).Sort((a, b) => a.bound.CompareTo(b.bound));

        for (int i = 0; i < partitionLen; i++)
        {
            var (bound, idx) = partitionEntries[i];
            if (bound >= bestDists[SearchConstants.K - 1])
                break;

            SearchNodeIterative(ReadI32(_partitionsPtr, idx * IndexFormat.RecordSize + 4), bound, query, bestDists, bestLabels);
        }
    }

    private void SearchNodeIterative(int root, long rootBound, Span<short> query, Span<long> bestDists, Span<byte> bestLabels)
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
                byte* record = _nodesPtr + (current * IndexFormat.RecordSize);
                int left = ReadI32(record, 0);
                int right = ReadI32(record, 4);

                if (left < 0 || right < 0)
                {
                    ScanLeaf(ReadI32(record, 8), ReadI32(record, 12), query, bestDists, bestLabels);
                }
                else
                {
                    byte* leftRecord = _nodesPtr + (left * IndexFormat.RecordSize);
                    byte* rightRecord = _nodesPtr + (right * IndexFormat.RecordSize);
                    long lb = LowerBoundBoxRecord(query, leftRecord);
                    long rb = LowerBoundBoxRecord(query, rightRecord);

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

    private void ScanLeaf(int startBlock, int len, Span<short> query, Span<long> bestDists, Span<byte> bestLabels)
    {
        int blocks = (len + SearchConstants.Lanes - 1) / SearchConstants.Lanes;
        Span<long> blockDists = stackalloc long[SearchConstants.Lanes];

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
                ScanBlockAvx2(_vectorsPtr, blockBase, query, blockDists);
            else
                ScanBlockScalar(_vectorsPtr, blockBase, query, blockDists);

            int labelsBase = blockIdx * SearchConstants.Lanes;
            int laneCount = Math.Min(len - b * SearchConstants.Lanes, SearchConstants.Lanes);

            for (int i = 0; i < laneCount; i++)
                InsertBest(blockDists[i], _labelsPtr[labelsBase + i], bestDists, bestLabels);
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertBest(long dist, byte label, Span<long> bestDists, Span<byte> bestLabels)
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
    private static unsafe void ScanBlockScalar(byte* vectors, int blockBase, Span<short> query, Span<long> outDists)
    {
        outDists.Clear();
        short* vPtr = (short*)vectors;
        for (int d = 0; d < SearchConstants.Dims; d++)
        {
            long q = query[d];
            for (int i = 0; i < SearchConstants.Lanes; i++)
            {
                long diff = q - vPtr[blockBase + d * SearchConstants.Lanes + i];
                outDists[i] += diff * diff;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ScanBlockAvx2(byte* vectors, int blockBase, Span<short> query, Span<long> outDists)
    {
        Vector256<int> sum32 = Vector256<int>.Zero;
        Vector256<long> sum64Lo = Vector256<long>.Zero;
        Vector256<long> sum64Hi = Vector256<long>.Zero;
        short* vPtr = (short*)vectors;

        for (int d = 0; d < SearchConstants.Dims; d++)
        {
            Vector128<short> qVec = Vector128.Create(query[d]);
            Vector128<short> vVec = Unsafe.ReadUnaligned<Vector128<short>>(vPtr + blockBase + d * SearchConstants.Lanes);
            Vector128<short> diff = Sse2.Subtract(qVec, vVec);
            Vector256<int> diff32 = Avx2.ConvertToVector256Int32(diff);
            Vector256<int> sq = Avx2.MultiplyLow(diff32, diff32);
            sum32 = Avx2.Add(sum32, sq);

            if ((d + 1) % 4 == 0)
            {
                Vector256<long> lo = Avx2.ConvertToVector256Int64(sum32.GetLower());
                Vector256<long> hi = Avx2.ConvertToVector256Int64(sum32.GetUpper());
                sum64Lo = Avx2.Add(sum64Lo, lo);
                sum64Hi = Avx2.Add(sum64Hi, hi);
                sum32 = Vector256<int>.Zero;
            }
        }

        Vector256<long> loFinal = Avx2.ConvertToVector256Int64(sum32.GetLower());
        Vector256<long> hiFinal = Avx2.ConvertToVector256Int64(sum32.GetUpper());
        sum64Lo = Avx2.Add(sum64Lo, loFinal);
        sum64Hi = Avx2.Add(sum64Hi, hiFinal);

        fixed (long* outPtr = outDists)
        {
            Unsafe.WriteUnaligned(outPtr, sum64Lo);
            Unsafe.WriteUnaligned(outPtr + 4, sum64Hi);
        }
    }

    public static uint ComputePartitionKey(Span<short> vector)
    {
        uint key = 0;

        if (vector[5] >= 0)
            key |= 1u << 0;

        if (vector[9] > 0)
            key |= 1u << 1;

        if (vector[10] > 0)
            key |= 1u << 2;

        if (vector[11] > 0)
            key |= 1u << 3;

        int mccBucket = vector[12] switch
        {
            <= 2047 => 0,
            <= 4095 => 1,
            <= 6143 => 2,
            _ => 3
        };
        key |= (uint)(mccBucket << 4);

        if (vector[2] > 4096)
            key |= 1u << 6;

        if (vector[8] > 2048)
            key |= 1u << 7;

        return key;
    }

    private static string ReadString(byte* ptr, int offset, int length)
    {
        return System.Text.Encoding.ASCII.GetString(ptr + offset, length);
    }

    private static int ReadI32(byte* ptr, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ptr + offset, 4));
    }

    public void Dispose()
    {
        if (_handle is not null)
            _handle.ReleasePointer();
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AppleMusicTranslator.Services;

public sealed class ProcessMemoryTtmlExtractor
{
    private const int MemCommit = 0x1000;
    private const int PageNoAccess = 0x01;
    private const int PageGuard = 0x100;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const ulong MaxUserModeAddress = 0x00007FFFFFFFFFFF;
    private const int ScanChunkSize = 4 * 1024 * 1024;
    private const int MaxTtmlBytes = 2 * 1024 * 1024;
    private const int MaxBlocks = 48;
    private const int MaxReadsPerScan = 40;
    private const int MaxAnchoredReadsPerScan = 128;
    private const int MaxCachedAddressesPerProcess = 32;
    private static readonly TimeSpan MaxFullScanDuration = TimeSpan.FromSeconds(6);

    private readonly object _addressCacheLock = new();
    private readonly Dictionary<CacheScope, ScopeCache> _ttmlAddressCache = new();

    private static readonly TtmlPattern[] TtmlPatterns =
    [
        new("utf16", Encoding.Unicode.GetBytes("<tt xmlns"), Encoding.Unicode.GetBytes("</tt>")),
        new("utf16-generic", Encoding.Unicode.GetBytes("<tt "), Encoding.Unicode.GetBytes("</tt>")),
        new("utf8", Encoding.UTF8.GetBytes("<tt xmlns"), Encoding.UTF8.GetBytes("</tt>")),
        new("utf8-generic", Encoding.UTF8.GetBytes("<tt "), Encoding.UTF8.GetBytes("</tt>"))
    ];

    public Task<IReadOnlyList<byte[]>> ExtractAllAsync(int processId, string cacheScopeKey, CancellationToken cancellationToken) =>
        Task.Run(() => ExtractAll(processId, cacheScopeKey, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<byte[]>> ExtractCachedAsync(int processId, string cacheScopeKey, CancellationToken cancellationToken) =>
        Task.Run(() => ExtractCached(processId, cacheScopeKey, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<byte[]>> ExtractLikelyCurrentAsync(
        int processId,
        string cacheScopeKey,
        IReadOnlyCollection<string> anchors,
        CancellationToken cancellationToken) =>
        Task.Run(() => ExtractLikelyCurrent(processId, cacheScopeKey, anchors, cancellationToken), cancellationToken);

    public IReadOnlyList<byte[]> ExtractAll(int processId, string cacheScopeKey, CancellationToken cancellationToken)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return Array.Empty<byte[]>();
        }

        try
        {
            var cached = ReadCachedBlocks(handle, processId, cacheScopeKey, cancellationToken);
            var scanned = ScanProcess(handle, cancellationToken, address => RememberTtmlAddress(processId, cacheScopeKey, address));
            return MergeBlocks(cached, scanned);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public IReadOnlyList<byte[]> ExtractCached(int processId, string cacheScopeKey, CancellationToken cancellationToken)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return Array.Empty<byte[]>();
        }

        try
        {
            return ReadCachedBlocks(handle, processId, cacheScopeKey, cancellationToken);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public IReadOnlyList<byte[]> ExtractLikelyCurrent(
        int processId,
        string cacheScopeKey,
        IReadOnlyCollection<string> anchors,
        CancellationToken cancellationToken)
    {
        var usefulAnchors = anchors
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor) && anchor.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();

        if (usefulAnchors.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return Array.Empty<byte[]>();
        }

        try
        {
            var blocks = ScanAnchoredBlocks(handle, usefulAnchors, cancellationToken, address => RememberTtmlAddress(processId, cacheScopeKey, address));
            return blocks;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public IReadOnlyList<byte[]> ExtractAroundAddresses(
        int processId,
        string cacheScopeKey,
        IReadOnlyCollection<ulong> addresses,
        CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return Array.Empty<byte[]>();
        }

        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return Array.Empty<byte[]>();
        }

        try
        {
            var blocks = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = addresses.Distinct().OrderBy(address => address).Take(MaxReadsPerScan).ToArray();

            foreach (var address in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var pattern in TtmlPatterns)
                {
                    var ttml = ReadTtmlAt(handle, address, pattern.End);
                    if (ttml is null)
                    {
                        continue;
                    }

                    var fingerprint = Fingerprint(ttml);
                    if (seen.Add(fingerprint))
                    {
                        RememberTtmlAddress(processId, cacheScopeKey, address);
                        blocks.Add(ttml);
                    }
                }
            }

            return blocks;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private IReadOnlyList<byte[]> ReadCachedBlocks(
        IntPtr processHandle,
        int processId,
        string cacheScopeKey,
        CancellationToken cancellationToken)
    {
        var addresses = SnapshotCachedAddresses(processId, cacheScopeKey);
        if (addresses.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        var blocks = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cachedAddress in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cachedAddress.IsExpired)
            {
                continue;
            }

            var address = cachedAddress.Address;

            foreach (var pattern in TtmlPatterns)
            {
                var ttml = ReadTtmlAt(processHandle, address, pattern.End);
                if (ttml is null)
                {
                    continue;
                }

                var fingerprint = Fingerprint(ttml);
                if (seen.Add(fingerprint))
                {
                    blocks.Add(ttml);
                }
            }
        }

        return blocks;
    }

    private void RememberTtmlAddress(int processId, string cacheScopeKey, ulong address)
    {
        if (address == 0 || string.IsNullOrWhiteSpace(cacheScopeKey))
        {
            return;
        }

        lock (_addressCacheLock)
        {
            var scope = new CacheScope(processId, cacheScopeKey);
            if (!_ttmlAddressCache.TryGetValue(scope, out var cache))
            {
                cache = new ScopeCache();
                _ttmlAddressCache[scope] = cache;
            }

            cache.LastSeenUtc = DateTime.UtcNow;

            var existingIndex = cache.Addresses.FindIndex(item => item.Address == address);
            if (existingIndex >= 0)
            {
                cache.Addresses[existingIndex] = new TrackedAddress(address, DateTime.UtcNow);
            }
            else
            {
                cache.Addresses.Add(new TrackedAddress(address, DateTime.UtcNow));
            }

            if (cache.Addresses.Count > MaxCachedAddressesPerProcess)
            {
                cache.Addresses.RemoveRange(0, cache.Addresses.Count - MaxCachedAddressesPerProcess);
            }

            PruneExpiredScopesLocked();
        }
    }

    private TrackedAddress[] SnapshotCachedAddresses(int processId, string cacheScopeKey)
    {
        lock (_addressCacheLock)
        {
            var scope = new CacheScope(processId, cacheScopeKey);
            if (!_ttmlAddressCache.TryGetValue(scope, out var cache) || cache.Addresses.Count == 0)
            {
                return Array.Empty<TrackedAddress>();
            }

            cache.LastSeenUtc = DateTime.UtcNow;
            return cache.Addresses.ToArray();
        }
    }

    private static IReadOnlyList<byte[]> MergeBlocks(IReadOnlyList<byte[]> first, IReadOnlyList<byte[]> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var merged = new List<byte[]>(first.Count + second.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in first.Concat(second))
        {
            var fingerprint = Fingerprint(block);
            if (seen.Add(fingerprint))
            {
                merged.Add(block);
            }
        }

        return merged;
    }

    private static IReadOnlyList<byte[]> ScanProcess(
        IntPtr processHandle,
        CancellationToken cancellationToken,
        Action<ulong>? rememberAddress)
    {
        var blocks = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong address = 0;
        var startedAt = DateTime.UtcNow;

        while (address < MaxUserModeAddress && blocks.Count < MaxBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - startedAt > MaxFullScanDuration)
            {
                break;
            }

            if (VirtualQueryEx(processHandle, new IntPtr(unchecked((long)address)), out var mbi, (uint)Marshal.SizeOf<MemoryBasicInformation64>()) == UIntPtr.Zero)
            {
                address += 0x10000;
                continue;
            }

            var nextAddress = mbi.BaseAddress + mbi.RegionSize;
            if (IsReadableCommittedRegion(mbi))
            {
                ScanRegion(processHandle, mbi.BaseAddress, mbi.RegionSize, blocks, seen, cancellationToken, rememberAddress);
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return blocks;
    }

    private static IReadOnlyList<byte[]> ScanAnchoredBlocks(
        IntPtr processHandle,
        IReadOnlyList<string> anchors,
        CancellationToken cancellationToken,
        Action<ulong>? rememberAddress)
    {
        var blocks = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anchorPatterns = anchors
            .SelectMany(anchor => new[]
            {
                new AnchorPattern(anchor, Encoding.Unicode.GetBytes(anchor), Encoding.Unicode),
                new AnchorPattern(anchor, Encoding.UTF8.GetBytes(anchor), Encoding.UTF8)
            })
            .ToArray();

        ulong address = 0;
        var readCount = 0;

        while (address < MaxUserModeAddress && blocks.Count < 8 && readCount < MaxAnchoredReadsPerScan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (VirtualQueryEx(processHandle, new IntPtr(unchecked((long)address)), out var mbi, (uint)Marshal.SizeOf<MemoryBasicInformation64>()) == UIntPtr.Zero)
            {
                address += 0x10000;
                continue;
            }

            var nextAddress = mbi.BaseAddress + mbi.RegionSize;
            if (IsReadableCommittedRegion(mbi))
            {
                readCount++;
                TryScanAnchorsInRegion(processHandle, mbi.BaseAddress, mbi.RegionSize, anchorPatterns, blocks, seen, cancellationToken, rememberAddress);
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return blocks;
    }

    private static bool TryScanAnchorsInRegion(
        IntPtr processHandle,
        ulong baseAddress,
        ulong regionSize,
        IReadOnlyList<AnchorPattern> anchorPatterns,
        List<byte[]> blocks,
        HashSet<string> seen,
        CancellationToken cancellationToken,
        Action<ulong>? rememberAddress)
    {
        var foundAny = false;
        var offset = 0UL;
        byte[] tail = [];
        var overlap = Math.Max(anchorPatterns.Max(pattern => pattern.Bytes.Length), TtmlPatterns.Max(pattern => pattern.Start.Length)) + 16;

        while (offset < regionSize && blocks.Count < 8)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)ScanChunkSize, regionSize - offset);
            var chunk = ReadBytes(processHandle, baseAddress + offset, bytesToRead);
            if (chunk.Length == 0)
            {
                offset += (ulong)bytesToRead;
                tail = [];
                continue;
            }

            var scanBuffer = Combine(tail, chunk);
            var scanBufferStart = baseAddress + offset - (ulong)Math.Min(tail.Length, (int)offset);

            foreach (var anchor in anchorPatterns)
            {
                var pos = IndexOf(scanBuffer, anchor.Bytes, 0);
                if (pos < 0)
                {
                    continue;
                }

                foundAny = true;
                var absoluteAnchor = scanBufferStart + (ulong)pos;
                TryExtractAroundAnchor(processHandle, absoluteAnchor, blocks, seen, rememberAddress);
                if (blocks.Count >= 8)
                {
                    break;
                }
            }

            tail = KeepTail(scanBuffer, overlap);
            offset += (ulong)bytesToRead;
        }

        return foundAny;
    }

    private static void TryExtractAroundAnchor(
        IntPtr processHandle,
        ulong absoluteAnchor,
        List<byte[]> blocks,
        HashSet<string> seen,
        Action<ulong>? rememberAddress)
    {
        var windowStart = absoluteAnchor > (ulong)MaxTtmlBytes ? absoluteAnchor - (ulong)MaxTtmlBytes : 0;
        var data = ReadBytes(processHandle, windowStart, MaxTtmlBytes * 2);
        if (data.Length == 0)
        {
            return;
        }

        foreach (var pattern in TtmlPatterns)
        {
            var anchorOffset = (int)Math.Min(absoluteAnchor - windowStart, (ulong)Math.Max(data.Length - 1, 0));
            var start = LastIndexOf(data, pattern.Start, anchorOffset);
            if (start < 0)
            {
                continue;
            }

            var end = IndexOf(data, pattern.End, anchorOffset);
            if (end < 0 || end <= start)
            {
                continue;
            }

            var ttml = data[start..(end + pattern.End.Length)];
            var fingerprint = Fingerprint(ttml);
            if (seen.Add(fingerprint))
            {
                rememberAddress?.Invoke(windowStart + (ulong)start);
                blocks.Add(ttml);
            }
        }
    }

    private static void ScanRegion(
        IntPtr processHandle,
        ulong baseAddress,
        ulong regionSize,
        List<byte[]> blocks,
        HashSet<string> seen,
        CancellationToken cancellationToken,
        Action<ulong>? rememberAddress)
    {
        var offset = 0UL;
        var overlap = (ulong)Math.Max(TtmlPatterns.Max(pattern => pattern.Start.Length) - 2, 0);
        byte[] tail = [];

        while (offset < regionSize && blocks.Count < MaxBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)ScanChunkSize, regionSize - offset);
            var chunk = ReadBytes(processHandle, baseAddress + offset, bytesToRead);
            if (chunk.Length == 0)
            {
                offset += (ulong)bytesToRead;
                tail = [];
                continue;
            }

            var scanBuffer = Combine(tail, chunk);
            var searchStart = 0;

            foreach (var pattern in TtmlPatterns)
            {
                searchStart = 0;

                while (blocks.Count < MaxBlocks)
                {
                    var pos = IndexOf(scanBuffer, pattern.Start, searchStart);
                    if (pos < 0)
                    {
                        break;
                    }

                    var scanBufferStart = baseAddress + offset - (ulong)Math.Min(tail.Length, (int)offset);
                    var absoluteMatch = scanBufferStart + (ulong)pos;
                    var ttml = ReadTtmlAt(processHandle, absoluteMatch, pattern.End);
                    if (ttml is not null)
                    {
                        var fingerprint = Fingerprint(ttml);
                        if (seen.Add(fingerprint))
                        {
                            rememberAddress?.Invoke(absoluteMatch);
                            blocks.Add(ttml);
                        }
                    }

                    searchStart = pos + pattern.Start.Length;
                }
            }

            tail = KeepTail(scanBuffer, (int)overlap);
            offset += (ulong)bytesToRead;
        }
    }

    private static byte[]? ReadTtmlAt(IntPtr processHandle, ulong address, byte[] endPattern)
    {
        var data = ReadBytes(processHandle, address, MaxTtmlBytes);
        if (data.Length == 0)
        {
            return null;
        }

        var end = IndexOf(data, endPattern, 0);
        return end < 0 ? null : data[..(end + endPattern.Length)];
    }

    private static bool IsReadableCommittedRegion(MemoryBasicInformation64 mbi) =>
        mbi.State == MemCommit
        && (mbi.Protect & PageNoAccess) == 0
        && (mbi.Protect & PageGuard) == 0
        && mbi.RegionSize >= (ulong)TtmlPatterns.Min(pattern => pattern.Start.Length);

    private static byte[] ReadBytes(IntPtr processHandle, ulong address, int size)
    {
        var buffer = new byte[size];
        _ = ReadProcessMemory(processHandle, new IntPtr(unchecked((long)address)), buffer, size, out var bytesRead);
        return bytesRead > 0
            ? buffer.AsSpan(0, bytesRead).ToArray()
            : [];
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        var combined = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);
        return combined;
    }

    private static byte[] KeepTail(byte[] buffer, int length)
    {
        if (length <= 0 || buffer.Length == 0)
        {
            return [];
        }

        var count = Math.Min(length, buffer.Length);
        var tail = new byte[count];
        Buffer.BlockCopy(buffer, buffer.Length - count, tail, 0, count);
        return tail;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length || start >= haystack.Length)
        {
            return -1;
        }

        for (var i = Math.Max(start, 0); i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle, int before)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        var start = Math.Min(before, haystack.Length - needle.Length);
        for (var i = start; i >= 0; i--)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Fingerprint(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed record TtmlPattern(string EncodingName, byte[] Start, byte[] End);

    private sealed record AnchorPattern(string Text, byte[] Bytes, Encoding Encoding);

    private sealed record CacheScope(int ProcessId, string TrackKey);

    private sealed class ScopeCache
    {
        public List<TrackedAddress> Addresses { get; } = [];

        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed record TrackedAddress(ulong Address, DateTime LastSeen)
    {
        public bool IsExpired => DateTime.UtcNow - LastSeen > TimeSpan.FromMinutes(3);
    }

    private void PruneExpiredScopesLocked()
    {
        if (_ttmlAddressCache.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expiredScopes = _ttmlAddressCache
            .Where(item => now - item.Value.LastSeenUtc > TimeSpan.FromMinutes(12))
            .Select(item => item.Key)
            .ToArray();

        foreach (var scope in expiredScopes)
        {
            _ttmlAddressCache.Remove(scope);
        }

        if (_ttmlAddressCache.Count <= 24)
        {
            return;
        }

        foreach (var scope in _ttmlAddressCache
                     .OrderBy(item => item.Value.LastSeenUtc)
                     .Take(_ttmlAddressCache.Count - 24)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _ttmlAddressCache.Remove(scope);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MemoryBasicInformation64 lpBuffer,
        uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

using System.Runtime.InteropServices;
using System.Text;

internal sealed class MemoryStringSearcher
{
    private const int MemCommit = 0x1000;
    private const int PageNoAccess = 0x01;
    private const int PageGuard = 0x100;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const ulong MaxUserModeAddress = 0x00007FFFFFFFFFFF;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int ContextBytes = 8192;
    private const int MaxHitsPerQuery = 20;

    public IReadOnlyList<SearchHit> Search(int processId, IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var patterns = queries
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .SelectMany(query => new[]
                {
                    new SearchPattern(query, "utf16", Encoding.Unicode.GetBytes(query), Encoding.Unicode),
                    new SearchPattern(query, "utf8", Encoding.UTF8.GetBytes(query), Encoding.UTF8)
                })
                .ToArray();

            return Scan(handle, patterns, cancellationToken);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IReadOnlyList<SearchHit> Scan(IntPtr processHandle, IReadOnlyList<SearchPattern> patterns, CancellationToken cancellationToken)
    {
        var hits = new List<SearchHit>();
        var hitCounts = patterns.ToDictionary(pattern => pattern.Key, _ => 0, StringComparer.Ordinal);
        ulong address = 0;

        while (address < MaxUserModeAddress && hitCounts.Values.Any(count => count < MaxHitsPerQuery))
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
                ScanRegion(processHandle, mbi, patterns, hitCounts, hits, cancellationToken);
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return hits;
    }

    private static void ScanRegion(
        IntPtr processHandle,
        MemoryBasicInformation64 mbi,
        IReadOnlyList<SearchPattern> patterns,
        Dictionary<string, int> hitCounts,
        List<SearchHit> hits,
        CancellationToken cancellationToken)
    {
        var offset = 0UL;
        byte[] tail = [];
        var tailLength = patterns.Max(pattern => pattern.Bytes.Length) + 8;

        while (offset < mbi.RegionSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)ChunkSize, mbi.RegionSize - offset);
            var chunk = ReadBytes(processHandle, mbi.BaseAddress + offset, bytesToRead);
            if (chunk.Length == 0)
            {
                offset += (ulong)bytesToRead;
                tail = [];
                continue;
            }

            var buffer = Combine(tail, chunk);
            var bufferStart = mbi.BaseAddress + offset - (ulong)Math.Min(tail.Length, (int)offset);

            foreach (var pattern in patterns)
            {
                if (hitCounts[pattern.Key] >= MaxHitsPerQuery)
                {
                    continue;
                }

                var searchFrom = 0;
                while (hitCounts[pattern.Key] < MaxHitsPerQuery)
                {
                    var pos = IndexOf(buffer, pattern.Bytes, searchFrom);
                    if (pos < 0)
                    {
                        break;
                    }

                    var absolute = bufferStart + (ulong)pos;
                    var context = ReadContext(processHandle, absolute, pattern.Encoding);
                    hits.Add(new SearchHit(
                        pattern.Query,
                        pattern.EncodingName,
                        absolute,
                        mbi.BaseAddress,
                        mbi.RegionSize,
                        context));

                    hitCounts[pattern.Key]++;
                    searchFrom = pos + Math.Max(1, pattern.Bytes.Length);
                }
            }

            tail = KeepTail(buffer, tailLength);
            offset += (ulong)bytesToRead;
        }
    }

    private static string ReadContext(IntPtr processHandle, ulong address, Encoding encoding)
    {
        var start = address > ContextBytes / 2 ? address - ContextBytes / 2 : 0;
        var bytes = ReadBytes(processHandle, start, ContextBytes);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var text = encoding.GetString(bytes)
            .Replace('\0', ' ')
            .Replace("\r", " ")
            .Replace("\n", " ");

        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ");
        }

        return text.Trim();
    }

    private static bool IsReadableCommittedRegion(MemoryBasicInformation64 mbi) =>
        mbi.State == MemCommit
        && (mbi.Protect & PageNoAccess) == 0
        && (mbi.Protect & PageGuard) == 0
        && mbi.RegionSize > 0;

    private static byte[] ReadBytes(IntPtr processHandle, ulong address, int size)
    {
        var buffer = new byte[size];
        _ = ReadProcessMemory(processHandle, new IntPtr(unchecked((long)address)), buffer, size, out var bytesRead);
        return bytesRead > 0 ? buffer.AsSpan(0, bytesRead).ToArray() : [];
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
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
        var count = Math.Min(length, buffer.Length);
        var tail = new byte[count];
        Buffer.BlockCopy(buffer, buffer.Length - count, tail, 0, count);
        return tail;
    }

    private sealed record SearchPattern(string Query, string EncodingName, byte[] Bytes, Encoding Encoding)
    {
        public string Key => $"{EncodingName}:{Query}";
    }

    public sealed record SearchHit(
        string Query,
        string EncodingName,
        ulong Address,
        ulong RegionBase,
        ulong RegionSize,
        string Context);

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

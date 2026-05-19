using System.Runtime.InteropServices;
using System.Text;

internal sealed class MemoryTokenProbe
{
    private const int MemCommit = 0x1000;
    private const int PageNoAccess = 0x01;
    private const int PageGuard = 0x100;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const ulong MaxUserModeAddress = 0x00007FFFFFFFFFFF;
    private const int ChunkSize = 2 * 1024 * 1024;

    private readonly IReadOnlyList<Token> _tokens =
    [
        Token.Utf16("<tt"),
        Token.Utf16("<tt "),
        Token.Utf16("<tt xmlns"),
        Token.Utf16("</tt>"),
        Token.Utf16("begin=\""),
        Token.Utf16("lyrics"),
        Token.Utf16("syncedLyrics"),
        Token.Utf16("songLyrics"),
        Token.Utf8("<tt"),
        Token.Utf8("<tt "),
        Token.Utf8("<tt xmlns"),
        Token.Utf8("</tt>"),
        Token.Utf8("begin=\""),
        Token.Utf8("lyrics"),
        Token.Utf8("syncedLyrics"),
        Token.Utf8("songLyrics"),
    ];

    public ProbeResult Scan(int processId, CancellationToken cancellationToken)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return new ProbeResult(new Dictionary<string, int>(StringComparer.Ordinal), "OpenProcess failed");
        }

        try
        {
            return ScanHandle(handle, cancellationToken);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private ProbeResult ScanHandle(IntPtr processHandle, CancellationToken cancellationToken)
    {
        var counts = _tokens.ToDictionary(token => token.Name, _ => 0, StringComparer.Ordinal);
        string? firstXmlContext = null;
        ulong address = 0;

        while (address < MaxUserModeAddress)
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
                ScanRegion(processHandle, mbi.BaseAddress, mbi.RegionSize, counts, ref firstXmlContext, cancellationToken);
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return new ProbeResult(counts, firstXmlContext ?? "no <tt context found");
    }

    private void ScanRegion(
        IntPtr processHandle,
        ulong baseAddress,
        ulong regionSize,
        Dictionary<string, int> counts,
        ref string? firstXmlContext,
        CancellationToken cancellationToken)
    {
        var offset = 0UL;
        byte[] tail = [];

        while (offset < regionSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)ChunkSize, regionSize - offset);
            var chunk = ReadBytes(processHandle, baseAddress + offset, bytesToRead);
            if (chunk.Length == 0)
            {
                offset += (ulong)bytesToRead;
                tail = [];
                continue;
            }

            var buffer = Combine(tail, chunk);
            foreach (var token in _tokens)
            {
                var found = CountOccurrences(buffer, token.Bytes);
                if (found > 0)
                {
                    counts[token.Name] += found;
                }
            }

            firstXmlContext ??= FindContext(buffer, Token.Utf16("<tt").Bytes, Encoding.Unicode)
                ?? FindContext(buffer, Token.Utf8("<tt").Bytes, Encoding.UTF8);

            tail = KeepTail(buffer, 64);
            offset += (ulong)bytesToRead;
        }
    }

    private static bool IsReadableCommittedRegion(MemoryBasicInformation64 mbi) =>
        mbi.State == MemCommit
        && (mbi.Protect & PageNoAccess) == 0
        && (mbi.Protect & PageGuard) == 0
        && mbi.RegionSize > 0;

    private static string? FindContext(byte[] buffer, byte[] token, Encoding encoding)
    {
        var pos = IndexOf(buffer, token, 0);
        if (pos < 0)
        {
            return null;
        }

        var start = Math.Max(0, pos - 96);
        var length = Math.Min(buffer.Length - start, 640);
        var text = encoding.GetString(buffer, start, length).Replace('\0', ' ');
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static int CountOccurrences(byte[] buffer, byte[] token)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var pos = IndexOf(buffer, token, offset);
            if (pos < 0)
            {
                return count;
            }

            count++;
            offset = pos + Math.Max(1, token.Length);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
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

    private static byte[] ReadBytes(IntPtr processHandle, ulong address, int size)
    {
        var buffer = new byte[size];
        _ = ReadProcessMemory(processHandle, new IntPtr(unchecked((long)address)), buffer, size, out var bytesRead);
        return bytesRead > 0 ? buffer.AsSpan(0, bytesRead).ToArray() : [];
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

    private sealed record Token(string Name, byte[] Bytes)
    {
        public static Token Utf16(string text) => new($"utf16:{text}", Encoding.Unicode.GetBytes(text));

        public static Token Utf8(string text) => new($"utf8:{text}", Encoding.UTF8.GetBytes(text));
    }

    public sealed record ProbeResult(IReadOnlyDictionary<string, int> Counts, string Context);

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

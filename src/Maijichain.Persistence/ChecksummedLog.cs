using System.Security.Cryptography;
using System.Text;

namespace Maijichain.Persistence;

public sealed class JournalCorruptedException(string message) : Exception(message);

/// <summary>
/// JSON-lines file where every line is <c>json + TAB + first 16 hex chars of SHA-256(json)</c>.
/// A final line without its newline is a torn write from a crash and is discarded; any checksum failure is corruption.
/// </summary>
internal static class ChecksummedLog
{
    public static byte[] Encode(string json) => Encoding.UTF8.GetBytes($"{json}\t{Checksum(json)}\n");

    public static ReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new ReadResult([], 0, false);
        }

        byte[] bytes = File.ReadAllBytes(path);
        var lines = new List<string>();
        long offset = 0;
        while (offset < bytes.Length)
        {
            int newline = Array.IndexOf(bytes, (byte)'\n', (int)offset);
            bool complete = newline >= 0;
            int end = complete ? newline : bytes.Length;
            string raw = Encoding.UTF8.GetString(bytes, (int)offset, end - (int)offset);
            if (!complete)
            {
                // Records are written as one "line + newline" write, so a final segment without a newline is an
                // interrupted write that was never acknowledged. Anything else that fails is real corruption.
                return new ReadResult(lines, offset, TornTail: true);
            }

            if (!TryDecode(raw, out string json))
            {
                throw new JournalCorruptedException($"{Path.GetFileName(path)}: line {lines.Count + 1} failed its checksum.");
            }

            lines.Add(json);
            offset = end + 1;
        }

        return new ReadResult(lines, offset, false);
    }

    /// <summary>Atomically replaces the file contents (write temp file, flush, rename).</summary>
    public static void Rewrite(string path, IEnumerable<string> jsonLines)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (string json in jsonLines)
            {
                stream.Write(Encode(json));
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Appends one line after truncating any torn tail beyond <paramref name="goodLength"/>.</summary>
    public static long Append(string path, long goodLength, string json)
    {
        byte[] line = Encode(json);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (stream.Length < goodLength)
        {
            throw new JournalCorruptedException($"{Path.GetFileName(path)} is shorter than its last known good length.");
        }

        if (stream.Length > goodLength)
        {
            stream.SetLength(goodLength);
        }

        stream.Seek(goodLength, SeekOrigin.Begin);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
        return goodLength + line.Length;
    }

    private static bool TryDecode(string raw, out string json)
    {
        json = string.Empty;
        int tab = raw.LastIndexOf('\t');
        if (tab <= 0)
        {
            return false;
        }

        json = raw[..tab];
        return string.Equals(raw[(tab + 1)..], Checksum(json), StringComparison.Ordinal);
    }

    private static string Checksum(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];

    public sealed record ReadResult(IReadOnlyList<string> Lines, long GoodLength, bool TornTail);
}

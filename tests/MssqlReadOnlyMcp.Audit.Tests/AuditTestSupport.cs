using System.Text;

namespace MssqlReadOnlyMcp.Audit.Tests;

/// <summary>A per-test directory under the system temp path, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mssql-audit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A handle still open in a failing test; nothing more to do.
        }
    }
}

/// <summary>A fixed clock that moves forward one millisecond per read so lines differ.</summary>
internal sealed class FixedTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 10, 0, 0, 123, TimeSpan.Zero);
    private long _ticks;

    /// <summary>How many times the clock was read.</summary>
    public long Reads => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddMilliseconds(Interlocked.Increment(ref _ticks) - 1);
}

/// <summary>A memory stream whose writes or flushes can be made to fail.</summary>
internal sealed class FaultyStream : MemoryStream
{
    public bool FailWrite { get; set; }

    public bool FailFlush { get; set; }

    /// <summary>When failing a write, write this many bytes first (a torn write).</summary>
    public int PartialBytes { get; set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (FailWrite)
        {
            base.Write(buffer, offset, Math.Min(PartialBytes, count));
            throw new IOException("There is not enough space on the disk.");
        }

        base.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        if (FailFlush)
        {
            throw new IOException("The device is not ready.");
        }

        base.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        // Keep the bytes readable after the log closes the stream.
    }
}

internal static class Fx
{
    public const string SecretSql = "SELECT secret_marker_7f3a FROM dbo.Payroll";

    public static AuditEntry Entry(int i = 0, string? sql = "SELECT 1") => new(
        Tool: "run_query",
        Decision: "allowed",
        Reason: "Allowed",
        Sql: sql,
        Tables: ["dbo.Orders", "dbo.Customers"],
        RowsReturned: i,
        Truncated: i % 2 == 0,
        DurationMs: 10 + i,
        ResultSha256: new string('a', 63) + (i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Writes <paramref name="count"/> entries to a new log at <paramref name="path"/> and closes it.</summary>
    public static void Write(string path, int count)
    {
        using AuditLog log = AuditLog.Open(path, "session-1", new FixedTime());
        for (int i = 1; i <= count; i++)
        {
            log.Append(Entry(i, i == 3 ? SecretSql : "SELECT " + i));
        }
    }

    /// <summary>The file's lines, each without its trailing newline; a final partial line is kept.</summary>
    public static List<byte[]> Lines(string path)
    {
        byte[] bytes = ReadShared(path);
        var lines = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lines.Add(bytes[start..i]);
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            lines.Add(bytes[start..]);
        }

        return lines;
    }

    /// <summary>Reads the whole file while a writer may hold it open.</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Writes lines back, each ended by a newline.</summary>
    public static void WriteLines(string path, IEnumerable<byte[]> lines)
    {
        using var output = new MemoryStream();
        foreach (byte[] line in lines)
        {
            output.Write(line);
            output.WriteByte((byte)'\n');
        }

        System.IO.File.WriteAllBytes(path, output.ToArray());
    }

    public static string Text(byte[] line) => Encoding.UTF8.GetString(line);

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}

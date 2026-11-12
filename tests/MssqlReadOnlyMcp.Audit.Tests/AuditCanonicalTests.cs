using System.Security.Cryptography;
using System.Text;

namespace MssqlReadOnlyMcp.Audit.Tests;

// R-1 canonical form: the escaping set is fixed by the library, not by the runtime's Unicode tables.
public sealed class AuditCanonicalTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // Pins every byte of one line, built by hand here, independent of the library serialiser. Code points are given
    // as numbers so no editor or tool can silently turn them into something else.
    [Fact]
    public void Line_bytes_are_pinned()
    {
        string sql = "SELECT '" + Cp(0x0378, 0x20C1, 0xE000, 0xFFFE, 0x2028, 0x2029, 0x007F, 0x1F600, 0x0001, 0x001F)
            + "\t\"q\"\\ ıİşğ'";
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(new AuditEntry("run_query", "allowed", "Allowed", sql, ["dbo.T"], 7, true, 42, null));
        }

        string sqlJson = "\"sql\":\"SELECT '" + Cp(0x0378, 0x20C1, 0xE000, 0xFFFE) + "\\u2028\\u2029\\u007f" + Cp(0x1F600)
            + "\\u0001\\u001f\\t\\\"q\\\"\\\\ ıİşğ'\"";
        string withoutHash = "{\"seq\":1,\"ts\":\"2026-11-11T10:00:00.123Z\",\"session\":\"s\",\"tool\":\"run_query\","
            + "\"decision\":\"allowed\",\"reason\":\"Allowed\"," + sqlJson + ",\"sql_sha256\":\"" + Sha(sql) + "\","
            + "\"tables\":[\"dbo.T\"],\"rows\":7,\"truncated\":true,\"duration_ms\":42,\"result_sha256\":null,"
            + "\"prev_hash\":\"" + new string('0', 64) + "\"}";
        string expected = withoutHash[..^1] + ",\"hash\":\"" + Sha(withoutHash) + "\"}\n";

        Assert.Equal(Encoding.UTF8.GetBytes(expected), Fx.ReadShared(path));
        Assert.True(AuditVerifier.Verify(path).Ok);
    }

    // A line written with the escaping of an older or other runtime (an escape where raw UTF-8 is canonical, or an
    // upper-case hex escape) is not canonical, even though its parsed values and hash are right.
    [Theory]
    [InlineData(0x20C1, "\\u20C1")]
    [InlineData(0x0378, "\\u0378")]
    [InlineData(0x1F600, "\\uD83D\\uDE00")]
    [InlineData(0x007F, "\\u007F")]
    public void Old_escaping_is_not_canonical(int codePoint, string oldEscape)
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1, "SELECT '" + Cp(codePoint) + "'"));
        }

        string line = Fx.Text(Fx.Lines(path)[0]);
        string canonical = codePoint == 0x007F ? "\\u007f" : Cp(codePoint);
        Assert.Contains(canonical, line, StringComparison.Ordinal);
        Fx.WriteLines(path, [Fx.Bytes(line.Replace(canonical, oldEscape, StringComparison.Ordinal))]);

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.False(result.Ok);
        Assert.Equal(1, result.FirstBadLine);
        Assert.Contains("NotCanonical", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_line_separator_is_not_canonical()
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1, "SELECT '" + Cp(0x2028) + "'"));
        }

        string line = Fx.Text(Fx.Lines(path)[0]);
        Fx.WriteLines(path, [Fx.Bytes(line.Replace("\\u2028", Cp(0x2028), StringComparison.Ordinal))]);
        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.False(result.Ok);
        Assert.Contains("NotCanonical", result.Message, StringComparison.Ordinal);
    }

    private static string Cp(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

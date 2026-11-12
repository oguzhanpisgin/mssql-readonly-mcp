using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.Audit.Tests;

public sealed class AuditLogTests : IDisposable
{
    private static readonly string[] ExpectedFields =
    [
        "seq", "ts", "session", "tool", "decision", "reason", "sql", "sql_sha256", "tables", "rows", "truncated",
        "duration_ms", "result_sha256", "prev_hash", "hash",
    ];

    private readonly TempDir _dir = new();
    private readonly ITestOutputHelper _output;

    public AuditLogTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _dir.Dispose();

    // R-1
    [Fact]
    public void Lines_have_fixed_field_order_and_values()
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "session-1", new FixedTime()))
        {
            log.Append(Fx.Entry(1, "SELECT 1"));
            log.Append(new AuditEntry("list_tables", "refused", "NotAllowed", null, [], null, false, 0, null));
        }

        byte[] bytes = Fx.ReadShared(path);
        Assert.Equal((byte)'\n', bytes[^1]);
        List<byte[]> lines = Fx.Lines(path);
        Assert.Equal(2, lines.Count);

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(ExpectedFields, first.RootElement.EnumerateObject().Select(p => p.Name));
        JsonElement a = first.RootElement;
        Assert.Equal(1, a.GetProperty("seq").GetInt64());
        Assert.Equal("2026-11-11T10:00:00.123Z", a.GetProperty("ts").GetString());
        Assert.Equal("session-1", a.GetProperty("session").GetString());
        Assert.Equal("run_query", a.GetProperty("tool").GetString());
        Assert.Equal("allowed", a.GetProperty("decision").GetString());
        Assert.Equal("Allowed", a.GetProperty("reason").GetString());
        Assert.Equal("SELECT 1", a.GetProperty("sql").GetString());
        Assert.Equal(Sha256Hex(Encoding.UTF8.GetBytes("SELECT 1")), a.GetProperty("sql_sha256").GetString());
        Assert.Equal(["dbo.Orders", "dbo.Customers"], a.GetProperty("tables").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(1, a.GetProperty("rows").GetInt32());
        Assert.False(a.GetProperty("truncated").GetBoolean());
        Assert.Equal(11, a.GetProperty("duration_ms").GetInt64());
        Assert.Equal(new string('0', 64), a.GetProperty("prev_hash").GetString());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        JsonElement b = second.RootElement;
        Assert.Equal(ExpectedFields, b.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, b.GetProperty("seq").GetInt64());
        Assert.Equal("2026-11-11T10:00:00.124Z", b.GetProperty("ts").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("sql").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("sql_sha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("rows").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("result_sha256").ValueKind);
        Assert.Equal(a.GetProperty("hash").GetString(), b.GetProperty("prev_hash").GetString());
    }

    // R-1: hash covers the object without its hash field, computed independently of the library serialiser.
    [Fact]
    public void Hash_is_sha256_of_the_line_without_its_hash_field()
    {
        string path = _dir.File("audit.jsonl");
        Fx.Write(path, 3);
        AuditLog? reopened = null;
        try
        {
            reopened = AuditLog.Open(path, "s");
            string? lastHash = null;
            foreach (byte[] raw in Fx.Lines(path))
            {
                string line = Fx.Text(raw);
                _output.WriteLine(line);
                int cut = line.LastIndexOf(",\"hash\":\"", StringComparison.Ordinal);
                string hash = line[(cut + 9)..^2];
                string withoutHash = line[..cut] + "}";
                Assert.Equal(Sha256Hex(Encoding.UTF8.GetBytes(withoutHash)), hash);
                Assert.Equal(line, withoutHash[..^1] + ",\"hash\":\"" + hash + "\"}");
                lastHash = hash;
            }

            Assert.Equal(lastHash, reopened.LastHash);
            Assert.Equal(3, reopened.LastSequence);
        }
        finally
        {
            reopened?.Dispose();
        }
    }

    // R-1: Turkish letters stay raw UTF-8; quotes, newlines and tabs are escaped; everything round-trips and verifies.
    [Fact]
    public void Special_characters_in_sql_round_trip_and_verify()
    {
        string sql = "SELECT N'ıİşğ ÇÖÜ' AS \"quoted\", 'a\\b' FROM dbo.Müşteri\r\nWHERE x = '\t'";
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "oturum-ş", new FixedTime()))
        {
            log.Append(Fx.Entry(1, sql) with { Tables = ["dbo.Müşteri"] });
            log.Append(Fx.Entry(2, "SELECT 2"));
        }

        List<byte[]> lines = Fx.Lines(path);
        Assert.Equal(2, lines.Count);
        string text = Fx.Text(lines[0]);
        Assert.Contains("ıİşğ ÇÖÜ", text, StringComparison.Ordinal);
        Assert.Contains("dbo.Müşteri", text, StringComparison.Ordinal);
        Assert.Contains("\\\"quoted\\\"", text, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", text, StringComparison.Ordinal);
        Assert.Contains("\\t", text, StringComparison.Ordinal);
        Assert.Contains("'a\\\\b'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', text);
        Assert.DoesNotContain('\r', text);

        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(sql, doc.RootElement.GetProperty("sql").GetString());
        Assert.Equal(Sha256Hex(Encoding.UTF8.GetBytes(sql)), doc.RootElement.GetProperty("sql_sha256").GetString());
        Assert.Equal("oturum-ş", doc.RootElement.GetProperty("session").GetString());

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LinesChecked);
    }

    // R-1: supplementary characters stay raw; DEL, NUL and the line separator use lower-case \u escapes; all round-trip and verify.
    [Theory]
    [InlineData("SELECT '\U0001F600'", "SELECT '\U0001F600'")]
    [InlineData("SELECT '\u007F\u0000\x2028'", "\\u007f\\u0000\\u2028")]
    public void Escaped_characters_still_verify(string sql, string expected)
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1, sql));
        }

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.True(result.Ok, result.Message);
        using JsonDocument doc = JsonDocument.Parse(Fx.Lines(path)[0]);
        Assert.Equal(sql, doc.RootElement.GetProperty("sql").GetString());
        Assert.Contains(expected, Fx.Text(Fx.Lines(path)[0]), StringComparison.Ordinal);
    }

    // R-1: a lone surrogate (built at run time; attribute strings cannot carry one) is written and verifies.
    [Fact]
    public void Lone_surrogate_in_sql_verifies()
    {
        string sql = "SELECT '" + '\uD800' + "'";
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1, sql));
            log.Append(Fx.Entry(2));
        }

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LinesChecked);

        // Stored as U+FFFD; sql_sha256 still equals the digest of the UTF-8 bytes of the text as received.
        using JsonDocument doc = JsonDocument.Parse(Fx.Lines(path)[0]);
        Assert.Equal("SELECT '�'", doc.RootElement.GetProperty("sql").GetString());
        Assert.Equal(Sha256Hex(Encoding.UTF8.GetBytes(sql)), doc.RootElement.GetProperty("sql_sha256").GetString());
    }

    // R-2: a failed write throws, leaves the state unchanged and poisons the instance.
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 17)]
    [InlineData(false, true, 0)]
    public void Failed_append_throws_and_poisons(bool failWrite, bool failFlush, int partialBytes)
    {
        var stream = new FaultyStream();
        using AuditLog log = AuditLog.OpenOnStream(stream, null, "audit.jsonl", "s", new FixedTime());
        log.Append(Fx.Entry(1));
        long seq = log.LastSequence;
        string hash = log.LastHash;

        stream.FailWrite = failWrite;
        stream.FailFlush = failFlush;
        stream.PartialBytes = partialBytes;
        var ex = Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(2, Fx.SecretSql)));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_marker", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        Assert.Equal(seq, log.LastSequence);
        Assert.Equal(hash, log.LastHash);

        // The stream is healthy again, but the instance stays poisoned.
        stream.FailWrite = false;
        stream.FailFlush = false;
        long length = stream.Length;
        Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(3)));
        Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(4)));
        Assert.Equal(length, stream.Length);
        Assert.Equal(seq, log.LastSequence);
        Assert.Equal(hash, log.LastHash);
    }

    // R-2/R-3: a torn line left by a failed write makes reopening fail instead of continuing silently.
    [Fact]
    public void Torn_write_makes_reopen_fail()
    {
        var stream = new FaultyStream();
        using (AuditLog log = AuditLog.OpenOnStream(stream, null, "audit.jsonl", "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1));
            stream.FailWrite = true;
            stream.PartialBytes = 40;
            Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(2)));
        }

        stream.FailWrite = false;
        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOnStream(stream, null, "audit.jsonl", "s", null));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PartialLine", ex.Message, StringComparison.Ordinal);
    }

    // R-2: the line is in the file (readable by another handle) when Append returns.
    [Fact]
    public void Line_is_in_the_file_when_append_returns()
    {
        string path = _dir.File("audit.jsonl");
        using AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        log.Append(Fx.Entry(1));
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Single(Fx.Lines(path));
            Assert.True(reader.Length > 0);
        }

        log.Append(Fx.Entry(2));
        Assert.Equal(2, Fx.Lines(path).Count);
    }

    [Fact]
    public void Append_after_dispose_throws()
    {
        string path = _dir.File("audit.jsonl");
        AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        log.Dispose();
        log.Dispose();
        Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(1)));
    }

    [Fact]
    public void Invalid_entries_are_rejected_without_writing()
    {
        string path = _dir.File("audit.jsonl");
        using AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        Assert.Throws<ArgumentException>(() => log.Append(Fx.Entry(1) with { Decision = "maybe" }));
        Assert.Throws<ArgumentException>(() => log.Append(Fx.Entry(1) with { ResultSha256 = "abc" }));
        Assert.Throws<ArgumentException>(() => log.Append(Fx.Entry(1) with { Tables = null! }));
        Assert.Equal(0, log.LastSequence);
        Assert.Empty(Fx.ReadShared(path));

        log.Append(Fx.Entry(1) with { ResultSha256 = new string('A', 64) });
        using JsonDocument doc = JsonDocument.Parse(Fx.Lines(path)[0]);
        Assert.Equal(new string('a', 64), doc.RootElement.GetProperty("result_sha256").GetString());
    }

    // R-3: reopening continues seq and prev_hash.
    [Fact]
    public void Reopen_continues_the_chain()
    {
        string path = _dir.File("audit.jsonl");
        Fx.Write(path, 3);
        string hashOfThree;
        using (AuditLog log = AuditLog.Open(path, "session-2", new FixedTime()))
        {
            Assert.Equal(3, log.LastSequence);
            hashOfThree = log.LastHash;
            log.Append(Fx.Entry(4));
            log.Append(Fx.Entry(5));
            Assert.Equal(5, log.LastSequence);
        }

        using JsonDocument fourth = JsonDocument.Parse(Fx.Lines(path)[3]);
        Assert.Equal(4, fourth.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(hashOfThree, fourth.RootElement.GetProperty("prev_hash").GetString());
        Assert.Equal("session-2", fourth.RootElement.GetProperty("session").GetString());

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(5, result.LinesChecked);
    }

    // R-3: a file that does not verify is refused and left untouched.
    [Theory]
    [InlineData("tamper")]
    [InlineData("partial")]
    public void Reopen_refuses_a_bad_file_and_writes_nothing(string damage)
    {
        string path = _dir.File("audit.jsonl");
        Fx.Write(path, 4);
        List<byte[]> lines = Fx.Lines(path);
        if (damage == "tamper")
        {
            lines[2] = Fx.Bytes(Fx.Text(lines[2]).Replace("\"rows\":3", "\"rows\":0", StringComparison.Ordinal));
            Fx.WriteLines(path, lines);
        }
        else
        {
            File.AppendAllText(path, "{\"seq\":5,\"ts\":\"2026-");
        }

        byte[] before = File.ReadAllBytes(path);
        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.Open(path, "s", new FixedTime()));
        Assert.Contains(damage == "tamper" ? "line 3" : "line 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("audit.jsonl", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir.Path, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_marker", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));

        // The failed open released the file and the writer lock.
        File.Delete(path);
        using AuditLog fresh = AuditLog.Open(path, "s", new FixedTime());
        Assert.Equal(0, fresh.LastSequence);
    }

    // R-3: missing file and missing parent directories are created.
    [Fact]
    public void Open_creates_missing_directories_and_file()
    {
        string path = Path.Combine(_dir.Path, "a", "b", "audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            Assert.Equal(0, log.LastSequence);
            Assert.Equal(new string('0', 64), log.LastHash);
            log.Append(Fx.Entry(1));
        }

        Assert.True(AuditVerifier.Verify(path).Ok);
    }

    // R-4: a second writer is refused while the first is open; readers and the verifier still read.
    [Fact]
    public void Second_writer_is_refused_while_readers_can_read()
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1));

            var ex = Assert.Throws<AuditWriteException>(() => AuditLog.Open(path, "other", new FixedTime()));
            Assert.Contains("audit.jsonl", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_dir.Path, ex.Message, StringComparison.Ordinal);

            // A path spelled differently still names the same file.
            string otherSpelling = Path.Combine(_dir.Path, ".", "audit.jsonl");
            Assert.Throws<AuditWriteException>(() => AuditLog.Open(otherSpelling, "other", new FixedTime()));

            AuditVerifyResult result = AuditVerifier.Verify(path);
            Assert.True(result.Ok, result.Message);
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Assert.True(reader.Length > 0);
            }

            log.Append(Fx.Entry(2));
            Assert.Equal(2, log.LastSequence);
        }

        using AuditLog again = AuditLog.Open(path, "s", new FixedTime());
        Assert.Equal(2, again.LastSequence);
    }

    // R-6: concurrent appends give a verifying file with strictly increasing seq and whole lines.
    [Fact]
    public void Concurrent_appends_verify()
    {
        const int threads = 8;
        const int perThread = 200;
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < perThread; i++)
                {
                    log.Append(Fx.Entry(i, $"SELECT {t}, {i} /* ıİşğ */"));
                }
            })).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());
            Assert.Equal(threads * perThread, log.LastSequence);
        }

        AuditVerifyResult result = AuditVerifier.Verify(path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(threads * perThread, result.LinesChecked);

        long previous = 0;
        var perThreadCounts = new int[threads];
        foreach (byte[] raw in Fx.Lines(path))
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            long seq = doc.RootElement.GetProperty("seq").GetInt64();
            Assert.Equal(previous + 1, seq);
            previous = seq;
            string sql = doc.RootElement.GetProperty("sql").GetString()!;
            perThreadCounts[int.Parse(sql[7..sql.IndexOf(',', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture)]++;
        }

        Assert.All(perThreadCounts, c => Assert.Equal(perThread, c));
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

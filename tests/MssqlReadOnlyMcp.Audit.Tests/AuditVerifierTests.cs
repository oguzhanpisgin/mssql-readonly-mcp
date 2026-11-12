namespace MssqlReadOnlyMcp.Audit.Tests;

// R-5 and R-7.
public sealed class AuditVerifierTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;

    public AuditVerifierTests()
    {
        _path = _dir.File("audit.jsonl");
        Fx.Write(_path, 5);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Untouched_file_verifies()
    {
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(5, result.LinesChecked);
        Assert.Null(result.FirstBadLine);
    }

    [Fact]
    public void Empty_file_verifies_with_zero_lines()
    {
        File.WriteAllBytes(_path, []);
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, result.LinesChecked);
    }

    [Fact]
    public void Changed_field_value_is_found()
    {
        Rewrite(lines => lines[2] = Fx.Bytes(Fx.Text(lines[2]).Replace("\"rows\":3", "\"rows\":0", StringComparison.Ordinal)));
        AssertBad(3, "HashMismatch");
    }

    [Fact]
    public void Changed_sql_is_found()
    {
        Rewrite(lines => lines[2] = Fx.Bytes(Fx.Text(lines[2]).Replace("dbo.Payroll", "dbo.Payrolx", StringComparison.Ordinal)));
        AssertBad(3, "SqlHashMismatch");
    }

    [Fact]
    public void Deleted_middle_line_is_found()
    {
        Rewrite(lines => lines.RemoveAt(2));
        AssertBad(3, "SequenceGap");
    }

    [Fact]
    public void Swapped_lines_are_found()
    {
        Rewrite(lines => (lines[1], lines[2]) = (lines[2], lines[1]));
        AssertBad(2, "SequenceGap");
    }

    [Fact]
    public void Inserted_copied_line_is_found()
    {
        Rewrite(lines => lines.Insert(3, lines[0]));
        AssertBad(4, "SequenceGap");
    }

    // A forged line that is valid on its own (right seq, right prev_hash, right hash) can only be detected at the
    // next original line, whose seq and prev_hash no longer follow.
    [Fact]
    public void Inserted_forged_line_is_found_at_the_next_line()
    {
        Rewrite(lines =>
        {
            string prev = Fx.Text(lines[1]);
            string prevHash = prev[(prev.LastIndexOf(":\"", StringComparison.Ordinal) + 2)..^2];
            var forged = new AuditRecord(
                3, "2026-11-11T10:00:00.500Z", "session-1", "run_query", "allowed", "Allowed", null, null, [], 0, false, 1, null, prevHash);
            lines.Insert(2, AuditFormat.Serialize(forged, AuditFormat.ComputeHash(forged)));
        });
        AssertBad(4, "SequenceGap");
    }

    [Fact]
    public void Duplicated_line_is_found()
    {
        Rewrite(lines => lines.Insert(2, lines[1]));
        AssertBad(3, "SequenceGap");
    }

    [Theory]
    [InlineData("not json at all secret_marker_7f3a")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{\"seq\":3}")]
    public void Malformed_line_is_found(string replacement)
    {
        Rewrite(lines => lines[2] = Fx.Bytes(replacement));
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.False(result.Ok);
        Assert.Equal(3, result.FirstBadLine);
        Assert.Equal(2, result.LinesChecked);
        AssertSafe(result.Message);
    }

    [Fact]
    public void Whitespace_change_is_found_as_not_canonical()
    {
        Rewrite(lines => lines[1] = Fx.Bytes(Fx.Text(lines[1]).Replace("\"seq\":2,", "\"seq\": 2,", StringComparison.Ordinal)));
        AssertBad(2, "NotCanonical");
    }

    [Fact]
    public void Partial_last_line_is_found()
    {
        byte[] bytes = File.ReadAllBytes(_path);
        File.WriteAllBytes(_path, bytes[..^1]);
        AssertBad(5, "PartialLine");
    }

    [Fact]
    public void Partial_extra_line_is_found()
    {
        File.AppendAllText(_path, "{\"seq\":6");
        AssertBad(6, "PartialLine");
    }

    [Fact]
    public void Wrong_first_prev_hash_is_found()
    {
        Rewrite(lines =>
        {
            var forged = new AuditRecord(
                1, "2026-11-11T10:00:00.123Z", "session-1", "run_query", "allowed", "Allowed", null, null, [], 0, false, 1, null, new string('1', 64));
            lines.Clear();
            lines.Add(AuditFormat.Serialize(forged, AuditFormat.ComputeHash(forged)));
        });
        AssertBad(1, "ChainBreak");
    }

    [Fact]
    public void Rehashed_edit_breaks_the_chain_at_the_next_line()
    {
        // Edit line 2 and recompute its own hash: line 2 now verifies alone, line 3's prev_hash no longer matches.
        Rewrite(lines =>
        {
            string text = Fx.Text(lines[1]);
            string prevHash = Fx.Text(lines[0]);
            prevHash = prevHash[(prevHash.LastIndexOf(":\"", StringComparison.Ordinal) + 2)..^2];
            var edited = new AuditRecord(
                2, text.Substring(text.IndexOf("\"ts\":\"", StringComparison.Ordinal) + 6, 24), "session-1", "run_query", "allowed",
                "Allowed", null, null, [], 999, false, 1, null, prevHash);
            lines[1] = AuditFormat.Serialize(edited, AuditFormat.ComputeHash(edited));
        });
        AssertBad(3, "ChainBreak");
    }

    // Honest limit (see the R-8 remarks on AuditLog): removing the newest lines leaves a shorter chain that still
    // verifies. Only a copy of LastHash kept where the file's writer cannot write would show it. This test pins the
    // limit so that it is visible, not an accident.
    [Fact]
    public void Tail_truncation_is_not_detected_by_design()
    {
        Rewrite(lines => lines.RemoveRange(3, 2));
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.LinesChecked);
    }

    [Fact]
    public void Missing_file_is_reported_not_thrown()
    {
        AuditVerifyResult result = AuditVerifier.Verify(_dir.File("missing.jsonl"));
        Assert.False(result.Ok);
        Assert.Null(result.FirstBadLine);
        Assert.Contains("missing.jsonl", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir.Path, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_path_is_reported_not_thrown()
    {
        AuditVerifyResult result = AuditVerifier.Verify(_dir.Path);
        Assert.False(result.Ok);
        Assert.Null(result.FirstBadLine);
    }

    [Fact]
    public void Messages_never_carry_sql_or_directory()
    {
        // Line 3 holds the secret SQL; damage it in a way that reaches every reason path that reads it.
        Rewrite(lines => lines[2] = Fx.Bytes(Fx.Text(lines[2]).Replace("\"rows\":3", "\"rows\":\"secret_marker_7f3a\"", StringComparison.Ordinal)));
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.False(result.Ok);
        AssertSafe(result.Message);
        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.Open(_path, "s", new FixedTime()));
        AssertSafe(ex.Message);
    }

    private void Rewrite(Action<List<byte[]>> change)
    {
        List<byte[]> lines = Fx.Lines(_path);
        change(lines);
        Fx.WriteLines(_path, lines);
    }

    private void AssertBad(long line, string reason)
    {
        AuditVerifyResult result = AuditVerifier.Verify(_path);
        Assert.False(result.Ok, result.Message);
        Assert.Equal(line, result.FirstBadLine);
        Assert.Equal(line - 1, result.LinesChecked);
        Assert.Contains($"line {line}", result.Message, StringComparison.Ordinal);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        AssertSafe(result.Message);
    }

    private void AssertSafe(string message)
    {
        Assert.Contains("audit.jsonl", message, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir.Path, message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_marker", message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Payroll", message, StringComparison.Ordinal);
    }
}

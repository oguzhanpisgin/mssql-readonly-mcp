using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MssqlReadOnlyMcp.Audit.Tests;

// R-2 disk flush, R-4 lock file and cross-process single writer, and the maximum line length.
public sealed class AuditLockFlushAndLimitTests : IDisposable
{
    private const string PwshMissing = "This test needs PowerShell 7 (pwsh) on PATH; it could not be started.";

    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(60);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // R-4: whoever holds the .lock file exclusively keeps AuditLog.Open out.
    [Fact]
    public void Lock_file_held_exclusively_blocks_open()
    {
        string path = _dir.File("audit.jsonl");
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<AuditWriteException>(() => AuditLog.Open(path, "s", new FixedTime()));
            Assert.Contains("audit.jsonl", ex.Message, StringComparison.Ordinal);
        }

        using AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        log.Append(Fx.Entry(1));
    }

    // R-2: the durable flush is FileStream.Flush(flushToDisk: true) on the real log file, after the line is written.
    // Fails if the call becomes Flush() (which is Flush(false)) or is removed.
    [Fact]
    public void Append_flushes_the_real_file_to_disk()
    {
        string path = _dir.File("audit.jsonl");
        RecordingFileStream? data = null;
        using (AuditLog log = AuditLog.Open(
                   path,
                   "s",
                   new FixedTime(),
                   full => data = new RecordingFileStream(full),
                   AuditVerifier.MaxLineBytes))
        {
            Assert.NotNull(data);
            Assert.Equal(Path.GetFullPath(path), data.Name);
            data.Flushes.Clear();

            log.Append(Fx.Entry(1));
            long afterFirst = new FileInfo(path).Length;
            Assert.Equal([(true, afterFirst)], data.Flushes);

            log.Append(Fx.Entry(2));
            long afterSecond = new FileInfo(path).Length;
            Assert.Equal([(true, afterFirst), (true, afterSecond)], data.Flushes);
        }

        Assert.True(AuditVerifier.Verify(path).Ok);
    }

    // R-4 across processes, holder in a child process: a PowerShell child holds the .lock file the way a second
    // AuditLog would (FileShare.None); this process's AuditLog.Open is refused until the child lets go.
    [Fact]
    public async Task Lock_held_by_another_process_blocks_open()
    {
        string path = _dir.File("audit.jsonl");
        const string script = """
            $f = [System.IO.File]::Open($env:AUDIT_LOCK_PATH, 'OpenOrCreate', 'ReadWrite', 'None')
            [Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush()
            [void][Console]::In.ReadLine()
            $f.Dispose()
            """;
        using (Process child = StartPwsh(script, path))
        {
            try
            {
                string? first = await child.StandardOutput.ReadLineAsync().WaitAsync(ChildTimeout);
                Assert.Equal("LOCKED", first);

                Assert.Throws<AuditWriteException>(() => AuditLog.Open(path, "s", new FixedTime()));

                await child.StandardInput.WriteLineAsync();
                await child.StandardInput.FlushAsync();
                await child.WaitForExitAsync().WaitAsync(ChildTimeout);
                Assert.Equal(0, child.ExitCode);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }
            }
        }

        using AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        log.Append(Fx.Entry(1));
    }

    // R-4 across processes, writer in this process: a child process can neither take the writer lock nor open the
    // log for writing, but can read the log with read-write sharing.
    [Fact]
    public async Task Open_log_refuses_another_process_but_lets_it_read()
    {
        string path = _dir.File("audit.jsonl");
        const string script = """
            $code = 0
            try { $l = [System.IO.File]::Open($env:AUDIT_LOCK_PATH, 'OpenOrCreate', 'ReadWrite', 'None'); $l.Dispose(); $code += 1 } catch { }
            try { $w = [System.IO.File]::Open($env:AUDIT_LOG_PATH, 'Open', 'ReadWrite', 'ReadWrite'); $w.Dispose(); if ($IsWindows) { $code += 2 } } catch { }
            try { $r = [System.IO.File]::Open($env:AUDIT_LOG_PATH, 'Open', 'Read', 'ReadWrite'); $n = $r.Length; $r.Dispose(); if ($n -le 0) { $code += 4 } } catch { $code += 8 }
            exit $code
            """;
        using (AuditLog log = AuditLog.Open(path, "s", new FixedTime()))
        {
            log.Append(Fx.Entry(1));
            using Process child = StartPwsh(script, path);
            try
            {
                await child.WaitForExitAsync().WaitAsync(ChildTimeout);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }
            }

            // 1: took the writer lock; 2: opened the log for writing (Windows); 4: read nothing; 8: could not read.
            Assert.Equal(0, child.ExitCode);
        }
    }

    // Maximum line length: Verify reports LineTooLong instead of buffering a huge line.
    [Fact]
    public void Verify_reports_a_line_over_the_limit()
    {
        string path = _dir.File("audit.jsonl");
        WriteWithLongSecondLine(path);

        Assert.True(AuditVerifier.Verify(path).Ok);
        AuditVerifyResult result = AuditVerifier.Verify(path, 1000);
        Assert.False(result.Ok);
        Assert.Equal(2, result.FirstBadLine);
        Assert.Equal(1, result.LinesChecked);
        Assert.Contains("LineTooLong", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", result.Message, StringComparison.Ordinal);

        // A file that is one long run of bytes with no newline at all.
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 5000)));
        result = AuditVerifier.Verify(path, 1000);
        Assert.False(result.Ok);
        Assert.Equal(1, result.FirstBadLine);
        Assert.Contains("LineTooLong", result.Message, StringComparison.Ordinal);
    }

    // Maximum line length: Open refuses such a file and releases both handles.
    [Fact]
    public void Open_refuses_a_line_over_the_limit_and_releases_both_handles()
    {
        string path = _dir.File("audit.jsonl");
        WriteWithLongSecondLine(path);
        byte[] before = File.ReadAllBytes(path);

        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.Open(path, "s", new FixedTime(), null, 1000));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LineTooLong", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));

        using (new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Both opened exclusively: the failed Open let go of both.
        }
    }

    // Maximum line length, exact byte check after serialising: the entry's strings fit the limit but the whole line
    // does not. Append writes nothing and is not poisoned.
    [Fact]
    public void Append_refuses_a_line_over_the_limit_without_poisoning()
    {
        string path = _dir.File("audit.jsonl");
        var time = new FixedTime();
        using AuditLog log = AuditLog.Open(path, "s", time, null, 1000);
        log.Append(Fx.Entry(1));
        long length = new FileInfo(path).Length;
        string hash = log.LastHash;

        long clockReads = time.Reads;
        var ex = Assert.Throws<AuditWriteException>(() => log.Append(Fx.Entry(2, "SELECT '" + new string('y', 800) + "'")));
        Assert.Contains("LineTooLong", ex.Message, StringComparison.Ordinal);
        Assert.Equal(clockReads + 1, time.Reads); // the record was built, so this was the late check
        Assert.Equal(1, log.LastSequence);
        Assert.Equal(hash, log.LastHash);
        Assert.Equal(length, new FileInfo(path).Length);

        log.Append(Fx.Entry(2));
        Assert.Equal(2, log.LastSequence);
    }

    // Maximum line length, early refusal: the entry's strings alone exceed the limit, so Append refuses before
    // building, hashing or serialising the line (the clock is not even read), writes nothing, is not poisoned, and
    // the next Append continues the chain.
    [Fact]
    public void Append_refuses_an_oversized_entry_before_serialising()
    {
        string path = _dir.File("audit.jsonl");
        var time = new FixedTime();
        using (AuditLog log = AuditLog.Open(path, "s", time, null, 1000))
        {
            log.Append(Fx.Entry(1));
            long length = new FileInfo(path).Length;
            string hash = log.LastHash;
            long clockReads = time.Reads;

            var ex = Assert.Throws<AuditWriteException>(
                () => log.Append(Fx.Entry(2, null) with { Tables = ["dbo.T", new string('t', 1000)] }));
            Assert.Contains("LineTooLong", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ttttt", ex.Message, StringComparison.Ordinal);
            Assert.Equal(clockReads, time.Reads);
            Assert.Equal(1, log.LastSequence);
            Assert.Equal(hash, log.LastHash);
            Assert.Equal(length, new FileInfo(path).Length);

            log.Append(Fx.Entry(2));
            Assert.Equal(2, log.LastSequence);
        }

        List<byte[]> lines = Fx.Lines(path);
        Assert.Equal(2, lines.Count);
        AuditVerifyResult result = AuditVerifier.Verify(path, 1000);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LinesChecked);
    }

    private static void WriteWithLongSecondLine(string path)
    {
        using AuditLog log = AuditLog.Open(path, "s", new FixedTime());
        log.Append(Fx.Entry(1));
        log.Append(Fx.Entry(2, "SELECT '" + new string('y', 2000) + "'"));
        log.Append(Fx.Entry(3));
    }

    private static Process StartPwsh(string script, string logPath)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.Environment["AUDIT_LOG_PATH"] = Path.GetFullPath(logPath);
        psi.Environment["AUDIT_LOCK_PATH"] = Path.GetFullPath(logPath) + ".lock";
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException(PwshMissing);
        }
        catch (Win32Exception ex)
        {
            // Fail, never skip: the cross-process rule stays unverified without PowerShell 7.
            throw new InvalidOperationException(PwshMissing, ex);
        }
    }

    /// <summary>The real log FileStream, recording each Flush(bool) call and the file length at that moment.</summary>
    private sealed class RecordingFileStream(string path)
        : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0)
    {
        public List<(bool ToDisk, long Length)> Flushes { get; } = [];

        public override void Flush(bool flushToDisk)
        {
            Flushes.Add((flushToDisk, Length));
            base.Flush(flushToDisk);
        }
    }
}

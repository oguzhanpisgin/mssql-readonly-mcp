using System.Text.Json;

namespace MssqlReadOnlyMcp.Audit.Tests;

// I-1: a second writer on the same configured path gets a sibling file of its own instead of failing; every failure
// other than a held lock stays fatal. A lock held by a FileStream with FileShare.None in this process conflicts with
// AuditLog's own open on Windows (sharing violation) and on Linux (flock on a second open file description), so it
// stands in for another process here; the cross-process race with the real server is in the Gate tests
// (AuditSiblingProcessTests).
public sealed class AuditSiblingTests : IDisposable
{
    private static readonly string Zeros = new('0', 64);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Sibling_paths_insert_the_number_before_the_extension()
    {
        string folder = Path.Combine("x", "y");
        Assert.Equal("audit.jsonl", AuditLog.SiblingPath("audit.jsonl", 1));
        Assert.Equal("audit.2.jsonl", AuditLog.SiblingPath("audit.jsonl", 2));
        Assert.Equal(Path.Combine(folder, "audit.8.jsonl"), AuditLog.SiblingPath(Path.Combine(folder, "audit.jsonl"), 8));
        Assert.Equal(Path.Combine(folder, "claude.desktop.3.jsonl"), AuditLog.SiblingPath(Path.Combine(folder, "claude.desktop.jsonl"), 3));
        Assert.Equal("audit.2", AuditLog.SiblingPath("audit", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditLog.SiblingPath("audit.jsonl", 0));
        Assert.Equal(8, AuditLog.MaxFilesPerPath);
    }

    [Fact]
    public void Free_configured_file_is_opened_and_no_sibling_is_created()
    {
        string path = _dir.File("audit.jsonl");
        using (AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime()))
        {
            Assert.Equal(Path.GetFullPath(path), log.FilePath);
            log.Append(Fx.Entry(1));
        }

        Assert.Equal(["audit.jsonl", "audit.jsonl.lock"], Names());
        Assert.True(AuditVerifier.Verify(path).Ok);
    }

    // Property 1 and 2: the configured file held, the first sibling is written as a chain of its own, and nothing is
    // written to the configured file, whose lock this process does not hold.
    [Fact]
    public void Held_configured_file_gives_the_first_sibling_with_a_chain_of_its_own()
    {
        string path = _dir.File("audit.jsonl");
        string sibling = _dir.File("audit.2.jsonl");
        using (HoldLock(path))
        {
            using AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime());
            Assert.Equal(Path.GetFullPath(sibling), log.FilePath);
            log.Append(Fx.Entry(1));
            log.Append(Fx.Entry(2));
        }

        Assert.False(File.Exists(path));
        AuditVerifyResult result = AuditVerifier.Verify(sibling);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LinesChecked);
        Assert.Equal("Audit file audit.2.jsonl: 2 lines verified.", result.Message);
        List<byte[]> lines = Fx.Lines(sibling);
        Assert.Equal(Zeros, Field(lines[0], "prev_hash"));
        Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("seq").GetInt64());
    }

    [Fact]
    public void Lowest_free_number_is_taken_first()
    {
        string path = _dir.File("audit.jsonl");
        using (HoldLock(path))
        using (HoldLock(_dir.File("audit.3.jsonl")))
        using (AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime()))
        {
            Assert.Equal(Path.GetFullPath(_dir.File("audit.2.jsonl")), log.FilePath);
        }

        using (HoldLock(path))
        using (HoldLock(_dir.File("audit.2.jsonl")))
        using (AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime()))
        {
            Assert.Equal(Path.GetFullPath(_dir.File("audit.3.jsonl")), log.FilePath);
        }

        // The configured file free again: it is taken, not a sibling.
        using (AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime()))
        {
            Assert.Equal(Path.GetFullPath(path), log.FilePath);
        }
    }

    [Fact]
    public void Sibling_reopen_verifies_and_continues_its_chain()
    {
        string path = _dir.File("audit.jsonl");
        string sibling = _dir.File("audit.2.jsonl");
        using (HoldLock(path))
        {
            using (AuditLog log = AuditLog.OpenOrSibling(path, "first", new FixedTime()))
            {
                log.Append(Fx.Entry(1));
                log.Append(Fx.Entry(2));
            }

            using (AuditLog again = AuditLog.OpenOrSibling(path, "second", new FixedTime()))
            {
                Assert.Equal(Path.GetFullPath(sibling), again.FilePath);
                Assert.Equal(2, again.LastSequence);
                again.Append(Fx.Entry(3));
            }
        }

        AuditVerifyResult result = AuditVerifier.Verify(sibling);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.LinesChecked);
    }

    // Property 1: the fixed upper bound. Beyond it the open fails with a message naming the files, and no ninth file is made.
    [Fact]
    public void All_files_held_fails_with_a_clear_message()
    {
        string path = _dir.File("audit.jsonl");
        var held = new List<FileStream>();
        try
        {
            for (int number = 1; number <= AuditLog.MaxFilesPerPath; number++)
            {
                held.Add(HoldLock(AuditLog.SiblingPath(path, number)));
            }

            var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime()));
            Assert.Equal(
                "Audit file audit.jsonl and its siblings audit.2.jsonl to audit.8.jsonl are all locked by other writers; " +
                "at most 8 server processes can write the audit files of one configured path.",
                ex.Message);
            Assert.False(ex.LockHeld);
        }
        finally
        {
            held.ForEach(h => h.Dispose());
        }

        Assert.False(File.Exists(_dir.File("audit.9.jsonl")));
        Assert.False(File.Exists(_dir.File("audit.9.jsonl.lock")));
        Assert.DoesNotContain(Names(), n => n.EndsWith(".jsonl", StringComparison.Ordinal));
    }

    // Property 3: a sibling that does not verify is fatal and is never skipped to reach the next number.
    [Fact]
    public void Sibling_that_does_not_verify_is_fatal_and_not_skipped()
    {
        string path = _dir.File("audit.jsonl");
        string sibling = _dir.File("audit.2.jsonl");
        Fx.Write(sibling, 3);
        List<byte[]> lines = Fx.Lines(sibling);
        lines.RemoveAt(1);
        Fx.WriteLines(sibling, lines);
        byte[] before = File.ReadAllBytes(sibling);

        using (HoldLock(path))
        {
            var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime()));
            Assert.StartsWith("Audit file audit.2.jsonl does not verify at line 2", ex.Message, StringComparison.Ordinal);
            Assert.False(ex.LockHeld);
        }

        Assert.Equal(before, File.ReadAllBytes(sibling));
        Assert.False(File.Exists(_dir.File("audit.3.jsonl")));
        Assert.False(File.Exists(_dir.File("audit.3.jsonl.lock")));
    }

    // DOCUMENTED LIMIT, NOT A GUARANTEE (review round 1, B-3 and N-7a): a configured file that does not verify but whose
    // lock another handle holds is not opened, so it is not verified at this start; the server takes the sibling and
    // starts. The bad file is left as it was. docs/audit-log.md, "Several server processes on one audit path".
    [Fact]
    public void Held_configured_file_that_does_not_verify_is_skipped_unverified_and_the_sibling_is_taken()
    {
        string path = _dir.File("audit.jsonl");
        Fx.Write(path, 3);
        List<byte[]> lines = Fx.Lines(path);
        lines.RemoveAt(1);
        Fx.WriteLines(path, lines);
        byte[] before = File.ReadAllBytes(path);
        Assert.False(AuditVerifier.Verify(path).Ok);

        using (HoldLock(path))
        {
            using AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime());
            Assert.Equal(Path.GetFullPath(_dir.File("audit.2.jsonl")), log.FilePath);
            log.Append(Fx.Entry(1));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(AuditVerifier.Verify(path).Ok);
        Assert.True(AuditVerifier.Verify(_dir.File("audit.2.jsonl")).Ok);
    }

    // Property 3: the configured file that does not verify is fatal; no sibling is opened instead.
    [Fact]
    public void Configured_file_that_does_not_verify_is_fatal_and_no_sibling_is_opened()
    {
        string path = _dir.File("audit.jsonl");
        Fx.Write(path, 2);
        File.AppendAllText(path, "{\"seq\":3");

        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime()));
        Assert.Contains("does not verify", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["audit.jsonl", "audit.jsonl.lock"], Names());
    }

    // Property 3: an error opening the lock file that is not "held" stays fatal on every platform: another code, a
    // subclass of IOException, or a different exception type. No sibling is tried.
    [Theory]
    [InlineData("lock-violation")]
    [InlineData("disk-full")]
    [InlineData("subclass-with-held-code")]
    [InlineData("access-denied")]
    public void Lock_open_error_other_than_held_is_fatal(string kind)
    {
        string path = _dir.File("audit.jsonl");
        int heldCode = AuditLog.CurrentPlatform == AuditLog.LockPlatform.Linux ? AuditLog.LinuxWouldBlock : AuditLog.WindowsSharingViolation;
        Exception failure = kind switch
        {
            "lock-violation" => new IOException("x", unchecked((int)0x80070021)),
            "disk-full" => new IOException("x", unchecked((int)0x80070070)),
            "subclass-with-held-code" => new FileNotFoundException("x") { HResult = heldCode },
            _ => new UnauthorizedAccessException("x"),
        };
        int calls = 0;

        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(
            path, "s", new FixedTime(), _ => { calls++; throw failure; }, AuditVerifier.MaxLineBytes));

        Assert.False(ex.LockHeld);
        Assert.DoesNotContain("locked by another writer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("audit.jsonl", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.Empty(Names());
    }

    // The seam's held code, for the platform this runs on, does move on to the sibling (the seam fails only for the
    // configured file's lock).
    [Fact]
    public void Lock_open_error_with_the_held_code_moves_to_the_sibling_on_windows_and_linux()
    {
        string path = _dir.File("audit.jsonl");
        string configuredLock = Path.GetFullPath(path) + ".lock";
        AuditLog.LockPlatform platform = AuditLog.CurrentPlatform;
        int heldCode = platform == AuditLog.LockPlatform.Linux ? AuditLog.LinuxWouldBlock : AuditLog.WindowsSharingViolation;
        FileStream OpenLock(string lockPath) => lockPath == configuredLock
            ? throw new IOException("x", heldCode)
            : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 0);

        if (platform == AuditLog.LockPlatform.Other)
        {
            // Fail-closed where the held error is not recognised.
            Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime(), OpenLock, AuditVerifier.MaxLineBytes));
            return;
        }

        using AuditLog log = AuditLog.OpenOrSibling(path, "s", new FixedTime(), OpenLock, AuditVerifier.MaxLineBytes);
        Assert.Equal(Path.GetFullPath(_dir.File("audit.2.jsonl")), log.FilePath);
    }

    // Property 3: how "held" is told apart, per platform, by exact type and HResult.
    [Theory]
    [InlineData("Windows", unchecked((int)0x80070020), true)]
    [InlineData("Windows", unchecked((int)0x80070021), false)]
    [InlineData("Windows", 11, false)]
    [InlineData("Windows", unchecked((int)0x80070005), false)]
    [InlineData("Linux", 11, true)]
    [InlineData("Linux", unchecked((int)0x80070020), false)]
    [InlineData("Linux", 13, false)]
    [InlineData("Linux", 28, false)]
    [InlineData("Other", 11, false)]
    [InlineData("Other", 35, false)]
    [InlineData("Other", unchecked((int)0x80070020), false)]
    public void Held_lock_is_recognised_by_exact_type_and_code(string platformName, int hresult, bool held)
    {
        var platform = Enum.Parse<AuditLog.LockPlatform>(platformName);
        Assert.Equal(held, AuditLog.IsLockHeld(new IOException("x", hresult), platform));
        Assert.False(AuditLog.IsLockHeld(new FileNotFoundException("x") { HResult = hresult }, platform));
        Assert.False(AuditLog.IsLockHeld(new PathTooLongException("x") { HResult = hresult }, platform));
    }

    // Property 3, on the platform this runs on: the exception a real held lock raises is classified as held (Windows:
    // 0x80070020; Linux: errno 11), and on any other platform as not held.
    [Fact]
    public void Real_held_lock_raises_the_code_recognised_on_this_platform()
    {
        string lockPath = _dir.File("x.lock");
        using FileStream first = HoldLockFile(lockPath);
        IOException ex = Assert.ThrowsAny<IOException>(() => HoldLockFile(lockPath).Dispose());
        Assert.Equal(typeof(IOException), ex.GetType());
        switch (AuditLog.CurrentPlatform)
        {
            case AuditLog.LockPlatform.Windows:
                Assert.Equal(AuditLog.WindowsSharingViolation, ex.HResult);
                Assert.True(AuditLog.IsLockHeld(ex, AuditLog.LockPlatform.Windows));
                break;
            case AuditLog.LockPlatform.Linux:
                Assert.Equal(AuditLog.LinuxWouldBlock, ex.HResult);
                Assert.True(AuditLog.IsLockHeld(ex, AuditLog.LockPlatform.Linux));
                break;
            default:
                Assert.False(AuditLog.IsLockHeld(ex, AuditLog.CurrentPlatform));
                break;
        }
    }

    // Property 3: the lock taken but the log itself held by a process that did not take the lock is fatal, not a
    // reason to move to a sibling.
    [Fact]
    public void Log_held_without_its_lock_is_fatal()
    {
        string path = _dir.File("audit.jsonl");
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime()));
            Assert.StartsWith("Audit file audit.jsonl cannot be opened although its lock was taken (IOException 0x", ex.Message, StringComparison.Ordinal);
            Assert.False(ex.LockHeld);
        }

        Assert.False(File.Exists(_dir.File("audit.2.jsonl")));
        Assert.False(File.Exists(_dir.File("audit.2.jsonl.lock")));
    }

    // Property 3: a folder that cannot be created (a file is in its place) is fatal.
    [Fact]
    public void Folder_that_cannot_be_created_is_fatal()
    {
        string blocker = _dir.File("not-a-folder");
        File.WriteAllText(blocker, "x");
        string path = Path.Combine(blocker, "audit.jsonl");

        var ex = Assert.Throws<AuditWriteException>(() => AuditLog.OpenOrSibling(path, "s", new FixedTime()));
        Assert.Contains("cannot be opened", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.LockHeld);
    }

    // Property 5, in process: writers that start at the same instant each end with a file of their own, the lowest
    // numbers, and every file verifies with only its own writer's lines. Nine writers for eight files: exactly one
    // fails with the bound's message while the other eight hold theirs.
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(9)]
    public void Writers_starting_together_each_get_their_own_file(int writers)
    {
        for (int round = 0; round < 10; round++)
        {
            using var dir = new TempDir();
            string path = dir.File("audit.jsonl");
            using var start = new Barrier(writers);
            using var opened = new Barrier(writers);
            var files = new string?[writers];
            var errors = new string?[writers];
            Thread[] threads = Enumerable.Range(0, writers).Select(i => new Thread(() =>
            {
                AuditLog? log = null;
                try
                {
                    start.SignalAndWait();
                    try
                    {
                        log = AuditLog.OpenOrSibling(path, $"writer-{i}", new FixedTime());
                        files[i] = log.FilePath;
                        for (int n = 1; n <= 3; n++)
                        {
                            log.Append(Fx.Entry(n));
                        }
                    }
                    catch (AuditWriteException ex)
                    {
                        errors[i] = ex.Message;
                    }

                    opened.SignalAndWait();
                }
                finally
                {
                    log?.Dispose();
                }
            })).ToArray();
            Array.ForEach(threads, t => t.Start());
            Array.ForEach(threads, t => Assert.True(t.Join(TimeSpan.FromSeconds(60))));

            int winners = Math.Min(writers, AuditLog.MaxFilesPerPath);
            string[] expected = Enumerable.Range(1, winners).Select(n => Path.GetFullPath(AuditLog.SiblingPath(path, n))).ToArray();
            Assert.Equal(expected.Order(StringComparer.Ordinal), files.OfType<string>().Order(StringComparer.Ordinal));
            Assert.Equal(writers - winners, errors.Count(e => e is not null));
            Assert.All(errors.OfType<string>(), e => Assert.Contains("are all locked by other writers", e, StringComparison.Ordinal));

            for (int i = 0; i < writers; i++)
            {
                if (files[i] is not { } file)
                {
                    continue;
                }

                AuditVerifyResult result = AuditVerifier.Verify(file);
                Assert.True(result.Ok, result.Message);
                Assert.Equal(3, result.LinesChecked);
                Assert.All(Fx.Lines(file), line => Assert.Equal($"writer-{i}", Field(line, "session")));
            }
        }
    }

    // D-N4: a configured path that ends in a folder separator names no file (a sibling of it would be a folder name
    // with a number); both opens refuse it with the same message before creating anything.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_path_that_ends_in_a_folder_separator_is_refused(bool orSibling, bool alternateSeparator)
    {
        string path = _dir.File("audit") + (alternateSeparator ? Path.AltDirectorySeparatorChar : Path.DirectorySeparatorChar);

        var ex = Assert.Throws<AuditWriteException>(() => orSibling
            ? AuditLog.OpenOrSibling(path, "s", new FixedTime())
            : AuditLog.Open(path, "s", new FixedTime()));

        Assert.Equal("The audit path ends in a folder separator; it must name a file, for example audit.jsonl.", ex.Message);
        Assert.False(ex.LockHeld);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir.Path));
    }

    private static string? Field(byte[] line, string name) =>
        JsonDocument.Parse(line).RootElement.GetProperty(name).GetString();

    private static FileStream HoldLock(string auditPath) => HoldLockFile(Path.GetFullPath(auditPath) + ".lock");

    private static FileStream HoldLockFile(string lockPath) =>
        new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 0);

    private string[] Names() =>
        Directory.EnumerateFiles(_dir.Path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
}

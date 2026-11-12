namespace MssqlReadOnlyMcp.Audit;

/// <summary>
/// Append-only, hash-chained JSON Lines audit log: one line per tool call, flushed to the device before
/// <see cref="Append(AuditEntry)"/> returns. The line format is described on the internal <c>AuditFormat</c> class.
/// </summary>
/// <remarks>
/// <para>
/// Honest limits: the hash chain shows edits, deletions and reordering of lines inside the file. It does not show
/// removal of the newest lines (tail truncation) or a full rewrite of the file by someone who can write the file,
/// unless <see cref="LastHash"/> is copied somewhere they cannot write.
/// </para>
/// <list type="bullet">
/// <item><description>The hash has no key, so anyone who can write the file, even with append-only rights, can add
/// lines that verify, and can recompute the chain from any line onward.</description></item>
/// <item><description>The SQL text, literals included, is stored in plain text and is readable by anyone who can read
/// the file.</description></item>
/// <item><description><c>ts</c> comes from the host clock; the verifier checks its format, not its
/// order.</description></item>
/// <item><description>On Linux the single-writer lock is an advisory <c>flock</c> and binds only cooperating
/// processes, that is, processes that take the same lock.</description></item>
/// <item><description>On Linux, flushing a newly created log file to disk does not persist its directory entry, so a
/// crash shortly after the file is created can lose the file; .NET offers no call to flush the
/// directory.</description></item>
/// <item><description>Verifying a file that is being written can report a partial last line.</description></item>
/// </list>
/// <para>
/// Fail-closed: when a line cannot be written and flushed, <see cref="Append(AuditEntry)"/> throws
/// <see cref="AuditWriteException"/> and the instance stays poisoned: every later append throws too, until the
/// file is reopened with <see cref="Open(string, string, TimeProvider?)"/>, which verifies it first.
/// </para>
/// <para>
/// Single writer: the writer holds an exclusive lock on a companion file named after the log with a
/// <c>.lock</c> suffix, which works across processes on Windows and Linux; the companion file is left in place on
/// close because deleting it could let two writers lock two different files. The log itself is opened for writing
/// with read sharing only. On Windows a reader must therefore open it allowing read-write sharing
/// (<see cref="FileShare.ReadWrite"/>), as <see cref="AuditVerifier.Verify(string)"/> does; a reader that allows
/// only read sharing is refused while the log is open.
/// </para>
/// </remarks>
public sealed class AuditLog : IDisposable
{
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal) { "allowed", "refused", "error" };

    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly IDisposable? _lockHandle;
    private readonly string _fileName;
    private readonly string _sessionId;
    private readonly TimeProvider _time;
    private readonly int _maxLineBytes;
    private long _lastSequence;
    private string _lastHash;
    private bool _poisoned;
    private bool _disposed;

    private AuditLog(
        Stream stream,
        IDisposable? lockHandle,
        string fileName,
        string sessionId,
        TimeProvider time,
        long lastSequence,
        string lastHash,
        int maxLineBytes)
    {
        _maxLineBytes = maxLineBytes;
        _stream = stream;
        _lockHandle = lockHandle;
        _fileName = fileName;
        _sessionId = AuditFormat.ToWellFormed(sessionId);
        _time = time;
        _lastSequence = lastSequence;
        _lastHash = lastHash;
    }

    /// <summary><c>seq</c> of the last line in the file; 0 for an empty file.</summary>
    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastSequence;
            }
        }
    }

    /// <summary><c>hash</c> of the last line in the file; 64 zeros for an empty file.</summary>
    public string LastHash
    {
        get
        {
            lock (_gate)
            {
                return _lastHash;
            }
        }
    }

    /// <summary>
    /// Opens or creates the audit file (and its parent directory), takes the single-writer lock, verifies the
    /// whole existing file and continues its chain from the last line.
    /// </summary>
    /// <param name="path">Path of the audit file.</param>
    /// <param name="sessionId">Session id written into every line.</param>
    /// <param name="time">Clock for the <c>ts</c> field; <see cref="TimeProvider.System"/> when null.</param>
    /// <returns>The open log.</returns>
    /// <exception cref="AuditWriteException">
    /// The file does not verify (the message names the first bad line; nothing is written), another writer holds
    /// it, or it cannot be created or opened.
    /// </exception>
    public static AuditLog Open(string path, string sessionId, TimeProvider? time = null) =>
        Open(path, sessionId, time, openData: null, AuditVerifier.MaxLineBytes);

    /// <summary>
    /// As <see cref="Open(string, string, TimeProvider?)"/>, with test seams: <paramref name="openData"/> opens the
    /// log file from its full path (null: the real <see cref="FileStream"/> with the sharing described on this class),
    /// and <paramref name="maxLineBytes"/> is the longest line accepted when verifying and appending.
    /// </summary>
    internal static AuditLog Open(
        string path, string sessionId, TimeProvider? time, Func<string, FileStream>? openData, int maxLineBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sessionId);
        string name = Path.GetFileName(path);
        FileStream? lockStream = null;
        FileStream? data = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                lockStream = new FileStream(
                    fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 0);
                data = openData is null
                    ? new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0)
                    : openData(fullPath);
            }
            catch (IOException ex)
            {
                throw new AuditWriteException(
                    $"Audit file {name} is locked by another writer or cannot be opened ({AuditWriteException.Describe(ex)}).");
            }

            AuditLog log = OpenOnStream(data, lockStream, name, sessionId, time, maxLineBytes);
            return log;
        }
        catch (AuditWriteException)
        {
            data?.Dispose();
            lockStream?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            data?.Dispose();
            lockStream?.Dispose();
            throw new AuditWriteException($"Audit file {name} cannot be opened ({AuditWriteException.Describe(ex)}).");
        }
    }

    /// <summary>
    /// Verifies <paramref name="stream"/> from its start and returns a log appending at its end. The returned log
    /// owns <paramref name="stream"/> and <paramref name="lockHandle"/>. Used by <see cref="Open(string, string, TimeProvider?)"/> and by tests that
    /// inject failing streams.
    /// </summary>
    internal static AuditLog OpenOnStream(
        Stream stream,
        IDisposable? lockHandle,
        string name,
        string sessionId,
        TimeProvider? time,
        int maxLineBytes = AuditVerifier.MaxLineBytes)
    {
        stream.Seek(0, SeekOrigin.Begin);
        AuditVerifier.StreamVerification verification = AuditVerifier.VerifyStream(stream, name, maxLineBytes);
        if (!verification.Result.Ok)
        {
            string where = verification.Result.FirstBadLine is long line ? $" at line {line}" : string.Empty;
            throw new AuditWriteException(
                $"Audit file {name} does not verify{where} ({verification.Reason}); nothing was written.");
        }

        stream.Seek(0, SeekOrigin.End);
        return new AuditLog(
            stream,
            lockHandle,
            name,
            sessionId,
            time ?? TimeProvider.System,
            verification.Chain.LastSequence,
            verification.Chain.LastHash,
            maxLineBytes);
    }

    /// <summary>
    /// Writes one line for <paramref name="entry"/> and returns only after it is flushed to the device.
    /// Safe to call from many threads; lines are written one at a time in <c>seq</c> order.
    /// </summary>
    /// <param name="entry">The tool call to record.</param>
    /// <exception cref="AuditWriteException">
    /// The line was not durably written, an earlier append failed (the log is poisoned), the log is closed, or the
    /// line would be longer than 16 MiB (refused before writing; this does not poison the log).
    /// <see cref="LastSequence"/> and <see cref="LastHash"/> are unchanged.
    /// </exception>
    /// <exception cref="ArgumentException">The entry has a missing field, an unknown decision or a malformed digest.</exception>
    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Tool is null || entry.Reason is null || entry.Tables is null)
        {
            throw new ArgumentException("Tool, Reason and Tables are required.", nameof(entry));
        }

        if (entry.Decision is null || !Decisions.Contains(entry.Decision))
        {
            throw new ArgumentException("Decision must be allowed, refused or error.", nameof(entry));
        }

        string[] tables = [.. entry.Tables];
        if (Array.Exists(tables, t => t is null))
        {
            throw new ArgumentException("Tables must not contain null.", nameof(entry));
        }

        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] = AuditFormat.ToWellFormed(tables[i]);
        }

        string? resultSha = entry.ResultSha256?.ToLowerInvariant();
        if (resultSha is not null && !AuditFormat.IsLowerHex64(resultSha))
        {
            throw new ArgumentException("ResultSha256 must be 64 hex characters.", nameof(entry));
        }

        // Early refusal before hashing or serialising: every char becomes at least one UTF-8 byte, so when the strings
        // alone are longer than the limit the line certainly is. The exact byte check after serialising stays.
        long chars = (long)entry.Tool.Length + entry.Decision.Length + entry.Reason.Length + (entry.Sql?.Length ?? 0)
            + (resultSha?.Length ?? 0);
        foreach (string table in tables)
        {
            chars += table.Length;
        }

        if (chars > _maxLineBytes)
        {
            throw new AuditWriteException($"Audit entry for {_fileName} was not written (LineTooLong).");
        }

        string? sqlSha = entry.Sql is null ? null : AuditFormat.Sha256Hex(entry.Sql);

        lock (_gate)
        {
            if (_disposed)
            {
                throw new AuditWriteException($"Audit log {_fileName} is closed.");
            }

            if (_poisoned)
            {
                throw new AuditWriteException($"Audit log {_fileName} is poisoned by an earlier write failure; reopen it.");
            }

            var record = new AuditRecord(
                _lastSequence + 1,
                AuditFormat.FormatTimestamp(_time.GetUtcNow()),
                _sessionId,
                AuditFormat.ToWellFormed(entry.Tool),
                entry.Decision,
                AuditFormat.ToWellFormed(entry.Reason),
                entry.Sql is null ? null : AuditFormat.ToWellFormed(entry.Sql),
                sqlSha,
                tables,
                entry.RowsReturned,
                entry.Truncated,
                entry.DurationMs,
                resultSha,
                _lastHash);
            string hash = AuditFormat.ComputeHash(record);
            byte[] body = AuditFormat.Serialize(record, hash);
            if (body.Length > _maxLineBytes)
            {
                // Refused before any byte is written, so the log is not poisoned; the verifier would reject the line.
                throw new AuditWriteException($"Audit line {record.Seq} of {_fileName} was not written (LineTooLong).");
            }

            byte[] line = new byte[body.Length + 1];
            body.CopyTo(line, 0);
            line[^1] = (byte)'\n';

            try
            {
                _stream.Write(line, 0, line.Length);
                if (_stream is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                _poisoned = true;
                throw new AuditWriteException(
                    $"Audit line {record.Seq} of {_fileName} was not durably written ({AuditWriteException.Describe(ex)}); the log is poisoned.");
            }

            _lastSequence = record.Seq;
            _lastHash = hash;
        }
    }

    /// <summary>Closes the file and releases the single-writer lock. Later appends throw <see cref="AuditWriteException"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
            _lockHandle?.Dispose();
        }
    }
}

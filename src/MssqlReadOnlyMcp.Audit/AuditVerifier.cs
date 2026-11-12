using System.Buffers;

namespace MssqlReadOnlyMcp.Audit;

/// <summary>Outcome of <see cref="AuditVerifier.Verify(string)"/>.</summary>
/// <param name="Ok">True when every line verifies and the file does not end in a partial line.</param>
/// <param name="LinesChecked">Number of lines that verified before the first bad line (all lines when <paramref name="Ok"/>).</param>
/// <param name="FirstBadLine">1-based number of the first bad line; null when the file verifies or could not be read.</param>
/// <param name="Message">Message built from the file name, a line number and a reason word only.</param>
public sealed record AuditVerifyResult(bool Ok, long LinesChecked, long? FirstBadLine, string Message);

/// <summary>Verifies an audit file written by <see cref="AuditLog"/>.</summary>
public static class AuditVerifier
{
    /// <summary>
    /// Longest accepted line in bytes, without its newline: 16 MiB. A longer line is reported as <c>LineTooLong</c>
    /// as soon as the limit is passed, without buffering the rest, and <see cref="AuditLog.Append(AuditEntry)"/>
    /// refuses to write one.
    /// </summary>
    internal const int MaxLineBytes = 16 * 1024 * 1024;

    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Reads the whole file and checks every line: canonical form, field order and types, <c>sql_sha256</c>,
    /// <c>hash</c>, <c>seq</c> continuity and <c>prev_hash</c> linkage. Never throws for a readable file; an
    /// unreadable or missing file gives <c>Ok = false</c>, and a line longer than 16 MiB gives <c>Ok = false</c> with
    /// reason <c>LineTooLong</c>. The file may be open by an <see cref="AuditLog"/> writer; a line being written at
    /// that moment can be reported as a partial last line.
    /// </summary>
    /// <param name="path">Path of the audit file.</param>
    /// <returns>The first problem found, or success with the number of lines checked.</returns>
    public static AuditVerifyResult Verify(string path) => Verify(path, MaxLineBytes);

    /// <summary>As <see cref="Verify(string)"/> with a chosen maximum line length (tests use a small one).</summary>
    internal static AuditVerifyResult Verify(string path, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = Path.GetFileName(path);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new AuditVerifyResult(false, 0, null, $"Audit file {name} is unreadable ({AuditWriteException.Describe(ex)}).");
        }

        using (stream)
        {
            return VerifyStream(stream, name, maxLineBytes).Result;
        }
    }

    /// <summary>Verifies from the current position to the end of <paramref name="stream"/>.</summary>
    internal static StreamVerification VerifyStream(Stream stream, string name, int maxLineBytes)
    {
        var checker = new AuditChainChecker();
        var line = new ArrayBufferWriter<byte>(4096);
        byte[] chunk = new byte[ChunkSize];
        long lineNumber = 0;
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                ReadOnlySpan<byte> rest = chunk.AsSpan(0, read);
                while (true)
                {
                    int newline = rest.IndexOf((byte)'\n');
                    int length = newline < 0 ? rest.Length : newline;
                    if (line.WrittenCount + (long)length > maxLineBytes)
                    {
                        return Fail(checker, lineNumber + 1, "LineTooLong", name);
                    }

                    if (newline < 0)
                    {
                        line.Write(rest);
                        break;
                    }

                    line.Write(rest[..newline]);
                    rest = rest[(newline + 1)..];
                    lineNumber++;
                    string? bad = checker.Check(line.WrittenMemory);
                    if (bad is not null)
                    {
                        return Fail(checker, lineNumber, bad, name);
                    }

                    line.ResetWrittenCount();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new StreamVerification(
                new AuditVerifyResult(
                    false, checker.LastSequence, null, $"Audit file {name} is unreadable ({AuditWriteException.Describe(ex)})."),
                "Unreadable",
                checker);
        }

        if (line.WrittenCount > 0)
        {
            // Bytes after the last newline: a write that did not finish.
            return Fail(checker, lineNumber + 1, "PartialLine", name);
        }

        return new StreamVerification(
            new AuditVerifyResult(true, lineNumber, null, $"Audit file {name}: {lineNumber} lines verified."),
            null,
            checker);
    }

    private static StreamVerification Fail(AuditChainChecker checker, long lineNumber, string reason, string name) =>
        new(
            new AuditVerifyResult(
                false, lineNumber - 1, lineNumber, $"Audit file {name} fails verification at line {lineNumber}: {reason}."),
            reason,
            checker);

    /// <summary>Verification result plus the chain state after the last good line.</summary>
    internal sealed record StreamVerification(AuditVerifyResult Result, string? Reason, AuditChainChecker Chain);
}

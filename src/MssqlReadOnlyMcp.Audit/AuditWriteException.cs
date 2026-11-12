namespace MssqlReadOnlyMcp.Audit;

/// <summary>
/// Thrown when an audit line cannot be durably written, or when an audit file cannot be opened for writing
/// (it does not verify, or another writer holds it). The message names only the file name, a line number and a
/// reason word; it never carries SQL text or field contents, and no inner exception is attached for that reason.
/// </summary>
public sealed class AuditWriteException : Exception
{
    /// <summary>Creates the exception with a message built from known-safe facts only.</summary>
    /// <param name="message">The message.</param>
    public AuditWriteException(string message)
        : base(message)
    {
    }

    internal static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}";
}

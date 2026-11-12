namespace MssqlReadOnlyMcp.Audit;

/// <summary>
/// One tool call to be recorded by <see cref="AuditLog.Append(AuditEntry)"/>.
/// </summary>
/// <param name="Tool">Tool name, for example <c>run_query</c>.</param>
/// <param name="Decision">One of <c>allowed</c>, <c>refused</c> or <c>error</c>.</param>
/// <param name="Reason">Short reason code, for example <c>Allowed</c>, <c>SelectInto</c> or <c>Timeout</c>.</param>
/// <param name="Sql">SQL text as received; <see langword="null"/> for tools without SQL.</param>
/// <param name="Tables">Names of the tables the call referenced.</param>
/// <param name="RowsReturned">Rows returned to the client; <see langword="null"/> when no rows were read.</param>
/// <param name="Truncated">Whether the returned rows were cut at the row cap.</param>
/// <param name="DurationMs">Duration of the call in milliseconds.</param>
/// <param name="ResultSha256">Hex SHA-256 digest of the payload returned to the client; <see langword="null"/> when none.</param>
public sealed record AuditEntry(
    string Tool,
    string Decision,
    string Reason,
    string? Sql,
    IReadOnlyList<string> Tables,
    int? RowsReturned,
    bool Truncated,
    long DurationMs,
    string? ResultSha256);

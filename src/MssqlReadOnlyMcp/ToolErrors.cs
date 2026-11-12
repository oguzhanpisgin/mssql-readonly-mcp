using System.Globalization;
using Microsoft.Data.SqlClient;
using MssqlReadOnlyMcp.Audit;
using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp;

/// <summary>A failed or refused tool call, described only by known-safe facts.</summary>
/// <param name="Decision">Audit decision: <c>refused</c> or <c>error</c>.</param>
/// <param name="Reason">Short reason word, also written to the audit line.</param>
/// <param name="Message">Text returned to the client.</param>
internal sealed record ToolFailure(string Decision, string Reason, string Message);

/// <summary>Thrown inside a tool for an expected, non-SQL failure; its message is built from known-safe facts.</summary>
internal sealed class ToolRefusalException(ToolFailure failure) : Exception(failure.Message)
{
    public ToolFailure Failure { get; } = failure;
}

/// <summary>
/// The one place that turns a refusal or exception into the text a client, a log line or an audit line may carry.
/// Nothing here copies a connection string, SQL text or a stack trace.
/// </summary>
internal static class ToolErrors
{
    public const string AuditUnavailableMessage =
        "AuditUnavailable: the audit log is unavailable, so the call was not completed and no data is returned.";

    /// <summary>The text for a call the SDK rejected before a tool ran (audited with reason <c>InvalidRequest</c>).</summary>
    public const string InvalidRequestMessage =
        "InvalidRequest: the call was rejected before the tool ran (an argument is missing or has the wrong type); no data is returned.";

    /// <summary>
    /// The failure for a gate refusal: the reason word once, then the gate's detail (which never echoes SQL). The gate
    /// starts its detail with the reason word; that copy is not repeated.
    /// </summary>
    public static ToolFailure GateRefusal(GateDecision decision)
    {
        var reason = decision.Reason.ToString();
        var detail = decision.Detail.StartsWith(reason + ": ", StringComparison.Ordinal)
            ? decision.Detail[(reason.Length + 2)..]
            : decision.Detail == reason ? string.Empty : decision.Detail;
        return new("refused", reason, detail.Length == 0
            ? $"{reason}: refused by the read-only gate."
            : $"{reason}: refused by the read-only gate; {detail}");
    }

    /// <summary>
    /// The failure for a call its own deadline ended: the same reason and text as SqlClient's own command timeout
    /// (error -2, class 11, line 0), so a timeout reads the same whichever bound ended the call.
    /// </summary>
    public static readonly ToolFailure CallTimeout = new("error", "Timeout", "Timeout: SQL Server error -2, class 11, line 0");

    /// <summary>A refusal for an invalid argument.</summary>
    public static ToolFailure InvalidArgument(string message) => new("error", "InvalidArgument", $"InvalidArgument: {message}");

    /// <summary>The failure for an exception thrown while a tool ran.</summary>
    public static ToolFailure FromException(Exception exception) => exception switch
    {
        ToolRefusalException refusal => refusal.Failure,
        SqlException sql => FromSqlException(sql),
        OperationCanceledException => new("error", "Cancelled", "Cancelled: the call was cancelled."),
        AuditWriteException => new("error", "AuditUnavailable", AuditUnavailableMessage),
        _ => new("error", "InternalError", "InternalError: the call failed."),
    };

    /// <summary>One stderr-safe phrase for a startup failure: an exception type, and for SQL its number and class.</summary>
    public static string DescribeForLog(Exception exception) => exception switch
    {
        SqlException sql => string.Create(
            CultureInfo.InvariantCulture, $"SqlException number {sql.Number}, class {sql.Class}, state {sql.State}"),
        _ => exception.GetType().Name,
    };

    private static ToolFailure FromSqlException(SqlException exception)
    {
        var reason = exception.Number switch
        {
            -2 => "Timeout",
            1222 => "LockTimeout",
            1205 => "DeadlockVictim",
            229 => "PermissionDenied",
            _ => "SqlError",
        };

        var facts = string.Create(
            CultureInfo.InvariantCulture,
            $"{reason}: SQL Server error {exception.Number}, class {exception.Class}, line {exception.LineNumber}");

        // Only user-error classes carry the server's message text; higher classes can describe server internals.
        var first = exception.Errors.Count > 0 ? exception.Errors[0] : null;
        var text = exception.Class is >= 11 and <= 16 && first is not null && exception.Number > 0
            ? $": {first.Message}"
            : string.Empty;
        return new ToolFailure("error", reason, facts + text);
    }
}

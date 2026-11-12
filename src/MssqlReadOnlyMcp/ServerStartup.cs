using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MssqlReadOnlyMcp.Audit;

namespace MssqlReadOnlyMcp;

/// <summary>Process exit codes.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int AuditVerifyFailed = 1;
    public const int ConfigOrAudit = 2;
    public const int LoginCanWrite = 3;
    public const int Unreachable = 4;
    public const int InternalError = 5;

    /// <summary>The server stopped while calls were still running; their audit lines may be missing.</summary>
    public const int DrainTimedOut = 6;
}

/// <summary>Outcome of the start sequence: an exit code and one stderr line, or a ready server.</summary>
internal sealed record StartupOutcome(int ExitCode, string? ErrorLine, PermissionReport? Report);

/// <summary>
/// The start order after the configuration and the audit log are open: connect and run the permission check, then
/// append one <c>startup</c> audit line (<c>allowed</c>, or <c>refused</c> with reason <c>LoginCanWrite</c>).
/// </summary>
internal static class ServerStartup
{
    public const string Prefix = "mssql-readonly-mcp: ";

    public static async Task<StartupOutcome> CheckAndRecordAsync(
        ServerConfig config, SqlGateway gateway, AuditLog audit, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        PermissionReport report;
        try
        {
            report = await PermissionCheck.RunAsync(gateway, config.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var (exitCode, line) = DescribeCheckFailure(exception);
            return new StartupOutcome(exitCode, line, null);
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var entry = new AuditEntry(
            "startup",
            report.Passed ? "allowed" : "refused",
            report.Passed ? "Allowed" : "LoginCanWrite",
            null,
            [],
            null,
            false,
            durationMs,
            null);

        string? auditError = null;
        try
        {
            audit.Append(entry);
        }
        catch (Exception exception) when (exception is AuditWriteException or ArgumentException)
        {
            auditError = exception.Message;
        }

        if (!report.Passed)
        {
            var count = report.Findings.Count;
            var auditNote = auditError is null ? string.Empty : " The startup audit line could not be written.";
            return new StartupOutcome(
                ExitCodes.LoginCanWrite,
                $"{Prefix}refusing to start: the login holds rights beyond read-only access ({count} finding{(count == 1 ? string.Empty : "s")}, first: " +
                $"{report.Findings[0]}). Run 'mssql-readonly-mcp check-login' for the full list.{auditNote}",
                report);
        }

        if (auditError is not null)
        {
            return new StartupOutcome(ExitCodes.ConfigOrAudit, $"{Prefix}audit log error: {auditError}", report);
        }

        return new StartupOutcome(ExitCodes.Ok, null, report);
    }

    /// <summary>
    /// The exit code and the one stderr line for any exception raised while connecting or running the permission
    /// check. The line is built from the exception's type name (and, for a <see cref="SqlException"/>, its number,
    /// class and state), never from its message, which can quote the connection string.
    /// </summary>
    public static (int ExitCode, string Line) DescribeCheckFailure(Exception exception)
    {
        // The client library rejects a connection string it parsed (a keyword combination it does not support, for
        // example) with an argument or not-supported exception before any network traffic: a configuration error.
        if (exception is ArgumentException or NotSupportedException or FormatException or KeyNotFoundException)
        {
            return (
                ExitCodes.ConfigOrAudit,
                $"{Prefix}configuration error: the SQL Server client library rejected {ServerConfig.ConnectionStringVariable} ({exception.GetType().Name}).");
        }

        return (
            ExitCodes.Unreachable,
            $"{Prefix}cannot connect to SQL Server or run the permission check ({ToolErrors.DescribeForLog(exception)}).");
    }
}

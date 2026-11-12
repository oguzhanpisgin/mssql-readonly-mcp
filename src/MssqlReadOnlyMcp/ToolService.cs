using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using MssqlReadOnlyMcp.Audit;
using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp;

/// <summary>
/// The three tools. Every call ends in <see cref="CompleteAsync"/>, which appends exactly one audit line before the
/// result or the error goes back; when the append fails the call returns an error and no data.
/// </summary>
internal sealed class ToolService(
    ServerConfig config,
    SqlGateway gateway,
    AuditLog audit,
    string database,
    ILogger<ToolService> logger)
{
    /// <summary>The tool names this server serves; any other name is audited as <c>unknown</c>.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["list_tables", "describe_table", "run_query"];

    /// <summary>The gate's size cap for <c>run_query</c> text; longer text is refused and never recorded.</summary>
    public static readonly int MaxSqlChars = new GateOptions().MaxSqlChars;

    private int callsInFlight;

    /// <summary>The connection's database; the gate's <see cref="GateOptions.DefaultDatabase"/>.</summary>
    public string Database => database;

    /// <summary>Calls that have started and not yet finished their audit line.</summary>
    public int CallsInFlight => Volatile.Read(ref callsInFlight);

    public Task<string> ListTablesAsync(string? schema, string? nameFilter, CancellationToken cancellationToken) =>
        CompleteAsync("list_tables", null, async call =>
        {
            // list_tables records neither argument in its audit line, so a refusal here records nothing of them.
            if (schema is { Length: > SchemaQueries.MaxNamePartChars })
            {
                throw new ToolRefusalException(ToolErrors.InvalidArgument(
                    $"schema must be at most {SchemaQueries.MaxNamePartChars} characters."));
            }

            if (nameFilter is { Length: > SchemaQueries.MaxNameFilterChars })
            {
                throw new ToolRefusalException(ToolErrors.InvalidArgument(
                    $"name_filter must be at most {SchemaQueries.MaxNameFilterChars} characters."));
            }

            RefuseHiddenCharacters("schema", schema);
            RefuseHiddenCharacters("name_filter", nameFilter);
            var result = await SchemaQueries.ListTablesAsync(
                    gateway, schema, nameFilter, config.MaxChars, config.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            call.Rows = result.ItemCount;
            call.Truncated = result.Truncated;
            return result.Payload;
        });

    public Task<string> DescribeTableAsync(string table, CancellationToken cancellationToken) =>
        CompleteAsync("describe_table", null, async call =>
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ToolRefusalException(ToolErrors.InvalidArgument("table is required."));
            }

            // Bounded before it is recorded: no longer text can be a name, and the audit line stays small.
            if (table.Length > SchemaQueries.MaxNameChars)
            {
                throw new ToolRefusalException(ToolErrors.InvalidArgument(
                    $"table must be at most {SchemaQueries.MaxNameChars} characters."));
            }

            // Not recorded either: SQL Server resolves such a name to the object without the character, so the
            // audit line would name something other than what was read.
            RefuseHiddenCharacters("table", table);
            call.Tables = [table];

            // A name SQL Server could never resolve (a part over 128 characters, more than four parts) is answered
            // here, before any connection is opened.
            if (SchemaQueries.CannotNameAnObject(table))
            {
                throw new ToolRefusalException(NotFound());
            }

            var result = await SchemaQueries.DescribeTableAsync(
                    gateway, table, config.MaxChars, config.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ToolRefusalException(NotFound());
            call.Tables = result.Tables;
            call.Rows = result.ItemCount;
            call.Truncated = result.Truncated;
            return result.Payload;
        });

    public Task<string> RunQueryAsync(string sql, int? maxRows, CancellationToken cancellationToken) =>
        // Text over the gate's size cap is never copied into the audit line, whatever its size, so the line always
        // fits and the call is always audited. The gate refuses it as TooLong without parsing.
        CompleteAsync("run_query", sql is { Length: var length } && length > MaxSqlChars ? null : sql ?? string.Empty, async call =>
        {
            var decision = ReadOnlyGate.Evaluate(sql, new GateOptions(DefaultDatabase: database, MaxSqlChars: MaxSqlChars));
            call.Tables = decision.Tables;
            if (decision.Reason == GateReason.TooLong)
            {
                call.Sql = null;
            }

            if (!decision.Allowed)
            {
                // Refused: nothing is sent to SQL Server.
                throw new ToolRefusalException(ToolErrors.GateRefusal(decision));
            }

            return await ReadAllowedTextAsync(call, sql!, maxRows, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>
    /// The <c>run_query</c> call without the gate: the same argument check, read, audit line and errors as an allowed
    /// call. Internal so tests can show what the server side does with text the gate would refuse.
    /// </summary>
    internal Task<string> RunPastTheGateAsync(string sql, int? maxRows, CancellationToken cancellationToken) =>
        CompleteAsync("run_query", sql, call => ReadAllowedTextAsync(call, sql, maxRows, cancellationToken));

    /// <summary>Everything <c>run_query</c> does after the gate allowed the text.</summary>
    private async Task<string> ReadAllowedTextAsync(CallState call, string sql, int? maxRows, CancellationToken cancellationToken)
    {
        if (maxRows is < 1)
        {
            throw new ToolRefusalException(ToolErrors.InvalidArgument("max_rows must be 1 or more."));
        }

        var rowCap = Math.Min(maxRows ?? config.MaxRows, config.MaxRows);
        var result = await ExecuteReadAsync(sql, rowCap, cancellationToken).ConfigureAwait(false);
        call.Rows = result.RowCount;
        call.Truncated = result.Truncated;
        return result.Payload;
    }

    /// <summary>
    /// Runs text that already passed the gate: a fresh connection, the session settings as their own command, then the
    /// text exactly as received, alone in its command, inside a transaction that is always rolled back. Internal so
    /// tests can send a statement past the gate on purpose and show that SQL Server itself refuses it.
    /// </summary>
    internal async Task<QueryResult> ExecuteReadAsync(string sql, int rowCap, CancellationToken cancellationToken)
    {
        await using var connection = await gateway.OpenSessionAsync(config.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = gateway.CreateCommand(connection, sql, transaction, config.TimeoutSeconds);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            QueryResult result;
            try
            {
                result = await ResultPayload.ReadAsync(reader, rowCap, config.MaxChars, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                command.Cancel();
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
                throw;
            }

            if (result.Truncated)
            {
                // Stop the server from producing more rows instead of draining them.
                command.Cancel();
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
            }
            else if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                // The gate passed one statement; a second result set means SQL Server read the text differently.
                // Nothing is returned.
                command.Cancel();
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
                throw new ToolRefusalException(new ToolFailure(
                    "error",
                    "MultipleResultSets",
                    "MultipleResultSets: SQL Server returned more than one result set for the text; no data is returned."));
            }
            else
            {
                await reader.DisposeAsync().ConfigureAwait(false);

                // RecordsAffected is -1 for a text that only reads. Any other value means a statement changed rows
                // inside the transaction (it is rolled back below): SQL Server read the text differently from the gate,
                // or the login can write. Nothing is returned. Not seen when the first set was truncated: the command
                // is cancelled before the rest of the text can report.
                if (reader.RecordsAffected != -1)
                {
                    throw new ToolRefusalException(new ToolFailure(
                        "error",
                        "RowsChanged",
                        "RowsChanged: SQL Server reported rows changed by the text; the change was rolled back and no data is returned."));
                }
            }

            return result;
        }
        finally
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqlException)
            {
                // The server already ended the transaction (for example a deadlock victim). The physical connection
                // is discarded so nothing of this session is reused.
                SqlConnection.ClearPool(connection);
            }

            transaction.Dispose();
            gateway.RecordReceived(connection);
        }
    }

    /// <summary>
    /// Counts a call as in flight until the returned handle is disposed; <see cref="DrainAsync"/> waits for the count
    /// to reach zero. Nested handles for the same call are allowed.
    /// </summary>
    public IDisposable TrackCall()
    {
        Interlocked.Increment(ref callsInFlight);
        return new CallHandle(this);
    }

    /// <summary>
    /// Waits until no call is in flight, so the audit log is closed only after every started call has written its
    /// line. Returns false when calls are still running after <paramref name="timeout"/>.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (CallsInFlight > 0)
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Appends the audit line for a call the MCP SDK rejected before a tool ran (an unknown tool name, a missing or
    /// mistyped argument). No argument is recorded. Returns false when the line could not be written.
    /// </summary>
    public bool AuditRejectedCall(string? toolName, long durationMs)
    {
        var tool = toolName is not null && ToolNames.Contains(toolName, StringComparer.Ordinal) ? toolName : "unknown";
        var entry = new AuditEntry(tool, "error", "InvalidRequest", null, [], null, false, durationMs, null);
        try
        {
            audit.Append(entry);
        }
        catch (Exception exception)
        {
            logger.LogError("{Tool}: audit append failed ({Error})", tool, exception.GetType().Name);
            return false;
        }

        logger.LogInformation("{Tool} {Decision} {Reason} {DurationMs} ms", tool, entry.Decision, entry.Reason, durationMs);
        return true;
    }

    /// <summary>
    /// Logs, at warning level, that a call failed before a tool ran: the tool name (or <c>unknown</c>) and the
    /// exception type only, never its message or the arguments.
    /// </summary>
    public void LogRejectedCall(string? toolName, Exception exception)
    {
        var tool = toolName is not null && ToolNames.Contains(toolName, StringComparer.Ordinal) ? toolName : "unknown";
        logger.LogWarning("{Tool}: rejected before the tool ran ({Error})", tool, exception.GetType().Name);
    }

    /// <summary>
    /// Refuses an argument that holds a character SQL Server can ignore when it resolves a name, or one the gate
    /// refuses outside literals: a control (Cc), format (Cf), line or paragraph separator (Zl, Zp), or space separator
    /// (Zs) other than U+0020; U+FFFD; a private-use (Co) or unassigned (Cn) code point; a Unicode noncharacter; or an
    /// unpaired surrogate. The message names the code point and its class, never the argument.
    /// </summary>
    internal static void RefuseHiddenCharacters(string argument, string? text)
    {
        if (FindHiddenCharacter(text) is { } found)
        {
            throw new ToolRefusalException(ToolErrors.InvalidArgument(string.Create(
                CultureInfo.InvariantCulture,
                $"{argument} contains U+{found.CodePoint:X4}, {found.Kind}; names with such characters are not accepted.")));
        }
    }

    /// <summary>The first refused character of <paramref name="text"/> with its class, or null.</summary>
    internal static (int CodePoint, string Kind)? FindHiddenCharacter(string? text)
    {
        if (text is null)
        {
            return null;
        }

        for (var i = 0; i < text.Length; i++)
        {
            Rune rune;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                rune = new Rune(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return (text[i], "an unpaired surrogate");
            }
            else
            {
                rune = new Rune(text[i]);
            }

            if (Classify(rune) is { } kind)
            {
                return (rune.Value, kind);
            }
        }

        return null;
    }

    private static string? Classify(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 0xFDD0 and <= 0xFDEF || (value & 0xFFFE) == 0xFFFE)
        {
            return "a Unicode noncharacter";
        }

        if (rune == Rune.ReplacementChar)
        {
            return "the replacement character";
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control => "a control character",
            UnicodeCategory.Format => "a format character",
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => "a line or paragraph separator",
            UnicodeCategory.SpaceSeparator when value != ' ' => "a space separator other than U+0020",
            UnicodeCategory.PrivateUse => "a private-use code point",
            UnicodeCategory.OtherNotAssigned => "an unassigned code point",
            _ => null,
        };
    }

    /// <summary>Runs <paramref name="body"/>, appends one audit line, then returns the payload or throws the error.</summary>
    private async Task<string> CompleteAsync(string tool, string? sql, Func<CallState, Task<string>> body)
    {
        using var tracked = TrackCall();
        CallAuditMarker.Current?.MarkAudited();
        var started = Stopwatch.GetTimestamp();
        var call = new CallState { Sql = sql };
        string? payload = null;
        ToolFailure? failure = null;
        try
        {
            payload = await body(call).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = ToolErrors.FromException(exception);
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var entry = new AuditEntry(
            tool,
            failure?.Decision ?? "allowed",
            failure?.Reason ?? "Allowed",
            call.Sql,
            call.Tables,
            payload is null ? null : call.Rows,
            payload is not null && call.Truncated,
            durationMs,
            payload is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));

        try
        {
            audit.Append(entry);
        }
        catch (Exception exception)
        {
            logger.LogError("{Tool}: audit append failed ({Error}); returning an error and no data", tool, exception.GetType().Name);
            throw new McpException(ToolErrors.AuditUnavailableMessage);
        }

        logger.LogInformation(
            "{Tool} {Decision} {Reason} {DurationMs} ms", tool, entry.Decision, entry.Reason, durationMs);

        if (failure is not null)
        {
            throw new McpException(failure.Message);
        }

        return payload!;
    }

    private static async Task DisposeQuietlyAsync(SqlDataReader reader)
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Closing a cancelled command reports the cancellation; the rows already read are complete.
        }
    }

    private ToolFailure NotFound() =>
        new("error", "NotFound", $"NotFound: no user table or view with that name in database {database}.");

    private sealed class CallHandle(ToolService owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.callsInFlight);
            }
        }
    }

    private sealed class CallState
    {
        /// <summary>The SQL text for the audit line; null when it is not recorded.</summary>
        public string? Sql { get; set; }

        public IReadOnlyList<string> Tables { get; set; } = [];

        public int? Rows { get; set; }

        public bool Truncated { get; set; }
    }
}

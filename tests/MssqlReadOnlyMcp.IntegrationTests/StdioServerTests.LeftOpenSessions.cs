using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Security finding U-1: after a call whose connection SqlClient broke under a reader (a truncated result whose reader
/// close is frozen; a <c>Timeout</c> on a throttled link), SqlClient leaves the socket open, and without
/// <c>LeftOpenConnections</c> the session stays at SQL Server with its open transaction and locks until the server
/// process ends. These tests run the server as its
/// own process, as a client does: in the test process the garbage collector of the busy test host can close such a
/// socket by finalizing it, and hide the defect. Each test releases the network and, on its failure path too, ends
/// the server process and waits until no session of its workstation name is left: the container is shared.
/// </summary>
public sealed partial class StdioServerTests
{
    // 20,000 rows of about 400 characters: truncated at the default max_chars after about 97 rows, while SQL Server
    // still has megabytes of the result to send.
    private const string ManyRowsSql =
        "SELECT TOP (20000) a.object_id, REPLICATE(CONVERT(varchar(max), 'p'), 400) AS pad FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b";

    // The same size of result read from a user table that stays the outer side of the plan (the apply is correlated),
    // so its scan is still open, with its locks, while SQL Server waits for the network: 600 order lines x 100 rows.
    private const string ManyRowsFromATableSql =
        "SELECT l.OrderId, l.LineNumber, x.object_id, REPLICATE(CONVERT(varchar(max), 'p'), 400) AS pad FROM dbo.OrderLines AS l " +
        "CROSS APPLY (SELECT TOP (100) a.object_id FROM sys.all_objects AS a WHERE a.object_id <> l.OrderId) AS x";

    /// <summary>
    /// The bound this fold states: once the network works again, the session of the call is gone (no open transaction,
    /// no lock, no session) within this time.
    /// </summary>
    private static readonly TimeSpan SessionEndBound = TimeSpan.FromSeconds(2);

    [Fact]
    public Task Truncated_result_whose_reader_close_is_frozen_ends_its_session_once_the_network_works_again() =>
        // U-1 case 1: the client has its rows (the result is truncated) and cancels the rest; the connection freezes
        // before the cancel is acknowledged, so the reader close cannot finish. The cancel reaches SQL Server before the
        // freeze: the request has ended there, the transaction is still open.
        TruncatedThenFrozenAsync(holdTheCancel: false);

    [Fact]
    public Task Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only() =>
        // U-1 case 1 as run 4 saw it: what the client sends is held from the first rows on, so the cancel reaches SQL
        // Server only when the network works again; until then the request keeps running, blocked on the network,
        // with its locks.
        TruncatedThenFrozenAsync(holdTheCancel: true);

    private async Task TruncatedThenFrozenAsync(bool holdTheCancel)
    {
        const int LimitSeconds = 3;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        var auditPath = fixture.NewAuditPath();
        var server = RawStdioServer.Start(
            ServerDll,
            ThroughProxy(proxy, fixture.ReaderConnectionString, marker),
            auditPath,
            (ServerConfig.TimeoutVariable, LimitSeconds.ToString(CultureInfo.InvariantCulture)));
        var passed = false;
        try
        {
            await server.InitializeAsync();

            // Frozen in both directions once 200,000 bytes came from SQL Server: the client already holds more than the
            // rows of its truncated result; its cancel and the rest of the result stay in the proxy.
            proxy.FreezeAfterServerBytes = 200_000;
            if (holdTheCancel)
            {
                // The login, the session settings and the transaction start take a few KB; 20,000 bytes are well inside
                // the result and short of the rows the truncation needs, so nothing the client sends after its query
                // passes, the cancel included.
                proxy.HoldClientAfterServerBytes = 20_000;
            }

            var watch = Stopwatch.StartNew();
            var answer = await server.CallAsync(2, "run_query", new { sql = holdTheCancel ? ManyRowsFromATableSql : ManyRowsSql });
            var answeredAt = watch.Elapsed;
            var line = TestText.AuditLines(auditPath)[^1];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"truncated, then frozen{(holdTheCancel ? ", cancel held" : string.Empty)}: answer at {answeredAt.TotalMilliseconds:F0} ms, {Key(line)}, rows {line.GetProperty("rows")}, " +
                $"truncated {line.GetProperty("truncated")}, duration_ms {line.GetProperty("duration_ms")}"));

            // Invariant 3: the rows come back only when the body, its reader close and its session end included, ended
            // within limit + grace; otherwise Timeout and no data. Which of the two happens depends on when SqlClient
            // gives up the frozen close (its command timeout, the limit counted from the query's start).
            Assert.True(answeredAt <= ClientBound(LimitSeconds), $"answered after {answeredAt}");
            AssertDataOnlyInsideTheCutoff(answer, line, LimitSeconds);

            // The freeze holds 12 s from the call: SqlClient breaks the connection meanwhile. SQL Server cannot know.
            await DelayUntilAsync(watch, TimeSpan.FromSeconds(12));
            var frozen = await SessionStateAsync(admin, marker);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"while frozen at 12 s: sessions {frozen.Sessions}, requests {frozen.Requests}, transactions {frozen.Transactions}, locks {frozen.Locks}"));
            Assert.True(frozen.Transactions > 0, "no open transaction while the link was frozen");
            if (holdTheCancel)
            {
                Assert.True(frozen.Requests > 0, "the request had ended while the cancel was held");
                Assert.True(frozen.Locks > 0, "no lock while the cancel was held");
            }

            passed = true;
        }
        finally
        {
            proxy.FreezeAfterServerBytes = 0;
            proxy.HoldClientAfterServerBytes = 0;
            proxy.ReleaseAll();
            if (!passed)
            {
                await EndServerAndSettleAsync(server, admin, marker);
            }
        }

        await AssertSessionEndsAfterTheReleaseAsync(server, admin, marker, auditPath);
    }

    [Fact]
    public async Task Timeout_on_a_throttled_link_ends_its_session_once_the_network_works_again()
    {
        // U-1 case 2: a plain Timeout on a link throttled to 5 KB/s; the cancel's acknowledgement waits behind the rest of
        // the result.
        const int LimitSeconds = 3;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        var auditPath = fixture.NewAuditPath();
        var server = RawStdioServer.Start(
            ServerDll,
            ThroughProxy(proxy, fixture.ReaderConnectionString, marker),
            auditPath,
            (ServerConfig.TimeoutVariable, LimitSeconds.ToString(CultureInfo.InvariantCulture)));
        var passed = false;
        try
        {
            await server.InitializeAsync();
            proxy.ServerBytesPerSecond = 5_000;
            var watch = Stopwatch.StartNew();
            var answer = await server.CallAsync(2, "run_query", new { sql = ManyRowsSql });
            var answeredAt = watch.Elapsed;
            var line = TestText.AuditLines(auditPath)[^1];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"throttled 5 KB/s: answer at {answeredAt.TotalMilliseconds:F0} ms, {Key(line)}, duration_ms {line.GetProperty("duration_ms")}"));

            Assert.Equal((true, ToolErrors.CallTimeout.Message), ToolAnswer(answer));
            Assert.True(answeredAt <= ClientBound(LimitSeconds), $"answered after {answeredAt}");
            AssertTimeoutLine(line, LimitSeconds);

            // The throttle holds 20 s from the call. Where the session ends meanwhile, it is recorded, not required: the
            // bound is stated for the time after the network works again.
            var endedWhileThrottled = (TimeSpan?)null;
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (endedWhileThrottled is null && (await SessionStateAsync(admin, marker)).Sessions == 0)
                {
                    endedWhileThrottled = watch.Elapsed;
                }

                await Task.Delay(100);
            }

            output.WriteLine(endedWhileThrottled is { } ended
                ? string.Create(CultureInfo.InvariantCulture, $"session ended {ended.TotalMilliseconds:F0} ms after the call started, while still throttled")
                : "session still present when the throttle ended");
            passed = true;
        }
        finally
        {
            proxy.ServerBytesPerSecond = 0;
            if (!passed)
            {
                await EndServerAndSettleAsync(server, admin, marker);
            }
        }

        await AssertSessionEndsAfterTheReleaseAsync(server, admin, marker, auditPath);
    }

    [Fact]
    public async Task The_state_object_of_a_live_connection_is_one_whose_network_handle_the_fix_resolved()
    {
        // The unit guard checks the types; this checks that a real connection on this platform uses one of them.
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        await using var connection = new SqlConnection(fixture.ReaderConnectionString);
        await connection.OpenAsync();
        var parser = typeof(SqlConnection).GetProperty("Parser", Members)!.GetValue(connection)!;
        var stateObject = parser.GetType().GetField("_physicalStateObj", Members)!.GetValue(parser)!;
        output.WriteLine($"state object of a live connection: {stateObject.GetType().FullName}");

        Assert.True(LeftOpenConnections.Available);
        Assert.Contains(stateObject.GetType(), LeftOpenConnections.StateObjectTypes);
    }

    /// <summary>
    /// After the release: within <see cref="SessionEndBound"/> no session of <paramref name="marker"/> is left, with or
    /// without a transaction or a lock; then a next call returns ok, the process exits 0 at end of input, and the
    /// audit file verifies. On a failure the server process is ended and the sessions settled before the test fails.
    /// </summary>
    private async Task AssertSessionEndsAfterTheReleaseAsync(
        RawStdioServer server, SqlConnection admin, string marker, string auditPath)
    {
        var passed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var state = await SessionStateAsync(admin, marker);
            var firstTransactionFree = state.Transactions == 0 && state.Locks == 0 ? TimeSpan.Zero : (TimeSpan?)null;
            while ((state.Sessions > 0 || state.Transactions > 0 || state.Locks > 0) && watch.Elapsed < SessionEndBound + TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
                state = await SessionStateAsync(admin, marker);
                if (firstTransactionFree is null && state.Transactions == 0 && state.Locks == 0)
                {
                    firstTransactionFree = watch.Elapsed;
                }
            }

            var gone = watch.Elapsed;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"after the release: no open transaction or lock after {firstTransactionFree?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) ?? "never"} ms, " +
                $"no session after {gone.TotalMilliseconds:F0} ms (last state: sessions {state.Sessions}, transactions {state.Transactions}, locks {state.Locks})"));
            Assert.True(state.Sessions == 0 && state.Transactions == 0 && state.Locks == 0, $"the session of the call was still there {gone} after the release");
            Assert.True(gone <= SessionEndBound, $"the session of the call ended {gone} after the release");

            var next = await server.CallAsync(3, "run_query", new { sql = "SELECT 1 AS one" });
            Assert.False(ToolAnswer(next).IsError, next.GetRawText());
            var run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
            AssertOnlyCallLogLines(run.Stderr);
            // The line the server writes at start and one per call: the abandoned clean-up wrote none.
            Assert.Equal(3, TestText.AuditLines(auditPath).Count);
            var verify = await RunServerAsync(["verify-audit", auditPath], null, null);
            Assert.True(verify.ExitCode == 0, string.Join(" | ", verify.Stdout.Concat(verify.Stderr)));
            AssertNoSecrets(new ProcessRun(run.ExitCode, run.Stdout, run.Stderr), auditPath);
            passed = true;
        }
        finally
        {
            if (!passed)
            {
                await EndServerAndSettleAsync(server, admin, marker);
            }
            else
            {
                await server.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Invariant 3 for one answer: rows only when the audit line says the body ended within limit + grace (plus a
    /// margin for the audit append on a loaded machine); otherwise <c>Timeout</c> with no data.
    /// </summary>
    private static void AssertDataOnlyInsideTheCutoff(JsonElement answer, JsonElement line, int limitSeconds)
    {
        var (isError, text) = ToolAnswer(answer);
        if (isError)
        {
            Assert.Equal(ToolErrors.CallTimeout.Message, text);
            AssertTimeoutLine(line, limitSeconds);
            return;
        }

        Assert.Equal("run_query:allowed:Allowed", Key(line));
        Assert.True(line.GetProperty("truncated").GetBoolean(), "the result was not truncated");
        Assert.True(line.GetProperty("rows").GetInt32() > 0, "no rows");
        var duration = line.GetProperty("duration_ms").GetInt64();
        Assert.True(
            duration <= (TimeSpan.FromSeconds(limitSeconds) + ToolService.Grace + TimeSpan.FromMilliseconds(100)).TotalMilliseconds,
            $"rows returned by a call that ended after its cutoff (duration_ms {duration})");
    }

    /// <summary>Ends the server process (its sockets close with it) and waits until no session of the marker is left.</summary>
    private static async Task EndServerAndSettleAsync(RawStdioServer server, SqlConnection admin, string marker)
    {
        await server.DisposeAsync();
        await SettleSessionsAsync(admin, marker);
    }

    private static async Task DelayUntilAsync(Stopwatch watch, TimeSpan at)
    {
        var left = at - watch.Elapsed;
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left);
        }
    }

    /// <summary>
    /// What SQL Server holds for <paramref name="marker"/>, read by the administrator: sessions
    /// (<c>sys.dm_exec_sessions</c>), requests (<c>sys.dm_exec_requests</c>), open transactions (<c>sys.dm_tran_session_transactions</c>) and locks other than
    /// the database lock (<c>sys.dm_tran_locks</c>).
    /// </summary>
    private static async Task<(int Sessions, int Requests, int Transactions, int Locks)> SessionStateAsync(SqlConnection admin, string marker)
    {
        await using var command = new SqlCommand(
            "SELECT (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE host_name = @app), " +
            "(SELECT COUNT(*) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id WHERE s.host_name = @app), " +
            "(SELECT COUNT(*) FROM sys.dm_tran_session_transactions AS t JOIN sys.dm_exec_sessions AS s ON s.session_id = t.session_id WHERE s.host_name = @app), " +
            "(SELECT COUNT(*) FROM sys.dm_tran_locks AS l JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id " +
            "WHERE s.host_name = @app AND l.resource_type <> N'DATABASE')",
            admin);
        command.Parameters.AddWithValue("@app", marker);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }
}

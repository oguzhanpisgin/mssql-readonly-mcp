using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Security findings T-1, T-2 and T-3: a call that has not ended at its time limit is audited and answered at the limit
/// plus <see cref="ToolService.Grace"/>, whatever SQL Server or the network is doing; its clean-up goes on in the
/// background. Every test here releases what it holds and waits until no abandoned clean-up is left before it ends:
/// the container is shared.
/// </summary>
public sealed partial class StdioServerTests
{
    // The query of finding T-1 ("heavyRows" in the retest harness): about one row every 2 s, each row filling a TDS
    // packet, so the cancel of a read arrives while SQL Server is busy computing the next row.
    private const string HeavyRowsSql =
        "SELECT n.x, REPLICATE(CONVERT(varchar(max), 'r'), 4100) AS pad, (SELECT COUNT_BIG(*) FROM sys.all_objects a " +
        "CROSS JOIN sys.all_objects b WHERE CHECKSUM(a.name, b.name) = n.x) AS c FROM (SELECT TOP (60) ROW_NUMBER() OVER " +
        "(ORDER BY (SELECT 1)) AS x FROM sys.all_objects) n";

    private const string LockedRowSql = "SELECT V FROM dbo.LockLab WHERE Id = 2";

    /// <summary>The longest wait for the background clean-up of an abandoned call to end once nothing holds it.</summary>
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Frozen_established_connection_is_answered_as_Timeout_at_the_limit_and_cleaned_up_after_release()
    {
        // The discriminating case: limit 2 s, a freeze of 20 s. Before this change the answer came 7.0 s after the
        // call started or only when the freeze was released (finding T-2).
        const int LimitSeconds = 2;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        var auditPath = fixture.NewAuditPath();
        await using var server = RawStdioServer.Start(
            ServerDll,
            ThroughProxy(proxy, fixture.ReaderConnectionString, marker),
            auditPath,
            (ServerConfig.TimeoutVariable, LimitSeconds.ToString(CultureInfo.InvariantCulture)));
        await server.InitializeAsync();

        var watch = Stopwatch.StartNew();
        server.Send(2, "tools/call", new { name = "run_query", arguments = new { sql = CallLimitTests.SlowRowsSql } });
        await WaitUntilAsync(async () => await RunningRequestsAsync(admin, marker) > 0, TimeSpan.FromSeconds(LimitSeconds), "the call never reached SQL Server");
        var frozen = proxy.FreezeEstablished();
        var frozenAt = watch.Elapsed;
        var passed = false;
        try
        {
            var answer = await server.ReadResponseAsync(2);
            var answeredAt = watch.Elapsed;
            var linesAtAnswer = TestText.AuditLines(auditPath);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"frozen {frozen} connection(s) at {frozenAt.TotalMilliseconds:F0} ms; answer at {answeredAt.TotalMilliseconds:F0} ms; " +
                $"audit duration_ms {linesAtAnswer[^1].GetProperty("duration_ms").GetInt64()}"));

            Assert.Equal(1, frozen);
            Assert.Equal((true, ToolErrors.CallTimeout.Message), ToolAnswer(answer));
            Assert.True(answeredAt <= ClientBound(LimitSeconds), $"answered after {answeredAt}");
            Assert.Equal(["startup:allowed:Allowed", "run_query:error:Timeout"], linesAtAnswer.Select(Key));
            AssertTimeoutLine(linesAtAnswer[^1], LimitSeconds);

            // While the freeze lasts, a call on a fresh connection still answers.
            var describe = await server.CallAsync(3, "describe_table", new { table = "dbo.Orders" });
            Assert.False(ToolAnswer(describe).IsError, describe.GetRawText());

            var holdLeft = TimeSpan.FromSeconds(20) - (watch.Elapsed - frozenAt);
            if (holdLeft > TimeSpan.Zero)
            {
                await Task.Delay(holdLeft);
            }

            Assert.Equal(3, TestText.AuditLines(auditPath).Count);
            passed = true;
        }
        finally
        {
            proxy.ReleaseAll();
            if (!passed)
            {
                await SettleSessionsAsync(admin, marker);
            }
        }

        // After the release the abandoned clean-up ends (no session of the server is left: no pooling), and it writes
        // no second line.
        var cleanup = await WaitUntilAsync(async () => await SessionsAsync(admin, marker) == 0, CleanupWait, "the abandoned clean-up never ended");
        output.WriteLine($"clean-up ended {cleanup.TotalMilliseconds:F0} ms after the release");
        Assert.Equal(3, TestText.AuditLines(auditPath).Count);

        var next = await server.CallAsync(4, "run_query", new { sql = "SELECT 1 AS one" });
        Assert.False(ToolAnswer(next).IsError, next.GetRawText());

        var run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
        AssertOnlyCallLogLines(run.Stderr);
        Assert.Equal(
            ["startup:allowed:Allowed", "run_query:error:Timeout", "describe_table:allowed:Allowed", "run_query:allowed:Allowed"],
            TestText.AuditLines(auditPath).Select(Key));
        var verify = await RunServerAsync(["verify-audit", auditPath], null, null);
        Assert.True(verify.ExitCode == 0, string.Join(" | ", verify.Stdout.Concat(verify.Stderr)));
        AssertNoSecrets(new ProcessRun(run.ExitCode, run.Stdout, run.Stderr), auditPath);
    }

    [Fact]
    public async Task Process_exits_0_at_end_of_input_while_a_frozen_connection_is_still_held()
    {
        const int LimitSeconds = 2;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        var auditPath = fixture.NewAuditPath();
        await using var server = RawStdioServer.Start(
            ServerDll,
            ThroughProxy(proxy, fixture.ReaderConnectionString, marker),
            auditPath,
            (ServerConfig.TimeoutVariable, LimitSeconds.ToString(CultureInfo.InvariantCulture)));
        await server.InitializeAsync();

        (int ExitCode, string[] Stdout, string[] Stderr) run;
        TimeSpan exitAfter;
        var passed = false;
        try
        {
            server.Send(2, "tools/call", new { name = "run_query", arguments = new { sql = CallLimitTests.SlowRowsSql } });
            await WaitUntilAsync(async () => await RunningRequestsAsync(admin, marker) > 0, TimeSpan.FromSeconds(LimitSeconds), "the call never reached SQL Server");
            Assert.Equal(1, proxy.FreezeEstablished());
            Assert.Equal((true, ToolErrors.CallTimeout.Message), ToolAnswer(await server.ReadResponseAsync(2)));

            // End of input with the abandoned clean-up still frozen: it does not hold the process, and the exit is 0
            // well inside the drain window (limit + 10 s), not the drain's exit 6.
            var watch = Stopwatch.StartNew();
            run = await server.CloseInputAndWaitAsync(TimeSpan.FromSeconds(60));
            exitAfter = watch.Elapsed;
            Assert.True(await SessionsAsync(admin, marker) > 0, "the frozen session was gone before the release");
            passed = true;
        }
        finally
        {
            proxy.ReleaseAll();
            if (!passed)
            {
                await SettleSessionsAsync(admin, marker);
            }
        }

        output.WriteLine($"exit {run.ExitCode} {exitAfter.TotalMilliseconds:F0} ms after end of input, freeze held");
        Assert.True(run.ExitCode == ExitCodes.Ok, $"exit {run.ExitCode}; stderr: {string.Join(" | ", run.Stderr)}");
        Assert.True(exitAfter < TimeSpan.FromSeconds(LimitSeconds + 10), $"the process ended {exitAfter} after end of input");
        AssertOnlyCallLogLines(run.Stderr);
        Assert.Equal(["startup:allowed:Allowed", "run_query:error:Timeout"], TestText.AuditLines(auditPath).Select(Key));
        var verify = await RunServerAsync(["verify-audit", auditPath], null, null);
        Assert.True(verify.ExitCode == 0, string.Join(" | ", verify.Stdout.Concat(verify.Stderr)));

        // The process is gone; once the proxy forwards the close, SQL Server ends the session and rolls it back.
        await WaitUntilAsync(async () => await SessionsAsync(admin, marker) == 0, CleanupWait, "the session of the ended process stayed");
    }

    [Fact]
    public async Task Healthy_link_heavy_rows_answer_Timeout_at_the_limit_three_times_and_the_server_is_clean_afterwards()
    {
        // Finding T-1 (limit 5 s: 8.2 to 8.5 s on SQL Server 2025 before this change). Not discriminating on every
        // engine: the old code was on time in some runs.
        const int LimitSeconds = 5;
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString, Limits(LimitSeconds));
        await using var admin = await OpenAdminAsync();

        var passed = false;
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                var watch = Stopwatch.StartNew();
                var outcome = await harness.RunQueryAsync(HeavyRowsSql);
                watch.Stop();
                var line = harness.AuditLines()[^1];
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"heavy rows call {i}: {watch.ElapsedMilliseconds} ms, duration_ms {line.GetProperty("duration_ms").GetInt64()}, " +
                    $"abandoned clean-ups {harness.Service.AbandonedCleanups}; {outcome.Error ?? "(no error)"}"));

                Assert.Equal(ToolErrors.CallTimeout.Message, outcome.Error);
                Assert.True(watch.Elapsed <= ClientBound(LimitSeconds), $"call {i} answered after {watch.Elapsed}");
                AssertTimeoutLine(line, LimitSeconds);
                Assert.Equal(1 + i, harness.AuditLines().Count);
            }

            passed = true;
        }
        finally
        {
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        // The clean-up did end: no running request and no open transaction of the reader within 15 s.
        var clean = await WaitUntilAsync(
            async () => harness.Service.AbandonedCleanups == 0 && await ReaderRequestsAsync(admin) == 0 && await ReaderOpenTransactionsAsync(admin) == 0,
            TimeSpan.FromSeconds(15),
            "the reader still had a request or an open transaction 15 s after the last answer");
        output.WriteLine($"server clean {clean.TotalMilliseconds:F0} ms after the last answer");
        Assert.Equal(4, harness.AuditLines().Count);
    }

    [Fact]
    public async Task Call_whose_rollback_is_held_past_the_limit_answers_Timeout_and_returns_no_rows()
    {
        // Finding T-3: before this change the call returned its rows when the rollback ended, about 20 s later.
        const int LimitSeconds = 2;
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString, Limits(LimitSeconds));
        await using var admin = await OpenAdminAsync();
        using var hold = new ManualResetEventSlim();
        harness.Service.BeforeRollback = () => hold.Wait(CleanupWait);
        var passed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var outcome = await harness.RunQueryAsync("SELECT TOP (2) Name FROM dbo.Customers ORDER BY CustomerId");
            watch.Stop();
            harness.Service.BeforeRollback = null;
            output.WriteLine($"rollback held: answer after {watch.ElapsedMilliseconds} ms");

            Assert.Equal(ToolErrors.CallTimeout.Message, outcome.Error);
            Assert.True(watch.Elapsed <= ClientBound(LimitSeconds), $"answered after {watch.Elapsed}");
            AssertTimeoutLine(harness.AuditLines()[^1], LimitSeconds);
            Assert.Equal(1, harness.Service.AbandonedCleanups);

            // What SQL Server is doing meanwhile: the reader's transaction is still open.
            Assert.True(await ReaderOpenTransactionsAsync(admin) > 0, "no open transaction while the rollback was held");
            passed = true;
        }
        finally
        {
            harness.Service.BeforeRollback = null;
            hold.Set();
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        await WaitForCleanupsAsync(harness);
        await WaitUntilAsync(async () => await ReaderOpenTransactionsAsync(admin) == 0, TimeSpan.FromSeconds(15), "the transaction stayed open");
        var next = await harness.RunQueryAsync("SELECT 1 AS one");
        Assert.False(next.IsError, next.Error);
        Assert.Equal(["startup:allowed:Allowed", "run_query:error:Timeout", "run_query:allowed:Allowed"], harness.AuditLines().Select(Key));
    }

    [Fact]
    public async Task Call_whose_transaction_start_is_held_past_the_limit_answers_Timeout_and_never_sends_its_query()
    {
        const int LimitSeconds = 2;
        var alias = "w_" + Guid.NewGuid().ToString("N");
        var sql = $"SELECT TOP (1) Name AS {alias} FROM dbo.Customers";
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString, Limits(LimitSeconds));
        await using var admin = await OpenAdminAsync();
        using var hold = new ManualResetEventSlim();
        var commandsBefore = harness.Gateway.CommandsCreated;
        harness.Service.AfterTransactionStart = () => hold.Wait(CleanupWait);
        var passed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var outcome = await harness.RunQueryAsync(sql);
            watch.Stop();
            harness.Service.AfterTransactionStart = null;
            output.WriteLine($"transaction start held: answer after {watch.ElapsedMilliseconds} ms");

            // The synchronous step being held does not hold the answer.
            Assert.Equal(ToolErrors.CallTimeout.Message, outcome.Error);
            Assert.True(watch.Elapsed <= ClientBound(LimitSeconds), $"answered after {watch.Elapsed}");
            AssertTimeoutLine(harness.AuditLines()[^1], LimitSeconds);
            Assert.Equal(1, harness.Service.AbandonedCleanups);
            passed = true;
        }
        finally
        {
            harness.Service.AfterTransactionStart = null;
            hold.Set();
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        // Released past the deadline, the body sends nothing more: the session settings were its only command.
        await WaitForCleanupsAsync(harness);
        Assert.Equal(commandsBefore + 1, harness.Gateway.CommandsCreated);
        var pattern = "%" + alias + "%";
        var watchServer = Stopwatch.StartNew();
        while (watchServer.Elapsed < TimeSpan.FromSeconds(2))
        {
            Assert.Equal(0, await ScalarAsync(
                admin,
                "SELECT COUNT(*) FROM sys.dm_exec_requests AS r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t " +
                "WHERE r.session_id <> @@SPID AND t.text LIKE @pattern",
                ("@pattern", pattern)));
            await Task.Delay(100);
        }

        Assert.Equal(0, await ScalarAsync(
            admin,
            "SELECT COUNT(*) FROM sys.dm_exec_query_stats AS q CROSS APPLY sys.dm_exec_sql_text(q.sql_handle) AS t WHERE t.text LIKE @pattern",
            ("@pattern", pattern)));
        var next = await harness.RunQueryAsync("SELECT 1 AS one");
        Assert.False(next.IsError, next.Error);
    }

    [Fact]
    public async Task Answers_stay_on_time_with_more_held_synchronous_steps_than_processors()
    {
        // (processors + 2) calls, each held in its synchronous transaction start (the seam runs on the thread that
        // step runs on) until well past its limit. Meanwhile the thread pool is capped at that same count: were the
        // synchronous steps run on pool threads, the held calls would take every pool thread, the cutoff timers could
        // not run, and no call would be answered before the hold ends (60 s). On their own threads they take none.
        const int LimitSeconds = 5;
        var count = Environment.ProcessorCount + 2;
        await using var locker = await HoldLockedRowAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString, Limits(LimitSeconds, lockTimeoutMs: 60000));
        using var hold = new ManualResetEventSlim();
        var held = 0;
        harness.Service.AfterTransactionStart = () =>
        {
            if (Interlocked.Increment(ref held) <= count)
            {
                hold.Wait(CleanupWait);
            }
        };
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxCompletionPorts);
        var passed = false;
        try
        {
            Assert.True(ThreadPool.SetMaxThreads(count, maxCompletionPorts), "the thread pool refused the cap");
            var calls = Enumerable.Range(0, count).Select(_ => TimedAsync(() => harness.RunQueryAsync("SELECT 1 AS one"))).ToArray();
            var answers = await Task.WhenAll(calls);
            var slowest = answers.Max(a => a.Elapsed);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{count} calls held in a synchronous step (processors {Environment.ProcessorCount}, pool cap {count}): answers " +
                $"{answers.Min(a => a.Elapsed).TotalMilliseconds:F0} to {slowest.TotalMilliseconds:F0} ms; held {Volatile.Read(ref held)}; " +
                $"abandoned clean-ups {harness.Service.AbandonedCleanups}; pool threads {ThreadPool.ThreadCount}, pending work items {ThreadPool.PendingWorkItemCount}"));

            // Every call reached its held step, and every one was answered at its limit while still held.
            Assert.Equal(count, Volatile.Read(ref held));
            Assert.All(answers, a => Assert.Equal(ToolErrors.CallTimeout.Message, a.Outcome.Error));
            Assert.True(slowest <= ClientBound(LimitSeconds), $"the slowest of the held calls answered after {slowest}");
            Assert.Equal(count, harness.Service.AbandonedCleanups);

            // The next calls, while those are still held: one that reaches its limit (the administrator's row lock),
            // then one that does not.
            var (nextTimeout, nextElapsed) = await TimedAsync(() => harness.RunQueryAsync(LockedRowSql));
            var (quick, quickElapsed) = await TimedAsync(() => harness.RunQueryAsync("SELECT 1 AS one"));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"next call at its limit: {nextElapsed.TotalMilliseconds:F0} ms; next quick call: {quickElapsed.TotalMilliseconds:F0} ms; " +
                $"abandoned clean-ups {harness.Service.AbandonedCleanups}; pool threads {ThreadPool.ThreadCount}"));
            Assert.Equal(ToolErrors.CallTimeout.Message, nextTimeout.Error);
            Assert.True(nextElapsed <= ClientBound(LimitSeconds), $"the next call answered after {nextElapsed}");
            AssertTimeoutLine(harness.AuditLines()[^2], LimitSeconds);
            Assert.False(quick.IsError, quick.Error);
            Assert.Equal(1 + count + 2, harness.AuditLines().Count);
            passed = true;
        }
        finally
        {
            harness.Service.AfterTransactionStart = null;
            hold.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxCompletionPorts);
            await locker.DisposeAsync();
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        await WaitForCleanupsAsync(harness);
    }

    [Fact]
    public async Task Client_cancel_during_a_frozen_connection_is_audited_as_Cancelled_within_the_grace()
    {
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.SalesReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        await using var locker = await HoldLockedRowAsync();
        using var harness = await Harness.StartAsync(
            fixture, ThroughProxy(proxy, fixture.SalesReaderConnectionString, marker), Limits(60, lockTimeoutMs: 60000));
        using var cancel = new CancellationTokenSource();
        var passed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var call = Assert.ThrowsAsync<McpException>(() => harness.Service.RunQueryAsync(LockedRowSql, null, cancel.Token));
            await WaitUntilAsync(async () => await BlockedRequestsAsync(admin, marker) == 1, TimeSpan.FromSeconds(20), "the call never reached the lock");
            Assert.Equal(1, proxy.FreezeEstablished());
            var cancelledAt = watch.Elapsed;
            await cancel.CancelAsync();
            var error = await call;
            var answeredAt = watch.Elapsed;
            var line = harness.AuditLines()[^1];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"cancel at {cancelledAt.TotalMilliseconds:F0} ms, answer at {answeredAt.TotalMilliseconds:F0} ms, duration_ms {line.GetProperty("duration_ms").GetInt64()}"));

            Assert.Equal("Cancelled: the call was cancelled.", error.Message);
            Assert.Equal("Cancelled", line.GetProperty("reason").GetString());
            Assert.True(answeredAt - cancelledAt <= ToolService.Grace + TimeSpan.FromSeconds(2), $"answered {answeredAt - cancelledAt} after the cancel");
            Assert.True(
                line.GetProperty("duration_ms").GetInt64() <= (cancelledAt + ToolService.Grace + TimeSpan.FromSeconds(1)).TotalMilliseconds,
                "the Cancelled line was written later than the grace after the cancel");
            Assert.Equal(2, harness.AuditLines().Count);
            passed = true;
        }
        finally
        {
            proxy.ReleaseAll();
            await locker.DisposeAsync();
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        await WaitForCleanupsAsync(harness);
        Assert.Equal(2, harness.AuditLines().Count);
    }

    [Fact]
    public async Task Throttled_link_answers_Timeout_at_the_limit_plus_grace()
    {
        // Finding T-2: 20 KB/s and 200,000 characters at limit 2 s answered 21 to 23 s past the limit.
        const int LimitSeconds = 2;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        using var harness = await Harness.StartAsync(fixture, ThroughProxy(proxy, fixture.ReaderConnectionString, marker), Limits(LimitSeconds));
        await using var admin = await OpenAdminAsync();
        proxy.ServerBytesPerSecond = 20_000;
        var passed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var outcome = await harness.RunQueryAsync("SELECT REPLICATE(CAST('x' AS varchar(max)), 200000) AS v");
            watch.Stop();
            output.WriteLine($"throttled 20 KB/s: answer after {watch.ElapsedMilliseconds} ms; {outcome.Error ?? "(no error)"}");

            Assert.Equal(ToolErrors.CallTimeout.Message, outcome.Error);
            Assert.True(watch.Elapsed <= ClientBound(LimitSeconds), $"answered after {watch.Elapsed}");
            AssertTimeoutLine(harness.AuditLines()[^1], LimitSeconds);
            passed = true;
        }
        finally
        {
            proxy.ServerBytesPerSecond = 0;
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        await WaitForCleanupsAsync(harness);
        var next = await harness.RunQueryAsync("SELECT 1 AS one");
        Assert.False(next.IsError, next.Error);
        await WaitUntilAsync(async () => await SessionsAsync(admin, marker) == 0, CleanupWait, "sessions of the throttled calls stayed");
    }

    [Fact]
    public async Task Schema_calls_on_a_frozen_connection_answer_Timeout_at_the_limit_plus_grace()
    {
        // Invariant 9: list_tables and describe_table run through the same race. Each call waits for the
        // administrator's uncommitted DDL on dbo.Orders, then its connection is frozen.
        const int LimitSeconds = 3;
        var marker = NewMarker();
        await using var proxy = StallingProxy.Start(fixture.ReaderConnectionString);
        await using var admin = await OpenAdminAsync();
        using var harness = await Harness.StartAsync(
            fixture, ThroughProxy(proxy, fixture.ReaderConnectionString, marker), Limits(LimitSeconds, lockTimeoutMs: 60000));
        await using var ddl = await HoldSchemaLockAsync();
        var passed = false;
        try
        {
            foreach (var tool in new[] { "describe_table", "list_tables" })
            {
                var call = TimedAsync(() => tool == "describe_table" ? harness.DescribeTableAsync("dbo.Orders") : harness.ListTablesAsync());
                await WaitUntilAsync(async () => await BlockedRequestsAsync(admin, marker) == 1, TimeSpan.FromSeconds(LimitSeconds - 1), $"{tool} never waited for the lock");
                Assert.Equal(1, proxy.FreezeEstablished());
                var (outcome, elapsed) = await call;
                var line = harness.AuditLines()[^1];
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tool} frozen: answer after {elapsed.TotalMilliseconds:F0} ms, duration_ms {line.GetProperty("duration_ms").GetInt64()}"));

                Assert.Equal(ToolErrors.CallTimeout.Message, outcome.Error);
                Assert.True(elapsed <= ClientBound(LimitSeconds), $"{tool} answered after {elapsed}");
                Assert.Equal($"{tool}:error:Timeout", Key(line));
                Assert.Equal(JsonValueKind.Null, line.GetProperty("rows").ValueKind);
                Assert.True(line.GetProperty("duration_ms").GetInt64() <= ServerBound(LimitSeconds).TotalMilliseconds);

                proxy.ReleaseAll();
                await WaitForCleanupsAsync(harness);
                await WaitUntilAsync(async () => await SessionsAsync(admin, marker) == 0, CleanupWait, $"the session of {tool} stayed");
            }

            passed = true;
        }
        finally
        {
            proxy.ReleaseAll();
            await ddl.DisposeAsync();
            if (!passed)
            {
                await SettleAsync(harness);
            }
        }

        await WaitForCleanupsAsync(harness);
        var next = await harness.DescribeTableAsync("dbo.Orders");
        Assert.False(next.IsError, next.Error);
        Assert.Equal(4, harness.AuditLines().Count);
    }

    [Fact]
    public async Task Calls_waiting_for_a_pool_slot_held_by_an_abandoned_clean_up_end_at_their_limit_or_at_the_connect_timeout()
    {
        // Invariant 10 (known limit, no new bound): Max Pool Size 1, its one connection held by an abandoned clean-up
        // (the rollback held by the test seam). A later call waits for a pool slot.
        const int ConnectTimeoutSeconds = 3;
        var connectionString = new SqlConnectionStringBuilder(fixture.ReaderConnectionString)
        {
            MaxPoolSize = 1,
            ConnectTimeout = ConnectTimeoutSeconds,
            WorkstationID = NewMarker(),
        }.ConnectionString;
        using var below = await Harness.StartAsync(fixture, connectionString, Limits(2));
        using var above = await Harness.StartAsync(fixture, connectionString, Limits(10));
        using var hold = new ManualResetEventSlim();
        below.Service.BeforeRollback = () => hold.Wait(CleanupWait);
        var passed = false;
        try
        {
            var first = await below.RunQueryAsync("SELECT 1 AS one");
            below.Service.BeforeRollback = null;
            Assert.Equal(ToolErrors.CallTimeout.Message, first.Error);
            Assert.Equal(1, below.Service.AbandonedCleanups);

            // Limit below the connect timeout: the limit ends the wait.
            var (limited, limitedElapsed) = await TimedAsync(() => below.RunQueryAsync("SELECT 1 AS one"));

            // Limit above the connect timeout: SqlClient gives up waiting for a slot first.
            var (exhausted, exhaustedElapsed) = await TimedAsync(() => above.RunQueryAsync("SELECT 1 AS one"));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"pool exhausted, limit 2 < Connect Timeout {ConnectTimeoutSeconds}: {limitedElapsed.TotalMilliseconds:F0} ms, {limited.Error}; " +
                $"limit 10 > Connect Timeout: {exhaustedElapsed.TotalMilliseconds:F0} ms, {exhausted.Error}"));

            Assert.Equal(ToolErrors.CallTimeout.Message, limited.Error);
            Assert.True(limitedElapsed <= ClientBound(2), $"answered after {limitedElapsed}");
            Assert.Equal("InternalError: the call failed.", exhausted.Error);
            Assert.Equal("InternalError", above.AuditLines()[^1].GetProperty("reason").GetString());
            passed = true;
        }
        finally
        {
            below.Service.BeforeRollback = null;
            hold.Set();
            if (!passed)
            {
                await SettleAsync(below, above);
            }
        }

        await WaitForCleanupsAsync(below);
        await WaitForCleanupsAsync(above);
        var next = await below.RunQueryAsync("SELECT 1 AS one");
        Assert.False(next.IsError, next.Error);
    }

    /// <summary>
    /// The answer bound on the server side (the audit line's duration_ms): limit + grace + 1 s (a loaded machine; the
    /// code's cutoff is limit + grace). Before the change the answer came 7.0 s after a call with limit 2 s.
    /// </summary>
    private static TimeSpan ServerBound(int limitSeconds) =>
        TimeSpan.FromSeconds(limitSeconds) + ToolService.Grace + TimeSpan.FromSeconds(1);

    /// <summary>The answer bound the client measures: limit + grace + 2 s (process start-up and a loaded machine).</summary>
    private static TimeSpan ClientBound(int limitSeconds) =>
        TimeSpan.FromSeconds(limitSeconds) + ToolService.Grace + TimeSpan.FromSeconds(2);

    private static void AssertTimeoutLine(JsonElement line, int limitSeconds)
    {
        Assert.Equal("run_query:error:Timeout", Key(line));
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rows").ValueKind);
        Assert.False(line.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("result_sha256").ValueKind);
        var duration = line.GetProperty("duration_ms").GetInt64();
        Assert.True(duration <= ServerBound(limitSeconds).TotalMilliseconds, $"duration_ms {duration}");
    }

    private static Dictionary<string, string> Limits(int timeoutSeconds, int? lockTimeoutMs = null)
    {
        var variables = new Dictionary<string, string> { [ServerConfig.TimeoutVariable] = timeoutSeconds.ToString(CultureInfo.InvariantCulture) };
        if (lockTimeoutMs is { } lockTimeout)
        {
            variables[ServerConfig.LockTimeoutVariable] = lockTimeout.ToString(CultureInfo.InvariantCulture);
        }

        return variables;
    }

    /// <summary>
    /// A unique workstation name (the session's host_name), so the administrator can find this test's sessions; the server
    /// replaces the application name with its own.
    /// </summary>
    private static string NewMarker() => "w-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The connection string through the proxy, without pooling: every call opens a fresh connection.</summary>
    private static string ThroughProxy(StallingProxy proxy, string connectionString, string marker) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            DataSource = proxy.DataSource,
            Pooling = false,
            WorkstationID = marker,
        }.ConnectionString;

    private static (bool IsError, string? Text) ToolAnswer(JsonElement response)
    {
        var result = response.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static async Task<(CallOutcome Outcome, TimeSpan Elapsed)> TimedAsync(Func<Task<CallOutcome>> call)
    {
        var watch = Stopwatch.StartNew();
        var outcome = await call();
        return (outcome, watch.Elapsed);
    }

    private static async Task WaitForCleanupsAsync(Harness harness) =>
        await WaitUntilAsync(() => Task.FromResult(harness.Service.AbandonedCleanups == 0), CleanupWait, "an abandoned clean-up never ended");

    /// <summary>
    /// The failure path of a test, after it released what it held: waits, at most <see cref="CleanupWait"/> and without
    /// asserting (the test's own failure is the one reported), until no abandoned clean-up of
    /// <paramref name="harnesses"/> is left, so nothing keeps running into the next test on the shared container.
    /// </summary>
    private static async Task SettleAsync(params Harness[] harnesses)
    {
        var watch = Stopwatch.StartNew();
        while (harnesses.Any(h => h.Service.AbandonedCleanups > 0) && watch.Elapsed < CleanupWait)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>The failure path of a stdio test: as <see cref="SettleAsync"/>, until no session of the marker is left.</summary>
    private static async Task SettleSessionsAsync(SqlConnection admin, string marker)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            while (await SessionsAsync(admin, marker) > 0 && watch.Elapsed < CleanupWait)
            {
                await Task.Delay(50);
            }
        }
        catch (SqlException)
        {
            // The administrator's connection itself failed: nothing more to wait for here.
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds; returns how long that took.</summary>
    private static async Task<TimeSpan> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < timeout, $"{message} (waited {watch.Elapsed})");
            await Task.Delay(50);
        }

        return watch.Elapsed;
    }

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var admin = new SqlConnection(fixture.SaConnectionString);
        await admin.OpenAsync();
        return admin;
    }

    /// <summary>The administrator's open transaction holding row 2 of <c>Sales.dbo.LockLab</c>; disposing rolls it back.</summary>
    private async Task<IAsyncDisposable> HoldLockedRowAsync()
    {
        var connection = new SqlConnection(fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase));
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var hold = new SqlCommand("UPDATE dbo.LockLab SET V = V + 1 WHERE Id = 2;", connection, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        return new LockHolder(connection, transaction);
    }

    /// <summary>The administrator's uncommitted DDL on <c>dbo.Orders</c> (a schema lock); disposing rolls it back.</summary>
    private async Task<IAsyncDisposable> HoldSchemaLockAsync()
    {
        var connection = new SqlConnection(fixture.SaConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var hold = new SqlCommand("ALTER TABLE dbo.Orders ADD w_held int NULL;", connection, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        return new LockHolder(connection, transaction);
    }

    private static Task<int> SessionsAsync(SqlConnection admin, string marker) =>
        ScalarAsync(admin, "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE host_name = @app", ("@app", marker));

    private static Task<int> RunningRequestsAsync(SqlConnection admin, string marker) =>
        ScalarAsync(
            admin,
            "SELECT COUNT(*) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id WHERE s.host_name = @app",
            ("@app", marker));

    private static Task<int> BlockedRequestsAsync(SqlConnection admin, string marker) =>
        ScalarAsync(
            admin,
            "SELECT COUNT(*) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id " +
            "WHERE s.host_name = @app AND r.wait_type LIKE N'LCK%'",
            ("@app", marker));

    private static Task<int> ReaderRequestsAsync(SqlConnection admin) =>
        ScalarAsync(
            admin,
            "SELECT COUNT(*) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id WHERE s.login_name = @login",
            ("@login", SqlServerFixture.ReaderLogin));

    private static Task<int> ReaderOpenTransactionsAsync(SqlConnection admin) =>
        ScalarAsync(
            admin,
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name = @login AND open_transaction_count > 0",
            ("@login", SqlServerFixture.ReaderLogin));

    private static async Task<int> ScalarAsync(SqlConnection admin, string sql, params (string Name, string Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, admin);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class LockHolder(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}

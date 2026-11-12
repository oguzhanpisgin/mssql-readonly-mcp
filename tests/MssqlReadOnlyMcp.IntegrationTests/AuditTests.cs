using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MssqlReadOnlyMcp.Audit;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>R-5: one audit line per tool call plus the startup line, a verifying chain, and fail-closed on audit failure.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AuditTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Mixed_calls_give_one_line_each_plus_startup_and_the_file_verifies()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var calls = new List<(string Tool, string Decision, string Reason)>();
        async Task Record(string tool, Task<CallOutcome> call, string decision, string reason)
        {
            await call;
            calls.Add((tool, decision, reason));
        }

        await Record("list_tables", harness.ListTablesAsync(), "allowed", "Allowed");
        await Record("describe_table", harness.DescribeTableAsync("dbo.Orders"), "allowed", "Allowed");
        await Record("describe_table", harness.DescribeTableAsync("dbo.NoSuchTable"), "error", "NotFound");
        await Record("run_query", harness.RunQueryAsync("SELECT TOP (5) Name FROM dbo.Customers"), "allowed", "Allowed");
        await Record("run_query", harness.RunQueryAsync("DELETE FROM dbo.Orders"), "refused", "NotSelect");
        await Record("run_query", harness.RunQueryAsync("SELECT * INTO dbo.Copy FROM dbo.Customers"), "refused", "SelectInto");
        await Record("run_query", harness.RunQueryAsync("SELECT * FROM ArchiveDb.dbo.ArchivedOrders"), "refused", "CrossDatabaseName");
        await Record("run_query", harness.RunQueryAsync("SELECT CAST(N'abc' AS int)"), "error", "SqlError");
        await Record("run_query", harness.RunQueryAsync("SELECT 1 AS one", 0), "error", "InvalidArgument");

        var lines = harness.AuditLines();
        Assert.Equal(calls.Count + 1, lines.Count);
        Assert.Equal("startup", lines[0].GetProperty("tool").GetString());
        Assert.Equal("allowed", lines[0].GetProperty("decision").GetString());
        for (var i = 0; i < calls.Count; i++)
        {
            var line = lines[i + 1];
            Assert.Equal(calls[i].Tool, line.GetProperty("tool").GetString());
            Assert.Equal(calls[i].Decision, line.GetProperty("decision").GetString());
            Assert.Equal(calls[i].Reason, line.GetProperty("reason").GetString());
        }

        Assert.Equal("[\"dbo.Orders\"]", lines[5].GetProperty("tables").GetRawText());
        var verify = AuditVerifier.Verify(harness.AuditPath);
        Assert.True(verify.Ok, verify.Message);
        Assert.Equal(calls.Count + 1, verify.LinesChecked);
        TestText.AssertNoSecrets(fixture, TestText.ReadShared(harness.AuditPath));
    }

    [Fact]
    public async Task Call_rejected_before_a_tool_ran_gets_AuditUnavailable_when_its_line_cannot_be_written()
    {
        var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        // A closed log makes every append throw AuditWriteException.
        harness.Dispose();
        await using var services = new ServiceCollection().AddSingleton(harness.Service).BuildServiceProvider();
        var transport = new StreamServerTransport(Stream.Null, Stream.Null);
        await using var server = McpServer.Create(transport, new McpServerOptions(), null, services);
        var request = new RequestContext<CallToolRequestParams>(
            server, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, new CallToolRequestParams { Name = "describe_table" })
        {
            Services = services,
        };

        // The SDK answers a call it rejects either with an error result or by throwing; no tool runs in either case.
        var answered = await CallToolAuditFilter.Wrap((_, _) => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "An error occurred invoking 'describe_table'." }],
        }))(request, CancellationToken.None);
        var thrown = await CallToolAuditFilter.Wrap((_, _) => throw new ArgumentException("table"))(request, CancellationToken.None);

        foreach (var result in new[] { answered, thrown })
        {
            Assert.True(result.IsError);
            Assert.Equal(ToolErrors.AuditUnavailableMessage, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        Assert.Single(harness.AuditLines());
    }

    [Fact]
    public async Task Audit_failure_returns_an_error_and_no_row_data()
    {
        var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        // A closed log makes every append throw AuditWriteException.
        harness.Dispose();
        var allowed = await harness.RunQueryAsync("SELECT Name, City FROM dbo.Customers WHERE CustomerId = 2");
        var refused = await harness.RunQueryAsync("DELETE FROM dbo.Orders");
        var listed = await harness.ListTablesAsync();

        foreach (var outcome in new[] { allowed, refused, listed })
        {
            Assert.True(outcome.IsError, $"Data was returned while the audit log was unavailable: {outcome.Payload}");
            Assert.Null(outcome.Payload);
            Assert.Equal(ToolErrors.AuditUnavailableMessage, outcome.Error);
        }

        Assert.DoesNotContain("Şule", allowed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Muğla", allowed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Customers", listed.Error, StringComparison.Ordinal);
        Assert.True(harness.Gateway.ConnectionsOpened > connections, "the allowed call ran before its audit line failed");
        Assert.Single(harness.AuditLines());
    }

    [Fact]
    public async Task TooLong_refusal_is_audited_without_the_sql_text()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        var outcome = await harness.RunQueryAsync("SELECT 1 /*" + new string('x', 70_000) + "*/");
        var ordinary = await harness.RunQueryAsync("DELETE FROM dbo.Orders");

        Assert.True(outcome.IsError);
        Assert.StartsWith("TooLong: refused by the read-only gate; ", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        var lines = harness.AuditLines();
        var tooLong = lines[^2];
        Assert.Equal("refused", tooLong.GetProperty("decision").GetString());
        Assert.Equal("TooLong", tooLong.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, tooLong.GetProperty("sql").ValueKind);
        Assert.Equal(JsonValueKind.Null, tooLong.GetProperty("sql_sha256").ValueKind);

        // Any other refusal still records its text.
        Assert.Equal("DELETE FROM dbo.Orders", lines[^1].GetProperty("sql").GetString());
        var verify = AuditVerifier.Verify(harness.AuditPath);
        Assert.True(verify.Ok, verify.Message);
    }

    [Fact]
    public async Task Sql_text_larger_than_an_audit_line_is_refused_as_TooLong_and_still_audited()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        // 17,000,000 characters: more than the audit log's 16 MiB line limit. The text is bounded at the tool
        // boundary, so the call cannot fail for want of an audit line.
        var huge = await harness.RunQueryAsync("SELECT 1 --" + new string('x', 17_000_000));
        var next = await harness.RunQueryAsync("SELECT TOP (2) Name FROM dbo.Customers ORDER BY CustomerId");

        Assert.True(huge.IsError);
        Assert.StartsWith("TooLong: refused by the read-only gate; ", huge.Error, StringComparison.Ordinal);
        Assert.False(next.IsError, next.Error);
        var lines = harness.AuditLines();
        Assert.Equal(3, lines.Count);
        Assert.Equal("refused", lines[1].GetProperty("decision").GetString());
        Assert.Equal("TooLong", lines[1].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("sql").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("sql_sha256").ValueKind);
        Assert.True(harness.Gateway.ConnectionsOpened == connections + 1, "only the second call reached SQL Server");
        var verify = AuditVerifier.Verify(harness.AuditPath);
        Assert.True(verify.Ok, verify.Message);
    }

    [Fact]
    public async Task Text_one_character_over_the_gate_limit_is_not_recorded_and_text_at_the_limit_is()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var atLimit = "SELECT 1 --" + new string('x', ToolService.MaxSqlChars - 11);
        Assert.Equal(ToolService.MaxSqlChars, atLimit.Length);

        await harness.RunQueryAsync(atLimit);
        await harness.RunQueryAsync(atLimit + "x");

        var lines = harness.AuditLines();
        Assert.Equal(atLimit, lines[^2].GetProperty("sql").GetString());
        Assert.Equal("TooLong", lines[^1].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, lines[^1].GetProperty("sql").ValueKind);
    }

    [Fact]
    public async Task Shutdown_drain_waits_for_a_call_in_flight_before_the_log_closes()
    {
        var harness = await Harness.StartAsync(
            fixture,
            fixture.SalesReaderConnectionString,
            new Dictionary<string, string> { [ServerConfig.LockTimeoutVariable] = "60000", [ServerConfig.TimeoutVariable] = "60" });
        await using var admin = new Microsoft.Data.SqlClient.SqlConnection(
            fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase));
        await admin.OpenAsync();
        await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await admin.BeginTransactionAsync();
        await using (var hold = new Microsoft.Data.SqlClient.SqlCommand("UPDATE dbo.LockLab SET V = V + 1 WHERE Id = 1;", admin, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        // The call waits for the admin session's lock; the drain must wait for the call.
        var call = harness.RunQueryAsync("SELECT V FROM dbo.LockLab WHERE Id = 1");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (harness.Service.CallsInFlight == 0)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "the call never started");
            await Task.Delay(20);
        }

        var drain = harness.Service.DrainAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(1000);
        Assert.False(drain.IsCompleted, "the drain finished while a call was still running");

        await transaction.RollbackAsync();
        var outcome = await call;
        Assert.True(await drain);
        harness.Dispose();

        Assert.False(outcome.IsError, outcome.Error);
        Assert.Equal("run_query:allowed:Allowed", string.Join(
            ':',
            new[] { "tool", "decision", "reason" }.Select(f => harness.AuditLines()[^1].GetProperty(f).GetString())));
        Assert.Equal(0, harness.Service.CallsInFlight);

        // A drain that cannot finish reports it instead of waiting for ever.
        using var stuck = harness.Service.TrackCall();
        Assert.False(await harness.Service.DrainAsync(TimeSpan.FromMilliseconds(200)));
    }
}

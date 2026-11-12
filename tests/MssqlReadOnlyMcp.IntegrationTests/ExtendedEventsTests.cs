using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Server-side proof with Extended Events: what the reader login sends, as SQL Server itself records it. The trace is
/// filtered on the login name (see <see cref="SqlServerFixture.TraceSession"/>); the marker connection's own events
/// are the only ones expected when nothing reached the server.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExtendedEventsTests(SqlServerFixture fixture)
{
    private const string ServerApplicationName = "mssql-readonly-mcp";

    [Fact]
    public async Task Positive_control_an_allowed_query_leaves_its_batch_in_the_trace()
    {
        const string sql = "SELECT TOP (3) CustomerId, Name FROM dbo.Customers ORDER BY CustomerId";
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var events = await TraceAsync(async () =>
        {
            var outcome = await harness.RunQueryAsync(sql);
            Assert.False(outcome.IsError, outcome.Error);
        });

        var server = events.Where(e => e.Application == ServerApplicationName).ToList();
        Assert.Contains(server, e => e.Name == "sql_batch_starting" && e.Text == sql);
        Assert.Contains(server, e => e.Name == "sql_batch_starting" && e.Text is not null && e.Text.StartsWith("SET LOCK_TIMEOUT", StringComparison.Ordinal));
        Assert.All(events, e => Assert.Equal(SqlServerFixture.ReaderLogin, e.Principal));

        // The filter records logins too: the marker's unpooled connection logged in.
        Assert.Contains(events, e => e.Name == "login" && e.Application == SqlServerFixture.MarkerApplicationName);
    }

    [Fact]
    public async Task Refused_set_leaves_no_batch_rpc_or_login_event_for_the_reader()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var answered = 0;
        var events = await TraceAsync(async () =>
        {
            foreach (var sql in GateRefusalTests.RefusedStatements)
            {
                if (!(await harness.RunQueryAsync(sql)).IsError)
                {
                    answered++;
                }
            }
        });

        // The server-side evidence first, so a gate that lets text through fails on what SQL Server saw.
        var reached = events.Where(e => e.Application != SqlServerFixture.MarkerApplicationName).ToList();
        Assert.True(
            reached.Count == 0,
            $"{reached.Count} events reached SQL Server for the reader login while the refused set ran: " +
            string.Join(", ", reached.GroupBy(e => $"{e.Name} from {e.Application}").Select(g => $"{g.Count()} x {g.Key}")));
        Assert.True(answered == 0, $"{answered} refused statements returned data.");

        // The trace was live and catching this login: the marker's login and batch are there.
        Assert.Contains(events, e => e.Name == "login" && e.Application == SqlServerFixture.MarkerApplicationName);
        Assert.Contains(events, e => e.Name == "sql_batch_starting" && e.Application == SqlServerFixture.MarkerApplicationName);
    }

    [Fact]
    public async Task Write_sent_past_the_gate_is_in_the_trace_and_refused_by_the_server()
    {
        const string sql = "UPDATE dbo.Customers SET Name = N'Değişti' WHERE CustomerId = 1";
        var before = await fixture.FingerprintAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        SqlException? refusal = null;
        var events = await TraceAsync(async () =>
        {
            // Test-only path: what run_query calls after the gate allows a statement.
            refusal = await Assert.ThrowsAsync<SqlException>(() => harness.Service.ExecuteReadAsync(sql, 10, CancellationToken.None));
        });

        Assert.Equal(229, refusal!.Number);
        Assert.Contains(events, e => e.Name == "sql_batch_starting" && e.Application == ServerApplicationName && e.Text == sql);
        Assert.Equal(before, await fixture.FingerprintAsync());
    }

    private async Task<IReadOnlyList<TraceEvent>> TraceAsync(Func<Task> action)
    {
        await fixture.StartTraceAsync();
        try
        {
            await action();
            return await fixture.ReadTraceAsync();
        }
        finally
        {
            await fixture.StopTraceAsync();
        }
    }
}

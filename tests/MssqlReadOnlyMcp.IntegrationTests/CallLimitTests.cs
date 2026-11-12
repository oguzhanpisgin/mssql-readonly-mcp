using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Code review C-1 and C-2: the time limit holds for the whole call, not for each network read, and the character
/// cap stops reading a row as soon as what was read of it can no longer fit.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CallLimitTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    // One row about every 0.4 s on the test container (measured): a correlated count over 600 x 600 x 16 order-line
    // combinations that no rewrite can share between rows, next to a 9,000-byte binary value, so every row fills a
    // TDS packet and reaches the client on its own. Each read waits far less than the 2 s limit; all 300 orders take
    // far more (30 rows already took 13.9 s before the call had one time limit).
    internal const string SlowRowsSql =
        "SELECT TOP (300) o.OrderId, CAST(REPLICATE(CAST(0x00 AS varbinary(max)), 9000) AS varbinary(max)) AS b, " +
        "(SELECT COUNT_BIG(*) FROM dbo.OrderLines AS a CROSS JOIN dbo.OrderLines AS b2 " +
        "CROSS JOIN (SELECT TOP (16) OrderId, LineNumber FROM dbo.OrderLines ORDER BY OrderId, LineNumber) AS c " +
        "WHERE ((a.OrderId * 7 + a.LineNumber) * (b2.OrderId * 3 + b2.LineNumber) + c.OrderId * c.LineNumber) % 1009 <> o.OrderId) AS n " +
        "FROM dbo.Orders AS o ORDER BY o.OrderId";

    private const int TimeoutSeconds = 2;

    [Fact]
    public async Task Rows_that_arrive_slowly_end_at_the_time_limit_of_the_call_as_a_timeout()
    {
        using var harness = await Harness.StartAsync(
            fixture,
            fixture.ReaderConnectionString,
            new Dictionary<string, string> { [ServerConfig.TimeoutVariable] = TimeoutSeconds.ToString(CultureInfo.InvariantCulture) });

        var watch = Stopwatch.StartNew();
        var outcome = await harness.RunQueryAsync(SlowRowsSql);
        watch.Stop();
        output.WriteLine($"call {watch.ElapsedMilliseconds} ms; error: {outcome.Error ?? "(none)"}");

        // What a timeout gives: the reason word and the client library's timeout number, no data.
        Assert.True(outcome.IsError, $"no timeout after {watch.Elapsed}; rows returned");
        Assert.Equal("Timeout: SQL Server error -2, class 11, line 0", outcome.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(TimeoutSeconds + 3), $"took {watch.Elapsed}");

        var line = harness.AuditLines()[^1];
        Assert.Equal("error", line.GetProperty("decision").GetString());
        Assert.Equal("Timeout", line.GetProperty("reason").GetString());
        Assert.Equal(SlowRowsSql, line.GetProperty("sql").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, line.GetProperty("rows").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, line.GetProperty("result_sha256").ValueKind);

        // The session ended cleanly: the next call on the same server works.
        var next = await harness.RunQueryAsync("SELECT 1 AS one");
        Assert.False(next.IsError, next.Error);
    }

    [Fact]
    public async Task Client_cancellation_of_a_call_whose_rows_arrive_slowly_keeps_its_answer()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<McpException>(() => harness.Service.RunQueryAsync(SlowRowsSql, null, cancel.Token));
        watch.Stop();
        output.WriteLine($"call {watch.ElapsedMilliseconds} ms; error: {error.Message}");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal("Cancelled: the call was cancelled.", error.Message);
        Assert.Equal("Cancelled", harness.AuditLines()[^1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_row_of_many_large_cells_stops_being_read_once_it_cannot_fit_the_character_cap()
    {
        // 64 values of 1,000,000 characters each (under the 1 MiB long-value stop): 64 MB on the wire if every cell is
        // read. Cut to 2,000 characters each, the row needs about 130,000 characters; the default cap is 40,000.
        const int Cells = 64;
        const int CellChars = 1_000_000;
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"WITH a AS (SELECT REPLICATE(CAST('x' AS varchar(max)), {CellChars}) AS v) SELECT {string.Join(", ", Enumerable.Repeat("v", Cells))} FROM a");

        var receivedBefore = harness.Gateway.BytesReceived;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var outcome = await harness.RunQueryAsync(sql);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var received = harness.Gateway.BytesReceived - receivedBefore;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Cells} cells of {CellChars} chars; received {received} bytes; allocated {allocated} bytes; call {watch.ElapsedMilliseconds} ms"));

        // The same answer as when the whole row was read first: no row fits, cut by the character cap.
        Assert.False(outcome.IsError, outcome.Error);
        var json = outcome.Json;
        Assert.Equal(0, json.GetProperty("row_count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(ResultPayload.ReasonMaxChars, json.GetProperty("truncated_reason").GetString());
        Assert.Equal("allowed", harness.AuditLines()[^1].GetProperty("decision").GetString());

        // About 20 cut cells fill the room; the rest of the row is neither read into memory nor serialised. Measured:
        // 12.8 MB allocated when the whole row was read and its JSON built, 3.7 MB with the early stop.
        Assert.True(allocated < MaxBytesAllocated, $"{allocated} bytes allocated during the call");

        // The stated limit, as for one long value (LargeValueTests): each cell read is read to its end to learn its
        // length (up to the 1 MiB long-value stop), and after the command is cancelled closing the reader still reads
        // the rest of the row off the network up to SQL Server's acknowledgement of the cancel, so the whole row
        // crosses the network and is discarded as it arrives. If this fails, the limit is gone and the docs sentence
        // must change.
        Assert.True(received >= (long)Cells * CellChars, $"{received} bytes received");
    }

    // Far below the 12.8 MB the call allocated when it read and serialised the whole row, above the 3.7 MB it
    // allocates now.
    private const long MaxBytesAllocated = 8L * 1024 * 1024;
}

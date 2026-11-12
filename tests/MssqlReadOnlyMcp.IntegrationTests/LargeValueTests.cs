using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// D-12: one very large cell. The gate allows <c>SELECT REPLICATE(CAST('x' AS varchar(max)), N)</c>; the server process
/// cuts the cell and never holds the value in memory. The rest of the value still crosses the network (a stated limit).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LargeValueTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    // 200,000,000 one-byte characters: 200 MB on the wire if the value were transferred whole. Large enough that a
    // drained value stands out from any packet buffering by two orders of magnitude, small enough for the test
    // container (SQL Server builds the value in tempdb in a few seconds).
    private const long ValueChars = 200_000_000;

    // A bound far below the value's size: what is held is the cut, the 1 MiB scan buffer and packet buffers.
    private const long MaxBytesAllocated = 64L * 1024 * 1024;

    [Fact]
    public async Task One_very_large_cell_is_cut_and_never_held_but_the_rest_is_still_transferred()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var sql = string.Create(CultureInfo.InvariantCulture, $"SELECT REPLICATE(CAST('x' AS varchar(max)), {ValueChars}) AS big");

        var receivedBefore = harness.Gateway.BytesReceived;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var outcome = await harness.RunQueryAsync(sql);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var received = harness.Gateway.BytesReceived - receivedBefore;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"value {ValueChars} chars; received {received} bytes; allocated {allocated} bytes; call {watch.ElapsedMilliseconds} ms"));

        Assert.False(outcome.IsError, outcome.Error);

        // (c) The stated limit: the command is cancelled before the reader moves on, yet closing the reader still
        // reads the rest of the value up to SQL Server's acknowledgement of the cancel, and the client library offers
        // no way to drop the connection instead. The whole value crosses the network (it is discarded as it arrives).
        // If this assertion ever fails, the limit is gone and the docs/reference.md and SECURITY.md sentence must
        // change.
        Assert.True(received >= ValueChars, $"{received} bytes received for a {ValueChars}-character value");

        // (b) The value was never held whole.
        Assert.True(allocated < MaxBytesAllocated, $"{allocated} bytes allocated during the call");

        // (a) The cell is cut at MaxCellChars plus the marker, and the result says why it stopped.
        var json = outcome.Json;
        Assert.Equal(new string('x', ResultPayload.MaxCellChars) + ResultPayload.CellCutMarker, json.GetProperty("rows")[0][0].GetString());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(ResultPayload.ReasonLongValue, json.GetProperty("truncated_reason").GetString());
        Assert.Equal(1, json.GetProperty("row_count").GetInt32());
    }
}

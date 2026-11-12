using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>R-4: row cap, character cap, cell cap, command timeout and lock timeout.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class CapsTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Row_cap_truncates_with_reason_max_rows()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var outcome = await harness.RunQueryAsync("SELECT OrderId, LineNumber, Product FROM dbo.OrderLines ORDER BY OrderId, LineNumber", 50);

        var json = outcome.Json;
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_rows", json.GetProperty("truncated_reason").GetString());
        Assert.Equal(50, json.GetProperty("row_count").GetInt32());
        Assert.Equal(50, json.GetProperty("rows").GetArrayLength());
        Assert.True(outcome.Payload!.Length <= harness.Config.MaxChars);
        Assert.Equal(1, json.GetProperty("rows")[0][0].GetInt32());

        var line = harness.AuditLines()[^1];
        Assert.Equal(50, line.GetProperty("rows").GetInt32());
        Assert.True(line.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Server_row_cap_applies_when_the_requested_limit_is_higher()
    {
        using var harness = await Harness.StartAsync(
            fixture, fixture.ReaderConnectionString, new Dictionary<string, string> { [ServerConfig.MaxRowsVariable] = "25" });

        var json = (await harness.RunQueryAsync("SELECT OrderId FROM dbo.Orders", 1000)).Json;

        Assert.Equal(25, json.GetProperty("row_count").GetInt32());
        Assert.Equal("max_rows", json.GetProperty("truncated_reason").GetString());
    }

    [Fact]
    public async Task Exact_row_count_is_not_truncated()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var json = (await harness.RunQueryAsync("SELECT CustomerId, City FROM dbo.Customers WHERE CustomerId <= 10", 10)).Json;

        Assert.Equal(10, json.GetProperty("row_count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("truncated_reason").ValueKind);
    }

    [Fact]
    public async Task Character_cap_truncates_with_reason_max_chars()
    {
        using var harness = await Harness.StartAsync(
            fixture, fixture.ReaderConnectionString, new Dictionary<string, string> { [ServerConfig.MaxCharsVariable] = "3000" });

        var outcome = await harness.RunQueryAsync("SELECT CustomerId, Name, City FROM dbo.Customers ORDER BY CustomerId");

        var json = outcome.Json;
        Assert.True(outcome.Payload!.Length <= 3000, $"payload length {outcome.Payload.Length}");
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_chars", json.GetProperty("truncated_reason").GetString());
        var rows = json.GetProperty("row_count").GetInt32();
        Assert.InRange(rows, 1, 119);
        Assert.Equal(rows, json.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Long_cells_are_cut_and_binary_values_are_summarised()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var json = (await harness.RunQueryAsync("SELECT Notes, Photo, Name FROM dbo.Customers WHERE CustomerId = 1")).Json;

        var row = json.GetProperty("rows")[0];
        var notes = row[0].GetString()!;
        Assert.Equal(ResultPayload.MaxCellChars + ResultPayload.CellCutMarker.Length, notes.Length);
        Assert.EndsWith(ResultPayload.CellCutMarker, notes, StringComparison.Ordinal);
        Assert.StartsWith("ğüşiöç İĞÜŞÖÇ", notes, StringComparison.Ordinal);
        Assert.Equal("<binary 5000 bytes>", row[1].GetString());
        Assert.Contains("İsmail", row[2].GetString(), StringComparison.Ordinal);
        Assert.Equal("nvarchar", json.GetProperty("columns")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Heavy_query_under_a_one_second_timeout_returns_the_timeout_reason()
    {
        using var harness = await Harness.StartAsync(
            fixture, fixture.ReaderConnectionString, new Dictionary<string, string> { [ServerConfig.TimeoutVariable] = "1" });
        var watch = Stopwatch.StartNew();

        var outcome = await harness.RunQueryAsync(
            "SELECT COUNT_BIG(*) FROM dbo.OrderLines AS a CROSS JOIN dbo.OrderLines AS b CROSS JOIN dbo.OrderLines AS c CROSS JOIN dbo.OrderLines AS d");

        Assert.True(outcome.IsError);
        Assert.StartsWith("Timeout: SQL Server error -2", outcome.Error, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        Assert.Equal("Timeout", harness.AuditLines()[^1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Exclusive_lock_held_elsewhere_returns_the_lock_timeout_reason_near_the_limit()
    {
        using var harness = await Harness.StartAsync(
            fixture, fixture.ReaderConnectionString, new Dictionary<string, string> { [ServerConfig.LockTimeoutVariable] = "1500" });

        await using var holder = new SqlConnection(fixture.SaConnectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var lockCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.Orders WITH (TABLOCKX, HOLDLOCK);", holder, transaction))
        {
            await lockCommand.ExecuteScalarAsync();
        }

        var watch = Stopwatch.StartNew();
        var outcome = await harness.RunQueryAsync("SELECT COUNT(*) AS n FROM dbo.Orders");
        watch.Stop();
        await transaction.RollbackAsync();

        Assert.True(outcome.IsError);
        Assert.StartsWith("LockTimeout: SQL Server error 1222", outcome.Error, StringComparison.Ordinal);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(1400), TimeSpan.FromMilliseconds(1500 + 4000));
        Assert.Equal("LockTimeout", harness.AuditLines()[^1].GetProperty("reason").GetString());
    }
}

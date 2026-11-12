using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// <c>list_tables</c> and the startup permission check at ERP scale: a separate database in the same container with
/// 20,000 tables and a reader granted SELECT on schema dbo. Times are written to the test output.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ErpScaleTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    public const string ErpDatabase = "ErpDb";
    public const string ErpReaderLogin = "mcp_erp";
    public const int TableCount = 20_000;

    private const int CreateBatchSize = 1000;

    // 2,000 tables per module: Assets_00001 .. Stock_02000.
    private static readonly string[] Modules =
        ["Finance", "Stock", "Sales", "Hr", "Purchase", "Production", "Logistics", "Crm", "Quality", "Assets"];

    private static readonly SemaphoreSlim CreateLock = new(1, 1);
    private static bool created;

    private string ErpReaderConnectionString => fixture.ConnectionString(ErpReaderLogin, fixture.ErpReaderPassword, ErpDatabase);

    [Fact]
    public async Task Permission_check_passes_within_the_default_connection_timeout()
    {
        await EnsureDatabaseAsync();
        var defaultTimeout = new SqlConnectionStringBuilder().ConnectTimeout;

        var watch = Stopwatch.StartNew();
        var report = await PermissionCheck.RunAsync(new SqlGateway(ErpReaderConnectionString, 5000), 30, CancellationToken.None);
        watch.Stop();

        Assert.True(report.Passed, string.Join("; ", report.Findings));
        Assert.Equal(ErpDatabase, report.Database);
        Write($"permission check on {TableCount} tables (cold connection): {watch.ElapsedMilliseconds} ms");
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(defaultTimeout),
            $"The permission check took {watch.ElapsedMilliseconds} ms, more than the default connection timeout of {defaultTimeout} s.");

        watch.Restart();
        using var harness = await Harness.StartAsync(fixture, ErpReaderConnectionString);
        Write($"full in-process startup (config, audit, check, startup line): {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task List_without_filter_stops_at_500_and_reports_the_total()
    {
        await EnsureDatabaseAsync();
        using var harness = await Harness.StartAsync(fixture, ErpReaderConnectionString);

        var watch = Stopwatch.StartNew();
        var outcome = await harness.ListTablesAsync();
        watch.Stop();

        var json = outcome.Json;
        Assert.Equal(500, json.GetProperty("tables").GetArrayLength());
        Assert.Equal(500, json.GetProperty("row_count").GetInt32());
        Assert.Equal(TableCount, json.GetProperty("total_count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_items", json.GetProperty("truncated_reason").GetString());
        Assert.Equal("Assets_00001", json.GetProperty("tables")[0].GetProperty("name").GetString());
        Assert.True(outcome.Payload!.Length <= harness.Config.MaxChars);

        var line = harness.AuditLines()[^1];
        Assert.Equal(500, line.GetProperty("rows").GetInt32());
        Assert.True(line.GetProperty("truncated").GetBoolean());
        Write($"list_tables without a filter ({TableCount} matches, 500 returned): {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task List_with_a_filter_returns_only_the_matches()
    {
        await EnsureDatabaseAsync();
        using var harness = await Harness.StartAsync(fixture, ErpReaderConnectionString);

        // A substring: the underscore is matched literally.
        var watch = Stopwatch.StartNew();
        var substring = (await harness.ListTablesAsync(nameFilter: "Stock_0001")).Json;
        var substringMs = watch.ElapsedMilliseconds;
        Assert.Equal(10, substring.GetProperty("row_count").GetInt32());
        Assert.Equal(10, substring.GetProperty("total_count").GetInt32());
        Assert.False(substring.GetProperty("truncated").GetBoolean());
        Assert.All(
            substring.GetProperty("tables").EnumerateArray(),
            t => Assert.StartsWith("Stock_0001", t.GetProperty("name").GetString(), StringComparison.Ordinal));

        // A LIKE pattern: Stock tables whose number ends in 5.
        watch.Restart();
        var pattern = (await harness.ListTablesAsync(nameFilter: "Stock[_]%5")).Json;
        var patternMs = watch.ElapsedMilliseconds;
        Assert.Equal(200, pattern.GetProperty("row_count").GetInt32());
        Assert.Equal(200, pattern.GetProperty("total_count").GetInt32());
        Assert.False(pattern.GetProperty("truncated").GetBoolean());

        // A filter wider than the cap still reports every match.
        watch.Restart();
        var wide = (await harness.ListTablesAsync(nameFilter: "Finance")).Json;
        var wideMs = watch.ElapsedMilliseconds;
        Assert.Equal(500, wide.GetProperty("row_count").GetInt32());
        Assert.Equal(2000, wide.GetProperty("total_count").GetInt32());
        Assert.True(wide.GetProperty("truncated").GetBoolean());

        Write($"list_tables name_filter \"Stock_0001\" (10 matches): {substringMs} ms");
        Write($"list_tables name_filter \"Stock[_]%5\" (200 matches): {patternMs} ms");
        Write($"list_tables name_filter \"Finance\" (2000 matches, 500 returned): {wideMs} ms");
    }

    [Fact]
    public async Task List_respects_the_character_cap()
    {
        await EnsureDatabaseAsync();
        using var harness = await Harness.StartAsync(
            fixture, ErpReaderConnectionString, new Dictionary<string, string> { [ServerConfig.MaxCharsVariable] = "1000" });

        var outcome = await harness.ListTablesAsync();

        var json = outcome.Json;
        Assert.True(outcome.Payload!.Length <= 1000, $"payload length {outcome.Payload.Length}");
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_chars", json.GetProperty("truncated_reason").GetString());
        Assert.InRange(json.GetProperty("row_count").GetInt32(), 1, 499);
        Assert.Equal(json.GetProperty("row_count").GetInt32(), json.GetProperty("tables").GetArrayLength());
        Assert.Equal(TableCount, json.GetProperty("total_count").GetInt32());
    }

    private async Task EnsureDatabaseAsync()
    {
        await CreateLock.WaitAsync();
        try
        {
            if (created)
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            await fixture.ExecuteAsAdminAsync("master", $"CREATE DATABASE [{ErpDatabase}];");
            for (var start = 0; start < TableCount; start += CreateBatchSize)
            {
                var batch = new StringBuilder();
                for (var i = start; i < start + CreateBatchSize; i++)
                {
                    var module = Modules[i % Modules.Length];
                    var number = (i / Modules.Length) + 1;
                    batch.Append(CultureInfo.InvariantCulture, $"CREATE TABLE dbo.[{module}_{number:D5}] (Id int NOT NULL, Name nvarchar(50) NULL);\n");
                }

                await fixture.ExecuteAsAdminAsync(ErpDatabase, batch.ToString());
            }

            await fixture.ExecuteAsAdminAsync(
                "master", $"CREATE LOGIN [{ErpReaderLogin}] WITH PASSWORD = N'{fixture.ErpReaderPassword}', DEFAULT_DATABASE = [{ErpDatabase}];");
            await fixture.ExecuteAsAdminAsync(ErpDatabase, $"""
                CREATE USER [{ErpReaderLogin}] FOR LOGIN [{ErpReaderLogin}];
                GRANT SELECT ON SCHEMA::dbo TO [{ErpReaderLogin}];
                """);
            Write($"created {TableCount} tables in {ErpDatabase}: {watch.ElapsedMilliseconds} ms");
            created = true;
        }
        finally
        {
            CreateLock.Release();
        }
    }

    private void Write(string line) => output.WriteLine(line);
}

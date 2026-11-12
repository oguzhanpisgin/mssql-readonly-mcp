using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using MssqlReadOnlyMcp.Audit;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>The outcome of one in-process tool call: the payload, or the error text the client would see.</summary>
internal sealed record CallOutcome(string? Payload, string? Error)
{
    public bool IsError => Error is not null;

    public JsonElement Json => JsonDocument.Parse(Payload ?? throw new InvalidOperationException($"No payload; error: {Error}")).RootElement;
}

/// <summary>
/// A server assembled in process exactly as <c>Program</c> assembles it (configuration from variables, audit log,
/// permission check, startup line, tool service), with the gateway's counters visible to the test.
/// </summary>
internal sealed class Harness : IDisposable
{
    private readonly SqlServerFixture fixture;

    private Harness(SqlServerFixture fixture, ServerConfig config, SqlGateway gateway, AuditLog audit, ToolService service)
    {
        this.fixture = fixture;
        Config = config;
        Gateway = gateway;
        Audit = audit;
        Service = service;
    }

    public ServerConfig Config { get; }

    public SqlGateway Gateway { get; }

    public AuditLog Audit { get; }

    public ToolService Service { get; }

    public string AuditPath => Config.AuditLogPath;

    public static async Task<Harness> StartAsync(
        SqlServerFixture fixture, string connectionString, IReadOnlyDictionary<string, string>? variables = null)
    {
        var env = new Dictionary<string, string>
        {
            [ServerConfig.ConnectionStringVariable] = connectionString,
            [ServerConfig.AuditLogVariable] = fixture.NewAuditPath(),
        };
        foreach (var (key, value) in variables ?? new Dictionary<string, string>())
        {
            env[key] = value;
        }

        var config = ServerConfig.Load(name => env.GetValueOrDefault(name), requireAuditLog: true);
        var audit = AuditLog.Open(config.AuditLogPath, "test-session");
        var checkGateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs);
        var outcome = await ServerStartup.CheckAndRecordAsync(config, checkGateway, audit, CancellationToken.None);
        Assert.True(outcome.ExitCode == ExitCodes.Ok, outcome.ErrorLine);
        var gateway = checkGateway.PinnedTo(outcome.Report!.Database);
        var service = new ToolService(config, gateway, audit, outcome.Report.Database, NullLogger<ToolService>.Instance);
        return new Harness(fixture, config, gateway, audit, service);
    }

    /// <summary>
    /// A server assembled without the permission check, for proofs that need a login the check refuses (the rollback
    /// of a write sent past the gate). The tools run in the connection string's database.
    /// </summary>
    public static Harness StartUnchecked(SqlServerFixture fixture, string connectionString, string database)
    {
        var env = new Dictionary<string, string>
        {
            [ServerConfig.ConnectionStringVariable] = connectionString,
            [ServerConfig.AuditLogVariable] = fixture.NewAuditPath(),
        };
        var config = ServerConfig.Load(name => env.GetValueOrDefault(name), requireAuditLog: true);
        var audit = AuditLog.Open(config.AuditLogPath, "test-session");
        var gateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs).PinnedTo(database);
        var service = new ToolService(config, gateway, audit, database, NullLogger<ToolService>.Instance);
        return new Harness(fixture, config, gateway, audit, service);
    }

    public Task<CallOutcome> RunQueryAsync(string sql, int? maxRows = null) =>
        CallAsync(() => Service.RunQueryAsync(sql, maxRows, CancellationToken.None));

    /// <summary>The <c>run_query</c> call without the gate (test-only path), audited like any call.</summary>
    public Task<CallOutcome> RunPastTheGateAsync(string sql, int? maxRows = null) =>
        CallAsync(() => Service.RunPastTheGateAsync(sql, maxRows, CancellationToken.None));

    public Task<CallOutcome> ListTablesAsync(string? schema = null, string? nameFilter = null) =>
        CallAsync(() => Service.ListTablesAsync(schema, nameFilter, CancellationToken.None));

    public Task<CallOutcome> DescribeTableAsync(string table) =>
        CallAsync(() => Service.DescribeTableAsync(table, CancellationToken.None));

    public IReadOnlyList<JsonElement> AuditLines() => TestText.AuditLines(AuditPath);

    public void Dispose() => Audit.Dispose();

    private async Task<CallOutcome> CallAsync(Func<Task<string>> call)
    {
        CallOutcome outcome;
        try
        {
            outcome = new CallOutcome(await call(), null);
        }
        catch (McpException exception)
        {
            outcome = new CallOutcome(null, exception.Message);
        }

        TestText.AssertNoSecrets(fixture, outcome.Payload);
        TestText.AssertNoSecrets(fixture, outcome.Error);
        return outcome;
    }
}

internal static class TestText
{
    public static void AssertNoSecrets(SqlServerFixture fixture, string? text)
    {
        if (text is null)
        {
            return;
        }

        foreach (var secret in fixture.Secrets)
        {
            Assert.False(text.Contains(secret, StringComparison.Ordinal), "A password appeared in output.");
        }
    }

    public static IReadOnlyList<JsonElement> AuditLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }

    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

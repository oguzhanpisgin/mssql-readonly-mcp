using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using MssqlReadOnlyMcp;
using MssqlReadOnlyMcp.Audit;

// Commands: no argument = stdio MCP server; check-login; verify-audit <path>. Configuration comes from environment
// variables only. Standard output carries only MCP messages (server) or the command's report; errors go to stderr.

// Every number and date this process formats is culture-independent, whatever the machine's settings.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

try
{
    return args switch
    {
        [] => await RunServerAsync(),
        ["check-login"] => await CheckLoginAsync(),
        ["verify-audit", var path] => VerifyAudit(path),
        _ => Usage(),
    };
}
catch (Exception exception)
{
    // Last resort: one line from the type name only (a message can quote configuration), never a stack trace. Its own
    // exit code: 4 stays "SQL Server unreachable".
    Console.Error.WriteLine($"{ServerStartup.Prefix}stopped by an unexpected error ({exception.GetType().Name}).");
    return ExitCodes.InternalError;
}

static int Usage()
{
    Console.Error.WriteLine(
        $"{ServerStartup.Prefix}usage: mssql-readonly-mcp [check-login | verify-audit <path>] (no argument runs the stdio server).");
    return ExitCodes.ConfigOrAudit;
}

static int VerifyAudit(string path)
{
    var result = AuditVerifier.Verify(path);
    Console.Out.WriteLine(result.Message);
    return result.Ok ? ExitCodes.Ok : ExitCodes.AuditVerifyFailed;
}

static ServerConfig? LoadConfig(bool requireAuditLog)
{
    try
    {
        return ServerConfig.Load(Environment.GetEnvironmentVariable, requireAuditLog);
    }
    catch (ConfigException exception)
    {
        Console.Error.WriteLine($"{ServerStartup.Prefix}configuration error: {exception.Message}.");
        return null;
    }
}

static async Task<int> CheckLoginAsync()
{
    var config = LoadConfig(requireAuditLog: false);
    if (config is null)
    {
        return ExitCodes.ConfigOrAudit;
    }

    PermissionReport report;
    try
    {
        var gateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs);
        report = await PermissionCheck.RunAsync(gateway, config.TimeoutSeconds, CancellationToken.None);
    }
    catch (Exception exception)
    {
        var (exitCode, line) = ServerStartup.DescribeCheckFailure(exception);
        Console.Error.WriteLine(line);
        return exitCode;
    }

    if (report.Passed)
    {
        Console.Out.WriteLine($"Login {report.Login} on database {report.Database}: passed, no permission to change data was found.");
        return ExitCodes.Ok;
    }

    Console.Out.WriteLine(
        $"Login {report.Login} on database {report.Database}: refused, {report.Findings.Count} right(s) outside the read-only set found:");
    foreach (var finding in report.Findings)
    {
        Console.Out.WriteLine($"  {finding}");
    }

    return ExitCodes.LoginCanWrite;
}

static async Task<int> RunServerAsync()
{
    var config = LoadConfig(requireAuditLog: true);
    if (config is null)
    {
        return ExitCodes.ConfigOrAudit;
    }

    AuditLog audit;
    try
    {
        audit = AuditLog.Open(config.AuditLogPath, Guid.NewGuid().ToString("N"));
    }
    catch (AuditWriteException exception)
    {
        Console.Error.WriteLine($"{ServerStartup.Prefix}audit log error: {exception.Message}");
        return ExitCodes.ConfigOrAudit;
    }

    using (audit)
    {
        var checkGateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs);
        var outcome = await ServerStartup.CheckAndRecordAsync(config, checkGateway, audit, CancellationToken.None);
        if (outcome.ExitCode != ExitCodes.Ok)
        {
            Console.Error.WriteLine(outcome.ErrorLine);
            return outcome.ExitCode;
        }

        // Every tool connection opens in the database the check ran in.
        var database = outcome.Report!.Database;
        var gateway = checkGateway.PinnedTo(database);

        // No default configuration sources: no appsettings.json from the working directory and no Logging__* variables
        // can raise a log level. The SDK's own log messages can quote whole JSON-RPC messages (and so SQL text), so
        // its categories are switched off; this server logs one line per tool call without SQL text instead.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(gateway);
        builder.Services.AddSingleton(audit);
        builder.Services.AddSingleton(sp => new ToolService(
            config, gateway, audit, database, sp.GetRequiredService<ILogger<ToolService>>()));
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "mssql-readonly-mcp",
                Version = ServerVersion.Current,
            })
            .WithStdioServerTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(CallToolAuditFilter.Wrap))
            .WithTools<McpTools>();

        // Shutdown order: the host stops (end of standard input or Ctrl+C), the calls still running finish and write
        // their audit lines, then the host is disposed, then the audit log (the enclosing using). The host is started
        // and stopped here rather than with RunAsync, which disposes it before the drain could run.
        using var host = builder.Build();
        var service = host.Services.GetRequiredService<ToolService>();
        await host.StartAsync();
        await host.WaitForShutdownAsync();

        if (!await service.DrainAsync(TimeSpan.FromSeconds(config.TimeoutSeconds + 10)))
        {
            // Not a normal shutdown: the audit log is closed under calls that have not written their lines.
            Console.Error.WriteLine(
                $"{ServerStartup.Prefix}stopped while {service.CallsInFlight} call(s) were still running; their audit lines may be missing and they return no data.");
            return ExitCodes.DrainTimedOut;
        }

        return ExitCodes.Ok;
    }
}

namespace MssqlReadOnlyMcp
{
    /// <summary>
    /// The version the server reports as MCP <c>serverInfo.version</c>: the assembly's informational version (set by
    /// <c>-p:Version=</c> at publish) without the <c>+commit</c> suffix the SDK appends, so a tag build reports
    /// <c>0.1.0</c>. Without it the MCP SDK would report the four-part assembly version (<c>0.1.0.0</c>).
    /// </summary>
    internal static class ServerVersion
    {
        /// <summary>Reported when the assembly carries no informational version.</summary>
        public const string Fallback = "0.0.0-unknown";

        public static string Current { get; } = FromInformationalVersion(
            typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        public static string FromInformationalVersion(string? informationalVersion)
        {
            var plus = informationalVersion?.IndexOf('+') ?? -1;
            var version = plus >= 0 ? informationalVersion![..plus] : informationalVersion;
            return string.IsNullOrEmpty(version) ? Fallback : version;
        }
    }
}

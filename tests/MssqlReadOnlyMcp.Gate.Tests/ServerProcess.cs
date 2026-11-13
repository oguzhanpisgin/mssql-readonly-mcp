using System.Collections.Concurrent;
using System.Diagnostics;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// The built server started as a child process (<c>dotnet mssql-readonly-mcp.dll</c>), without SQL Server. The
/// project reference copies the server with its own <c>.deps.json</c> and <c>.runtimeconfig.json</c> into this test
/// project's output folder, so it starts from there with its own settings.
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    public static readonly string ServerDll = Path.Combine(AppContext.BaseDirectory, "mssql-readonly-mcp.dll");

    private static readonly string[] ServerVariables =
    [
        ServerConfig.ConnectionStringVariable, ServerConfig.AuditLogVariable, ServerConfig.MaxRowsVariable,
        ServerConfig.MaxCharsVariable, ServerConfig.TimeoutVariable, ServerConfig.LockTimeoutVariable,
    ];

    private readonly ConcurrentQueue<string> _stderr = new();

    private ServerProcess(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    /// <summary>Lines written to stderr so far.</summary>
    public string[] Stderr => [.. _stderr];

    /// <summary>
    /// Starts the server with only the given server variables set (the others removed from the inherited
    /// environment). Standard input stays open, so a server that reaches its serving step waits for messages.
    /// </summary>
    public static ServerProcess Start(string[] arguments, IReadOnlyDictionary<string, string> variables)
    {
        Assert.True(File.Exists(ServerDll), "The server build output is missing: " + ServerDll);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ServerDll);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string name in ServerVariables)
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in variables)
        {
            start.Environment[name] = value;
        }

        var process = new Process { StartInfo = start };
        var server = new ServerProcess(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                server._stderr.Enqueue(e.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return server;
    }

    /// <summary>Runs the server to its exit with standard input closed; returns the exit code and stderr lines.</summary>
    public static async Task<(int ExitCode, string[] Stderr)> RunAsync(string[] arguments, IReadOnlyDictionary<string, string> variables)
    {
        using ServerProcess server = Start(arguments, variables);
        server.Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await server.Process.WaitForExitAsync(timeout.Token);
        server.Process.WaitForExit(); // drains the redirected streams
        return (server.Process.ExitCode, server.Stderr);
    }

    /// <summary>Stops this test's own child process, if it still runs.</summary>
    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            // Never started.
        }

        Process.Dispose();
    }
}

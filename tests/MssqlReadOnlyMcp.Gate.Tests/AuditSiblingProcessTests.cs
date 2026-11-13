using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MssqlReadOnlyMcp.Audit;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// I-1 with the real server, without SQL Server: two server processes started at the same instant on one audit path
/// each end with an audit file of their own. The connection string points at a local listener that accepts and never
/// answers, so each server, once its audit file is open, waits in its connect step and stays alive.
/// </summary>
public sealed class AuditSiblingProcessTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mssql-sibling-test-" + Guid.NewGuid().ToString("N"));

    public AuditSiblingProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A child still closing its handles; nothing more to do.
        }
    }

    [Fact]
    public async Task Two_servers_started_together_on_one_audit_path_each_write_a_file_of_their_own()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new ConcurrentQueue<TcpClient>();
        using var stopAccepting = new CancellationTokenSource();
        Task acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Enqueue(await listener.AcceptTcpClientAsync(stopAccepting.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        string auditPath = Path.Combine(_dir, "audit.jsonl");
        string password = "Pw_" + Guid.NewGuid().ToString("N");
        var variables = new Dictionary<string, string>
        {
            [ServerConfig.ConnectionStringVariable] =
                $"Server=tcp:127.0.0.1,{port};Database=d;User ID=u;Password={password};Encrypt=False;Connect Timeout=90",
            [ServerConfig.AuditLogVariable] = auditPath,
        };

        var servers = new ServerProcess?[2];
        try
        {
            // Both started from two threads released together.
            using (var together = new Barrier(2))
            {
                Thread[] starters = Enumerable.Range(0, 2).Select(i => new Thread(() =>
                {
                    together.SignalAndWait();
                    servers[i] = ServerProcess.Start([], variables);
                })).ToArray();
                Array.ForEach(starters, t => t.Start());
                Array.ForEach(starters, t => Assert.True(t.Join(Patience)));
            }

            ServerProcess first = servers[0]!;
            ServerProcess second = servers[1]!;
            string[] lockPaths = [auditPath + ".lock", Path.Combine(_dir, "audit.2.jsonl.lock")];

            // Ready when both have reached the connect step (which comes after the audit open) and both locks are held.
            var deadline = DateTime.UtcNow + Patience;
            while (!(accepted.Count >= 2 && lockPaths.All(IsHeld)))
            {
                Assert.False(first.Process.HasExited, $"The first server exited ({string.Join(" | ", first.Stderr)}).");
                Assert.False(second.Process.HasExited, $"The second server exited ({string.Join(" | ", second.Stderr)}).");
                Assert.True(DateTime.UtcNow < deadline, "The two servers did not both open an audit file in time.");
                await Task.Delay(100);
            }

            Assert.False(first.Process.HasExited);
            Assert.False(second.Process.HasExited);

            // Exactly one of them writes the sibling and says so; the other writes the configured file and says nothing.
            string[] lines = [.. first.Stderr, .. second.Stderr];
            Assert.Equal(
                "mssql-readonly-mcp: audit file audit.jsonl is locked by another process; this process writes audit.2.jsonl in the same folder.",
                Assert.Single(lines));
            Assert.DoesNotContain(lines, l => l.Contains(password, StringComparison.Ordinal) || l.Contains("127.0.0.1", StringComparison.Ordinal));

            first.Dispose();
            second.Dispose();
            Assert.Equal(
                ["audit.2.jsonl", "audit.2.jsonl.lock", "audit.jsonl", "audit.jsonl.lock"],
                Directory.EnumerateFiles(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));

            foreach (string file in new[] { auditPath, Path.Combine(_dir, "audit.2.jsonl") })
            {
                AuditVerifyResult result = AuditVerifier.Verify(file);
                Assert.True(result.Ok, result.Message);

                // The server's own verify-audit command works on each file unchanged.
                var (exitCode, stderr) = await ServerProcess.RunAsync(["verify-audit", file], new Dictionary<string, string>());
                Assert.Equal(0, exitCode);
                Assert.Empty(stderr);
            }
        }
        finally
        {
            foreach (ServerProcess? server in servers)
            {
                server?.Dispose();
            }

            await stopAccepting.CancelAsync();
            listener.Stop();
            await acceptLoop;
            while (accepted.TryDequeue(out TcpClient? client))
            {
                client.Dispose();
            }
        }
    }

    // Property 4: the start line names both files by file name only, and there is none for the configured file.
    [Fact]
    public void Start_line_names_the_sibling_by_file_name_only()
    {
        string folder = Path.Combine(_dir, "secret-folder-name");
        string configured = Path.Combine(folder, "audit.jsonl");
        Assert.Null(ServerStartup.DescribeAuditFile(configured, Path.GetFullPath(configured)));
        Assert.Equal(
            "mssql-readonly-mcp: audit file audit.jsonl is locked by another process; this process writes audit.3.jsonl in the same folder.",
            ServerStartup.DescribeAuditFile(configured, Path.Combine(folder, "audit.3.jsonl")));
    }

    private static bool IsHeld(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
    }
}

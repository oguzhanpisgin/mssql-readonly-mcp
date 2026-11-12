namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>The configuration record never prints its connection string.</summary>
public sealed class ServerConfigTests
{
    [Fact]
    public void ToString_and_interpolation_leave_out_the_connection_string()
    {
        // A throwaway value shaped like a password; no server is involved.
        var password = "Pw_" + Guid.NewGuid().ToString("N");
        var config = ServerConfig.Load(
            name => name switch
            {
                ServerConfig.ConnectionStringVariable => $"Server=db.example.local;User ID=reader;Password={password};Database=Shop",
                ServerConfig.AuditLogVariable => "audit.jsonl",
                _ => null,
            },
            requireAuditLog: true);

        Assert.Contains(password, config.ConnectionString, StringComparison.Ordinal);
        foreach (var text in new[] { config.ToString(), $"{config}", string.Join(" ", new object[] { config }) })
        {
            Assert.DoesNotContain(password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("db.example.local", text, StringComparison.Ordinal);
            Assert.Equal(
                "ServerConfig { ConnectionString = <not shown>, AuditLogPath = audit.jsonl, MaxRows = 200, MaxChars = 40000, " +
                "TimeoutSeconds = 30, LockTimeoutMs = 5000 }",
                text);
        }
    }
}

using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// R-2: refused statements send nothing to SQL Server (the gateway's counters do not move) and leave every seeded
/// table, the sequence and the object catalogue unchanged.
/// R-3: a write sent past the gate on purpose is refused by SQL Server itself.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class GateRefusalTests(SqlServerFixture fixture)
{
    /// <summary>The R-2 refused set; the Extended Events test replays it too.</summary>
    internal static readonly string[] RefusedStatements =
    [
        // DML
        "INSERT INTO dbo.Customers (Name) VALUES (N'Yeni Müşteri')",
        "UPDATE dbo.Customers SET Name = N'Değişti'",
        "DELETE FROM dbo.Orders",
        "MERGE dbo.Customers AS t USING (SELECT 1 AS id) AS s ON t.CustomerId = s.id WHEN MATCHED THEN DELETE;",
        "INSERT INTO dbo.Orders (CustomerId, OrderedAt, Total) EXEC ('SELECT 1, GETDATE(), 1')",
        "WITH c AS (SELECT TOP (1) * FROM dbo.Orders) DELETE FROM c",
        // DDL and administration
        "TRUNCATE TABLE dbo.OrderLines",
        "DROP TABLE dbo.OrderLines",
        "CREATE TABLE dbo.Extra (Id int)",
        "ALTER TABLE dbo.Customers ADD Extra int NULL",
        "GRANT SELECT ON dbo.Customers TO public",
        "UPDATE STATISTICS dbo.Customers",
        "SET IDENTITY_INSERT dbo.Customers ON",
        "BACKUP DATABASE ShopDb TO DISK = N'/tmp/shop.bak'",
        // EXEC
        "EXEC dbo.AddCustomer N'Kayıt'",
        "EXECUTE sp_executesql N'DELETE FROM dbo.Orders'",
        "EXEC ('DROP TABLE dbo.OrderLines')",
        // SELECT INTO and variables
        "SELECT * INTO dbo.CustomersCopy FROM dbo.Customers",
        "SELECT * INTO #copy FROM dbo.Customers",
        "DECLARE @n int = 1",
        // Stacked statements and batches
        "SELECT 1; DELETE FROM dbo.Orders",
        "SELECT * FROM dbo.Customers; DROP TABLE dbo.OrderLines",
        "SELECT 1\nGO\nDELETE FROM dbo.Orders",
        "BEGIN TRANSACTION",
        // Sequence advance
        "SELECT NEXT VALUE FOR dbo.InvoiceNumbers",
        "SELECT NEXT VALUE FOR dbo.InvoiceNumbers AS n FROM dbo.Customers",
        // OPENROWSET and other remote sources
        "SELECT * FROM OPENROWSET(BULK '/etc/passwd', SINGLE_CLOB) AS x",
        "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=other;Trusted_Connection=yes;', 'SELECT 1') AS x",
        "SELECT * FROM OPENQUERY(remote, 'SELECT 1')",
        "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=other').ShopDb.dbo.Customers",
        // Cross-database and linked server names
        "SELECT * FROM ArchiveDb.dbo.ArchivedOrders",
        "SELECT * FROM ArchiveDb..ArchivedOrders",
        "SELECT * FROM master.sys.sql_logins",
        "SELECT * FROM otherserver.ShopDb.dbo.Customers",
        // Locking hints, waits and server control
        "SELECT * FROM dbo.Customers WITH (TABLOCKX)",
        "WAITFOR DELAY '00:00:05'",
        "SHUTDOWN",
    ];

    [Fact]
    public async Task Refused_statements_send_nothing_and_change_nothing()
    {
        Assert.True(RefusedStatements.Length >= 25);
        var before = await fixture.FingerprintAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;
        var commands = harness.Gateway.CommandsCreated;

        foreach (var sql in RefusedStatements)
        {
            var expected = ReadOnlyGate.Evaluate(sql, new GateOptions(SqlServerFixture.Database));
            Assert.False(expected.Allowed, $"The gate allowed a statement this test expects refused (reason {expected.Reason}).");

            var outcome = await harness.RunQueryAsync(sql);

            // The counting seam first: no connection was opened and no command was created for a refused statement.
            Assert.True(
                connections == harness.Gateway.ConnectionsOpened && commands == harness.Gateway.CommandsCreated,
                $"A {expected.Reason} statement reached SQL Server: connections {connections} -> {harness.Gateway.ConnectionsOpened}, " +
                $"commands {commands} -> {harness.Gateway.CommandsCreated}.");
            Assert.True(outcome.IsError, $"No error for a {expected.Reason} statement.");
            Assert.StartsWith($"{expected.Reason}: refused by the read-only gate", outcome.Error, StringComparison.Ordinal);
        }

        var refusedLines = harness.AuditLines().Skip(1).ToList();
        Assert.Equal(RefusedStatements.Length, refusedLines.Count);
        Assert.All(refusedLines, line => Assert.Equal("refused", line.GetProperty("decision").GetString()));
        Assert.Equal(before, await fixture.FingerprintAsync());
    }

    [Theory]
    [InlineData("UPDATE dbo.Customers SET Name = N'Değişti'")]
    [InlineData("DELETE FROM dbo.OrderLines")]
    [InlineData("INSERT INTO dbo.Customers (Name) VALUES (N'Yeni')")]
    [InlineData("EXEC dbo.AddCustomer N'Kayıt'")]
    [InlineData("CREATE TABLE dbo.Extra (Id int)")]
    [InlineData("SELECT NEXT VALUE FOR dbo.InvoiceNumbers")]
    public async Task Write_sent_past_the_gate_is_refused_by_sql_server(string sql)
    {
        var before = await fixture.FingerprintAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        // Test-only path: ExecuteReadAsync is what run_query calls after the gate allows a statement.
        var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => harness.Service.ExecuteReadAsync(sql, 10, CancellationToken.None));
        var failure = ToolErrors.FromException(exception);

        // 229: permission denied on an object; 262: CREATE permission denied in the database.
        Assert.Contains(exception.Number, new[] { 229, 262 });
        if (exception.Number == 229)
        {
            Assert.Equal("PermissionDenied", failure.Reason);
        }

        TestText.AssertNoSecrets(fixture, failure.Message);
        Assert.Equal(before, await fixture.FingerprintAsync());
    }
}

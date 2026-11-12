namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>R-1: the startup permission check passes the reader and refuses every login that can write.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PermissionCheckTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Reader_passes()
    {
        var report = await CheckAsync(fixture.ReaderConnectionString);

        Assert.True(report.Passed, string.Join(" | ", report.Findings));
        Assert.Equal(SqlServerFixture.Database, report.Database);
    }

    [Fact]
    public async Task Datareader_only_member_is_refused_for_its_role_schema_because_it_can_create_sequences_there()
    {
        var report = await CheckAsync(fixture.DataReaderConnectionString);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f.ToString() == "CONTROL on schema [db_datareader]");
        Assert.Contains(report.Findings, f => f.ToString() == "CREATE SEQUENCE on schema [db_datareader]");
        Assert.All(report.Findings.Where(f => f.Scope == "schema"), f => Assert.Equal(["[db_datareader]"], f.Objects));

        // The role also reads every table of the database: SELECT at database scope is outside the allowed set.
        Assert.Contains(report.Findings, f => f.ToString() == "SELECT on database ShopDb");

        // The finding is real: SQL Server lets the member create a schema object there (rolled back here).
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.DataReaderConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "CREATE SEQUENCE db_datareader.ProbeSequence START WITH 1;", connection, transaction);
        await command.ExecuteNonQueryAsync();
        transaction.Rollback();
    }

    [Fact]
    public async Task Single_table_update_grant_is_refused_and_named_by_object()
    {
        var report = await CheckAsync(fixture.UpdaterConnectionString);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("UPDATE", finding.Permission);
        Assert.Equal("object", finding.Scope);
        Assert.Equal(["[dbo].[Customers]"], finding.Objects);
        Assert.Equal("UPDATE on object [dbo].[Customers]", finding.ToString());
    }

    [Fact]
    public async Task Datawriter_member_is_refused_by_role()
    {
        var report = await CheckAsync(fixture.WriterConnectionString);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f is { Permission: "db_datawriter", Scope: "database role" });
        Assert.Contains(report.Findings, f => f.ToString() == "member of database role db_datawriter");
    }

    [Fact]
    public async Task Owner_member_is_refused_by_role()
    {
        var report = await CheckAsync(fixture.OwnerConnectionString);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f is { Permission: "db_owner", Scope: "database role" });
        Assert.Contains(report.Findings, f => f is { Permission: "CREATE TABLE" } && f.Scope.StartsWith("database ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sa_is_refused_as_sysadmin_and_control_server()
    {
        var report = await CheckAsync(fixture.SaConnectionString);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f.ToString() == "member of server role sysadmin");
        Assert.Contains(report.Findings, f => f.ToString() == "CONTROL SERVER on server");
        Assert.All(report.Findings, f => Assert.True(f.Objects.Count <= PermissionFinding.MaxObjectNames));
    }

    [Fact]
    public async Task Column_level_update_grant_is_refused_and_named_by_object_and_column()
    {
        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.ColumnUpdaterLogin));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("UPDATE on column [dbo].[Customers].[City]", finding.ToString());
    }

    [Theory]
    [InlineData(SqlServerFixture.ImpersonatorLogin, "IMPERSONATE on user [mcp_reader]")]
    [InlineData(SqlServerFixture.RoleAdminLogin, "ALTER ANY ROLE on database ShopDb")]
    [InlineData(SqlServerFixture.SecurityAdminLogin, "member of server role securityadmin")]
    [InlineData(SqlServerFixture.SecurityAdminLogin, "ALTER ANY LOGIN on server")]
    [InlineData(SqlServerFixture.ProcedureRunnerLogin, "EXECUTE on object [dbo].[AddCustomer]")]
    [InlineData(SqlServerFixture.SequenceUserLogin, "UPDATE on object [dbo].[InvoiceNumbers]")]
    [InlineData(SqlServerFixture.DenyWriterLogin, "CONTROL on schema [db_denydatawriter]")]
    [InlineData(SqlServerFixture.TracerLogin, "ALTER TRACE on server")]
    [InlineData(SqlServerFixture.BackupLogin, "BACKUP DATABASE on database ShopDb")]
    [InlineData(SqlServerFixture.UnmaskLogin, "UNMASK on database ShopDb")]
    [InlineData(SqlServerFixture.RoleTakerLogin, "TAKE OWNERSHIP on role [reporting]")]
    [InlineData(SqlServerFixture.SynonymUpdaterLogin, "UPDATE on synonym [dbo].[CustomerAlias]")]
    public async Task Login_with_one_extra_right_is_refused_and_the_right_is_named(string login, string expected)
    {
        var report = await CheckAsync(fixture.ExtraConnectionString(login));

        Assert.False(report.Passed);
        Assert.Contains(expected, report.Findings.Select(f => f.ToString()));
        Assert.DoesNotContain(report.Findings, f => f.Unchecked);
    }

    [Fact]
    public async Task Single_right_logins_have_exactly_their_one_finding()
    {
        foreach (var (login, expected) in new[]
        {
            (SqlServerFixture.ImpersonatorLogin, "IMPERSONATE on user [mcp_reader]"),
            (SqlServerFixture.ProcedureRunnerLogin, "EXECUTE on object [dbo].[AddCustomer]"),
            (SqlServerFixture.SequenceUserLogin, "UPDATE on object [dbo].[InvoiceNumbers]"),

            // Rights outside the old lists: refused because they are not in the allowed server or database set.
            (SqlServerFixture.BackupLogin, "BACKUP DATABASE on database ShopDb"),
            (SqlServerFixture.UnmaskLogin, "UNMASK on database ShopDb"),
            (SqlServerFixture.RoleTakerLogin, "TAKE OWNERSHIP on role [reporting]"),
            (SqlServerFixture.SynonymUpdaterLogin, "UPDATE on synonym [dbo].[CustomerAlias]"),
        })
        {
            var report = await CheckAsync(fixture.ExtraConnectionString(login));
            Assert.Equal([expected], report.Findings.Select(f => f.ToString()));
        }

        // Effective permissions include what a grant implies: ALTER TRACE on the server implies SHOWPLAN in every
        // database, and both are named.
        var tracer = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.TracerLogin));
        Assert.Equal(["SHOWPLAN on database ShopDb", "ALTER TRACE on server"], tracer.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Has_perms_by_name_answers_yes_or_no_for_every_right_the_check_asks_of_a_synonym()
    {
        // No NULL for any of the seven rights (a NULL would be a "could not be checked" finding for every login);
        // the reader holds none of them, the synonym updater only UPDATE.
        async Task<string[]> AnswersAsync(string connectionString)
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT p.name + N'=' + ISNULL(CONVERT(nvarchar(4), HAS_PERMS_BY_NAME(N'[dbo].[CustomerAlias]', N'OBJECT', p.name)), N'NULL') " +
                "FROM (VALUES (1, N'INSERT'), (2, N'UPDATE'), (3, N'DELETE'), (4, N'EXECUTE'), (5, N'ALTER'), (6, N'CONTROL'), " +
                "(7, N'TAKE OWNERSHIP')) AS p(n, name) ORDER BY p.n",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            var answers = new List<string>();
            while (await reader.ReadAsync())
            {
                answers.Add(reader.GetString(0));
            }

            return [.. answers];
        }

        Assert.Equal(
            ["INSERT=0", "UPDATE=0", "DELETE=0", "EXECUTE=0", "ALTER=0", "CONTROL=0", "TAKE OWNERSHIP=0"],
            await AnswersAsync(fixture.ReaderConnectionString));
        Assert.Equal(
            ["INSERT=0", "UPDATE=1", "DELETE=0", "EXECUTE=0", "ALTER=0", "CONTROL=0", "TAKE OWNERSHIP=0"],
            await AnswersAsync(fixture.ExtraConnectionString(SqlServerFixture.SynonymUpdaterLogin)));
    }

    [Fact]
    public async Task Database_whose_name_needs_quoting_is_checked_not_skipped()
    {
        var reader = await CheckAsync(fixture.ConnectionString(SqlServerFixture.ReaderLogin, fixture.ReaderPassword, SqlServerFixture.QuotedDatabase));
        var creator = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.QuotedCreatorLogin, SqlServerFixture.QuotedDatabase));

        Assert.True(reader.Passed, string.Join(" | ", reader.Findings));
        Assert.Equal(SqlServerFixture.QuotedDatabase, reader.Database);

        // Without QUOTENAME, HAS_PERMS_BY_NAME reads "Odd.Name ]Db" as a two-part name and answers NULL.
        Assert.Equal(["CREATE TABLE on database Odd.Name ]Db"], creator.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Null_answer_is_a_finding_named_could_not_be_checked()
    {
        // The NULL path is the server's answer for a name it cannot parse; shown on the formatting here, and in the
        // query as ISNULL(state, 1) = 1.
        var finding = new PermissionFinding("CREATE TABLE", "database x", [], 0, Unchecked: true);

        Assert.Equal("CREATE TABLE on database x (could not be checked)", finding.ToString());
    }

    private static async Task<PermissionReport> CheckAsync(string connectionString)
    {
        var config = ServerConfig.Load(
            name => name == ServerConfig.ConnectionStringVariable ? connectionString : null, requireAuditLog: false);
        var gateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs);
        return await PermissionCheck.RunAsync(gateway, config.TimeoutSeconds, CancellationToken.None);
    }
}

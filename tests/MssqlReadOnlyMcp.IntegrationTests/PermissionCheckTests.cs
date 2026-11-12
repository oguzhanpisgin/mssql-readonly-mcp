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

    [Theory]
    [InlineData(SqlServerFixture.QueueReceiverLogin, "RECEIVE on queue [dbo].[InvoiceQueue]")]
    [InlineData(SqlServerFixture.ServiceSenderLogin, "SEND on service id {service_id}")]
    [InlineData(SqlServerFixture.ServiceRoleSenderLogin, "SEND on service id {service_id}")]
    [InlineData(SqlServerFixture.ServiceDoubleSenderLogin, "SEND on service id {service_id}")]
    [InlineData(SqlServerFixture.ShippedQueueReceiverLogin, "RECEIVE on queue [dbo].[QueryNotificationErrorsQueue]")]
    [InlineData(SqlServerFixture.ShippedAssemblyTakerLogin, "TAKE OWNERSHIP on assembly [Microsoft.SqlServer.Types]")]
    [InlineData(SqlServerFixture.ServiceAltererLogin, "ALTER on service [InvoiceService]")]
    [InlineData(
        SqlServerFixture.CertificateControllerLogin,
        "ALTER on certificate [ShopSigning] | CONTROL on certificate [ShopSigning] | TAKE OWNERSHIP on certificate [ShopSigning]")]
    [InlineData(
        SqlServerFixture.CertificateOwnerLogin,
        "ALTER on certificate [OwnedCertificate] | CONTROL on certificate [OwnedCertificate] | TAKE OWNERSHIP on certificate [OwnedCertificate]")]
    [InlineData(SqlServerFixture.AsymmetricKeyAltererLogin, "ALTER on asymmetric key [ShopAsymmetric]")]
    [InlineData(SqlServerFixture.SymmetricKeyTakerLogin, "TAKE OWNERSHIP on symmetric key [ShopSymmetric]")]
    [InlineData(SqlServerFixture.AssemblyAltererLogin, "ALTER on assembly [ShopTypes]")]
    [InlineData(SqlServerFixture.ClrTypeUserLogin, "EXECUTE on type [dbo].[Point]")]
    [InlineData(SqlServerFixture.XmlSchemaAltererLogin, "ALTER on xml schema collection [dbo].[OrderNotes]")]
    [InlineData(
        SqlServerFixture.CatalogControllerLogin,
        "ALTER on full-text catalog [ShopCatalog] | CONTROL on full-text catalog [ShopCatalog] | TAKE OWNERSHIP on full-text catalog [ShopCatalog]")]
    public async Task Right_on_a_queue_service_certificate_key_assembly_type_xml_schema_or_catalog_is_named(string login, string expected)
    {
        // A service the login holds only SEND on is not visible to it, so it is named by its id, once however many
        // grants give it SEND. A CONTROL holder or owner holds ALTER and TAKE OWNERSHIP too, and each is named. A
        // built-in queue or assembly is asked like a user one.
        var serviceId = await AdminScalarAsync(
            SqlServerFixture.Database, $"SELECT service_id FROM sys.services WHERE name = N'{SqlServerFixture.InvoiceService}'");
        var report = await CheckAsync(fixture.ExtraConnectionString(login));

        Assert.Equal(
            expected.Replace("{service_id}", serviceId, StringComparison.Ordinal).Split(" | "),
            report.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Login_with_only_the_allowed_rights_on_these_classes_passes()
    {
        // VIEW DEFINITION and REFERENCES on every class, and EXECUTE on the table type, the alias type and the XML
        // schema collection: none of them changes anything or runs code (measured).
        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.ClassReaderLogin));

        Assert.True(report.Passed, string.Join(" | ", report.Findings));
    }

    [Theory]
    [InlineData(SqlServerFixture.QueueReceiverLogin, "RECEIVE TOP (1) N'received' AS outcome FROM dbo.InvoiceQueue;", "received", "error 1088")]
    [InlineData(
        SqlServerFixture.ShippedQueueReceiverLogin,
        "DECLARE @h uniqueidentifier; " +
        "BEGIN DIALOG @h FROM SERVICE [" + SqlServerFixture.QueryNotificationService + "] TO SERVICE N'InvoiceService' " +
        "ON CONTRACT [DEFAULT] WITH ENCRYPTION = OFF; " +
        "SEND ON CONVERSATION @h (CONVERT(varbinary(max), N'notification sent')); " +
        "SELECT CONVERT(nvarchar(100), message_body) FROM dbo.InvoiceQueue " +
        "WHERE message_body = CONVERT(varbinary(max), N'notification sent');",
        "notification sent", "error 1088")]
    [InlineData(
        SqlServerFixture.ShippedAssemblyTakerLogin,
        "ALTER AUTHORIZATION ON ASSEMBLY::[Microsoft.SqlServer.Types] TO [mcp_shipped_assembly_taker]; " +
        "SELECT USER_NAME(principal_id) FROM sys.assemblies WHERE name = N'Microsoft.SqlServer.Types';",
        "mcp_shipped_assembly_taker", "error 15151")]
    [InlineData(
        SqlServerFixture.ServiceAltererLogin,
        "ALTER SERVICE [InvoiceService] (DROP CONTRACT [DEFAULT]); SELECT N'altered';", "altered", "error 15151")]
    [InlineData(
        SqlServerFixture.CertificateControllerLogin,
        "SELECT CASE WHEN SIGNBYCERT(CERT_ID(N'ShopSigning'), N'invoice') IS NULL THEN N'no signature' ELSE N'signed' END;",
        "signed", "no signature")]
    [InlineData(
        SqlServerFixture.CertificateOwnerLogin,
        "SELECT CASE WHEN SIGNBYCERT(CERT_ID(N'OwnedCertificate'), N'invoice') IS NULL THEN N'no signature' ELSE N'signed' END;",
        "signed", "no signature")]
    [InlineData(
        SqlServerFixture.CertificateOwnerLogin,
        "ALTER CERTIFICATE [OwnedCertificate] REMOVE PRIVATE KEY; SELECT N'altered';", "altered", "error 15151")]
    [InlineData(
        SqlServerFixture.AsymmetricKeyAltererLogin,
        "ALTER ASYMMETRIC KEY [ShopAsymmetric] REMOVE PRIVATE KEY; SELECT N'altered';", "altered", "error 15151")]
    [InlineData(
        SqlServerFixture.SymmetricKeyTakerLogin,
        "ALTER AUTHORIZATION ON SYMMETRIC KEY::[ShopSymmetric] TO [mcp_symmetric_key_taker]; " +
        "SELECT USER_NAME(principal_id) FROM sys.symmetric_keys WHERE name = N'ShopSymmetric';",
        "mcp_symmetric_key_taker", "error 15151")]
    [InlineData(
        SqlServerFixture.AssemblyAltererLogin,
        "ALTER ASSEMBLY [ShopTypes] ADD FILE FROM 0x00 AS N'note.txt'; SELECT N'altered';", "altered", "error 300")]
    [InlineData(
        SqlServerFixture.ClrTypeUserLogin,
        "DECLARE @p dbo.Point = CONVERT(dbo.Point, N'x'); SELECT @p.Twice();", "42", "error 229")]
    [InlineData(
        SqlServerFixture.XmlSchemaAltererLogin,
        "ALTER XML SCHEMA COLLECTION dbo.OrderNotes ADD N'<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
        "<xsd:element name=\"extra\" type=\"xsd:int\"/></xsd:schema>'; SELECT N'altered';",
        "altered", "error 229")]
    public async Task Right_found_on_these_classes_does_what_it_is_named_for_and_the_reader_cannot(
        string login, string act, string outcome, string readerOutcome)
    {
        // Each act runs in a transaction that is rolled back; the reader is the comparison. RECEIVE on a built-in
        // queue lets the login begin a dialog from that queue's built-in service, owned by dbo, and SEND puts the
        // message on a user queue (delivered inside the transaction, which is then rolled back).
        Assert.Equal(outcome, await RolledBackAsync(fixture.ExtraConnectionString(login), act));
        Assert.Equal(readerOutcome, await RolledBackAsync(fixture.ReaderConnectionString, act));
    }

    [Fact]
    public async Task Control_on_a_full_text_catalog_rebuilds_it_and_the_reader_cannot()
    {
        // ALTER FULLTEXT CATALOG cannot run in a transaction; the catalog is empty, so the rebuild leaves it as it was.
        const string act = "ALTER FULLTEXT CATALOG [ShopCatalog] REBUILD; SELECT N'rebuilt';";

        Assert.Equal("rebuilt", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.CatalogControllerLogin), act, rollBack: false));
        Assert.Equal("error 7641", await OutcomeAsync(fixture.ReaderConnectionString, act, rollBack: false));
    }

    [Fact]
    public async Task Send_on_a_service_lets_a_dialog_from_another_database_put_a_message_on_its_queue()
    {
        // Both logins begin a dialog to InvoiceService from a service they own in the TRUSTWORTHY broker database;
        // only the login holding SEND on InvoiceService gets its message onto dbo.InvoiceQueue. The other dialog is
        // answered with a Service Broker error message.
        var sent = $"sent {Guid.NewGuid():N}";
        var refused = $"refused {Guid.NewGuid():N}";
        await BeginDialogAsync(SqlServerFixture.ServiceSenderLogin, SqlServerFixture.SenderOutboxService, sent);
        await BeginDialogAsync(SqlServerFixture.ClassReaderLogin, SqlServerFixture.ReaderOutboxService, refused);

        string Count(string body) =>
            $"SELECT COUNT(*) FROM dbo.InvoiceQueue WHERE CONVERT(nvarchar(100), message_body) = N'{body}'";
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var arrived = await AdminScalarAsync(SqlServerFixture.Database, Count(sent));
            var errors = await AdminScalarAsync(
                SqlServerFixture.BrokerDatabase,
                "SELECT COUNT(*) FROM dbo.ReaderOutboxQueue WHERE message_type_name = N'http://schemas.microsoft.com/SQL/ServiceBroker/Error'");
            if (arrived == "1" && errors != "0")
            {
                break;
            }

            if (started.Elapsed > TimeSpan.FromSeconds(60))
            {
                var status = await AdminScalarAsync(
                    SqlServerFixture.BrokerDatabase,
                    "SELECT STRING_AGG(CONCAT(from_service_name, N': ', transmission_status), N' | ') FROM sys.transmission_queue");
                Assert.Fail($"After 60 seconds: sent message on the queue {arrived}, error answers {errors}; transmission queue: {status}");
            }

            await Task.Delay(250);
        }

        Assert.Equal("0", await AdminScalarAsync(SqlServerFixture.Database, Count(refused)));
    }

    [Theory]
    [InlineData(
        SqlServerFixture.ShippedWriterLogin,
        SqlServerFixture.ShippedDatabase,
        "DELETE on object [dbo].[MarkedNotes] | EXECUTE on object [dbo].[AddLedger] | INSERT on object [dbo].[MarkedNotes]")]
    [InlineData(SqlServerFixture.CdcDeleterLogin, SqlServerFixture.ShippedDatabase, "DELETE on object [cdc].[ddl_history]")]
    [InlineData(SqlServerFixture.ServerRoleAltererLogin, SqlServerFixture.Database, "ALTER on server role [mcp_login_admins]")]
    [InlineData(SqlServerFixture.AppRoleAltererLogin, SqlServerFixture.Database, "ALTER on application role [reporting_app]")]
    public async Task Right_on_a_shipped_object_a_server_role_or_an_application_role_is_named(
        string login, string database, string expected)
    {
        // Objects SQL Server marks as shipped (is_ms_shipped = 1) are asked like any other: a procedure and a table
        // marked with sp_MS_marksystemobject, and a table change data capture adds.
        var report = await CheckAsync(fixture.ExtraConnectionString(login, database));

        Assert.Equal(expected.Split(" | "), report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(
        SqlServerFixture.ShippedWriterLogin, SqlServerFixture.ShippedDatabase,
        "EXEC dbo.AddLedger; SELECT COUNT(*) FROM dbo.Ledger;", "1", "error 229")]
    [InlineData(
        SqlServerFixture.ShippedWriterLogin, SqlServerFixture.ShippedDatabase,
        "INSERT INTO dbo.MarkedNotes (Note) VALUES (N'x'); SELECT N'inserted';", "inserted", "error 229")]
    [InlineData(
        SqlServerFixture.CdcDeleterLogin, SqlServerFixture.ShippedDatabase,
        "DELETE FROM cdc.ddl_history; SELECT N'deleted';", "deleted", "error 229")]
    [InlineData(
        SqlServerFixture.ServerRoleAltererLogin, SqlServerFixture.Database,
        "ALTER SERVER ROLE [mcp_login_admins] ADD MEMBER [mcp_server_role_alterer]; SELECT N'added';", "added", "error 15151")]
    [InlineData(
        SqlServerFixture.AppRoleAltererLogin, SqlServerFixture.Database,
        "ALTER APPLICATION ROLE [reporting_app] WITH DEFAULT_SCHEMA = [guest]; " +
        "SELECT default_schema_name FROM sys.database_principals WHERE name = N'reporting_app';",
        "guest", "error 15151")]
    public async Task Right_on_a_shipped_object_a_server_role_or_an_application_role_does_what_it_is_named_for_and_the_reader_cannot(
        string login, string database, string act, string outcome, string readerOutcome)
    {
        // Each act runs in a transaction that is rolled back. ALTER on a server role lets the login add itself to
        // it, and so gain the role's ALTER ANY LOGIN; ALTER on an application role changes its default schema.
        var reader = database == SqlServerFixture.Database
            ? fixture.ReaderConnectionString
            : fixture.ExtraConnectionString(SqlServerFixture.PlainReaderLogin, database);

        Assert.Equal(outcome, await RolledBackAsync(fixture.ExtraConnectionString(login, database), act));
        Assert.Equal(readerOutcome, await RolledBackAsync(reader, act));
    }

    [Theory]
    [InlineData("master")]
    [InlineData(SqlServerFixture.ShippedDatabase)]
    public async Task Plain_reader_passes_in_master_and_in_a_database_with_change_data_capture(string database)
    {
        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.PlainReaderLogin, database));

        Assert.Equal([], report.Findings.Select(f => f.ToString()));
        Assert.Equal(database, report.Database);
    }

    [Fact]
    public async Task Connected_to_msdb_every_login_is_refused_because_public_may_execute_shipped_procedures_and_functions()
    {
        // public holds EXECUTE on these shipped msdb procedures and functions, so every login with a user in msdb
        // holds it; sp_sysutility_cag_create_db is not in SQL Server 2019 (measured on 2019, 2022 and 2025).
        string[] all =
        [
            "[dbo].[fn_sysdac_is_currentuser_sa]", "[dbo].[fn_sysdac_is_dac_creator]", "[dbo].[fn_sysdac_is_login_creator]",
            "[dbo].[fn_syspolicy_is_automation_enabled]", "[dbo].[sp_sysdac_add_history_entry]",
            "[dbo].[sp_sysdac_add_instance]", "[dbo].[sp_sysdac_delete_history]", "[dbo].[sp_sysdac_delete_instance]",
            "[dbo].[sp_sysdac_drop_database]", "[dbo].[sp_sysdac_ensure_dac_creator]", "[dbo].[sp_sysdac_rename_database]",
            "[dbo].[sp_sysdac_update_history_entry]", "[dbo].[sp_sysdac_update_instance]",
            "[dbo].[sp_sysdac_upgrade_instance]", "[dbo].[sp_sysutility_cag_create_db]",
        ];
        var expected = fixture.MajorVersion == 15 ? all.Where(n => n != "[dbo].[sp_sysutility_cag_create_db]").ToArray() : all;
        var granted = await AdminScalarAsync(
            "msdb",
            "SELECT STRING_AGG(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N' | ') WITHIN GROUP (ORDER BY o.name) " +
            "FROM sys.database_permissions AS p JOIN sys.objects AS o ON o.object_id = p.major_id " +
            "WHERE p.class = 1 AND p.grantee_principal_id = 0 AND p.permission_name = N'EXECUTE' AND p.state IN ('G', 'W') " +
            "AND o.is_ms_shipped = 1");

        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.PlainReaderLogin, "msdb"));

        Assert.Equal(expected, granted.Split(" | "));
        var finding = Assert.Single(report.Findings);
        Assert.Equal(("EXECUTE", "object", false), (finding.Permission, finding.Scope, finding.Unchecked));
        Assert.Equal(expected.Take(PermissionFinding.MaxObjectNames), finding.Objects);
        Assert.Equal(expected.Length, finding.ObjectCount);
    }

    [Theory]
    [InlineData(SqlServerFixture.UserAltererLogin, SqlServerFixture.Database, "ALTER on user [mcp_target_user]")]
    [InlineData(SqlServerFixture.LoginAltererLogin, SqlServerFixture.Database, "ALTER on login [mcp_target_login]")]
    [InlineData(SqlServerFixture.EndpointAltererLogin, SqlServerFixture.Database, "ALTER on endpoint [TSQL Default TCP]")]
    [InlineData(SqlServerFixture.RouteAltererLogin, SqlServerFixture.Database, "ALTER on route [AutoCreatedLocal]")]
    [InlineData(SqlServerFixture.RouteRoleAltererLogin, SqlServerFixture.Database, "ALTER on route [AutoCreatedLocal]")]
    [InlineData(SqlServerFixture.NestedRouteAltererLogin, SqlServerFixture.Database, "ALTER on route [AutoCreatedLocal]")]
    [InlineData(SqlServerFixture.MessageTypeAltererLogin, SqlServerFixture.Database, "ALTER on message type [InvoiceMessage]")]
    [InlineData(SqlServerFixture.ContractTakerLogin, SqlServerFixture.Database, "TAKE OWNERSHIP on contract [InvoiceContract]")]
    [InlineData(SqlServerFixture.BindingAltererLogin, SqlServerFixture.Database, "ALTER on remote service binding [InvoiceBinding]")]
    [InlineData(SqlServerFixture.StoplistTakerLogin, SqlServerFixture.Database, "TAKE OWNERSHIP on full-text stoplist [ShopStoplist]")]
    [InlineData(SqlServerFixture.PropertyListAltererLogin, SqlServerFixture.Database, "ALTER on search property list [ShopProperties]")]
    [InlineData(SqlServerFixture.CredentialAltererLogin, SqlServerFixture.Database, "ALTER on database scoped credential [ShopCredential]")]
    [InlineData(SqlServerFixture.SystemProcedureRunnerLogin, "master", "EXECUTE on object [sys].[xp_regwrite]")]
    public async Task Right_found_in_the_permission_rows_the_login_can_see_is_named(string login, string database, string expected)
    {
        // The login's own grant, a grant to a role it is in (route_admins), or to a role that role is in (route_outer):
        // the row is read from sys.database_permissions or sys.server_permissions, whatever the class. Built-in
        // securables (the T-SQL endpoint, the local route, a system extended procedure in master) are asked like user
        // ones. A right the class rule also finds is named once.
        var report = await CheckAsync(fixture.ExtraConnectionString(login, database));

        Assert.Equal([expected], report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(
        SqlServerFixture.UserAltererLogin, SqlServerFixture.Database,
        "ALTER USER [mcp_target_user] WITH DEFAULT_SCHEMA = [guest]; " +
        "SELECT default_schema_name FROM sys.database_principals WHERE name = N'mcp_target_user';",
        "guest", "error 15151")]
    [InlineData(
        SqlServerFixture.LoginAltererLogin, SqlServerFixture.Database,
        "ALTER LOGIN [mcp_target_login] WITH DEFAULT_DATABASE = [tempdb]; " +
        "SELECT default_database_name FROM sys.server_principals WHERE name = N'mcp_target_login';",
        "tempdb", "error 15151")]
    [InlineData(
        SqlServerFixture.RouteAltererLogin, SqlServerFixture.Database,
        "ALTER ROUTE [AutoCreatedLocal] WITH ADDRESS = N'TCP://10.0.0.1:4022'; " +
        "SELECT address FROM sys.routes WHERE name = N'AutoCreatedLocal';",
        "TCP://10.0.0.1:4022", "error 15151")]
    [InlineData(
        SqlServerFixture.RouteRoleAltererLogin, SqlServerFixture.Database,
        "ALTER ROUTE [AutoCreatedLocal] WITH ADDRESS = N'TCP://10.0.0.1:4022'; " +
        "SELECT address FROM sys.routes WHERE name = N'AutoCreatedLocal';",
        "TCP://10.0.0.1:4022", "error 15151")]
    [InlineData(
        SqlServerFixture.MessageTypeAltererLogin, SqlServerFixture.Database,
        "ALTER MESSAGE TYPE [InvoiceMessage] VALIDATION = EMPTY; " +
        "SELECT validation_desc FROM sys.service_message_types WHERE name = N'InvoiceMessage';",
        "EMPTY", "error 15151")]
    [InlineData(
        SqlServerFixture.ContractTakerLogin, SqlServerFixture.Database,
        "ALTER AUTHORIZATION ON CONTRACT::[InvoiceContract] TO [mcp_contract_taker]; " +
        "SELECT USER_NAME(principal_id) FROM sys.service_contracts WHERE name = N'InvoiceContract';",
        "mcp_contract_taker", "error 15151")]
    [InlineData(
        SqlServerFixture.BindingAltererLogin, SqlServerFixture.Database,
        "ALTER REMOTE SERVICE BINDING [InvoiceBinding] WITH ANONYMOUS = OFF; " +
        "SELECT CONVERT(nvarchar(10), is_anonymous_on) FROM sys.remote_service_bindings WHERE name = N'InvoiceBinding';",
        "0", "error 15151")]
    [InlineData(
        SqlServerFixture.StoplistTakerLogin, SqlServerFixture.Database,
        "ALTER AUTHORIZATION ON FULLTEXT STOPLIST::[ShopStoplist] TO [mcp_stoplist_taker]; " +
        "SELECT USER_NAME(principal_id) FROM sys.fulltext_stoplists WHERE name = N'ShopStoplist';",
        "mcp_stoplist_taker", "error 15151")]
    [InlineData(
        SqlServerFixture.CredentialAltererLogin, SqlServerFixture.Database,
        "ALTER DATABASE SCOPED CREDENTIAL [ShopCredential] WITH IDENTITY = N'other'; " +
        "SELECT credential_identity FROM sys.database_scoped_credentials WHERE name = N'ShopCredential';",
        "other", "error 15151")]
    [InlineData(SqlServerFixture.SystemProcedureRunnerLogin, "master", "EXEC sys.xp_regwrite; SELECT N'ran';", "error 0", "error 229")]
    public async Task Right_found_in_the_permission_rows_does_what_it_is_named_for_and_the_reader_cannot(
        string login, string database, string act, string outcome, string readerOutcome)
    {
        // Each act runs in a transaction that is rolled back. xp_regwrite is called without arguments (no key or value
        // is named). The grantee passes the EXECUTE check and the procedure runs: sqlcmd shows no error, and this
        // client reports "a severe error occurred" with number 0 (measured). That 0 is Microsoft.Data.SqlClient's own
        // number for the severe error, not SQL Server's, and may change with a client upgrade. The reader is stopped
        // at the EXECUTE check (229).
        var reader = database == SqlServerFixture.Database
            ? fixture.ReaderConnectionString
            : fixture.ExtraConnectionString(SqlServerFixture.PlainReaderLogin, database);

        Assert.Equal(outcome, await RolledBackAsync(fixture.ExtraConnectionString(login, database), act));
        Assert.Equal(readerOutcome, await RolledBackAsync(reader, act));
    }

    [Fact]
    public async Task Alter_on_an_endpoint_and_on_a_search_property_list_runs_and_the_reader_cannot()
    {
        // Neither statement can run in a transaction. STATE = STARTED runs on the running endpoint; whether it changed
        // anything was not shown. The added search property is removed afterwards.
        const string endpoint = "ALTER ENDPOINT [TSQL Default TCP] STATE = STARTED; SELECT N'ran';";
        const string property =
            "ALTER SEARCH PROPERTY LIST [ShopProperties] ADD N'ProbeProperty' WITH (" +
            "PROPERTY_SET_GUID = 'F29F85E0-4FF9-1068-AB91-08002B27B3D9', PROPERTY_INT_ID = 99, PROPERTY_DESCRIPTION = N'probe'); " +
            "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM sys.registered_search_properties WHERE property_name = N'ProbeProperty';";
        const string removeProperty =
            "IF EXISTS (SELECT 1 FROM sys.registered_search_properties WHERE property_name = N'ProbeProperty') " +
            "ALTER SEARCH PROPERTY LIST [ShopProperties] DROP N'ProbeProperty'; SELECT N'removed';";

        Assert.Equal("ran", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.EndpointAltererLogin), endpoint, rollBack: false));
        Assert.Equal("error 6004", await OutcomeAsync(fixture.ReaderConnectionString, endpoint, rollBack: false));
        try
        {
            Assert.Equal("1", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.PropertyListAltererLogin), property, rollBack: false));
        }
        finally
        {
            await AdminScalarAsync(SqlServerFixture.Database, removeProperty);
        }

        Assert.Equal("error 30025", await OutcomeAsync(fixture.ReaderConnectionString, property, rollBack: false));
    }

    [Theory]
    [InlineData(SqlServerFixture.MessageTypeOwnerLogin, "message type [OwnedMessage]")]
    [InlineData(SqlServerFixture.ContractOwnerLogin, "contract [OwnedContract]")]
    [InlineData(SqlServerFixture.BindingOwnerLogin, "remote service binding [OwnedBinding]")]
    [InlineData(SqlServerFixture.RouteOwnerLogin, "route [OwnedRoute]")]
    [InlineData(SqlServerFixture.StoplistOwnerLogin, "full-text stoplist [OwnedStoplist]")]
    [InlineData(SqlServerFixture.PropertyListOwnerLogin, "search property list [OwnedProperties]")]
    [InlineData(SqlServerFixture.CredentialOwnerLogin, "database scoped credential [OwnedCredential]")]
    [InlineData(SqlServerFixture.EndpointOwnerLogin, "endpoint [mcp_owned_endpoint]")]
    public async Task Owner_of_a_securable_no_grant_row_shows_is_named(string login, string securable)
    {
        // An owner holds CONTROL without any grant row; the class rule asks every right the class lists, so the owner
        // is named for ALTER, CONTROL and TAKE OWNERSHIP, as the certificate owner is.
        var report = await CheckAsync(fixture.ExtraConnectionString(login));

        Assert.Equal(
            [$"ALTER on {securable}", $"CONTROL on {securable}", $"TAKE OWNERSHIP on {securable}"],
            report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(
        SqlServerFixture.MessageTypeOwnerLogin,
        "ALTER MESSAGE TYPE [OwnedMessage] VALIDATION = EMPTY; " +
        "SELECT validation_desc FROM sys.service_message_types WHERE name = N'OwnedMessage';",
        "EMPTY", "error 15151")]
    [InlineData(
        SqlServerFixture.ContractOwnerLogin,
        "DROP CONTRACT [OwnedContract]; " +
        "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM sys.service_contracts WHERE name = N'OwnedContract';",
        "0", "error 15151")]
    [InlineData(
        SqlServerFixture.BindingOwnerLogin,
        "ALTER REMOTE SERVICE BINDING [OwnedBinding] WITH ANONYMOUS = OFF; " +
        "SELECT CONVERT(nvarchar(10), is_anonymous_on) FROM sys.remote_service_bindings WHERE name = N'OwnedBinding';",
        "0", "error 15151")]
    [InlineData(
        SqlServerFixture.RouteOwnerLogin,
        "ALTER ROUTE [OwnedRoute] WITH ADDRESS = N'TCP://10.0.0.3:4022'; " +
        "SELECT address FROM sys.routes WHERE name = N'OwnedRoute';",
        "TCP://10.0.0.3:4022", "error 15151")]
    [InlineData(
        SqlServerFixture.CredentialOwnerLogin,
        "ALTER DATABASE SCOPED CREDENTIAL [OwnedCredential] WITH IDENTITY = N'other'; " +
        "SELECT credential_identity FROM sys.database_scoped_credentials WHERE name = N'OwnedCredential';",
        "other", "error 15151")]
    public async Task Owner_of_a_securable_no_grant_row_shows_changes_it_and_the_reader_cannot(
        string login, string act, string outcome, string readerOutcome)
    {
        // Each act runs in a transaction that is rolled back.
        Assert.Equal(outcome, await RolledBackAsync(fixture.ExtraConnectionString(login), act));
        Assert.Equal(readerOutcome, await RolledBackAsync(fixture.ReaderConnectionString, act));
    }

    [Fact]
    public async Task Owner_of_a_stoplist_a_search_property_list_and_an_endpoint_changes_it_and_the_reader_cannot()
    {
        // None of these statements can run in a transaction. Each change is undone afterwards as sa: the stoplist is
        // created again for its owner, the property removed, the endpoint's port set back. The reader tries first.
        const string drop =
            "DROP FULLTEXT STOPLIST [OwnedStoplist]; " +
            "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM sys.fulltext_stoplists WHERE name = N'OwnedStoplist';";
        const string restoreStoplist =
            "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_stoplists WHERE name = N'OwnedStoplist') BEGIN " +
            "CREATE FULLTEXT STOPLIST [OwnedStoplist]; " +
            "ALTER AUTHORIZATION ON FULLTEXT STOPLIST::[OwnedStoplist] TO [mcp_stoplist_owner]; END; SELECT N'restored';";
        const string property =
            "ALTER SEARCH PROPERTY LIST [OwnedProperties] ADD N'OwnerProbe' WITH (" +
            "PROPERTY_SET_GUID = 'F29F85E0-4FF9-1068-AB91-08002B27B3D9', PROPERTY_INT_ID = 98, PROPERTY_DESCRIPTION = N'probe'); " +
            "SELECT CONVERT(nvarchar(10), COUNT(*)) FROM sys.registered_search_properties WHERE property_name = N'OwnerProbe';";
        const string removeProperty =
            "IF EXISTS (SELECT 1 FROM sys.registered_search_properties WHERE property_name = N'OwnerProbe') " +
            "ALTER SEARCH PROPERTY LIST [OwnedProperties] DROP N'OwnerProbe'; SELECT N'removed';";
        const string port =
            "ALTER ENDPOINT [mcp_owned_endpoint] AS TCP (LISTENER_PORT = 4098); " +
            "SELECT CONVERT(nvarchar(10), port) FROM sys.tcp_endpoints WHERE name = N'mcp_owned_endpoint';";
        const string restorePort = "ALTER ENDPOINT [mcp_owned_endpoint] AS TCP (LISTENER_PORT = 4099); SELECT N'restored';";

        Assert.Equal("error 30047", await OutcomeAsync(fixture.ReaderConnectionString, drop, rollBack: false));
        Assert.Equal("error 30025", await OutcomeAsync(fixture.ReaderConnectionString, property, rollBack: false));
        Assert.Equal("error 6004", await OutcomeAsync(fixture.ReaderConnectionString, port, rollBack: false));
        try
        {
            Assert.Equal("0", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.StoplistOwnerLogin), drop, rollBack: false));
            Assert.Equal("1", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.PropertyListOwnerLogin), property, rollBack: false));
            Assert.Equal("4098", await OutcomeAsync(fixture.ExtraConnectionString(SqlServerFixture.EndpointOwnerLogin), port, rollBack: false));
        }
        finally
        {
            await AdminScalarAsync(SqlServerFixture.Database, restoreStoplist);
            await AdminScalarAsync(SqlServerFixture.Database, removeProperty);
            await AdminScalarAsync("master", restorePort);
        }
    }

    [Fact]
    public async Task Member_of_a_role_that_owns_a_schema_is_named_for_the_schema()
    {
        // The role owns the schema; its member holds CONTROL on it through the role, with no grant row.
        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.RoleSchemaOwnerLogin));

        Assert.Equal(
            [
                "ALTER on schema [role_owned]", "CONTROL on schema [role_owned]", "CREATE SEQUENCE on schema [role_owned]",
                "DELETE on schema [role_owned]", "EXECUTE on schema [role_owned]", "INSERT on schema [role_owned]",
                "TAKE OWNERSHIP on schema [role_owned]", "UPDATE on schema [role_owned]",
            ],
            report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(SqlServerFixture.RoleMessageTypeOwnerLogin, "message type [RoleOwnedMessage]")]
    [InlineData(SqlServerFixture.RoleStoplistOwnerLogin, "full-text stoplist [RoleOwnedStoplist]")]
    [InlineData(SqlServerFixture.RoleEndpointOwnerLogin, "endpoint [mcp_role_owned_endpoint]")]
    [InlineData(SqlServerFixture.RoleServerRoleOwnerLogin, "server role [mcp_role_owned_role]")]
    public async Task Member_of_a_role_that_owns_a_securable_is_named_for_it(string login, string securable)
    {
        // A database role owns the message type and the stoplist; a server role owns the endpoint and the server role.
        var report = await CheckAsync(fixture.ExtraConnectionString(login));

        Assert.Equal(
            [$"ALTER on {securable}", $"CONTROL on {securable}", $"TAKE OWNERSHIP on {securable}"],
            report.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Allowed_right_public_holds_with_grant_option_is_named_with_it()
    {
        // public holds SELECT and INSERT on the table WITH GRANT OPTION; the login has no grant of its own.
        var report = await CheckAsync(
            fixture.ExtraConnectionString(SqlServerFixture.PublicGrantReaderLogin, SqlServerFixture.PublicGrantDatabase));

        Assert.Contains("SELECT WITH GRANT OPTION on object [dbo].[PublicNotes]", report.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public async Task Refused_right_public_holds_with_grant_option_keeps_its_one_finding()
    {
        var report = await CheckAsync(
            fixture.ExtraConnectionString(SqlServerFixture.PublicGrantReaderLogin, SqlServerFixture.PublicGrantDatabase));

        Assert.Equal(
            ["INSERT on object [dbo].[PublicNotes]", "SELECT WITH GRANT OPTION on object [dbo].[PublicNotes]"],
            report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(SqlServerFixture.SchemaGrantorLogin, "SELECT WITH GRANT OPTION on schema [dbo]")]
    [InlineData(SqlServerFixture.RoleSchemaGrantorLogin, "SELECT WITH GRANT OPTION on schema [dbo]")]
    [InlineData(SqlServerFixture.ObjectGrantorLogin, "SELECT WITH GRANT OPTION on object [dbo].[Customers]")]
    [InlineData(SqlServerFixture.RoleObjectGrantorLogin, "SELECT WITH GRANT OPTION on object [dbo].[Customers]")]
    [InlineData(SqlServerFixture.ColumnGrantorLogin, "SELECT WITH GRANT OPTION on column [dbo].[Customers].[City]")]
    [InlineData(SqlServerFixture.RoleColumnGrantorLogin, "SELECT WITH GRANT OPTION on column [dbo].[Customers].[City]")]
    [InlineData(SqlServerFixture.DatabaseGrantorLogin, "CONNECT WITH GRANT OPTION on database ShopDb")]
    [InlineData(SqlServerFixture.RoleDatabaseGrantorLogin, "CONNECT WITH GRANT OPTION on database ShopDb")]
    [InlineData(SqlServerFixture.ServerGrantorLogin, "VIEW ANY DATABASE WITH GRANT OPTION on server")]
    [InlineData(SqlServerFixture.RoleServerGrantorLogin, "VIEW ANY DATABASE WITH GRANT OPTION on server")]
    [InlineData(SqlServerFixture.DatabaseSelectGrantorLogin, "SELECT on database ShopDb")]
    [InlineData(SqlServerFixture.InsertGrantorLogin, "INSERT on object [dbo].[Customers]")]
    public async Task Right_held_with_grant_option_is_named_with_it_when_the_right_itself_is_allowed(string login, string expected)
    {
        // A right the check allows, held WITH GRANT OPTION by the login or a role it is in, lets the login grant it on;
        // a right the check refuses keeps its one finding.
        var report = await CheckAsync(fixture.ExtraConnectionString(login));

        Assert.Equal([expected], report.Findings.Select(f => f.ToString()));
    }

    [Theory]
    [InlineData(SqlServerFixture.SchemaGrantorLogin, "GRANT SELECT ON SCHEMA::dbo TO guest;", "error 15151")]
    [InlineData(SqlServerFixture.RoleSchemaGrantorLogin, "GRANT SELECT ON SCHEMA::dbo TO guest AS [schema_grantors];", "error 15151")]
    [InlineData(SqlServerFixture.ObjectGrantorLogin, "GRANT SELECT ON dbo.Customers TO guest;", "error 15151")]
    [InlineData(SqlServerFixture.RoleObjectGrantorLogin, "GRANT SELECT ON dbo.Customers TO guest AS [object_grantors];", "error 15151")]
    [InlineData(SqlServerFixture.ColumnGrantorLogin, "GRANT SELECT ON dbo.Customers (City) TO guest;", "error 15151")]
    [InlineData(
        SqlServerFixture.RoleColumnGrantorLogin, "GRANT SELECT ON dbo.Customers (City) TO guest AS [column_grantors];", "error 15151")]
    [InlineData(SqlServerFixture.DatabaseGrantorLogin, "GRANT CONNECT TO guest;", "error 4613")]
    [InlineData(SqlServerFixture.RoleDatabaseGrantorLogin, "GRANT CONNECT TO guest AS [database_grantors];", "error 15151")]
    public async Task Grant_option_lets_the_login_grant_the_right_in_the_database_and_the_reader_cannot(
        string login, string grant, string readerOutcome)
    {
        // Rolled back. The grantee sees the new grant row to guest.
        var act = grant + " SELECT CONVERT(nvarchar(10), COUNT(*)) FROM sys.database_permissions " +
                  "WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'guest');";

        Assert.Equal("1", await RolledBackAsync(fixture.ExtraConnectionString(login), act));
        Assert.Equal(readerOutcome, await RolledBackAsync(fixture.ReaderConnectionString, act));
    }

    [Theory]
    [InlineData(SqlServerFixture.ServerGrantorLogin, "GRANT VIEW ANY DATABASE TO [mcp_target_login];", "error 4613")]
    [InlineData(
        SqlServerFixture.RoleServerGrantorLogin, "GRANT VIEW ANY DATABASE TO [mcp_target_login] AS [mcp_database_listers];",
        "error 15151")]
    public async Task Grant_option_lets_the_login_grant_the_right_on_the_server_and_the_reader_cannot(
        string login, string grant, string readerOutcome)
    {
        // Rolled back. A server-scope GRANT runs only in master (error 4621 elsewhere); the grantors enter it as guest.
        var act = grant + " SELECT N'granted';";

        Assert.Equal("granted", await RolledBackAsync(fixture.ExtraConnectionString(login, "master"), act));
        Assert.Equal(
            readerOutcome, await RolledBackAsync(fixture.ExtraConnectionString(SqlServerFixture.PlainReaderLogin, "master"), act));
    }

    [Fact]
    public async Task Login_that_sees_other_logins_grants_on_a_user_an_application_role_and_a_login_passes()
    {
        // VIEW DEFINITION on a user, an application role or a login shows the login every grant on it, also those
        // to principals it cannot see (measured). Such a row is decided by what the login holds itself
        // (HAS_PERMS_BY_NAME), so these rows are not findings.
        var report = await CheckAsync(fixture.ExtraConnectionString(SqlServerFixture.PrincipalViewerLogin));

        Assert.Equal([], report.Findings.Select(f => f.ToString()));
    }

    private async Task BeginDialogAsync(string login, string fromService, string body)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            fixture.ExtraConnectionString(login, SqlServerFixture.BrokerDatabase));
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"""
            DECLARE @dialog uniqueidentifier;
            BEGIN DIALOG @dialog FROM SERVICE [{fromService}] TO SERVICE N'{SqlServerFixture.InvoiceService}'
                ON CONTRACT [DEFAULT] WITH ENCRYPTION = OFF;
            SEND ON CONVERSATION @dialog (@body);
            """,
            connection);
        command.Parameters.Add("@body", System.Data.SqlDbType.NVarChar, 100).Value = body;
        await command.ExecuteNonQueryAsync();
    }

    private Task<string> RolledBackAsync(string connectionString, string sql) => OutcomeAsync(connectionString, sql, rollBack: true);

    /// <summary>The first value <paramref name="sql"/> returns, or <c>error N</c> with SQL Server's error number.</summary>
    private static async Task<string> OutcomeAsync(string connectionString, string sql, bool rollBack)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        // Disposing a transaction that was not committed rolls it back.
        await using var transaction = rollBack ? connection.BeginTransaction() : null;
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection, transaction);
        try
        {
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Microsoft.Data.SqlClient.SqlException e)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"error {e.Number}");
        }
    }

    private async Task<string> AdminScalarAsync(string database, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString("sa", fixture.SaPassword, database));
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    private static async Task<PermissionReport> CheckAsync(string connectionString)
    {
        var config = ServerConfig.Load(
            name => name == ServerConfig.ConnectionStringVariable ? connectionString : null, requireAuditLog: false);
        var gateway = new SqlGateway(config.ConnectionString, config.LockTimeoutMs);
        return await PermissionCheck.RunAsync(gateway, config.TimeoutSeconds, CancellationToken.None);
    }
}

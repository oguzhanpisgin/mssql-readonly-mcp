using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MssqlReadOnlyMcp.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}

/// <summary>
/// One SQL Server container for the whole test run, seeded with two databases and five logins. Passwords are random
/// per run and are never written to a file. When Docker is not reachable, starting the container throws and every
/// test in the collection fails; nothing is skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>The image used when <see cref="ImageVariable"/> is not set.</summary>
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    /// <summary>Environment variable naming another SQL Server image, for example a 2019 CU tag.</summary>
    public const string ImageVariable = "MSSQL_TEST_IMAGE";

    public const string Database = "ShopDb";
    public const string OtherDatabase = "ArchiveDb";

    /// <summary>
    /// The database the gate's regression table is written against (its rows use <c>DefaultDatabase = "Sales"</c>),
    /// plus the xml, hierarchyid and lock test tables.
    /// </summary>
    public const string SalesDatabase = "Sales";

    /// <summary>The Extended Events session that records what the reader login sends (created stopped).</summary>
    public const string TraceSession = "mcp_reader_trace";

    /// <summary>The application name of the trace marker connection; its events are not the server's.</summary>
    public const string MarkerApplicationName = "xe-marker";

    /// <summary>
    /// The reader the server runs as: SELECT granted on schema dbo, no fixed role. A db_datareader member cannot be
    /// used, because SQL Server lets every member of a fixed role create sequences in the schema that role owns
    /// (see <see cref="DataReaderLogin"/>).
    /// </summary>
    public const string ReaderLogin = "mcp_reader";

    /// <summary>A db_datareader-only member; the permission check refuses it for CONTROL on schema db_datareader.</summary>
    public const string DataReaderLogin = "mcp_datareader";
    public const string UpdaterLogin = "mcp_updater";
    public const string WriterLogin = "mcp_writer";
    public const string OwnerLogin = "mcp_owner";

    /// <summary>SELECT on schema dbo plus UPDATE on one column: <c>GRANT UPDATE ON dbo.Customers (City)</c>.</summary>
    public const string ColumnUpdaterLogin = "mcp_column_updater";

    /// <summary>SELECT on schema dbo plus IMPERSONATE on the user of <see cref="ReaderLogin"/>.</summary>
    public const string ImpersonatorLogin = "mcp_impersonator";

    /// <summary>SELECT on schema dbo plus ALTER ANY ROLE.</summary>
    public const string RoleAdminLogin = "mcp_role_admin";

    /// <summary>SELECT on schema dbo plus membership of the fixed server role securityadmin.</summary>
    public const string SecurityAdminLogin = "mcp_securityadmin";

    /// <summary>SELECT on schema dbo plus EXECUTE on the procedure dbo.AddCustomer.</summary>
    public const string ProcedureRunnerLogin = "mcp_procedure_runner";

    /// <summary>SELECT on schema dbo plus UPDATE on the sequence dbo.InvoiceNumbers.</summary>
    public const string SequenceUserLogin = "mcp_sequence_user";

    /// <summary>Member of db_denydatawriter only.</summary>
    public const string DenyWriterLogin = "mcp_denywriter";

    /// <summary>In <see cref="QuotedDatabase"/>: SELECT on schema dbo plus CREATE TABLE.</summary>
    public const string QuotedCreatorLogin = "mcp_quoted_creator";

    /// <summary>SELECT on schema dbo plus the server permission ALTER TRACE (not in the old server list).</summary>
    public const string TracerLogin = "mcp_tracer";

    /// <summary>SELECT on schema dbo plus the database permission BACKUP DATABASE (not in the old database patterns).</summary>
    public const string BackupLogin = "mcp_backup";

    /// <summary>SELECT on schema dbo plus UNMASK, a right that only widens reading.</summary>
    public const string UnmaskLogin = "mcp_unmasker";

    /// <summary>SELECT on schema dbo plus TAKE OWNERSHIP on the role <see cref="ReportingRole"/>.</summary>
    public const string RoleTakerLogin = "mcp_role_taker";

    /// <summary>
    /// SELECT on schema dbo plus UPDATE on the synonym <see cref="CustomerSynonym"/> only (none on the table it names).
    /// </summary>
    public const string SynonymUpdaterLogin = "mcp_synonym_updater";

    /// <summary>In <see cref="Database"/>: a synonym for dbo.Customers, owned like the table (dbo).</summary>
    public const string CustomerSynonym = "dbo.CustomerAlias";

    /// <summary>A reader like <see cref="ReaderLogin"/> (SELECT on schema dbo) with <c>DENY VIEW ANY DATABASE</c>.</summary>
    public const string NoDatabaseListLogin = "mcp_no_db_list";

    /// <summary>A user-defined database role in <see cref="Database"/>, with no members.</summary>
    public const string ReportingRole = "reporting";

    /// <summary>A database whose name needs quoting: a dot, a space and a closing bracket.</summary>
    public const string QuotedDatabase = "Odd.Name ]Db";

    /// <summary>The database the setup script test grants on; it holds the script's three example tables.</summary>
    public const string ExampleDatabase = "ExampleDb";

    /// <summary>In <see cref="SalesDatabase"/>: a table in schema hr, on which the reader has no grant.</summary>
    public const string HiddenTable = "hr.Payroll";

    /// <summary>In <see cref="SalesDatabase"/>: a table with a Turkish name, which stays a valid argument.</summary>
    public const string TurkishTable = "dbo.Müşteri_Şubeleri";

    /// <summary>In <see cref="SalesDatabase"/>: a table with 40 columns, whose description passes a 1000-character cap.</summary>
    public const string WideTable = "dbo.WideTable";

    /// <summary>Logins that can write or act as someone else in other ways than the five above; each is refused.</summary>
    public static readonly IReadOnlyList<string> ExtraLogins =
    [
        ColumnUpdaterLogin, ImpersonatorLogin, RoleAdminLogin, SecurityAdminLogin, ProcedureRunnerLogin, SequenceUserLogin,
        DenyWriterLogin, QuotedCreatorLogin, TracerLogin, BackupLogin, UnmaskLogin, RoleTakerLogin, NoDatabaseListLogin,
        SynonymUpdaterLogin,
    ];

    /// <summary>Every seeded user table, with the database it lives in.</summary>
    public static readonly (string Database, string Table)[] SeededTables =
    [
        (Database, "dbo.Customers"),
        (Database, "dbo.Orders"),
        (Database, "dbo.OrderLines"),
        (OtherDatabase, "dbo.ArchivedOrders"),
    ];

    private MsSqlContainer? container;

    public string SaPassword { get; } = NewPassword();

    public string ReaderPassword { get; } = NewPassword();

    public string DataReaderPassword { get; } = NewPassword();

    public string UpdaterPassword { get; } = NewPassword();

    public string WriterPassword { get; } = NewPassword();

    public string OwnerPassword { get; } = NewPassword();

    /// <summary>The password of the ERP-scale reader, whose login <see cref="ErpScaleTests"/> creates.</summary>
    public string ErpReaderPassword { get; } = NewPassword();

    /// <summary>The password the setup script test gives the login the script creates.</summary>
    public string ScriptLoginPassword { get; } = NewPassword();

    /// <summary>The password of each of <see cref="ExtraLogins"/>.</summary>
    public IReadOnlyDictionary<string, string> ExtraPasswords { get; } = ExtraLogins.ToDictionary(l => l, _ => NewPassword());

    /// <summary>The image this run uses.</summary>
    public string Image { get; } = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>SQL Server's major version (15 = 2019, 16 = 2022), read after start.</summary>
    public int MajorVersion { get; private set; }

    /// <summary>A folder for this run's audit files; removed at the end.</summary>
    public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "mssql-readonly-mcp-tests", Guid.NewGuid().ToString("N"));

    public string SaConnectionString => ConnectionString("sa", SaPassword);

    public string ReaderConnectionString => ConnectionString(ReaderLogin, ReaderPassword);

    /// <summary>The reader, connected to <see cref="SalesDatabase"/>.</summary>
    public string SalesReaderConnectionString => ConnectionString(ReaderLogin, ReaderPassword, SalesDatabase);

    public string DataReaderConnectionString => ConnectionString(DataReaderLogin, DataReaderPassword);

    public string UpdaterConnectionString => ConnectionString(UpdaterLogin, UpdaterPassword);

    public string WriterConnectionString => ConnectionString(WriterLogin, WriterPassword);

    public string OwnerConnectionString => ConnectionString(OwnerLogin, OwnerPassword);

    /// <summary>Every password of this run, for the leak checks.</summary>
    public IReadOnlyList<string> Secrets =>
        [SaPassword, ReaderPassword, DataReaderPassword, UpdaterPassword, WriterPassword, OwnerPassword, ErpReaderPassword,
         ScriptLoginPassword, .. ExtraPasswords.Values];

    /// <summary><paramref name="name"/> as a bracketed T-SQL identifier.</summary>
    public static string Bracket(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>The connection string of one of <see cref="ExtraLogins"/>.</summary>
    public string ExtraConnectionString(string login, string database = Database) =>
        ConnectionString(login, ExtraPasswords[login], database);

    /// <summary>Copies <paramref name="content"/> to <paramref name="path"/> inside the SQL Server container.</summary>
    public Task CopyToContainerAsync(byte[] content, string path) => container!.CopyAsync(content, path);

    /// <summary>Runs a command inside the SQL Server container.</summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> ExecInContainerAsync(IList<string> command) =>
        container!.ExecAsync(command);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WorkDirectory);
        container = new MsSqlBuilder(Image).WithPassword(SaPassword).Build();
        await container.StartAsync();
        MajorVersion = int.Parse(
            await ScalarAsync("master", "SELECT CONVERT(nvarchar(10), SERVERPROPERTY('ProductMajorVersion'))"),
            System.Globalization.CultureInfo.InvariantCulture);

        foreach (var batch in SeedBatches())
        {
            await ExecuteAsAdminAsync(batch.Database, batch.Sql);
        }
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }

        try
        {
            Directory.Delete(WorkDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a child process; the folder is under the temp directory.
        }
    }

    public string ConnectionString(string user, string password, string database = Database)
    {
        var builder = new SqlConnectionStringBuilder(container!.GetConnectionString())
        {
            UserID = user,
            Password = password,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Pooling = true,
        };
        return builder.ConnectionString;
    }

    public string NewAuditPath() => Path.Combine(WorkDirectory, $"audit-{Guid.NewGuid():N}.jsonl");

    /// <summary>Runs administrative SQL as <c>sa</c> (test setup and fingerprints only).</summary>
    public async Task ExecuteAsAdminAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString("sa", SaPassword, database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Row count and aggregate checksum of every seeded table, the sequence's current value and the number of user
    /// objects in both databases, read as <c>sa</c>.
    /// </summary>
    public async Task<string> FingerprintAsync()
    {
        var parts = new List<string>();
        foreach (var (database, table) in SeededTables)
        {
            parts.Add($"{database}.{table}=" + await ScalarAsync(
                database, $"SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM {table}"));
        }

        parts.Add("sequence=" + await ScalarAsync(
            Database, "SELECT CONVERT(nvarchar(40), current_value) FROM sys.sequences WHERE name = N'InvoiceNumbers'"));
        foreach (var database in new[] { Database, OtherDatabase })
        {
            parts.Add($"{database}.objects=" + await ScalarAsync(
                database, "SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(name, type))) FROM sys.objects WHERE is_ms_shipped = 0"));
        }

        parts.Add("logins=" + await ScalarAsync(
            "master", "SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(name))) FROM sys.server_principals"));
        return string.Join("; ", parts);
    }

    /// <summary>Starts the reader trace; the ring buffer starts empty.</summary>
    public Task StartTraceAsync() =>
        ExecuteAsAdminAsync("master", $"ALTER EVENT SESSION [{TraceSession}] ON SERVER STATE = START;");

    /// <summary>Stops the reader trace, which discards its ring buffer.</summary>
    public Task StopTraceAsync() =>
        ExecuteAsAdminAsync("master", $"ALTER EVENT SESSION [{TraceSession}] ON SERVER STATE = STOP;");

    /// <summary>
    /// Sends a marker batch as the reader from a fresh, unpooled connection named <see cref="MarkerApplicationName"/>,
    /// waits until the marker is in the ring buffer (so every earlier event has been dispatched too), and returns
    /// every event recorded since the trace started, the marker's own included.
    /// </summary>
    public async Task<IReadOnlyList<TraceEvent>> ReadTraceAsync()
    {
        var marker = $"xe-marker-{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(ReaderConnectionString) { ApplicationName = MarkerApplicationName, Pooling = false };
        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"SELECT N'{marker}'", connection);
            await command.ExecuteScalarAsync();
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var events = await ReadRingBufferAsync();
            if (events.Any(e => e.Name == "sql_batch_starting" && e.Text is not null && e.Text.Contains(marker, StringComparison.Ordinal)))
            {
                return events;
            }

            if (started.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"The trace marker did not reach the ring buffer within 30 seconds; {events.Count} events seen.");
            }

            await Task.Delay(250);
        }
    }

    private async Task<IReadOnlyList<TraceEvent>> ReadRingBufferAsync()
    {
        var xml = await ScalarAsync("master", $"""
            SELECT t.target_data
            FROM sys.dm_xe_session_targets AS t
            JOIN sys.dm_xe_sessions AS s ON s.address = t.event_session_address
            WHERE s.name = N'{TraceSession}' AND t.target_name = N'ring_buffer'
            """);
        var root = System.Xml.Linq.XElement.Parse(xml);
        return root.Elements("event")
            .Select(e =>
            {
                string? Field(string element, string name) =>
                    e.Elements(element).FirstOrDefault(d => (string?)d.Attribute("name") == name)?.Element("value")?.Value;
                return new TraceEvent(
                    (string?)e.Attribute("name") ?? string.Empty,
                    Field("action", "client_app_name"),
                    Field("action", "server_principal_name"),
                    Field("data", "batch_text") ?? Field("data", "statement"));
            })
            .ToList();
    }

    private async Task<string> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString("sa", SaPassword, database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    private static string NewPassword()
    {
        // Letters, digits and two symbols: meets the SQL Server complexity policy and needs no quoting anywhere.
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "_-";
        const string all = upper + lower + digits;
        var chars = new StringBuilder();
        chars.Append(upper[RandomNumberGenerator.GetInt32(upper.Length)]);
        for (var i = 0; i < 20; i++)
        {
            chars.Append(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        chars.Append(lower[RandomNumberGenerator.GetInt32(lower.Length)]);
        chars.Append(digits[RandomNumberGenerator.GetInt32(digits.Length)]);
        chars.Append(symbols[RandomNumberGenerator.GetInt32(symbols.Length)]);
        chars.Append(all[RandomNumberGenerator.GetInt32(all.Length)]);
        return chars.ToString();
    }

    private static string WideTableSql()
    {
        var columns = Enumerable.Range(1, 40)
            .Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ColumnWithAVeryLongDescriptiveName_{i:D2} int NULL"));
        return $"CREATE TABLE dbo.WideTable (Id int NOT NULL PRIMARY KEY, {string.Join(", ", columns)});";
    }

    private IEnumerable<(string Database, string Sql)> SeedBatches()
    {
        yield return ("master", $"CREATE DATABASE [{Database}]; CREATE DATABASE [{OtherDatabase}]; CREATE DATABASE [{SalesDatabase}]; " +
            $"CREATE DATABASE {Bracket(QuotedDatabase)}; CREATE DATABASE [{ExampleDatabase}];");

        yield return (Database, """
            CREATE TABLE dbo.Customers (
                CustomerId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                Name nvarchar(100) NOT NULL,
                City nvarchar(50) NULL,
                Notes nvarchar(max) NULL,
                Photo varbinary(max) NULL);
            CREATE TABLE dbo.Orders (
                OrderId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
                CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (CustomerId),
                OrderedAt datetime2(0) NOT NULL,
                Total decimal(12, 2) NOT NULL);
            CREATE TABLE dbo.OrderLines (
                OrderId int NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES dbo.Orders (OrderId),
                LineNumber int NOT NULL,
                Product nvarchar(80) NOT NULL,
                Quantity int NOT NULL,
                UnitPrice decimal(10, 2) NOT NULL,
                LineTotal AS (Quantity * UnitPrice),
                CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));
            CREATE SEQUENCE dbo.InvoiceNumbers AS int START WITH 1000;
            """);

        yield return (Database, """
            CREATE VIEW dbo.CustomerTotals AS
            SELECT c.CustomerId, c.Name, SUM(o.Total) AS Total
            FROM dbo.Customers AS c JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId
            GROUP BY c.CustomerId, c.Name;
            """);

        yield return (Database, """
            CREATE PROCEDURE dbo.AddCustomer @name nvarchar(100) AS
            INSERT INTO dbo.Customers (Name) VALUES (@name);
            """);

        yield return (Database, """
            WITH n AS (SELECT TOP (120) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Customers (Name, City, Notes, Photo)
            SELECT
                CONCAT(f.v, N' ', l.v, N' ', n.i),
                c.v,
                CASE WHEN n.i <= 3 THEN REPLICATE(CAST(N'ğüşiöç İĞÜŞÖÇ ' AS nvarchar(max)), 400) ELSE N'Kısa not' END,
                CASE WHEN n.i <= 3 THEN CAST(REPLICATE(CAST('x' AS varchar(max)), 5000) AS varbinary(max)) END
            FROM n
            CROSS APPLY (SELECT CASE n.i % 5 WHEN 0 THEN N'Çağla' WHEN 1 THEN N'İsmail' WHEN 2 THEN N'Şule'
                                             WHEN 3 THEN N'Ömer' ELSE N'Gülşen' END) AS f(v)
            CROSS APPLY (SELECT CASE n.i % 4 WHEN 0 THEN N'Öztürk' WHEN 1 THEN N'Yıldız' WHEN 2 THEN N'Işık'
                                             ELSE N'Doğan' END) AS l(v)
            CROSS APPLY (SELECT CASE n.i % 3 WHEN 0 THEN N'İstanbul' WHEN 1 THEN N'İzmir' ELSE N'Muğla' END) AS c(v);

            WITH n AS (SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Orders (CustomerId, OrderedAt, Total)
            SELECT (n.i % 120) + 1, DATEADD(day, n.i, '2026-01-01'), CAST(n.i * 7.25 AS decimal(12, 2))
            FROM n;

            WITH n AS (SELECT TOP (600) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.OrderLines (OrderId, LineNumber, Product, Quantity, UnitPrice)
            SELECT ((n.i - 1) / 2) + 1, ((n.i - 1) % 2) + 1,
                   CASE n.i % 3 WHEN 0 THEN N'Çay bardağı' WHEN 1 THEN N'Şeker' ELSE N'Gözlük' END,
                   (n.i % 5) + 1, CAST((n.i % 40) + 0.5 AS decimal(10, 2))
            FROM n;
            """);

        yield return (OtherDatabase, """
            CREATE TABLE dbo.ArchivedOrders (Id int NOT NULL PRIMARY KEY, Note nvarchar(50) NOT NULL);
            INSERT INTO dbo.ArchivedOrders (Id, Note)
            SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Arşiv' FROM sys.all_objects;
            """);

        var logins = new StringBuilder();
        foreach (var (login, password) in new[]
        {
            (ReaderLogin, ReaderPassword), (DataReaderLogin, DataReaderPassword), (UpdaterLogin, UpdaterPassword), (WriterLogin, WriterPassword), (OwnerLogin, OwnerPassword),
        })
        {
            logins.Append($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', DEFAULT_DATABASE = [{Database}];\n");
        }

        foreach (var login in ExtraLogins)
        {
            logins.Append($"CREATE LOGIN [{login}] WITH PASSWORD = N'{ExtraPasswords[login]}', DEFAULT_DATABASE = [{Database}];\n");
        }

        logins.Append($"ALTER SERVER ROLE securityadmin ADD MEMBER [{SecurityAdminLogin}];\n");
        logins.Append($"GRANT ALTER TRACE TO [{TracerLogin}];\n");
        logins.Append($"DENY VIEW ANY DATABASE TO [{NoDatabaseListLogin}];\n");
        yield return ("master", logins.ToString());

        yield return (Database, $"""
            CREATE USER [{ReaderLogin}] FOR LOGIN [{ReaderLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ReaderLogin}];
            CREATE USER [{DataReaderLogin}] FOR LOGIN [{DataReaderLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{DataReaderLogin}];
            CREATE USER [{UpdaterLogin}] FOR LOGIN [{UpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{UpdaterLogin}];
            GRANT UPDATE ON dbo.Customers TO [{UpdaterLogin}];
            CREATE USER [{WriterLogin}] FOR LOGIN [{WriterLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{WriterLogin}];
            ALTER ROLE db_datawriter ADD MEMBER [{WriterLogin}];
            CREATE USER [{OwnerLogin}] FOR LOGIN [{OwnerLogin}];
            ALTER ROLE db_owner ADD MEMBER [{OwnerLogin}];
            CREATE USER [{ColumnUpdaterLogin}] FOR LOGIN [{ColumnUpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ColumnUpdaterLogin}];
            GRANT UPDATE ON dbo.Customers (City) TO [{ColumnUpdaterLogin}];
            CREATE USER [{ImpersonatorLogin}] FOR LOGIN [{ImpersonatorLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ImpersonatorLogin}];
            GRANT IMPERSONATE ON USER::[{ReaderLogin}] TO [{ImpersonatorLogin}];
            CREATE USER [{RoleAdminLogin}] FOR LOGIN [{RoleAdminLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{RoleAdminLogin}];
            GRANT ALTER ANY ROLE TO [{RoleAdminLogin}];
            CREATE USER [{SecurityAdminLogin}] FOR LOGIN [{SecurityAdminLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SecurityAdminLogin}];
            CREATE USER [{ProcedureRunnerLogin}] FOR LOGIN [{ProcedureRunnerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ProcedureRunnerLogin}];
            GRANT EXECUTE ON dbo.AddCustomer TO [{ProcedureRunnerLogin}];
            CREATE USER [{SequenceUserLogin}] FOR LOGIN [{SequenceUserLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SequenceUserLogin}];
            GRANT UPDATE ON dbo.InvoiceNumbers TO [{SequenceUserLogin}];
            CREATE USER [{DenyWriterLogin}] FOR LOGIN [{DenyWriterLogin}];
            ALTER ROLE db_denydatawriter ADD MEMBER [{DenyWriterLogin}];
            CREATE USER [{TracerLogin}] FOR LOGIN [{TracerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{TracerLogin}];
            CREATE USER [{BackupLogin}] FOR LOGIN [{BackupLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{BackupLogin}];
            GRANT BACKUP DATABASE TO [{BackupLogin}];
            CREATE USER [{UnmaskLogin}] FOR LOGIN [{UnmaskLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{UnmaskLogin}];
            GRANT UNMASK TO [{UnmaskLogin}];
            CREATE ROLE [{ReportingRole}];
            CREATE USER [{RoleTakerLogin}] FOR LOGIN [{RoleTakerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{RoleTakerLogin}];
            GRANT TAKE OWNERSHIP ON ROLE::[{ReportingRole}] TO [{RoleTakerLogin}];
            CREATE USER [{NoDatabaseListLogin}] FOR LOGIN [{NoDatabaseListLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{NoDatabaseListLogin}];
            CREATE SYNONYM {CustomerSynonym} FOR dbo.Customers;
            CREATE USER [{SynonymUpdaterLogin}] FOR LOGIN [{SynonymUpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SynonymUpdaterLogin}];
            GRANT UPDATE ON {CustomerSynonym} TO [{SynonymUpdaterLogin}];
            """);

        yield return (QuotedDatabase, $"""
            CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
            INSERT INTO dbo.Items (Id, Name) VALUES (1, N'Bir'), (2, N'İki');
            CREATE USER [{ReaderLogin}] FOR LOGIN [{ReaderLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ReaderLogin}];
            CREATE USER [{QuotedCreatorLogin}] FOR LOGIN [{QuotedCreatorLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{QuotedCreatorLogin}];
            GRANT CREATE TABLE TO [{QuotedCreatorLogin}];
            """);

        // The three tables scripts/create-readonly-login.sql grants SELECT on as shipped, and one it does not.
        yield return (ExampleDatabase, """
            CREATE TABLE dbo.ExampleCustomers (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
            CREATE TABLE dbo.ExampleOrders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL);
            CREATE TABLE dbo.ExampleOrderLines (OrderId int NOT NULL, LineNumber int NOT NULL, PRIMARY KEY (OrderId, LineNumber));
            CREATE TABLE dbo.NotGranted (Id int NOT NULL PRIMARY KEY);
            INSERT INTO dbo.ExampleCustomers (Id, Name) VALUES (1, N'Örnek');
            """);

        // The objects the gate's regression table names (dbo.A..F, dbo.Orders and their columns), with a few rows.
        yield return (SalesDatabase, $$"""
            CREATE TABLE dbo.A (
                Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Code nvarchar(20) NULL, a int NULL, b int NULL,
                x float NULL, y float NULL, g int NULL, h int NULL, d datetime2(0) NULL, Doc nvarchar(max) NULL,
                [weird col] int NULL, [quoted] int NULL);
            CREATE TABLE dbo.B (Id int NOT NULL PRIMARY KEY, AId int NULL, Name nvarchar(50) NULL);
            CREATE TABLE dbo.C (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.D (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.E (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.F (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Total decimal(12, 2) NOT NULL, Flags int NOT NULL);
            INSERT INTO dbo.A (Id, Name, Code, a, b, x, y, g, h, d, Doc, [weird col], [quoted]) VALUES
                (1, N'alpha', N'12', 0, 1, 1.5, 2.5, 1, 1, '2026-01-15T10:00:00', N'{"a":1,"b":[1,2]}', 1, 1),
                (2, N'beta', N'x9', 2, NULL, -3.25, 4, 2, 1, '2026-02-20T11:30:00', N'{"a":"z"}', 2, 2),
                (3, NULL, NULL, NULL, 3, NULL, NULL, 2, 2, NULL, NULL, NULL, NULL);
            INSERT INTO dbo.B (Id, AId, Name) VALUES (1, 1, N'b1'), (2, 1, N'b2'), (3, 2, N'b3');
            INSERT INTO dbo.C (Id) VALUES (1), (2);
            INSERT INTO dbo.D (Id) VALUES (1);
            INSERT INTO dbo.E (Id) VALUES (2);
            INSERT INTO dbo.F (Id) VALUES (1), (2);
            INSERT INTO dbo.Orders (Id, CustomerId, Total, Flags) VALUES (1, 1, 10.50, 1), (2, 1, 99.99, 2), (3, 2, 0, 0);

            CREATE TABLE dbo.TypeSamples (Id int NOT NULL PRIMARY KEY, Doc xml NULL, Node hierarchyid NULL);
            INSERT INTO dbo.TypeSamples (Id, Doc, Node) VALUES
                (1, N'<order id="1"><line>Çay bardağı</line></order>', hierarchyid::Parse('/1/2/')),
                (2, CAST(N'<r>' + REPLICATE(CAST(N'x' AS nvarchar(max)), 3000) + N'</r>' AS xml), hierarchyid::GetRoot()),
                (3, NULL, NULL);

            CREATE TABLE dbo.LockLab (Id int NOT NULL PRIMARY KEY, V int NOT NULL);
            INSERT INTO dbo.LockLab (Id, V) VALUES (1, 0), (2, 0);

            CREATE TABLE dbo.[Müşteri_Şubeleri] (Id int NOT NULL PRIMARY KEY, Şehir nvarchar(50) NOT NULL);
            INSERT INTO dbo.[Müşteri_Şubeleri] (Id, Şehir) VALUES (1, N'İzmir');

            -- Binary values of exactly 1 MiB, 1 MiB + 1 byte and 2,000,000 bytes.
            CREATE TABLE dbo.BlobLab (Id int NOT NULL PRIMARY KEY, Data varbinary(max) NOT NULL);
            INSERT INTO dbo.BlobLab (Id, Data) VALUES
                (1, CAST(REPLICATE(CAST('b' AS varchar(max)), 1048576) AS varbinary(max))),
                (2, CAST(REPLICATE(CAST('b' AS varchar(max)), 1048577) AS varbinary(max))),
                (3, CAST(REPLICATE(CAST('b' AS varchar(max)), 2000000) AS varbinary(max)));

            {{WideTableSql()}}

            CREATE USER [{{ReaderLogin}}] FOR LOGIN [{{ReaderLogin}}];
            GRANT SELECT ON SCHEMA::dbo TO [{{ReaderLogin}}];
            """);

        // A schema the reader has no grant on (CREATE SCHEMA must start its batch).
        yield return (SalesDatabase, "CREATE SCHEMA hr;");
        yield return (SalesDatabase, """
            CREATE TABLE hr.Payroll (Id int NOT NULL PRIMARY KEY, Salary decimal(12, 2) NOT NULL);
            INSERT INTO hr.Payroll (Id, Salary) VALUES (1, 12345.67);
            """);

        // Everything the reader login sends: batches, RPCs and logins, filtered on the login name. The application
        // name is chosen by the client, so it cannot be trusted to catch everything the login sends; the login name
        // is set by the server at authentication. NO_EVENT_LOSS, so an empty trace cannot be the result of a drop.
        yield return ("master", $"""
            CREATE EVENT SESSION [{TraceSession}] ON SERVER
            ADD EVENT sqlserver.sql_batch_starting (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}')),
            ADD EVENT sqlserver.rpc_starting (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}')),
            ADD EVENT sqlserver.login (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}'))
            ADD TARGET package0.ring_buffer (SET max_memory = 16384, max_events_limit = 100000)
            WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS, STARTUP_STATE = OFF);
            """);
    }
}

/// <summary>One Extended Events record: the event name, the client's application name, the login and the SQL text.</summary>
public sealed record TraceEvent(string Name, string? Application, string? Principal, string? Text);

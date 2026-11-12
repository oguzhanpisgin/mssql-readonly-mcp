using System.Text.Json;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>R-7: list_tables and describe_table match the seeded schema; hostile names are answered as not found.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SchemaToolTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task List_tables_matches_the_seeded_schema()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var all = Items((await harness.ListTablesAsync()).Json);
        var dbo = Items((await harness.ListTablesAsync("dbo")).Json);
        var none = Items((await harness.ListTablesAsync("no_such_schema")).Json);

        string[] expected =
        [
            "dbo.CustomerTotals:view", "dbo.Customers:table", "dbo.OrderLines:table", "dbo.Orders:table",
        ];
        Assert.Equal(expected, all.Order(StringComparer.Ordinal));
        Assert.Equal(expected, dbo.Order(StringComparer.Ordinal));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Describe_table_returns_columns_primary_key_and_foreign_keys()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var lines = (await harness.DescribeTableAsync("dbo.OrderLines")).Json;
        var customers = (await harness.DescribeTableAsync("[dbo].[Customers]")).Json;
        var view = (await harness.DescribeTableAsync("CustomerTotals")).Json;

        Assert.Equal("table", lines.GetProperty("kind").GetString());
        Assert.Equal(
            [
                "OrderId int notnull", "LineNumber int notnull", "Product nvarchar(80) notnull", "Quantity int notnull",
                "UnitPrice decimal(10,2) notnull", "LineTotal decimal(21,2) null computed",
            ],
            Columns(lines));
        Assert.Equal("PK_OrderLines", lines.GetProperty("primary_key").GetProperty("name").GetString());
        Assert.Equal("[\"OrderId\",\"LineNumber\"]", lines.GetProperty("primary_key").GetProperty("columns").GetRawText());
        var fk = Assert.Single(lines.GetProperty("foreign_keys").EnumerateArray());
        Assert.Equal("FK_OrderLines_Orders", fk.GetProperty("name").GetString());
        Assert.Equal("[\"OrderId\"]", fk.GetProperty("columns").GetRawText());
        Assert.Equal("Orders", fk.GetProperty("references").GetProperty("table").GetString());
        Assert.Equal("[\"OrderId\"]", fk.GetProperty("references").GetProperty("columns").GetRawText());

        Assert.Equal(
            [
                "CustomerId int notnull identity", "Name nvarchar(100) notnull", "City nvarchar(50) null",
                "Notes nvarchar(max) null", "Photo varbinary(max) null",
            ],
            Columns(customers));
        Assert.Empty(customers.GetProperty("foreign_keys").EnumerateArray());

        Assert.Equal("view", view.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("primary_key").ValueKind);
        Assert.Equal(3, view.GetProperty("columns").GetArrayLength());
    }

    [Theory]
    [InlineData("Customers'; DROP TABLE dbo.OrderLines; --")]
    [InlineData("dbo.Customers]; DELETE FROM dbo.Orders; --")]
    [InlineData("x' OR 1=1 --")]
    [InlineData("ArchiveDb.dbo.ArchivedOrders")]
    [InlineData("#temp")]
    [InlineData("sys.objects")]
    public async Task Hostile_or_foreign_names_are_answered_as_not_found(string name)
    {
        var before = await fixture.FingerprintAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var outcome = await harness.DescribeTableAsync(name);

        Assert.True(outcome.IsError);
        Assert.StartsWith("NotFound:", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(before, await fixture.FingerprintAsync());
    }

    [Fact]
    public async Task List_tables_name_filter_is_a_substring_or_a_like_pattern()
    {
        var before = await fixture.FingerprintAsync();
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);

        var order = (await harness.ListTablesAsync(nameFilter: "Order")).Json;
        var lines = (await harness.ListTablesAsync(nameFilter: "%Lines")).Json;
        var underscore = (await harness.ListTablesAsync(nameFilter: "_")).Json;
        var percentInside = (await harness.ListTablesAsync(nameFilter: "Cust%Totals")).Json;
        var hostile = (await harness.ListTablesAsync(nameFilter: "'; DROP TABLE dbo.Orders; --")).Json;
        var empty = (await harness.ListTablesAsync(nameFilter: string.Empty)).Json;

        Assert.Equal(["dbo.OrderLines:table", "dbo.Orders:table"], Items(order).Order(StringComparer.Ordinal));
        Assert.Equal(2, order.GetProperty("total_count").GetInt32());
        Assert.False(order.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("truncated_reason").ValueKind);
        Assert.Equal(["dbo.OrderLines:table"], Items(lines));

        // Without %, the underscore is a literal character, not the LIKE "any one character".
        Assert.Empty(Items(underscore));
        Assert.Equal(0, underscore.GetProperty("total_count").GetInt32());
        Assert.Equal(["dbo.CustomerTotals:view"], Items(percentInside));
        Assert.Empty(Items(hostile));
        Assert.Equal(4, empty.GetProperty("row_count").GetInt32());
        Assert.Equal(before, await fixture.FingerprintAsync());
    }

    [Fact]
    public async Task List_tables_refuses_an_overlong_name_filter()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        var outcome = await harness.ListTablesAsync(nameFilter: new string('a', 257));

        Assert.True(outcome.IsError);
        Assert.StartsWith("InvalidArgument:", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
    }

    [Theory]
    [InlineData(129, "{0}")]
    [InlineData(129, "dbo.{0}")]
    [InlineData(129, "[{0}]")]
    [InlineData(129, "{0}.Customers")]
    [InlineData(129, "\"{0}\".Customers")]
    [InlineData(1, "a.b.c.d.{0}")]
    [InlineData(500, "[{0}]")]
    public async Task Describe_name_that_cannot_exist_is_not_found_before_any_query(int partLength, string format)
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;
        var commands = harness.Gateway.CommandsCreated;
        var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, new string('x', partLength));

        var outcome = await harness.DescribeTableAsync(name);

        Assert.True(outcome.IsError);
        Assert.StartsWith("NotFound:", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        Assert.Equal(commands, harness.Gateway.CommandsCreated);
        Assert.Equal("NotFound", harness.AuditLines()[^1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Describe_name_of_exactly_128_characters_still_asks_the_server()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        var outcome = await harness.DescribeTableAsync("dbo.[" + new string('x', 128) + "]");

        Assert.True(outcome.IsError);
        Assert.StartsWith("NotFound:", outcome.Error, StringComparison.Ordinal);
        Assert.True(harness.Gateway.ConnectionsOpened > connections, "a valid-length name must be resolved by SQL Server");
    }

    [Theory]
    [InlineData(SchemaQueries.MaxNameChars + 1)]
    [InlineData(17_000_000)]
    public async Task Describe_name_longer_than_any_name_is_an_invalid_argument_and_is_not_recorded(int length)
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        // 17,000,000 characters is more than the audit log's 16 MiB line limit.
        var outcome = await harness.DescribeTableAsync(new string('x', length));

        Assert.True(outcome.IsError);
        Assert.Equal(
            $"InvalidArgument: table must be at most {SchemaQueries.MaxNameChars} characters.", outcome.Error);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        var line = harness.AuditLines()[^1];
        Assert.Equal("describe_table:error:InvalidArgument", Key(line));
        Assert.Empty(line.GetProperty("tables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("sql").ValueKind);
        Assert.True(Audit.AuditVerifier.Verify(harness.AuditPath).Ok);
    }

    [Theory]
    [InlineData(SchemaQueries.MaxNamePartChars + 1, null)]
    [InlineData(17_000_000, null)]
    [InlineData(null, 17_000_000)]
    public async Task List_tables_arguments_are_bounded_and_never_recorded(int? schemaLength, int? filterLength)
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        var outcome = await harness.ListTablesAsync(
            schemaLength is { } s ? new string('s', s) : null, filterLength is { } f ? new string('f', f) : null);

        Assert.True(outcome.IsError);
        Assert.StartsWith(schemaLength is null ? "InvalidArgument: name_filter" : "InvalidArgument: schema", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        var line = harness.AuditLines()[^1];
        Assert.Equal("list_tables:error:InvalidArgument", Key(line));
        Assert.Empty(line.GetProperty("tables").EnumerateArray());
        Assert.True(Audit.AuditVerifier.Verify(harness.AuditPath).Ok);
    }

    [Fact]
    public async Task Name_with_a_zero_width_joiner_is_refused_although_sql_server_would_resolve_it()
    {
        // The reason: SQL Server resolves the name with U+200D (or U+FEFF) to the plain name's object.
        var admin = new Microsoft.Data.SqlClient.SqlConnection(fixture.SaConnectionString);
        await using (admin)
        {
            await admin.OpenAsync();
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.Customers' + NCHAR(0x200D)) = OBJECT_ID(N'dbo.Customers') " +
                "AND OBJECT_ID(N'dbo.Customers' + NCHAR(0xFEFF)) = OBJECT_ID(N'dbo.Customers') THEN 1 ELSE 0 END",
                admin);
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
        }

        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;

        var describe = await harness.DescribeTableAsync("Customers\u200D");
        var listSchema = await harness.ListTablesAsync(schema: "dbo\uFEFF");
        var listFilter = await harness.ListTablesAsync(nameFilter: "Cust\u00A0omers");

        Assert.Equal(
            "InvalidArgument: table contains U+200D, a format character; names with such characters are not accepted.",
            describe.Error);
        Assert.StartsWith("InvalidArgument: schema contains U+FEFF", listSchema.Error, StringComparison.Ordinal);
        Assert.StartsWith("InvalidArgument: name_filter contains U+00A0", listFilter.Error, StringComparison.Ordinal);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        var lines = harness.AuditLines();
        Assert.Equal(
            ["describe_table:error:InvalidArgument", "list_tables:error:InvalidArgument", "list_tables:error:InvalidArgument"],
            lines.Skip(lines.Count - 3).Select(Key));

        // The spelling the server would ignore is not written to the audit line.
        Assert.Empty(lines[^3].GetProperty("tables").EnumerateArray());
    }

    [Theory]
    [InlineData(0xFFFD, "the replacement character")]
    [InlineData(0xE000, "a private-use code point")]
    [InlineData(0xF0000, "a private-use code point")]
    [InlineData(0x0378, "an unassigned code point")]
    [InlineData(0xFDD0, "a Unicode noncharacter")]
    [InlineData(0xFFFE, "a Unicode noncharacter")]
    [InlineData(0x1FFFF, "a Unicode noncharacter")]
    [InlineData(0x0007, "a control character")]
    [InlineData(0x2028, "a line or paragraph separator")]
    [InlineData(0x3000, "a space separator other than U+0020")]
    public async Task Name_with_a_character_the_gate_refuses_outside_literals_is_an_invalid_argument(int codePoint, string kind)
    {
        using var harness = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
        var connections = harness.Gateway.ConnectionsOpened;
        var character = char.ConvertFromUtf32(codePoint);
        var hex = codePoint.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

        var describe = await harness.DescribeTableAsync("Customers" + character);
        var listSchema = await harness.ListTablesAsync(schema: "dbo" + character);
        var listFilter = await harness.ListTablesAsync(nameFilter: "Cust" + character);

        Assert.Equal(
            $"InvalidArgument: table contains U+{hex}, {kind}; names with such characters are not accepted.", describe.Error);
        Assert.Equal(
            $"InvalidArgument: schema contains U+{hex}, {kind}; names with such characters are not accepted.", listSchema.Error);
        Assert.Equal(
            $"InvalidArgument: name_filter contains U+{hex}, {kind}; names with such characters are not accepted.", listFilter.Error);
        Assert.Equal(connections, harness.Gateway.ConnectionsOpened);
        var lines = harness.AuditLines();
        Assert.All(lines.Skip(1), line => Assert.Empty(line.GetProperty("tables").EnumerateArray()));
    }

    [Fact]
    public async Task Turkish_name_stays_allowed_in_every_argument()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var describe = await harness.DescribeTableAsync(SqlServerFixture.TurkishTable);
        var listed = await harness.ListTablesAsync(schema: "dbo", nameFilter: "Müşteri");

        Assert.False(describe.IsError, describe.Error);
        Assert.Equal("Müşteri_Şubeleri", describe.Json.GetProperty("name").GetString());
        Assert.Equal("Şehir", describe.Json.GetProperty("columns")[1].GetProperty("name").GetString());
        Assert.False(listed.IsError, listed.Error);
        Assert.Equal(["dbo.Müşteri_Şubeleri:table"], Items(listed.Json));
        Assert.Equal(["dbo.Müşteri_Şubeleri"], harness.AuditLines()[^2].GetProperty("tables").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Table_without_a_grant_is_not_found_not_listed_and_not_in_sys_objects()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var describe = await harness.DescribeTableAsync(SqlServerFixture.HiddenTable);
        var listAll = await harness.ListTablesAsync();
        var listHr = await harness.ListTablesAsync(schema: "hr");
        var catalogue = await harness.RunQueryAsync("SELECT name FROM sys.objects", maxRows: 1000);

        Assert.StartsWith("NotFound:", describe.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Items(listAll.Json), item => item.StartsWith("hr.", StringComparison.Ordinal));
        Assert.Empty(Items(listHr.Json));
        Assert.Equal(0, listHr.Json.GetProperty("total_count").GetInt32());
        Assert.False(catalogue.IsError, catalogue.Error);
        var seen = catalogue.Json.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()!).ToList();
        Assert.False(catalogue.Json.GetProperty("truncated").GetBoolean());
        Assert.Contains("A", seen);
        Assert.DoesNotContain("Payroll", seen);

        // Every object the reader sees is in schema dbo, the one schema it has SELECT on, or ships with SQL Server
        // (the internal queue_messages_* tables of the built-in Service Broker queues).
        await using var admin = new Microsoft.Data.SqlClient.SqlConnection(
            fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase));
        await admin.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT name FROM sys.objects WHERE schema_id = SCHEMA_ID(N'dbo') OR is_ms_shipped = 1", admin);
        var dbo = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                dbo.Add(reader.GetString(0));
            }
        }

        Assert.All(seen, name => Assert.Contains(name, dbo));
    }

    [Fact]
    public async Task Describe_respects_the_character_cap_and_says_what_was_left_out()
    {
        using var harness = await Harness.StartAsync(
            fixture,
            fixture.SalesReaderConnectionString,
            new Dictionary<string, string> { [ServerConfig.MaxCharsVariable] = "1000" });

        var wide = await harness.DescribeTableAsync(SqlServerFixture.WideTable);
        var small = await harness.DescribeTableAsync("dbo.LockLab");

        Assert.False(wide.IsError, wide.Error);
        Assert.True(wide.Payload!.Length <= 1000, $"payload of {wide.Payload.Length} characters");
        Assert.True(wide.Json.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_chars", wide.Json.GetProperty("truncated_reason").GetString());
        Assert.Equal(41, wide.Json.GetProperty("column_count").GetInt32());
        var shown = wide.Json.GetProperty("columns").GetArrayLength();
        Assert.InRange(shown, 1, 40);
        Assert.Equal("PK", wide.Json.GetProperty("primary_key").GetProperty("name").GetString()![..2]);
        var line = harness.AuditLines()[^2];
        Assert.True(line.GetProperty("truncated").GetBoolean());
        Assert.Equal(shown, line.GetProperty("rows").GetInt32());

        Assert.False(small.Json.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, small.Json.GetProperty("truncated_reason").ValueKind);
        Assert.Equal(2, small.Json.GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task Tools_run_in_the_checked_database_when_the_connection_string_names_none()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.ReaderConnectionString) { Pooling = false };
        builder.Remove("Initial Catalog");
        using var harness = await Harness.StartAsync(fixture, builder.ConnectionString);
        Assert.Equal(SqlServerFixture.Database, harness.Service.Database);

        // After the check, the login's default database moves to Sales: a fresh unpinned connection would land there.
        await fixture.ExecuteAsAdminAsync("master", $"ALTER LOGIN [{SqlServerFixture.ReaderLogin}] WITH DEFAULT_DATABASE = [{SqlServerFixture.SalesDatabase}];");
        try
        {
            await using (var unpinned = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString))
            {
                await unpinned.OpenAsync();
                Assert.Equal(SqlServerFixture.SalesDatabase, unpinned.Database);
            }

            var listed = await harness.ListTablesAsync();
            var current = await harness.Service.ExecuteReadAsync("SELECT DB_NAME() AS d", 1, CancellationToken.None);

            Assert.Contains("dbo.Customers:table", Items(listed.Json));
            Assert.Equal(SqlServerFixture.Database, JsonDocument.Parse(current.Payload).RootElement.GetProperty("rows")[0][0].GetString());
        }
        finally
        {
            await fixture.ExecuteAsAdminAsync("master", $"ALTER LOGIN [{SqlServerFixture.ReaderLogin}] WITH DEFAULT_DATABASE = [{SqlServerFixture.Database}];");
        }
    }

    private static string Key(JsonElement line) =>
        $"{line.GetProperty("tool").GetString()}:{line.GetProperty("decision").GetString()}:{line.GetProperty("reason").GetString()}";

    private static List<string> Items(JsonElement json) =>
        json.GetProperty("tables").EnumerateArray()
            .Select(t => $"{t.GetProperty("schema").GetString()}.{t.GetProperty("name").GetString()}:{t.GetProperty("kind").GetString()}")
            .ToList();

    private static List<string> Columns(JsonElement json) =>
        json.GetProperty("columns").EnumerateArray()
            .Select(c => string.Join(
                ' ',
                new[]
                {
                    c.GetProperty("name").GetString(),
                    c.GetProperty("type").GetString(),
                    c.GetProperty("nullable").GetBoolean() ? "null" : "notnull",
                    c.GetProperty("identity").GetBoolean() ? "identity" : null,
                    c.GetProperty("computed").GetBoolean() ? "computed" : null,
                }.Where(s => s is not null)))
            .ToList();
}

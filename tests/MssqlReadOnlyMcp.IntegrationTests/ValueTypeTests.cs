using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>Column types beyond the plain ones (xml, the CLR type hierarchyid) and the deadlock-victim mapping.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ValueTypeTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Xml_column_is_returned_as_text_and_cut_at_the_cell_cap()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var json = (await harness.RunQueryAsync("SELECT Id, Doc FROM dbo.TypeSamples ORDER BY Id")).Json;

        Assert.Equal("xml", json.GetProperty("columns")[1].GetProperty("type").GetString());
        var rows = json.GetProperty("rows");
        Assert.Equal("<order id=\"1\"><line>Çay bardağı</line></order>", rows[0][1].GetString());
        var cut = rows[1][1].GetString()!;
        Assert.Equal(ResultPayload.MaxCellChars + ResultPayload.CellCutMarker.Length, cut.Length);
        Assert.StartsWith("<r>xxx", cut, StringComparison.Ordinal);
        Assert.EndsWith(ResultPayload.CellCutMarker, cut, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, rows[2][1].ValueKind);
    }

    [Fact]
    public async Task Hierarchyid_column_is_summarised_and_its_text_form_is_readable_by_cast()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var raw = await harness.RunQueryAsync("SELECT Id, Node FROM dbo.TypeSamples ORDER BY Id");
        var text = await harness.RunQueryAsync("SELECT Id, CAST(Node AS nvarchar(4000)) AS Node FROM dbo.TypeSamples ORDER BY Id");

        // The server has no CLR type assembly: the value is named, not decoded, and the call still succeeds.
        var rows = raw.Json.GetProperty("rows");
        Assert.StartsWith("<unsupported value of type ", rows[0][1].GetString(), StringComparison.Ordinal);
        Assert.Contains("hierarchyid", rows[0][1].GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, rows[2][1].ValueKind);
        Assert.Equal(3, raw.Json.GetProperty("row_count").GetInt32());

        var textRows = text.Json.GetProperty("rows");
        Assert.Equal("/1/2/", textRows[0][1].GetString());
        Assert.Equal("/", textRows[1][1].GetString());
    }

    [Fact]
    public async Task Deadlock_victim_is_reported_as_DeadlockVictim_and_the_next_call_works()
    {
        using var harness = await Harness.StartAsync(
            fixture,
            fixture.SalesReaderConnectionString,
            new Dictionary<string, string>
            {
                [ServerConfig.LockTimeoutVariable] = "60000",
                [ServerConfig.TimeoutVariable] = "60",
            });
        var adminConnectionString = fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.SalesDatabase);
        await using var admin = new SqlConnection(adminConnectionString);
        await admin.OpenAsync();
        int adminSession;
        // The admin session keeps the default priority (NORMAL); only the tool's own setting decides the victim.
        await using (var priority = new SqlCommand("SELECT CONVERT(int, @@SPID);", admin))
        {
            adminSession = (int)(await priority.ExecuteScalarAsync())!;
        }
        await using var transaction = (SqlTransaction)await admin.BeginTransactionAsync();
        await using (var first = new SqlCommand("UPDATE dbo.LockLab SET V = V + 1 WHERE Id = 2;", admin, transaction))
        {
            await first.ExecuteNonQueryAsync();
        }

        // Test-only path (HOLDLOCK is refused by the gate): the reader keeps its shared lock on row 1 and waits for row 2.
        var read = harness.Service.ExecuteReadAsync(
            "SELECT Id, V FROM dbo.LockLab WITH (HOLDLOCK) WHERE Id IN (1, 2) ORDER BY Id", 10, CancellationToken.None);
        await WaitUntilBlockedByAsync(adminConnectionString, adminSession);

        // The admin session now wants row 1: a deadlock. SQL Server chooses the session with the lower priority, so
        // the reader (DEADLOCK_PRIORITY LOW) is the victim against a NORMAL session.
        await using var second = new SqlCommand("UPDATE dbo.LockLab SET V = V + 1 WHERE Id = 1;", admin, transaction) { CommandTimeout = 60 };
        var update = second.ExecuteNonQueryAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() => read);
        Assert.Equal(1205, exception.Number);
        Assert.Equal("DeadlockVictim", ToolErrors.FromException(exception).Reason);
        await update;
        await transaction.RollbackAsync();

        var next = await harness.RunQueryAsync("SELECT COUNT(*) AS n FROM dbo.LockLab");
        Assert.False(next.IsError, next.Error);
        Assert.Equal(2, next.Json.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Binary_values_are_counted_up_to_one_mebibyte_only()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var json = (await harness.RunQueryAsync("SELECT Id, Data FROM dbo.BlobLab ORDER BY Id")).Json;

        // A value over 1 MiB ends the result after its row: the rest of it is never read into memory, but it still
        // crosses the network when the reader closes (LargeValueTests).
        var rows = json.GetProperty("rows");
        Assert.Equal("<binary 1048576 bytes>", rows[0][1].GetString());
        Assert.Equal("<binary over 1048576 bytes>", rows[1][1].GetString());
        Assert.Equal(2, json.GetProperty("row_count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(ResultPayload.ReasonLongValue, json.GetProperty("truncated_reason").GetString());
    }

    [Fact]
    public async Task Session_runs_with_quoted_identifier_on()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var result = await harness.Service.ExecuteReadAsync(
            "SELECT CONVERT(int, SESSIONPROPERTY('QUOTED_IDENTIFIER')) AS q", 1, CancellationToken.None);

        Assert.Equal(1, JsonDocument.Parse(result.Payload).RootElement.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Write_by_a_login_that_can_write_runs_inside_the_call_is_rolled_back_and_fails_as_RowsChanged()
    {
        var before = await fixture.FingerprintAsync();
        using var harness = Harness.StartUnchecked(fixture, fixture.WriterConnectionString, SqlServerFixture.Database);

        // Test-only path past the gate, as the db_datawriter member. SQL Server runs both writes (it reports the rows
        // they changed); the call returns no data, and the transaction is rolled back.
        var withOutput = await harness.RunPastTheGateAsync(
            "UPDATE dbo.Customers SET City = N'Değişti' OUTPUT inserted.City WHERE CustomerId <= 5");
        var withoutOutput = await harness.RunPastTheGateAsync("UPDATE dbo.Customers SET City = N'Değişti' WHERE CustomerId <= 5");

        foreach (var outcome in new[] { withOutput, withoutOutput })
        {
            Assert.Null(outcome.Payload);
            Assert.Equal(
                "RowsChanged: SQL Server reported rows changed by the text; the change was rolled back and no data is returned.",
                outcome.Error);
        }

        var lines = harness.AuditLines();
        Assert.Equal(["run_query:error:RowsChanged", "run_query:error:RowsChanged"], lines.Select(Key));
        Assert.Equal("UPDATE dbo.Customers SET City = N'Değişti' WHERE CustomerId <= 5", lines[1].GetProperty("sql").GetString());
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("rows").ValueKind);
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task Update_grant_on_a_synonym_alone_lets_the_login_change_the_table_it_names()
    {
        var before = await fixture.FingerprintAsync();
        using var harness = Harness.StartUnchecked(
            fixture, fixture.ExtraConnectionString(SqlServerFixture.SynonymUpdaterLogin), SqlServerFixture.Database);

        // Test-only path past the gate. The login holds UPDATE on the synonym only, none on dbo.Customers; both are
        // owned by dbo, so the ownership chain is unbroken and SQL Server checks the right on the synonym alone.
        var outcome = await harness.RunPastTheGateAsync(
            $"UPDATE {SqlServerFixture.CustomerSynonym} SET City = N'Değişti' WHERE CustomerId <= 5");

        Assert.Equal(
            "RowsChanged: SQL Server reported rows changed by the text; the change was rolled back and no data is returned.",
            outcome.Error);
        Assert.Equal(["run_query:error:RowsChanged"], harness.AuditLines().Select(Key));
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task Session_runs_with_nocount_off()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);

        var outcome = await harness.RunPastTheGateAsync("SELECT @@OPTIONS & 512 AS nocount");

        Assert.False(outcome.IsError, outcome.Error);
        Assert.Equal(0, outcome.Json.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Write_fails_as_RowsChanged_even_when_the_instance_turns_nocount_on_for_new_sessions()
    {
        // 'user options' 512 makes every new session start with NOCOUNT ON, which hides row counts from the client.
        // The change is instance-wide: every test of this assembly that reaches SQL Server is in one collection and
        // so runs alone, the old value is restored in finally, and the pools are cleared on both sides so no
        // connection opened under the setting is reused.
        var before = await fixture.FingerprintAsync();
        var previous = await UserOptionsAsync();
        try
        {
            await SetUserOptionsAsync(previous | 512);
            await using (var probe = new SqlConnection(fixture.SaConnectionString))
            {
                await probe.OpenAsync();
                await using var command = new SqlCommand("SELECT @@OPTIONS & 512", probe);
                Assert.Equal(512, (int)(await command.ExecuteScalarAsync())!);
            }

            using var harness = Harness.StartUnchecked(fixture, fixture.WriterConnectionString, SqlServerFixture.Database);
            var withOutput = await harness.RunPastTheGateAsync(
                "UPDATE dbo.Customers SET City = N'Değişti' OUTPUT inserted.City WHERE CustomerId <= 5");
            var withoutOutput = await harness.RunPastTheGateAsync("UPDATE dbo.Customers SET City = N'Değişti' WHERE CustomerId <= 5");

            foreach (var outcome in new[] { withOutput, withoutOutput })
            {
                Assert.Equal(
                    "RowsChanged: SQL Server reported rows changed by the text; the change was rolled back and no data is returned.",
                    outcome.Error);
            }

            Assert.Equal(["run_query:error:RowsChanged", "run_query:error:RowsChanged"], harness.AuditLines().Select(Key));
        }
        finally
        {
            // A restore that did not take would leak NOCOUNT ON into every later test of the run: fail here instead.
            await SetUserOptionsAsync(previous);
            Assert.Equal(previous, await UserOptionsAsync());
        }

        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task Long_value_ending_never_passes_the_character_cap()
    {
        // A cap small enough that the padding fits in one cell without being cut.
        const int MaxChars = 4000;
        using var harness = await Harness.StartAsync(
            fixture,
            fixture.SalesReaderConnectionString,
            new Dictionary<string, string> { [ServerConfig.MaxCharsVariable] = MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        const string Big = "REPLICATE(CAST('x' AS varchar(max)), 1048577)";

        // Measure the payload of one row with a one-character padding cell; every padding character adds one.
        var probe = await harness.RunQueryAsync($"SELECT REPLICATE(CAST('p' AS varchar(max)), 1) AS pad, {Big} AS big", maxRows: 1);
        Assert.Equal(ResultPayload.ReasonLongValue, probe.Json.GetProperty("truncated_reason").GetString());
        var fixedPart = probe.Payload!.Length - 1;

        // Padding such that the row plus an ending with the reason "max_chars" (9 characters) is exactly the cap; the
        // long-value ending ("long_value", 10 characters) is one character longer.
        var pad = MaxChars + (ResultPayload.ReasonLongValue.Length - ResultPayload.ReasonMaxChars.Length) - fixedPart;
        Assert.InRange(pad, 1, ResultPayload.MaxCellChars);
        var outcome = await harness.RunQueryAsync(
            $"SELECT REPLICATE(CAST('p' AS varchar(max)), {pad}) AS pad, {Big} AS big", maxRows: 1);

        Assert.False(outcome.IsError, outcome.Error);
        Assert.True(outcome.Payload!.Length <= MaxChars, $"payload {outcome.Payload.Length} characters, cap {MaxChars}");
    }

    [Fact]
    public async Task Limit_a_write_whose_first_result_set_is_truncated_is_not_seen_and_its_rows_are_returned_rolled_back()
    {
        var before = await fixture.FingerprintAsync();
        using var harness = Harness.StartUnchecked(fixture, fixture.WriterConnectionString, SqlServerFixture.Database);

        // The row cap cancels the command after two rows, before SQL Server reports the rows changed: the stated
        // limit. The rows returned are values of a change that was rolled back.
        var outcome = await harness.RunPastTheGateAsync(
            "UPDATE dbo.Customers SET City = N'Değişti' OUTPUT inserted.City WHERE CustomerId <= 5", maxRows: 2);

        Assert.False(outcome.IsError, outcome.Error);
        Assert.True(outcome.Json.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, outcome.Json.GetProperty("rows").GetArrayLength());
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task Second_result_set_fails_as_MultipleResultSets_with_no_data_and_the_transaction_is_rolled_back()
    {
        var before = await fixture.FingerprintAsync();
        using var harness = Harness.StartUnchecked(fixture, fixture.WriterConnectionString, SqlServerFixture.Database);

        var reads = await harness.RunPastTheGateAsync("SELECT 1 AS a; SELECT 2 AS b");
        var readThenWrite = await harness.RunPastTheGateAsync(
            "SELECT 1 AS a; UPDATE dbo.Customers SET City = N'Değişti' OUTPUT inserted.City WHERE CustomerId <= 5");

        foreach (var outcome in new[] { reads, readThenWrite })
        {
            Assert.Null(outcome.Payload);
            Assert.Equal(
                "MultipleResultSets: SQL Server returned more than one result set for the text; no data is returned.",
                outcome.Error);
        }

        var lines = harness.AuditLines();
        Assert.Equal(["run_query:error:MultipleResultSets", "run_query:error:MultipleResultSets"], lines.Select(Key));
        Assert.Equal("SELECT 1 AS a; SELECT 2 AS b", lines[0].GetProperty("sql").GetString());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("result_sha256").ValueKind);
        await AssertNothingChangedAsync(before);
    }

    private static string Key(JsonElement line) =>
        $"{line.GetProperty("tool").GetString()}:{line.GetProperty("decision").GetString()}:{line.GetProperty("reason").GetString()}";

    private async Task<int> UserOptionsAsync()
    {
        await using var admin = new SqlConnection(fixture.SaConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'user options'", admin);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetUserOptionsAsync(int value)
    {
        SqlConnection.ClearAllPools();
        await using (var admin = new SqlConnection(fixture.SaConnectionString))
        {
            await admin.OpenAsync();
            await using var command = new SqlCommand("EXEC sp_configure N'user options', @v; RECONFIGURE;", admin);
            command.Parameters.AddWithValue("@v", value);
            await command.ExecuteNonQueryAsync();
        }

        SqlConnection.ClearAllPools();
    }

    private async Task AssertNothingChangedAsync(string before)
    {
        Assert.Equal(before, await fixture.FingerprintAsync());
        await using var admin = new SqlConnection(fixture.SaConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Customers WHERE City = N'Değişti'", admin);
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }

    private static async Task WaitUntilBlockedByAsync(string adminConnectionString, int blockingSession)
    {
        await using var probe = new SqlConnection(adminConnectionString);
        await probe.OpenAsync();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @s", probe);
            command.Parameters.AddWithValue("@s", blockingSession);
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "The reader never waited for the admin session's lock.");
            await Task.Delay(100);
        }
    }
}

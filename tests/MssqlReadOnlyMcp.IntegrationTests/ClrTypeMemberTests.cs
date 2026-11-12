using System.Text.RegularExpressions;
using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Code review C-4: members of the CLR user-defined type <see cref="SqlServerFixture.ClrType"/> (assembly type
/// <c>Pt</c>: field <c>X</c>, property <c>IsNull</c>, <c>ToString</c> answering "Pt", method <c>Twice</c>) reached
/// through column syntax and conversions, as a reader with SELECT on the table and no right on the type, and as a
/// reader that also holds EXECUTE on the type. For each text: what the gate decides, and what the server answers
/// through <c>run_query</c> (a text the gate refuses is refused by <c>run_query</c> and is also sent past the gate,
/// test-only, to show what the server would do).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed partial class ClrTypeMemberTests(SqlServerFixture fixture)
{
    private const string Table = "dbo.ClrPlaces";

    private static readonly (string Id, string Sql)[] Texts =
    [
        ("M1", $"SELECT t.Spot.X FROM {Table} AS t"),
        ("M2", $"SELECT Spot.X FROM {Table}"),
        ("M3", $"SELECT t.Spot.IsNull FROM {Table} AS t"),
        ("M4", $"SELECT CAST(t.Spot AS nvarchar(100)) FROM {Table} AS t"),
        ("M5", $"SELECT CONVERT(nvarchar(100), t.Spot) FROM {Table} AS t"),
        ("M6", $"SELECT t.Spot.ToString() FROM {Table} AS t"),
        ("M7", $"SELECT t.Spot.Twice() FROM {Table} AS t"),
        ("M8", $"SELECT t.Spot FROM {Table} AS t"),
    ];

    [Fact]
    public async Task Clr_type_members_through_column_syntax_and_conversion_as_readers_without_and_with_execute_on_the_type()
    {
        try
        {
            await AdminAsync($"""
                CREATE TABLE {Table} (Id int NOT NULL PRIMARY KEY, Spot {SqlServerFixture.ClrType} NULL);
                INSERT INTO {Table} (Id, Spot) VALUES (1, CONVERT({SqlServerFixture.ClrType}, N'x'));
                """);

            // The reader passes the permission check; the EXECUTE holder does not (EXECUTE on a CLR type is a finding).
            using var reader = await Harness.StartAsync(fixture, fixture.ReaderConnectionString);
            using var typeUser = Harness.StartUnchecked(
                fixture, fixture.ExtraConnectionString(SqlServerFixture.ClrTypeUserLogin), SqlServerFixture.Database);
            var lines = new List<string>();
            foreach (var (id, sql) in Texts)
            {
                var decision = ReadOnlyGate.Evaluate(sql, new GateOptions(DefaultDatabase: SqlServerFixture.Database));
                var gate = decision.Allowed ? "allowed" : $"refused {decision.Reason}";
                lines.Add($"{id} gate {gate} | reader {await AnswerAsync(reader, sql, decision.Allowed)}"
                    + $" | type user {await AnswerAsync(typeUser, sql, decision.Allowed)}");
            }

            Assert.True(Expected.SequenceEqual(lines), string.Join(Environment.NewLine, lines));
        }
        finally
        {
            await AdminAsync($"DROP TABLE IF EXISTS {Table};");
        }
    }

    /// <summary>
    /// The gate allows the field, the property and both conversions (which run <c>ToString</c>) and refuses the
    /// method calls; the server asks EXECUTE on the type for every member and conversion, so the reader without it
    /// runs none of the type's code. The raw value needs no EXECUTE and is named, not decoded.
    /// </summary>
    private static readonly string[] Expected =
    [
        "M1 gate allowed | reader run_query error 229 EXECUTE on Point | type user run_query value 21",
        "M2 gate allowed | reader run_query error 229 EXECUTE on Point | type user run_query value 21",
        "M3 gate allowed | reader run_query error 229 EXECUTE on Point | type user run_query value False",
        "M4 gate allowed | reader run_query error 229 EXECUTE on Point | type user run_query value Pt",
        "M5 gate allowed | reader run_query error 229 EXECUTE on Point | type user run_query value Pt",
        "M6 gate refused DisallowedSyntax | reader run_query refused DisallowedSyntax, past the gate error 229 EXECUTE on Point"
            + " | type user run_query refused DisallowedSyntax, past the gate value Pt",
        "M7 gate refused DisallowedSyntax | reader run_query refused DisallowedSyntax, past the gate error 229 EXECUTE on Point"
            + " | type user run_query refused DisallowedSyntax, past the gate value 42",
        "M8 gate allowed | reader run_query value <unsupported value of type ShopDb.dbo.Point>"
            + " | type user run_query value <unsupported value of type ShopDb.dbo.Point>",
    ];

    private static async Task<string> AnswerAsync(Harness harness, string sql, bool allowed)
    {
        if (!allowed)
        {
            // run_query itself must refuse the text; the server's answer is then shown past the gate.
            var refused = await harness.RunQueryAsync(sql);
            var reason = refused.Error is null ? null : GateRefusal().Match(refused.Error);
            var shown = reason is { Success: true } ? $"refused {reason.Groups[1].Value}" : refused.Error ?? "ran";
            return $"run_query {shown}, {await ServerAnswerAsync(harness, sql, allowed)}";
        }

        return await ServerAnswerAsync(harness, sql, allowed);
    }

    private static async Task<string> ServerAnswerAsync(Harness harness, string sql, bool allowed)
    {
        var outcome = allowed ? await harness.RunQueryAsync(sql) : await harness.RunPastTheGateAsync(sql);
        var path = allowed ? "run_query" : "past the gate";
        if (outcome.IsError)
        {
            // A permission error names the right and the object the server asked it on.
            var number = ErrorNumber().Match(outcome.Error!);
            var denied = Denied().Match(outcome.Error!);
            return !number.Success ? $"{path} {outcome.Error}"
                : denied.Success ? $"{path} error {number.Groups[1].Value} {denied.Groups[1].Value} on {denied.Groups[2].Value}"
                : $"{path} error {number.Groups[1].Value}";
        }

        return $"{path} value {outcome.Json.GetProperty("rows")[0][0]}";
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            fixture.ConnectionString("sa", fixture.SaPassword, SqlServerFixture.Database));
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex(@"SQL Server error (\d+),")]
    private static partial Regex ErrorNumber();

    [GeneratedRegex(@"The (\w+) permission was denied on the object '([^']+)'")]
    private static partial Regex Denied();

    [GeneratedRegex(@"^(\w+): refused by the read-only gate")]
    private static partial Regex GateRefusal();
}

using System.Globalization;
using Microsoft.Data.SqlClient;
using MssqlReadOnlyMcp.Gate;
using MssqlReadOnlyMcp.Gate.Tests;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Differential test: every row the gate's regression table allows is sent to SQL Server as the reader through the
/// post-gate execution path. SQL Server must run it, or refuse it only because a name does not exist. Any other
/// error means the gate allows text the server reads differently, and fails the test.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DifferentialTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    /// <summary>Error numbers that only say an object, column or type does not exist.</summary>
    internal static readonly IReadOnlyDictionary<int, string> AcceptedErrors = new Dictionary<int, string>
    {
        // "Invalid column name": the row names a column the test schema does not have.
        [207] = "column does not exist",
        // "Invalid object name": the row names a table or view the test database does not have.
        [208] = "object does not exist",
        // "The multi-part identifier could not be bound": a qualified column whose table part does not resolve.
        [4104] = "qualified column does not resolve",
        // "Type ... is not a defined system type": a type name in CAST or CONVERT does not exist.
        [243] = "type does not exist",
    };

    /// <summary>
    /// Rows whose syntax SQL Server 2019 (major version 15) does not have, with the error 2019 gives. On 2022 they
    /// must run; on 2019 they must fail with exactly this error. The gate parses with ScriptDom's TSql170Parser.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Error, string Feature)> NewIn2022 = new Dictionary<string, (int, string)>
    {
        ["SELECT x FROM dbo.A WHERE x IS DISTINCT FROM 1"] = (156, "IS DISTINCT FROM"),
        ["SELECT SUM(x) OVER w FROM dbo.A WINDOW w AS (PARTITION BY g)"] = (102, "WINDOW clause"),
        ["SELECT TRIM(LEADING 'a' FROM Name), STRING_ESCAPE(Name, 'json'), TRANSLATE(Name, 'ab', 'cd') FROM dbo.A"] = (102, "TRIM LEADING"),
        ["SELECT DATEDIFF(day, d, GETUTCDATE()), DATETRUNC(month, d), EOMONTH(d), DATEFROMPARTS(2026, 1, 1) FROM dbo.A"] = (195, "DATETRUNC"),
        ["SELECT ISNULL(x, 0), CHOOSE(1, 'a', 'b'), GREATEST(x, 1), LEAST(x, 1), ISNUMERIC(Code) FROM dbo.A"] = (195, "GREATEST and LEAST"),
    };

    /// <summary>
    /// Rows SQL Server refuses while parsing, each with the exact error it gives. ScriptDom reads the look-alike
    /// character as part of one identifier, so the gate sees one plain SELECT; the server does not take the character
    /// as an identifier character and stops with a syntax error, so nothing runs (probe/summary.md K22, K24, K29). Each
    /// is still sent and must fail with exactly this error; error 102 is not accepted for any other row.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Error, string Reason)> ParseTimeRefusals = new Dictionary<string, (int, string)>
    {
        ["SELECT ＇a＇"] = (102, "full-width apostrophe U+FF07 around a name (K22)"),
        ["SELECT ’a’"] = (102, "right single quotation mark U+2019 around a name (K24)"),
        ["SELECT （1）"] = (102, "full-width parentheses U+FF08 and U+FF09 (K29)"),
    };

    /// <summary>Row cap for the differential reads: above the row count of every fixture table.</summary>
    private const int RowCap = 100_000;

    /// <summary>Rows not sent, each with its reason. Empty: every allowed row is sent.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SkippedRows = new Dictionary<string, string>();

    [Fact]
    public async Task Every_allowed_regression_row_runs_or_fails_only_on_a_missing_name()
    {
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);
        Assert.Equal(SqlServerFixture.SalesDatabase, harness.Service.Database);
        var before = await fixture.FingerprintAsync();

        var cases = RegressionTableTests.Rows.Where(r => r.Allowed).Select(r => r.Sql).ToList();
        Assert.All(
            ParseTimeRefusals.Keys.Concat(NewIn2022.Keys).Concat(SkippedRows.Keys),
            listed => Assert.True(cases.Contains(listed, StringComparer.Ordinal), "A listed row is no longer an allowed row of the table."));
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unexpected = new List<string>();

        for (var index = 0; index < cases.Count; index++)
        {
            var sql = cases[index];
            var label = string.Create(CultureInfo.InvariantCulture, $"case {index + 1}");
            if (SkippedRows.TryGetValue(sql, out var skipReason))
            {
                Count(counts, "skipped");
                output.WriteLine($"{label}: skipped ({skipReason})");
                continue;
            }

            // The table's verdict holds for the real database name too.
            var decision = ReadOnlyGate.Evaluate(sql, new GateOptions(DefaultDatabase: harness.Service.Database));
            Assert.True(decision.Allowed, $"{label}: the gate refuses an allowed row here ({decision.Reason}).");

            var versionError = fixture.MajorVersion < 16 && NewIn2022.TryGetValue(sql, out var feature) ? feature : ((int Error, string Feature)?)null;
            var parseError = ParseTimeRefusals.TryGetValue(sql, out var refusal) ? refusal : ((int Error, string Reason)?)null;
            string outcome;
            try
            {
                // A truncated first set cancels the command before a second set or a row count could be seen, so
                // every row must be read whole: the cap is above the largest fixture table.
                var result = await harness.Service.ExecuteReadAsync(sql, RowCap, CancellationToken.None);
                outcome = "success";
                if (result.Truncated)
                {
                    unexpected.Add($"{label}: the result was truncated, so a second result set could not be seen | {Shorten(sql)}");
                }
                if (versionError is not null)
                {
                    unexpected.Add($"{label}: ran on major version {fixture.MajorVersion}, listed as new in 2022 ({versionError.Value.Feature})");
                }

                if (parseError is not null)
                {
                    unexpected.Add($"{label}: ran, listed as refused while parsing ({parseError.Value.Reason})");
                }
            }
            catch (SqlException exception)
            {
                if (versionError is { } expected && expected.Error == exception.Number)
                {
                    outcome = string.Create(CultureInfo.InvariantCulture, $"version error {exception.Number} ({expected.Feature})");
                }
                else if (versionError is null && parseError is { } parse && parse.Error == exception.Number)
                {
                    outcome = string.Create(CultureInfo.InvariantCulture, $"listed parse error {exception.Number} ({parse.Reason})");
                }
                else if (versionError is null && parseError is null && AcceptedErrors.TryGetValue(exception.Number, out var meaning))
                {
                    outcome = string.Create(CultureInfo.InvariantCulture, $"accepted error {exception.Number} ({meaning})");
                }
                else
                {
                    outcome = string.Create(CultureInfo.InvariantCulture, $"unexpected error {exception.Number}");
                    unexpected.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{label}: SQL Server error {exception.Number}, class {exception.Class}: {exception.Message} | {Shorten(sql)}"));
                }
            }
            catch (Exception exception) when (exception is ToolRefusalException or InvalidOperationException)
            {
                outcome = $"unexpected {exception.GetType().Name}";
                unexpected.Add($"{label}: {exception.GetType().Name}: {exception.Message} | {Shorten(sql)}");
            }

            Count(counts, outcome);
            output.WriteLine($"{label}: {outcome}");
        }

        foreach (var (outcome, count) in counts)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"outcome {outcome}: {count}"));
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"allowed regression rows {cases.Count}, major version {fixture.MajorVersion}"));
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected));
        Assert.Equal(before, await fixture.FingerprintAsync());
    }

    private static void Count(SortedDictionary<string, int> counts, string outcome) =>
        counts[outcome] = counts.GetValueOrDefault(outcome) + 1;

    private static string Shorten(string sql) => sql.Length <= 160 ? sql : string.Concat(sql.AsSpan(0, 160), "...");
}

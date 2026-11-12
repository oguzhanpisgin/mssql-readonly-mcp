using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlReadOnlyMcp.Gate;
using Xunit.Abstractions;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Every lexer case of the probe, as a permanent test. For each case the gate decides; a case the gate allows runs on
/// SQL Server as the reader through the post-gate path, and must give exactly one result set whose first column name
/// is the name the parser saw, or a server error (safe: nothing ran). A second result set or another column name means
/// the server read more or other text than the gate did, and fails the test.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ParserVersusServerLexerTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Every_lexer_case_is_refused_by_the_gate_or_read_by_the_server_as_the_parser_read_it()
    {
        Assert.Equal(280 + 55, ParserVersusServerLexerCases.Cases.Count);
        Assert.Equal(
            ParserVersusServerLexerCases.Cases.Count,
            ParserVersusServerLexerCases.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        using var harness = await Harness.StartAsync(fixture, fixture.SalesReaderConnectionString);
        var before = await fixture.FingerprintAsync();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var lexerCase in ParserVersusServerLexerCases.Cases)
        {
            var decision = ReadOnlyGate.Evaluate(lexerCase.Sql, new GateOptions(DefaultDatabase: harness.Service.Database));
            if (!decision.Allowed)
            {
                Count(counts, "refused by the gate");
                output.WriteLine($"{lexerCase.Id}: refused by the gate ({decision.Reason})");
                continue;
            }

            var expectedName = ParserColumnName(lexerCase);
            string outcome;
            try
            {
                var result = await harness.Service.ExecuteReadAsync(lexerCase.Sql, 100_000, CancellationToken.None);
                var columns = JsonDocument.Parse(result.Payload).RootElement.GetProperty("columns");
                var serverName = columns.GetArrayLength() == 0 ? null : columns[0].GetProperty("name").GetString();
                if (result.Truncated)
                {
                    // A truncated set hides any second set: every allowed case must be read whole.
                    outcome = "allowed, TRUNCATED";
                    failures.Add($"{lexerCase.Id}: the result was truncated, so a second result set could not be seen");
                }
                else if (expectedName is not null && serverName != expectedName)
                {
                    outcome = "allowed, OTHER COLUMN NAME";
                    failures.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{lexerCase.Id}: the parser saw column name [{CodePoints(expectedName)}], the server returned [{CodePoints(serverName ?? "(none)")}]"));
                }
                else
                {
                    outcome = "allowed and agreed";
                }
            }
            catch (SqlException exception) when (CompileTimeErrors.ContainsKey(exception.Number))
            {
                outcome = "allowed and refused by the server";
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{lexerCase.Id}: server error {exception.Number}"));
            }
            catch (SqlException exception)
            {
                // A runtime error in statement 1 does not prove that a statement 2 did not run.
                outcome = "allowed, RUNTIME ERROR";
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{lexerCase.Id}: server error {exception.Number} is not an error raised when the batch is compiled"));
            }
            catch (ToolRefusalException exception)
            {
                outcome = $"allowed, {exception.Failure.Reason.ToUpperInvariant()}";
                failures.Add($"{lexerCase.Id}: {exception.Failure.Message}");
            }

            Count(counts, outcome);
            output.WriteLine($"{lexerCase.Id}: {outcome}");
        }

        foreach (var (outcome, count) in counts)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"outcome {outcome}: {count}"));
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"lexer cases {ParserVersusServerLexerCases.Cases.Count}, major version {fixture.MajorVersion}"));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(before, await fixture.FingerprintAsync());

        // docs/promises.md quotes these numbers; this test is their source. They change only when the gate or the
        // case list changes, and they are the same on SQL Server 2019, 2022 and 2025.
        Assert.Equal(
            new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["allowed and agreed"] = 61,
                ["allowed and refused by the server"] = 5,
                ["refused by the gate"] = 269,
            },
            counts);
    }

    /// <summary>
    /// The server errors that count as "the server ran nothing": both are raised while the batch is compiled, before
    /// any statement of it runs. Any other error number fails the test.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> CompileTimeErrors = new Dictionary<int, string>
    {
        // Incorrect syntax near a token: the batch did not parse (cases C31c, K22, K24, K29).
        [102] = "syntax error",

        // Invalid column name: the batch parsed but a column did not bind when it was compiled (case K23).
        [207] = "invalid column name",
    };

    /// <summary>
    /// The first column's name as ScriptDom reads it (the probe's rule): the alias of the first select element; for
    /// round 2, an unaliased column reference is named by its last identifier; an unaliased expression has the empty
    /// name. Null when the first statement is not a plain query specification.
    /// </summary>
    private static string? ParserColumnName(LexerCase lexerCase)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(lexerCase.Sql), out _);
        var first = (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).FirstOrDefault();
        if (first is not SelectStatement { QueryExpression: QuerySpecification query }
            || query.SelectElements.FirstOrDefault() is not SelectScalarExpression element)
        {
            return null;
        }

        if (element.ColumnName is not null)
        {
            return element.ColumnName.Value;
        }

        return lexerCase.NamesColumnReferences && element.Expression is ColumnReferenceExpression { MultiPartIdentifier: { } name }
            ? name.Identifiers[^1].Value
            : string.Empty;
    }

    private static string CodePoints(string text) =>
        string.Join(' ', text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private static void Count(SortedDictionary<string, int> counts, string outcome) =>
        counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
}

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>One lexer case: the probe's id, and whether the parser names an unaliased column by its last identifier.</summary>
/// <param name="Id">The id used in <c>probe/results-2022.md</c> and <c>probe/results2-2022.md</c>.</param>
/// <param name="Sql">The text exactly as the probe sent it.</param>
/// <param name="NamesColumnReferences">
/// Round 2 cases: an unaliased column reference (<c>SELECT a&lt;U+200D&gt;b FROM ...</c>) takes its last identifier
/// as the expected column name. Round 1 cases expect the alias only (an unaliased expression has no name).
/// </param>
public sealed record LexerCase(string Id, string Sql, bool NamesColumnReferences)
{
    public override string ToString() => Id;
}

/// <summary>
/// The parser-versus-server lexer cases of the probe (<c>probe/LexerProbe/Program.cs</c>), ported unchanged: round 1
/// has 31 characters in 8 positions and 32 constructs (280 cases, <c>probe/summary.md</c>); round 2 has ODBC forms,
/// noncharacters, identifier folding and comment starts next to Unicode whitespace (55 cases,
/// <c>probe/summary2.md</c>). <see cref="ParserVersusServerLexerTests"/> runs every case.
/// </summary>
internal static class ParserVersusServerLexerCases
{
    private static readonly (string X, string Label)[] Chars =
    [
        ("\u0000", "U+0000"), ("\u0001", "U+0001"), ("\u0008", "U+0008"), ("\u000B", "U+000B"), ("\u000C", "U+000C"),
        ("\u001A", "U+001A"), ("\u001F", "U+001F"), ("\u007F", "U+007F"), ("\u0085", "U+0085"), ("\u00A0", "U+00A0"),
        ("\u00AD", "U+00AD"), ("\u1680", "U+1680"), ("\u180E", "U+180E"), ("\u2000", "U+2000"), ("\u2003", "U+2003"),
        ("\u200A", "U+200A"), ("\u200B", "U+200B"), ("\u200C", "U+200C"), ("\u200D", "U+200D"), ("\u2028", "U+2028"),
        ("\u2029", "U+2029"), ("\u202F", "U+202F"), ("\u205F", "U+205F"), ("\u2060", "U+2060"), ("\u3000", "U+3000"),
        ("\uFEFF", "U+FEFF"), ("\uFFFD", "U+FFFD"), ("\uE000", "U+E000"), ("\uD83D", "U+D83D lone high"),
        ("\uDE00", "U+DE00 lone low"), ("\U0001F600", "U+1F600 pair"),
    ];

    /// <summary>Round 1 (280 cases) followed by round 2 (55 cases).</summary>
    public static readonly IReadOnlyList<LexerCase> Cases = [.. Round1(), .. Round2()];

    private static IEnumerable<LexerCase> Round1()
    {
        var n = 0;
        foreach (var (x, _) in Chars)
        {
            foreach (var c in Positions($"C{++n:00}", x, false))
            {
                yield return c;
            }
        }

        string[] constructs =
        [
            "/* a /* b */ SELECT 2 */ SELECT 1",
            "SELECT 1 /* a /* b */ SELECT 2",
            "SELECT 1 /* a /* b */ SELECT 2 */",
            "SELECT 1 /* /* */ */ SELECT 2",
            "SELECT 1 /* ' */ SELECT 2",
            "SELECT 1 /* ' */ SELECT 2 /* ' */",
            "SELECT 1 /* \" */ SELECT 2",
            "SELECT 1 /* -- */ SELECT 2",
            "SELECT 1 /* -- */ SELECT 2 /* x\n */",
            "SELECT 1 -- /*\nSELECT 2",
            "SELECT 1 -- /*\nSELECT 2 -- */",
            "SELECT 1 /* x",
            "SELECT 1; SELECT 2 /* x",
            "SELECT 1 -- c\rSELECT 2",
            "SELECT 1 -- c\nSELECT 2",
            "SELECT 1 -- c\r\nSELECT 2",
            "SELECT 1\nGO\nSELECT 2",
            "SELECT 1\r\nGO\r\nSELECT 2",
            "\uFF33\uFF25\uFF2C\uFF25\uFF23\uFF34 1",
            "SELECT 1\uFF1BSELECT 2",
            "SELECT 1 \uFF1B SELECT 2",
            "SELECT \uFF07a\uFF07",
            "SELECT \u02BCa\u02BC",
            "SELECT \u2019a\u2019",
            "SELECT N'a\uFF07 SELECT 2 --'",
            "SELECT N'a\u02BC SELECT 2 --'",
            "SELECT N'a\u2019 SELECT 2 --'",
            "SELECT 'a\uFF07 SELECT 2 --'",
            "SELECT \uFF081\uFF09",
            "SELECT 1;",
            "SELECT 1;;",
            "SELECT 1;; SELECT 2",
        ];
        for (var k = 0; k < constructs.Length; k++)
        {
            yield return new LexerCase($"K{k + 1:00}", constructs[k], false);
        }
    }

    private static IEnumerable<LexerCase> Positions(string prefix, string x, bool round2)
    {
        yield return new LexerCase(prefix + "a", "SELECT" + x + "1", round2);
        yield return new LexerCase(prefix + "b", "SELECT 1" + x + "SELECT 2", round2);
        yield return new LexerCase(prefix + "c", "SELECT 1 AS a" + x + "b", round2);
        yield return new LexerCase(prefix + "d", "SELECT 1 -- c" + x + "SELECT 2", round2);
        yield return new LexerCase(prefix + "e", "SELECT N'a" + x + "b'", round2);
        yield return new LexerCase(prefix + "f1", "SELECT 1 AS [a" + x + "b]", round2);
        yield return new LexerCase(prefix + "f2", "SELECT 1 AS \"a" + x + "b\"", round2);
        yield return new LexerCase(prefix + "g", x + "SELECT 1", round2);
    }

    private static IEnumerable<LexerCase> Round2()
    {
        string[] odbcComments =
        [
            "SELECT 1 --(* x",
            "SELECT 1 --(* x\nSELECT 2",
            "SELECT 1 --(* x\nSELECT 2\n*)--",
            "SELECT 1 --(*vendor(Microsoft),product(ODBC) fn now() *)--",
            "SELECT 1 --(*vendor(Microsoft),product(ODBC) fn now()\nSELECT 2\n*)--",
            "--(* x\nSELECT 2 *)--",
            "SELECT 1 -- (* x\nSELECT 2",
        ];
        for (var o = 0; o < odbcComments.Length; o++)
        {
            yield return new LexerCase($"O{o + 1:00}", odbcComments[o], true);
        }

        string[] odbcBraces =
        [
            "SELECT {fn NOW()}",
            "SELECT {d '2020-01-01'}",
            "SELECT {ts '2020-01-01 00:00:00'}",
            "SELECT {fn CONCAT('a','b')}",
            "{call sp_who}",
            "{? = call sp_who}",
            "SELECT 1 FROM {oj sys.objects a LEFT OUTER JOIN sys.objects b ON a.object_id = b.object_id}",
            "SELECT {guid '00000000-0000-0000-0000-000000000000'}",
            "SELECT {escape '\\'}",
            "SELECT 1 WHERE 'a_b' LIKE 'a\\_b' {escape '\\'}",
        ];
        for (var b = 0; b < odbcBraces.Length; b++)
        {
            yield return new LexerCase($"B{b + 1:00}", odbcBraces[b], true);
        }

        var n = 0;
        foreach (var x in new[] { "\uFFFF", "\uFFFE", "\uFDD0" })
        {
            foreach (var c in Positions($"N{++n}", x, true))
            {
                yield return c;
            }
        }

        yield return new LexerCase("N4", "SELECT 1 /* a\uFFFFb */ SELECT 2", true);
        yield return new LexerCase("N5", "SELECT 'a\uFFFFb' SELECT 2", true);
        yield return new LexerCase("N6", "SELECT 1 -- a\uFFFFb\nSELECT 2", true);

        var i = 0;
        foreach (var x in new[] { "\u200D", "\u00AD", "\uFEFF", "\u200B" })
        {
            i++;
            yield return new LexerCase($"I{i}q", "SELECT [a" + x + "b] FROM (SELECT 1 AS ab) t", true);
            yield return new LexerCase($"I{i}u", "SELECT a" + x + "b FROM (SELECT 1 AS ab) t", true);
        }

        yield return new LexerCase("U1", "SELECT 1 -\u00A0- x\nSELECT 2", true);
        yield return new LexerCase("U2", "SELECT 1 /\u200B* x */ SELECT 2", true);
        yield return new LexerCase("U3", "SELECT 1 --\u2028SELECT 2", true);
    }

    /// <summary>
    /// Look-alike ends (code review C-5), kept apart from <see cref="Cases"/> so that the counts quoted from that list
    /// stay as they are. Each text holds, inside a block comment, a string literal or a quoted identifier, a character
    /// that looks like the end of it, followed by a second statement with the marker column <c>hidden</c>; if the
    /// server ended the comment, string or identifier there, that statement would run. The first column is named.
    /// </summary>
    public static readonly IReadOnlyList<LexerCase> LookAlikeEndCases =
    [
        new("L01", "SELECT 1 AS shown /* \uFF0A\uFF0F ; SELECT 2 AS hidden --*/", false),
        new("L02", "SELECT 1 AS shown /* \u2217\u2215 ; SELECT 2 AS hidden --*/", false),
        new("L03", "SELECT 1 AS shown /* \u2731\u2044 ; SELECT 2 AS hidden --*/", false),
        new("L04", "SELECT 1 AS shown /* *\uFF0F ; SELECT 2 AS hidden --*/", false),
        new("L05", "SELECT 1 AS shown /* \uFF0A/ ; SELECT 2 AS hidden --*/", false),
        new("L06", "SELECT 1 AS shown /* \uFE61\uFF0F ; SELECT 2 AS hidden --*/", false),
        new("L07", "SELECT 1 AS shown /* /* \uFF0A\uFF0F */ ; SELECT 2 AS hidden --*/", false),
        new("L08", "SELECT 1 AS shown /* /* */ \uFF0A\uFF0F ; SELECT 2 AS hidden --*/", false),
        new("L09", "SELECT 'a\u2019 ; SELECT 2 AS hidden --' AS shown", false),
        new("L10", "SELECT 'a\u02BC ; SELECT 2 AS hidden --' AS shown", false),
        new("L11", "SELECT 'a\uFF07 ; SELECT 2 AS hidden --' AS shown", false),
        new("L12", "SELECT N'a\u2019 ; SELECT 2 AS hidden --' AS shown", false),
        new("L13", "SELECT N'a\u02BC ; SELECT 2 AS hidden --' AS shown", false),
        new("L14", "SELECT N'a\uFF07 ; SELECT 2 AS hidden --' AS shown", false),
        new("L15", "SELECT 1 AS [a\uFF3D ; SELECT 2 AS hidden --]", false),
        new("L16", "SELECT 1 AS \"a\uFF02 ; SELECT 2 AS hidden --\"", false),
        new("L17", "SELECT 1 AS \"a\u201D ; SELECT 2 AS hidden --\"", false),
    ];
}

using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlReadOnlyMcp.Gate;

/// <summary>
/// Decides, before anything reaches SQL Server, whether one T-SQL text is a single side-effect-free SELECT. The
/// decision is an allowlist over the ScriptDom syntax tree; no keyword or pattern matching on the text decides it.
/// A few refuse-only checks on the token stream run as well (see <c>EvaluateCore</c>); they can only refuse.
/// </summary>
public static class ReadOnlyGate
{
    /// <summary>The deepest parenthesis nesting accepted, counted on the token stream before parsing.</summary>
    internal const int MaxParenthesisDepth = 64;

    /// <summary>
    /// The ceiling for <see cref="GateOptions.MaxSqlChars"/>. The ScriptDom parser recurses without a stack guard,
    /// so a stack overflow there cannot be caught; the stack budget below was measured, not proven, and the ceiling
    /// keeps inputs small enough that the measured worst case leaves a wide margin.
    /// </summary>
    internal const int HardMaxSqlChars = 131_072;

    // The ScriptDom parser and visitor recurse once per nesting level, and nesting is not bounded by parentheses
    // alone (unary operators, CASE, AND/OR chains, set operators). Measured worst case for 180.140.0 is below
    // 256 bytes of stack per character; the evaluation thread gets twice that plus a fixed base.
    private const int BaseStackBytes = 16 * 1024 * 1024;
    private const int StackBytesPerChar = 512;

    private static readonly IReadOnlyList<string> NoTables = [];

    /// <summary>Evaluates one SQL text. Never throws; every failure is a refusal.</summary>
    /// <param name="sql">The T-SQL text to check.</param>
    /// <param name="options">Gate options; null uses the defaults.</param>
    /// <returns>The decision, with the referenced table names when the text parsed.</returns>
    public static GateDecision Evaluate(string? sql, GateOptions? options = null)
    {
        try
        {
            options ??= new GateOptions();
            if (string.IsNullOrWhiteSpace(sql))
            {
                return Refuse(GateReason.Empty, "Empty: no SQL text");
            }

            var maxChars = Math.Min(options.MaxSqlChars, HardMaxSqlChars);
            if (sql.Length > maxChars)
            {
                return Refuse(GateReason.TooLong, "TooLong: text exceeds the configured maximum length");
            }

            // Whole-text checks before the text is tokenised or parsed, including inside string literals, quoted
            // identifiers and comments. Nothing has parsed yet, so no tables are reported for these refusals.
            // - Not well-formed UTF-16: the server reads an unpaired surrogate as U+FFFD, so the parser and the server
            //   would not see the same text.
            // - Unicode noncharacters (U+FDD0..U+FDEF and U+xFFFE/U+xFFFF in every plane): ScriptDom before 180.140.0
            //   used U+FFFF as its end-of-input marker and stopped reading there, and SQL Server does not accept
            //   U+FFFF even in a delimited identifier.
            var textDetail = CheckWholeText(sql);
            if (textDetail is not null)
            {
                return Refuse(GateReason.DisallowedSyntax, textDetail);
            }

            return RunOnLargeStack(sql, options);
        }
        catch (Exception exception)
        {
            return Refuse(GateReason.ParseError, $"ParseError: gate failure ({exception.GetType().Name})");
        }
    }

    private static GateDecision RunOnLargeStack(string sql, GateOptions options)
    {
        GateDecision? decision = null;
        var stackBytes = (int)Math.Min(int.MaxValue, BaseStackBytes + ((long)sql.Length * StackBytesPerChar));
        var thread = new Thread(
            () =>
            {
                try
                {
                    decision = EvaluateCore(sql, options);
                }
                catch (InsufficientExecutionStackException)
                {
                    decision = Refuse(GateReason.TooDeep, "TooDeep: syntax tree nests too deeply");
                }
                catch (Exception exception)
                {
                    decision = Refuse(GateReason.ParseError, $"ParseError: gate failure ({exception.GetType().Name})");
                }
            },
            stackBytes)
        {
            IsBackground = true,
            Name = "ReadOnlyGate",
        };
        thread.Start();
        thread.Join();
        return decision ?? Refuse(GateReason.ParseError, "ParseError: gate produced no decision");
    }

    private static GateDecision EvaluateCore(string sql, GateOptions options)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(new StringReader(sql), out var lexErrors);

        // Token-level checks. None of them can allow anything; each can only refuse. (Text that is not well-formed
        // UTF-16 or contains a Unicode noncharacter was already refused in Evaluate, anywhere in the text.)
        // 1. Parenthesis depth, before parsing (TooDeep).
        // 2. A line comment that runs across a character some readers treat as a line break (control characters
        //    other than TAB, NEL, U+2028, U+2029), which could hide text from ScriptDom that the server runs.
        // 3. Outside string literals, quoted or bracketed identifiers and comments: Unicode space separators other
        //    than U+0020, line and paragraph separators, format characters and control characters other than TAB,
        //    CR and LF; U+FFFD, private-use and unassigned code points. ScriptDom treats some of them as whitespace
        //    (it splits a<U+200B>b into two identifiers), and the server may not. Inside bracketed and double-quoted
        //    identifiers the same space, separator, format and control characters are refused as well (the server
        //    ignores some of them when it compares names); U+FFFD, private-use and unassigned code points are not.
        // 4. ODBC escape tokens: OdbcInitiator ("--(*") and LeftCurly/RightCurly outside literals, quoted
        //    identifiers and comments, whatever the parser later makes of them.
        // 5. A GO token (NotSingleBatch, checked after parsing below).
        var depth = 0;
        string? characterDetail = null;
        foreach (var token in tokens)
        {
            if (token.TokenType == TSqlTokenType.LeftParenthesis && ++depth > MaxParenthesisDepth)
            {
                return Refuse(
                    GateReason.TooDeep,
                    $"TooDeep: more than {MaxParenthesisDepth} nested parentheses at line {token.Line}, column {token.Column}");
            }

            if (token.TokenType == TSqlTokenType.RightParenthesis && depth > 0)
            {
                depth--;
            }

            characterDetail ??= CheckTokenCharacters(token);
        }

        var fragment = parser.Parse(tokens, out var parseErrors);
        var script = lexErrors.Count == 0 && parseErrors.Count == 0 ? fragment as TSqlScript : null;

        if (characterDetail is not null)
        {
            return Refuse(
                GateReason.DisallowedSyntax,
                characterDetail,
                script is null ? NoTables : CollectTables(script, options));
        }

        if (lexErrors.Count > 0)
        {
            return ParseFailure(lexErrors[0]);
        }

        if (parseErrors.Count > 0)
        {
            return ParseFailure(parseErrors[0]);
        }

        if (script is null)
        {
            return Refuse(GateReason.ParseError, "ParseError: parser returned no script");
        }

        // ScriptDom drops an empty batch before or after GO, so the token is checked as well as the batch count.
        var goToken = tokens.FirstOrDefault(t => t.TokenType == TSqlTokenType.Go);
        if (goToken is not null || script.Batches.Count > 1)
        {
            var where = goToken is null ? string.Empty : $" at line {goToken.Line}, column {goToken.Column}";
            return Refuse(
                GateReason.NotSingleBatch,
                $"NotSingleBatch: {script.Batches.Count} batches or a GO separator{where}",
                CollectTables(script, options));
        }

        if (script.Batches.Count == 0 || script.Batches[0].Statements.Count != 1)
        {
            var count = script.Batches.Count == 0 ? 0 : script.Batches[0].Statements.Count;
            return Refuse(
                GateReason.NotSingleStatement,
                $"NotSingleStatement: {count.ToString(CultureInfo.InvariantCulture)} statements",
                CollectTables(script, options));
        }

        var statement = script.Batches[0].Statements[0];
        if (!Allowlist.IsAllowedStatementType(statement.GetType()))
        {
            return Refuse(
                GateReason.NotSelect,
                $"NotSelect: {statement.GetType().Name} at line {statement.StartLine}, column {statement.StartColumn}",
                CollectTables(script, options));
        }

        var visitor = new GateVisitor(options.DefaultDatabase, CteScopes.Build(statement), collectOnly: false);
        statement.Accept(visitor);
        if (visitor.Reason != GateReason.Allowed)
        {
            return Refuse(visitor.Reason, visitor.Detail ?? visitor.Reason.ToString(), visitor.Tables());
        }

        return new GateDecision(true, GateReason.Allowed, "Allowed", visitor.Tables());
    }

    private static string? CheckTokenCharacters(TSqlParserToken token)
    {
        switch (token.TokenType)
        {
            case TSqlTokenType.SingleLineComment:
                return HasHiddenLineBreak(token.Text)
                    ? $"DisallowedSyntax: SingleLineComment with a control or line separator character at line {token.Line}, column {token.Column}"
                    : null;

            // ODBC escape forms. ScriptDom reads "--" directly followed by "(*" as an OdbcInitiator token, not as a
            // comment, and the server's reading of the same text is not known; braces start ODBC escapes ({fn ...},
            // {d ...}, {call ...}, {oj ...}). A brace inside a literal, quoted identifier or comment is part of that
            // token, so only braces outside them reach these cases.
            case TSqlTokenType.OdbcInitiator:
            case TSqlTokenType.LeftCurly:
            case TSqlTokenType.RightCurly:
                return $"DisallowedSyntax: {token.TokenType} token at line {token.Line}, column {token.Column}";

            // Bracketed ([..]) and double-quoted identifiers (QUOTED_IDENTIFIER is on, so ".." is an identifier).
            // SQL Server 2019 and 2022 were measured to ignore some invisible characters (U+200D, U+FEFF) when they
            // compare names, so [a<U+200D>b] reaches the column ab while the gate's names are compared ordinally
            // and Tables records another spelling. The plain name reaches the same object, so nothing is lost.
            case TSqlTokenType.QuotedIdentifier:
            case TSqlTokenType.AsciiStringOrQuotedIdentifier:
                return FindRune(token, IsInvisibleOrSeparator);

            case TSqlTokenType.MultilineComment:
            case TSqlTokenType.AsciiStringLiteral:
            case TSqlTokenType.UnicodeStringLiteral:
                return null;

            default:
                // Unpaired surrogates never reach this point: Evaluate refuses text that is not well-formed UTF-16.
                return FindRune(token, IsLexerAmbiguous);
        }
    }

    private static string? FindRune(TSqlParserToken token, Func<Rune, bool> refused)
    {
        foreach (var rune in (token.Text ?? string.Empty).EnumerateRunes())
        {
            if (refused(rune))
            {
                var codePoint = rune.Value.ToString("X4", CultureInfo.InvariantCulture);
                return $"DisallowedSyntax: {token.TokenType} token with U+{codePoint} at line {token.Line}, column {token.Column}";
            }
        }

        return null;
    }

    /// <summary>
    /// Refused outside literals and comments: the invisible and separator characters of
    /// <see cref="IsInvisibleOrSeparator"/>, plus U+FFFD, private-use and unassigned code points.
    /// </summary>
    private static bool IsLexerAmbiguous(Rune rune) =>
        rune == Rune.ReplacementChar
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
        || IsInvisibleOrSeparator(rune);

    /// <summary>
    /// Refused everywhere except inside string literals and comments: Unicode space separators other than U+0020,
    /// line and paragraph separators, format characters and control characters other than TAB, CR and LF.
    /// </summary>
    private static bool IsInvisibleOrSeparator(Rune rune)
    {
        const int Tab = 9;
        const int LineFeed = 10;
        const int CarriageReturn = 13;
        const int Space = 32;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.SpaceSeparator => rune.Value != Space,
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format => true,
            UnicodeCategory.Control => rune.Value is not (Tab or LineFeed or CarriageReturn),
            _ => false,
        };
    }

    /// <summary>
    /// Returns the refusal detail for the first unpaired surrogate or Unicode noncharacter in the text, or null.
    /// Works on UTF-16 code units, so it never depends on how a tokeniser would group the text.
    /// </summary>
    private static string? CheckWholeText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                var unit = ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture);
                return $"DisallowedSyntax: text is not well-formed UTF-16 (unpaired surrogate U+{unit})";
            }
            else
            {
                codePoint = text[i];
            }

            if (IsNoncharacter(codePoint))
            {
                var hex = codePoint.ToString("X4", CultureInfo.InvariantCulture);
                return $"DisallowedSyntax: text contains a Unicode noncharacter (U+{hex})";
            }
        }

        return null;
    }

    /// <summary>The 66 Unicode noncharacters: U+FDD0..U+FDEF and the last two code points of every plane.</summary>
    private static bool IsNoncharacter(int codePoint) =>
        codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

    private static bool HasHiddenLineBreak(string? text)
    {
        foreach (var c in text ?? string.Empty)
        {
            if (c != '\t' && char.IsControl(c))
            {
                return true;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> CollectTables(TSqlScript script, GateOptions options)
    {
        var collector = new GateVisitor(options.DefaultDatabase, CteScopes.Build(script), collectOnly: true);
        script.Accept(collector);
        return collector.Tables();
    }

    private static GateDecision ParseFailure(ParseError error) =>
        Refuse(
            GateReason.ParseError,
            $"ParseError: error {error.Number.ToString(CultureInfo.InvariantCulture)} at line {error.Line}, column {error.Column}");

    private static GateDecision Refuse(GateReason reason, string detail, IReadOnlyList<string>? tables = null) =>
        new(false, reason, detail, tables ?? NoTables);
}

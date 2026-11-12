namespace MssqlReadOnlyMcp.Gate;

/// <summary>Options for <see cref="ReadOnlyGate.Evaluate(string?, GateOptions?)"/>.</summary>
/// <param name="DefaultDatabase">
/// The database the connection uses. A three-part name is allowed only when its database part equals this value
/// exactly (ordinal, case-sensitive: on a case-sensitive instance <c>SALES</c> and <c>Sales</c> can be two
/// databases, so a differently-cased spelling is refused); when this is null every three-part name is refused.
/// </param>
/// <param name="MaxSqlChars">
/// The longest accepted SQL text, in UTF-16 characters. Longer text is refused without parsing. Values above
/// 131,072 are treated as 131,072: the ScriptDom parser recurses once per nesting level without a stack guard, so
/// an overflow there cannot be caught, and the gate's stack budget per character was measured, not proven.
/// </param>
public sealed record GateOptions(string? DefaultDatabase = null, int MaxSqlChars = 65536);

/// <summary>Why the gate allowed or refused a SQL text.</summary>
public enum GateReason
{
    /// <summary>The text is one side-effect-free SELECT statement built only from allowlisted syntax.</summary>
    Allowed,

    /// <summary>The text is null, empty or whitespace only.</summary>
    Empty,

    /// <summary>The text is longer than the configured maximum; it was not parsed.</summary>
    TooLong,

    /// <summary>The text did not lex or parse as T-SQL.</summary>
    ParseError,

    /// <summary>The text contains a <c>GO</c> batch separator or more than one batch.</summary>
    NotSingleBatch,

    /// <summary>The batch holds zero statements or more than one statement.</summary>
    NotSingleStatement,

    /// <summary>The one statement is not a plain <c>SELECT</c> statement.</summary>
    NotSelect,

    /// <summary>The statement is <c>SELECT ... INTO</c>, which creates a table.</summary>
    SelectInto,

    /// <summary>The statement assigns a variable (<c>SELECT @v = ...</c>).</summary>
    VariableAssignment,

    /// <summary>
    /// The statement contains a syntax node outside the allowlist, or the text contains a character the gate refuses
    /// (including text that is not well-formed UTF-16 or contains a Unicode noncharacter), or an ODBC escape token.
    /// </summary>
    DisallowedSyntax,

    /// <summary>A table hint other than <c>NOLOCK</c> or <c>READUNCOMMITTED</c>.</summary>
    DisallowedTableHint,

    /// <summary>A query hint: any <c>OPTION (...)</c> clause or a join hint.</summary>
    DisallowedQueryHint,

    /// <summary>A four-part (linked server) name.</summary>
    LinkedServerName,

    /// <summary>A three-part name whose database is not the configured default database.</summary>
    CrossDatabaseName,

    /// <summary>The text nests deeper than the gate can check safely.</summary>
    TooDeep,
}

/// <summary>The gate's verdict for one SQL text.</summary>
/// <param name="Allowed">True only when <paramref name="Reason"/> is <see cref="GateReason.Allowed"/>.</param>
/// <param name="Reason">Why the text was allowed or refused.</param>
/// <param name="Detail">
/// A short explanation built only from known-safe facts (the reason, a syntax node or token type name, a line and a
/// column); it never contains the SQL text.
/// </param>
/// <param name="Tables">
/// Referenced table or view names as written (identifier parts joined with dots, brackets and quotes removed),
/// distinct ignoring case, in order of first appearance in the text. A one-part name is left out only where it
/// names a common table expression in scope (exact spelling): inside CTE number i only CTEs 0..i are in scope, the
/// statement body sees all of them. Filled whenever the text parsed, also for refused text; empty otherwise. Always
/// empty for the <see cref="GateReason.DisallowedSyntax"/> refusals of text that is not well-formed UTF-16 (an
/// unpaired surrogate) or that contains a Unicode noncharacter (U+FDD0..U+FDEF, U+xFFFE, U+xFFFF), anywhere, also
/// inside a literal, quoted identifier or comment: that text is refused before it is parsed.
/// </param>
public sealed record GateDecision(bool Allowed, GateReason Reason, string Detail, IReadOnlyList<string> Tables);

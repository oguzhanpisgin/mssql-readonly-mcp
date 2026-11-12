using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlReadOnlyMcp.Gate;

/// <summary>
/// The explicit set of ScriptDom node types a read-only SELECT may contain. A node is accepted only when its exact
/// runtime type is listed here; subclasses of a listed type are not accepted. Every entry is written out by hand:
/// a new node type in a later ScriptDom version is refused until someone adds it on purpose.
/// </summary>
internal static class Allowlist
{
    /// <summary>The exact node types the gate accepts.</summary>
    internal static readonly IReadOnlySet<Type> NodeTypes = new HashSet<Type>
    {
        // Statement and query structure.
        typeof(SelectStatement),              // INTO, OPTION and COMPUTE are checked or refused separately.
        typeof(QuerySpecification),
        typeof(BinaryQueryExpression),        // UNION, EXCEPT, INTERSECT.
        typeof(QueryParenthesisExpression),
        typeof(WithCtesAndXmlNamespaces),     // XmlNamespaces is not listed, so WITH XMLNAMESPACES is refused.
        typeof(CommonTableExpression),
        typeof(SelectScalarExpression),
        typeof(SelectStarExpression),
        typeof(FromClause),
        typeof(WhereClause),
        typeof(GroupByClause),
        typeof(HavingClause),
        typeof(OrderByClause),
        typeof(OffsetClause),
        typeof(TopRowFilter),
        typeof(ExpressionWithSortOrder),
        typeof(WindowClause),
        typeof(WindowDefinition),

        // Grouping.
        typeof(ExpressionGroupingSpecification),
        typeof(RollupGroupingSpecification),
        typeof(CubeGroupingSpecification),
        typeof(GroupingSetsGroupingSpecification),
        typeof(CompositeGroupingSpecification),
        typeof(GrandTotalGroupingSpecification),

        // Table sources.
        typeof(NamedTableReference),          // Names, temp tables and hints are checked separately.
        typeof(QualifiedJoin),                // Join hints (LOOP, HASH, MERGE, REMOTE ...) are refused separately.
        typeof(UnqualifiedJoin),              // CROSS JOIN, CROSS APPLY, OUTER APPLY.
        typeof(JoinParenthesisTableReference),
        typeof(QueryDerivedTable),
        typeof(InlineDerivedTable),           // (VALUES (...)) AS v(...).
        typeof(RowValue),
        typeof(PivotedTableReference),        // The aggregate must be a one-part (built-in) name, checked separately.
        typeof(UnpivotedTableReference),
        typeof(TableHint),                    // Only NOLOCK and READUNCOMMITTED, checked separately.

        // Names.
        typeof(Identifier),
        typeof(MultiPartIdentifier),
        typeof(SchemaObjectName),             // Four-part and three-part names are checked separately.
        typeof(IdentifierOrValueExpression),
        typeof(ColumnReferenceExpression),    // Five-part and four-part column names are checked separately.

        // Scalar expressions.
        typeof(BinaryExpression),
        typeof(UnaryExpression),
        typeof(ParenthesisExpression),
        typeof(ScalarSubquery),
        typeof(SearchedCaseExpression),
        typeof(SimpleCaseExpression),
        typeof(SearchedWhenClause),
        typeof(SimpleWhenClause),
        typeof(CastCall),
        typeof(ConvertCall),
        typeof(TryCastCall),
        typeof(TryConvertCall),
        typeof(SqlDataTypeReference),         // Built-in types only; user, XML and vector types are not listed.
        typeof(CoalesceExpression),
        typeof(NullIfExpression),
        typeof(IIfCall),
        typeof(FunctionCall),                 // No call target node is listed, so dbo.fn(), x.method() are refused.
        typeof(LeftFunctionCall),
        typeof(RightFunctionCall),
        typeof(ParameterlessCall),            // CURRENT_TIMESTAMP only; other call types are refused separately.
        typeof(AtTimeZoneCall),
        typeof(OverClause),
        typeof(WindowFrameClause),
        typeof(WindowDelimiter),
        typeof(WithinGroupClause),

        // Literals.
        typeof(IntegerLiteral),
        typeof(NumericLiteral),
        typeof(RealLiteral),
        typeof(MoneyLiteral),
        typeof(StringLiteral),
        typeof(BinaryLiteral),
        typeof(NullLiteral),
        typeof(MaxLiteral),                   // varchar(max).
        typeof(IdentifierLiteral),            // DATEPART(year, ...).

        // Predicates.
        typeof(BooleanBinaryExpression),
        typeof(BooleanComparisonExpression),
        typeof(BooleanIsNullExpression),
        typeof(BooleanNotExpression),
        typeof(BooleanParenthesisExpression),
        typeof(BooleanTernaryExpression),     // BETWEEN.
        typeof(ExistsPredicate),
        typeof(InPredicate),
        typeof(LikePredicate),
        typeof(SubqueryComparisonPredicate),  // = ANY / ALL / SOME (subquery).
        typeof(DistinctPredicate),            // IS [NOT] DISTINCT FROM.
    };

    /// <summary>
    /// The built-in function names a <see cref="FunctionCall"/> (or a PIVOT aggregate) may use, compared ignoring
    /// case. Only names ScriptDom 180.140.0 parses as <see cref="FunctionCall"/> are listed: LEFT and RIGHT are
    /// their own node types, CURRENT_TIMESTAMP is a <see cref="ParameterlessCall"/>, and CAST, CONVERT, TRY_CAST,
    /// TRY_CONVERT, COALESCE, NULLIF and IIF have their own nodes. Nothing here reaches outside the query or
    /// exposes metadata or security material; any other name, including a future built-in, is refused.
    /// </summary>
    internal static readonly IReadOnlySet<string> FunctionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Aggregates.
        "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG",
        "GROUPING", "GROUPING_ID", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",

        // Ranking and window.
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "PERCENT_RANK",
        "CUME_DIST", "PERCENTILE_CONT", "PERCENTILE_DISC",

        // String.
        "LEN", "DATALENGTH", "SUBSTRING", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "REPLACE", "REPLICATE",
        "REVERSE", "STUFF", "CHARINDEX", "PATINDEX", "CONCAT", "CONCAT_WS", "FORMAT", "STR", "SPACE", "QUOTENAME",
        "ASCII", "CHAR", "NCHAR", "UNICODE", "TRANSLATE", "STRING_ESCAPE",

        // Date and time.
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "DATEADD", "DATEDIFF",
        "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATEFROMPARTS", "DATETIMEFROMPARTS",
        "DATETIME2FROMPARTS", "TIMEFROMPARTS", "EOMONTH", "YEAR", "MONTH", "DAY", "ISDATE", "SWITCHOFFSET",
        "TODATETIMEOFFSET",

        // Math.
        "ABS", "ROUND", "CEILING", "FLOOR", "SIGN", "POWER", "SQRT", "SQUARE", "EXP", "LOG", "LOG10", "PI",
        "SIN", "COS", "TAN", "COT", "ASIN", "ACOS", "ATAN", "ATN2", "DEGREES", "RADIANS",

        // Null handling and choice.
        "ISNULL", "ISNUMERIC", "CHOOSE", "GREATEST", "LEAST",

        // JSON readers.
        "ISJSON", "JSON_VALUE", "JSON_QUERY",

        // Hashing and identifiers.
        "CHECKSUM", "BINARY_CHECKSUM", "HASHBYTES", "NEWID",
    };

    /// <summary>
    /// The statement check: true only for the plain <see cref="SelectStatement"/> type. Its ScriptDom subclass
    /// <c>SelectStatementSnippet</c> is a script-generation placeholder, not produced by parsing, and is refused.
    /// </summary>
    internal static bool IsAllowedStatementType(Type statementType) => statementType == typeof(SelectStatement);
}

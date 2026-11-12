# What SQL is allowed

This page covers what the read-only gate lets through `run_query` and what it refuses; the
[README](../README.md#what-it-promises) has the promises and the refused-query diagram.

`run_query` accepts one plain SELECT statement built only from these forms (node types listed in
`src/MssqlReadOnlyMcp.Gate/Allowlist.cs`, field `NodeTypes`: 84 types at the time of writing; recount with the
number of `typeof(` lines in that list):

- `SELECT` with `DISTINCT`, `TOP (n)` (also `PERCENT` and `WITH TIES`), `WHERE`, `GROUP BY` (also `ROLLUP`, `CUBE`,
  `GROUPING SETS`), `HAVING`, `ORDER BY`, `OFFSET ... FETCH`, and the `WINDOW` clause.
- `WITH` common table expressions, recursive ones too.
- `UNION`, `EXCEPT`, `INTERSECT`, and parenthesised queries.
- Joins: `INNER`, `LEFT`, `RIGHT`, `FULL`, `CROSS JOIN`, `CROSS APPLY`, `OUTER APPLY`; derived tables;
  `(VALUES ...) AS v(...)`; `PIVOT` and `UNPIVOT` with an aggregate from the list below.
- Subqueries, `CASE`, `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT`, `COALESCE`, `NULLIF`, `IIF`, `LEFT`, `RIGHT`,
  `CURRENT_TIMESTAMP`, `AT TIME ZONE`, window functions with `OVER (PARTITION BY ... ORDER BY ... ROWS/RANGE ...)`
  and `WITHIN GROUP`.
- Predicates: comparisons, `AND`, `OR`, `NOT`, `BETWEEN`, `IN`, `LIKE`, `IS [NOT] NULL`, `EXISTS`,
  `= ANY / ALL / SOME (subquery)`, `IS [NOT] DISTINCT FROM`.
- Table hints `NOLOCK` and `READUNCOMMITTED` only.
- One-part and two-part names; three-part names only when the database part is exactly the connected database
  (same case).

Function names allowed in a function call (`Allowlist.cs`, field `FunctionNames`: 109 names at the time of writing,
compared ignoring case):

- Aggregates: `COUNT`, `COUNT_BIG`, `SUM`, `AVG`, `MIN`, `MAX`, `STDEV`, `STDEVP`, `VAR`, `VARP`, `STRING_AGG`,
  `GROUPING`, `GROUPING_ID`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`.
- Ranking and window: `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `NTILE`, `LAG`, `LEAD`, `FIRST_VALUE`, `LAST_VALUE`,
  `PERCENT_RANK`, `CUME_DIST`, `PERCENTILE_CONT`, `PERCENTILE_DISC`.
- String: `LEN`, `DATALENGTH`, `SUBSTRING`, `LTRIM`, `RTRIM`, `TRIM`, `UPPER`, `LOWER`, `REPLACE`, `REPLICATE`,
  `REVERSE`, `STUFF`, `CHARINDEX`, `PATINDEX`, `CONCAT`, `CONCAT_WS`, `FORMAT`, `STR`, `SPACE`, `QUOTENAME`, `ASCII`,
  `CHAR`, `NCHAR`, `UNICODE`, `TRANSLATE`, `STRING_ESCAPE`.
- Date and time: `GETDATE`, `GETUTCDATE`, `SYSDATETIME`, `SYSUTCDATETIME`, `SYSDATETIMEOFFSET`, `DATEADD`,
  `DATEDIFF`, `DATEDIFF_BIG`, `DATEPART`, `DATENAME`, `DATETRUNC`, `DATEFROMPARTS`, `DATETIMEFROMPARTS`,
  `DATETIME2FROMPARTS`, `TIMEFROMPARTS`, `EOMONTH`, `YEAR`, `MONTH`, `DAY`, `ISDATE`, `SWITCHOFFSET`,
  `TODATETIMEOFFSET`.
- Math: `ABS`, `ROUND`, `CEILING`, `FLOOR`, `SIGN`, `POWER`, `SQRT`, `SQUARE`, `EXP`, `LOG`, `LOG10`, `PI`, `SIN`,
  `COS`, `TAN`, `COT`, `ASIN`, `ACOS`, `ATAN`, `ATN2`, `DEGREES`, `RADIANS`.
- Null handling and choice: `ISNULL`, `ISNUMERIC`, `CHOOSE`, `GREATEST`, `LEAST`.
- JSON readers: `ISJSON`, `JSON_VALUE`, `JSON_QUERY`.
- Hashing and identifiers: `CHECKSUM`, `BINARY_CHECKSUM`, `HASHBYTES`, `NEWID`.

Anything not listed is refused by design, including a built-in that a later SQL Server version adds. That covers:

- user-defined functions (`dbo.fn(...)`) and table-valued functions, also system ones such as `sys.fn_get_audit_file`;
- variables (`@v`, `@@VERSION`), table variables and temporary tables (`#t`);
- table hints other than `NOLOCK` and `READUNCOMMITTED`, join hints and `OPTION (...)`;
- cross-database and linked-server names, `OPENQUERY`, `OPENROWSET`, `OPENDATASOURCE`, `OPENJSON`, `OPENXML`;
- `FOR XML`, `FOR JSON`, `WITH XMLNAMESPACES`, `NEXT VALUE FOR`;
- metadata and identity functions (`OBJECT_ID`, `DB_NAME`, `CURRENT_USER`, `SESSION_USER`, `SYSTEM_USER`, `USER`
  and the rest), `PARSE`, the JSON builders, and `CURRENT_DATE`.

If you need a function that is not listed, open an issue with the query you wanted to run. A name is added only
after it is checked not to reach outside the query or expose metadata or security material.

## Members of CLR user-defined types

Reading a field or property of a CLR user-defined type written as a column name (`t.Col.X`), and `CAST` or
`CONVERT` of such a column to text, pass the gate; a property getter and the conversions run the type's code
(`ToString`). SQL Server asks `EXECUTE` on the type for each of them: a reader without that right got error 229, and
the login check names a direct `EXECUTE` grant on a CLR type as a finding. The instance method calls tried
(`t.Col.ToString()`, `t.Col.Twice()`) are refused by the gate (`DisallowedSyntax`); the raw value is returned
undecoded as `<unsupported value of type ...>`. Measured for the test suite's one native-format type on SQL Server
2019, 2022 and 2025 (`tests/MssqlReadOnlyMcp.IntegrationTests/ClrTypeMemberTests.cs`).

## Syntax newer than SQL Server 2019

- On SQL Server 2019 some allowed syntax is newer than the server: `IS [NOT] DISTINCT FROM`, the `WINDOW` clause,
  `TRIM(LEADING ...)`, `DATETRUNC`, `GREATEST` and `LEAST` pass the gate and fail on the server with a syntax error
  (156 or 102), or with error 195 (not a recognized built-in function: `DATETRUNC`, `GREATEST`, `LEAST`) (list
  `NewIn2022` in `tests/MssqlReadOnlyMcp.IntegrationTests/DifferentialTests.cs`). Nothing runs; the call returns
  `SqlError`.

## Checks on the text and the tokens

These checks can only refuse; they run before or beside the syntax tree (`src/MssqlReadOnlyMcp.Gate/ReadOnlyGate.cs`).
Each refusal is `DisallowedSyntax` unless named otherwise.

- Before parsing, anywhere in the text (also inside literals and comments): text that is not well-formed UTF-16
  (an unpaired surrogate), and the 66 Unicode noncharacters (U+FDD0 to U+FDEF, and the last two code points of
  every plane). The tests show the unpaired-surrogate refusal by calling the gate directly
  (`RegressionTableTests.cs`); over stdio a `run_query` call with one in `sql` does not reach the gate
  ([Requests with no audit line](audit-log.md#requests-with-no-audit-line)).
- ODBC escape tokens outside literals, quoted names and comments: `{fn ...}`, `{d ...}`, `{oj ...}` and any other
  brace, and `--(*`. So `LIKE 'a\_b' {escape '\'}` is refused, while `LIKE 'a\_b' ESCAPE '\'` is allowed. A brace
  inside a string literal or a comment is allowed.
- Outside literals and comments: Unicode space characters other than U+0020, line and paragraph separators, format
  characters (for example U+200B, U+200D, U+FEFF), control characters other than TAB, CR and LF, U+FFFD, private-use
  and unassigned code points.
- Inside bracketed and double-quoted names: the same space, separator, format and control characters.
- A line comment (`--`) that contains a control character other than TAB, or a line or paragraph separator.
- A `GO` that ScriptDom reads as a batch separator, or more than one batch (`NotSingleBatch`). The tested rows put
  `GO` on its own line, among them: `SELECT 1` newline `GO` newline `SELECT 2`, `GO` newline `SELECT 1`, and
  `SELECT 1` newline `GO` (`RegressionTableTests.cs`). The gate returns a parse error before it looks for `GO`, so
  `SELECT 1 x` newline `GO 2` is refused as `ParseError`. A `GO` the parser reads as a column alias is one statement:
  `SELECT 1 GO` is allowed and returns one column named `GO`. These two were measured with `run_query` on SQL Server
  2019, 2022 and 2025; no test holds them.
- More than 64 nested parentheses (`TooDeep`).

The gate parses with `TSql170Parser` (`ReadOnlyGate.cs`), which the package documents as the parser for version
17.0; the package also ships `TSql180Parser`, which the gate does not use. `StatementTypeTests.cs`
(`ScriptDom_is_at_least_the_version_with_the_U_FFFF_end_of_input_fix`) asserts that the loaded ScriptDom file is
version 18.0.140.0 or later (NuGet package 180.140.0); earlier versions stopped reading at U+FFFF.

The regression table (`tests/MssqlReadOnlyMcp.Gate.Tests/RegressionTableTests.cs`) has 71 allowed and 247 refused
rows at the time of writing (count the `Ok(` and `No(` rows).

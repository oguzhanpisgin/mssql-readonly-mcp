using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// The regression table: each row is a SQL text, whether it must be allowed, and the expected reason. It replays
/// the public bypass classes (stacked statements, GO batches, dynamic SQL, DML in or after a CTE, conditional and
/// nested comments, text hidden after a line comment) and every statement family a read-only gate must refuse.
/// All rows run with <c>DefaultDatabase = "Sales"</c>.
/// </summary>
public sealed class RegressionTableTests
{
    public static readonly GateOptions Options = new(DefaultDatabase: "Sales");

    public sealed record Row(string Sql, bool Allowed, GateReason Reason, string? DetailPrefix = null);

    private static Row Ok(string sql) => new(sql, true, GateReason.Allowed);

    private static Row No(string sql, GateReason reason, string? detail = null) => new(sql, false, reason, detail);

    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    // One UTF-16 code unit, so a lone surrogate can be built (ConvertFromUtf32 rejects surrogate code points).
    private static string C(int codeUnit) => ((char)codeUnit).ToString();

    // Declared before Rows: static fields initialise in text order.
    private static readonly string AmbiguousCharacters =
        string.Concat(new[] { 0x200B, 0xFEFF, 0x180E, 0x1680, 0x2003, 0x205F, 0x00A0, 0x3000 }.Select(U));

    public static readonly IReadOnlyList<Row> Rows =
    [
        // ---- Allowed: ordinary reporting SQL. ----
        Ok("SELECT 1"),
        Ok("SELECT 1;"),
        Ok("SELECT 1; -- trailing comment"),
        Ok("/* leading */ SELECT Id FROM dbo.Orders -- trailing"),
        Ok("SELECT 1 /* outer /* nested */ still comment */"),
        Ok("SELECT 1 /*!50000 DROP TABLE x */"), // MySQL conditional comment: on SQL Server it is only a comment.
        Ok("SELECT 1 -- a\tcomment with a tab"),
        Ok("SELECT * FROM dbo.Orders o WITH (NOLOCK) WHERE o.Id = 5"),
        Ok("SELECT x.* FROM dbo.Orders x WITH (READUNCOMMITTED)"),
        Ok("SELECT * FROM dbo.Orders (NOLOCK)"),
        Ok("SELECT DISTINCT TOP (10) PERCENT WITH TIES o.Id, o.Total * 1.5 AS t, -o.Total, ~o.Flags FROM dbo.Orders AS o ORDER BY o.Id DESC"),
        Ok("SELECT TOP 5 Id FROM Orders"),
        Ok("SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId LEFT OUTER JOIN dbo.C c ON c.Id = a.Id RIGHT JOIN dbo.D d ON d.Id = a.Id FULL JOIN dbo.E e ON e.Id = a.Id CROSS JOIN dbo.F f"),
        Ok("SELECT a.Id, x.Cnt FROM dbo.A a CROSS APPLY (SELECT COUNT(*) AS Cnt FROM dbo.B b WHERE b.AId = a.Id) x OUTER APPLY (SELECT TOP 1 c.Id FROM dbo.C c) y"),
        Ok("SELECT * FROM (dbo.A a JOIN dbo.B b ON a.Id = b.Id)"),
        Ok("WITH c AS (SELECT Id FROM dbo.A), d (X) AS (SELECT Id FROM c) SELECT * FROM d"),
        Ok("WITH r AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM r WHERE n < 10) SELECT n FROM r"),
        Ok("SELECT Id FROM dbo.A UNION SELECT Id FROM dbo.B EXCEPT SELECT Id FROM dbo.C INTERSECT SELECT Id FROM dbo.D"),
        Ok("(SELECT Id FROM dbo.A) ORDER BY Id"),
        Ok("SELECT Id FROM dbo.A WHERE EXISTS (SELECT 1 FROM dbo.B) AND Id IN (1, 2, 3) AND Id NOT IN (SELECT Id FROM dbo.C) AND Id BETWEEN 1 AND 5 AND Name LIKE 'a%' ESCAPE '\\' AND Name IS NULL AND Code IS NOT NULL OR NOT (Id <> 3) AND Id > ALL (SELECT Id FROM dbo.D)"),
        Ok("SELECT (SELECT MAX(Id) FROM dbo.B) AS m, CASE WHEN Id > 1 THEN 'a' ELSE 'b' END, CASE Id WHEN 1 THEN 'x' END FROM dbo.A"),
        Ok("SELECT CAST(Id AS varchar(10)), CONVERT(nvarchar(max), Id, 120), TRY_CAST(Id AS int), TRY_CONVERT(decimal(10, 2), Id), COALESCE(a, b, 1), NULLIF(a, 0), IIF(a > 1, 1, 0) FROM dbo.A"),
        Ok("SELECT ROW_NUMBER() OVER (PARTITION BY g ORDER BY Id) AS rn, SUM(x) OVER (ORDER BY Id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.A"),
        Ok("SELECT AVG(x) OVER (PARTITION BY g ORDER BY Id ROWS BETWEEN 1 PRECEDING AND 2 FOLLOWING), LAG(x, 1) OVER (ORDER BY Id), NTILE(4) OVER (ORDER BY Id) FROM dbo.A"),
        Ok("SELECT COUNT(DISTINCT y), COUNT(*), LEN(N'abc'), GETDATE(), DATEADD(day, 1, d), DATEPART(year, d) FROM dbo.A GROUP BY d"),
        Ok("SELECT g, SUM(x) FROM dbo.A GROUP BY g HAVING SUM(x) > 0 ORDER BY g OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY"),
        Ok("SELECT g, h, SUM(x) FROM dbo.A GROUP BY ROLLUP(g, h)"),
        Ok("SELECT g, h, SUM(x) FROM dbo.A GROUP BY CUBE(g, h)"),
        Ok("SELECT g, h, SUM(x) FROM dbo.A GROUP BY GROUPING SETS ((g), (h), ())"),
        Ok("SELECT * FROM (SELECT Id FROM dbo.A) AS t(Id)"),
        Ok("SELECT 1.5e3, 0x1F, $5, NULL, N'x', 'y', -1, 1 % 2, 'a' + 'b', [weird col], \"quoted\" FROM dbo.A"),
        Ok("SELECT * FROM (VALUES (1, 2), (3, 4)) AS v(a, b)"),
        Ok("SELECT * FROM dbo.A PIVOT (SUM(x) FOR g IN ([1], [2])) p"),
        Ok("SELECT * FROM dbo.A UNPIVOT (v FOR c IN (x, y)) u"),
        Ok("SELECT STRING_AGG(Name, ',') WITHIN GROUP (ORDER BY Name) FROM dbo.A"),
        Ok("SELECT CURRENT_TIMESTAMP, Name COLLATE Latin1_General_CI_AS, d AT TIME ZONE 'UTC' FROM dbo.A"),
        Ok("SELECT x FROM dbo.A WHERE x IS DISTINCT FROM 1"),
        Ok("SELECT Id = a.Id, a.Id AS [X], LEFT(a.Name, 1), RIGHT(a.Name, 1) FROM dbo.A a"),
        Ok("SELECT SUM(x) OVER w FROM dbo.A WINDOW w AS (PARTITION BY g)"),
        Ok("SELECT Id FROM Sales.dbo.Orders"),
        No("SELECT o.Id FROM SALES.dbo.Orders o", GateReason.CrossDatabaseName), // Exact spelling only.
        Ok("SELECT Sales.dbo.Orders.Id FROM Sales.dbo.Orders"),
        Ok("SELECT " + new string('(', 64) + "1" + new string(')', 64)),

        // ---- Refused: stacked statements, batches, dynamic SQL. ----
        No("SELECT 1; DROP TABLE x", GateReason.NotSingleStatement),
        No("SELECT 1 DROP TABLE x", GateReason.NotSingleStatement),
        No("SELECT 1 UNION SELECT 2; SELECT 3", GateReason.NotSingleStatement),
        No("SELECT 1\nGO\nDROP TABLE x", GateReason.NotSingleBatch),
        No("SELECT 1\nGO\nSELECT 2", GateReason.NotSingleBatch), // Probe K17/K18: the server reads GO as an alias and returns two result sets.
        No("GO\nSELECT 1", GateReason.NotSingleBatch), // The server would see a call of a procedure named GO.
        No("SELECT 1\nGO", GateReason.NotSingleBatch),
        No("EXEC('DELETE FROM dbo.Orders')", GateReason.NotSelect),
        No("EXEC sp_executesql N'DELETE FROM dbo.Orders'", GateReason.NotSelect),
        No("EXECUTE dbo.usp_Report", GateReason.NotSelect),
        No("sp_who", GateReason.NotSelect),
        No("", GateReason.Empty),
        No("  \t\r\n ", GateReason.Empty),
        No("-- only a comment", GateReason.NotSingleStatement),
        No(";", GateReason.NotSingleStatement),

        // ---- Refused: DML in or after a CTE, MERGE. ----
        No("WITH c AS (SELECT Id FROM dbo.Orders) UPDATE c SET Id = 1", GateReason.NotSelect),
        No("WITH c AS (SELECT Id FROM dbo.Orders) DELETE FROM c", GateReason.NotSelect),
        No("WITH c AS (SELECT Id FROM dbo.Orders) INSERT INTO dbo.T SELECT Id FROM c", GateReason.NotSelect),
        No("WITH c AS (UPDATE dbo.Orders SET Id = 1) SELECT 1", GateReason.ParseError),
        No("MERGE dbo.T AS t USING dbo.S AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", GateReason.NotSelect),

        // ---- Refused: comments that hide text. ----
        No("/*!50000 DROP TABLE x */", GateReason.NotSingleStatement),
        No("SELECT 1 /* a /* b */ DROP TABLE x", GateReason.ParseError),
        No("SELECT 1 /* unterminated", GateReason.ParseError),
        No("SELECT 1 -- hidden\nDROP TABLE x", GateReason.NotSingleStatement),
        No("SELECT 1 -- hidden\rDROP TABLE x", GateReason.NotSingleStatement),
        No("SELECT 1 -- hidden\u2028DROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),
        No("SELECT 1 -- hidden\u2029DROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),
        No("SELECT 1 -- hidden\u0085DROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),
        No("SELECT 1 -- hidden\vDROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),
        No("SELECT 1 -- hidden\fDROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),
        No("SELECT 1 -- hidden\0DROP TABLE x", GateReason.DisallowedSyntax, "SingleLineComment"),

        // ---- Refused: DML, DDL, security, server and session statements. ----
        No("INSERT INTO dbo.T VALUES (1)", GateReason.NotSelect),
        No("UPDATE dbo.T SET a = 1", GateReason.NotSelect),
        No("DELETE FROM dbo.T", GateReason.NotSelect),
        No("TRUNCATE TABLE dbo.T", GateReason.NotSelect),
        No("CREATE TABLE dbo.T (a int)", GateReason.NotSelect),
        No("ALTER TABLE dbo.T ADD b int", GateReason.NotSelect),
        No("DROP TABLE dbo.T", GateReason.NotSelect),
        No("CREATE VIEW dbo.V AS SELECT 1 AS a", GateReason.NotSelect),
        No("ALTER VIEW dbo.V AS SELECT 2 AS a", GateReason.NotSelect),
        No("DROP VIEW dbo.V", GateReason.NotSelect),
        No("CREATE PROCEDURE dbo.P AS SELECT 1", GateReason.NotSelect),
        No("CREATE FUNCTION dbo.F() RETURNS int AS BEGIN RETURN 1 END", GateReason.NotSelect),
        No("CREATE INDEX ix ON dbo.T (a)", GateReason.NotSelect),
        No("CREATE LOGIN x WITH PASSWORD = 'p@ssw0rd!x'", GateReason.NotSelect),
        No("ALTER DATABASE Sales SET SINGLE_USER", GateReason.NotSelect),
        No("DROP DATABASE Sales", GateReason.NotSelect),
        No("GRANT SELECT ON dbo.T TO public", GateReason.NotSelect),
        No("DENY SELECT ON dbo.T TO public", GateReason.NotSelect),
        No("REVOKE SELECT ON dbo.T FROM public", GateReason.NotSelect),
        No("WAITFOR DELAY '00:00:10'", GateReason.NotSelect),
        No("BACKUP DATABASE Sales TO DISK = 'c:\\x.bak'", GateReason.NotSelect),
        No("DBCC CHECKDB", GateReason.NotSelect),
        No("SHUTDOWN", GateReason.NotSelect),
        No("USE master", GateReason.NotSelect),
        No("SET NOCOUNT ON", GateReason.NotSelect),
        No("DECLARE @v int", GateReason.NotSelect),
        No("BEGIN TRAN", GateReason.NotSelect),
        No("COMMIT", GateReason.NotSelect),
        No("KILL 52", GateReason.NotSelect),
        No("RECONFIGURE", GateReason.NotSelect),
        No("IF 1 = 1 SELECT 1", GateReason.NotSelect),
        No("BEGIN SELECT 1 END", GateReason.NotSelect),
        No("PRINT 1", GateReason.NotSelect),
        No("RAISERROR('x', 16, 1)", GateReason.NotSelect),

        // ---- Refused: SELECT forms with side effects or hidden reach. ----
        No("SELECT * INTO dbo.Copy FROM dbo.Orders", GateReason.SelectInto),
        No("SELECT Id INTO #t FROM dbo.Orders", GateReason.SelectInto),
        No("SELECT @v = Id FROM dbo.Orders", GateReason.VariableAssignment),
        No("SELECT Id FROM dbo.Orders WHERE Id = @id", GateReason.DisallowedSyntax, "VariableReference"),
        No("SELECT @@VERSION", GateReason.DisallowedSyntax, "GlobalVariableExpression"), // A global variable reference is a variable reference.
        No("SELECT TOP (@n) Id FROM dbo.Orders", GateReason.DisallowedSyntax, "VariableReference"),
        No("SELECT NEXT VALUE FOR dbo.Seq", GateReason.DisallowedSyntax, "NextValueForExpression"),
        No("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes;', 'SELECT 1') AS a", GateReason.DisallowedSyntax, "OpenRowsetTableReference"),
        No("SELECT * FROM OPENROWSET(BULK 'c:\\x.txt', SINGLE_CLOB) AS a", GateReason.DisallowedSyntax, "BulkOpenRowset"),
        No("SELECT * FROM OPENQUERY(srv, 'SELECT 1')", GateReason.DisallowedSyntax, "OpenQueryTableReference"),
        No("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", GateReason.DisallowedSyntax, "AdHocTableReference"),
        No("SELECT * FROM OPENXML(@h, '/x')", GateReason.DisallowedSyntax, "OpenXmlTableReference"),
        No("SELECT * FROM OPENJSON(N'[1]')", GateReason.DisallowedSyntax, "OpenJsonTableReference"),
        No("SELECT * FROM STRING_SPLIT('a,b', ',')", GateReason.DisallowedSyntax, "GlobalFunctionTableReference"),
        No("SELECT * FROM GENERATE_SERIES(1, 10)", GateReason.DisallowedSyntax, "GlobalFunctionTableReference"),
        No("SELECT * FROM dbo.fn_Orders(1)", GateReason.DisallowedSyntax, "SchemaObjectFunctionTableReference"),
        No("SELECT * FROM sys.fn_get_audit_file('x', NULL, NULL)", GateReason.DisallowedSyntax, "SchemaObjectFunctionTableReference"),
        No("SELECT a.Id FROM dbo.A a CROSS APPLY dbo.fn_Lines(a.Id) l", GateReason.DisallowedSyntax, "SchemaObjectFunctionTableReference"),
        No("SELECT dbo.fn_Scalar(1)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT x.value('a', 'int') FROM dbo.A", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT CAST(1 AS dbo.MyType)", GateReason.DisallowedSyntax, "UserDataTypeReference"),
        No("SELECT JSON_OBJECT('a':1)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT $PARTITION.pf(1)", GateReason.DisallowedSyntax, "PartitionFunctionCall"),
        No("SELECT {fn NOW()}", GateReason.DisallowedSyntax, "LeftCurly token"), // OdbcFunctionCall is also off the allowlist.
        No("SELECT * FROM CONTAINSTABLE(dbo.A, *, 'x')", GateReason.DisallowedSyntax, "FullTextTableReference"),
        No("SELECT * FROM dbo.A TABLESAMPLE (10 PERCENT)", GateReason.DisallowedSyntax, "TableSampleClause"),
        No("SELECT * FROM dbo.A FOR SYSTEM_TIME AS OF '2020-01-01'", GateReason.DisallowedSyntax, "TemporalClause"),
        No("WITH XMLNAMESPACES ('urn:x' AS x) SELECT 1", GateReason.DisallowedSyntax, "XmlNamespaces"),
        No("SELECT * FROM dbo.Orders FOR XML PATH", GateReason.DisallowedSyntax, "XmlForClause"),
        No("SELECT * FROM dbo.Orders FOR JSON AUTO", GateReason.DisallowedSyntax, "JsonForClause"),
        No("SELECT * FROM dbo.Orders FOR BROWSE", GateReason.DisallowedSyntax, "BrowseForClause"),
        No("SELECT * FROM #t", GateReason.DisallowedSyntax, "NamedTableReference (temporary table)"),
        No("SELECT * FROM ##g", GateReason.DisallowedSyntax, "NamedTableReference (temporary table)"),
        No("SELECT * FROM @t", GateReason.DisallowedSyntax, "VariableTableReference"),

        // ---- Refused: hints. ----
        No("SELECT * FROM dbo.Orders WITH (TABLOCKX)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (XLOCK)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (HOLDLOCK)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (UPDLOCK)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (NOLOCK, UPDLOCK)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (READPAST)", GateReason.DisallowedTableHint, "TableHint"),
        No("SELECT * FROM dbo.Orders WITH (INDEX(1))", GateReason.DisallowedTableHint, "IndexTableHint"),
        No("SELECT * FROM dbo.Orders WITH (FORCESEEK)", GateReason.DisallowedTableHint, "ForceSeekTableHint"),
        No("SELECT * FROM dbo.Orders OPTION (RECOMPILE)", GateReason.DisallowedQueryHint, "OptimizerHint"),
        No("SELECT * FROM dbo.Orders OPTION (MAXDOP 1)", GateReason.DisallowedQueryHint, "OptimizerHint"),
        No("SELECT * FROM dbo.A a INNER LOOP JOIN dbo.B b ON a.Id = b.Id", GateReason.DisallowedQueryHint, "QualifiedJoin"),

        // ---- Refused: linked server and cross-database names. ----
        No("SELECT * FROM srv.Sales.dbo.Orders", GateReason.LinkedServerName),
        No("SELECT * FROM srv...Orders", GateReason.LinkedServerName),
        No("SELECT srv.Sales.dbo.Orders.Id FROM dbo.Orders", GateReason.LinkedServerName),
        No("SELECT * FROM Other.dbo.Orders", GateReason.CrossDatabaseName),
        No("SELECT * FROM master..sysdatabases", GateReason.CrossDatabaseName),
        No("SELECT * FROM ..Orders", GateReason.CrossDatabaseName),
        No("SELECT Other.dbo.Orders.Id FROM dbo.Orders", GateReason.CrossDatabaseName),
        No("SELECT o.Id FROM dbo.Orders o JOIN Other.dbo.Lines l ON l.OrderId = o.Id", GateReason.CrossDatabaseName),

        // ---- Built-in function names (G-1): allowed names from the list. ----
        Ok("SELECT COUNT_BIG(*), STDEV(Total), APPROX_COUNT_DISTINCT(CustomerId) FROM dbo.Orders"),
        Ok("SELECT DENSE_RANK() OVER (ORDER BY Total), CUME_DIST() OVER (ORDER BY Total) FROM dbo.Orders"),
        Ok("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Total) OVER (PARTITION BY CustomerId) FROM dbo.Orders"),
        Ok("SELECT CONCAT_WS(',', Name, Code), REPLACE(Name, 'a', 'b'), CHARINDEX('a', Name), QUOTENAME(Name) FROM dbo.A"),
        Ok("SELECT TRIM(LEADING 'a' FROM Name), STRING_ESCAPE(Name, 'json'), TRANSLATE(Name, 'ab', 'cd') FROM dbo.A"),
        Ok("SELECT DATEDIFF(day, d, GETUTCDATE()), DATETRUNC(month, d), EOMONTH(d), DATEFROMPARTS(2026, 1, 1) FROM dbo.A"),
        Ok("SELECT SYSDATETIMEOFFSET(), SWITCHOFFSET(SYSDATETIMEOFFSET(), '+01:00'), DATENAME(month, d) FROM dbo.A"),
        Ok("SELECT ROUND(x, 2), CEILING(x), POWER(x, 2), SQRT(ABS(x)), LOG10(ABS(x) + 1), ATN2(x, 1), PI() FROM dbo.A"),
        Ok("SELECT ISNULL(x, 0), CHOOSE(1, 'a', 'b'), GREATEST(x, 1), LEAST(x, 1), ISNUMERIC(Code) FROM dbo.A"),
        Ok("SELECT ISJSON(Doc), JSON_VALUE(Doc, '$.a'), JSON_QUERY(Doc, '$.b') FROM dbo.A"),
        Ok("SELECT CHECKSUM(*), BINARY_CHECKSUM(Name), HASHBYTES('SHA2_256', Name), NEWID() FROM dbo.A"),
        Ok("select count(*), Max(x), datepart(Year, d) from dbo.A group by d"),

        // ---- Built-in function names (G-1): names left off the list. ----
        No("SELECT DB_ID()", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT DB_NAME(1)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT OBJECT_ID('dbo.Orders')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT OBJECT_NAME(1)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT IDENT_CURRENT('dbo.Orders')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT COL_LENGTH('dbo.Orders', 'Id')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT HAS_PERMS_BY_NAME('dbo.Orders', 'OBJECT', 'SELECT')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT CERTPRIVATEKEY(CERT_ID('c'), 'p')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT SIGNBYCERT(256, 'x')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT DECRYPTBYKEYAUTOCERT(256, NULL, Secret) FROM dbo.A", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT PWDCOMPARE('p', Hash) FROM dbo.A", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT LOGINPROPERTY('sa', 'IsLocked')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT SESSION_CONTEXT(N'k')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT CONTEXT_INFO()", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT APPLOCK_TEST('public', 'r', 'Shared', 'Session')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT RAND(5)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT AI_GENERATE_RESPONSE('x')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT AI_SUMMARIZE('x')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT INVOKE_EXTERNAL_API('x')", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT FUTURE_BUILTIN_X(1)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT Id FROM dbo.Orders WHERE Total > FUTURE_BUILTIN_X(Total)", GateReason.DisallowedSyntax, "FunctionCall (function"),
        No("SELECT * FROM dbo.A PIVOT (FUTURE_AGG(x) FOR g IN ([1], [2])) p", GateReason.DisallowedSyntax, "PivotedTableReference (aggregate function"),

        // ---- Lexer-ambiguous characters outside literals, quoted identifiers and comments (G-3). ----
        No("SELECT" + U(0x200B) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+200B"),
        No("SELECT" + U(0xFEFF) + "1", GateReason.DisallowedSyntax, "Identifier token with U+FEFF"),
        No("SELECT" + U(0x180E) + "1", GateReason.DisallowedSyntax, "Identifier token with U+180E"),
        No("SELECT" + U(0x1680) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+1680"),
        No("SELECT" + U(0x2003) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+2003"),
        No("SELECT" + U(0x205F) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+205F"),
        No("SELECT" + U(0x00A0) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+00A0"),
        No("SELECT" + U(0x3000) + "1", GateReason.DisallowedSyntax, "WhiteSpace token with U+3000"),
        No("SELECT a" + U(0x200B) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+200B"),
        No("SELECT a" + U(0xFEFF) + "b FROM dbo.A", GateReason.DisallowedSyntax, "Identifier token with U+FEFF"),
        No("SELECT a" + U(0x180E) + "b FROM dbo.A", GateReason.DisallowedSyntax, "Identifier token with U+180E"),
        No("SELECT a" + U(0x1680) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+1680"),
        No("SELECT a" + U(0x2003) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+2003"),
        No("SELECT a" + U(0x205F) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+205F"),
        No("SELECT a" + U(0x00A0) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+00A0"),
        No("SELECT a" + U(0x3000) + "b FROM dbo.A", GateReason.DisallowedSyntax, "WhiteSpace token with U+3000"),
        No(U(0xFEFF) + "SELECT 1", GateReason.DisallowedSyntax, "Identifier token with U+FEFF"), // A leading BOM.
        Ok("SELECT N'" + AmbiguousCharacters + "' AS x, '" + AmbiguousCharacters + "' AS y /* " + AmbiguousCharacters + " */"),
        // Until R5-4 this text was allowed; the characters inside the bracketed and double-quoted aliases now refuse it.
        No("SELECT N'" + AmbiguousCharacters + "' AS [x" + AmbiguousCharacters + "y], '" + AmbiguousCharacters + "' AS \"q" + AmbiguousCharacters + "\" /* " + AmbiguousCharacters + " */", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+200B"),

        // ---- Call targets refuse even for allowlisted names (N-1). ----
        No("SELECT dbo.LEN('a')", GateReason.DisallowedSyntax, "MultiPartIdentifierCallTarget"),
        No("SELECT x.COUNT() FROM dbo.A", GateReason.DisallowedSyntax, "MultiPartIdentifierCallTarget"),

        // ---- Parameterless calls: CURRENT_TIMESTAMP only (N-2). ----
        Ok("SELECT CURRENT_TIMESTAMP"),
        No("SELECT CURRENT_USER", GateReason.DisallowedSyntax, "ParameterlessCall (call type not on the allowlist)"),
        No("SELECT SESSION_USER", GateReason.DisallowedSyntax, "ParameterlessCall (call type not on the allowlist)"),
        No("SELECT SYSTEM_USER", GateReason.DisallowedSyntax, "ParameterlessCall (call type not on the allowlist)"),
        No("SELECT USER", GateReason.DisallowedSyntax, "ParameterlessCall (call type not on the allowlist)"),

        // CURRENT_DATE stays refused (W-2): SQL Server 2019 and 2022 do not have it.
        No("SELECT CURRENT_DATE", GateReason.DisallowedSyntax, "ParameterlessCall (call type not on the allowlist)"),

        // ---- U+FFFD, private-use and unassigned code points outside literals (N-3). ----
        No("SELECT a" + U(0xFFFD) + "b FROM dbo.A", GateReason.DisallowedSyntax, "Identifier token with U+FFFD"),
        No("SELECT a" + U(0xE000) + "b FROM dbo.A", GateReason.DisallowedSyntax, "Identifier token with U+E000"),
        No("SELECT a" + U(0x0378) + "b FROM dbo.A", GateReason.DisallowedSyntax, "Identifier token with U+0378"),

        // ---- Text that is not well-formed UTF-16 is refused anywhere, before parsing (W-1). The probe measured that
        // the server reads a lone surrogate as U+FFFD, also inside literals and quoted identifiers. ----
        No("SELECT " + C(0xD800) + " 1", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT " + C(0xDC00) + " 1", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT a" + C(0xD800) + "b FROM dbo.A", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT a" + C(0xDC00) + "b FROM dbo.A", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT 1 AS [a" + C(0xD800) + "b]", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT 1 AS [a" + C(0xDC00) + "b]", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT 1 AS \"a" + C(0xD800) + "b\"", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT 1 AS \"a" + C(0xDC00) + "b\"", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT N'a" + C(0xD800) + "b' AS x", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT N'a" + C(0xDC00) + "b' AS x", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT 'a" + C(0xD800) + "b' AS x", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT 'a" + C(0xDC00) + "b' AS x", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT 1 /* a" + C(0xD800) + "b */", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT 1 /* a" + C(0xDC00) + "b */", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        No("SELECT 1 -- a" + C(0xD800) + "b", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D800)"),
        No("SELECT 1 -- a" + C(0xDC00) + "b", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DC00)"),
        // A high surrogate as the last code unit, and a pair in the wrong order (low before high).
        No("SELECT 1 -- a" + C(0xD83D), GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+D83D)"),
        No("SELECT N'" + C(0xDE00) + C(0xD83D) + "' AS x", GateReason.DisallowedSyntax, "text is not well-formed UTF-16 (unpaired surrogate U+DE00)"),
        // A valid pair (U+1F600) stays allowed inside an N-string and a bracketed alias.
        Ok("SELECT N'" + U(0x1F600) + "' AS [a" + U(0x1F600) + "b]"),

        // ---- Probe constructs (W-3): each row is the gate's decision; the comment is the measured server outcome
        // (probe/summary.md). None of the allowed rows makes the server run more than ScriptDom saw. ----
        // K21: ScriptDom sees two statements (U+FF1B is an alias); the server fails with error 102.
        No("SELECT 1 " + U(0xFF1B) + " SELECT 2", GateReason.NotSingleStatement),
        // K22: ScriptDom reads one identifier; the server fails with error 102.
        Ok("SELECT " + U(0xFF07) + "a" + U(0xFF07)),
        // K23: ScriptDom reads one identifier; the server fails with error 207 (invalid column name).
        Ok("SELECT " + U(0x02BC) + "a" + U(0x02BC)),
        // K24: ScriptDom reads one identifier; the server fails with error 102.
        Ok("SELECT " + U(0x2019) + "a" + U(0x2019)),
        // K28: one statement on both sides; the server returns one result set whose value has an ASCII apostrophe.
        Ok("SELECT 'a" + U(0xFF07) + " SELECT 2 --'"),
        // K29: ScriptDom reads one identifier; the server fails with error 102.
        Ok("SELECT " + U(0xFF08) + "1" + U(0xFF09)),
        // U+0000 between two statements: refused by the character check outside literals.
        No("SELECT 1" + U(0) + "SELECT 2", GateReason.DisallowedSyntax, "WhiteSpace token with U+0000"),
        // C01f1: ScriptDom accepts the alias; the server fails with error 1055 (NULL character in a name). Allowed by
        // the gate until R5-4; now refused as a control character inside a bracketed identifier.
        No("SELECT 1 AS [a" + U(0) + "b]", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+0000"),

        // ---- Unicode noncharacters are refused anywhere, before parsing (R5-1). ScriptDom before 180.140.0 stopped
        // reading at U+FFFF; SQL Server does not accept U+FFFF even in a delimited identifier. ----
        No("SELECT " + U(0xFFFF) + " 1", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT N'a" + U(0xFFFF) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 'a" + U(0xFFFF) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 1 AS [a" + U(0xFFFF) + "b]", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 1 AS \"a" + U(0xFFFF) + "b\"", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 1 /* a" + U(0xFFFF) + "b */", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 1 -- a" + U(0xFFFF) + "b", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT " + U(0xFFFE) + " 1", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT N'a" + U(0xFFFE) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT 'a" + U(0xFFFE) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT 1 AS [a" + U(0xFFFE) + "b]", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT 1 AS \"a" + U(0xFFFE) + "b\"", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT 1 /* a" + U(0xFFFE) + "b */", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT 1 -- a" + U(0xFFFE) + "b", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFE)"),
        No("SELECT " + U(0xFDD0) + " 1", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT N'a" + U(0xFDD0) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT 'a" + U(0xFDD0) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT 1 AS [a" + U(0xFDD0) + "b]", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT 1 AS \"a" + U(0xFDD0) + "b\"", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT 1 /* a" + U(0xFDD0) + "b */", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        No("SELECT 1 -- a" + U(0xFDD0) + "b", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDD0)"),
        // Stacked forms: an older ScriptDom would have stopped at U+FFFF and never seen the second statement.
        No("SELECT 1 /* a" + U(0xFFFF) + "b */ SELECT 2", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 'a" + U(0xFFFF) + "b' SELECT 2", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        No("SELECT 1 -- a" + U(0xFFFF) + "b\nSELECT 2", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FFFF)"),
        // The end of the U+FDD0 range and supplementary-plane noncharacters, inside literals and comments too.
        No("SELECT 1 AS [a" + U(0xFDEF) + "b]", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+FDEF)"),
        No("SELECT N'a" + U(0x1FFFE) + "b' AS x", GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+1FFFE)"),
        No("SELECT 1 -- a" + U(0x10FFFF), GateReason.DisallowedSyntax, "text contains a Unicode noncharacter (U+10FFFF)"),
        // Neighbours of the noncharacters stay allowed inside a literal and a bracketed identifier.
        Ok("SELECT N'" + U(0xFDCF) + U(0xFDF0) + U(0xFFFD) + U(0x1FFFD) + "' AS [x" + U(0xFFFC) + "]"),

        // ---- ODBC escape tokens are refused at token level, whatever the parser makes of them (R5-2). ScriptDom reads
        // "--(*" as an OdbcInitiator token, not a comment; the server's reading is not known. ----
        No("SELECT 1 --(* x", GateReason.DisallowedSyntax, "OdbcInitiator token"),
        No("SELECT 1 --(* x\nSELECT 2", GateReason.DisallowedSyntax, "OdbcInitiator token"),
        No("SELECT 1 --(* x\nSELECT 2\n*)--", GateReason.DisallowedSyntax, "OdbcInitiator token"),
        No("SELECT 1 --(*vendor(Microsoft),product(ODBC) fn now() *)--", GateReason.DisallowedSyntax, "OdbcInitiator token"),
        No("--(* x\nSELECT 2 *)--", GateReason.DisallowedSyntax, "OdbcInitiator token"),
        No("SELECT {d '2020-01-01'}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("SELECT {ts '2020-01-01 00:00:00'}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("{call sp_who}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("{? = call sp_who}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("SELECT 1 FROM {oj sys.objects a LEFT OUTER JOIN sys.objects b ON a.object_id = b.object_id}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("SELECT {guid '00000000-0000-0000-0000-000000000000'}", GateReason.DisallowedSyntax, "LeftCurly token"),
        No("SELECT 1 }", GateReason.DisallowedSyntax, "RightCurly token"),
        // Controls: the same characters inside a comment, literal or bracketed identifier stay allowed.
        Ok("SELECT 1 -- (* x"),
        Ok("SELECT '{fn NOW()}' AS a"),
        Ok("SELECT 1 AS [{a}]"),
        Ok("SELECT 1 /* {x} --(* */"),

        // ---- Invisible, separator and control characters inside bracketed and double-quoted identifiers (R5-4).
        // Measured on SQL Server 2019 and 2022 (probe/summary2.md): [a<U+200D>b] and [a<U+FEFF>b] resolve to the
        // column ab; U+00AD and U+200B do not. Refused all the same: the plain name reaches the same object. ----
        No("SELECT [a" + U(0x200D) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+200D"),
        No("SELECT [a" + U(0xFEFF) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+FEFF"),
        No("SELECT [a" + U(0x00AD) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+00AD"),
        No("SELECT [a" + U(0x200B) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+200B"),
        No("SELECT [a" + U(0x00A0) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+00A0"),
        No("SELECT [a" + U(0x0085) + "b] FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+0085"),
        No("SELECT \"a" + U(0x200D) + "b\" FROM (SELECT 1 AS ab) t", GateReason.DisallowedSyntax, "AsciiStringOrQuotedIdentifier token with U+200D"),
        // The default database (Sales) plus U+200D: the character rule fires first, before the cross-database rule.
        No("SELECT * FROM [Sales" + U(0x200D) + "].dbo.A", GateReason.DisallowedSyntax, "QuotedIdentifier token with U+200D"),
        // Controls: a literal, an ordinary space and Turkish letters stay allowed.
        Ok("SELECT N'a" + U(0x200D) + "b' AS x"),
        Ok("SELECT 1 AS [a b]"),
        Ok("SELECT [ab] FROM (SELECT 1 AS ab) t"),
        Ok("SELECT 1 AS [" + U(0x0131) + U(0x011F) + U(0x00FC) + U(0x015F) + U(0x00F6) + U(0x00E7) + " " + U(0x0130) + U(0x011E) + U(0x00DC) + U(0x015E) + U(0x00D6) + U(0x00C7) + "]"),

        // ---- Refused: Unicode look-alikes, full-width characters, limits. ----
        No("\uFF33ELECT 1", GateReason.NotSelect), // Full-width S: one identifier, which is a procedure call.
        No("\uFF33\uFF25\uFF2C\uFF25\uFF23\uFF34 1", GateReason.NotSelect), // Probe K19: ExecuteStatement; server error 2812.
        No("SELECT 1\uFF1BDROP TABLE x", GateReason.ParseError), // Full-width semicolon.
        No("SELECT 1" + U(0x00A0) + "DROP TABLE x", GateReason.DisallowedSyntax, "WhiteSpace token with U+00A0"),
        No("SELECT 1" + U(0x3000) + "DROP TABLE x", GateReason.DisallowedSyntax, "WhiteSpace token with U+3000"),
        No("SELECT 1" + U(0x2003) + "DROP TABLE x", GateReason.DisallowedSyntax, "WhiteSpace token with U+2003"),
        No("SELECT " + new string('(', 65) + "1" + new string(')', 65), GateReason.TooDeep),
        No("SELECT 1 WHERE " + new string('(', 65) + "1 = 1" + new string(')', 65), GateReason.TooDeep),
        No("SELECT 1" + new string(' ', 65536), GateReason.TooLong),
        No("SELECT '" + new string('x', 70000) + "'", GateReason.TooLong),
    ];

    public static TheoryData<int> RowIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RowIndexes))]
    public void Row_matches_expected_decision(int index)
    {
        var row = Rows[index];

        var decision = ReadOnlyGate.Evaluate(row.Sql, Options);

        Assert.True(
            row.Allowed == decision.Allowed && row.Reason == decision.Reason,
            $"Row {index}: expected {row.Reason}, got {decision.Reason} ({decision.Detail})");
        if (row.Reason is GateReason.DisallowedSyntax or GateReason.DisallowedTableHint or GateReason.DisallowedQueryHint)
        {
            // The reason alone can pass for the wrong node; the detail names the node or token that refused.
            Assert.False(row.DetailPrefix is null, $"Row {index}: a {row.Reason} row needs an expected detail prefix");
            var expected = $"{row.Reason}: {row.DetailPrefix}";
            Assert.True(
                decision.Detail.StartsWith(expected, StringComparison.Ordinal),
                $"Row {index}: expected detail starting '{expected}', got '{decision.Detail}'");
        }
    }

    [Fact]
    public void Table_has_enough_rows_of_each_kind()
    {
        Assert.True(Rows.Count(r => r.Allowed) >= 30, "at least 30 allowed rows");
        Assert.True(Rows.Count(r => !r.Allowed) >= 60, "at least 60 refused rows");
        Assert.All(Rows, r => Assert.Equal(r.Allowed, r.Reason == GateReason.Allowed));
    }

    [Fact]
    public void Detail_never_echoes_sql_text()
    {
        foreach (var row in Rows)
        {
            var decision = ReadOnlyGate.Evaluate(row.Sql, Options);
            Assert.DoesNotContain("Orders", decision.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP", decision.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE", decision.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("'", decision.Detail, StringComparison.Ordinal);
            Assert.False(decision.Detail.Contains(row.Sql.Trim(), StringComparison.Ordinal) && row.Sql.Trim().Length > 2);
        }
    }

    [Fact]
    public void Null_text_is_empty()
    {
        var decision = ReadOnlyGate.Evaluate(null);

        Assert.False(decision.Allowed);
        Assert.Equal(GateReason.Empty, decision.Reason);
        Assert.Empty(decision.Tables);
    }

    [Fact]
    public void Three_part_names_are_refused_when_no_default_database_is_set()
    {
        Assert.Equal(GateReason.CrossDatabaseName, ReadOnlyGate.Evaluate("SELECT Id FROM Sales.dbo.Orders").Reason);
        Assert.Equal(GateReason.Allowed, ReadOnlyGate.Evaluate("SELECT Id FROM dbo.Orders").Reason);
    }

    [Fact]
    public void MaxSqlChars_is_honoured()
    {
        var options = new GateOptions(MaxSqlChars: 10);

        Assert.Equal(GateReason.Allowed, ReadOnlyGate.Evaluate("SELECT 1", options).Reason);
        Assert.Equal(GateReason.TooLong, ReadOnlyGate.Evaluate("SELECT 12345", options).Reason);
    }
}

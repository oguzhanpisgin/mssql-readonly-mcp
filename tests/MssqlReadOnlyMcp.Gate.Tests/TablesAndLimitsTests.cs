using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp.Gate.Tests;

public sealed class TablesTests
{
    private static readonly GateOptions Options = new(DefaultDatabase: "Sales");

    public static TheoryData<string, string[]> Cases => new()
    {
        { "SELECT 1", [] },
        { "SELECT * FROM Orders", ["Orders"] },
        { "SELECT * FROM [dbo].[Order Lines]", ["dbo.Order Lines"] },
        { "SELECT * FROM Sales.dbo.Orders", ["Sales.dbo.Orders"] },
        {
            "SELECT o.Id FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId LEFT JOIN dbo.Orders o2 ON o2.Id = o.Id",
            ["dbo.Orders", "dbo.Customers"]
        },
        {
            "SELECT Id FROM dbo.Orders WHERE CustomerId IN (SELECT Id FROM dbo.Customers WHERE EXISTS (SELECT 1 FROM dbo.Regions))",
            ["dbo.Orders", "dbo.Customers", "dbo.Regions"]
        },
        {
            "SELECT (SELECT MAX(Id) FROM dbo.Lines) AS m FROM dbo.Orders",
            ["dbo.Lines", "dbo.Orders"]
        },
        {
            "WITH recent AS (SELECT Id FROM dbo.Orders), big AS (SELECT Id FROM recent) SELECT * FROM big JOIN dbo.Lines l ON l.OrderId = big.Id",
            ["dbo.Orders", "dbo.Lines"]
        },
        {
            "WITH r AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r",
            []
        },
        {
            "SELECT Id FROM dbo.A UNION SELECT Id FROM dbo.B EXCEPT SELECT Id FROM dbo.A INTERSECT SELECT Id FROM DBO.b",
            ["dbo.A", "dbo.B"]
        },
        {
            "SELECT a.Id FROM dbo.A a CROSS APPLY (SELECT TOP 1 b.Id FROM dbo.B b WHERE b.AId = a.Id) x",
            ["dbo.A", "dbo.B"]
        },

        // CTE scoping (G-2): inside CTE i only CTEs 0..i are visible; the statement body sees all of them.
        {
            "WITH a AS (SELECT * FROM Payroll), Payroll AS (SELECT 1 AS x) SELECT * FROM a",
            ["Payroll"]
        },
        {
            "WITH Payroll AS (SELECT 1 AS x), a AS (SELECT * FROM Payroll) SELECT * FROM a",
            []
        },
        {
            "WITH a AS (SELECT * FROM b), b AS (SELECT * FROM a) SELECT * FROM b",
            ["b"]
        },
        {
            "WITH a AS (SELECT 1 AS x), b AS (SELECT 2 AS y) SELECT * FROM a JOIN b ON 1 = 1 JOIN dbo.T t ON 1 = 1 WHERE EXISTS (SELECT 1 FROM b)",
            ["dbo.T"]
        },
        {
            "WITH payroll AS (SELECT 1 AS x) SELECT * FROM Payroll",
            ["Payroll"]
        },
    };

    [Fact]
    public void Nested_WITH_inside_a_subquery_does_not_parse()
    {
        // T-SQL has no WITH inside a subquery; ScriptDom refuses it, so no nested CTE scope can reach the gate.
        var decision = ReadOnlyGate.Evaluate("SELECT * FROM (WITH c AS (SELECT 1 AS a) SELECT * FROM c) x", Options);

        Assert.Equal(GateReason.ParseError, decision.Reason);
    }

    [Fact]
    public void A_CTE_name_is_scoped_to_its_own_statement()
    {
        var decision = ReadOnlyGate.Evaluate("WITH c AS (SELECT 1 AS x) SELECT * FROM c; SELECT * FROM c", Options);

        Assert.Equal(GateReason.NotSingleStatement, decision.Reason);
        Assert.Equal(["c"], decision.Tables);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tables_lists_referenced_tables_in_first_seen_order(string sql, string[] expected)
    {
        var decision = ReadOnlyGate.Evaluate(sql, Options);

        Assert.True(decision.Allowed, decision.Detail);
        Assert.Equal(expected, decision.Tables);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Orders WITH (UPDLOCK)", GateReason.DisallowedTableHint, new[] { "dbo.Orders" })]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Orders", GateReason.SelectInto, new[] { "dbo.Orders" })]
    [InlineData("SELECT 1; DELETE FROM dbo.Orders", GateReason.NotSingleStatement, new[] { "dbo.Orders" })]
    [InlineData("WITH c AS (SELECT Id FROM dbo.Orders) UPDATE c SET Id = 1", GateReason.NotSelect, new[] { "dbo.Orders" })]
    [InlineData("SELECT * FROM dbo.Orders o JOIN Other.dbo.Lines l ON l.Id = o.Id", GateReason.CrossDatabaseName, new[] { "dbo.Orders", "Other.dbo.Lines" })]
    [InlineData("SELECT * FROM dbo.Orders\nGO\nSELECT * FROM dbo.Lines", GateReason.NotSingleBatch, new[] { "dbo.Orders", "dbo.Lines" })]
    [InlineData("SELECT * FROM dbo.Orders WHERE", GateReason.ParseError, new string[0])]
    public void Tables_is_filled_for_refused_text_that_parsed(string sql, GateReason reason, string[] expected)
    {
        var decision = ReadOnlyGate.Evaluate(sql, Options);

        Assert.False(decision.Allowed);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(expected, decision.Tables);
    }

    [Fact]
    public void Tables_is_empty_for_text_that_is_not_well_formed_UTF16()
    {
        // The refusal happens before parsing (W-1), so the table name is not reported even though it is valid SQL.
        var decision = ReadOnlyGate.Evaluate("SELECT * FROM dbo.Orders WHERE Name = N'" + (char)0xD800 + "'", Options);

        Assert.Equal(GateReason.DisallowedSyntax, decision.Reason);
        Assert.StartsWith("DisallowedSyntax: text is not well-formed UTF-16", decision.Detail);
        Assert.Empty(decision.Tables);
    }
}

public sealed class LimitsTests
{
    [Theory]
    [InlineData("tilde")]
    [InlineData("minus")]
    [InlineData("not")]
    [InlineData("case")]
    [InlineData("plus")]
    [InlineData("and")]
    [InlineData("union")]
    public void Deep_nesting_without_parentheses_does_not_crash(string shape)
    {
        // Nesting that is not bounded by the parenthesis limit, sized to the default 64 KiB maximum. Without the
        // gate's large evaluation stack each of these overflows the stack and kills the process.
        var sql = Deep(shape, 65536);
        Assert.InRange(sql.Length, 60000, 65536);

        var decision = ReadOnlyGate.Evaluate(sql);

        Assert.True(decision.Reason is GateReason.Allowed or GateReason.TooDeep, $"{decision.Reason}: {decision.Detail}");
    }

    [Fact]
    public void Deep_nesting_at_a_larger_configured_maximum_does_not_crash()
    {
        var sql = Deep("tilde", ReadOnlyGate.HardMaxSqlChars);

        var decision = ReadOnlyGate.Evaluate(sql, new GateOptions(MaxSqlChars: ReadOnlyGate.HardMaxSqlChars));

        Assert.True(decision.Reason is GateReason.Allowed or GateReason.TooDeep, $"{decision.Reason}: {decision.Detail}");
    }

    [Fact]
    public void MaxSqlChars_above_the_hard_ceiling_is_capped()
    {
        var sql = "SELECT 1" + new string(' ', ReadOnlyGate.HardMaxSqlChars);

        var decision = ReadOnlyGate.Evaluate(sql, new GateOptions(MaxSqlChars: int.MaxValue));

        Assert.Equal(GateReason.TooLong, decision.Reason);
    }

    [Fact]
    public void Evaluate_never_throws_for_arbitrary_text()
    {
        var random = new Random(20261111);
        const string alphabet = "SELECT FROM WHERE ()[]'\";-/*\n\r\t@#$.,0123456789abcxyz\u00A0\u2028\uFF33\0";
        for (var i = 0; i < 400; i++)
        {
            var chars = new char[random.Next(0, 200)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = random.Next(10) == 0 ? (char)random.Next(0, 0xFFFF) : alphabet[random.Next(alphabet.Length)];
            }

            var decision = ReadOnlyGate.Evaluate(new string(chars), new GateOptions("Sales"));

            Assert.NotNull(decision.Detail);
            Assert.NotNull(decision.Tables);
            Assert.Equal(decision.Allowed, decision.Reason == GateReason.Allowed);
        }
    }

    private static string Deep(string shape, int maxChars)
    {
        string Repeat(string part, int overhead) => string.Concat(Enumerable.Repeat(part, (maxChars - overhead) / part.Length));

        return shape switch
        {
            "tilde" => "SELECT " + Repeat("~", 10) + "1",
            "minus" => "SELECT " + Repeat("- ", 10) + "1",
            "not" => "SELECT 1 WHERE " + Repeat("NOT ", 30) + "1 = 1",
            "case" => CaseNest(maxChars),
            "plus" => "SELECT 1" + Repeat("+1", 10),
            "and" => "SELECT 1 WHERE 1 = 1" + Repeat(" AND 1 = 1", 30),
            "union" => "SELECT 1" + Repeat(" UNION SELECT 1", 10),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static string CaseNest(int maxChars)
    {
        const string open = "CASE WHEN 1 = 1 THEN ";
        const string close = " END";
        var n = (maxChars - 20) / (open.Length + close.Length);
        return "SELECT " + string.Concat(Enumerable.Repeat(open, n)) + "1" + string.Concat(Enumerable.Repeat(close, n));
    }
}

using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlReadOnlyMcp.Gate;

namespace MssqlReadOnlyMcp.Gate.Tests;

/// <summary>
/// Generated checks over the ScriptDom assembly, so a statement or node type added by a later ScriptDom version
/// cannot be let through silently.
/// </summary>
public sealed class StatementTypeTests
{
    private static readonly Assembly ScriptDom = typeof(TSqlStatement).Assembly;

    [Fact]
    public void ScriptDom_is_at_least_the_version_with_the_U_FFFF_end_of_input_fix()
    {
        // Before NuGet 180.140.0 the ScriptDom lexer used U+FFFF as its end-of-input marker and stopped reading at a
        // literal U+FFFF inside string literals, delimited identifiers and comments, so the parser could see less text
        // than the server. The gate also refuses U+FFFF before parsing; this floor keeps a later downgrade from
        // passing silently. NuGet 180.140.0 ships file version 18.0.140.0 (major 180 is written as 18.0).
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(ScriptDom.Location).FileVersion;

        Assert.False(string.IsNullOrEmpty(fileVersion), "ScriptDom file version is not readable");
        Assert.True(
            Version.Parse(fileVersion!) >= new Version(18, 0, 140, 0),
            $"ScriptDom file version {fileVersion} is below 18.0.140.0 (NuGet 180.140.0)");
    }

    [Fact]
    public void Statement_check_accepts_exactly_one_statement_type()
    {
        var statementTypes = ScriptDom.GetTypes()
            .Where(t => typeof(TSqlStatement).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        var accepted = statementTypes.Where(Allowlist.IsAllowedStatementType).ToList();

        Assert.True(statementTypes.Count > 300, $"expected the full statement list, found {statementTypes.Count}");
        Assert.Equal([typeof(SelectStatement)], accepted);
    }

    [Fact]
    public void SelectStatement_subclasses_are_named_and_refused()
    {
        var subclasses = ScriptDom.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(SelectStatement)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // SelectStatementSnippet is a script-generation placeholder; it is not listed, so it is refused.
        Assert.Equal(["SelectStatementSnippet"], subclasses);
        Assert.False(Allowlist.IsAllowedStatementType(typeof(SelectStatementSnippet)));
        Assert.DoesNotContain(typeof(SelectStatementSnippet), Allowlist.NodeTypes);
    }

    [Fact]
    public void Allowlist_holds_only_concrete_fragment_types_and_one_statement()
    {
        Assert.All(Allowlist.NodeTypes, t =>
        {
            Assert.True(typeof(TSqlFragment).IsAssignableFrom(t), t.Name);
            Assert.False(t.IsAbstract, t.Name);
        });
        Assert.Equal(
            [typeof(SelectStatement)],
            Allowlist.NodeTypes.Where(t => typeof(TSqlStatement).IsAssignableFrom(t)).ToList());
    }

    [Fact]
    public void Visitor_sees_every_node_a_reflection_walk_finds_in_allowed_rows()
    {
        // The gate trusts ScriptDom's visitor to reach every child. This walks the tree a second way (public
        // properties by reflection) and checks that every node it finds in an allowed row is allowlisted.
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        foreach (var row in RegressionTableTests.Rows.Where(r => r.Allowed))
        {
            var fragment = parser.Parse(new StringReader(row.Sql), out var errors);
            Assert.Empty(errors);
            var statement = ((TSqlScript)fragment).Batches[0].Statements[0];
            foreach (var node in ReflectionWalk(statement))
            {
                Assert.True(Allowlist.NodeTypes.Contains(node.GetType()), $"{node.GetType().Name} in allowed row");
            }
        }
    }

    private static IEnumerable<TSqlFragment> ReflectionWalk(TSqlFragment root)
    {
        var stack = new Stack<TSqlFragment>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.Name == nameof(TSqlFragment.ScriptTokenStream))
                {
                    continue;
                }

                var value = property.GetValue(node);
                if (value is TSqlFragment child)
                {
                    stack.Push(child);
                }
                else if (value is System.Collections.IEnumerable items and not string)
                {
                    foreach (var item in items)
                    {
                        if (item is TSqlFragment listed)
                        {
                            stack.Push(listed);
                        }
                    }
                }
            }
        }
    }
}

using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlReadOnlyMcp.Gate;

/// <summary>
/// Walks every fragment under the node it is given. It records the first refusal (if any) and keeps walking to
/// collect the referenced table names. In collect-only mode it records no refusal.
/// </summary>
internal sealed class GateVisitor : TSqlFragmentVisitor
{
    private readonly string? defaultDatabase;
    private readonly CteScopes cteScopes;
    private readonly bool collectOnly;
    private readonly List<(string Name, int Offset)> tables = [];

    internal GateVisitor(string? defaultDatabase, CteScopes cteScopes, bool collectOnly)
    {
        this.defaultDatabase = defaultDatabase;
        this.cteScopes = cteScopes;
        this.collectOnly = collectOnly;
    }

    /// <summary>The first refusal reason, or <see cref="GateReason.Allowed"/>.</summary>
    internal GateReason Reason { get; private set; } = GateReason.Allowed;

    /// <summary>The detail of the first refusal, or null.</summary>
    internal string? Detail { get; private set; }

    /// <summary>Distinct table names (ignoring case) in order of first appearance in the text.</summary>
    internal IReadOnlyList<string> Tables()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var (name, _) in tables.OrderBy(t => t.Offset))
        {
            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override void Visit(TSqlFragment fragment)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new InsufficientExecutionStackException();
        }

        if (fragment is NamedTableReference named && named.SchemaObject is not null)
        {
            CollectTable(named);
        }

        if (collectOnly || Reason != GateReason.Allowed)
        {
            return;
        }

        var reason = Check(fragment, out var what);
        if (reason != GateReason.Allowed)
        {
            Reason = reason;
            Detail = $"{reason}: {what} at line {fragment.StartLine}, column {fragment.StartColumn}";
        }
    }

    private void CollectTable(NamedTableReference named)
    {
        var parts = named.SchemaObject.Identifiers;
        if (parts.Count == 1 && cteScopes.VisibleAt(named).Contains(parts[0].Value ?? string.Empty))
        {
            return;
        }

        var name = string.Join('.', parts.Select(p => p.Value ?? string.Empty));
        tables.Add((name, named.StartOffset));
    }

    private GateReason Check(TSqlFragment fragment, out string what)
    {
        var type = fragment.GetType();
        what = type.Name;

        if (!Allowlist.NodeTypes.Contains(type))
        {
            return fragment switch
            {
                SelectSetVariable => GateReason.VariableAssignment,
                TableHint => GateReason.DisallowedTableHint,
                OptimizerHint => GateReason.DisallowedQueryHint,
                _ => GateReason.DisallowedSyntax,
            };
        }

        switch (fragment)
        {
            case SelectStatement select:
                if (select.Into is not null)
                {
                    return GateReason.SelectInto;
                }

                if (select.OptimizerHints.Count > 0)
                {
                    what = nameof(OptimizerHint);
                    return GateReason.DisallowedQueryHint;
                }

                if (select.ComputeClauses.Count > 0)
                {
                    what = nameof(ComputeClause);
                    return GateReason.DisallowedSyntax;
                }

                return GateReason.Allowed;

            case TableHint hint:
                return hint.HintKind is TableHintKind.NoLock or TableHintKind.ReadUncommitted
                    ? GateReason.Allowed
                    : GateReason.DisallowedTableHint;

            case QualifiedJoin join:
                return join.JoinHint == JoinHint.None ? GateReason.Allowed : GateReason.DisallowedQueryHint;

            case NamedTableReference table:
                var baseName = table.SchemaObject?.BaseIdentifier?.Value ?? string.Empty;
                if (baseName.StartsWith('#'))
                {
                    what = "NamedTableReference (temporary table)";
                    return GateReason.DisallowedSyntax;
                }

                return GateReason.Allowed;

            case PivotedTableReference pivot:
                if (pivot.AggregateFunctionIdentifier is not { Identifiers: [var aggregate] }
                    || !Allowlist.FunctionNames.Contains(aggregate.Value ?? string.Empty))
                {
                    // The name is user-written text, so the detail never carries it.
                    what = "PivotedTableReference (aggregate function not on the allowlist)";
                    return GateReason.DisallowedSyntax;
                }

                return GateReason.Allowed;

            case ParameterlessCall parameterless:
                // Only CURRENT_TIMESTAMP: the identity calls (CURRENT_USER, SESSION_USER, SYSTEM_USER, USER) are
                // refused because identity functions are off the function-name list too.
                if (parameterless.ParameterlessCallType != ParameterlessCallType.CurrentTimestamp)
                {
                    what = "ParameterlessCall (call type not on the allowlist)";
                    return GateReason.DisallowedSyntax;
                }

                return GateReason.Allowed;

            case FunctionCall call:
                if (!Allowlist.FunctionNames.Contains(call.FunctionName?.Value ?? string.Empty))
                {
                    // The name is user-written text, so the detail never carries it.
                    what = "FunctionCall (function not on the allowlist)";
                    return GateReason.DisallowedSyntax;
                }

                return GateReason.Allowed;

            case SchemaObjectName name:
                var count = name.Identifiers.Count;
                return CheckNameParts(count, count >= 3 ? name.Identifiers[count - 3].Value : null);

            case ColumnReferenceExpression column when column.MultiPartIdentifier is { } parts:
                // A column name has one part more than its table name: server.database.schema.table.column.
                return CheckNameParts(
                    parts.Identifiers.Count - 1,
                    parts.Identifiers.Count >= 4 ? parts.Identifiers[parts.Identifiers.Count - 4].Value : null);

            default:
                return GateReason.Allowed;
        }
    }

    private GateReason CheckNameParts(int objectNameParts, string? database)
    {
        if (objectNameParts >= 4)
        {
            return GateReason.LinkedServerName;
        }

        if (objectNameParts == 3)
        {
            // Exact ordinal equality: on a case-sensitive instance SALES and Sales can be two databases, and
            // refusing a differently-cased spelling is the safe direction.
            var sameDatabase = defaultDatabase is not null
                && string.Equals(database, defaultDatabase, StringComparison.Ordinal);
            return sameDatabase ? GateReason.Allowed : GateReason.CrossDatabaseName;
        }

        return GateReason.Allowed;
    }
}

/// <summary>
/// Maps each table reference to the common table expression names visible at that point. Inside CTE number i of a
/// WITH list only CTEs 0..i are visible (i itself for recursion); the statement body sees every CTE of its own
/// WITH. A nested statement (only reachable when a script holds several statements) uses its own WITH only. Names
/// compare by exact ordinal equality, so on doubt a reference is listed as a table rather than hidden.
/// </summary>
internal sealed class CteScopes : TSqlFragmentVisitor
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<NamedTableReference, IReadOnlySet<string>> visible =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The CTE names visible at a table reference; empty when no WITH applies.</summary>
    internal IReadOnlySet<string> VisibleAt(NamedTableReference reference) =>
        visible.TryGetValue(reference, out var names) ? names : None;

    /// <summary>Builds the map for every statement under <paramref name="root"/>.</summary>
    internal static CteScopes Build(TSqlFragment root)
    {
        var scopes = new CteScopes();
        root.Accept(scopes);
        return scopes;
    }

    /// <inheritdoc />
    public override void Visit(TSqlFragment fragment)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new InsufficientExecutionStackException();
        }

        // The visitor reaches a parent before its children, so an inner statement overwrites what its outer
        // statement assigned, and each CTE body overwrites what its statement assigned.
        if (fragment is not StatementWithCtesAndXmlNamespaces { WithCtesAndXmlNamespaces: { } with } statement)
        {
            return;
        }

        var ctes = with.CommonTableExpressions;
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cte in ctes)
        {
            all.Add(cte.ExpressionName?.Value ?? string.Empty);
        }

        Assign(statement, all);
        var soFar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cte in ctes)
        {
            soFar.Add(cte.ExpressionName?.Value ?? string.Empty);
            Assign(cte, new HashSet<string>(soFar, StringComparer.Ordinal));
        }
    }

    private void Assign(TSqlFragment root, IReadOnlySet<string> names)
    {
        var collector = new TableReferenceCollector();
        root.Accept(collector);
        foreach (var reference in collector.References)
        {
            visible[reference] = names;
        }
    }

    private sealed class TableReferenceCollector : TSqlFragmentVisitor
    {
        internal List<NamedTableReference> References { get; } = [];

        public override void Visit(TSqlFragment fragment)
        {
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                throw new InsufficientExecutionStackException();
            }

            if (fragment is NamedTableReference reference)
            {
                References.Add(reference);
            }
        }
    }
}

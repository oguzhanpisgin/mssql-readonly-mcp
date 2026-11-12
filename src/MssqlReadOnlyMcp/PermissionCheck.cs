using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>One way the login can change data, schema or security, or a right the check could not evaluate.</summary>
/// <param name="Permission">The permission or role name.</param>
/// <param name="Scope">
/// <c>server</c>, <c>server role</c>, <c>login</c>, <c>database</c>, <c>database role</c>, <c>user</c>, <c>role</c>,
/// <c>schema</c>, <c>object</c> or <c>column</c>.
/// </param>
/// <param name="Objects">At most ten names the permission is held on; empty for scopes without names.</param>
/// <param name="ObjectCount">How many names hold the permission in total.</param>
/// <param name="Unchecked">
/// SQL Server answered NULL instead of yes or no for this right (for example a name it could not parse). The check
/// treats that as a finding: "could not be checked" is never a pass.
/// </param>
internal sealed record PermissionFinding(
    string Permission, string Scope, IReadOnlyList<string> Objects, int ObjectCount, bool Unchecked = false)
{
    public const int MaxObjectNames = 10;

    /// <summary>One line naming the permission, the scope and at most ten names.</summary>
    public override string ToString()
    {
        var note = Unchecked ? " (could not be checked)" : string.Empty;
        if (Objects.Count == 0)
        {
            return Scope switch
            {
                "server role" or "database role" => $"member of {Scope} {Permission}{note}",
                _ => $"{Permission} on {Scope}{note}",
            };
        }

        var more = ObjectCount > Objects.Count
            ? string.Create(CultureInfo.InvariantCulture, $" and {ObjectCount - Objects.Count} more")
            : string.Empty;
        return $"{Permission} on {Scope} {string.Join(", ", Objects)}{more}{note}";
    }
}

/// <summary>Result of the startup permission check.</summary>
/// <param name="Database">The connection's database.</param>
/// <param name="Login">The login name as SQL Server reports it.</param>
/// <param name="Findings">Every way the login can write; empty when the check passes.</param>
internal sealed record PermissionReport(string Database, string Login, IReadOnlyList<PermissionFinding> Findings)
{
    public bool Passed => Findings.Count == 0;
}

/// <summary>
/// The startup check that refuses a login able to change data, schema or security in the connection's database, or
/// to act as another principal. There is no override.
/// </summary>
internal static class PermissionCheck
{
    // One fixed, set-based statement. Every candidate row carries the server's answer in "state": 1 (held), 0 (not
    // held) or NULL (SQL Server could not evaluate it). Rows with 1 or NULL are findings; NULL is never a pass.
    // Each answer is computed once, in the derived table, and filtered with ISNULL so it is not evaluated twice.
    // Server and database scope follow the rule used everywhere else: allow what is listed, refuse the rest. The
    // login's effective permissions there (fn_my_permissions, which includes what roles and higher scopes imply) must
    // be inside the allowed sets below; any other permission is a finding by its own name, so a permission SQL Server
    // adds in a later version is refused without a code change.
    // Allowed sets, measured on 2019 (15.0.4510), 2022 (16.0.4295) and 2025 (17.0.5015) for a login made by
    // scripts/create-readonly-login.sql (identical on all three):
    //   server:   CONNECT SQL (log in at all), VIEW ANY DATABASE (see database names; public holds it by default).
    //   database: CONNECT (enter the database), VIEW ANY COLUMN ENCRYPTION KEY DEFINITION and VIEW ANY COLUMN MASTER
    //             KEY DEFINITION (Always Encrypted key metadata, never key material; public holds both by default).
    // Below those, objects, columns, roles, schemas, sequences, modules and synonyms are asked one by one. Names are passed
    // through QUOTENAME so a name that needs quoting (a dot, a space, a bracket) is not misread as another name,
    // which would answer NULL.
    // Metadata visibility helps here: a login sees a login, user, role or object it holds a permission on.
    // The first arm's COLLATE settles the arms' different catalogue collations (server, database, permission list).
    private const string CheckSql = """
        SELECT f.permission, f.scope, f.object_name, f.column_name, f.state
        FROM (
            SELECT CAST(r.name AS nvarchar(128)) COLLATE DATABASE_DEFAULT AS permission,
                   CAST(N'server role' AS nvarchar(20)) COLLATE DATABASE_DEFAULT AS scope,
                   CAST(NULL AS nvarchar(600)) COLLATE DATABASE_DEFAULT AS object_name,
                   CAST(NULL AS nvarchar(300)) COLLATE DATABASE_DEFAULT AS column_name,
                   IS_SRVROLEMEMBER(r.name) AS state
            FROM sys.server_principals AS r
            WHERE r.type = 'R' AND r.is_fixed_role = 1 AND r.name <> N'public'
            UNION ALL
            SELECT p.permission_name, N'server', NULL, NULL, 1
            FROM sys.fn_my_permissions(NULL, N'SERVER') AS p
            WHERE p.permission_name NOT IN (N'CONNECT SQL', N'VIEW ANY DATABASE')
            UNION ALL
            SELECT N'IMPERSONATE', N'login', QUOTENAME(l.name), NULL, HAS_PERMS_BY_NAME(QUOTENAME(l.name), N'LOGIN', N'IMPERSONATE')
            FROM sys.server_principals AS l
            WHERE l.type IN ('S', 'U', 'G', 'C', 'K', 'E', 'X') AND l.sid <> SUSER_SID()
            UNION ALL
            SELECT r.name, N'database role', NULL, NULL, IS_ROLEMEMBER(r.name)
            FROM (VALUES (N'db_owner'), (N'db_datawriter'), (N'db_ddladmin'), (N'db_securityadmin'),
                         (N'db_accessadmin'), (N'db_backupoperator')) AS r(name)
            UNION ALL
            SELECT p.permission_name, N'database', NULL, NULL, 1
            FROM sys.fn_my_permissions(NULL, N'DATABASE') AS p
            WHERE p.permission_name NOT IN (N'CONNECT', N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION',
                                            N'VIEW ANY COLUMN MASTER KEY DEFINITION')
            UNION ALL
            SELECT N'IMPERSONATE', N'user', QUOTENAME(d.name), NULL, HAS_PERMS_BY_NAME(QUOTENAME(d.name), N'USER', N'IMPERSONATE')
            FROM sys.database_principals AS d
            WHERE d.type IN ('S', 'U', 'G', 'C', 'K', 'E', 'X') AND d.principal_id <> DATABASE_PRINCIPAL_ID()
            UNION ALL
            SELECT p.name, N'role', QUOTENAME(d.name), NULL, HAS_PERMS_BY_NAME(QUOTENAME(d.name), N'ROLE', p.name)
            FROM sys.database_principals AS d
            CROSS JOIN (VALUES (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE d.type = 'R'
            UNION ALL
            SELECT p.permission_name, N'schema', QUOTENAME(s.name), NULL, HAS_PERMS_BY_NAME(QUOTENAME(s.name), N'SCHEMA', p.permission_name)
            FROM sys.schemas AS s
            CROSS JOIN sys.fn_builtin_permissions(N'SCHEMA') AS p
            WHERE p.permission_name IN (N'INSERT', N'UPDATE', N'DELETE', N'EXECUTE', N'ALTER', N'CONTROL', N'TAKE OWNERSHIP')
               OR p.permission_name LIKE N'CREATE %'
            UNION ALL
            -- Tables, views and inline table-valued functions (SQL Server lets INSERT, UPDATE and DELETE run through
            -- an inline function as through a view).
            SELECT p.name, N'object', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'INSERT'), (N'UPDATE'), (N'DELETE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type IN ('U', 'V', 'IF') AND o.is_ms_shipped = 0
            UNION ALL
            -- Sequences: UPDATE is what NEXT VALUE FOR needs.
            SELECT p.name, N'object', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'UPDATE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type = 'SO' AND o.is_ms_shipped = 0
            UNION ALL
            -- Procedures and functions (SQL, CLR and extended).
            SELECT p.name, N'object', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'EXECUTE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type IN ('P', 'PC', 'X', 'RF', 'FN', 'FS', 'TF', 'FT', 'AF') AND o.is_ms_shipped = 0
            UNION ALL
            -- Synonyms: with an unbroken ownership chain a right granted on the synonym alone reaches the object it
            -- names (measured: UPDATE on a synonym of a table changes the table). EXECUTE because a synonym can name
            -- a procedure.
            SELECT p.name, N'synonym', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'INSERT'), (N'UPDATE'), (N'DELETE'), (N'EXECUTE'), (N'ALTER'), (N'CONTROL'),
                               (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type = 'SN' AND o.is_ms_shipped = 0
            UNION ALL
            -- Column-level UPDATE (the only column-level right that changes data; SQL Server has no column-level
            -- INSERT or DELETE). A column of an object that already has object-level UPDATE is dropped by the caller.
            SELECT N'UPDATE', N'column', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), QUOTENAME(c.name),
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', N'UPDATE',
                                     QUOTENAME(c.name), N'COLUMN')
            FROM sys.objects AS o
            JOIN sys.columns AS c ON c.object_id = o.object_id
            WHERE o.type IN ('U', 'V', 'IF') AND o.is_ms_shipped = 0
        ) AS f
        WHERE ISNULL(f.state, 1) = 1
        ORDER BY f.scope, f.permission, f.object_name, f.column_name;
        SELECT DB_NAME() AS database_name, SUSER_SNAME() AS login_name;
        """;

    /// <summary>Connects and runs the check.</summary>
    /// <exception cref="SqlException">The server is unreachable or the check query failed.</exception>
    public static async Task<PermissionReport> RunAsync(SqlGateway gateway, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await gateway.OpenSessionAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
        await using var command = gateway.CreateCommand(connection, CheckSql, null, timeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<(string Permission, string Scope, string? Name, string? Column, bool Unchecked)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var database = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var login = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        // A column of an object whose UPDATE is already a finding adds nothing to name.
        var updatable = rows
            .Where(r => r is { Scope: "object", Permission: "UPDATE", Unchecked: false })
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);
        var findings = rows
            .Where(r => !(r.Scope == "column" && !r.Unchecked && updatable.Contains(r.Name)))
            .GroupBy(r => (r.Permission, r.Scope, r.Unchecked))
            .Select(g =>
            {
                var names = g
                    .Where(r => r.Name is not null)
                    .Select(r => r.Column is null ? r.Name! : $"{r.Name}.{r.Column}")
                    .ToList();
                return new PermissionFinding(
                    g.Key.Permission,
                    g.Key.Scope == "database" ? $"database {database}" : g.Key.Scope,
                    names.Take(PermissionFinding.MaxObjectNames).ToList(),
                    names.Count,
                    g.Key.Unchecked);
            })
            .ToList();
        return new PermissionReport(database, login, findings);
    }
}

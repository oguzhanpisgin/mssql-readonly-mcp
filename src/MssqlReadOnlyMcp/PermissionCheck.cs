using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>One way the login can change data, schema or security, or a right the check could not evaluate.</summary>
/// <param name="Permission">The permission or role name.</param>
/// <param name="Scope">
/// <c>server</c>, <c>server role</c>, <c>login</c>, <c>database</c>, <c>database role</c>, <c>user</c>, <c>role</c>,
/// <c>application role</c>, <c>schema</c>, <c>object</c>, <c>synonym</c>, <c>column</c>, <c>queue</c>,
/// <c>service</c>, <c>certificate</c>, <c>asymmetric key</c>, <c>symmetric key</c>, <c>assembly</c>, <c>type</c>,
/// <c>xml schema collection</c> or <c>full-text catalog</c>. <c>server role</c> without names is a membership; with
/// a name it is a right on that user-defined server role. A service the login holds only SEND on is not visible to it
/// and is named <c>id &lt;service_id&gt;</c>.
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
    // Below those, objects, columns, roles, application roles, schemas, sequences, modules and synonyms are asked one
    // by one, and so are user-defined server roles, Service Broker queues and services, certificates, asymmetric and
    // symmetric keys, assemblies, user types, XML schema collections and full-text catalogs (measured on the same
    // three versions). Names are passed through QUOTENAME so a name that needs quoting (a dot, a space, a bracket) is
    // not misread as another name, which would answer NULL.
    // The object arms read every object of their types in the connected database's sys.objects, the ones SQL Server
    // marks as shipped (is_ms_shipped = 1) included: sp_MS_marksystemobject marks any object so, and change data
    // capture adds shipped tables in schema cdc, and a right on either works (measured). Connected to msdb every
    // login with a user there is therefore refused: public may execute 14 shipped procedures and functions there on
    // 2019, 15 on 2022 and 2025. The system views, procedures and functions in schemas sys and INFORMATION_SCHEMA are
    // in sys.system_objects, not sys.objects, and are not asked; in sys.objects those schemas hold only IT, S, PK and
    // TT rows, none of the asked types (measured); the schemas themselves are asked. Connected to master, SQL Server
    // accepts an EXECUTE grant on a system extended procedure in schema sys (sys.xp_regwrite, type X); it is not in
    // sys.objects, so this check does not see it (measured).
    // Metadata visibility helps here: a login sees a login, user, role or object it holds a permission on.
    // The first arm's COLLATE settles the arms' different catalogue collations (server, database, permission list).
    // Filters kept below, and why. Measured: a user-defined server role's rights reach its members'
    // fn_my_permissions(SERVER), so membership is asked of fixed server roles only; GRANT ALTER, CONTROL or TAKE
    // OWNERSHIP on a fixed server role or public fails with error 15151, so rights are asked of user-defined server
    // roles only; every right on a built-in type fails with 15151 and on sys.sys with 4606, so types are asked when
    // user-defined and XML schema collections outside schema sys. Reasons, not measurements: public is left out of
    // membership (every login is in it, and its rights are in fn_my_permissions); the login's own login and user are
    // left out of IMPERSONATE (acting as itself gives it nothing it lacks); membership is asked of six fixed database
    // roles because the rights of any other role reach fn_my_permissions(DATABASE) or the one-by-one questions.
    private const string CheckSql = """
        SELECT f.permission, f.scope, f.object_name, f.column_name, f.state
        FROM (
            SELECT CAST(r.name AS nvarchar(128)) COLLATE DATABASE_DEFAULT AS permission,
                   CAST(N'server role' AS nvarchar(30)) COLLATE DATABASE_DEFAULT AS scope,
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
            -- Application roles: ALTER (implied by CONTROL) lets the login change the role's default schema
            -- (measured; a rename, a new password or DROP needs ALTER ANY APPLICATION ROLE). The class has no
            -- TAKE OWNERSHIP. The plain reader does not see application roles; a login holding ALTER does.
            SELECT p.name, N'application role', QUOTENAME(d.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(d.name), N'APPLICATION ROLE', p.name)
            FROM sys.database_principals AS d
            CROSS JOIN (VALUES (N'ALTER'), (N'CONTROL')) AS p(name)
            WHERE d.type = 'A'
            UNION ALL
            -- User-defined server roles: ALTER lets the login add itself as a member and so gain the role's rights
            -- (measured with ALTER ANY LOGIN).
            SELECT p.name, N'server role', QUOTENAME(r.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(r.name), N'SERVER ROLE', p.name)
            FROM sys.server_principals AS r
            CROSS JOIN (VALUES (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE r.type = 'R' AND r.is_fixed_role = 0 AND r.name <> N'public'
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
            WHERE o.type IN ('U', 'V', 'IF')
            UNION ALL
            -- Sequences: UPDATE is what NEXT VALUE FOR needs.
            SELECT p.name, N'object', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'UPDATE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type = 'SO'
            UNION ALL
            -- Procedures and functions (SQL, CLR and extended).
            SELECT p.name, N'object', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'EXECUTE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type IN ('P', 'PC', 'X', 'RF', 'FN', 'FS', 'TF', 'FT', 'AF')
            UNION ALL
            -- Synonyms: with an unbroken ownership chain a right granted on the synonym alone reaches the object it
            -- names (measured: UPDATE on a synonym of a table changes the table). EXECUTE because a synonym can name
            -- a procedure.
            SELECT p.name, N'synonym', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'INSERT'), (N'UPDATE'), (N'DELETE'), (N'EXECUTE'), (N'ALTER'), (N'CONTROL'),
                               (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type = 'SN'
            UNION ALL
            -- Column-level UPDATE (the only column-level right that changes data; SQL Server has no column-level
            -- INSERT or DELETE). A column of an object that already has object-level UPDATE is dropped by the caller.
            SELECT N'UPDATE', N'column', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), QUOTENAME(c.name),
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', N'UPDATE',
                                     QUOTENAME(c.name), N'COLUMN')
            FROM sys.objects AS o
            JOIN sys.columns AS c ON c.object_id = o.object_id
            WHERE o.type IN ('U', 'V', 'IF')
            UNION ALL
            -- Service Broker queues: RECEIVE takes messages off the queue, ALTER turns it off or changes it. The
            -- list is fixed because a queue is an OBJECT: INSERT, UPDATE, DELETE, EXECUTE, UNMASK and VIEW CHANGE
            -- TRACKING cannot be granted on one (4606) and a queue cannot be written directly (416, even for sa);
            -- SELECT only reads the queue. Built-in queues are asked too: a user database has three in schema dbo
            -- (is_ms_shipped = 1) that accept RECEIVE and CONTROL, and RECEIVE on one lets the login begin a dialog
            -- from that queue's built-in service, owned by dbo, and send to a user service in the database (measured).
            SELECT p.name, N'queue', QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), NULL,
                   HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', p.name)
            FROM sys.objects AS o
            CROSS JOIN (VALUES (N'RECEIVE'), (N'ALTER'), (N'CONTROL'), (N'TAKE OWNERSHIP')) AS p(name)
            WHERE o.type = 'SQ'
            UNION ALL
            -- Services, certificates, asymmetric and symmetric keys, assemblies, user types, XML schema collections
            -- and full-text catalogs: every right SQL Server lists for the class is a finding except
            -- those allowed here. Allowed (measured): VIEW DEFINITION and REFERENCES change nothing and run nothing
            -- (REFERENCES on a certificate or asymmetric key encrypts with the public key only; on an assembly, type,
            -- XML schema collection or catalog it is used by CREATE statements the login cannot run; on a symmetric
            -- key protected by a password, either right plus that password opens the key, which reads with a secret
            -- the login already holds and changes nothing); EXECUTE on a
            -- table type, an alias type or an XML schema collection only declares a variable. EXECUTE on a CLR type
            -- runs the assembly's code. SEND on a service puts messages on its queue (from a TRUSTWORTHY database).
            -- An owner holds CONTROL, which is a finding. Built-in assemblies are asked too: Microsoft.SqlServer.Types
            -- (owner sys) accepts ALTER, CONTROL and TAKE OWNERSHIP grants; ALTER ASSEMBLY and DROP ASSEMBLY are
            -- still refused on it, but CONTROL or TAKE OWNERSHIP lets the login make itself its owner (measured).
            -- Built-in types and the XML schema collections in schema sys are left out because
            -- SQL Server refuses every right on them: error 15151 on a built-in type, 4606 on sys.sys (measured for
            -- each right the class lists).
            SELECT p.permission_name, k.scope, k.name, NULL, HAS_PERMS_BY_NAME(k.name, k.class, p.permission_name)
            FROM (
                SELECT CAST(N'SERVICE' AS nvarchar(60)) COLLATE DATABASE_DEFAULT, CAST(N'service' AS nvarchar(30)) COLLATE DATABASE_DEFAULT,
                       CAST(QUOTENAME(s.name) AS nvarchar(600)) COLLATE DATABASE_DEFAULT, 0
                FROM sys.services AS s
                UNION ALL
                SELECT N'CERTIFICATE', N'certificate', QUOTENAME(c.name), 0 FROM sys.certificates AS c
                UNION ALL
                SELECT N'ASYMMETRIC KEY', N'asymmetric key', QUOTENAME(a.name), 0 FROM sys.asymmetric_keys AS a
                UNION ALL
                SELECT N'SYMMETRIC KEY', N'symmetric key', QUOTENAME(y.name), 0 FROM sys.symmetric_keys AS y
                UNION ALL
                SELECT N'ASSEMBLY', N'assembly', QUOTENAME(a.name), 0 FROM sys.assemblies AS a
                UNION ALL
                SELECT N'TYPE', N'type', QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name), 1 - t.is_assembly_type
                FROM sys.types AS t
                WHERE t.is_user_defined = 1
                UNION ALL
                SELECT N'XML SCHEMA COLLECTION', N'xml schema collection',
                       QUOTENAME(SCHEMA_NAME(x.schema_id)) + N'.' + QUOTENAME(x.name), 1
                FROM sys.xml_schema_collections AS x
                WHERE x.schema_id <> SCHEMA_ID(N'sys')
                UNION ALL
                SELECT N'FULLTEXT CATALOG', N'full-text catalog', QUOTENAME(t.name), 0 FROM sys.fulltext_catalogs AS t
            ) AS k(class, scope, name, execute_allowed)
            JOIN sys.fn_builtin_permissions(DEFAULT) AS p ON p.class_desc = k.class
            WHERE p.permission_name NOT IN (N'VIEW DEFINITION', N'REFERENCES')
              AND (p.permission_name <> N'EXECUTE' OR k.execute_allowed = 0)
            UNION ALL
            -- SEND alone does not make a service visible in sys.services, but the grant row is visible in
            -- sys.database_permissions when it is the login's own, a role's it is in, or public's. Such a service
            -- is named by its id, once however many grant rows reach the login: held if any row answers 1, else
            -- could not be checked if any answers NULL, else not held. A deny next to the grant is not read: the
            -- grant is still a finding.
            SELECT N'SEND', N'service', CONCAT(N'id ', u.major_id), NULL,
                   CASE WHEN MAX(CASE WHEN u.state = 1 THEN 1 ELSE 0 END) = 1 THEN 1
                        WHEN COUNT(*) > COUNT(u.state) THEN NULL
                        ELSE 0 END
            FROM (
                SELECT p.major_id,
                       CASE WHEN p.grantee_principal_id = DATABASE_PRINCIPAL_ID() THEN 1
                            WHEN g.type IN ('R', 'G', 'X') THEN IS_MEMBER(g.name)
                            WHEN g.principal_id IS NOT NULL THEN 0 END
                FROM sys.database_permissions AS p
                LEFT JOIN sys.database_principals AS g ON g.principal_id = p.grantee_principal_id
                WHERE p.class = 17 AND p.type = 'SN' AND p.state IN ('G', 'W')
                  AND NOT EXISTS (SELECT 1 FROM sys.services AS s WHERE s.service_id = p.major_id)
            ) AS u(major_id, state)
            GROUP BY u.major_id
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

using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>A schema tool's JSON text and the number of items it lists.</summary>
internal sealed record SchemaResult(string Payload, int ItemCount, IReadOnlyList<string> Tables, bool Truncated = false);

/// <summary>
/// The fixed catalogue queries behind <c>list_tables</c> and <c>describe_table</c>. Caller input reaches SQL Server
/// only as a parameter value.
/// </summary>
internal static class SchemaQueries
{
    /// <summary>The most objects one <c>list_tables</c> call returns.</summary>
    public const int MaxListItems = 500;

    /// <summary>The longest <c>name_filter</c> accepted.</summary>
    public const int MaxNameFilterChars = 256;

    /// <summary>The longest name part SQL Server accepts (<c>sysname</c>).</summary>
    public const int MaxNamePartChars = 128;

    public const string ReasonMaxItems = "max_items";

    /// <summary>
    /// The longest <c>describe_table</c> name accepted: four parts, each fully bracketed with every character a doubled
    /// ], and three dots. No longer text can be a name.
    /// </summary>
    public const int MaxNameChars = (4 * (2 + (2 * MaxNamePartChars))) + 3;

    // TOP stops the scan at the cap; COUNT_BIG(*) OVER () is computed before TOP, so it is the number of every match.
    private const string ListSql = """
        SELECT TOP (@cap) s.name AS schema_name, o.name AS object_name,
               CASE o.type WHEN 'U' THEN N'table' ELSE N'view' END AS kind, COUNT_BIG(*) OVER () AS total_count
        FROM sys.objects AS o
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND (@schema IS NULL OR s.name = @schema)
          AND (@pattern IS NULL OR o.name LIKE @pattern)
        ORDER BY s.name, o.name;
        """;

    // The name is resolved by PARSENAME and equality against the catalogue of this database only: no OBJECT_ID, so a
    // name in another database or in tempdb can never resolve to an object id here.
    private const string DescribeSql = """
        DECLARE @id int = (
            SELECT o.object_id
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
              AND PARSENAME(@name, 4) IS NULL
              AND (PARSENAME(@name, 3) IS NULL OR PARSENAME(@name, 3) = DB_NAME())
              AND o.name = PARSENAME(@name, 1)
              AND s.name = COALESCE(PARSENAME(@name, 2), SCHEMA_NAME()));
        SELECT s.name, o.name, CASE o.type WHEN 'U' THEN N'table' ELSE N'view' END
        FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.object_id = @id;
        SELECT c.name, t.name, t.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed
        FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = @id
        ORDER BY c.column_id;
        SELECT i.name, c.name
        FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = @id AND i.is_primary_key = 1 AND ic.key_ordinal > 0
        ORDER BY ic.key_ordinal;
        SELECT fk.name, pc.name, rs.name, ro.name, rc.name
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.objects AS ro ON ro.object_id = fkc.referenced_object_id
        JOIN sys.schemas AS rs ON rs.schema_id = ro.schema_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = @id
        ORDER BY fk.name, fkc.constraint_column_id;
        """;

    /// <summary>
    /// Lists at most <see cref="MaxListItems"/> user tables and views, stopping earlier when the payload would pass
    /// <paramref name="maxChars"/>. The payload carries the total number of matches either way.
    /// </summary>
    public static async Task<SchemaResult> ListTablesAsync(
        SqlGateway gateway, string? schema, string? nameFilter, int maxChars, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await gateway.OpenSessionAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
        await using var command = gateway.CreateCommand(connection, ListSql, null, timeoutSeconds);
        command.Parameters.Add(new SqlParameter("@cap", SqlDbType.Int) { Value = MaxListItems });
        command.Parameters.Add(TextParameter("@schema", schema));
        command.Parameters.Add(TextParameter("@pattern", NamePattern(nameFilter)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<(string Schema, string Name, string Kind)>();
        long totalCount = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            totalCount = reader.GetInt64(3);
        }

        // A read the deadline cancelled ends the call as a timeout, never as a shorter list.
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new StringBuilder("{\"tables\":[");
        var endReserve = ListEnding(MaxListItems, totalCount, ResultPayload.ReasonMaxChars).Length;
        var count = 0;
        string? truncatedReason = null;
        foreach (var (s, n, k) in items)
        {
            var item = ResultPayload.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("schema", s);
                w.WriteString("name", n);
                w.WriteString("kind", k);
                w.WriteEndObject();
            });
            var separator = count == 0 ? 0 : 1;
            if (payload.Length + separator + item.Length + endReserve > maxChars)
            {
                truncatedReason = ResultPayload.ReasonMaxChars;
                break;
            }

            if (separator == 1)
            {
                payload.Append(',');
            }

            payload.Append(item);
            count++;
        }

        if (truncatedReason is null && totalCount > count)
        {
            truncatedReason = ReasonMaxItems;
        }

        payload.Append(ListEnding(count, totalCount, truncatedReason));
        return new SchemaResult(payload.ToString(), count, [], truncatedReason is not null);
    }

    /// <summary>
    /// The LIKE pattern for a <c>name_filter</c>: null for no filter; text containing % as given; any other text as a
    /// literal substring, with the LIKE wildcards in it bracketed so they match only themselves.
    /// </summary>
    internal static string? NamePattern(string? nameFilter)
    {
        if (string.IsNullOrEmpty(nameFilter))
        {
            return null;
        }

        if (nameFilter.Contains('%', StringComparison.Ordinal))
        {
            return nameFilter;
        }

        var pattern = new StringBuilder(nameFilter.Length + 8).Append('%');
        foreach (var ch in nameFilter)
        {
            if (ch is '[' or '_' or '%')
            {
                pattern.Append('[').Append(ch).Append(']');
            }
            else
            {
                pattern.Append(ch);
            }
        }

        return pattern.Append('%').ToString();
    }

    /// <summary>
    /// True when <paramref name="name"/> cannot be the name of any object: longer than any valid multi-part name, or a
    /// well-formed name with more than four parts or a part longer than 128 characters. Malformed names are left to
    /// SQL Server, which answers them as not found.
    /// </summary>
    internal static bool CannotNameAnObject(string name)
    {
        if (name.Length > MaxNameChars)
        {
            return true;
        }

        var parts = SplitNameParts(name);
        return parts is not null && (parts.Count > 4 || parts.Exists(p => p.Length > MaxNamePartChars));
    }

    /// <summary>Splits a multi-part name into its unquoted parts; null when the text is not well formed.</summary>
    private static List<string>? SplitNameParts(string name)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        var i = 0;
        while (true)
        {
            if (i < name.Length && name[i] is '[' or '"')
            {
                var close = name[i] == '[' ? ']' : '"';
                i++;
                while (true)
                {
                    if (i >= name.Length)
                    {
                        return null;
                    }

                    if (name[i] == close)
                    {
                        if (i + 1 < name.Length && name[i + 1] == close)
                        {
                            part.Append(close);
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    part.Append(name[i++]);
                }

                if (i < name.Length && name[i] != '.')
                {
                    return null;
                }
            }
            else
            {
                while (i < name.Length && name[i] != '.')
                {
                    if (name[i] is '[' or ']' or '"')
                    {
                        return null;
                    }

                    part.Append(name[i++]);
                }
            }

            parts.Add(part.ToString());
            part.Clear();
            if (i >= name.Length)
            {
                return parts;
            }

            i++;
        }
    }

    private static string ListEnding(int count, long totalCount, string? reason)
    {
        var reasonJson = reason is null ? "null" : $"\"{reason}\"";
        var truncatedJson = reason is null ? "false" : "true";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"],\"row_count\":{count},\"total_count\":{totalCount},\"truncated\":{truncatedJson},\"truncated_reason\":{reasonJson}}}");
    }

    /// <summary>
    /// Describes one table or view, or returns null when no user table or view has that name. Columns, then foreign
    /// keys, are added while the payload stays within <paramref name="maxChars"/>; the payload says whether any was left
    /// out and carries the full column and foreign key counts.
    /// </summary>
    public static async Task<SchemaResult?> DescribeTableAsync(
        SqlGateway gateway, string table, int maxChars, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await gateway.OpenSessionAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
        await using var command = gateway.CreateCommand(connection, DescribeSql, null, timeoutSeconds);
        command.Parameters.Add(TextParameter("@name", table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // A read the deadline cancelled ends the call as a timeout, never as NotFound.
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        var (schemaName, objectName, kind) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<(string Name, string Type, bool Nullable, bool Identity, bool Computed)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add((
                reader.GetString(0),
                FormatType(reader.GetString(1), reader.GetBoolean(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5)),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        string? primaryKeyName = null;
        var primaryKey = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            primaryKeyName = reader.GetString(0);
            primaryKey.Add(reader.GetString(1));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeys = new List<(string Name, string Column, string RefSchema, string RefTable, string RefColumn)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var columnItems = columns.Select(c => ResultPayload.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("type", c.Type);
            w.WriteBoolean("nullable", c.Nullable);
            w.WriteBoolean("identity", c.Identity);
            w.WriteBoolean("computed", c.Computed);
            w.WriteEndObject();
        })).ToList();
        var foreignKeyItems = foreignKeys.GroupBy(f => f.Name).Select(group => ResultPayload.Json(w =>
        {
            var first = group.First();
            w.WriteStartObject();
            w.WriteString("name", group.Key);
            WriteStrings(w, "columns", group.Select(f => f.Column));
            w.WriteStartObject("references");
            w.WriteString("schema", first.RefSchema);
            w.WriteString("table", first.RefTable);
            WriteStrings(w, "columns", group.Select(f => f.RefColumn));
            w.WriteEndObject();
            w.WriteEndObject();
        })).ToList();
        var primaryKeyJson = primaryKeyName is null
            ? "null"
            : ResultPayload.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", primaryKeyName);
                WriteStrings(w, "columns", primaryKey);
                w.WriteEndObject();
            });

        var head = string.Concat(
            "{\"schema\":", JsonString(schemaName), ",\"name\":", JsonString(objectName), ",\"kind\":", JsonString(kind),
            ",\"columns\":[");
        var middle = $"],\"primary_key\":{primaryKeyJson},\"foreign_keys\":[";
        var endReserve = DescribeEnding(columnItems.Count, foreignKeyItems.Count, ResultPayload.ReasonMaxChars).Length;
        if (head.Length + middle.Length + endReserve > maxChars)
        {
            throw new ToolRefusalException(new ToolFailure(
                "error", "ResultTooWide", "ResultTooWide: the table's name and primary key alone are larger than the character cap."));
        }

        var payload = new StringBuilder(head);
        var writtenColumns = AppendWhileFits(payload, columnItems, middle.Length + endReserve, maxChars);
        payload.Append(middle);
        var writtenForeignKeys = writtenColumns == columnItems.Count
            ? AppendWhileFits(payload, foreignKeyItems, endReserve, maxChars)
            : 0;
        var truncated = writtenColumns < columnItems.Count || writtenForeignKeys < foreignKeyItems.Count;
        payload.Append(DescribeEnding(columnItems.Count, foreignKeyItems.Count, truncated ? ResultPayload.ReasonMaxChars : null));
        return new SchemaResult(payload.ToString(), writtenColumns, [$"{schemaName}.{objectName}"], truncated);
    }

    /// <summary>Appends comma-separated items while the payload plus <paramref name="reserve"/> stays within the cap.</summary>
    private static int AppendWhileFits(StringBuilder payload, IReadOnlyList<string> items, int reserve, int maxChars)
    {
        var count = 0;
        foreach (var item in items)
        {
            var separator = count == 0 ? 0 : 1;
            if (payload.Length + separator + item.Length + reserve > maxChars)
            {
                break;
            }

            if (separator == 1)
            {
                payload.Append(',');
            }

            payload.Append(item);
            count++;
        }

        return count;
    }

    private static string DescribeEnding(int columnCount, int foreignKeyCount, string? reason)
    {
        var reasonJson = reason is null ? "null" : $"\"{reason}\"";
        var truncatedJson = reason is null ? "false" : "true";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"],\"column_count\":{columnCount},\"foreign_key_count\":{foreignKeyCount},\"truncated\":{truncatedJson},\"truncated_reason\":{reasonJson}}}");
    }

    private static string JsonString(string value) => ResultPayload.Json(w => w.WriteStringValue(value));

    /// <summary>The column type as it is written in T-SQL, for example <c>nvarchar(100)</c> or <c>decimal(12,2)</c>.</summary>
    internal static string FormatType(string typeName, bool userDefined, short maxLength, byte precision, byte scale)
    {
        if (userDefined)
        {
            return typeName;
        }

        static string Length(int length) => length == -1 ? "max" : length.ToString(CultureInfo.InvariantCulture);

        return typeName switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({Length(maxLength)})",
            "nvarchar" or "nchar" => $"{typeName}({Length(maxLength == -1 ? -1 : maxLength / 2)})",
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision},{scale})"),
            "datetime2" or "time" or "datetimeoffset" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})"),
            _ => typeName,
        };
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static SqlParameter TextParameter(string name, string? value) =>
        new(name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value };
}

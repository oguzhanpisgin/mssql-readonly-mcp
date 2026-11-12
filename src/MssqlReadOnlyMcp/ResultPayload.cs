using System.Buffers;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>The rows read for one query and the JSON text returned to the client.</summary>
internal sealed record QueryResult(string Payload, int RowCount, bool Truncated, string? TruncatedReason);

/// <summary>
/// Reads a result set row by row into the JSON payload, stopping at the row cap or before the payload would pass
/// the character cap. No SQL text is changed to enforce a cap.
/// </summary>
internal static class ResultPayload
{
    public const int MaxCellChars = 2000;
    public const string CellCutMarker = "…[cut: value longer than 2000 characters]";
    public const string ReasonMaxRows = "max_rows";
    public const string ReasonMaxChars = "max_chars";
    public const string ReasonLongValue = "long_value";

    /// <summary>
    /// The most bytes of one binary value, or characters of one text or xml value, that are read: 1 MiB. A longer
    /// value ends the result after its row (reason <see cref="ReasonLongValue"/>) and the caller cancels the command,
    /// so the value is never held in memory and no further row is produced. Closing the reader still reads the rest
    /// of the value off the network up to SQL Server's acknowledgement of the cancel and discards it (a stated limit:
    /// the client library has no way to drop the connection instead).
    /// </summary>
    public const int MaxBinaryCount = 1024 * 1024;

    /// <summary>The summary of a binary value longer than <see cref="MaxBinaryCount"/>.</summary>
    public const string BinaryOverMarker = "<binary over 1048576 bytes>";

    /// <summary>The cells after a value longer than <see cref="MaxBinaryCount"/> in the same row: not read.</summary>
    public const string NotReadMarker = "<not read: an earlier value in the row is longer than 1048576>";

    /// <summary>The longest truncation reason; the ending reserved before each row is sized for it.</summary>
    private static readonly string LongestReason =
        new[] { ReasonMaxRows, ReasonMaxChars, ReasonLongValue }.MaxBy(reason => reason.Length)!;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>Serialises one value written by <paramref name="write"/> to JSON text.</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads every row of <paramref name="reader"/> (opened with <see cref="CommandBehavior.SequentialAccess"/>) until a
    /// cap is reached. Returns with <see cref="QueryResult.Truncated"/> set when the caller must cancel the command.
    /// </summary>
    public static async Task<QueryResult> ReadAsync(SqlDataReader reader, int maxRows, int maxChars, CancellationToken cancellationToken)
    {
        var fieldCount = reader.FieldCount;
        var columns = Json(w =>
        {
            w.WriteStartArray();
            for (var i = 0; i < fieldCount; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", reader.GetName(i));
                w.WriteString("type", reader.GetDataTypeName(i));
                w.WriteEndObject();
            }

            w.WriteEndArray();
        });

        var payload = new StringBuilder(Math.Min(maxChars, 8192));
        payload.Append("{\"columns\":").Append(columns).Append(",\"rows\":[");

        // The longest possible ending, so a row is only added when the finished payload still fits.
        var endReserve = Ending(maxRows, true, LongestReason).Length;
        if (payload.Length + endReserve > maxChars)
        {
            throw new ToolRefusalException(new ToolFailure(
                "error", "ResultTooWide", "ResultTooWide: the column list alone is larger than the character cap."));
        }

        var rowCount = 0;
        string? truncatedReason = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rowCount == maxRows)
            {
                truncatedReason = ReasonMaxRows;
                break;
            }

            var separator = rowCount == 0 ? 0 : 1;
            var room = maxChars - payload.Length - separator - endReserve;
            var (row, longValue) = await ReadRowAsync(reader, fieldCount, room, cancellationToken).ConfigureAwait(false);
            if (row is null || row.Length > room)
            {
                truncatedReason = ReasonMaxChars;
                break;
            }

            if (separator == 1)
            {
                payload.Append(',');
            }

            payload.Append(row);
            rowCount++;
            if (longValue)
            {
                truncatedReason = ReasonLongValue;
                break;
            }
        }

        payload.Append(Ending(rowCount, truncatedReason is not null, truncatedReason));
        return new QueryResult(payload.ToString(), rowCount, truncatedReason is not null, truncatedReason);
    }

    private static string Ending(int rowCount, bool truncated, string? reason)
    {
        var count = rowCount.ToString(CultureInfo.InvariantCulture);
        var reasonJson = reason is null ? "null" : $"\"{reason}\"";
        var truncatedJson = truncated ? "true" : "false";
        return $"],\"row_count\":{count},\"truncated\":{truncatedJson},\"truncated_reason\":{reasonJson}}}";
    }

    /// <summary>
    /// One row as JSON, and whether a value in it was longer than <see cref="MaxBinaryCount"/>. Null when what was read
    /// of the row already cannot fit in <paramref name="room"/> characters: the rest of the row is not read.
    /// </summary>
    private static async Task<(string? Json, bool LongValue)> ReadRowAsync(
        SqlDataReader reader, int fieldCount, int room, CancellationToken cancellationToken)
    {
        var cells = new object?[fieldCount];
        var longValue = false;

        // A lower bound on the row's JSON length: the brackets, the separators and one character per value, plus the
        // characters and quotes of each text value read so far (escaping only lengthens it). Past the room, the row
        // cannot fit whatever the remaining values are.
        long least = 2 + (fieldCount - 1) + fieldCount;
        for (var i = 0; i < fieldCount; i++)
        {
            var (cell, isLong) = await ReadCellAsync(reader, i, cancellationToken).ConfigureAwait(false);
            cells[i] = cell;
            least += cell is string text ? text.Length + 1 : 0;
            if (least > room)
            {
                return (null, false);
            }

            if (isLong)
            {
                // The rest of this value is still on the wire; reading the next column would transfer it.
                for (var j = i + 1; j < fieldCount; j++)
                {
                    cells[j] = NotReadMarker;
                }

                longValue = true;
                break;
            }
        }

        return (Json(w =>
        {
            w.WriteStartArray();
            foreach (var cell in cells)
            {
                WriteCell(w, cell);
            }

            w.WriteEndArray();
        }), longValue);
    }

    /// <summary>
    /// One cell's value, and whether it was longer than <see cref="MaxBinaryCount"/> (bytes for binary, characters
    /// for text and xml). Such a value is read no further: its rest stays on the wire until the command is cancelled.
    /// </summary>
    private static async Task<(object? Value, bool LongValue)> ReadCellAsync(SqlDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return (null, false);
        }

        Type type;
        try
        {
            type = reader.GetFieldType(ordinal);
        }
        catch (Exception exception) when (IsMissingUdtSupport(exception))
        {
            return (Unsupported(reader, ordinal), false);
        }

        if (type == typeof(byte[]))
        {
            // Count at most MaxBinaryCount bytes; a longer value is not read to its end.
            await using var stream = reader.GetStream(ordinal);
            var buffer = new byte[8192];
            long length = 0;
            int read;
            while (length <= MaxBinaryCount
                   && (read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            return length > MaxBinaryCount
                ? (BinaryOverMarker, true)
                : (string.Create(CultureInfo.InvariantCulture, $"<binary {length} bytes>"), false);
        }

        if (type == typeof(string) && reader.GetDataTypeName(ordinal) == "xml")
        {
            // GetTextReader refuses xml; GetChars reads it as text from the given offset.
            var head = new char[MaxCellChars + 1];
            var scratch = new char[8192];
            long total = 0;
            long read;
            while (total <= MaxBinaryCount)
            {
                // GetChars is synchronous and takes no token: the deadline is checked between its calls.
                cancellationToken.ThrowIfCancellationRequested();
                var target = total < head.Length ? head : scratch;
                var offset = total < head.Length ? (int)total : 0;
                read = reader.GetChars(ordinal, total, target, offset, target.Length - offset);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return CutText(head, total);
        }

        if (type == typeof(string) || type == typeof(char[]))
        {
            using var text = reader.GetTextReader(ordinal);
            var head = new char[MaxCellChars + 1];
            var scratch = new char[8192];
            long total = 0;
            int read;
            while (total <= MaxBinaryCount)
            {
                var target = total < head.Length ? head.AsMemory((int)total) : scratch.AsMemory();
                read = await text.ReadAsync(target, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return CutText(head, total);
        }

        if (type == typeof(decimal) && reader.GetDataTypeName(ordinal) is "decimal" or "numeric")
        {
            // decimal(38, s) can exceed System.Decimal; such values are returned as their exact text.
            var exact = reader.GetSqlDecimal(ordinal);
            try
            {
                return (exact.Value, false);
            }
            catch (OverflowException)
            {
                return (exact.ToString(), false);
            }
        }

        object value;
        try
        {
            value = reader.GetValue(ordinal);
        }
        catch (Exception exception) when (IsMissingUdtSupport(exception))
        {
            return (Unsupported(reader, ordinal), false);
        }

        return value switch
        {
            string s when s.Length > MaxCellChars => (string.Concat(s.AsSpan(0, MaxCellChars), CellCutMarker), false),
            _ => (value, false),
        };
    }

    /// <summary>The first characters of a text value of <paramref name="total"/> characters read, cut if needed.</summary>
    private static (object Value, bool LongValue) CutText(char[] head, long total) =>
        total > MaxCellChars
            ? (string.Concat(head.AsSpan(0, MaxCellChars), CellCutMarker), total > MaxBinaryCount)
            : (new string(head, 0, (int)total), false);

    // CLR user-defined types (geography, hierarchyid) need assemblies this server does not load.
    private static bool IsMissingUdtSupport(Exception exception) =>
        exception is InvalidCastException or FileNotFoundException or FileLoadException or NotSupportedException or TypeLoadException;

    private static string Unsupported(SqlDataReader reader, int ordinal) =>
        $"<unsupported value of type {reader.GetDataTypeName(ordinal)}>";

    private static void WriteCell(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte n:
                writer.WriteNumberValue(n);
                break;
            case short n:
                writer.WriteNumberValue(n);
                break;
            case int n:
                writer.WriteNumberValue(n);
                break;
            case long n:
                writer.WriteNumberValue(n);
                break;
            case decimal n:
                writer.WriteNumberValue(n);
                break;
            case double n:
                writer.WriteNumberValue(n);
                break;
            case float n:
                writer.WriteNumberValue(n);
                break;
            case DateTime d:
                writer.WriteStringValue(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset d:
                writer.WriteStringValue(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
                break;
            case TimeSpan t:
                writer.WriteStringValue(t.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString("D"));
                break;
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                writer.WriteStringValue(text.Length > MaxCellChars ? string.Concat(text.AsSpan(0, MaxCellChars), CellCutMarker) : text);
                break;
        }
    }
}

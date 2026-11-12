using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MssqlReadOnlyMcp.Audit;

/// <summary>The values of one audit line, everything except <c>hash</c>.</summary>
internal sealed record AuditRecord(
    long Seq,
    string Ts,
    string Session,
    string Tool,
    string Decision,
    string Reason,
    string? Sql,
    string? SqlSha256,
    IReadOnlyList<string> Tables,
    int? Rows,
    bool Truncated,
    long DurationMs,
    string? ResultSha256,
    string PrevHash);

/// <summary>
/// The one canonical line form, shared by the writer and the verifier.
/// </summary>
/// <remarks>
/// <para>
/// Canonical form of a line, produced only by the hand-written <see cref="Serialize"/> below (no
/// <c>Utf8JsonWriter</c> and no <c>JavaScriptEncoder</c>, whose escaping set follows the running runtime's Unicode
/// tables and differs between runtime versions): one JSON object whose properties appear exactly in the order of
/// <see cref="FieldNames"/>, no whitespace between tokens, followed by one <c>\n</c> byte.
/// </para>
/// <para>
/// Strings: <c>"</c> and <c>\</c> become <c>\"</c> and <c>\\</c>; U+0008, U+000C, U+000A, U+000D and U+0009 become
/// <c>\b</c>, <c>\f</c>, <c>\n</c>, <c>\r</c> and <c>\t</c>; every other code point in U+0000..U+001F, and U+007F,
/// U+2028 and U+2029, become <c>\u</c> followed by four lower-case hex digits (for example <c>\u001f</c>,
/// <c>\u007f</c>); every other Unicode scalar value, supplementary characters included, is written as raw UTF-8.
/// An unpaired UTF-16 surrogate is written as U+FFFD (see also <see cref="ToWellFormed(string)"/>), because JSON
/// read back cannot reproduce it. So a line never contains a raw newline, and the bytes depend only on the values.
/// </para>
/// <para>
/// Numbers (<c>seq</c>, <c>rows</c>, <c>duration_ms</c>) are integers written with invariant-culture integer
/// formatting: an optional <c>-</c> and ASCII digits, no leading zeros. Booleans and null are the literals
/// <c>true</c>, <c>false</c> and <c>null</c>, written by this code. <c>tables</c> is an array of strings.
/// <c>ts</c> is UTC in the form <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>. Hex digests are 64 lower-case characters.
/// </para>
/// <para>
/// <c>hash</c> is the hex SHA-256 of the canonical bytes of the same object written without the <c>hash</c>
/// property (and without the trailing <c>\n</c>). The verifier parses every line, rebuilds it from the parsed
/// values with this same serialiser and requires byte equality, so any line that is not in this exact form fails
/// as <c>NotCanonical</c>.
/// </para>
/// </remarks>
internal static class AuditFormat
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static readonly string[] FieldNames =
    [
        "seq", "ts", "session", "tool", "decision", "reason", "sql", "sql_sha256", "tables", "rows", "truncated",
        "duration_ms", "result_sha256", "prev_hash", "hash",
    ];

    private const string LowerHex = "0123456789abcdef";

    /// <summary>Serialises the record; with <paramref name="hash"/> null the <c>hash</c> property is omitted.</summary>
    public static byte[] Serialize(AuditRecord r, string? hash)
    {
        var b = new ArrayBufferWriter<byte>(512);
        Ascii(b, "{\"seq\":");
        Integer(b, r.Seq);
        Ascii(b, ",\"ts\":");
        JsonString(b, r.Ts);
        Ascii(b, ",\"session\":");
        JsonString(b, r.Session);
        Ascii(b, ",\"tool\":");
        JsonString(b, r.Tool);
        Ascii(b, ",\"decision\":");
        JsonString(b, r.Decision);
        Ascii(b, ",\"reason\":");
        JsonString(b, r.Reason);
        Ascii(b, ",\"sql\":");
        NullableString(b, r.Sql);
        Ascii(b, ",\"sql_sha256\":");
        NullableString(b, r.SqlSha256);
        Ascii(b, ",\"tables\":[");
        for (int i = 0; i < r.Tables.Count; i++)
        {
            if (i > 0)
            {
                Ascii(b, ",");
            }

            JsonString(b, r.Tables[i]);
        }

        Ascii(b, "],\"rows\":");
        if (r.Rows is int rows)
        {
            Integer(b, rows);
        }
        else
        {
            Ascii(b, "null");
        }

        Ascii(b, ",\"truncated\":");
        Ascii(b, r.Truncated ? "true" : "false");
        Ascii(b, ",\"duration_ms\":");
        Integer(b, r.DurationMs);
        Ascii(b, ",\"result_sha256\":");
        NullableString(b, r.ResultSha256);
        Ascii(b, ",\"prev_hash\":");
        JsonString(b, r.PrevHash);
        if (hash is not null)
        {
            Ascii(b, ",\"hash\":");
            JsonString(b, hash);
        }

        Ascii(b, "}");
        return b.WrittenSpan.ToArray();
    }

    /// <summary>Writes ASCII-only text as bytes.</summary>
    private static void Ascii(ArrayBufferWriter<byte> b, string ascii)
    {
        Span<byte> span = b.GetSpan(ascii.Length);
        for (int i = 0; i < ascii.Length; i++)
        {
            span[i] = (byte)ascii[i];
        }

        b.Advance(ascii.Length);
    }

    private static void Integer(ArrayBufferWriter<byte> b, long value) =>
        Ascii(b, value.ToString(CultureInfo.InvariantCulture));

    private static void NullableString(ArrayBufferWriter<byte> b, string? value)
    {
        if (value is null)
        {
            Ascii(b, "null");
        }
        else
        {
            JsonString(b, value);
        }
    }

    /// <summary>Writes a JSON string with the fixed escaping set described on this class.</summary>
    private static void JsonString(ArrayBufferWriter<byte> b, string value)
    {
        Ascii(b, "\"");
        Span<byte> utf8 = stackalloc byte[4];
        int i = 0;
        while (i < value.Length)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(i), out Rune rune, out int used) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
            }

            i += Math.Max(used, 1);
            int v = rune.Value;
            switch (v)
            {
                case '"':
                    Ascii(b, "\\\"");
                    break;
                case '\\':
                    Ascii(b, "\\\\");
                    break;
                case '\b':
                    Ascii(b, "\\b");
                    break;
                case '\f':
                    Ascii(b, "\\f");
                    break;
                case '\n':
                    Ascii(b, "\\n");
                    break;
                case '\r':
                    Ascii(b, "\\r");
                    break;
                case '\t':
                    Ascii(b, "\\t");
                    break;
                default:
                    if (v < 0x20 || v == 0x7F || v == 0x2028 || v == 0x2029)
                    {
                        Ascii(b, "\\u");
                        Span<byte> hex = b.GetSpan(4);
                        hex[0] = (byte)LowerHex[(v >> 12) & 0xF];
                        hex[1] = (byte)LowerHex[(v >> 8) & 0xF];
                        hex[2] = (byte)LowerHex[(v >> 4) & 0xF];
                        hex[3] = (byte)LowerHex[v & 0xF];
                        b.Advance(4);
                    }
                    else
                    {
                        int n = rune.EncodeToUtf8(utf8);
                        b.Write(utf8[..n]);
                    }

                    break;
            }
        }

        Ascii(b, "\"");
    }

    /// <summary>
    /// Replaces unpaired UTF-16 surrogates with U+FFFD. JSON text read back can never hold an unpaired surrogate,
    /// so without this a written line would not verify. <see cref="Sha256Hex(string)"/> gives the same digest for
    /// the original and the replaced text, because UTF-8 encoding performs the same replacement.
    /// </summary>
    public static string ToWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
            }
        }

        return text;
    }

    public static string ComputeHash(AuditRecord r) => Convert.ToHexStringLower(SHA256.HashData(Serialize(r, null)));

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string FormatTimestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static bool IsLowerHex64(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses one line (without its <c>\n</c>) into its values. Returns a reason word on failure, otherwise null.
    /// Only checks that the line is a JSON object with the fixed fields, in order, of the right types.
    /// </summary>
    public static string? TryParse(ReadOnlyMemory<byte> line, out AuditRecord? record, out string? hash)
    {
        record = null;
        hash = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "NotAnObject";
            }

            var values = new JsonElement[FieldNames.Length];
            int i = 0;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (i >= FieldNames.Length || property.Name != FieldNames[i])
                {
                    return "FieldOrder";
                }

                values[i++] = property.Value;
            }

            if (i != FieldNames.Length)
            {
                return "FieldOrder";
            }

            if (!TryLong(values[0], out long seq)
                || !TryString(values[1], out string ts)
                || !TryString(values[2], out string session)
                || !TryString(values[3], out string tool)
                || !TryString(values[4], out string decision)
                || !TryString(values[5], out string reason)
                || !TryNullableString(values[6], out string? sql)
                || !TryNullableString(values[7], out string? sqlSha)
                || !TryStringArray(values[8], out string[] tables)
                || !TryNullableInt(values[9], out int? rows)
                || !TryBool(values[10], out bool truncated)
                || !TryLong(values[11], out long durationMs)
                || !TryNullableString(values[12], out string? resultSha)
                || !TryString(values[13], out string prevHash)
                || !TryString(values[14], out string lineHash))
            {
                return "FieldType";
            }

            record = new AuditRecord(
                seq, ts, session, tool, decision, reason, sql, sqlSha, tables, rows, truncated, durationMs, resultSha, prevHash);
            hash = lineHash;
            return null;
        }
        catch (JsonException)
        {
            return "MalformedJson";
        }
        catch (InvalidOperationException)
        {
            // Raised by GetString for text that is not valid UTF-8 or UTF-16 once unescaped.
            return "MalformedJson";
        }
    }

    private static bool TryString(JsonElement e, out string value)
    {
        value = e.ValueKind == JsonValueKind.String ? e.GetString()! : string.Empty;
        return e.ValueKind == JsonValueKind.String;
    }

    private static bool TryNullableString(JsonElement e, out string? value)
    {
        value = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return e.ValueKind is JsonValueKind.String or JsonValueKind.Null;
    }

    private static bool TryLong(JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }

    private static bool TryNullableInt(JsonElement e, out int? value)
    {
        value = null;
        if (e.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v))
        {
            value = v;
            return true;
        }

        return false;
    }

    private static bool TryBool(JsonElement e, out bool value)
    {
        value = e.ValueKind == JsonValueKind.True;
        return e.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryStringArray(JsonElement e, out string[] value)
    {
        value = [];
        if (e.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<string>(e.GetArrayLength());
        foreach (JsonElement item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            items.Add(item.GetString()!);
        }

        value = [.. items];
        return true;
    }
}

/// <summary>Checks lines in order and carries the chain state between them.</summary>
internal sealed class AuditChainChecker
{
    public long LastSequence { get; private set; }

    public string LastHash { get; private set; } = AuditFormat.GenesisHash;

    /// <summary>Checks the next line (without its <c>\n</c>); returns a reason word when it is bad, otherwise null.</summary>
    public string? Check(ReadOnlyMemory<byte> line)
    {
        string? bad = AuditFormat.TryParse(line, out AuditRecord? record, out string? hash);
        if (bad is not null)
        {
            return bad;
        }

        if (!DateTime.TryParseExact(
                record!.Ts,
                AuditFormat.TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            return "BadTimestamp";
        }

        if (!AuditFormat.IsLowerHex64(hash) || !AuditFormat.IsLowerHex64(record.PrevHash)
            || (record.ResultSha256 is not null && !AuditFormat.IsLowerHex64(record.ResultSha256)))
        {
            return "BadDigest";
        }

        string? expectedSqlSha = record.Sql is null ? null : AuditFormat.Sha256Hex(record.Sql);
        if (!string.Equals(record.SqlSha256, expectedSqlSha, StringComparison.Ordinal))
        {
            return "SqlHashMismatch";
        }

        if (!line.Span.SequenceEqual(AuditFormat.Serialize(record, hash)))
        {
            return "NotCanonical";
        }

        if (!string.Equals(AuditFormat.ComputeHash(record), hash, StringComparison.Ordinal))
        {
            return "HashMismatch";
        }

        if (record.Seq != LastSequence + 1)
        {
            return "SequenceGap";
        }

        if (!string.Equals(record.PrevHash, LastHash, StringComparison.Ordinal))
        {
            return "ChainBreak";
        }

        LastSequence = record.Seq;
        LastHash = hash!;
        return null;
    }
}

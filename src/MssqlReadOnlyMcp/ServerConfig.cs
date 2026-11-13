using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace MssqlReadOnlyMcp;

/// <summary>Thrown when the configuration is missing or invalid. The message never contains a configured value.</summary>
internal sealed class ConfigException(string message) : Exception(message);

/// <summary>The server configuration, read from environment variables only.</summary>
/// <param name="ConnectionString">The connection string with <c>ApplicationIntent=ReadOnly</c> and the application name set.</param>
/// <param name="AuditLogPath">Path of the audit file; empty when the command does not need it.</param>
/// <param name="MaxRows">Row cap for <c>run_query</c>.</param>
/// <param name="MaxChars">Character cap for one tool result.</param>
/// <param name="TimeoutSeconds">Time limit of every call, and the command timeout of every command.</param>
/// <param name="LockTimeoutMs">Value of <c>SET LOCK_TIMEOUT</c>.</param>
internal sealed record ServerConfig(
    string ConnectionString,
    string AuditLogPath,
    int MaxRows,
    int MaxChars,
    int TimeoutSeconds,
    int LockTimeoutMs)
{
    public const string ApplicationName = "mssql-readonly-mcp";

    public const string ConnectionStringVariable = "MSSQL_CONNECTION_STRING";
    public const string AuditLogVariable = "MSSQL_MCP_AUDIT_LOG";
    public const string MaxRowsVariable = "MSSQL_MCP_MAX_ROWS";
    public const string MaxCharsVariable = "MSSQL_MCP_MAX_CHARS";
    public const string TimeoutVariable = "MSSQL_MCP_TIMEOUT_SECONDS";
    public const string LockTimeoutVariable = "MSSQL_MCP_LOCK_TIMEOUT_MS";

    public const int DefaultMaxRows = 200;
    public const int MinMaxRows = 1;
    public const int MaxRowsCeiling = 5000;

    public const int DefaultMaxChars = 40000;
    public const int MinMaxChars = 1000;
    public const int MaxCharsCeiling = 45000;

    public const int DefaultTimeoutSeconds = 30;
    public const int MinTimeoutSeconds = 1;
    public const int TimeoutCeilingSeconds = 300;

    public const int DefaultLockTimeoutMs = 5000;
    public const int MinLockTimeoutMs = 0;
    public const int LockTimeoutCeilingMs = 300000;

    /// <summary>Where the README creates the read-only login and checks it; named by the connection string messages.</summary>
    public const string LoginSection = "README step 2, \"Create and check the read-only login\"";

    /// <summary>The variable is set to an empty or whitespace-only text.</summary>
    public const string EmptyConnectionStringMessage =
        $"{ConnectionStringVariable} is set but empty: enter the connection string of the read-only login in the " +
        $"client's settings for this server ({LoginSection})";

    /// <summary>The variable holds a text that starts with <c>${</c> and ends with <c>}</c>, as a client template does.</summary>
    public const string PlaceholderConnectionStringMessage =
        $"{ConnectionStringVariable} holds a placeholder of the form ${{...}}: the client did not fill it in. Enter the " +
        "connection string in the client's settings for this server, or check that the client supports the setting, " +
        "then start the server again";

    /// <summary>The SQL Server client library cannot parse the variable's value.</summary>
    public const string InvalidConnectionStringMessage =
        $"{ConnectionStringVariable} is not a valid SQL Server connection string (the value is not shown): expected " +
        "keyword=value pairs separated by semicolons, with keywords such as Server, Database, User ID and Password, " +
        $"and nothing else around them. Check it against {LoginSection}";

    /// <summary>Reads and validates the configuration.</summary>
    /// <param name="read">Reads one environment variable.</param>
    /// <param name="requireAuditLog">Whether <c>MSSQL_MCP_AUDIT_LOG</c> is required.</param>
    /// <exception cref="ConfigException">A required value is missing, or a value is unparsable or out of range.</exception>
    public static ServerConfig Load(Func<string, string?> read, bool requireAuditLog)
    {
        var raw = read(ConnectionStringVariable);
        if (raw is null)
        {
            throw new ConfigException($"{ConnectionStringVariable} is required");
        }

        // The three messages below are fixed texts: none of them holds any character of the value, which can be a
        // password or a fragment of one.
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ConfigException(EmptyConnectionStringMessage);
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            throw new ConfigException(PlaceholderConnectionStringMessage);
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(raw);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // The parser's message can quote parts of the string, so it is not passed on.
            throw new ConfigException(InvalidConnectionStringMessage);
        }

        builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        builder.ApplicationName = ApplicationName;

        var auditLog = read(AuditLogVariable);
        if (requireAuditLog && string.IsNullOrWhiteSpace(auditLog))
        {
            throw new ConfigException($"{AuditLogVariable} is required");
        }

        return new ServerConfig(
            builder.ConnectionString,
            auditLog?.Trim() ?? string.Empty,
            ReadInt(read, MaxRowsVariable, DefaultMaxRows, MinMaxRows, MaxRowsCeiling),
            ReadInt(read, MaxCharsVariable, DefaultMaxChars, MinMaxChars, MaxCharsCeiling),
            ReadInt(read, TimeoutVariable, DefaultTimeoutSeconds, MinTimeoutSeconds, TimeoutCeilingSeconds),
            ReadInt(read, LockTimeoutVariable, DefaultLockTimeoutMs, MinLockTimeoutMs, LockTimeoutCeilingMs));
    }

    /// <summary>Every setting except the connection string, which can hold a password and is never printed.</summary>
    public override string ToString()
    {
        var text = new StringBuilder("ServerConfig { ");
        PrintMembers(text);
        return text.Append(" }").ToString();
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ConnectionString = <not shown>, AuditLogPath = {AuditLogPath}, ");
        builder.Append(CultureInfo.InvariantCulture, $"MaxRows = {MaxRows}, MaxChars = {MaxChars}, ");
        builder.Append(CultureInfo.InvariantCulture, $"TimeoutSeconds = {TimeoutSeconds}, LockTimeoutMs = {LockTimeoutMs}");
        return true;
    }

    private static int ReadInt(Func<string, string?> read, string name, int defaultValue, int min, int max)
    {
        var text = read(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new ConfigException($"{name} must be a whole number from {min} to {max}");
        }

        if (value < min || value > max)
        {
            throw new ConfigException($"{name} must be from {min} to {max}");
        }

        return value;
    }
}

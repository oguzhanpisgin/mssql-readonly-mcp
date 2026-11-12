using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MssqlReadOnlyMcp;

/// <summary>
/// The MCP surface: exactly three read-only tools, each a thin call into <see cref="ToolService"/>. Each returns its own
/// <see cref="CallToolResult"/>, which the SDK sends unchanged: a failure goes out with <c>isError</c> set and the
/// service's text as it is, which starts with the reason word. (A thrown <see cref="McpException"/> would reach the
/// client behind the SDK's prefix <c>An error occurred invoking '&lt;tool&gt;': </c>.)
/// </summary>
[McpServerToolType]
internal sealed class McpTools(ToolService service)
{
    [McpServerTool(Name = "list_tables", Title = "List tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the user tables and views of the connected database: schema, name and kind (table or view), at most " +
        "500 per call, ordered by schema and name. The JSON also has row_count, total_count (every match), truncated " +
        "and truncated_reason; when truncated, narrow the list with schema or name_filter.")]
    public Task<CallToolResult> ListTables(
        [Description("Optional schema name; when given, only objects of that schema are listed.")] string? schema = null,
        [Description(
            "Optional filter on the object name. Text without % is matched as a plain substring; text with % is a " +
            "LIKE pattern (% any text, _ one character, [] a character set).")] string? name_filter = null,
        CancellationToken cancellationToken = default) =>
        ResultAsync(service.ListTablesAsync(schema, name_filter, cancellationToken));

    [McpServerTool(Name = "describe_table", Title = "Describe table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Describes one table or view: columns (name, SQL type, nullable, identity, computed), primary key and foreign keys.")]
    public Task<CallToolResult> DescribeTable(
        [Description("Table or view name, optionally schema-qualified, for example dbo.Orders.")] string table,
        CancellationToken cancellationToken = default) =>
        ResultAsync(service.DescribeTableAsync(table, cancellationToken));

    [McpServerTool(Name = "run_query", Title = "Run read-only query", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Runs one read-only T-SQL SELECT statement and returns JSON with columns, rows, row_count, truncated and " +
        "truncated_reason. Anything other than a single plain SELECT is refused before it reaches SQL Server. " +
        "Results stop at a row cap and a character cap; long cells are cut and binary values are summarised.")]
    public Task<CallToolResult> RunQuery(
        [Description("One T-SQL SELECT statement.")] string sql,
        [Description("Optional row limit; values above the server's cap use the cap.")] int? max_rows = null,
        CancellationToken cancellationToken = default) =>
        ResultAsync(service.RunQueryAsync(sql, max_rows, cancellationToken));

    /// <summary>The payload as text, or the failure text (already audited by the service) as an error result.</summary>
    private static async Task<CallToolResult> ResultAsync(Task<string> call)
    {
        try
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = await call.ConfigureAwait(false) }] };
        }
        catch (McpException exception)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = exception.Message }] };
        }
    }
}

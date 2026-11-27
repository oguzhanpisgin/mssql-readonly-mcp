---
name: query-sql-server-read-only
description: Use when the user asks a question about data in their SQL Server database, wants to explore its tables, columns or keys, or asks for a T-SQL SELECT to be run, and the mssql-readonly tools (list_tables, describe_table, run_query) are available. Covers the order of the calls, what the read-only gate refuses, the result caps, error reasons and the audit file.
---

# Query SQL Server read-only

The `mssql-readonly` MCP server reads one SQL Server database as a login that passed the server's read-only
permission check at startup. It has three tools, all read-only:

| Tool | Use it for |
| --- | --- |
| `list_tables` | The tables and views the login can see. Optional `schema`, and `name_filter` (a substring, or a `LIKE` pattern when it contains `%`). At most 500 per call. |
| `describe_table` | Columns with types and nullability, primary key and foreign keys of one table, for example `dbo.Orders`. |
| `run_query` | One plain `SELECT`, in `sql`. Optional `max_rows`. |

## Work in this order

1. Find the tables with `list_tables`. Use `name_filter` on a large database instead of listing everything.
2. Read the columns and keys with `describe_table` before you write a query. Do not guess column names or joins:
   take them from the foreign keys it returns.
3. Run one `SELECT` with `run_query`. Name the columns you need instead of `SELECT *`, add `TOP (n)` with an
   `ORDER BY` when the user wants a sample or a ranking, and filter in SQL rather than reading many rows to filter
   them yourself.
4. Answer from the rows returned, and say when a result was cut (see "Caps").

## What the gate refuses

`run_query` accepts only text that passes an allowlist for one plain SELECT. It refuses, before anything reaches
SQL Server: `INSERT`, `UPDATE`, `DELETE`, `MERGE`, DDL, `EXEC`, `SELECT ... INTO`, `SELECT @v = ...`, more than one
statement or batch, `OPTION (...)` and join hints, table hints other than `NOLOCK` and `READUNCOMMITTED`,
four-part (linked server) names, three-part names for another database, and functions and syntax outside the
allowlist. The refusal text starts with a reason word such as `NotSelect` or `DisallowedSyntax`.

When a call is refused:

- Do not try to get the same effect another way, and do not hide a write inside a SELECT. The server is read-only
  on purpose and the refused call is already in the audit file.
- If the user asked for a change to data or schema, say that this server cannot make it and offer the T-SQL text
  for the user to review and run with their own tools.
- If a read was refused as `DisallowedSyntax`, `DisallowedQueryHint` or `ParseError`, rewrite it with plainer
  syntax: a subquery or a join in place of the refused construct, no `OPTION (...)` or join hint.
- `CrossDatabaseName` and `LinkedServerName` are boundaries, not syntax problems: the server reads only the
  database it connected to. Tell the user; do not look for another way to reach that data.
- `DisallowedTableHint`: drop the hint; only `NOLOCK` and `READUNCOMMITTED` are allowed.
- `TooLong`: the text is over 65,536 characters. Write a shorter query.

## Caps

Results are capped by rows (200 by default), characters (40,000 by default) and time (30 seconds per call by
default); the server's administrator can lower or raise them. A result that hit a cap has `truncated: true` and a
`truncated_reason`:

- `max_rows` or `max_chars`: tell the user the answer is partial. Narrow the query (fewer columns, a `WHERE`, an
  aggregate such as `COUNT(*)` or `SUM`) instead of paging through the table.
- `long_value`: a very long text or binary cell ended the result. Select `DATALENGTH(col)` or
  `SUBSTRING(col, 1, n)` instead of the column.
- `max_items` from `list_tables`: more than 500 names matched; `total_count` says how many. Narrow the call with
  `schema` or `name_filter`.

Text cells longer than 2,000 characters are cut, binary values come back as `<binary N bytes>`, and CLR types such
as `hierarchyid` come back as `<unsupported value of type X>`: `CAST(col AS nvarchar(4000))` reads their text form.

## Error reasons

A failed call returns text that starts with its reason word:

- `PermissionDenied` (SQL Server error 229): the login has no grant on that object. Do not look for a way around
  it; tell the user which object it was, so that an administrator can grant SELECT if it should be readable.
- `SqlError`: a T-SQL error. Read SQL Server's message, fix the query and try once more. Some newer syntax fails on
  SQL Server 2019.
- `Timeout`, `LockTimeout`, `DeadlockVictim`: the query took too long or met locks held by others. Make it
  smaller (a tighter `WHERE`, fewer joins, `TOP`) before you run it again; do not repeat the same heavy query.
- `NotFound` from `describe_table`: no table or view of that name is visible to the login. Check the name with
  `list_tables`.
- `AuditUnavailable`: the audit line could not be written, so no data was returned. Tell the user; the audit file
  or its folder needs attention.
- `RowsChanged` or `MultipleResultSets`: SQL Server reported a change or a second result set; the server rolled
  back and returned no data. Stop and tell the user; do not retry the same text.
- `ResultTooWide`: the column list alone is over the character cap. Select fewer columns.
- `InvalidArgument`: an argument is out of range or a name holds a hidden character. Fix the argument.
- `InternalError`: the server gives no details. Tell the user and do not repeat the call in a loop.

## Data and the audit file

- Every call, refused ones included, is a line in a hash-chained audit file on the user's machine, with the SQL
  text, the decision, the tables, the row count and the time, but never the row values. Literal values in the
  query's text (a name or an e-mail address in a `WHERE`) are in that text. Do not put passwords or other secrets
  in a query's text, and filter on keys rather than on personal values when you can.
- A `SqlError` message can quote a value from the query or from a row; treat it like a result.
- The rows you read go to the AI vendor with the conversation. Read only the columns the user's question needs,
  and ask before you read columns that hold personal data the question does not need.
- The user can check the file at any time with `mssql-readonly-mcp verify-audit <path>`.

## When the tools are missing

If the `mssql-readonly` tools are not available, the server did not start. The usual causes, in order:

1. The `mssql-readonly-mcp` program, or Node.js 18 or later (which runs the plugin's start file), is not on the
   `PATH` of the process that started Claude Code. The plugin's README has the install steps.
2. The connection string or the audit file path is not set: `/plugin configure` with the plugin's id in Claude
   Code (for example `mssql-readonly-mcp@mssql-readonly-mcp` when it came from the project's own marketplace)
   shows and sets the plugin's two options.
3. The login failed its permission check (exit code 3) or SQL Server could not be reached (exit code 4). The user
   can run `mssql-readonly-mcp check-login` in a terminal with `MSSQL_CONNECTION_STRING` set to see why.

Explain the cause to the user; do not ask them to paste the connection string or a password into the chat.

# Reference

This page covers configuration, commands and exit codes, the tools, result limits, error reasons and what the reader
sees at server level; the [README](../README.md) has the short start.

## Configuration

All settings are environment variables (`src/MssqlReadOnlyMcp/ServerConfig.cs`). A value that is not a whole number
or is out of range stops the start with exit code 2. A configuration error names the variable, never its value; an
audit file error names the audit file by its file name (`Program.cs`, `AuditLog.cs`). Connection pooling is left
at the client library's default (SECURITY.md, "Known limits", "Idle sessions stay open").

| Variable | Default | Range | Meaning |
| --- | --- | --- | --- |
| `MSSQL_CONNECTION_STRING` | none, required | | SQL Server connection string. The server adds `ApplicationIntent=ReadOnly` and `Application Name=mssql-readonly-mcp`. |
| `MSSQL_MCP_AUDIT_LOG` | none, required for the server | | Path of the audit file. Created with its folder if missing. Not needed by `check-login`. |
| `MSSQL_MCP_MAX_ROWS` | 200 | 1 to 5000 | Most rows `run_query` returns. |
| `MSSQL_MCP_MAX_CHARS` | 40000 | 1000 to 45000 | Most characters in one tool result. Claude Code saves a text result above 50,000 characters to a file instead of showing it, so the ceiling stays below that. |
| `MSSQL_MCP_TIMEOUT_SECONDS` | 30 | 1 to 300 | Time limit of every call, counted from its start: it covers opening the connection, the session settings and the query with its reading, and is also the command timeout of every command, the startup check included. A call the limit stops is answered and audited as `Timeout`, unless the client cancelled it first; a call that finished reading before the limit keeps its answer. Starting and rolling back the transaction, and cancelling and closing the reader, are synchronous and have no limit of their own; an `xml` value is read synchronously, and the limit is checked between those reads. Connecting is also bounded by the connection string's `Connect Timeout` (default 15 s). |
| `MSSQL_MCP_LOCK_TIMEOUT_MS` | 5000 | 0 to 300000 | Value of `SET LOCK_TIMEOUT` for every session. |

## Commands and exit codes

`src/MssqlReadOnlyMcp/Program.cs` and `ServerStartup.cs`.

| Command | What it does |
| --- | --- |
| (no argument) | Runs the stdio MCP server. Opens and verifies the audit file, runs the login check, writes a `startup` audit line, then serves. |
| `check-login` | Runs the login check only and prints the result. Needs `MSSQL_CONNECTION_STRING`. |
| `verify-audit <path>` | Verifies an audit file and prints the result. Needs no variable. |

| Exit code | Meaning |
| --- | --- |
| 0 | Success, including a normal server shutdown at end of input or Ctrl+C. |
| 1 | `verify-audit`: the file does not verify or cannot be read. |
| 2 | Configuration error (including a connection string the SQL Server client library rejects), audit file cannot be opened or does not verify, startup audit line not written, or wrong usage. |
| 3 | The login holds a permission outside the allowed set (see ["What `check-login` refuses"](login-check.md)). |
| 4 | SQL Server unreachable, or the check query failed (for example a wrong password). |
| 5 | An unexpected internal error. The stderr line names only the exception type. No test produces this code. |
| 6 | The server stopped while calls were still running. Only after Ctrl+C or a termination signal, never after end of input (see below). No test produces this code. |

Where output goes (`Program.cs`): `check-login` writes its result to stdout, the pass line or, with exit 3, the
header line and one line per finding; `verify-audit` writes its result to stdout with exit 0 and 1. Every other
failure, the server's own exit 3 included, is one line on stderr. The running server writes only MCP messages to
stdout, and to stderr one line per call, never the SQL text (`ToolService.cs`); its shape, an example, and the
shape of the warning described below:

```text
info: MssqlReadOnlyMcp.ToolService[0] <tool> <decision> <reason> <ms> ms
info: MssqlReadOnlyMcp.ToolService[0] run_query refused NotSelect 4 ms
warn: MssqlReadOnlyMcp.ToolService[0] <tool or unknown>: rejected before the tool ran (<exception type name>)
```

The `warn:` line is added for a call the SDK rejects by throwing (every rejection tested) before a tool runs; it
holds only the tool name and the exception type name, never the message or the arguments
(`ToolService.LogRejectedCall`; `StdioServerTests.cs`, `Calls_the_sdk_rejects_before_a_tool_runs_are_audited`).
For a connection string the client library rejects, the stderr line names only the exception type, for example
`mssql-readonly-mcp: configuration error: the SQL Server client library rejected MSSQL_CONNECTION_STRING (ArgumentException).`
(`StdioServerTests.cs`, `Connection_string_the_client_library_rejects_exits_2_with_one_line_from_the_type_name`).

The startup refusal line shown in [README step 3](../README.md#3-check-the-login) says `1 finding` for one, and
adds ` The startup audit line could not be written.` when that write failed too. In
both texts (that line and the `check-login` output) a finding names at most ten objects, then ` and <N> more`
(`PermissionCheck.cs`). A refusal lists rights outside the read-only set; a finding can be a right that only
widens reading, for example `UNMASK`. The pass line reports the outcome of the checks in ["What `check-login` refuses"](login-check.md), not a proof that the login cannot write.

At end of input the MCP SDK keeps the server running until every call in flight has ended on its own; each writes
its audit line, then the audit file closes and the process exits 0. Each call ends at its time limit
(`MSSQL_MCP_TIMEOUT_SECONDS`) plus the time to finish a synchronous read already under way (part of an `xml` value,
for example), cancel the command, close the reader and roll back; the time limit does not stop these steps (measured
on SQL Server 2022: 0.1 to 0.3 s past the limit), and starting the transaction is not stopped by it either (see
`MSSQL_MCP_TIMEOUT_SECONDS` above). No test holds a call past the drain window (`MSSQL_MCP_TIMEOUT_SECONDS` plus
10 seconds, below). Tests: `StdioServerTests.cs`
(`Client_closing_stdin_after_a_call_ends_the_process_with_exit_0_and_no_error_line`,
`Call_in_flight_when_stdin_closes_writes_its_audit_line_before_the_process_ends`,
`Call_stalled_at_connection_open_ends_at_the_time_limit_of_the_call_and_is_audited`), `CallLimitTests.cs`
(`Rows_that_arrive_slowly_end_at_the_time_limit_of_the_call_as_a_timeout`) and `AuditTests.cs`
(`Shutdown_drain_waits_for_a_call_in_flight_before_the_log_closes`). On Ctrl+C or a termination signal the host's
own stop runs first (its shutdown timeout, 30 s by default; not tested), then the server waits at most
`MSSQL_MCP_TIMEOUT_SECONDS` plus 10 seconds for calls still running; if any remain, it writes one stderr line with
their number and exits 6 (`Program.cs`). Those calls return no data and may have no audit line. A call can still be
running then only through a step the time limit does not stop (listed above); no test produces exit 6. Ctrl+C is not
covered by a test, so whether it cancels calls in flight (they would be audited as `Cancelled`) is not known here.

## Tools

All three tools are marked read-only and non-destructive (`src/MssqlReadOnlyMcp/McpTools.cs`).
`list_tables` and `describe_table` run fixed catalogue queries; their argument reaches SQL Server only as a
parameter value (`SchemaQueries.cs`). Only `run_query` takes SQL, and that SQL goes through the gate.

| Tool | Arguments | Returns |
| --- | --- | --- |
| `list_tables` | `schema` (optional, at most 128 characters); `name_filter` (optional, at most 256 characters): a substring of the name, or a `LIKE` pattern when it contains `%` | JSON: `tables` (schema, name, kind: table or view) for the user tables and views the login can see, at most 500 per call; `row_count` (items returned), `total_count` (every match), `truncated`, `truncated_reason` (`max_items`, `max_chars` or null). |
| `describe_table` | `table` (required), for example `dbo.Orders`; at most 1035 characters (four fully bracketed parts) | JSON: schema, name, kind, `columns` (name, SQL type, nullable, identity, computed), `primary_key`, `foreign_keys`, `column_count`, `foreign_key_count`, `truncated`, `truncated_reason` (`max_chars` or null). A name in another database, or one the login cannot see, is answered `NotFound`. |
| `run_query` | `sql` (required, one SELECT, at most 65,536 characters); `max_rows` (optional, 1 or more; a value above the server cap uses the cap) | JSON: `columns` (name, type), `rows`, `row_count`, `truncated`, `truncated_reason` (`max_rows`, `max_chars`, `long_value` or null). |

`run_query` opens a new connection, runs the session settings, then sends the SQL text exactly as received inside
a transaction that is always rolled back (`src/MssqlReadOnlyMcp/ToolService.cs`). If SQL Server returns a second
result set (`MultipleResultSets`) or reports rows changed (`RowsChanged`), the call fails with no data. These are
diagnostics, not the barrier: they run after the server has executed the text. The barrier is the gate, the
transaction that is always rolled back, and the login's permissions checked at startup. When the first result set
was truncated, the command is cancelled before either can be seen. The session's `SET NOCOUNT OFF` keeps row counts
visible when the instance's `user options` turn NOCOUNT on for new sessions. Tests (`ValueTypeTests.cs`, text sent
past the gate on purpose as a `db_datawriter` member):
`Second_result_set_fails_as_MultipleResultSets_with_no_data_and_the_transaction_is_rolled_back`,
`Write_by_a_login_that_can_write_runs_inside_the_call_is_rolled_back_and_fails_as_RowsChanged`,
`Write_fails_as_RowsChanged_even_when_the_instance_turns_nocount_on_for_new_sessions` (with
`Session_runs_with_nocount_off`), and
`Limit_a_write_whose_first_result_set_is_truncated_is_not_seen_and_its_rows_are_returned_rolled_back`.
The rollback does not undo a statistics object that SQL Server creates on its own while it runs a read (SECURITY.md,
"Two layers").

Every tool connection opens in the database the startup check ran in, even when the connection string names no
database and the login's default database changes later (`SqlGateway.PinnedTo`; `SchemaToolTests.cs`,
`Tools_run_in_the_checked_database_when_the_connection_string_names_none`).

Name arguments (`schema`, `name_filter`, `table`) that are too long, or that contain one of the characters listed
below (invisible ones, and some the server may read differently), are refused as `InvalidArgument` before any
connection opens, and the name is not recorded. Refused: control, format, line and paragraph separator characters,
space characters other than U+0020, U+FFFD, private-use, unassigned and noncharacter code points, and unpaired
surrogates (`ToolService.FindHiddenCharacter`). For example, SQL Server resolves a name with U+200D or U+FEFF to
the object without that character (`SchemaToolTests.cs`,
`Name_with_a_zero_width_joiner_is_refused_although_sql_server_would_resolve_it`,
`Name_with_a_character_the_gate_refuses_outside_literals_is_an_invalid_argument`). Length limits on tool arguments
are counted in UTF-16 code units.
Full-width letters are not refused, and SQL Server may read one as the ASCII letter:
`describe_table` with `dbo.ＥxampleCustomers` (U+FF25, FULLWIDTH LATIN CAPITAL LETTER E) described
`dbo.ExampleCustomers`, and the audit line's `tables` held the resolved `dbo.ExampleCustomers` (measured on
SQL Server 2022 with the image's default collation, which is width-insensitive; no test). The object is one the
login may see under its ASCII name anyway. `list_tables` arguments with such letters were not measured.

## Result limits

From `src/MssqlReadOnlyMcp/ResultPayload.cs` and `src/MssqlReadOnlyMcp.Gate/GateTypes.cs`:

- Rows: at most `MSSQL_MCP_MAX_ROWS`, or the caller's lower `max_rows`. The server stops reading and cancels the
  command; it does not rewrite the SQL to add `TOP`.
- Characters: the whole `run_query` result stays within `MSSQL_MCP_MAX_CHARS` whatever its ending, `long_value`
  included (`Long_value_ending_never_passes_the_character_cap`); rows that would pass it are left out and
  `truncated_reason` is `max_chars`.
- Cells: a text value longer than 2000 characters is cut and ends with `…[cut: value longer than 2000 characters]`.
  An `xml` value is returned as text and cut the same way (`ValueTypeTests.cs`).
- Binary values are returned as `<binary N bytes>`.
- Long values: a text or xml value over 1,048,576 characters, or a binary value over 1,048,576 bytes (shown as
  `<binary over 1048576 bytes>`), ends the result after its row with `truncated_reason` `long_value`. Later cells
  of that row read `<not read: an earlier value in the row is longer than 1048576>`. The server process never holds
  such a value whole, but the client library still reads the rest of it off the network when the reader closes,
  and discards it (`LargeValueTests.cs`, `One_very_large_cell_is_cut_and_never_held_but_the_rest_is_still_transferred`:
  a 200,000,000-character value; the test asserts that less than 64 MiB was allocated, and the 2026-11-12 run
  reported about 0.3 MB). `ValueTypeTests.cs` (`Binary_values_are_counted_up_to_one_mebibyte_only`) covers binary.
  This read limit holds for text and binary values. An xml value with one very large text node, or a large CLR
  value, may be read whole into memory by the client library before it is cut or named; not measured here. To see
  a long value, select `DATALENGTH(col)` or `SUBSTRING(col, 1, n)` instead of the column.
- CLR types are returned as `<unsupported value of type X>` and the call still succeeds. Tested with
  `hierarchyid`; `geometry` and `geography` take the same path in the source but have no test. To read such a column,
  convert it in the query, for example `CAST(Node AS nvarchar(4000))`, which returns `/1/2/` for a `hierarchyid`
  (`ValueTypeTests.cs`, `Hierarchyid_column_is_summarised_and_its_text_form_is_readable_by_cast`). The returned
  placeholder does not say this; it names only the type.
- A `decimal` too large for .NET is returned as its exact text.
- SQL text longer than 65,536 characters is refused (`TooLong`) without being parsed, and its text is not written to
  the audit line.
- `list_tables` and `describe_table` keep their answer within `MSSQL_MCP_MAX_CHARS` as well; items that would pass it
  are left out and `truncated_reason` is `max_chars` (`ErpScaleTests.cs`, `SchemaToolTests.cs`).
- Time: `MSSQL_MCP_TIMEOUT_SECONDS` per call, and per command; `MSSQL_MCP_LOCK_TIMEOUT_MS` per lock wait.

## Error reasons

A failed call comes back over stdio as a tool result with `isError: true` whose text starts with the reason word,
once, with no SDK prefix (`McpTools.cs`, `CallToolAuditFilter.cs`). The same word is the audit line's `reason`
(`src/MssqlReadOnlyMcp/ToolErrors.cs`, `src/MssqlReadOnlyMcp.Gate/GateTypes.cs`). `StdioServerTests.cs`
(`Every_failed_call_reaches_the_client_as_an_error_whose_text_starts_with_its_reason_word`) checks these texts (for
`SqlError` up to `line 1: `; the rest is SQL Server's message):

```text
NotSelect: refused by the read-only gate; DeleteStatement at line 1, column 1
InvalidArgument: max_rows must be 1 or more.
SqlError: SQL Server error 245, class 16, line 1: Conversion failed when converting the nvarchar value 'abc' to data type int.
InvalidRequest: the call was rejected before the tool ran (an argument is missing or has the wrong type); no data is returned.
```

An unknown tool name is not a tool result: the client gets the SDK's JSON-RPC error (`Unknown tool: 'no_such_tool'`).

Gate refusals, text `<reason>: refused by the read-only gate; <detail>`, or with no detail
`<reason>: refused by the read-only gate.` The detail names a node or token type, a line and a column; it never
repeats the SQL text.

| Reason | Meaning |
| --- | --- |
| `Empty` | No SQL text. |
| `TooLong` | Longer than 65,536 characters. |
| `ParseError` | Not valid T-SQL. |
| `NotSingleBatch` | A `GO` that the parser reads as a batch separator (the tested rows put it on its own line), or more than one batch (see [docs/sql-gate.md](sql-gate.md#checks-on-the-text-and-the-tokens)). |
| `NotSingleStatement` | Zero or more than one statement. |
| `NotSelect` | The statement is not a plain SELECT. |
| `SelectInto` | `SELECT ... INTO`. |
| `VariableAssignment` | `SELECT @v = ...`. |
| `DisallowedSyntax` | A syntax node, function name or character that is not on the allowlist. |
| `DisallowedTableHint` | A table hint other than `NOLOCK` or `READUNCOMMITTED`. |
| `DisallowedQueryHint` | An `OPTION (...)` clause or a join hint. |
| `LinkedServerName` | A four-part name. |
| `CrossDatabaseName` | A three-part name for a database other than the connected one. |
| `TooDeep` | More than 64 nested parentheses, or a tree too deep to check. |

Other errors, text `<reason>: ...`:

| Reason | Meaning |
| --- | --- |
| `Timeout` | The call's time limit or the command timeout passed. The text names SQL Server error -2 in both cases, even when SQL Server raised no error (a connection that did not open in time), and also when a server error arrived after the limit. |
| `LockTimeout` | A lock wait passed the lock timeout (error 1222). |
| `DeadlockVictim` | SQL Server chose this query as the deadlock victim (error 1205). |
| `PermissionDenied` | SQL Server refused the login (error 229). |
| `SqlError` | Any other SQL Server error. |
| `NotFound` | `describe_table` found no table or view of that name. |
| `InvalidArgument` | A missing or invalid argument, for example `max_rows` below 1, a name argument that is too long, or a name with a hidden character. |
| `InvalidRequest` | The MCP SDK rejected the call before a tool ran: a missing or mistyped argument (text above), or an unknown tool name (audited only; the client gets the JSON-RPC error), or a request cancelled before the tool ran (audited only). The audit line has tool `unknown` for an unknown tool name and records no argument (`CallToolAuditFilter.cs`; `StdioServerTests.cs`, `Calls_the_sdk_rejects_before_a_tool_runs_are_audited`). A request whose parameters the SDK cannot deserialize (a lone surrogate such as `\ud800` anywhere in `params` other than the 2026-07-28 `_meta` keys named in ["Requests with no audit line"](audit-log.md#requests-with-no-audit-line), `arguments` that is not an object, no tool name) is audited the same way, with the tool name when it can be read and `unknown` otherwise, before the client gets the SDK's JSON-RPC error `-32603 "An error occurred."` (`CallToolAuditFilter.cs`, `WrapMessages`; `StdioServerTests.cs`, `Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`). |
| `MultipleResultSets` | SQL Server returned more than one result set; no data is returned. |
| `RowsChanged` | SQL Server reported rows changed by the text; the change was rolled back and no data is returned. |
| `ResultTooWide` | The column list alone is larger than the character cap. |
| `Cancelled` | The client cancelled the call. |
| `AuditUnavailable` | The audit line could not be written, so no data is returned. |
| `InternalError` | Anything else. No details are returned. |

A reason from a SQL Server error reads `<reason>: SQL Server error <number>, class <class>, line <line>`; for classes
11 to 16 and a positive error number, `: <server message>` follows (`ToolErrors.cs`). Such a message goes to the
assistant like a result, and can name objects or quote a value from the query or from a row (the `SqlError`
example above).

Such errors also show which tables exist. `list_tables`, `describe_table` and `sys.objects` do not show a table the
login has no grant on (`SchemaToolTests.cs`, `Table_without_a_grant_is_not_found_not_listed_and_not_in_sys_objects`),
nor does `INFORMATION_SCHEMA` (measured on SQL Server 2019, 2022 and 2025; no test). A `run_query` that names the
table does show it. SQL Server answers error 229
(`PermissionDenied`, "The SELECT permission was denied on the object ...") for an existing table without a grant,
208 (`SqlError`, "Invalid object name ...") for a missing one, and, on the table without a grant, 207 ("Invalid
column name ...") for a column it does not have and 229 for one it has. So the assistant can learn that such a
table exists and test its column names; it gets no row data. Measured on SQL Server 2022 with `run_query` as a
reader without a grant on the table; no test. The server does not map these errors to one answer.

## What the reader sees at server level

SQL Server gives `public` some server-level metadata, and the gate allows catalogue views by design. Measured with
`run_query` on SQL Server 2019, 2022 and 2025 as a login made by `scripts/create-readonly-login.sql` (no test,
except `sys.databases`), that login could read, among others:

- server configuration values (`sys.configurations`);
- the host operating system and distribution (`sys.dm_os_host_info`);
- the server's host name (`sys.servers`);
- every database name on the instance, also of databases it has no user in (`sys.databases`; `StdioServerTests.cs`,
  `Reader_sees_in_sys_databases_a_database_it_has_no_user_in`);
- the login names it may see (`sys.sql_logins`: `sa` and its own login);
- its own session row, including the client machine's host name (`sys.dm_exec_sessions`);
- the names of `xp_*` procedures.

These were denied to it: `sys.dm_exec_connections`, `sys.dm_os_sys_info`, `sys.dm_server_services` and
`sys.traces`. What reaches the assistant reaches the AI vendor. The server started with this login, so the login
check passed it: what it read comes from SQL Server's defaults for `public`. `DENY VIEW ANY DATABASE` and its
effect on the database list are in SECURITY.md, "Known limits"; no other change to these defaults was measured
with this server.

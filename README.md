# mssql-readonly-mcp

An MCP server that lets an AI assistant read one on-premises SQL Server database.
Its SQL tool accepts only text that passes an allowlist for one plain SELECT, and it will not start with a login
that fails its permission check (see "What `check-login` refuses" for what that check covers and what it does not).
It keeps a tamper-evident record (a hash-chained audit file) of every question asked.

Status: a sample, published as it is; not a supported product. The tests it passed are listed under
"Building and testing".

## What it promises

Each promise names the test files that check it. Test files are under `tests/`.

- **A write or DDL statement is refused before it reaches SQL Server.** The check is an allowlist over the
  ScriptDom syntax tree, not a keyword filter: a statement passes only when every node in its tree is on a
  hand-written list. Proven by `MssqlReadOnlyMcp.Gate.Tests/RegressionTableTests.cs` (allowed and refused rows,
  including the public bypass classes: stacked statements, `GO` batches, `EXEC` of a string, DML inside or after a
  CTE, conditional and nested comments, text hidden after a line comment), `StatementTypeTests.cs` (only one
  statement type is accepted, checked against every type in the ScriptDom assembly), and
  `MssqlReadOnlyMcp.IntegrationTests/GateRefusalTests.cs` (refused statements open no connection, create no
  command, and leave every table, the sequence and the object catalogue unchanged on a real SQL Server). On the
  server side, `ExtendedEventsTests.cs` traces the reader login with an Extended Events session: the refused set
  leaves no batch, RPC or login event, while an allowed query and a write sent past the gate on purpose do appear
  in the trace.
- **In every case tested, SQL Server ran the allowed text or refused it before running; in the lexer cases it ran,
  it read the first column name as the parser did.**
  `DifferentialTests.cs` sends every allowed row of the regression table to SQL Server as the reader; each must run
  or fail only on a missing name, except rows listed with the exact error the server gives.
  `ParserVersusServerLexerTests.cs` sends 335 lexer cases (invisible and separator characters in eight positions,
  comment and string forms, ODBC escapes): each is refused by the gate, or runs with one result set whose first
  column name is the name the parser saw, or is refused by the server. The test asserts the split exactly: 269
  refused by the gate, 61 allowed and read the same, 5 allowed and refused by the server. See SECURITY.md, "Where
  the parser and the server read text differently".
- **The server refuses to start with a login that fails its permission check.** At start it asks SQL Server about
  the login's rights on the server, the connected database and the classes listed in "What `check-login` refuses",
  and exits with code 3 on any finding. There is no override. Proven by `PermissionCheckTests.cs` and
  `StdioServerTests.cs` (`Started_with_sa_the_process_exits_3_with_one_stderr_line`).
- **No call returns data before its hash-chained audit line is written.** The line is appended after the
  SQL has run: if it cannot be written, the query has still run and the call returns an error and no data. For
  calls still running at an exit 6, see "Commands and exit codes". Proven
  by `MssqlReadOnlyMcp.IntegrationTests/AuditTests.cs` (one line per call plus one startup line; no data when the
  audit file fails), `StdioServerTests.cs`, and the `MssqlReadOnlyMcp.Audit.Tests` project (format, chain,
  verification, flush, single writer).
- **Results are capped by rows, characters and time.** Proven by `MssqlReadOnlyMcp.IntegrationTests/CapsTests.cs`
  (row cap, character cap, cell cut, command timeout, lock timeout).
- **A query waits a bounded time for locks and loses deadlocks against default-priority sessions.** Each session
  runs `SET LOCK_TIMEOUT` (default 5000 ms), `SET DEADLOCK_PRIORITY LOW`, `SET QUOTED_IDENTIFIER ON` and
  `SET NOCOUNT OFF` before the query (`src/MssqlReadOnlyMcp/SqlGateway.cs`).
  The lock timeout is proven by `CapsTests.cs` (`Exclusive_lock_held_elsewhere_returns_the_lock_timeout_reason_near_the_limit`).
  The deadlock priority is proven by `ValueTypeTests.cs`
  (`Deadlock_victim_is_reported_as_DeadlockVictim_and_the_next_call_works`): against a session at the default
  priority, the reader is the victim, and the next call works. Limit: while a query runs under the default READ
  COMMITTED level, its own shared locks can still make a writer wait, up to the command timeout.

## What it does not solve

Read this before you install it.

- **The rows the assistant reads leave your building.** They go to the AI vendor that runs the model, with the
  table and column names and any SQL Server error text. Read-only does not change that.
- **Personal data and cross-border transfer rules are the firm's decision.** In Turkey that is KVKK; other countries
  have their own rules. This project does not decide what may be sent.
- **The answer this project offers is the login.** `scripts/create-readonly-login.sql` creates a login that gets
  SELECT only on the tables and views the firm lists in the script. SQL Server shows that login only the user
  tables and views it holds a permission on, plus the names of objects that ship with SQL Server; a table without a
  grant is not found, not listed and not in `sys.objects` (`SchemaToolTests.cs`,
  `Table_without_a_grant_is_not_found_not_listed_and_not_in_sys_objects`).

More limits are in [SECURITY.md](SECURITY.md).

## Five-minute start

### 1. Prerequisites

- A .NET 10 SDK, 10.0.400 or later; `global.json` (`rollForward: latestFeature`) accepts a later 10.0 band, not 11.x.
- On Linux: the ICU libraries (for example the `libicu` package). The server turns globalization on because
  Microsoft.Data.SqlClient needs it.
- `sqlcmd`, to run the login script as a database administrator.
- The commands below are PowerShell 7 (`Read-Host -MaskInput` needs 7.1 or later).
- A SQL Server the machine can reach. The integration tests passed on 2026-11-12 on three Linux container images:
  `mcr.microsoft.com/mssql/server:2019-CU32-GDR13-ubuntu-20.04`, `2022-CU27-ubuntu-22.04` and
  `2025-CU9-GDR1-ubuntu-22.04`. Other versions and Windows installations were not tested.
- On SQL Server 2019 some allowed syntax is newer than the server: `IS [NOT] DISTINCT FROM`, the `WINDOW` clause,
  `TRIM(LEADING ...)`, `DATETRUNC`, `GREATEST` and `LEAST` pass the gate and fail on the server with a syntax error
  (156 or 102), or with error 195 (not a recognized built-in function: `DATETRUNC`, `GREATEST`, `LEAST`) (list
  `NewIn2022` in `tests/MssqlReadOnlyMcp.IntegrationTests/DifferentialTests.cs`). Nothing runs; the call returns
  `SqlError`.

Build it:

```powershell
git clone https://github.com/oguzhanpisgin/mssql-readonly-mcp.git
cd mssql-readonly-mcp
dotnet build -c Release
$dll = (Resolve-Path src/MssqlReadOnlyMcp/bin/Release/net10.0/mssql-readonly-mcp.dll).Path
```

The commands in this guide run from the repository root. The server is that DLL, started with `dotnet <path>` (on
Windows the build also writes `mssql-readonly-mcp.exe` beside it); the PowerShell steps use `$dll` from the last
line, so set it again in a new session. The JSON snippets cannot use a variable: they assume the clone is at
`C:/tools/mssql-readonly-mcp` and audit files under `C:/mcp-audit`. If yours differ, change every such path below
(the value of `$dll` is the DLL path to use).

### 2. Create the read-only login

Open `scripts/create-readonly-login.sql`. Its `GRANT SELECT ON OBJECT::...` lines are examples (a GRANT on an
object that does not exist stops the script): replace them with one line per table or view of yours the assistant
may read. Then run it as an administrator. Passwords come from environment variables, never from the file or the
command line. With Windows authentication (`-E`):

```powershell
$env:MCP_LOGIN = 'mcp_reader'
$env:MCP_DATABASE = 'ExampleDb'
$env:MCP_PASSWORD = Read-Host -MaskInput 'New password for mcp_reader'
sqlcmd -S sql.example.local -E -b -i scripts/create-readonly-login.sql
Remove-Item Env:MCP_PASSWORD
```

With a SQL Server administrator login (as the test does), after the same `MCP_` lines:

```powershell
$env:SQLCMDPASSWORD = Read-Host -MaskInput 'Password for the administrator login'
sqlcmd -S sql.example.local -U admin_login -b -i scripts/create-readonly-login.sql
Remove-Item Env:SQLCMDPASSWORD, Env:MCP_PASSWORD
```

Add `-C` only for a test server with a self-signed certificate (see step 3).

The script prints `Created login mcp_reader.` and `Created user mcp_reader in database ExampleDb.`, then lists the
permissions the user now holds and its role memberships (expected: none). It sets the login's default database to
`MCP_DATABASE`. A second run prints `Login mcp_reader exists; its password was not changed.` and reuses the user.
It stops with an error if the user belongs to another login or is in any role. `StdioServerTests.cs`
(`Setup_script_creates_a_login_that_passes_check_login_and_runs_twice`) runs this file unchanged in the test
container with `sqlcmd` from a Linux shell, then `check-login` (exit 0), then the file a second time. The PowerShell
lines above were not run by a test.

Do not add the login to `db_datareader`. See "What `check-login` refuses" below.

### 3. Check the login

Microsoft.Data.SqlClient encrypts the connection and checks the server certificate by default. An untrusted
certificate shows as exit code 4 with `SqlException number -2146893019` (seen on Windows with a self-signed
certificate). Install a trusted certificate, or, for a test server, add `TrustServerCertificate=True` to the
connection string (sqlcmd 18: `-C`) and accept that a machine in the middle is then not detected.

```powershell
$password = Read-Host -MaskInput 'Password for mcp_reader'
$env:MSSQL_CONNECTION_STRING = "Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=$password"
dotnet $dll check-login
```

Exit code 0 and `Login mcp_reader on database ExampleDb: passed, no permission to change data was found.` means you
can go on. Exit code 3 prints `Login mcp_reader on database ExampleDb: refused, <n> right(s) outside the read-only
set found:`, then each finding on its own line after two spaces, for example `  UNMASK on database ExampleDb`; fix
those grants and run it again (`Program.cs`). When the server itself refuses to start, it exits 3 with one stderr
line that points to this command (`ServerStartup.cs`):

```text
mssql-readonly-mcp: refusing to start: the login holds rights beyond read-only access (2 findings, first: <finding>). Run 'mssql-readonly-mcp check-login' for the full list.
```

It says `1 finding` for one, and adds ` The startup audit line could not be written.` when that write failed too. In
both texts a finding names at most ten objects, then ` and <N> more` (`PermissionCheck.cs`). A refusal lists rights
outside the read-only set; a finding can be a right that only widens reading, for example `UNMASK`. The pass line
reports the outcome of the checks in "What `check-login` refuses", not a proof that the login cannot write.

### 4. Register with Claude Code

Claude Code reads a project file `.mcp.json` and expands `${VAR}` from the environment it was started in. The
secret stays in the environment, not in the file and not on a command line. (`claude mcp add --env NAME=value`
would put the password on a command line; do not use it for the connection string.)

Set the two variables for your user. These lines are for Windows; on Linux, `export` both variables in the shell
that starts Claude Code, from a file only you can read.

```powershell
$password = Read-Host -MaskInput 'Password for mcp_reader'
[Environment]::SetEnvironmentVariable('MSSQL_CONNECTION_STRING', "Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=$password", 'User')
[Environment]::SetEnvironmentVariable('MSSQL_MCP_AUDIT_LOG', 'C:/mcp-audit/claude-code.jsonl', 'User')
```

A user variable reaches only processes started afterwards: quit and restart the application that launches Claude
Code (the terminal application or Claude Desktop), not only a tab. Until then `claude mcp list` warns about missing
environment variables and the server exits with code 2. Windows stores the variable in your registry hive
(`HKCU\Environment`) in plain text, password included, readable by any process running as you. To remove it:
`Remove-ItemProperty HKCU:\Environment -Name MSSQL_CONNECTION_STRING` (setting it to `$null` left an empty value).

`.mcp.json` at the project root:

```json
{
  "mcpServers": {
    "mssql-readonly": {
      "command": "dotnet",
      "args": ["C:/tools/mssql-readonly-mcp/src/MssqlReadOnlyMcp/bin/Release/net10.0/mssql-readonly-mcp.dll"],
      "env": {
        "MSSQL_CONNECTION_STRING": "${MSSQL_CONNECTION_STRING}",
        "MSSQL_MCP_AUDIT_LOG": "${MSSQL_MCP_AUDIT_LOG}"
      }
    }
  }
}
```

An entry with no `type` is a local stdio server. Source: <https://code.claude.com/docs/en/mcp>. A server from a
project `.mcp.json` shows as "Pending approval" in `claude mcp list` until you start `claude` once in that folder
and approve it; after that it is listed as connected.

The error number in step 3, the restart advice and exit code 2, the `$null` remark and "Pending approval" were seen
in a first run on 2026-11-12 on Windows with Claude Code; they are not covered by a test.

### 5. Register with Claude Desktop

The hand-edited `claude_desktop_config.json` form is documented on the MCP project's page
<https://modelcontextprotocol.io/docs/develop/connect-local-servers>, not on an Anthropic page. Anthropic's own
pages steer local servers to MCP Bundles (`.mcpb`); this project does not ship a bundle.

The file is `%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS. Open it from Settings > Developer >
Edit Config. Paths must be absolute; backslashes are doubled in JSON.

```json
{
  "mcpServers": {
    "mssql-readonly": {
      "command": "dotnet",
      "args": ["C:\\tools\\mssql-readonly-mcp\\src\\MssqlReadOnlyMcp\\bin\\Release\\net10.0\\mssql-readonly-mcp.dll"],
      "env": {
        "MSSQL_CONNECTION_STRING": "Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=EXAMPLE-ONLY",
        "MSSQL_MCP_AUDIT_LOG": "C:\\mcp-audit\\claude-desktop.jsonl"
      }
    }
  }
}
```

That page documents no `${VAR}` expansion for this file, so the password sits in it in plain text. Limit who can
read the file. Quit Claude Desktop completely and start it again after editing. The server's stderr is in
`%APPDATA%\Claude\logs\mcp-server-mssql-readonly.log`.

Each server process needs its own audit file. The file is locked by one writer at a time through a companion
`<audit file>.lock` beside it (for example `claude-code.jsonl.lock`), which stays after the server exits; a second
server on the same file exits with code 2.

### 6. First question

Ask: "Use the mssql-readonly tools. List the tables, describe the orders table, then show the ten most recent
orders with their customer names."

Then run `verify-audit` on the audit file (see "The audit log").

## Reference

### Configuration

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
| `MSSQL_MCP_TIMEOUT_SECONDS` | 30 | 1 to 300 | Command timeout of every command, the startup check included. Connecting uses the connection string's `Connect Timeout` (default 15 s). |
| `MSSQL_MCP_LOCK_TIMEOUT_MS` | 5000 | 0 to 300000 | Value of `SET LOCK_TIMEOUT` for every session. |

### Commands and exit codes

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
| 3 | The login holds a permission outside the allowed set (see "What `check-login` refuses"). |
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

At end of input the MCP SDK keeps the server running until every call in flight has ended on its own; each writes
its audit line, then the audit file closes and the process exits 0. Opening a call's connection is bounded by the
connect timeout, and the session settings and the query by the command timeout; the server sets no timeout of its
own for the rollback. Only the connect bound is exercised by a test. Tests: `StdioServerTests.cs`
(`Client_closing_stdin_after_a_call_ends_the_process_with_exit_0_and_no_error_line`,
`Call_in_flight_when_stdin_closes_writes_its_audit_line_before_the_process_ends`,
`Call_still_running_at_end_of_input_holds_the_process_past_the_drain_window_and_is_audited`) and `AuditTests.cs`
(`Shutdown_drain_waits_for_a_call_in_flight_before_the_log_closes`). On Ctrl+C or a termination signal the host's
own stop runs first (its shutdown timeout, 30 s by default; not tested), then the server waits at most
`MSSQL_MCP_TIMEOUT_SECONDS` plus 10 seconds for calls still running; if any remain, it writes one stderr line with
their number and exits 6 (`Program.cs`). Those calls return no data and may have no audit line. Ctrl+C is not
covered by a test, so whether it cancels calls in flight (they would be audited as `Cancelled`) is not known here.

### Tools

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

Every tool connection opens in the database the startup check ran in, even when the connection string names no
database and the login's default database changes later (`SqlGateway.PinnedTo`; `SchemaToolTests.cs`,
`Tools_run_in_the_checked_database_when_the_connection_string_names_none`).

Name arguments (`schema`, `name_filter`, `table`) that are too long, or that contain a character that is invisible
or that the server may read differently, are refused as `InvalidArgument` before any connection opens, and the
name is not recorded. Refused: control, format, line and paragraph separator characters, space characters other
than U+0020, U+FFFD, private-use, unassigned and noncharacter code points, and unpaired surrogates
(`ToolService.FindHiddenCharacter`). For example, SQL Server resolves a name with U+200D or U+FEFF to the object
without that character (`SchemaToolTests.cs`, `Name_with_a_zero_width_joiner_is_refused_although_sql_server_would_resolve_it`,
`Name_with_a_character_the_gate_refuses_outside_literals_is_an_invalid_argument`). Length limits on tool arguments
are counted in UTF-16 code units.

### Result limits

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
- Time: `MSSQL_MCP_TIMEOUT_SECONDS` per command, `MSSQL_MCP_LOCK_TIMEOUT_MS` per lock wait.

### Error reasons

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
| `NotSingleBatch` | A `GO` separator or more than one batch. |
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
| `Timeout` | The command timeout passed (SQL Server error -2). |
| `LockTimeout` | A lock wait passed the lock timeout (error 1222). |
| `DeadlockVictim` | SQL Server chose this query as the deadlock victim (error 1205). |
| `PermissionDenied` | SQL Server refused the login (error 229). |
| `SqlError` | Any other SQL Server error. |
| `NotFound` | `describe_table` found no table or view of that name. |
| `InvalidArgument` | A missing or invalid argument, for example `max_rows` below 1, a name argument that is too long, or a name with a hidden character. |
| `InvalidRequest` | The MCP SDK rejected the call before a tool ran: a missing or mistyped argument (text above), or an unknown tool name (audited only; the client gets the JSON-RPC error), or a request cancelled before the tool ran (audited only). The audit line has tool `unknown` for an unknown tool name and records no argument (`CallToolAuditFilter.cs`; `StdioServerTests.cs`, `Calls_the_sdk_rejects_before_a_tool_runs_are_audited`). |
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

## What SQL is allowed

`run_query` accepts one plain SELECT statement built only from these forms (node types listed in
`src/MssqlReadOnlyMcp.Gate/Allowlist.cs`, field `NodeTypes`: 84 types at the time of writing; recount with the
number of `typeof(` lines in that list):

- `SELECT` with `DISTINCT`, `TOP (n)` (also `PERCENT` and `WITH TIES`), `WHERE`, `GROUP BY` (also `ROLLUP`, `CUBE`,
  `GROUPING SETS`), `HAVING`, `ORDER BY`, `OFFSET ... FETCH`, and the `WINDOW` clause.
- `WITH` common table expressions, recursive ones too.
- `UNION`, `EXCEPT`, `INTERSECT`, and parenthesised queries.
- Joins: `INNER`, `LEFT`, `RIGHT`, `FULL`, `CROSS JOIN`, `CROSS APPLY`, `OUTER APPLY`; derived tables;
  `(VALUES ...) AS v(...)`; `PIVOT` and `UNPIVOT` with an aggregate from the list below.
- Subqueries, `CASE`, `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT`, `COALESCE`, `NULLIF`, `IIF`, `LEFT`, `RIGHT`,
  `CURRENT_TIMESTAMP`, `AT TIME ZONE`, window functions with `OVER (PARTITION BY ... ORDER BY ... ROWS/RANGE ...)`
  and `WITHIN GROUP`.
- Predicates: comparisons, `AND`, `OR`, `NOT`, `BETWEEN`, `IN`, `LIKE`, `IS [NOT] NULL`, `EXISTS`,
  `= ANY / ALL / SOME (subquery)`, `IS [NOT] DISTINCT FROM`.
- Table hints `NOLOCK` and `READUNCOMMITTED` only.
- One-part and two-part names; three-part names only when the database part is exactly the connected database
  (same case).

Function names allowed in a function call (`Allowlist.cs`, field `FunctionNames`: 109 names at the time of writing,
compared ignoring case):

- Aggregates: `COUNT`, `COUNT_BIG`, `SUM`, `AVG`, `MIN`, `MAX`, `STDEV`, `STDEVP`, `VAR`, `VARP`, `STRING_AGG`,
  `GROUPING`, `GROUPING_ID`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`.
- Ranking and window: `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `NTILE`, `LAG`, `LEAD`, `FIRST_VALUE`, `LAST_VALUE`,
  `PERCENT_RANK`, `CUME_DIST`, `PERCENTILE_CONT`, `PERCENTILE_DISC`.
- String: `LEN`, `DATALENGTH`, `SUBSTRING`, `LTRIM`, `RTRIM`, `TRIM`, `UPPER`, `LOWER`, `REPLACE`, `REPLICATE`,
  `REVERSE`, `STUFF`, `CHARINDEX`, `PATINDEX`, `CONCAT`, `CONCAT_WS`, `FORMAT`, `STR`, `SPACE`, `QUOTENAME`, `ASCII`,
  `CHAR`, `NCHAR`, `UNICODE`, `TRANSLATE`, `STRING_ESCAPE`.
- Date and time: `GETDATE`, `GETUTCDATE`, `SYSDATETIME`, `SYSUTCDATETIME`, `SYSDATETIMEOFFSET`, `DATEADD`,
  `DATEDIFF`, `DATEDIFF_BIG`, `DATEPART`, `DATENAME`, `DATETRUNC`, `DATEFROMPARTS`, `DATETIMEFROMPARTS`,
  `DATETIME2FROMPARTS`, `TIMEFROMPARTS`, `EOMONTH`, `YEAR`, `MONTH`, `DAY`, `ISDATE`, `SWITCHOFFSET`,
  `TODATETIMEOFFSET`.
- Math: `ABS`, `ROUND`, `CEILING`, `FLOOR`, `SIGN`, `POWER`, `SQRT`, `SQUARE`, `EXP`, `LOG`, `LOG10`, `PI`, `SIN`,
  `COS`, `TAN`, `COT`, `ASIN`, `ACOS`, `ATAN`, `ATN2`, `DEGREES`, `RADIANS`.
- Null handling and choice: `ISNULL`, `ISNUMERIC`, `CHOOSE`, `GREATEST`, `LEAST`.
- JSON readers: `ISJSON`, `JSON_VALUE`, `JSON_QUERY`.
- Hashing and identifiers: `CHECKSUM`, `BINARY_CHECKSUM`, `HASHBYTES`, `NEWID`.

Anything not listed is refused by design, including a built-in that a later SQL Server version adds. That covers:

- user-defined functions (`dbo.fn(...)`) and table-valued functions, also system ones such as `sys.fn_get_audit_file`;
- variables (`@v`, `@@VERSION`), table variables and temporary tables (`#t`);
- table hints other than `NOLOCK` and `READUNCOMMITTED`, join hints and `OPTION (...)`;
- cross-database and linked-server names, `OPENQUERY`, `OPENROWSET`, `OPENDATASOURCE`, `OPENJSON`, `OPENXML`;
- `FOR XML`, `FOR JSON`, `WITH XMLNAMESPACES`, `NEXT VALUE FOR`;
- metadata and identity functions (`OBJECT_ID`, `DB_NAME`, `CURRENT_USER`, `SESSION_USER`, `SYSTEM_USER`, `USER`
  and the rest), `PARSE`, the JSON builders, and `CURRENT_DATE`.

If you need a function that is not listed, open an issue with the query you wanted to run. A name is added only
after it is checked not to reach outside the query or expose metadata or security material.

### Checks on the text and the tokens

These checks can only refuse; they run before or beside the syntax tree (`src/MssqlReadOnlyMcp.Gate/ReadOnlyGate.cs`).
Each refusal is `DisallowedSyntax` unless named otherwise.

- Before parsing, anywhere in the text (also inside literals and comments): text that is not well-formed UTF-16
  (an unpaired surrogate), and the 66 Unicode noncharacters (U+FDD0 to U+FDEF, and the last two code points of
  every plane).
- ODBC escape tokens outside literals, quoted names and comments: `{fn ...}`, `{d ...}`, `{oj ...}` and any other
  brace, and `--(*`. So `LIKE 'a\_b' {escape '\'}` is refused, while `LIKE 'a\_b' ESCAPE '\'` is allowed. A brace
  inside a string literal or a comment is allowed.
- Outside literals and comments: Unicode space characters other than U+0020, line and paragraph separators, format
  characters (for example U+200B, U+200D, U+FEFF), control characters other than TAB, CR and LF, U+FFFD, private-use
  and unassigned code points.
- Inside bracketed and double-quoted names: the same space, separator, format and control characters.
- A line comment (`--`) that contains a control character other than TAB, or a line or paragraph separator.
- A `GO` token anywhere (`NotSingleBatch`); more than 64 nested parentheses (`TooDeep`).

The gate parses with `TSql170Parser` (`ReadOnlyGate.cs`), which the package documents as the parser for version
17.0; the package also ships `TSql180Parser`, which the gate does not use. `StatementTypeTests.cs`
(`ScriptDom_is_at_least_the_version_with_the_U_FFFF_end_of_input_fix`) asserts that the loaded ScriptDom file is
version 18.0.140.0 or later (NuGet package 180.140.0); earlier versions stopped reading at U+FFFF.

The regression table (`tests/MssqlReadOnlyMcp.Gate.Tests/RegressionTableTests.cs`) has 71 allowed and 247 refused
rows at the time of writing (count the `Ok(` and `No(` rows).

### What `check-login` refuses

The check runs one query (`src/MssqlReadOnlyMcp/PermissionCheck.cs`) and refuses the login when it finds any of:

- an effective server permission other than `CONNECT SQL` and `VIEW ANY DATABASE`;
- an effective permission on the connection's database other than `CONNECT`,
  `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` and `VIEW ANY COLUMN MASTER KEY DEFINITION`;
- membership of any fixed server role other than `public`, or of `db_owner`, `db_datawriter`, `db_ddladmin`,
  `db_securityadmin`, `db_accessadmin` or `db_backupoperator`;
- below database scope, user objects only (not `is_ms_shipped`): write, alter, control and ownership rights on
  tables, views and inline functions, column `UPDATE`, sequences, procedures and functions (also `EXECUTE`),
  schemas (also `CREATE ...`), roles, and synonyms (also `EXECUTE`); `IMPERSONATE` on users and logins.

The allowed sets are what a login made by `scripts/create-readonly-login.sql` holds on SQL Server 2019, 2022 and
2025 (code comment in `PermissionCheck.cs`). Any other permission is a finding by its own name, so one that a later
SQL Server version adds is refused without a code change. "Effective" (`sys.fn_my_permissions`) includes rights
that roles and higher scopes imply, and rights that only widen reading: database-wide `SELECT`, `UNMASK`,
`CONNECT ANY DATABASE` and `SELECT ALL USER SECURABLES` are refused too, as are read-only database grants such as
`VIEW DEFINITION`, `SHOWPLAN` and `VIEW DATABASE STATE`, so a platform that grants such extras by default will see
its logins refused. The query counts a NULL answer as a finding (`ISNULL(state, 1) = 1`, printed as
`could not be checked`), never a pass; its test (`Null_answer_is_a_finding_named_could_not_be_checked`) checks
only how the finding is printed, and no test produces a NULL answer.
`PermissionCheckTests.cs` covers the reader passing and, among others, `sa`, `db_owner`, `db_datawriter`, single
table and column `UPDATE`, `IMPERSONATE`, `ALTER TRACE` (with the `SHOWPLAN` it implies), `BACKUP DATABASE`,
`UNMASK`, `TAKE OWNERSHIP` on a role and `UPDATE` on a synonym (`UPDATE on synonym [dbo].[CustomerAlias]`).
`CONNECT ANY DATABASE`, `SELECT ALL USER SECURABLES`, `VIEW DEFINITION` and `VIEW DATABASE STATE` follow from the
rule and have no test of their own. A synonym counts because, with an unbroken ownership chain, `UPDATE` granted on
the synonym alone changes the table it names, measured on 2019, 2022 and 2025 (`ValueTypeTests.cs`,
`Update_grant_on_a_synonym_alone_lets_the_login_change_the_table_it_names`; inside this server the call still fails
as `RowsChanged` and is rolled back). The check reports such a grant even when the synonym's target is in another
database, on a linked server or behind a broken ownership chain, where SQL Server also asks for the right on the
target (not tested here); the login is then refused although it may not be able to write through that synonym.

A `db_datareader` member is refused: the role gives `SELECT` on the whole database, and SQL Server makes the role
the owner of schema `db_datareader`, so its members can create objects there (`CONTROL on schema [db_datareader]`,
`CREATE SEQUENCE on schema [db_datareader]`, `SELECT on database ...`). `PermissionCheckTests.cs` shows that such a
member really can create a sequence in that schema. Grant SELECT on named tables, views or schemas instead, as
`scripts/create-readonly-login.sql` does.

`check-login` inspects the server and the connection's database only. Not checked: other databases (`msdb`
included), linked-server login mappings, application roles, cross-database ownership chains, the object and
schema `SELECT` grants you make, what views and synonyms read, and object-level grants or ownership on Service
Broker queues (`RECEIVE`) and services (`SEND`), assemblies, certificates, asymmetric and symmetric keys, types,
XML schema collections and full-text catalogs. Use a login created by `scripts/create-readonly-login.sql`, which
grants none of these, and check what `public` holds there. See SECURITY.md, "Known limits".

## The audit log

One JSON object per line, UTF-8, ending in `\n` (`src/MssqlReadOnlyMcp.Audit/AuditFormat.cs`). The fields always
appear in this order:

| Field | Value |
| --- | --- |
| `seq` | 1, 2, 3, ... with no gaps. |
| `ts` | UTC time from the host clock, `yyyy-MM-ddTHH:mm:ss.fffZ`. |
| `session` | A new id each time the server starts. |
| `tool` | `startup`, `list_tables`, `describe_table`, `run_query`, or `unknown` for a call to a tool name the server does not have. |
| `decision` | `allowed`, `refused` or `error`. |
| `reason` | `Allowed`, or a reason word from the tables above (`LoginCanWrite` for a start refused on a login-check finding). |
| `sql` | The SQL text as received for `run_query`; null for the other tools, for a `TooLong` refusal, and for a call the SDK rejected (`InvalidRequest`). An unpaired surrogate is written as U+FFFD (`AuditLog.cs`); the gate refuses such text, so only refused calls are affected, and `sql_sha256` is the same for both forms. |
| `sql_sha256` | SHA-256 of `sql`, or null. |
| `tables` | For `run_query`, the table and view names the SQL refers to, as written. For `describe_table`, the resolved `schema.name` when the lookup succeeded, empty when the argument itself was refused (missing, too long, or with a hidden character), otherwise the name as written. Empty for `list_tables`. |
| `rows` | Rows or items returned, or null when no data was returned. |
| `truncated` | Whether a cap cut the result. |
| `duration_ms` | Time the call took. |
| `result_sha256` | SHA-256 of the text returned to the client, or null. |
| `prev_hash` | `hash` of the previous line; 64 zeros on the first line. |
| `hash` | SHA-256 of this line written without its `hash` field. |

The line is written after the SQL has run and flushed to disk before the result goes back to the client
(`AuditTests.cs`, `Audit_failure_returns_an_error_and_no_row_data`: the call opened its connection before its line
failed). The SQL text is stored in plain text, literals included. The connection string and passwords are never
written.

`sql` and `sql_sha256` are the authoritative record of what was asked; `tables` is a reading aid. It lists names as
written, and SQL Server may resolve a differently spelled name to the same object (measured for U+200D and U+FEFF,
which the gate now refuses inside names; full-width letters and some other characters were not measured).

What is left out of a line, so that it stays small and never names something other than what was read:

- a `run_query` text over 65,536 characters (`TooLong`): `sql` and `sql_sha256` are null (`AuditTests.cs`);
- `list_tables` never records its arguments;
- a `describe_table` name that is too long or holds a hidden character: `tables` is empty (`SchemaToolTests.cs`);
- a call the SDK rejected: no argument is recorded.

A `run_query` text the gate allowed and then refused for `max_rows` below 1 is recorded as usual.

Verify a file:

```powershell
dotnet $dll verify-audit C:/mcp-audit/claude-code.jsonl
```

It prints `Audit file <name>: <N> lines verified.` and exits 0, or names the first bad line and a reason word
(for example `HashMismatch`, `ChainBreak`, `SequenceGap`, `NotCanonical`, `PartialLine`) and exits 1. The server
also verifies the whole file each time it starts, so start time grows with the file, and will not start on a file
that fails.

Limits, quoted from the `AuditLog` class documentation (`src/MssqlReadOnlyMcp.Audit/AuditLog.cs`):

> Honest limits: the hash chain shows edits, deletions and reordering of lines inside the file. It does not show
> removal of the newest lines (tail truncation) or a full rewrite of the file by someone who can write the file,
> unless `LastHash` is copied somewhere they cannot write.
>
> - The hash has no key, so anyone who can write the file, even with append-only rights, can add lines that verify,
>   and can recompute the chain from any line onward.
> - The SQL text, literals included, is stored in plain text and is readable by anyone who can read the file.
> - `ts` comes from the host clock; the verifier checks its format, not its order.
> - On Linux the single-writer lock is an advisory `flock` and binds only cooperating processes, that is, processes
>   that take the same lock.
> - On Linux, flushing a newly created log file to disk does not persist its directory entry, so a crash shortly
>   after the file is created can lose the file; .NET offers no call to flush the directory.
> - Verifying a file that is being written can report a partial last line.

The server has no command that copies the last hash elsewhere. To detect tail truncation or a rewrite, copy the
`hash` of the last line to a place the server's user cannot write, on a schedule you choose.

## Prior art, and when to prefer it

Facts below are from public repositories and documentation read on 2026-11-11.

- **Microsoft's SQL MCP Server (part of Data API builder).** <https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/overview>.
  It has no free-form SQL tool by design: typed tools over entities you configure, with per-role permissions, and
  SQL generated from structured parameters. Prefer it when you can describe the data the assistant needs as a set
  of entities and you do not want the assistant to write SQL at all. Note that it is a create, read, update and
  delete tool: writes are switched off by configuration, not absent.
- **alyiox/mcp-mssql.** <https://github.com/alyiox/mcp-mssql>. C#, MIT, stdio. It parses with ScriptDom, requires
  exactly one SELECT in one batch, and rejects a list of unsafe node types; it has row and time caps with ceilings.
  It is the closest project to this one. Prefer it if its features fit your case, if you want write tools that a
  profile can switch on, or if you want a project with a release history (v1.8.0 on 2026-10-26).

This project does not claim to be first. These points were not seen in the ten servers read on 2026-11-11 (a
statement about the files read, not proof that nobody does them):

- an allowlist of syntax node types and function names, instead of a list of forbidden ones;
- a startup check that refuses a login holding any right listed in "What `check-login` refuses";
- a hash-chained audit line with the SQL text, written and flushed before the result is returned, failing closed;
- tests that replay the publicly reported bypass classes against a real SQL Server container;
- a session lock timeout and low deadlock priority for reports.

## Try to break it

If you get a write, a schema change, a read outside the connected database, or a call with no audit line through
this server, please report it. Send this, as plain text:

```
Exact text sent (the run_query sql argument, or the tool and its arguments):
What you expected:
What happened (tool result text, exit code, anything that changed on the server):
SQL Server version (SELECT @@VERSION, run separately):
Server commit or version:
The audit line for the call (or "none"):
```

Where to send it: an issue at <https://github.com/oguzhanpisgin/mssql-readonly-mcp/issues>. For a problem that should not be public first, see
[SECURITY.md](SECURITY.md).

## Building and testing

Run every command from the repository root, so `global.json` applies.

```powershell
dotnet build -c Release
dotnet test tests/MssqlReadOnlyMcp.Gate.Tests -c Release
dotnet test tests/MssqlReadOnlyMcp.Audit.Tests -c Release
dotnet test tests/MssqlReadOnlyMcp.IntegrationTests -c Release
```

- The integration tests need Docker: they start a SQL Server container with Testcontainers.
- Two audit tests need PowerShell 7 (`pwsh`) on `PATH`: they start a second process to test the file lock.
- Without Docker or `pwsh` these tests fail (the `pwsh` ones with a message that names `pwsh`). They never skip.
- `MSSQL_TEST_IMAGE` selects the SQL Server container image, for example
  `mcr.microsoft.com/mssql/server:2019-CU32-GDR13-ubuntu-20.04`. Without it the tests use
  `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` (`SqlServerFixture.cs`).
- Test counts at the time of writing: Gate 364, Audit 55, Integration 119 on each of the three images named under
  "Prerequisites".
- `ErpScaleTests.cs` creates a second database with 20,000 tables and asserts that the startup check passes there
  within the default connection timeout (15 s); it writes the time to its output. Measured before the check moved
  to the allowlist: about 1.1 s on SQL Server 2022 and 3.4 s on 2019 (2026-11-12).
- The SQL Server container images run on x86-64 Linux hosts only (Docker Desktop on Windows uses a Linux VM).
- `.github/workflows/ci.yml` runs the same steps on GitHub. It has not run yet.

## Licence

MIT. See [LICENSE](LICENSE).

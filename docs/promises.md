# What it promises

This page holds the full text of each promise that the [README](../README.md#what-it-promises) lists in short.

Each promise names the test files that check it. Test files are under `tests/`.

## Writes refused before SQL Server

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

## Allowed text on a real SQL Server

- **In every case tested, SQL Server ran the allowed text or refused it before running; in the lexer cases it ran,
  it read the first column name as the parser did.**
  `DifferentialTests.cs` sends every allowed row of the regression table to SQL Server as the reader; each must run
  or fail only on a missing name, except rows listed with the exact error the server gives.
  `ParserVersusServerLexerTests.cs` sends 335 lexer cases (invisible and separator characters in eight positions,
  comment and string forms, ODBC escapes): each is refused by the gate, or runs with one result set whose first
  column name is the name the parser saw, or is refused by the server. The test asserts the split exactly: 269
  refused by the gate, 61 allowed and read the same, 5 allowed and refused by the server. See SECURITY.md, "Where
  the parser and the server read text differently".

## Login check at startup

- **The server refuses to start with a login that fails its permission check.** At start it asks SQL Server about
  the login's rights on the server, the connected database and the classes listed in ["What `check-login` refuses"](login-check.md),
  and exits with code 3 on any finding. There is no override. Proven by `PermissionCheckTests.cs` and
  `StdioServerTests.cs` (`Started_with_sa_the_process_exits_3_with_one_stderr_line`).

## Audit line before data

- **No call returns data before its hash-chained audit line is written.** The line is appended after the
  SQL has run: if it cannot be written, the query has still run and the call returns an error and no data. For
  calls still running at an exit 6, see ["Commands and exit codes"](reference.md#commands-and-exit-codes). Proven
  by `MssqlReadOnlyMcp.IntegrationTests/AuditTests.cs` (one line per call plus one startup line; no data when the
  audit file fails), `StdioServerTests.cs`, and the `MssqlReadOnlyMcp.Audit.Tests` project (format, chain,
  verification, flush, single writer).

## Result caps

- **Results are capped by rows, characters and time.** Proven by `MssqlReadOnlyMcp.IntegrationTests/CapsTests.cs`
  (row cap, character cap, cell cut, time limit, lock timeout) and `CallLimitTests.cs` (time limit per call, also
  when rows arrive slowly; a row that cannot fit the character cap is not held whole in memory, but the cells read
  and the rest of the row still cross the network). The steps that have no time limit of their own are listed under
  `MSSQL_MCP_TIMEOUT_SECONDS` in ["Configuration"](reference.md#configuration).

## Lock timeout and deadlock priority

- **A query waits a bounded time for locks and loses deadlocks against default-priority sessions.** Each session
  runs `SET LOCK_TIMEOUT` (default 5000 ms), `SET DEADLOCK_PRIORITY LOW`, `SET QUOTED_IDENTIFIER ON` and
  `SET NOCOUNT OFF` before the query (`src/MssqlReadOnlyMcp/SqlGateway.cs`).
  The lock timeout is proven by `CapsTests.cs` (`Exclusive_lock_held_elsewhere_returns_the_lock_timeout_reason_near_the_limit`).
  The deadlock priority is proven by `ValueTypeTests.cs`
  (`Deadlock_victim_is_reported_as_DeadlockVictim_and_the_next_call_works`): against a session at the default
  priority, the reader is the victim, and the next call works. Limit: while a query runs under the default READ
  COMMITTED level, its own shared locks can still make a writer wait, up to the time limit of the call.

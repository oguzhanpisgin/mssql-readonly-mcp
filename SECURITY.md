# Security

## Threat model

The attacker is the assistant: a model that was prompt-injected (for example by text stored in a table it read) or
that simply makes a mistake. It can call the three tools with any arguments, as often as it likes. The goal is that
it cannot change the database, given the gate (one plain SELECT), the transaction that is always rolled back, and a
login that passes the permission check (see "What `check-login` refuses" in `docs/login-check.md` for the classes it
inspects and those it does not); that it cannot read outside the connected database; and that it cannot get data
back from a call before that call's audit line is written. Every `tools/call` request the MCP SDK passes to the
server's message filters gets exactly one audit line before its answer, also when the SDK cannot read its
parameters. Requests the SDK or its stdio transport rejects or drops before that point run no tool and have no line
(`docs/audit-log.md`, "Requests with no audit line"). The line is appended after the SQL has run, or, for a call
ended as `Timeout` or `Cancelled` at its cutoff, while its clean-up may still run at SQL Server: when it cannot
be written, the query has still run, and the call fails with no data. A call not yet answered when the server exits
with code 6 may have no audit line and returns no data.
Server-level metadata that SQL Server shows every login through `public` (among them the host name, the operating
system, the configuration and every database name) can be read, and is not counted as outside the connected
database (`docs/reference.md`, "What the reader sees at server level").

Out of scope: a database administrator, anyone who can change the server's binaries or its configuration, and anyone
who can write the audit file. They can do more than this server can stop.

## Two layers

1. **The gate** (`src/MssqlReadOnlyMcp.Gate`). Before anything is sent, the SQL text of `run_query` is parsed with
   ScriptDom. It must be exactly one plain SELECT, and every node in its syntax tree must be on a hand-written
   allowlist of node types and function names. Checks on the whole text and on the token stream can only refuse,
   never allow. A refused call opens no connection.
2. **The login's own permissions.** At start the server asks SQL Server what the login may do and refuses to start
   on any finding of the check described below (`src/MssqlReadOnlyMcp/PermissionCheck.cs`). So a write to the
   connected database's tables, views, sequences, schemas or synonyms that got past the gate would still be refused
   by SQL Server. The check asks the objects that ship with SQL Server (`is_ms_shipped`) in the connected database's
   `sys.objects` like any other. The system views, procedures and functions in schemas `sys` and
   `INFORMATION_SCHEMA` (listed in `sys.system_objects`) are not asked one by one; a grant on one is found from
   `sys.database_permissions`, except `public`'s `EXECUTE` in `master`, which a default installation holds on more
   than 1,600 of them. No other database (`tempdb` included) is inspected; only the gate stops writes through those
   and through a `public` grant of that kind. The check runs once, at start: rights granted afterwards to the login,
   its roles or `public` are not seen until the server restarts. With an availability group, whether the check and
   later calls reach different replicas is not tested (`docs/login-check.md`).

`run_query` also runs inside a transaction that is always rolled back. That is not counted as a layer: a rollback
does not undo every effect. For example, with `AUTO_CREATE_STATISTICS ON` (the database default) a read can make
SQL Server create a statistics object, which stays after the rollback: `run_query` with
`SELECT COUNT(*) FROM dbo.ExampleOrders WHERE CodeText = 'abc'` took the table's rows in `sys.stats` from 1 to 2
on SQL Server 2019, 2022 and 2025 (measured; no test). User data and table definitions were unchanged. After
the text has run, a second result set (`MultipleResultSets`) or a reported row change (`RowsChanged`) fails the call
with no data. These are diagnostics, not a barrier: the server has already executed the text, and when the first
result set was truncated the command is cancelled before either can be seen
(`ValueTypeTests`, `Limit_a_write_whose_first_result_set_is_truncated_is_not_seen_and_its_rows_are_returned_rolled_back`).

### What the login check covers

The check is an allowlist over the login's effective permissions at server and database scope
(`sys.fn_my_permissions`), plus a one-by-one question for write, alter, control and ownership rights below database
scope on every object of the asked types in the connected database's `sys.objects` (those marked as shipped, such
as `cdc` tables or objects marked with `sp_MS_marksystemobject`, included), on schemas, roles and application
roles, and on user-defined server roles at server scope, for four rights on every Service Broker queue (the
built-in ones included), and for every right but a few allowed ones on the sixteen other securable classes that
`docs/login-check.md` lists as checked (the built-in assembly `Microsoft.SqlServer.Types` included; an owner holds
`CONTROL`, so this question finds it). As a backstop, any right granted to the login, to a role or group it is in, or
to `public` on any securable of the connected database or the server, whatever its class, is a finding unless it is
`CONNECT` or `VIEW DEFINITION`, on a database securable also `SELECT`, `REFERENCES`, `VIEW CHANGE TRACKING`, `UNMASK`
or `EXECUTE` on a table type, an alias type or an XML schema collection, or, in `master`, `public`'s `EXECUTE` on a
system object. An allowed right held `WITH GRANT OPTION` is a finding too, `<right> WITH GRANT OPTION`, also for the
rights allowed on the database and the server. A NULL answer is a finding, never a pass
(`src/MssqlReadOnlyMcp/PermissionCheck.cs`).
Connected to `msdb`, a login with a user there is refused (measured for a reader with `SELECT` on schema `dbo`;
every user is in `public`), because `public` may execute shipped procedures and functions there (14 on SQL Server
2019, 15 on 2022 and 2025; `docs/login-check.md` lists them); a reader with a user and `SELECT` on schema `dbo` passes
in `master` and in a new user database. It inspects the server and the connection's database only. `ALTER` on a user
or a login, `ALTER` on an external model (SQL Server 2025) and `EXECUTE` on a system extended procedure in `master`
are found only by the backstop. `ALTER` on an endpoint, a route, a message type, a remote service binding, a search
property list or a database scoped credential and `TAKE OWNERSHIP` on a contract or a full-text stoplist are found by
the class question and by the backstop, and an owner of any of those eight classes by the class question, also when a
role the login is in is the owner (measured for a message type, a full-text stoplist and an endpoint, and for a route
owned by a role that the login's role is in; no act was run through a role). With each right, and as each owner,
the login ran a statement that the plain reader could not run (measured; `docs/login-check.md` names each statement
and what it changed, where a change was shown). With an allowed right held `WITH GRANT OPTION` the login granted it
to another principal, and the plain reader could not (measured at schema, object, column, database and server
scope).
A reader with `SELECT` on a schema that holds a temporal or ledger history table is refused:
on SQL Server 2019 it could insert into the temporal history table through `OUTPUT ... INTO` (measured); on 2022
and 2025 no insert that was tried ran. `docs/login-check.md` explains why and what to grant instead. The full lists of
what it refuses, what it allows on each class and why, and what it does not inspect, why synonyms are asked and where
that question over-reports, the limits of the class rules, and the platform note are in `docs/login-check.md`,
"What `check-login` refuses"; read them before you rely on a pass.

## What each test class proves

The attack tests, scans and code security review run before the first release, their results and what they did not
cover are in [docs/security-testing.md](docs/security-testing.md).

Gate (`tests/MssqlReadOnlyMcp.Gate.Tests`, no database needed):

- `RegressionTableTests`: each row is a SQL text, allowed or refused, with the expected reason (71 allowed and 247
  refused rows at the time of writing). The refused rows replay the publicly reported bypass classes (stacked
  statements, `GO`, `EXEC` of a string, DML in or after a CTE, conditional and nested comments, text hidden after a
  line comment, ODBC escapes, invisible and separator characters, noncharacters) and every statement family a
  read-only gate must refuse.
- `StatementTypeTests`: generated over the ScriptDom assembly; exactly one statement type is accepted, and a node
  type that a later ScriptDom version adds cannot pass silently. It also asserts that the loaded ScriptDom file is
  version 18.0.140.0 or later (NuGet 180.140.0).
- `TablesTests`, `LimitsTests` (file `TablesAndLimitsTests.cs`): the referenced-table list, deep nesting without a crash, the size ceiling, and that the
  gate never throws.
- `LeftOpenConnectionsGuardTests`: the referenced client library is Microsoft.Data.SqlClient 7.1.1, the version
  whose internals the server's fix for U-1 reads (`docs/reference.md`, "Commands and exit codes"); every internal the
  fix needs is found; the network handle resolves on both the native and the managed network type.

Audit (`tests/MssqlReadOnlyMcp.Audit.Tests`, no database needed):

- `AuditLogTests`: field order, hash, fail-closed on a failed write (the log stays poisoned), reopen continues the
  chain, a second writer is refused.
- `AuditVerifierTests`: changed, deleted, swapped, inserted, duplicated and malformed lines are found; tail
  truncation is not found, by design.
- `AuditCanonicalTests`: compares every byte of one line, built by hand in the test without the library's
  serialiser, with what the library writes, for a SQL text that holds unassigned, private-use, noncharacter,
  separator, control and Turkish characters. It also shows that a line written with another escaping (an escape
  where raw UTF-8 is canonical, upper-case hex, a raw U+2028) fails verification as `NotCanonical`. It runs on the
  .NET runtime of the test run only; no test runs the format on a second runtime.
- `AuditLockFlushAndLimitTests`: the file is flushed to disk before `Append` returns, the line length limit, and two
  tests that start a PowerShell 7 child process: while the child holds the `.lock` file, opening the log fails; while
  this process has the log open, the child cannot take the lock (nor, on Windows, open the log for writing) but can
  read the log.

Integration (`tests/MssqlReadOnlyMcp.IntegrationTests`, a real SQL Server in a container; passed on 2026-11-12 on
the 2019 CU32 GDR13, 2022 CU27 and 2025 CU9 GDR1 images; one test is not stable: U-1R under "Known limits"):

- `GateRefusalTests`: refused statements open no connection, create no command, and leave every table, the
  sequence, the object catalogue and the logins unchanged. A write sent past the gate on purpose is refused by
  SQL Server itself.
- `ExtendedEventsTests`: an Extended Events session filtered on the reader login. The refused set leaves no batch,
  RPC or login event from the reader; an allowed query and a write sent past the gate both appear in the trace.
- `DifferentialTests`: every allowed regression row runs on the engine as the reader, or fails only because a name
  does not exist. Listed exceptions, each with the exact error: three look-alike quote and full-width parenthesis
  rows the server refuses while parsing, and on SQL Server 2019 the rows that use syntax 2019 lacks
  (`IS DISTINCT FROM`, `WINDOW`, `TRIM(LEADING ...)`, `DATETRUNC`, `GREATEST`, `LEAST`).
- `ParserVersusServerLexerTests`: the 335 lexer cases described below, as a permanent test.
- `PermissionCheckTests`: the reader passes; a `db_datareader` member (also for `SELECT` on the database), single
  UPDATE and column UPDATE grants, `db_datawriter`, `db_owner`, `sa`, IMPERSONATE, ALTER ANY ROLE, `securityadmin`,
  EXECUTE on a procedure, UPDATE on a sequence, `db_denydatawriter`, ALTER TRACE, BACKUP DATABASE, UNMASK, TAKE
  OWNERSHIP on a role and UPDATE on a synonym (`UPDATE on synonym [dbo].[CustomerAlias]`) are refused, each named by
  its finding; one or more logins per class covered in "What `check-login` refuses" (`docs/login-check.md`) get their
  exact findings (a built-in queue and the built-in assembly included), a login holding only rights the check
  allows on those classes passes, and at least one act for each refused login, except the two that hold `SEND`
  through a role, shows its right at work while the reader cannot; rights on a procedure and a table marked with
  `sp_MS_marksystemobject`, on the change data capture table `cdc.ddl_history`, on a user-defined server role and on
  an application role are found exactly and shown at work; the rights found through the grant rows (named under
  "What the login check covers", a route also through a role and through a role in a role) get their exact findings
  and an act each while the reader cannot (for `xp_regwrite` the test shows only a different outcome: error 0 for
  the grantee, 229 for the reader; see `docs/login-check.md`); an owner of each of the eight classes named there, and
  a login holding an allowed right `WITH GRANT OPTION` at schema, object, column, database and server scope, directly
  and through a role, get their exact findings and an act each while the reader cannot; a member of a role that owns a
  schema, a message type, a full-text stoplist, an endpoint or a server role, or a route through a nested role, and a
  login in a database where `public` holds `SELECT` and `INSERT` `WITH GRANT OPTION`, get their exact findings (no
  act); a login that sees other principals' grants on a user, an application role and a login passes; a plain
  reader passes in `master` and in a database with change data capture enabled at database level
  (`sys.sp_cdc_enable_db`, no table enabled for capture; `docs/login-check.md` gives a measurement with captured
  tables), and is refused in `msdb` with the exact list `public` may execute there; how a NULL answer's finding is
  printed (the NULL path in the query has no test).
- `AuditTests`: one audit line per call plus one startup line, no data when the audit file fails, a `TooLong` text
  is not recorded, and shutdown waits for a call in flight.
- `CapsTests`: row cap, character cap, cell cut, time limit, lock timeout.
- `CallLimitTests`: time limit per call (rows that arrive slowly end as `Timeout` less than 3 s after it; a client
  cancel stays `Cancelled`); a row that cannot fit the character cap is not held whole in memory, but the cells
  read and the rest of the row still cross the network.
- `ValueTypeTests`: xml as text with the cell cut, `hierarchyid` as a placeholder, binary counting up to 1 MiB and
  `long_value` past it, the character cap with a `long_value` ending, `QUOTED_IDENTIFIER ON` and `NOCOUNT OFF`, the
  reader as deadlock victim; text sent past the gate as a `db_datawriter` member fails as `RowsChanged` or
  `MultipleResultSets` with nothing changed, also when the instance's `user options` turn NOCOUNT on, and a write
  whose first result set is truncated is not seen (its returned rows are values of a change that was rolled back);
  with an unbroken ownership chain, `UPDATE` granted on a synonym alone changes the table it names, sent past the
  gate it fails as `RowsChanged` (`Update_grant_on_a_synonym_alone_lets_the_login_change_the_table_it_names`).
- `LargeValueTests`: a 200,000,000-character value is cut, ends the result as `long_value` and is never held whole
  (asserted: less than 64 MiB allocated), while its rest still crosses the network (asserted).
- `SchemaToolTests`: the schema tools match the database; hostile or foreign names are answered as not found;
  argument limits; names with invisible characters refused; a table without a grant is not found by `describe_table`,
  not listed by `list_tables` and not in `sys.objects`; the character cap; tools stay in the checked database.
- `ErpScaleTests`: `list_tables` and the startup check on 20,000 tables.
- `ServerConfigTests`: the configuration's text form leaves out the connection string.
- `StdioServerTests`: the built server as a child process over stdio; start with `sa` exits 3; a login whose only
  finding widens reading is refused with the startup and `check-login` texts; the commands' exit codes; every failed
  call reaches the client with `isError` and its reason word first, once
  (`Every_failed_call_reaches_the_client_as_an_error_whose_text_starts_with_its_reason_word`); a connection string
  the client library rejects; a second server on the same audit file; calls the SDK rejects are audited and logged,
  also when it cannot read their parameters
  (`Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`);
  end of input exits 0, a call in flight at end of input holds the process until it ends and writes its audit line,
  and a call stalled at the connection open ends at its time limit as `Timeout`, inside the drain window
  (`MSSQL_MCP_TIMEOUT_SECONDS` + 10 s); the login script; `sys.databases` visibility with and without
  `DENY VIEW ANY DATABASE`; no password in any result, stderr line or audit file.
- `StdioServerTests` (file `StdioServerTests.AnswerAtTheLimit.cs`, over stdio and in process): a call on a frozen
  or throttled connection, with its transaction start or rollback held, or a schema call on a frozen connection is
  answered as `Timeout` with no rows within the limit plus 2.5 s at the client (audit `duration_ms` within the limit
  plus 1.5 s; the 0.5 s figure was measured by the attack tests only), and its clean-up ends after the release with
  no second audit line; a held transaction start sends no
  query; with the rollback held, the reader's transaction stays open after the answer; a client cancel on a frozen
  connection is `Cancelled` within 2.5 s of the cancel; with more calls held in a synchronous step than processors,
  each of them and the next call that reaches its limit are answered within the same bound, and a quick call after
  them succeeds; heavy rows on a healthy link answer `Timeout` three times and leave no request or open transaction
  of the reader within 15 s; end of input exits 0 while a frozen connection is still held; a call waiting for a pool
  slot held by an abandoned clean-up ends at its limit as `Timeout` or at `Connect Timeout` as `InternalError`.
- `StdioServerTests` (file `StdioServerTests.LeftOpenSessions.cs`, the server as its own process over stdio):
  after a truncated result whose reader close is frozen, with or without its cancel held, and after a `Timeout` on
  a throttled link, no session, open transaction or lock of the call is left 2 s after the network works again, and a
  next call, the exit at end of input and the audit file are clean (one call per case; attack test run 5 found the
  throttled case not clean in every call, and the test with the held cancel is not stable: U-1R below); a live
  connection uses a network type whose handle the fix resolves.

## Where the parser and the server read text differently

Method: 335 SQL texts were sent to SQL Server 2019 CU32 (15.0.4510.2) and SQL Server 2022 CU27 (16.0.4295.3), each as
one command, and parsed by ScriptDom 180.140.0 with the same grammar the gate uses. Round 1 (280 texts) puts each of
31 characters (controls, Unicode spaces, format characters, U+FFFD, private use, lone surrogates, a surrogate pair)
in eight positions: between keyword and value, between two statements, inside an unquoted name, inside a line
comment, inside a string literal, inside a bracketed and a double-quoted name, and first in the text; plus 32
constructs (`GO`, full-width keywords and punctuation, look-alike quotes). Round 2 (55 texts) adds `--(*`, ODBC
brace escapes, noncharacters, names that differ only by an invisible character, and Unicode characters placed around comment
starts. For each text the server's result sets and first column name were compared with the statements and the
first column name ScriptDom saw. 2019 and 2022 gave the same outcome on every text.

The same 335 texts are the permanent test `ParserVersusServerLexerTests`
(`tests/MssqlReadOnlyMcp.IntegrationTests/ParserVersusServerLexerCases.cs`), which also runs on SQL Server 2025.
The test asserts the split exactly, the same on 2019, 2022 and 2025: 269 refused by the gate, 61 allowed and read
the same by the server, 5 allowed and refused by the server.

A separate test (`Look_alike_end_inside_a_comment_a_string_or_an_identifier_ends_nothing_on_the_server`, 17 texts)
puts a character that only looks like an end before a second statement: U+FF0A U+FF0F, U+2217 U+2215, U+2731 U+2044,
U+FE61 U+FF0F and part-ASCII pairs inside a block comment, and also as the inner or outer end of a nested comment;
U+2019, U+02BC and U+FF07 inside a string literal and an `N'...'` literal; U+FF3D inside a bracketed name; U+FF02
and U+201D inside a double-quoted name. On SQL Server 2019, 2022 and 2025 the gate allows all 17 and the server
reads each as one statement with the parser's column name.

| Class | SQL Server | ScriptDom | Gate |
| --- | --- | --- | --- |
| Line comment end | ends only at CR or LF | ends only at CR or LF | refuses a line comment holding other controls or U+2028, U+2029 |
| U+0000 outside literals | whitespace | whitespace | refused |
| Lone surrogate | arrives as U+FFFD | reads the surrogate | text refused before parsing (the gate called directly, `RegressionTableTests`); over stdio the MCP SDK rejects such a `run_query` call before the gate: audit reason `InvalidRequest`, `sql` null (`docs/audit-log.md`) |
| `GO` on its own line | reads it as a column alias, runs both statements | batch separator | refused (`NotSingleBatch`) |
| U+200D and U+FEFF in names | identifier characters; `a<U+200D>b`, `[a<U+200D>b]` and the same with U+FEFF resolve to the column `ab` | identifier characters (U+200B, by contrast, is whitespace to it) | refused, also inside bracketed and quoted names |
| Look-alike quotes (U+FF07, U+2019) and full-width parentheses | syntax error | identifier characters | allowed; the server refuses it, nothing runs |
| `--(*` | syntax error | syntax error | refused as a token |

Limits of this measurement:

- Only the default collation and the default compatibility level of each image were measured.
- Whether Microsoft.Data.SqlClient or the server replaces a lone surrogate with U+FFFD was not isolated. Over stdio
  such text reaches neither: the MCP SDK rejects the request first (measured on SQL Server 2019, 2022 and 2025).
- The gate's check for unassigned code points follows the Unicode tables of the .NET runtime in use.
- SQL Server 2025 runs the permanent test but was not part of the original measurement.

## Known limits

- **The login check does not inspect everything.** It looks at the server and the connected database only; in
  `master` it does not tell a `public` grant of `EXECUTE` on a system object from the default ones; the owner of
  an external model (SQL Server 2025), an availability group, an external language or an external table is not
  found; the other object types it does not ask (defaults, rules, constraints, DML triggers, internal and system
  tables) could not be given an owner of their own; a
  password-protected symmetric key can be opened with `REFERENCES` or `VIEW DEFINITION` plus its password, which
  the check allows. The lists and the other limits of the check are in `docs/login-check.md`,
  "What `check-login` refuses".
- **Synonyms and views can point elsewhere.** The gate sees a one-part or two-part name. A synonym can point to
  another database or, through a linked server, to another server; a view can read tables the login has no SELECT
  on through an ownership chain; and a view or a computed column can call a user-defined function, which the gate
  refuses only when it is written in the query text. The login check asks for rights on a synonym itself but does
  not follow synonyms, views or computed columns to what they read. None of this reading has a test here. Grant
  SELECT only on objects whose content you accept the assistant reading, and check synonyms, views and computed
  columns before you grant them. Reading a field or property of a CLR user-defined type (`t.Col.X`), and `CAST` or
  `CONVERT` of such a column to text, pass the gate; for the test suite's one native-format type, SQL Server asked
  `EXECUTE` on the type for each (`docs/sql-gate.md`, "Members of CLR user-defined types").
- **Catalogue views are ordinary names to the gate.** `sys.objects` or `INFORMATION_SCHEMA.TABLES` pass the gate as
  two-part names. SQL Server's metadata visibility shows the reader the user objects it holds a permission on, plus
  the names of objects that ship with SQL Server (`is_ms_shipped`); a user table without a grant is not in
  `sys.objects` (`SchemaToolTests`, `Table_without_a_grant_is_not_found_not_listed_and_not_in_sys_objects`).
  The error text of a `run_query` that names such a table does show it: error 229 for an existing table, 208 for a
  missing one, 207 for a missing column on it; no row data (measured on SQL Server 2022; `docs/reference.md`,
  "Error reasons").
  Database names are different: `public` holds `VIEW ANY DATABASE` by default, so `sys.databases` lists every
  database on the instance, also those the login has no user in (`StdioServerTests`,
  `Reader_sees_in_sys_databases_a_database_it_has_no_user_in`). Optional hardening, not in the login script:
  `DENY VIEW ANY DATABASE TO <login>` leaves only `master`, `tempdb` and the connected database visible, and
  `check-login` and the tools keep working (`Deny_view_any_database_hides_other_databases_and_the_server_still_works`).
- **The audit `tables` field is a reading aid.** It lists names as written. SQL Server may resolve a differently
  spelled name to the same object; in a `run_query` text, full-width letters and some other characters were not
  measured (a `describe_table` name with a full-width letter: `docs/reference.md`, "Tools"). `sql` and
  `sql_sha256` are the authoritative record. A `TooLong` text and a refused name argument are not recorded.
- **CPU, memory and tempdb use at SQL Server can outlast the answer of a call stopped by its time limit.** The
  limit counts from the start of the call and covers opening the connection, the session settings, and the query
  and its reading; each command also keeps it as its command timeout. A call that has not ended 0.5 s after the
  limit, its clean-up included, is answered as `Timeout` with no data (`Cancelled`, with no answer, if the client
  cancelled it first), and its clean-up (the cancel, the reader close, the rollback and the connection close) goes
  on in the background; a client cancel moves the cutoff to 0.5 s after the cancel if that is earlier. A call that
  ends before its cutoff keeps its own outcome: in run 4 one returned `ok` with 23 rows at 5,135 ms at a limit of
  5 s, and no `Timeout` or `Cancelled` answer or audit line carried data (`docs/security-testing.md`, run 4).
  Many calls frozen at once in the middle of a row delay the answers of every call, healthy ones too (U-2 in
  `docs/security-testing.md`). Measured in run 4 at a limit of 5 s, once per engine and count (2022 at 60 calls twice):
  with 60 or 120 such calls the median answer came after 8.2 to 29.8 s in parallel runs on the three engines and after
  10.4 to 30.2 s alone (2019 and 2025), each maximum within 0.04 s of its median; a healthy one-row call sent 1.5 s in
  returned `ok` after up to 28.7 s on 2022 and 2019, and on 2025 `Timeout` after 6.4 to 9.2 s. With 30 such calls
  the answers came within 5.62 s on 2022 and 2019 in parallel runs, but at a median of 6.7 s alone on 2019 and at
  most 7.5 s in a parallel run on 2025, where the healthy call got `Timeout` at 6,014 ms. Calls frozen at the
  rollback kept within 5.65 s at every count. Run 5 on `a38fd06`, 60 such calls at a limit of 5 s, once in a
  parallel run and once alone per engine: median answers 10.8, 12.1 and 10.8 s in parallel and 10.2, 10.2 and
  10.5 s alone (2022, 2019, 2025); the healthy call returned `ok` after 8.7 to 10.6 s on 2022 and 2019, and on 2025
  got `Timeout` at 6.2 s, alone too. While answers are delayed this way, the server's own cutoff timer can run late
  too, so a call whose work ended after its limit plus 0.5 s could still return data (reasoned from the code; not
  observed).
  Until the clean-up ends, SQL Server may still run the query, and the session keeps its read transaction and its
  locks. Measured in run 4 on `12706a1`: with heavy rows on a healthy link at a limit of 5 s, in one run per
  engine the session was gone at the answer on SQL Server 2022 and 2025 and ended 1.8 s after it on 2019 (alone:
  0.9 s); in other heavy-row runs at 5 and 10 s, on all three engines, the reader's request and open transaction
  were still there 1.5 s after the answer (when they ended was not timed), and repeats at 5 s on fresh containers
  (2019, 2025) were clean by then; two 2019 repeats at 10 s were not. A connection frozen or throttled kept its session,
  and a writer on the same rows waited, until the network worked again or the connection was cut. On
  `12706a1` such a session could stay until the server process exited (U-1).
  Since `a38fd06` the server closes a connection the client library gave up and left open, so SQL Server ends
  the session once the client's close reaches it. That close is best effort, by a reading of the code: when a
  network callback of the client library is still pending at that close, the server does not close the connection,
  and that session stays as on `12706a1` (no repository test is built to produce that case, and none observed it);
  a connection the client library breaks only after the server's close is not closed either; and whether the client
  library's own timers can still use a connection the server closed is not covered (run 5: no crash in 312 process
  ends). Measured in run 5 on `a38fd06`, three engines: on a frozen link (a truncated result whose connection then
  froze, a call frozen in the middle of a row, 120 such calls at once), after a client cancel, after a cut and for
  the schema tools, no session of the call was left after the release (0.2 to 0.7 s after it where timed);
  `12706a1`, run the same way, still had the session 60 s after the release for the truncated result, the 120 calls
  and a cancelled truncated result. On a link throttled to 5 KB/s this does not hold for every `Timeout`: next
  bullet (U-1R). The repository's tests (`StdioServerTests.LeftOpenSessions.cs`, limit 3 s, one call per case, on
  `a38fd06`, on Windows, on the three images) found no session, open transaction or lock of the call within 2 s after
  the network worked again, for a truncated result whose reader close froze, with and without its cancel held, and for a
  `Timeout` on a link throttled to 5 KB/s; one of these tests is not stable (next bullet). If the close never
  reaches SQL Server, the session stays until SQL Server
  finds the connection dead by itself (not measured) (`docs/reference.md`, "Commands and exit codes"). The fix reads
  internal members of the client library, so it depends on the version the release ships (Microsoft.Data.SqlClient
  7.1.1). Gate tests (`LeftOpenConnectionsGuardTests`), which run in CI and in the release workflow, assert that version
  and that every internal the fix needs is found, so a version change fails them. The fix's integration tests ran on
  Windows, where the client library uses its native networking; CI runs them on Ubuntu, with its managed networking;
  macOS was not tested. Before `12706a1` the answer itself waited for that clean-up (`docs/security-testing.md`, T-1 and
  T-2). A heavy join or sort can load the server until then. The server does not limit how many calls run at once. The
  server-side answer is Resource Governor: put the login in a workload group with its own limits (check that your SQL
  Server edition offers it).
- **On a slow link, a `Timeout` can leave its session open (U-1R).** Measured in run 5 on `a38fd06`, three engines,
  on a link throttled to 5 KB/s: 8 of 27 single calls at a limit of 3 s, and 21 of 450 calls repeated 50 in a row at
  a limit of 2 s (`12706a1`, run 4: 210 of 450), left the reader's session at SQL Server with an open read
  transaction and a lock. Every one of these calls was answered `Timeout` with one audit line and no data. In the
  single calls that left it, the client never closed the connection: while the link stayed slow, a writer on the
  same rows waited 58.3 to 60.3 s, against about 6.1 s when the client closed it, and after the throttle ended the
  session stayed to the end of each observation (up to 60 s after the release, and after the next call); in the
  repeated calls, the open transaction and the lock were read 45 s after the release. Longer was not measured. No
  data leaked and no audit line was missing. The cause is not proven; the fix looks at the connection
  once, before the server's close. Whether the code before `12706a1` left such sessions was not measured. The
  repository test
  `Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only`
  is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2
  of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the
  test of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was
  captured. That it fails for the same reason as U-1R (the fix looks at the connection once, before the close) is
  reasoned, not proven. It can fail in a CI run or a local run.
- **Abandoned sessions add up when a client calls again after each `Timeout`.** Each such call can add one more
  abandoned session (its request, read transaction and locks) when its clean-up has not ended at its cutoff, while
  the earlier ones still clean up. The server sets no limit of its own on them. Measured in run 4 on
  `12706a1`, three engines, 50 calls in a row at a limit of 2 s, each on a frozen link: all 50 answered
  `Timeout` with no data; with the default pool and with `Pooling=false` the reader's sessions at SQL Server grew by
  one per call; with `Max Pool Size=5` they stopped at 5, and from the sixth call each call waited for a free
  connection and was answered `Timeout` after about 2.0 s. The same on a throttled link. After the release no
  session of the reader was left. Run 5 on `a38fd06`, the same 50 calls, once per engine: all answered `Timeout`
  with no data. On a frozen link the sessions again grew by one per call with the default pool and with
  `Pooling=false`; with `Max Pool Size=5` they stopped at 5 on SQL Server 2019 and 2025 but reached 12 on 2022; 45 s
  after the release none held an open transaction. On a throttled link they no longer grew by one per call (at most
  8 at any sample), and 45 s after the release 0 to 5 per pool setting still held an open transaction and a lock
  (U-1R).
  In the repository's test with `Max Pool Size=1` and the one pooled connection held by an abandoned clean-up, a
  later call waited for that connection and ended at its time limit as `Timeout` (limit 2 s), or at the connection
  string's `Connect Timeout` of 3 s as `InternalError` (limit 10 s) (`docs/reference.md`, "Commands and exit codes").
- **Long values still cross the network.** A value that ends the result as `long_value` is never held whole, but
  SQL Server still sends it in full (`docs/reference.md`, "Result limits"; `LargeValueTests`). That read limit holds
  for text and binary values. An xml value with one very large text node, or a large CLR value (`geography`,
  `geometry`, `hierarchyid`), may be read whole into memory by the client library before it is cut or named. In the
  attack test, one `xml` value with a 20,000,000-character text node took 26 to 30 s to read, and the server process
  peaked at 102 MB working set, against 61 to 62 MB for `SELECT 1` (SQL Server 2019, 2022 and 2025, once each; on
  2019 the read ended as `Timeout` at the limit of 30 s; `docs/security-testing.md`, run 3). Whether the client
  library held the value whole was not shown; a large CLR value was not measured.
- **Locks.** The session waits at most the lock timeout and runs at low deadlock priority; under READ COMMITTED its
  own shared locks can still make a writer wait, until the call ends (`docs/promises.md`, "What it promises").
  For a call ended as `Timeout` or `Cancelled` at its cutoff, its locks and its read transaction stay until its
  abandoned clean-up ends, which can be after the answer ("CPU, memory and tempdb use" above); on a slow link a
  `Timeout` can leave them longer, with its session (U-1R above).
- **Syntax newer than the server.** The gate parses with `TSql170Parser` (the package's parser for version 17.0;
  the newer `TSql180Parser` is not used). On SQL Server 2019, syntax added in 2022 passes the gate and fails on the
  server with a syntax error, or with error 195 for `DATETRUNC`, `GREATEST` and `LEAST` (`DifferentialTests`,
  `NewIn2022`); nothing runs.
- **Audit limits.** The chain shows edits, deletions and reordering inside the file. It does not show removal of
  the newest lines or a full rewrite, unless the last hash is copied somewhere the writer cannot write. The hash
  has no key. The SQL text, literals included, is stored in plain text. Timestamps come from the host clock. On
  Linux the file lock is advisory, and a crash just after the file is created can lose it. The full text is in the
  `AuditLog` class documentation and in `docs/audit-log.md`.
- **Data leaves the building.** Rows, table and column names, and SQL Server error text (for classes 11 to 16,
  under any reason, the server's message, which can name objects or quote a value) go to the AI vendor. Read-only
  does not change that. A login that fails after start (for example, the reader's password was changed) is answered
  with SQL Server's message, which names the login (error 18456); error 4060 names the database. Neither holds the
  password or the connection string (`ToolErrors.cs` adds only the number, class and line; no test covers a failed
  login after start). Choose what the login may read with `scripts/create-readonly-login.sql`.
- **Secrets on the client side.** The connection string lives in the client's environment or configuration file.
  The server never prints or audits it, but anyone who can read that environment or file can read the password.
- **Idle sessions stay open.** The server does not change the client library's connection pooling settings, so
  idle sessions of the login stay open on SQL Server between calls. When a call that its time limit or a client
  cancel reached comes to close its connection and that connection is not already closed, the server clears the
  client library's pool (`SqlConnection.ClearPool`): every connection of the pool, also those other calls are
  using, is closed instead of reused when it is released, so the calls that follow open new physical connections
  and log in again. For a call answered at its cutoff, this happens when its clean-up reaches the close, after the
  answer, and not while its connection stays frozen. In run 4 (limit 8 s, three engines), one call frozen in the
  middle of a row and ended by its limit or by a client cancel did not break ten healthy calls that shared its pool:
  all returned `ok` with the right rows; run 5 on `a38fd06` the same. In run 5, 0 of 3 to 4 idle pooled sessions
  survived a broken call, on `12706a1` as well.

## Reporting a problem

Report it privately through GitHub private vulnerability reporting: the "Report a vulnerability" button under the
Security tab of <https://github.com/oguzhanpisgin/mssql-readonly-mcp>. Please do not open a public issue for it.

We aim to answer within 3 days. The maintainer is one person, so this is an aim, not a guarantee.

Please include the report format from the README section "Try to break it". Do not test against a database you are
not allowed to test.

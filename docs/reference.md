# Reference

This page covers configuration, commands and exit codes, the tools, result limits, error reasons and what the reader
sees at server level; the [README](../README.md) has the short start.

## Configuration

All settings are environment variables (`src/MssqlReadOnlyMcp/ServerConfig.cs`). A value that is not a whole number
or is out of range stops the start with exit code 2. A configuration error names the variable, never its value; an
audit file error names the audit file by its file name (`Program.cs`, `AuditLog.cs`). The connection string error
says which case it is, from fixed texts that hold no part of the value: not set (`MSSQL_CONNECTION_STRING is
required`), set but empty, a placeholder of the form `${...}` that the client did not fill in, or a value the SQL
Server client library cannot parse, with the form it expects (keyword=value pairs separated by semicolons) and a
pointer to [README step 2](../README.md#2-create-and-check-the-read-only-login) (`ServerConfig.cs`;
`ServerConfigMessageTests.cs`, in the Gate tests). The connection pooling
settings are left to the client library's defaults and the connection string; when a call that its time limit or a
client cancel reached comes to close its connection and that connection is not already closed, the server clears
the pool ([Commands and exit codes](#commands-and-exit-codes); SECURITY.md, "Known limits", "Idle sessions stay
open").

| Variable | Default | Range | Meaning |
| --- | --- | --- | --- |
| `MSSQL_CONNECTION_STRING` | none, required | | SQL Server connection string. The server adds `ApplicationIntent=ReadOnly` and `Application Name=mssql-readonly-mcp`. |
| `MSSQL_MCP_AUDIT_LOG` | none, required for the server | | Path of the audit file. Created with its folder if missing; a path ending in a folder separator stops the start with exit 2. On Windows, when another process holds it, the server writes a sibling in the same folder instead (`audit.2.jsonl` for `audit.jsonl`, up to `audit.8.jsonl`) without verifying the held file, so the record of one path can be several files; on Linux it has run only in the repository's tests, which passed in CI on Ubuntu on 2026-11-13; on other platforms the second server exits 2 ([The audit log](audit-log.md#several-server-processes-on-one-audit-path)). Do not configure a name ending in `.<number>` before the extension, such as `audit.2.jsonl`: it is also sibling 2 of `audit.jsonl` in the same folder, so two configurations can write one file, one at a time. Not needed by `check-login`. |
| `MSSQL_MCP_MAX_ROWS` | 200 | 1 to 5000 | Most rows `run_query` returns. |
| `MSSQL_MCP_MAX_CHARS` | 40000 | 1000 to 45000 | Most characters in one tool result. Claude Code saves a text result above 50,000 characters to a file instead of showing it, so the ceiling stays below that. |
| `MSSQL_MCP_TIMEOUT_SECONDS` | 30 | 1 to 300 | Time limit of every call, counted from its start: it covers opening the connection, the session settings and the query with its reading, and is also the command timeout of every command, the startup check included. A call the limit stops is answered and audited as `Timeout`, unless the client cancelled it first. A call that has not ended at its cutoff, 0.5 s after its limit, is audited as `Timeout` then and answered with no data (`Cancelled`, with no answer, if the client cancelled it first); its clean-up (closing the reader, rolling back, closing the connection) counts, so this holds also for a call that had finished reading before the limit. A truncated result whose reader close cannot finish (a frozen or very slow link) is answered `ok` with the rows read before the cut at about the limit, inside the limit plus 0.5 s, because the client library gives that close up at its command timeout (as the fold of U-1 read the client library's code); a close that ended later would give `Timeout` with no data. A client cancel moves the cutoff to 0.5 s after the cancel if that is earlier than 0.5 s after the limit, and the call is then audited as `Cancelled`. A call that ends before its cutoff keeps its own outcome, so an `ok` answer with data can arrive up to 0.5 s after the limit, or later when the answer itself is delayed. Many calls frozen at once in the middle of a row delay the answers of every call, healthy ones too: in run 4, at a limit of 5 s with 60 or 120 such calls, the median answer came after 8.2 to 30.2 s (U-2). The clean-up of a call ended at its cutoff goes on at SQL Server after the answer. Connecting is also bounded by the connection string's `Connect Timeout` (default 15 s). When the answer leaves, what SQL Server may still be doing then, and calls after a `Timeout`: ["Commands and exit codes"](#commands-and-exit-codes). With 10,000 views and a 1,024-column table in the database and a limit of 1 s, the server stopped at startup on SQL Server 2019, 2022 and 2025 (`SqlException` number -2 from the startup check; [Security testing](security-testing.md), T-4); in run 4 on SQL Server 2022 with the 10,000 views and a limit of 1 s, 1 of 3 starts failed and two took 1.24 and 1.29 s. |
| `MSSQL_MCP_LOCK_TIMEOUT_MS` | 5000 | 0 to 300000 | Value of `SET LOCK_TIMEOUT` for every session. |

## Commands and exit codes

`src/MssqlReadOnlyMcp/Program.cs` and `ServerStartup.cs`.

| Command | What it does |
| --- | --- |
| (no argument) | Runs the stdio MCP server. Opens and verifies the audit file (on Windows, a sibling of it when another process holds it; Linux: run only in the repository's tests, which passed in CI on Ubuntu on 2026-11-13; elsewhere it exits 2), runs the login check, writes a `startup` audit line, then serves. |
| `check-login` | Runs the login check only and prints the result. Needs `MSSQL_CONNECTION_STRING`. |
| `verify-audit <path>` | Verifies an audit file and prints the result. Needs no variable. |

| Exit code | Meaning |
| --- | --- |
| 0 | Success, including a normal server shutdown at end of input or Ctrl+C. |
| 1 | `verify-audit`: the file does not verify or cannot be read. |
| 2 | Configuration error (including a connection string the SQL Server client library rejects), audit file cannot be opened or does not verify, all 8 audit files of the path held by other processes (Windows; Linux: run only in the repository's tests, which passed in CI on Ubuntu on 2026-11-13) or the one audit file held (other platforms), startup audit line not written, or wrong usage. |
| 3 | The login holds a permission outside the allowed set (see ["What `check-login` refuses"](login-check.md)). |
| 4 | SQL Server unreachable, or the check query failed (for example a wrong password). |
| 5 | An unexpected internal error. The stderr line names only the exception type. No test produces this code. |
| 6 | The server stopped while calls had not yet been audited and answered. Only after Ctrl+C or a termination signal, never after end of input, and only when answering a call is itself held up (see below). No test produces this code; two attempts in the attack test ended 0. |

Where output goes (`Program.cs`): `check-login` writes its result to stdout, the pass line or, with exit 3, the
header line and one line per finding; `verify-audit` writes its result to stdout with exit 0 and 1. Every other
failure, the server's own exit 3 included, is one line on stderr. A server that writes a sibling of the configured
audit file (Windows; Linux: run only in the repository's tests, which passed in CI on Ubuntu on 2026-11-13) says so
in one stderr line at start ([The audit log](audit-log.md#several-server-processes-on-one-audit-path)). The running
server writes only MCP messages to stdout, and to stderr one line per call, never the SQL text (`ToolService.cs`);
its shape, an example, and the shape of the warning described below:

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

The startup refusal line shown in [README step 2](../README.md#2-create-and-check-the-read-only-login) says `1 finding` for one, and
adds ` The startup audit line could not be written.` when that write failed too. In
both texts (that line and the `check-login` output) a finding names at most ten objects, then ` and <N> more`
(`PermissionCheck.cs`). A refusal lists rights outside the read-only set; a finding can be a right that only
widens reading, for example `UNMASK`. The pass line reports the outcome of the checks in ["What `check-login` refuses"](login-check.md), not a proof that the login cannot write.

When the answer leaves: a call is answered when it ends, or at its cutoff, 0.5 s after its time limit
(`MSSQL_MCP_TIMEOUT_SECONDS`, counted from the start of the call) or 0.5 s after a client cancel if that comes
first. At the cutoff it is audited as `Timeout` and answered with no data; after a client cancel it is audited as
`Cancelled`, and the cancelled request gets no answer (run 4). Its clean-up goes on in the background. Measured in
the attack test ([Security testing](security-testing.md), run 4, SQL Server 2019, 2022 and 2025, once each unless the
record gives repeats): a call frozen at the session settings, the transaction start, the query, the first row, in the
middle of a row, between rows or at the rollback, and the schema tools frozen at each of their points after the open, at
the limit plus 0.50 to 0.52 s (limit 3 s); a call or a schema tool frozen at the connection open, at the limit plus 0.03
to 0.05 s; on a single frozen pooled connection or a throttled link, 2,496 to 2,516 ms at a limit of 2 s and 5,500 to
5,513 ms at 5 s; with the transaction start or the rollback held, 3,525 to 3,539 ms at 3 s; every slow-row, heavy-query
and lock case that ended as `Timeout`, at most 0.56 s past the limit (limits 2 to 120 s). Measured only at the 22
freeze points (limit 30 s), a client cancel was audited as `Cancelled` 500 to 517 ms after the cancel; at the
connection open the line came at the cancel itself (10 to 21 ms before the harness's cancel time); the schema tools
the same. The
repository's tests assert an answer within the limit plus 2.5 s at the client and an audit `duration_ms` within the
limit plus 1.5 s, for a call on a frozen or throttled connection, with a held transaction start or rollback, and for the
schema tools on a frozen connection; after a client cancel on a frozen connection, a `Cancelled` result within 2.5 s of
the cancel. Those are the tests' bounds; the 0.5 s figures above were measured by the attack tests only. Run 5
measured the same rows on `a38fd06` (three engines, with two or three engines running in parallel): the
freeze points at the limit plus 0.50 to 0.53 s (at the connection open, 0.03 to 0.05 s), a single frozen
pooled connection, a held transaction start or rollback and a throttled link at the limit plus 0.50 to 0.54 s, and a
client cancel audited 501 to 517 ms after the cancel. Before `12706a1` the answer waited for the clean-up: in run 3 up
to about 5 s past the limit on a healthy connection, and on a frozen one 5 s past the limit or until the network was
released (T-1 and T-2 in the record).

Many calls frozen at once in the middle of a row delay the answers of every call, healthy ones too (U-2 in the
record). Measured in run 4 at a limit of 5 s, once per engine and count (2022 at 60 calls twice): with 60 or 120 such
calls the median answer came after 8.2 to 29.8 s in parallel runs on the three engines and after 10.4 to 30.2 s alone
(2019 and 2025), each maximum within 0.04 s of its median; a healthy one-row call sent 1.5 s in returned `ok` after
up to 28.7 s on 2022 and 2019, and on 2025 `Timeout` after 6.4 to 9.2 s. With 30 such calls the answers came within
5.62 s on 2022 and 2019 in parallel runs, but at a median of 6.7 s alone on 2019 and at most 7.5 s in a parallel run
on 2025, where the healthy call got `Timeout` at 6,014 ms. Calls frozen at the rollback kept within 5.65 s at every
count. Run 5 on `a38fd06`, 60 such calls at a limit of 5 s, once in a parallel run and once alone per engine: median
answers 10.8, 12.1 and 10.8 s in parallel and 10.2, 10.2 and 10.5 s alone (2022, 2019, 2025); the healthy call
returned `ok` after 8.7 to 10.6 s on 2022 and 2019, and on 2025 got `Timeout` at 6.2 s, alone too. While answers are
delayed this way, the server's own cutoff timer can run late too, so a call whose work ended after its limit plus
0.5 s could still return data (reasoned from the code; not observed).

Data after the limit: a call can return `ok` with data after its limit when its work, clean-up included, ended
inside the 0.5 s grace (run 4: 23 rows at 5,135 ms at a limit of 5 s, SQL Server 2019, once), or later when the
answer itself is delayed (U-2). In run 4 no `Timeout` or `Cancelled` answer or audit line carried data, in more than
1,000 calls.

What SQL Server may still be doing after a call ended at its cutoff (`Timeout` or `Cancelled`): the answer does not
end the call's work at SQL Server. Until its clean-up ends, the abandoned session keeps its request, its open read
transaction and its locks, so a writer can still wait on it; with the rollback held, the reader's transaction stayed
open after the `Timeout` answer. Measured in run 4 on `12706a1`: with heavy rows on a healthy link at a
limit of 5 s, in one run per engine the session was gone at the answer on SQL Server 2022 and 2025 and ended 1.8 s
after it on 2019 (alone: 0.9 s), and a writer on the same rows waited 1,565 ms (alone: 762 ms); in other heavy-row
runs at 5 and 10 s, on all three engines, the reader's request and open transaction were still there 1.5 s after
the answer (when they ended was not timed), and repeats at 5 s on fresh containers (2019, 2025) were clean by then; two
2019 repeats at 10 s were not. A connection frozen and released or cut after 20 s kept the request, the transaction and
the locks until the release or the cut, and a writer on the same rows was blocked until then. On `12706a1` such a
session could stay until the server process exited (U-1 in the record). Since `a38fd06`, when the client library
gives up a broken connection (at its command timeout for a reader still reading, or 5 s after a cancel it sent went
unanswered, as the fold of U-1 read the client library's code) and leaves the network connection open, the server
closes it, and SQL Server ends the session and rolls back its read transaction once the client's close reaches it: on
a frozen link, once the network works again or the connection is cut. That close is best effort, by a reading of
the code: when a network callback of the client library is still pending at that close, the server does not close
the connection, and that session stays as on `12706a1` (no repository test is built to produce that case, and none
observed it); a connection the client library breaks only after the server's close is not closed either; and
whether the client library's own timers can still use a connection the server closed is not covered (run 5: no
crash in 312 process ends). Measured in run 5 on `a38fd06`, three engines: on a frozen link (a truncated result whose
connection then froze, a call frozen in the middle of a row, 120 such calls at once), after a client cancel, after a
cut and for the schema tools, no session of the call was left after the release (0.2 to 0.7 s after it where
timed); `12706a1`, run the same way, still had the session 60 s after the release for the truncated result, the 120
calls and a cancelled truncated result. On a link throttled to 5 KB/s this did not hold for every `Timeout` up to
v0.1.0 (U-1R in the record): 8 of 27 single calls at a limit of 3 s, and 21 of 450 calls repeated 50 in a row at a
limit of 2 s (`12706a1`, run 4: 210 of 450), left the reader's session with an open read transaction and a lock, on all
three engines. In the single calls that left it, the client never closed the connection: while the link stayed
slow, a writer on the same rows waited 58.3 to 60.3 s, against about 6.1 s when the client closed it, and after the
throttle ended the session stayed to the end of each observation (up to 60 s after the release, and after the next
call); in the repeated calls, the open transaction and the lock were read 45 s after the release. Longer was not
measured. No data leaked and no audit line was missing. Whether the code before `12706a1` left such sessions was not
measured. As diagnosed after v0.1.0 (2022 image), the client library's command timeout fires in the middle of a row
while the server's reader close drains the throttled result, and the library breaks the connection twice: its parser
ends `Broken`, not `Closed`, and the fix of v0.1.0 closed only a `Closed` one. Since v0.1.1 the server also closes a
connection whose parser is `Broken`, when the client library has doomed it, the call's own `SqlConnection` is closed
and no other `SqlConnection` holds it, with the same pending-callback and handle checks; when an internal member it
reads is missing, it closes nothing. Retest on 2026-11-13, on Windows, 2022 image, single calls at a limit of 3 s,
the throttle held 10 s after the limit: 100 of 100 runs left no connection open (in a 25-run diagnostic retest the
`Broken` case closed it in 2). Not shown: SQL Server 2019 and 2025 in this case, the 50 calls in a row of run 5, a
link throttled for 60 s as in run 5, a real slow network, Linux and macOS
([Security testing](security-testing.md#findings), U-1R). The
repository's tests (`StdioServerTests.LeftOpenSessions.cs`, limit 3 s, one call per case, on `a38fd06`, on Windows,
on the three images) found no session, open transaction or lock of the call within 2 s after the network worked again,
for a truncated result whose reader close froze, with and without its cancel held, and for a `Timeout` on a link
throttled to 5 KB/s. One of these tests,
`Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only`,
is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2
of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the test
of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was captured
then, so the cause of those failures is not known. The failures whose message was captured after v0.1.0 (one in 40
local runs on the 2022 image, one CI run on the 2019 image) had a cause other than U-1R: the request had ended while
the cancel was held; in the local one, SQL Server ended it on its own because the whole result fit into the network
buffers between SQL Server and the test's proxy. It can fail in a CI run or a local run. If the close
never reaches SQL Server, the session stays until SQL Server finds the connection dead by itself (not measured); run 4
kept one link frozen, or throttled to 5 KB/s, through
50 calls in a row for about 2 minutes, kept 120 calls frozen until the last was answered, about 30 s on 2022 and 2019,
and watched a session left after a release for at most 180 s. The fix reads internal members of the client library, so
it depends on the version the release ships (Microsoft.Data.SqlClient 7.1.1). Gate tests
(`LeftOpenConnectionsGuardTests`), which run in CI and in the release workflow, assert that version and that every
internal the fix needs is found, so a version change fails them; `LeftOpenConnectionsDecisionTests` check, without
SQL Server, which connections the fix closes. The fix's integration tests ran on Windows, where the
client library uses its native networking; CI runs them on Ubuntu, with its managed networking; macOS was not tested.
The audit line's `duration_ms` and the stderr line's `<ms>` are the time from the start of the call to its cutoff or its
answer, not the time SQL Server worked on it. An abandoned clean-up writes no audit line and no stderr line, also when
it fails.

Calls after a `Timeout`: a client that calls again after each `Timeout` can add one more abandoned session each
time, when that call's clean-up has not ended at its cutoff, while the earlier ones still clean up. The server sets
no limit of its own on them. Measured in run 4 on `12706a1`, three engines, 50 calls in a row at a limit of
2 s, each on a frozen link: all 50 answered `Timeout` with no data; with the default pool and with `Pooling=false`
the reader's sessions at SQL Server grew by one per call; with `Max Pool Size=5` they stopped at 5, and from the
sixth call each call waited for a free connection and was answered `Timeout` (text
`Timeout: SQL Server error -2, class 11, line 0`) after about 2.0 s. The same on a throttled link. After the release
no session of the reader was left. Run 5 on `a38fd06`, the same 50 calls, once per engine: all answered `Timeout`
with no data. On a frozen link the sessions again grew by one per call with the default pool and with
`Pooling=false`; with `Max Pool Size=5` they stopped at 5 on SQL Server 2019 and 2025 but reached 12 on 2022; 45 s
after the release none held an open transaction. On a throttled link they no longer grew by one per call (at most 8
at any sample), and 45 s after the release 0 to 5 per pool setting still held an open transaction and a lock (U-1R).
In the repository's test, with `Max Pool Size=1` and the one pooled connection held by an abandoned clean-up whose
rollback was held, a later call ended at whichever came first: its time limit, answered `Timeout` at a limit of 2 s,
or the connection string's `Connect Timeout` of 3 s, answered `InternalError` (text `InternalError: the call
failed.`) at a limit of 10 s.

When a call that its time limit or a client cancel reached comes to close its connection and that connection is
not already closed, also a call that then ended before its cutoff, the server clears the client library's
connection pool (`SqlConnection.ClearPool`): every connection of the pool, also those other calls are using, is
closed instead of reused when it is released, so the calls that follow open new physical connections and log in
again. For a call answered at its cutoff, this happens when its clean-up reaches the close, after the answer, and
not while its connection stays frozen. In run 4 (limit 8 s, three engines), one call frozen in the middle of a row
and ended by its limit or by a client cancel did not break ten healthy calls that shared its pool: all returned
`ok` with the right rows; run 5 on `a38fd06` the same. In run 5, 0 of 3 to 4 idle pooled sessions survived a broken
call, on `12706a1` as well.

End of input, signals and exit 6: at end of input the server keeps running until every call in flight has been
audited, then the audit file closes and the process exits 0. A call still unanswered when input ends gets its audit
line but no answer: in run 4, with three calls frozen at a limit of 5 s, the process wrote their three `Timeout`
lines, wrote no answer, and exited 0 about 4.55 s after input closed (three engines; the same in runs 2 and 3).
Abandoned clean-ups do not hold the process: with input closed after the answers and the clean-ups still running,
it exited 0 in 18 to 22 ms (run 4, three engines); in the repository's test, with a frozen connection still held,
it exited 0 within the drain window and the frozen session was still at SQL Server. The process's connections
close with it, and SQL Server rolls back each session once the closed connection reaches it (in that test, once the
proxy passed the close on). On Ctrl+C or a termination signal the host's own stop runs first (its shutdown timeout,
30 s by default; not tested: run 4's Ctrl+C and Ctrl+Break exits took at most 569 ms), then the server waits at most
`MSSQL_MCP_TIMEOUT_SECONDS` plus 10 seconds for calls not yet answered; if any remain, it writes one stderr line with
their number and exits 6 (`Program.cs`). Those calls return no data and may have no audit line. Run 4 sent Windows
console events, three engines, limit 5 s, three calls frozen in the middle of a row: Ctrl+C or Ctrl+Break while they
were unanswered, exit 0 in 554 to 569 ms, each call audited as `Cancelled` and given no answer; Ctrl+C while their
clean-ups ran, exit 0 in 86 to 147 ms; Ctrl+C when idle, 92 to 101 ms. Run 5, on `a38fd06`, the same cases: input
closed while the calls were unanswered, exit 0 after 4,552 to 4,562 ms; after their answers, 17 to 22 ms; Ctrl+C or
Ctrl+Break while they were unanswered, 543 to 560 ms; Ctrl+C while their clean-ups ran, 91 to 145 ms; Ctrl+C when
idle, 90 to 94 ms. A termination signal on Linux was not sent. As a
call is answered at its cutoff, one can remain unanswered at the end of the drain only if answering it is itself held
up, for example by the audit file's flush to disk, a stalled host or many calls frozen at once in the middle of a row
(reasoned from the code). No test produces exit 6, and run 4's two attempts ended 0: 120 calls frozen in the middle of a
row at a limit of 2 s with input closed at once, and output not read until the pipe was full, which kept the process
running until the output was read again. Ending the process with `TerminateProcess` leaves no audit line for the calls
in flight (exit -1 in run 4), and their sessions stay until the link heals.

Tests: `StdioServerTests.cs`
(`Client_closing_stdin_after_a_call_ends_the_process_with_exit_0_and_no_error_line`,
`Call_in_flight_when_stdin_closes_writes_its_audit_line_before_the_process_ends`,
`Call_stalled_at_connection_open_ends_at_the_time_limit_of_the_call_and_is_audited`),
`StdioServerTests.AnswerAtTheLimit.cs`
(`Frozen_established_connection_is_answered_as_Timeout_at_the_limit_and_cleaned_up_after_release`,
`Throttled_link_answers_Timeout_at_the_limit_plus_grace`,
`Call_whose_transaction_start_is_held_past_the_limit_answers_Timeout_and_never_sends_its_query`,
`Call_whose_rollback_is_held_past_the_limit_answers_Timeout_and_returns_no_rows`,
`Schema_calls_on_a_frozen_connection_answer_Timeout_at_the_limit_plus_grace`,
`Client_cancel_during_a_frozen_connection_is_audited_as_Cancelled_within_the_grace`,
`Healthy_link_heavy_rows_answer_Timeout_at_the_limit_three_times_and_the_server_is_clean_afterwards`,
`Answers_stay_on_time_with_more_held_synchronous_steps_than_processors`,
`Calls_waiting_for_a_pool_slot_held_by_an_abandoned_clean_up_end_at_their_limit_or_at_the_connect_timeout`,
`Process_exits_0_at_end_of_input_while_a_frozen_connection_is_still_held`), `CallLimitTests.cs`
(`Rows_that_arrive_slowly_end_at_the_time_limit_of_the_call_as_a_timeout`) and `AuditTests.cs`
(`Shutdown_drain_waits_for_a_call_in_flight_before_the_log_closes`). The end of the session after a broken connection:
`StdioServerTests.LeftOpenSessions.cs`
(`Truncated_result_whose_reader_close_is_frozen_ends_its_session_once_the_network_works_again`,
`Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only`,
`Timeout_on_a_throttled_link_ends_its_session_once_the_network_works_again`,
`The_state_object_of_a_live_connection_is_one_whose_network_handle_the_fix_resolved`); the client library version
and internals the fix reads: `LeftOpenConnectionsGuardTests.cs` in the Gate tests
(`The_referenced_SqlClient_is_the_version_whose_internals_were_read`,
`Every_SqlClient_internal_of_the_fix_is_found`,
`The_network_handle_resolves_on_the_native_and_the_managed_state_object`).

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
surrogates (`ToolService.FindHiddenCharacter`). No test hands this check an unpaired surrogate: over stdio the MCP
SDK rejects a call with one in a name argument before the tool runs, audit reason `InvalidRequest`
([Requests with no audit line](audit-log.md#requests-with-no-audit-line)). For example, SQL Server resolves a name
with U+200D or U+FEFF to the object without that character (`SchemaToolTests.cs`,
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
  An `xml` value is returned as text and cut the same way (`ValueTypeTests.cs`). An unpaired surrogate in a text
  cell is returned as U+FFFD, with no truncation reason, and a cut that split a surrogate pair left no lone half
  (measured on SQL Server 2019, 2022 and 2025 in the attack test, [Security testing](security-testing.md), T-5;
  no test in this repository).
- Binary values are returned as `<binary N bytes>`.
- Long values: a text or xml value over 1,048,576 characters, or a binary value over 1,048,576 bytes (shown as
  `<binary over 1048576 bytes>`), ends the result after its row with `truncated_reason` `long_value`. Later cells
  of that row read `<not read: an earlier value in the row is longer than 1048576>`. The server process never holds
  such a value whole, but the client library still reads the rest of it off the network when the reader closes,
  and discards it (`LargeValueTests.cs`, `One_very_large_cell_is_cut_and_never_held_but_the_rest_is_still_transferred`:
  a 200,000,000-character value; the test asserts that less than 64 MiB was allocated, and the 2026-11-12 run
  reported about 0.3 MB). `ValueTypeTests.cs` (`Binary_values_are_counted_up_to_one_mebibyte_only`) covers binary.
  This read limit holds for text and binary values. An xml value with one very large text node, or a large CLR
  value, may be read whole into memory by the client library before it is cut or named. In the attack test
  ([Security testing](security-testing.md), run 3), one `xml` value with a 20,000,000-character text node took 26
  to 30 s to read, and the server process peaked at 102 MB working set, against 61 to 62 MB for `SELECT 1` (SQL
  Server 2019, 2022 and 2025, once each; on 2019 the read ended as `Timeout` at the limit of 30 s). Whether the
  client library held the value whole was not shown; a large CLR value was not measured. To see a long value,
  select `DATALENGTH(col)` or `SUBSTRING(col, 1, n)` instead of the column.
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
- Time: `MSSQL_MCP_TIMEOUT_SECONDS` per call, and per command; `MSSQL_MCP_LOCK_TIMEOUT_MS` per lock wait. A call
  that has not ended 0.5 s after its limit, its clean-up included, is answered as `Timeout` with no data
  (`Cancelled`, with no answer, after a client cancel); many calls frozen at once in the middle of a row delay the
  answers of every call (U-2; [Configuration](#configuration)).

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
| `Timeout` | The call's time limit or the command timeout passed. Also a call that had not ended, its clean-up included, 0.5 s after its limit, unless the client cancelled it first, and a call still waiting for a free connection of the pool at its limit. The text names SQL Server error -2 in every case, even when SQL Server raised no error (a connection that did not open in time), and also when a server error arrived after the limit. |
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
| `Cancelled` | The client cancelled the call. The audit line holds this reason; over stdio the cancelled request gets no answer (attack test, run 4). |
| `AuditUnavailable` | The audit line could not be written, so no data is returned. |
| `InternalError` | Anything else, among them a wait for a free connection of the pool that passed the connection string's `Connect Timeout` before the call's limit. No details are returned. |

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

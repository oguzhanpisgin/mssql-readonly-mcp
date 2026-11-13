# The audit log

This page covers the audit file's format, what it leaves out, how to verify it, its limits and the requests that get
no line; step 6 of the [README](../README.md#6-first-question) gives the `verify-audit` command and its exit codes.

One JSON object per line, UTF-8, ending in `\n` (`src/MssqlReadOnlyMcp.Audit/AuditFormat.cs`). The fields always
appear in this order:

| Field | Value |
| --- | --- |
| `seq` | 1, 2, 3, ... with no gaps. |
| `ts` | UTC time from the host clock, `yyyy-MM-ddTHH:mm:ss.fffZ`. |
| `session` | A new id each time the server starts. |
| `tool` | `startup`, `list_tables`, `describe_table`, `run_query`, or `unknown` for a call to a tool name the server does not have or whose tool name cannot be read. |
| `decision` | `allowed`, `refused` or `error`. |
| `reason` | `Allowed`, or a reason word from the tables in ["Error reasons"](reference.md#error-reasons) (`LoginCanWrite` for a start refused on a login-check finding). |
| `sql` | The SQL text as received for `run_query`; null for the other tools, for a `TooLong` refusal, and for a call rejected before a tool ran (`InvalidRequest`), including a request whose parameters the MCP SDK cannot read. Over stdio, a `run_query` call whose `sql` holds an unpaired surrogate (sent as a JSON escape such as `\ud800`) is such a request: reason `InvalidRequest`, `sql` null (measured on SQL Server 2019, 2022 and 2025; `StdioServerTests.cs`, `Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`). In the tests, an unpaired surrogate reached the audit writer only through a direct call of the audit library, which writes it as U+FFFD with the same `sql_sha256` for both forms (`AuditLog.cs`; `AuditLogTests.cs`, `Lone_surrogate_in_sql_verifies`); the gate, called directly, refuses such text (`RegressionTableTests.cs`). |
| `sql_sha256` | SHA-256 of `sql`, or null. |
| `tables` | For `run_query`, the table and view names the SQL refers to, as written. For `describe_table`, the resolved `schema.name` when the lookup succeeded, empty when the argument itself was refused (missing, too long, or with a hidden character), otherwise the name as written. Empty for `list_tables`. |
| `rows` | Rows or items returned, or null when no data was returned. |
| `truncated` | Whether a cap cut the result. |
| `duration_ms` | Time from the start of the call to its answer. For a call ended as `Timeout` or `Cancelled` at its cutoff, the time to that cutoff (a cancelled request gets no answer), not the time SQL Server worked on the call ([Commands and exit codes](reference.md#commands-and-exit-codes)). |
| `result_sha256` | SHA-256 of the text returned to the client, or null. |
| `prev_hash` | `hash` of the previous line; 64 zeros on the first line. |
| `hash` | SHA-256 of this line written without its `hash` field. |

The line is written after the SQL has run and flushed to disk before the result goes back to the client
(`AuditTests.cs`, `Audit_failure_returns_an_error_and_no_row_data`: the call opened its connection before its line
failed). A call that has not ended at its cutoff, 0.5 s after its time limit or 0.5 s after a client cancel,
whichever comes first, gets its `Timeout` or `Cancelled` line then, while its clean-up may still run at SQL Server;
that clean-up writes no second line ([Commands and exit codes](reference.md#commands-and-exit-codes)). Many calls
frozen at once in the middle of a row delay the answers of every call, healthy ones too: in run 4, at a limit of
5 s with 60 or 120 such calls, the median answer came after 8.2 to 30.2 s (U-2 in
[Security testing](security-testing.md)).
The SQL text is stored in plain text, literals included. The connection string and passwords are never written.

`sql` and `sql_sha256` are the authoritative record of what was asked; `tables` is a reading aid. It lists names as
written, and SQL Server may resolve a differently spelled name to the same object (measured for U+200D and U+FEFF,
which the gate now refuses inside names; in a `run_query` text, full-width letters and some other characters were
not measured; for a `describe_table` name see [Tools](reference.md#tools)).

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

**After a failed write.** A write that fails part-way (for example on a full disk) can leave bytes after the last
newline. The log then refuses every later line too (`AuditLogTests.cs`, `Failed_append_throws_and_poisons`), and the
server opens it only at start (`Program.cs`), so later calls fail with `AuditUnavailable` and no data until the server
restarts. At the next start the server finds `PartialLine` on that line and writes nothing (`AuditLogTests.cs`,
`Torn_write_makes_reopen_fail`, `Reopen_refuses_a_bad_file_and_writes_nothing`) and exits 2 (`Program.cs`), and
`verify-audit` reports the same reason (`AuditVerifierTests.cs`, `Partial_extra_line_is_found`) and exits 1. To
start again, keep a copy of the file, then remove only the bytes after the last newline; the file must then verify,
and the server continues the chain from the last whole line. No test runs that removal: what is tested is that a file
that verifies reopens and continues its chain (`Reopen_continues_the_chain`).

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

## Requests with no audit line

Every `tools/call` request the MCP SDK passes to the server's message filters gets exactly one audit line before
its answer, also when the SDK cannot read its parameters (a lone surrogate such as `\ud800` in an argument,
`arguments` that is not an object, no tool name). Such a line has reason `InvalidRequest`, the tool name when it
can be read and `unknown` otherwise, and `sql` null; the client gets the SDK's JSON-RPC error
`-32603 "An error occurred."` (`src/MssqlReadOnlyMcp/CallToolAuditFilter.cs`, `WrapMessages`; `StdioServerTests.cs`,
`Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`). As for every call, when the line
cannot be written the call fails with no data, and a call not yet answered when the server exits with code 6 may
have no audit line ([Commands and exit codes](reference.md#commands-and-exit-codes)).

Requests the SDK or its stdio transport rejects or drops before that point run no tool and get no audit line.
That is expected and not a finding; a tool that ran without an audit line is one (README, "Try to break it").
These are:

- lines the stdio transport drops or rejects before they become a request: malformed JSON, a batch array,
  `"jsonrpc":"1.0"`, a lone surrogate in `method`, an `id` that is null, an object, an array, `true`, a fraction or
  a number too large for 64 bits, or a string `id` with a lone surrogate;
- a `tools/call` sent as a notification, without an `id`;
- on protocol revision 2026-07-28, a request whose per-request `_meta` the SDK rejects before the server sees it
  (`io.modelcontextprotocol/clientCapabilities` missing or not an object; an `io.modelcontextprotocol/protocolVersion`
  that is unsupported or holds a lone surrogate; and, once the connection has served a 2026-07-28 request without
  `initialize`, any later request whose `_meta` has no string `io.modelcontextprotocol/protocolVersion`).

The shapes in this list were sent to the server over stdio on the SQL Server 2022 image, with a probe that is not
kept as a test, except the last case of the third item, which was measured against the SDK in process, not on the
stdio server.

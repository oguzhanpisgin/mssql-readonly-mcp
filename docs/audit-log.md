# The audit log

This page covers the audit file's format, what it leaves out, how to verify it and its limits; step 6 of the
[README](../README.md#6-first-question) gives the `verify-audit` command and its exit codes.

One JSON object per line, UTF-8, ending in `\n` (`src/MssqlReadOnlyMcp.Audit/AuditFormat.cs`). The fields always
appear in this order:

| Field | Value |
| --- | --- |
| `seq` | 1, 2, 3, ... with no gaps. |
| `ts` | UTC time from the host clock, `yyyy-MM-ddTHH:mm:ss.fffZ`. |
| `session` | A new id each time the server starts. |
| `tool` | `startup`, `list_tables`, `describe_table`, `run_query`, or `unknown` for a call to a tool name the server does not have. |
| `decision` | `allowed`, `refused` or `error`. |
| `reason` | `Allowed`, or a reason word from the tables in ["Error reasons"](reference.md#error-reasons) (`LoginCanWrite` for a start refused on a login-check finding). |
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

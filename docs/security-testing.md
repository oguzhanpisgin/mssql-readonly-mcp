# Security testing

This page records the security tests run before the first release: what was attacked, with which tool and version,
on which commit and date, what came out, and what was not tested. The tests are the maintainer's own: the attack
tests and the code security review were run apart from the writing of the code and changed nothing in it. This is
not a third-party audit, not a penetration test by an outside firm, and not a certification. Every run took place
on 2026-11-12; times below are UTC. The code tested last is `7f98bfd`.

| Tests | Public commit | Tree |
| --- | --- | --- |
| Attack test, run 1 | `dedf54ad73b7063126eaa2d9c9f5ffa648ad8dbc` | `25eb3c6d76a443425fecabe194a0a5d151bb8ec7` |
| Attack test, run 2; code security review; scans, round 1 | `46baefb610a775c04b2c7909fe7476a8142e2e1f` | `6ae8882be207c02a3487c7c0d190c772b8fb27d2` |
| Attack test, run 3; scans, round 2; test suites; CI; CodeQL | `7f98bfdcec9baf1d7aa0492f7804dec712988d1d` | `617d9dd554f6e9937f8418fd30748f1f248e7782` |

This page landed in a later commit that changes documentation only. To check in a clone that the code is still the
same as in `7f98bfd` (no output means unchanged):

```powershell
git diff --stat 7f98bfd HEAD -- src tests scripts packaging .github
```

Times: run 1, server built at 03:56; run 2, 05:59 to 07:58; run 3, 08:08 to 11:18; code security review, 06:08;
scans, round 1 at 05:59 and round 2 at 08:05; test suites, finished at 07:05; CI and CodeQL results read at
07:09.

## What was tested

| Test | Tool and version | Scope | Result |
| --- | --- | --- | --- |
| Attack test, run 1 | Own attack harness: JSON-RPC over stdio to the built server (not in this repository; no version) | `dedf54a`; SQL Server 2022 every class, 2019 and 2025 classes A and C | No gate bypass, no change to database state, no password in any output; 2 LOW and 5 NOTE findings (X-1 to X-7) |
| Attack test, run 2 | The same harness, with classes H and E5 added | `46baefb`; SQL Server 2019, 2022 and 2025, every class | 584 outcome rows per engine, with the same counts and the same pass and miss results on all three; outcomes differ only where the engine does (A-198, A-205, A-224, A-236); 5 expectation misses, each explained; X-1 fixed; 1 new NOTE (R-1); no tool ran without an audit line |
| Attack test, run 3 | The run 2 harness, with one more scenario module (R3) and a local TCP proxy | `7f98bfd`; SQL Server 2019, 2022 and 2025, every class of run 2 plus R3, the paths changed after run 2 (time limit per call, size cap) | 788 outcome rows per engine: the 584 of run 2, none different from run 2, and 204 R3 rows, of which 199, 198 and 196 passed; X-1 still fixed; 2 LOW and 3 NOTE findings (T-1 to T-5); no call answered before its audit line was on disk |
| Code security review | Reading of the source at the commit; no build, no run, no container | `46baefb`: every file under `src/`, read in full (the SQL semantics of the permission check excepted); the build files, workflows, Dependabot settings, packaging and the login script; the docs that state promises and limits; tests only searched for specific cases | 1 MEDIUM, 3 LOW, 6 NOTE (C-1 to C-10); no path found where a write, DDL, EXEC or second statement passes the gate, where data returns before its audit line, or where a password or the connection string reaches the client, stderr or the audit file |
| Secret scan, files | gitleaks 8.30.1, `gitleaks dir` | `7f98bfd`, the files of the checkout | No leaks found |
| Secret scan, history | gitleaks 8.30.1, `gitleaks git` | 12 commits: `main` up to `7f98bfd` and four Dependabot branches | No leaks found |
| Workflow check | actionlint 1.7.12 | `7f98bfd`, `.github/workflows` | No output, exit 0 |
| Vulnerable NuGet packages | `dotnet list package --vulnerable --include-transitive`, .NET SDK 10.0.404 | `7f98bfd`, the six projects, source nuget.org | No vulnerable package in any of the six projects |
| Static analysis | semgrep 1.180.0, rule sets `p/csharp`, `p/secrets`, `p/github-actions`, `p/security-audit` | `7f98bfd`, the 63 files tracked by git outside `tests/` | 0 findings; 78 rules run; about 99.8% of lines parsed (see "What was not tested") |
| File system scan | trivy 0.75.0, scanners `vuln`, `secret`, `misconfig` | `7f98bfd` | 2 vulnerabilities, both in `packaging/mcpb/package-lock.json`: `tmp` 0.0.33, CVE-2026-44705 HIGH and CVE-2025-54798 LOW; no secret and no misconfiguration reported |
| Dependency scan | osv-scanner 2.6.0 (osv-scalibr 0.5.2), `scan source -r` | `7f98bfd`: `packaging/mcpb/package-lock.json` (55 packages), `Directory.Build.props` and the six project files | NuGet: none. npm, in `packaging/mcpb/package-lock.json`: `tmp` 0.0.33 GHSA-52f5-9888-hmc6 (CVSS 2.5) and GHSA-ph9p-34f9-6g65 (7.7), `zod` 3.25.76 GHSA-mrhx-c7xc-m3w5 (8.2) |
| npm packages | `npm audit --package-lock-only`, npm 12.2.2 | `7f98bfd`, `packaging/mcpb/package-lock.json` | 6 vulnerable packages (3 low, 3 high): `tmp` and `zod`, each "No fix available", and four packages that depend on them, the MCPB CLI among them |
| Test suites | `dotnet build`, `dotnet test` (SDK version not recorded in the run log) | `7f98bfd`; integration tests on the SQL Server 2022-CU27, 2019-CU32-GDR13 and 2025-CU9-GDR1 images | Build: 0 warnings, 0 errors. Passed, 0 failed: Gate 364, Audit 55; Integration 245 on each of the three images |
| CI | GitHub Actions workflow `ci` (build, unit tests, integration tests on the three images) | `7f98bfd` | Run 43172714768: success |
| Code scanning | CodeQL through `github/codeql-action` v4.39.3, languages `csharp` and `actions` (CodeQL version not recorded) | `7f98bfd` | Run 43172714825: success; open code scanning alerts: 0 |

Scans, round 1, on `46baefb` with the same tools and versions gave the same results; gitleaks read 10 commits there.
npm audit ran on `7f98bfd` only.

## Attack tests

The harness starts the built server as a child process and sends it JSON-RPC requests over stdio. SQL Server runs
in a local container, one at a time, with a test database, decoy objects and a second database. The reader login
is made by `scripts/create-readonly-login.sql`, unchanged. Passwords are generated at run time and not written to
disk; after each run every output file was searched for them, with 0 hits. The engines were the images named in
the README [prerequisites](../README.md#1-prerequisites): `@@VERSION` 15.0.4510.2 (2019), 16.0.4295.3 (2022) and
17.0.5015.4 (2025).

| Class | What it tries | Run 1 (`dedf54a`), cases per engine | Run 2 (`46baefb`) and run 3 (`7f98bfd`), outcome rows on each engine |
| --- | --- | --- | --- |
| A, gate | SQL text that writes, runs a second statement, reaches another database or server, or reads differently to the parser and to SQL Server: DML, DDL, `EXEC`, hints, comments, look-alike characters, size limits, catalogue reads | 2022: 221; 2019 and 2025: 225 | 225 |
| B, caps and errors | Row, character and long-value caps, the time limit, the lock timeout, and what error texts reveal | 2022: 37 | 37 |
| C, side effects and session | Whether a read leaves a change after the rollback, and whether session state carries from one call to the next | 13 on each | 14 (13 cases) |
| D, protocol and arguments | Malformed and batched JSON-RPC, unknown methods and tools, missing, mistyped and extra arguments, hostile names, parallel calls, cancellation | 2022: 83 | 91 (83 cases) |
| E, login check | Logins with rights beyond reading: `check-login` and the server start must refuse them | 2022: 1 scenario, 11 logins | 11 |
| F, audit log | One line per call before the answer, text injected into a line, tampering found by `verify-audit`, file locking and bad paths, a killed server | 2022: 7 scenarios, 50 sub-checks | 45 (7 cases) |
| G, configuration and secrets | Invalid settings, connection string keywords, the most verbose logging: no password or SQL text may leak | 2022: 3 scenarios, 46 sub-checks | 43 (3 cases) |
| H, added in run 2 | Request shapes around the X-1 fix: odd ids, framing, protocol revision 2026-07-28 without `initialize`, connection state, attempts to run a tool without an audit line or to put text into the audit file or stderr | not run | 90 |
| E5, added in run 2 | Login check: ownership through roles and nested roles, rights `WITH GRANT OPTION`, `IMPERSONATE`, fixed database roles, server roles | not run | 20 (19 logins and the reader) |
| Audit checks per session | `verify-audit` on each session's audit file | counted with the runs: 1 on 2022 in the class A run, 3 on 2019 and on 2025 in the classes A and C run | 8 |
| Total | | | 584; run 3 adds 204 rows for the changed paths (R3), 788 |

Run 1: on all three engines no case got past the gate, and no case changed database state (a fingerprint taken with
an administrator login before and after each class A case). The findings are X-1 to X-7 below.

Run 2: 584 outcome rows on each engine, with the same counts and the same pass and miss results on all three. The
outcomes differ only where the engine does: the row counts of `sys.configurations` (A-198) and
`sys.server_principals` (A-205), and syntax added in SQL Server 2025, which runs on 2025 only (A-224, A-236). Five
rows missed the harness's expectation, the same five on each engine, each explained: A-003 and A-073 (X-4), C-001
(X-3), D-054 (the expectation dated from run 1; the call is now audited) and F-006 (the harness expected `sql` as
U+FFFD; it is null, see R-1). The five request shapes of X-1 each got exactly one audit line before the answer and
ran no tool. The 90 class H rows were the same on each engine and all as expected: no tool ran without an audit
line, and no attacker text reached the audit file or stderr outside the documented `sql` and `tables` fields. Of
the 19 E5 logins, 18 were refused at start (exit 3); one that owns another database passed, a documented limit:
only the server and the connected database are checked, and the gate refuses names in other databases.

Run 3 retested the paths changed after run 2, the time limit per call and the size cap (C-1 and C-2), on `7f98bfd`
from 08:08 to 11:18 (clean full runs from 08:36). It used the images of run 2, one engine container at a time:
`@@VERSION` 16.0.4295.3 (2022), 15.0.4510.2 (2019) and 17.0.5015.4 (2025), in that order. Each engine gave 788
outcome rows: 584 from classes A to H and E5, and 204 from the changed-path cases (R3).

- Classes A to H and E5, compared field by field with run 2 on the same engine: 579 rows the same, 0 different.
  Five rows on each side were present in one run only: G-001 rows whose ids carry a per-run marker or the
  container port; they passed in both runs. No class B outcome (time limit, caps, errors) changed.
- The five known expectation misses of run 2 came again on each engine: A-003 and A-073 (X-4), C-001 (X-3), D-054
  and F-006 (R-1).
- The five request shapes of X-1 each got -32603, one `InvalidRequest` audit line before the answer, and ran no
  tool; `verify-audit` exited 0 on each engine. X-1 stays fixed.
- R3 passed 199 (SQL Server 2022), 198 (2019) and 196 (2025) of 204 rows. The misses (5, 6 and 8):
  - the time-limit overruns of T-1 (1 on 2022 and on 2019, 4 on 2025);
  - the frozen connection of T-2 at limits of 2 and 5 s (2 on each engine), and on 2022 and 2025 the next call
    after its release, whose answer found two audit lines: the frozen call's line, written at release, and its own;
  - the start failure of T-4 (1 on each engine);
  - on 2019 two more rows. A lock row: `describe_table` under a held lock, lock timeout 5000 ms, limit 8 s, ended
    as `Timeout` at 8,078 ms instead of `LockTimeout`; the likely cause, as the tester gives it, is that the schema
    query waited on two locks of up to 5 s each, so the limit ended it first, which is the documented behaviour
    (measured once). And the read of one `xml` value with a 20,000,000-character text node
    (`R3-E.size-xml-20M-one-node`), which ended as `Timeout` at 30,260 ms, 0.26 s past its limit of 30 s, with no
    data, where 2022 and 2025 returned it cut (`long_value`).
- The R3 cases: rows that arrive slowly, a heavy query and a blocking lock at limits from 2 to 120 s; `list_tables`
  and `describe_table` against uncommitted DDL; size-cap shapes (wide text, `xml`, binary, values just over the
  limits, surrogates, nulls) with the peak working set of the server process; a stalled connection open and
  stalled, held or throttled established connections through the proxy; 30 parallel calls; stdin closed with
  calls in flight; client cancels; 10,000 views and a 1,024-column table.
- No answer went over its cap. The server's peak working set stayed between 61 and 102 MB, measured for one large
  call at a time; 61 to 62 MB was `SELECT 1`, and 102 MB was one `xml` value with a 20,000,000-character text node,
  which took 26 to 30 s to read (once on each engine; on 2019 the read ended as `Timeout` at the limit of 30 s).
  Whether the client library held that value whole was not
  shown; a large CLR value was not measured.
- 210 server sessions (70 per engine): each exited 0, `verify-audit` exited 0 on each audit file, and its call
  lines equalled its calls.
- No call ran without an audit line on disk before its answer; the one exception is a call that had not answered
  at all yet (the frozen connection of T-2), which had no line yet. No data came back after a `Timeout`. No
  transaction or request of the reader stayed open after a call had ended, as an administrator login saw once any
  proxy hold was released. No process crashed.

State of the earlier findings on `7f98bfd`: X-1 still fixed on the three engines; X-2, X-3, X-5 and X-7 unchanged;
X-4 unchanged and documented; X-6 unchanged and accepted. R-1: F-006 again gave `InvalidRequest` with `sql` null,
and the docs of `7f98bfd` still said U+FFFD; the correction lands with this page.

## Findings

Severity is the tester's or reviewer's: CRITICAL, HIGH, MEDIUM, LOW or NOTE. The attack tests and the code security
review made no finding of severity HIGH or above, and one of severity MEDIUM (C-1). The dependency scans report two
HIGH advisories in the build-time packaging tool ("Open dependency alerts" below).

### Attack tests

| Id | Severity | Finding | What was done |
| --- | --- | --- | --- |
| X-1 | LOW | A `tools/call` with a lone surrogate in an argument, with `arguments` that is not an object, or with no tool name got JSON-RPC error -32603 and no audit line; no SQL ran | Fixed in `46baefb`: such a call gets one audit line (`InvalidRequest`) before its answer; retested in run 2 on the three engines; test `Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`. Requests that still get no line: [The audit log](audit-log.md#requests-with-no-audit-line) |
| X-2 | LOW | `run_query` errors 229, 208 and 207 tell an existing table without a grant from a missing one, and its column names from missing ones; no row data | Named as a limit: [README](../README.md#what-it-does-not-solve), [Error reasons](reference.md#error-reasons). Behaviour unchanged (SQL Server's binding order) |
| X-3 | NOTE | An auto-created statistics object stays after the rolled-back read (`sys.stats` for the table from 1 to 2 on the three engines) | Named as a limit: [SECURITY.md](../SECURITY.md#two-layers) |
| X-4 | NOTE | `SELECT 1 GO` is allowed and returns one column named `GO`; the docs said a `GO` token anywhere is refused | Docs corrected to what the gate does: [What SQL is allowed](sql-gate.md#checks-on-the-text-and-the-tokens) |
| X-5 | NOTE | The reader sees server-level metadata that SQL Server shows `public`: configuration values, host operating system, host name, every database name, login names it may see, its own session row | Named as a limit: [What the reader sees at server level](reference.md#what-the-reader-sees-at-server-level), [SECURITY.md](../SECURITY.md#threat-model) |
| X-6 | NOTE | `tools/call` and `tools/list` are served before `initialize` | Accepted: such a call passes the same gate and gets its audit line (run 1 and run 2) |
| X-7 | NOTE | A full-width letter in a `describe_table` name resolves to the object with the ASCII letter; the audit line records the resolved name | Named as a limit: [Tools](reference.md#tools) |
| R-1 | NOTE | Run 2: two docs said a lone surrogate in `run_query` SQL is written as U+FFFD and refused by the gate; over stdio the MCP SDK rejects the call first (`InvalidRequest`, `sql` null) | Corrected with this page in [The audit log](audit-log.md) and [SECURITY.md](../SECURITY.md#where-the-parser-and-the-server-read-text-differently) |
| T-1 | LOW | Run 3, healthy connection: a call stopped by its time limit ended up to about 5 s past it. Rows about every 2.3 s, limit 5 s: 8,295, 8,238 and 8,474 ms on SQL Server 2025 (3 of 3); on 2022 5,043 to 5,066 ms in the clean run (3 runs) but 8,220 to 9,755 ms in 4 of 5 earlier measurements; at a limit of 10 s, 15,000 ms (2022), 13,266 ms (2019) and 12,503 ms (2025), once each. Rows about every 1 s on 2025: 3,015 ms at a limit of 2 s and 6,107 ms at 5 s, once each. Each answer was `Timeout` with no data, its audit line was on disk first, and the session was left clean. The docs said 0.1 to 0.3 s past the limit. The cause was not investigated | Open: measured and documented, no code change in this commit. [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| T-2 | LOW | Run 3, stalled or slow established connection: the time limit did not bound the call. A frozen pooled connection ended 5 s past the limit or later: 7,017 to 7,033 ms at a limit of 2 s on the three engines (once each); at a limit of 5 s, 10,038 ms (2019) and 10,029 ms (2022, earlier round), and on 2022 and 2025 the call was held until the network was released after 45 s, with no answer and no audit line before then (2 of 4 measurements). With the link throttled to 20 KB/s, 23,796 to 25,104 ms at a limit of 2 s, once on each engine. Holds at the transaction start and the rollback lasted until release, about 20 s, as the docs say for those steps. Calls on fresh connections answered meanwhile (38 to 153 ms) | Open: measured and documented, no code change in this commit. [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| T-3 | NOTE | Run 3: a call whose rollback was held for about 20 s at a limit of 3 s returned its 2 rows after 20,141 to 20,264 ms, with one `Allowed` audit line, on the three engines (once each); the reader's transaction stayed open during the hold | Matches the documented rule: a call that finished reading before the limit keeps its answer, and the rollback has no limit of its own ([Configuration](reference.md#configuration)) |
| T-4 | NOTE | Run 3: with 10,000 views and a 1,024-column table in the database and a limit of 1 s, the server stopped at startup (`SqlException` number -2 from the startup check, empty audit file) on the three engines, once each; at a limit of 30 s each schema call answered in under 0.6 s. Startup at a limit of 1 s without those objects was not run | Consistent with the docs (the limit is also the startup check's command timeout); one sentence added at `MSSQL_MCP_TIMEOUT_SECONDS` in [Configuration](reference.md#configuration) |
| T-5 | NOTE | Run 3: `SELECT NCHAR(55296) AS v` (an unpaired surrogate in a cell) returned U+FFFD with no truncation reason, on the three engines; a cut that split a surrogate pair left no lone half. No security effect | Documented: [Result limits](reference.md#result-limits) |

### Code security review

| Id | Severity | Finding | What was done |
| --- | --- | --- | --- |
| C-1 | MEDIUM | The time limit applied to each network read, not to the call: one SELECT whose rows arrive slowly could run up to the row cap times the timeout | Changed in `7f98bfd`: one time limit per call over the connection open, the session settings, the execution and every read. Measured on SQL Server 2022, 30 slow rows, 2 s limit: before, 13,871 ms and all 30 rows; after, `Timeout` at 2,082 and 2,277 ms in two runs. Run 3 measured longer overruns (T-1, T-2). Steps the limit does not stop: [Commands and exit codes](reference.md#commands-and-exit-codes) |
| C-2 | LOW | The character cap was checked only after a whole row was read | Changed in `7f98bfd`: a row stops being read once a lower bound of its size passes the remaining room; for one row of 64 cells of 1,000,000 characters, memory allocated fell from 12.8 MB to 3.7 MB. The bytes received did not fall (64.1 MB): named as a limit in the [README](../README.md#what-it-promises) proof table |
| C-3 | LOW | The login check runs once, at start; this was not stated | Named as a limit: [What `check-login` refuses](login-check.md), README, SECURITY.md |
| C-4 | NOTE | A field, a property, `CAST` or `CONVERT` of a CLR user-defined type passes the gate and runs the type's code | Measured as a test (`ClrTypeMemberTests.cs`): for the test suite's one type, SQL Server asked `EXECUTE` on the type each time; a reader without it gets error 229, and the login check names that right. [Members of CLR user-defined types](sql-gate.md#members-of-clr-user-defined-types) |
| C-5 | NOTE | Characters that only look like the end of a comment, a string or a name were not measured | Measured as a test (`Look_alike_end_inside_a_comment_a_string_or_an_identifier_ends_nothing_on_the_server`, 17 texts): the gate allows all 17, and SQL Server 2019, 2022 and 2025 read each as one statement |
| C-6 | NOTE | After start, a failed login is answered with SQL Server's message, which names the login (error 18456) or the database (error 4060) | Named as a limit: [SECURITY.md](../SECURITY.md#known-limits), "Data leaves the building" |
| C-7 | NOTE | A write that fails part-way leaves a partial last line; the recovery was not documented | Documented: [The audit log](audit-log.md), "After a failed write"; the removal step itself is not tested |
| C-8 | LOW | The `.mcpb` bundles are attested without comparing the server files inside them with the build job's output | Open: depends on a packaging decision not yet made. What the attestation shows: [Releasing](releasing.md#what-the-workflow-does) |
| C-9 | NOTE | No NuGet lock file: transitive versions and package hashes are not pinned for release builds | Accepted for the first release |
| C-10 | NOTE | The login script's stated character limits did not cover every use of its variables | Fixed in the header comment of `scripts/create-readonly-login.sql` |

### Open dependency alerts

On 2026-11-12 the public repository had 3 open Dependabot alerts, none dismissed, all in
`packaging/mcpb/package-lock.json`, the pinned MCPB CLI that packs the bundles at build time:

- `zod`, GHSA-mrhx-c7xc-m3w5, high;
- `tmp`, GHSA-ph9p-34f9-6g65, high;
- `tmp`, GHSA-52f5-9888-hmc6, low.

The scans above count the same lock file differently. Dependabot (3) and osv-scanner (3) count advisories: two for
`tmp`, one for `zod`. trivy reported the two `tmp` advisories and not the `zod` one. npm audit counts packages (6):
`tmp`, `zod`, and the chain that depends on them, `external-editor`, `@inquirer/editor`, `@inquirer/prompts` and the
MCPB CLI. These npm packages run only in the release workflow's bundle job; what that means for the bundles is in
[Releasing](releasing.md#what-the-workflow-does).

## What was not tested

Engines and setups:

- SQL Server other than the three Linux container images above: no Windows installation, no other version, update
  or edition.
- Collations other than each image's default, among them width- and case-sensitive ones; compatibility levels
  other than the default.
- Windows authentication: every test used a SQL Server login.
- Azure SQL, Always On availability groups, Resource Governor, replication and contained databases.
- Routing in an availability group: whether the login check and later calls reach different replicas.
- A production database: the tests used databases they built themselves; the largest held 20,000 tables, for the
  startup check and `list_tables` only (`ErpScaleTests.cs`: the check passes within the default connection
  timeout; a list without a filter stops at 500 and reports the total, a filtered list returns only the matches,
  and a list stays within the character cap).
- Denial of service by volume: many concurrent calls. Run 1 sent 40 parallel calls on one connection, run 2 a
  burst of 20 calls with cancels and run 3 30 parallel calls at a limit of 2 s; nothing larger.
- Races across many concurrent connections: run 2 used only one connection per session, plus the 20-call cancel
  burst.
- Memory under many concurrent large calls: run 3 measured the server's peak working set for one large call at a
  time; its 30 parallel calls used small rows.
- The client side: Claude Desktop, other MCP clients and the MCP client code itself. The attack tests used their
  own JSON-RPC client.
- The host machine and the network between it and SQL Server. Run 3 stalled and throttled connections through a
  local proxy only: no real network fault (a cable pulled, a firewall drop, a TCP keepalive that expires), and no
  freeze longer than 45 s.
- Encrypted connections in run 3's proxy cases: they ran with `Encrypt=False`, so the proxy could see packet types.
- A Linux or macOS host for the attack tests (file lock and file mode behaviour): they ran on Windows only. The
  repository's test suites also run on Linux, in CI; nothing ran on macOS.
- The HTTP transport (the server offers stdio only) and `server/discover` on protocol revision 2026-07-28.

Audit log and process:

- An audit file that fails to write a line for a request whose parameters the MCP SDK cannot read (the X-1 kind).
- A full disk while the audit file is written.
- Removing a partial last audit line, the recovery step described in [The audit log](audit-log.md).
- Ctrl+C or a termination signal: whether calls in flight are cancelled, and the exit code 6 drain, also during a
  stalled connection (T-2).

Login check:

- Every securable class owned through a role: certificates, keys, Service Broker objects, full-text catalogs and
  assemblies.
- The owner of an external table, a CLR trigger or an edge constraint.

SQL text and values:

- Whether the SQL Server client library (Microsoft.Data.SqlClient) or SQL Server turns a lone surrogate into
  U+FFFD. Over stdio such text reaches neither: the MCP SDK rejects the request first.
- `sql_variant` edge values, and CLR types other than the test suite's one type; a large CLR value. Large `xml`
  values only in run 3's size cases, one call at a time, without showing whether the client library held a value
  whole.

Time limit and size cap:

- What the SQL Server client library does inside a synchronous read already under way, or while it cancels the
  command and closes the reader, when the time limit passes; the cause of the T-1 overruns was not investigated.
  The steps the limit does not stop are in [Commands and exit codes](reference.md#commands-and-exit-codes).
- The time limit passing during an `xml` cell, at the end of a schema tool's read, or during the step to a next
  result set: handled in code, no repository test. Run 3 measured the first once: on SQL Server 2019 the read of
  one 20,000,000-character `xml` text node ended 0.26 s past a limit of 30 s, as `Timeout` with no data.
- A SQL Server that itself stops answering when the command is cancelled; run 3 froze the network path instead
  (T-2).
- The time limit at which the startup check fails (T-4): startup at a limit of 1 s without the 10,000 extra
  objects was not run.
- Repeat counts: unless a finding says otherwise, each run 3 value was measured once per engine.
- One row of 4,096 cells, the code review's case for C-2; it was measured with 64.

Code security review:

- ScriptDom, Microsoft.Data.SqlClient and the MCP SDK were not decompiled. Their behaviour (whether ScriptDom
  visits every part of the syntax tree, the SqlClient per-read timeout and how it surfaces errors, the SDK's filter
  order and exception mapping) is taken from their documentation and from the repository's own comments and tests.
- Not reviewed: the SQL semantics of the permission check (reviewed in five earlier rounds),
  `docs/login-check.md`, `docs/sql-gate.md`, the test suites in full, the issue templates and the SVG files.
- An error raised in the middle of the login check's first result set: no test.

Scanners:

- semgrep parsed some lines only partly (about 99.8% of lines parsed): `src/MssqlReadOnlyMcp.Gate/GateVisitor.cs`
  line 242, `src/MssqlReadOnlyMcp/SqlGateway.cs` line 10, `src/MssqlReadOnlyMcp/ToolErrors.cs` line 15,
  `src/MssqlReadOnlyMcp/ToolService.cs` lines 18 to 23, 448 to 449 and 472, and the shell snippets at lines 85 and
  159 of `.github/workflows/release.yml`.
- semgrep skipped the 31 files under `tests/` (its default ignore patterns).
- trivy found one language-specific file, the npm lock file, and no configuration file it checks. The NuGet
  packages were checked by `dotnet list package` and osv-scanner only.
- CodeQL analyses the C# code and the workflow files; the PowerShell packaging script and the SQL login script are
  not among its languages.
- The dependency scans know only the advisories published on the day of the scan.
- The scanner versions are the ones installed on the maintainer's machine that day; trivy reported that version
  0.76.2 was available (it ran 0.75.0).
- The release archives and bundles: the release workflow has not built one yet, so none was scanned
  ([Releasing](releasing.md#not-yet-verified)).

## Repeat it

The scans, from the root of a clone at the commit you want to check; semgrep downloads the four rule sets from its
registry:

```powershell
gitleaks dir . --report-path gitleaks-tree.json
gitleaks git . --report-path gitleaks-history.json
actionlint
dotnet list package --vulnerable --include-transitive
semgrep scan --metrics=off --disable-version-check --config p/csharp --config p/secrets --config p/github-actions --config p/security-audit .
trivy fs --scanners vuln,secret,misconfig .
osv-scanner scan source -r .
cd packaging/mcpb
npm audit --package-lock-only
cd ../..
```

The test suites, as in [Building and testing](building-and-testing.md) (Docker and PowerShell 7 needed):

```powershell
dotnet build -c Release
dotnet test tests/MssqlReadOnlyMcp.Gate.Tests -c Release
dotnet test tests/MssqlReadOnlyMcp.Audit.Tests -c Release
dotnet test tests/MssqlReadOnlyMcp.IntegrationTests -c Release
$env:MSSQL_TEST_IMAGE = 'mcr.microsoft.com/mssql/server:2019-CU32-GDR13-ubuntu-20.04'
dotnet test tests/MssqlReadOnlyMcp.IntegrationTests -c Release
$env:MSSQL_TEST_IMAGE = 'mcr.microsoft.com/mssql/server:2025-CU9-GDR1-ubuntu-22.04'
dotnet test tests/MssqlReadOnlyMcp.IntegrationTests -c Release
```

The attack harness is not in this repository, so the attack tests cannot be repeated from it. Run 3 used the run 2
harness with one more scenario module and a local TCP proxy that stalls, holds or throttles the connection to SQL
Server. These tests in the repository cover the same promises, class by class:

- A: `RegressionTableTests.cs`, `StatementTypeTests.cs`, `GateRefusalTests.cs`, `ExtendedEventsTests.cs`,
  `DifferentialTests.cs`, `ParserVersusServerLexerTests.cs`, `ClrTypeMemberTests.cs`.
- B: `CapsTests.cs`, `CallLimitTests.cs`, `LargeValueTests.cs`.
- C: `GateRefusalTests.cs` (nothing changed after a refusal), `ValueTypeTests.cs` (session settings; text sent past
  the gate that writes fails with nothing changed).
- D and H: `StdioServerTests.cs`, among them
  `Call_whose_parameters_the_sdk_cannot_read_is_audited_before_its_error_answer`.
- E and E5: `PermissionCheckTests.cs`, and `StdioServerTests.cs` for the server start.
- F: the tests in `tests/MssqlReadOnlyMcp.Audit.Tests`, `AuditTests.cs` and `StdioServerTests.cs`.
- G: `ServerConfigTests.cs`, and `StdioServerTests.cs` (no password in any result, stderr line or audit file).

No repository test covers X-2, X-3, X-6 or X-7, nor X-5 beyond the database list
(`Reader_sees_in_sys_databases_a_database_it_has_no_user_in`). The other class H shapes were sent by the harness
only.

## Reporting a problem

Report a security problem privately, as [SECURITY.md](../SECURITY.md#reporting-a-problem) describes.

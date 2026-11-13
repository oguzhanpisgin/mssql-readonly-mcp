# Security testing

This page records the security tests run before the first release: what was attacked, with which tool and version,
on which commit and date, what came out, and what was not tested. The tests are the maintainer's own: the attack
tests and the code security review were run apart from the writing of the code and changed nothing in it. This is
not a third-party audit, not a penetration test by an outside firm, and not a certification. Every attack run, the
code security review and the scans took place on 2026-11-12, and the last test suites and a rescan of the working
tree with the npm overrides (trivy, osv-scanner, npm audit) on 2026-11-13; times below are UTC. Attack test run 5
tested `a38fd06`. The test suites of this record ran last on `eededa6`, which only removes an unused method and an
unused test counter from the code of `a38fd06`; run 5 did not test it.

| Tests | Public commit | Tree |
| --- | --- | --- |
| Attack test, run 1 | `dedf54ad73b7063126eaa2d9c9f5ffa648ad8dbc` | `25eb3c6d76a443425fecabe194a0a5d151bb8ec7` |
| Attack test, run 2; code security review; scans, round 1 | `46baefb610a775c04b2c7909fe7476a8142e2e1f` | `6ae8882be207c02a3487c7c0d190c772b8fb27d2` |
| Attack test, run 3; scans, round 2; test suites; CI; CodeQL | `7f98bfdcec9baf1d7aa0492f7804dec712988d1d` | `617d9dd554f6e9937f8418fd30748f1f248e7782` |
| Attack test, run 4; test suites | `12706a1fe29c3110188ee04a1b313665b104f335` | `87ed587062302f2bd8bc11df5a9ab470b0c687c4` |
| Attack test, run 5; test suites | `a38fd062c301937699e254826dbaad0b90e1c83d` | `0c5d6423cfb3787f6154e3427bfa73b29fe5c44d` |
| Test suites | `eededa69dac5ba8459d423487847cfdb171ecb48` | `3a84226272a09459d3613da021211367828448ff` |

Later commits change documentation and, on 2026-11-13, two files under `packaging/mcpb`: `package.json` and
`package-lock.json`, the npm overrides for the MCPB CLI that packs the bundles
([Dependency alerts](#dependency-alerts)). Later the same day they change how the audit file is opened (a second
server process on the same path writes a sibling file:
[The audit log](audit-log.md#several-server-processes-on-one-audit-path)), the texts of the connection-string
configuration errors, the texts of the bundle manifest, and the tests for them. To list in a clone what differs from
`eededa6` in the server source, the tests, the scripts, the workflows and `packaging`:

```powershell
git diff --stat eededa6 HEAD -- src tests scripts packaging .github/workflows
```

At v0.1.0 it lists 13 files: `packaging/mcpb/` `manifest.json`, `package-lock.json`, `package.json`;
`src/MssqlReadOnlyMcp.Audit/` `AuditLog.cs`, `AuditWriteException.cs`; `src/MssqlReadOnlyMcp/` `Program.cs`,
`ServerConfig.cs`, `ServerStartup.cs`; `tests/MssqlReadOnlyMcp.Audit.Tests/AuditSiblingTests.cs`;
`tests/MssqlReadOnlyMcp.Gate.Tests/` `AuditSiblingProcessTests.cs`, `ServerConfigMessageTests.cs`,
`ServerProcess.cs`; `tests/MssqlReadOnlyMcp.IntegrationTests/StdioServerTests.cs`. After v0.1.0 the bundle icon brought
two more files into this list: it added `packaging/mcpb/icon.png`, the image, and changed `packaging/build-release.ps1`,
whose bundle step now copies it into each bundle. It also added the `icon` line to `manifest.json`, already listed, and
changed no file under `src/` or `tests/`. With it the command lists 15 files. Any other file means the code differs from
what this page describes.

The attack test runs of this page were not repeated on that later code, and the scans listed here were not re-run on
it, except CodeQL, which runs on every push to `main` and every pull request, and gitleaks, which on 2026-11-13,
before the push, found no leaks in the commit that published that code and in its archived tree. What covered it:
three rounds of independent read-only review that asked first whether a line can be written to a file whose lock is
not held, whether a file that does not verify can be skipped, whether an error other than "lock held" can move to
the next file, and whether any part of the connection string reaches a message; and, on 2026-11-13, locally on
Windows and in CI on Linux, the unit tests (Gate 387, Audit 86) and the integration tests (259 on each of the three
images).

Times: run 1, server built at 03:56; run 2, 05:59 to 07:58; run 3, 08:08 to 11:18; run 4, 15:43 to 20:00; run 5,
22:16 to 23:59; code security review, 06:08; scans, round 1 at 05:59 and round 2 at 08:05; test suites, on
`7f98bfd` finished at 07:05, on `12706a1` 15:17 to 15:30, on `a38fd06` 21:04 to 21:15, on `eededa6` 00:13 to 00:24
on 2026-11-13 (its Release build with warnings as errors at 05:44), the reruns of two U-1 tests 00:25 to 00:32 (on
`a38fd06` and `eededa6`), and of the unstable one alone on `a38fd06` 00:32 to 00:36; CI and CodeQL results read at
07:09. The scans, CI and CodeQL rows below are of `7f98bfd`, except the three scan rows dated 2026-11-13, which are
of a working tree with the code of `eededa6` and the npm overrides in `packaging/mcpb` (see
[Dependency alerts](#dependency-alerts)).

## What was tested

| Test | Tool and version | Scope | Result |
| --- | --- | --- | --- |
| Attack test, run 1 | Own attack harness: JSON-RPC over stdio to the built server (not in this repository; no version) | `dedf54a`; SQL Server 2022 every class, 2019 and 2025 classes A and C | No gate bypass, no change to database state, no password in any output; 2 LOW and 5 NOTE findings (X-1 to X-7) |
| Attack test, run 2 | The same harness, with classes H and E5 added | `46baefb`; SQL Server 2019, 2022 and 2025, every class | 584 outcome rows per engine, with the same counts and the same pass and miss results on all three; outcomes differ only where the engine does (A-198, A-205, A-224, A-236); 5 expectation misses, each explained; X-1 fixed; 1 new NOTE (R-1); no tool ran without an audit line |
| Attack test, run 3 | The run 2 harness, with one more scenario module (R3) and a local TCP proxy | `7f98bfd`; SQL Server 2019, 2022 and 2025, every class of run 2 plus R3, the paths changed after run 2 (time limit per call, size cap) | 788 outcome rows per engine: the 584 of run 2, none different from run 2, and 204 R3 rows, of which 199, 198 and 196 passed; X-1 still fixed; 2 LOW and 3 NOTE findings (T-1 to T-5); no call answered before its audit line was on disk |
| Attack test, run 4 | The run 3 harness and proxy, with one more scenario module (R4) that freezes or throttles one connection at a chosen request type or byte count and reads the reader's sessions, transactions and locks as an administrator, and a console-event signaller for Ctrl+C and Ctrl+Break; run by two tester lanes | `12706a1`; SQL Server 2019, 2022 and 2025: every class of run 3, and the changed-behaviour cases (freeze points, state at SQL Server after the answer, late sends, repeated calls, many frozen calls, pool clear, client cancel, ending the process, break attempts) | 788 outcome rows per engine, of which 779 (2022), 778 (2019) and 780 (2025) passed; against run 3, 6, 9 and 6 rows different, all in R3; X-1 still fixed; T-1 and T-3 resolved, T-2 resolved for single calls; 1 MEDIUM (U-1), 1 LOW (U-2) and 4 NOTE (N-1 to N-4) findings; no call was answered before its audit line was on disk |
| Attack test, run 5 | The run 4 harness, proxy and signaller; the proxy also records the client's and SQL Server's socket events, and one more scenario module (R5) runs variants of the U-1 cases, each polled as an administrator until the call's sessions are gone, and attempts to break the fix | `a38fd06`, and `12706a1` built and run the same way for the U-1 variants and the break attempts; SQL Server 2019, 2022 and 2025: the U-1 cases and variants (frozen, throttled, cancelled, cut, 120 calls frozen at once, the schema tools), the freeze points, client cancel, run 3's stalled and throttled connection rows, repeated calls, 60 calls frozen in the middle of a row, the pool clear, ending the process, and attempts to break the fix; not the classes of runs 1 to 3 | U-1 fixed for every frozen-link, cancel, cut and schema-tool case measured, on the three engines: no session of the call 0.2 to 0.7 s after the release, where `12706a1` still had it 60 s after. On a link throttled to 5 KB/s, 8 of 27 single calls and 21 of 450 repeated calls still left a session with an open read transaction and a lock: 1 MEDIUM (U-1R). U-2 unchanged; 4 NOTE (notes 1 to 4 of run 5); every call row had exactly one audit line at its answer, and no `Timeout` answer carried data |
| Code security review | Reading of the source at the commit; no build, no run, no container | `46baefb`: every file under `src/`, read in full (the SQL semantics of the permission check excepted); the build files, workflows, Dependabot settings, packaging and the login script; the docs that state promises and limits; tests only searched for specific cases | 1 MEDIUM, 3 LOW, 6 NOTE (C-1 to C-10); no path found where a write, DDL, EXEC or second statement passes the gate, where data returns before its audit line, or where a password or the connection string reaches the client, stderr or the audit file |
| Secret scan, files | gitleaks 8.30.1, `gitleaks dir` | `7f98bfd`, the files of the checkout | No leaks found |
| Secret scan, history | gitleaks 8.30.1, `gitleaks git` | 12 commits: `main` up to `7f98bfd` and four Dependabot branches | No leaks found |
| Workflow check | actionlint 1.7.12 | `7f98bfd`, `.github/workflows` | No output, exit 0 |
| Vulnerable NuGet packages | `dotnet list package --vulnerable --include-transitive`, .NET SDK 10.0.404 | `7f98bfd`, the six projects, source nuget.org | No vulnerable package in any of the six projects |
| Static analysis | semgrep 1.180.0, rule sets `p/csharp`, `p/secrets`, `p/github-actions`, `p/security-audit` | `7f98bfd`, the 63 files tracked by git outside `tests/` | 0 findings; 78 rules run; about 99.8% of lines parsed (see "What was not tested") |
| File system scan | trivy 0.75.0, scanners `vuln`, `secret`, `misconfig` | `7f98bfd` | 2 vulnerabilities, both in `packaging/mcpb/package-lock.json`: `tmp` 0.0.33, CVE-2026-44705 HIGH and CVE-2025-54798 LOW; no secret and no misconfiguration reported |
| Dependency scan | osv-scanner 2.6.0 (osv-scalibr 0.5.2), `scan source -r` | `7f98bfd`: `packaging/mcpb/package-lock.json` (55 packages), `Directory.Build.props` and the six project files | NuGet: none. npm, in `packaging/mcpb/package-lock.json`: `tmp` 0.0.33 GHSA-52f5-9888-hmc6 (CVSS 2.5) and GHSA-ph9p-34f9-6g65 (7.7), `zod` 3.25.76 GHSA-mrhx-c7xc-m3w5 (8.2) |
| npm packages | `npm audit --package-lock-only`, npm 12.2.2 | `7f98bfd`, `packaging/mcpb/package-lock.json` | 6 vulnerable packages (3 low, 3 high): `tmp` and `zod`, each "No fix available", and four packages that depend on them, the MCPB CLI among them |
| File system scan, 2026-11-13 | trivy 0.75.0 (database of 2026-11-13), scanners `vuln`, `secret`, `misconfig` | A working tree with the code of `eededa6`, the npm overrides `tmp` 0.2.7 and `zod` 4.6.7 in `packaging/mcpb/package.json` and the lock file regenerated | 0 vulnerabilities in `packaging/mcpb/package-lock.json`; no secret and no misconfiguration reported. trivy did not report the `zod` advisory on the old lock file either (row of `7f98bfd` above, and on 2026-11-13 with the same database), so this zero says nothing about `zod`; npm audit and osv-scanner below do |
| Dependency scan, 2026-11-13 | osv-scanner 2.6.0 (osv-scalibr 0.5.2), `scan source -r` | The same working tree: `packaging/mcpb/package-lock.json` (54 packages), `Directory.Build.props` and the six project files | No issues found |
| npm packages, 2026-11-13 | `npm audit --package-lock-only`, npm 12.2.2 | The same working tree, `packaging/mcpb/package-lock.json` | 0 vulnerabilities |
| Test suites | `dotnet build`, `dotnet test` (SDK version not recorded in the run log) | `7f98bfd`; integration tests on the SQL Server 2022-CU27, 2019-CU32-GDR13 and 2025-CU9-GDR1 images | Build: 0 warnings, 0 errors. Passed, 0 failed: Gate 364, Audit 55; Integration 245 on each of the three images |
| Test suites | `dotnet build`, `dotnet test` (SDK version not recorded in the run log) | `12706a1`; integration tests on the same three images | Build: 0 warnings, 0 errors. Passed, 0 failed: Gate 364, Audit 55; Integration 255 on each of the three images |
| Test suites | `dotnet build`, `dotnet test` (SDK version not recorded in the run log); a Release build with warnings as errors | `a38fd06`; integration tests on the same three images, on Windows | Build: 0 warnings, 0 errors; Release build with warnings as errors: 0 warnings, 0 errors. Passed, 0 failed: Gate 367, Audit 55; Integration 259 on each of the three images, in this run; one of its U-1 tests failed later in 2 of 13 runs outside the full suite on this code (next row and U-1R) |
| Test suites | `dotnet build`, `dotnet test`; a Release build with warnings as errors | `eededa6`, which only removes an unused method and an unused test counter from the code of `a38fd06`; integration tests on the same three images, on Windows | Build: 0 warnings, 0 errors; Release build with warnings as errors: 0 warnings, 0 errors. Gate 367 and Audit 55 passed, 0 failed; Integration 259 of 259 passed on 2019 and 2025, and 258 of 259 on 2022: `Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only` failed. That test is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2 of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the test of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was captured. That it fails for the same reason as U-1R (the fix looks at the connection once, before the close) is reasoned, not proven. It can fail in a CI run or a local run. |
| CI | GitHub Actions workflow `ci` (build, unit tests, integration tests on the three images) | `7f98bfd` | Run 43172714768: success |
| Code scanning | CodeQL through `github/codeql-action` v4.39.3, languages `csharp` and `actions` (CodeQL version not recorded) | `7f98bfd` | Run 43172714825: success; open code scanning alerts: 0 |

Scans, round 1, on `46baefb` with the same tools and versions gave the same results; gitleaks read 10 commits there.
npm audit ran on `7f98bfd` and, on 2026-11-13, on the lock file with the overrides.

## Attack tests

The harness starts the built server as a child process and sends it JSON-RPC requests over stdio. SQL Server runs
in a local container, one at a time in runs 1 to 3 (runs 4 and 5: below), with a test database, decoy objects and a
second database. The reader login is made by `scripts/create-readonly-login.sql`, unchanged. Passwords are generated at
run time and not written to disk; after each run every output file was searched for them, with 0 hits. The engines were
the images named in the README [prerequisites](../README.md#1-prerequisites): `@@VERSION` 15.0.4510.2 (2019),
16.0.4295.3 (2022) and 17.0.5015.4 (2025).

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
| Total | | | 584; run 3 adds 204 rows for the changed paths (R3), 788; run 4 the same 788 on each engine, plus its changed-behaviour cases (R4) |

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

Run 4 retested the time limit per call on `12706a1`, the change that answers a call at its time limit
(T-1, T-2, T-3), from 15:43 to 20:00. It used the images of run 3, with the same `@@VERSION`, but not one engine
container at a time: two tester lanes shared one machine. The whole run 3 set ran on 2022 alone (15:44 to 16:39), and
on 2019 and 2025 in the second lane while the first lane ran the changed-behaviour cases on another engine. Every
changed-behaviour row ran while another engine's run was going, except the repeats run alone on 2019 and 2025 from
19:48 to 19:58: the healthy heavy rows after the answer, and 30, 60 and 120 calls frozen in the middle of a row and
60 at the rollback. Once, the first lane stopped a server process of the second lane by mistake; the row it hit was
run again alone on a fresh container. Each engine gave 788 outcome rows from the run 3 set; the changed-behaviour
cases (R4) gave 242 rows on 2022.

- Compared field by field with run 3 on the same engine: 782 rows the same and 6 different on 2022, 779 and 9 on
  2019, 782 and 6 on 2025. No row was present in one run only, and every row of classes A to H, E5 and the audit
  checks was the same. Every difference is in R3:
  - by this change: the frozen pooled connection at limits of 2 and 5 s, `Timeout` at 2,504 to 2,516 ms and 5,500
    to 5,513 ms (run 3: 7.0 s, 10.0 s, or no answer within 45 s); on 2022 and 2025, the next call after its release
    found one audit line at its answer (run 3: two); the held rollback, `Timeout` with no data at 3,525 to 3,539 ms
    (run 3: 2 rows after about 20 s); heavy rows at a limit of 10 s on 2019 and 2025 and at 5 s (run 1) on 2025,
    `Timeout` at 10,536 to 10,552 ms and 5,523 ms (run 3: overruns, see T-1);
  - an `ok` answer from a call whose work ended inside the 0.5 s grace: on 2019, 200 rows of 64 binary cells of
    1 MiB at a limit of 5 s returned 23 rows cut at the character cap at 5,135 ms, the call's work having ended
    103 ms after the limit (two repeats ended inside the limit, at 4,627 and 4,768 ms); in a burst of 30 calls at a
    limit of 2 s, 1 (2022), 2 (2019) and 3 (2025) calls returned `ok` with the column names and no rows, the others
    `Timeout`;
  - timing only: on 2019, the read of one 20,000,000-character `xml` text node at a limit of 30 s returned the
    value cut (`long_value`) at 25,096 ms (two repeats: 20,412 and 18,235 ms), where run 3 ended it as `Timeout` at
    30,260 ms.
- Case expectations that encoded the old behaviour: heavy rows at a limit of 5 s (2022 runs 2 and 3, 2019 runs 1
  to 3, 2025 runs 2 and 3) and 10 s (2022, and two 2019 repeats) were answered `Timeout` at 5,533 to 5,550 ms and
  10,522 to 10,536 ms, with no data and one audit line, and failed only the harness check 1.5 s after the answer,
  which still expected the session to have ended before the answer: an administrator saw the reader's request and
  open transaction then. Repeated on fresh containers, the 2019 and 2025 rows at 5 s passed, the session gone within
  1.5 s. The 2022 row at 10 s and the 2025 rows had also failed in run 3, on overrun.
- The known expectation misses of run 3 came again on each engine: A-003 and A-073 (X-4), C-001 (X-3), D-054, F-006
  (R-1) and the start row of T-4; and, on 2019, the `describe_table` row under a held lock, `Timeout` at 8,050 ms
  (run 3: 8,078 ms).
- The five request shapes of X-1 each got -32603, one `InvalidRequest` audit line before the answer, and ran no
  tool; `verify-audit` exited 0 on each engine. X-1 stays fixed.
- The changed-behaviour cases, once per engine unless repeats are given:
  - freeze points, limit 3 s: a call frozen at the session settings, the transaction start, the query, the first
    row, in the middle of a row (`sql_variant`, `nvarchar(max)`, `xml`, `varbinary`), between rows or at the
    rollback, and `list_tables` and `describe_table` frozen at each of their points after the open, was answered
    `Timeout` with no data and one audit line at the limit plus 0.50 to 0.52 s; a call or a schema tool frozen at the
    connection open, at the limit plus 0.03 to 0.05 s. The exception: a truncated result whose reader close froze
    returned `ok` with the 97 rows it had read before the limit, at 3,041 to 3,045 ms (U-1);
  - every case of run 3's slow-row, heavy-query and lock table (limits 2 to 120 s) that ended as `Timeout` did so
    at most 0.56 s past the limit (run 3: up to about 5 s); single frozen, held or throttled connections ended at
    2,496 to 2,516 ms at a limit of 2 s, 3,525 to 3,539 ms at 3 s and 5,500 to 5,513 ms at 5 s;
  - what SQL Server kept after the answer, read by an administrator about every second: healthy heavy rows at a
    limit of 5 s, in one run per engine, left nothing at the answer on 2022 and 2025; on 2019 the abandoned session
    ended 1.8 s after the answer (alone: 0.9 s), and a writer on the same rows waited 1,565 ms (alone: 762 ms); in
    the other heavy-row runs the request and open transaction were still there 1.5 s after the answer (the case
    expectations above). A connection frozen and released after 20 s, or cut, kept the reader's request, open
    transaction and locks until the release or the cut, and a writer was blocked from about 4.5 s until then; with the
    rollback held, the transaction stayed open until the release. After a throttled link (5 KB/s for 60 s) and after a
    truncated result whose connection froze, the session stayed after the release (U-1);
  - late sends: with one client packet (login, session settings, transaction start or query) held past the
    deadline and released 6 to 8 s into the call, the client sent no new packet after the deadline, and the query
    never ran after it on any engine: no Extended Events start event and no plan-cache entry with the call's marker. The
    held packets reached the server at the release; they had been sent before the deadline;
  - repeated calls, 50 in a row at a limit of 2 s, each on a frozen link: all 50 `Timeout`, one audit line each, no
    data. With the default pool and with `Pooling=false` the reader's sessions grew by one per call, and the answers
    came at a median of 2,505 to 2,510 ms and at most 2,553 ms. With `Max Pool Size=5` they stopped at 5; from the
    sixth call each call waited for the pool and was answered `Timeout: SQL Server error -2, class 11, line 0` after
    about 2.0 s. The same on a throttled link. After the release: no session of the reader, and the next call
    returned `ok` in 36 ms;
  - many calls frozen at once, limit 5 s, `Max Pool Size=500` unless noted: U-2. Calls frozen at the rollback
    (30, 60 and 120) were answered within 5,649 ms. Calls frozen in the middle of a row, median and maximum answer
    times: with 30, 5,608 and 5,616 ms (2022), 5,539 and 5,593 ms (2019), 5,998 and 7,522 ms (2025); with 60,
    10,199 and 10,203 ms (2022; repeat 10,222 and 10,225 ms), 11,163 and 11,166 ms (2019), 8,165 and 8,170 ms (2025);
    with 120, 29,830 and 29,841 ms (2022), 28,788 and 28,798 ms (2019), 8,562 and 8,599 ms (2025); with 120 at the
    default `Max Pool Size` (100), 20,374, 20,281 and 10,396 ms median. Alone (2019 and 2025): with 30, 6,727 and
    6,733 ms (2019, 23 of 30 past the limit plus 1 s) and 5,520 and 5,595 ms (2025); with 60, 11,811 and 10,363 ms
    median; with 120, 30,153 and 10,810 ms; with 120 at the default pool, 20,270 and 10,703 ms. During the mid-row
    freezes, a healthy one-row call sent 1.5 s in returned `ok` after 3,713 to 28,656 ms on 2022 and 2019, and on 2025
    `ok` after 3,641 ms with 30 calls alone, otherwise `Timeout` after 6,014 to 9,187 ms. On 2025, only 33 of 60 or 120
    calls in the parallel runs, and 47 to 49 alone, had reached the freeze point by 3 s. The server's threads came back
    to about 13 after the release; its handles stayed at about 1,000 to 1,400 for 25 to 45 s (443 at the start;
    N-2);
  - the pool clear with other calls running, limit 8 s: one call frozen in the middle of a row while ten healthy
    calls ran on the same pool, ended by its limit (`Timeout`) or by a client cancel (`Cancelled`): all ten
    returned `ok` with the right rows and result hashes, and no session of the reader was left, on the three
    engines;
  - client cancel at each of the 22 freeze points, limit 30 s: the request got no answer, and its audit line read
    `Cancelled`, 500 to 517 ms after the cancel; frozen at the connection open, at the cancel;
  - ending the process, limit 5 s, three calls frozen in the middle of a row: input closed while they were still
    unanswered, exit 0 about 4.55 s later, with their three `Timeout` audit lines and no answer written (N-3);
    input closed after their answers, with their clean-ups still running, exit 0 in 18 to 22 ms; Ctrl+C when idle,
    exit 0 in 92 to 101 ms; Ctrl+C or Ctrl+Break while they were frozen and unanswered, exit 0 in 554 to 569 ms,
    with three `Cancelled` audit lines and no answer; Ctrl+C while their clean-ups ran, exit 0 in 86 to 147 ms;
    `TerminateProcess` while they were frozen, exit -1 in 10 to 12 ms, no audit line, the sessions held until the
    link healed (N-4). Ctrl+C and Ctrl+Break were sent as Windows console events. Two attempts at exit 6 ended 0
    (N-1): 120 calls frozen in the middle of a row at a limit of 2 s with input closed at once (exit after 2.6, 6.2
    and 7.2 s, 120 lines); and output not read until the pipe was full, then input closed, which kept the process
    running at 30 s and ended 0 once the output was read again;
  - attempts to break the change: no `Timeout` or `Cancelled` answer or audit line carried data (0 of more than
    1,000 calls); no call without its audit line or with two; no query ran after its deadline; starving the pool
    (`Max Pool Size=5`, 50 frozen calls) made later calls `Timeout` at the pool wait of about 2 s, and everything
    recovered after the release. Leaving a transaction open after the call succeeded (U-1), and so did delaying
    other calls with many frozen calls (U-2). Exit 6 or a hang at shutdown was not reached.
- Every server session closed with exit 0, except the `TerminateProcess` case (exit -1, no lines, as expected) and
  the session stopped by mistake; `verify-audit` exited 0 on every audit file, and call lines equalled calls. The
  report gives no session count.
- No finding of severity CRITICAL or HIGH. No call returned data produced after its limit plus the 0.5 s grace, no
  call was answered without exactly one audit line on disk before it, no query ran after its deadline, and no
  process crashed. Calls frozen when input closed or when `TerminateProcess` ran got no answer (N-3, N-4).

State of the earlier findings on `12706a1`: X-1 still fixed on the three engines; X-2, X-3, X-5 and X-7
unchanged; X-4 unchanged and documented; X-6 unchanged and accepted. R-1: F-006 again gave `InvalidRequest` with
`sql` null; the harness still expects U+FFFD, and the docs say null. T-1 resolved, T-2 resolved for single calls,
T-3 reversed as intended, T-4 measured further and T-5 unchanged (Findings below).

Run 5 retested U-1 on `a38fd06`, the fix of U-1, from 22:16 to 23:59, with the images and `@@VERSION` of run 4.
For comparison, `12706a1` was built and run the same way for the U-1 variants and the break attempts, polled for 60 s
after the release (the fix: until the call's sessions were gone, at most 180 s). Every row ran while one or two other
engines' runs were going; the 60-call freeze in the middle of a row and run 4's cases of state at SQL Server after
the answer were repeated alone, one engine at a time. Times below are given for 2022, 2019 and 2025, in that order.

- U-1 on a frozen link, after a cancel, after a cut and for the schema tools, three engines: no session of the call
  was left after the release. Time from the release until no session of the reader held a transaction, a lock or a
  request and the call's sessions were gone:
  - a truncated result (`ok`, 97 rows) whose connection then froze, released 5 s later: 297, 271 and 234 ms;
    released 20 s later: 263, 334 and 307 ms. On `12706a1` the session, with its transaction and lock, was still
    there 60 s after the release, and the client never closed the connection;
  - 120 calls frozen in the middle of a row (limit 5 s, `Max Pool Size=500`): 538, 654 and 517 ms; the client closed
    every connection the proxy marked (117, 119 and 39). On `12706a1`, 19, 2 and 3 of them were still open 60 s
    after the release;
  - one call frozen in the middle of a row, released 20 s later: 535, 431 and 390 ms (`12706a1`: 312, 394 and
    305 ms);
  - a client cancel 1.5 s into the call: in the middle of a row, 344, 318 and 476 ms (`12706a1`: clean); on a
    truncated result whose connection then froze, 332, 427 and 381 ms (`12706a1`: the session still there 60 s after
    the release); with the rollback held, 236, 221 and 218 ms (`12706a1`: clean);
  - in the cases above with one call, a writer on the same rows got through 0.21 to 0.36 s after the release (with
    the rollback held it was not blocked), and the proxy saw the client close the connection about 0.2 to 0.3 s
    after the release;
  - `describe_table` and `list_tables` frozen in the middle of their result: `Timeout` at about 3.51 s, no
    transaction, lock or request while frozen, and no session left;
  - run 4's truncated-result cases (cold, warm, `Max Pool Size=1`, and five calls): no session 1, 5, 15 and 30 s
    after the release, and for the five calls also 60, 120 and 180 s after; a schema change on the table as an
    administrator succeeded. Run 4's cases of a connection frozen and then released or cut after 20 s, and of a
    truncated result whose connection then froze: no session left at the next call, in parallel runs and alone.
- U-1R, a `Timeout` on a link throttled to 5 KB/s for 60 s, limit 3 s: the call was answered `Timeout` with one audit
  line and no data, but in 8 of 27 single calls the session stayed: in the form of run 4's case, 7 of 15 (3 of 3 and
  2 of 9 in parallel runs, 2 of 3 alone, on 2019 and 2025); in the form of run 5's variant, 1 of 12 (2022). In those
  calls the proxy saw no close from the client; the reader's request ran (`ASYNC_NETWORK_IO`) and blocked a writer on
  the same rows until the throttle ended, 58.3 to 60.3 s, and then the session stayed `sleeping` with an open
  transaction and one lock; in the form of run 4's case it was still there 20 s after the release, where that
  observation ended, and after the next call. In
  the other calls the client closed the connection 5.9 to 6.2 s into the call and the writer got through at about
  6.1 s. On `12706a1`, run 5's variant left the session in 3 of 3 calls, with its transaction and lock 60 s after
  the release. Of 450 repeated calls on a throttled link (below), 21 did the same (`12706a1`, run 4: 210 of 450).
- Timing against run 4: row by row, the same answer kind, reason and audit reason in 153 of 154 rows (2022), 122 of
  122 (2019) and 121 of 122 (2025); the one difference is the healthy heavy row (note 2 below). The freeze points
  answered `Timeout` at 3,497 to 3,527 ms (at the connection open, 3,029 to 3,045 ms); a truncated result whose reader
  close froze returned `ok` with 97 rows at 3,036 to 3,045 ms; a client cancel at each freeze point (limit 30 s) was
  audited 501 to 517 ms after the cancel (at the connection open, 9 to 11 ms before the harness's cancel time); run
  3's open stalls ended at 1,997 to 5,043 ms, the frozen pooled connection at 2,500 to 2,510 ms (limit 2 s) and 5,508
  to 5,516 ms (5 s), the held transaction start and rollback at 3,535 to 3,540 and 3,520 to 3,535 ms, the throttled
  links at 2,506 to 2,518 ms.
- Repeated calls, 50 in a row at a limit of 2 s, with the default pool, `Max Pool Size=5` and `Pooling=false`, once
  per engine: all `Timeout`, no data, median answers 2,506 to 2,509 ms and maxima 2,521 to 2,538 ms (at
  `Max Pool Size=5` on a frozen link, median 2,005 to 2,010 ms). On a frozen link the sessions grew by one per call
  with the default pool and with `Pooling=false`; with `Max Pool Size=5` they stayed at 5 on 2019 and 2025 and
  reached 12 on 2022; 45 s after the release none held an open transaction. On a throttled link they did not grow
  by one per call (at most 8 at any sample), and 45 s after the release 2, 3 and 2 (2022), 2, 2 and 0 (2019), and 3,
  5 and 2 (2025) sessions per pool setting still held an open transaction and a lock (U-1R; run 4: 21 to 25 each).
  The next call after the release returned `ok` in 14 to 55 ms.
- The pool clear with ten healthy calls running, limit 8 s: 10 of 10 returned the same data with one `Allowed` line
  each, on the three engines; the frozen call answered `Timeout` at 8,014 to 8,018 ms, or was audited `Cancelled` at
  6,510 to 6,520 ms after a client cancel.
- Ending the process, three calls frozen in the middle of a row: input closed while they were unanswered, exit 0
  after 4,552 to 4,562 ms with three `Timeout` lines; after their answers, 17 to 22 ms; Ctrl+C idle 90 to 94 ms,
  while they were frozen 544 to 560 ms, while their clean-ups ran 91 to 145 ms; Ctrl+Break while frozen 543 to
  553 ms; `TerminateProcess` while frozen, exit -1 after 9 to 10 ms. After the exit with the freeze still held, SQL
  Server showed 3 requests, 3 open transactions and 9 locks, gone 773 to 787 ms after the release.
- Many calls frozen at once (U-2), limit 5 s: with 60 in the middle of a row, median answers 10,794, 12,149 and
  10,756 ms in parallel runs and 10,180, 10,187 and 10,543 ms alone; the healthy call returned `ok` after 9,292 ms
  (alone 8,673 ms) on 2022 and 10,646 ms (alone 8,691 ms) on 2019, and got `Timeout` after 6,185 ms (alone 6,163 ms)
  on 2025. With 120 (the U-1 case above), median answers 27.3, 20.3 and 10.1 s.
- Attempts to break the fix, on both commits: with 3 to 4 idle pooled sessions and one call broken (a truncated
  result, a cut in the middle of a row, a `Timeout` in the middle of a row), 8 of 8 healthy calls during its
  clean-up returned `ok`, then 10 in a row and 10 in parallel; 0 of the 3 to 4 idle pooled sessions survived the
  break, on `12706a1` as well (note 3); no busy session 2 s and 32 s after the break on `a38fd06` (on `12706a1` the
  truncated result's session was there both times, on the three engines); no call row with two audit lines or none
  (0 of 576 on `a38fd06`); no `Timeout` with data; no crash in 312 process ends (45 on `12706a1`). Thirty breaks on
  one process, each released, then a check: threads 19, then 9 after 30 s; handles from 472 to 474 at the start to
  651 to 667, `12706a1` from 469 to 645 to 678 (note 4); no session left after 30 s, and the client closed all 30
  connections.
- Every call row had exactly one audit line on disk at its answer: 164, 164 and 168 call rows, plus 4 and 4 for the
  schema tools on 2022 and 2019; no `Timeout` answer carried data; `verify-audit` exited 0 on every process (96, 96
  and 98); every server process exited 0 when its input closed (89, 89 and 91), except the `TerminateProcess` rows
  (exit -1). The passwords were found in no output file of the 20 runs.
- Notes of run 5 (NOTE; not the N-1 to N-4 of run 4): 1, the truncated result answered `ok` with 97 rows at 3,015 to
  3,111 ms with one `Allowed` line, as in run 4; the harness still expected `Timeout`, so these rows count as failed
  in its summaries (8 to 12 per engine), and the rule of T-3 accepts them. 2, the healthy heavy row (limit 5 s, no
  proxy) returned `ok` with 19 rows at 4,683 to 4,998 ms on 2022 and 2025, and `Timeout` at 5,505 to 5,520 ms on 2019
  and at 5,026 ms on 2025 alone (run 4: `Timeout` on the three); no data came after the limit. 3, after a broken call
  none of the idle pooled sessions survived, on `12706a1` as well; healthy calls were not affected, and the cost is
  one new login per pooled connection. 4, after 30 breaks the handle count stayed higher (above), the same on
  `12706a1`; whether it levels off was not measured.
- No finding of severity CRITICAL or HIGH; 1 MEDIUM (U-1R), U-2 (LOW) unchanged, and the 4 notes.

## Findings

Severity is the tester's or reviewer's: CRITICAL, HIGH, MEDIUM, LOW or NOTE. The attack tests and the code security
review made no finding of severity HIGH or above, and three of severity MEDIUM (C-1, U-1 and U-1R). The dependency
scans of 2026-11-12 reported three advisories, two of them HIGH, in packages under the build-time packaging tool;
since 2026-11-13 its lock file resolves versions outside their ranges ([Dependency alerts](#dependency-alerts) below).

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
| T-1 | LOW | Run 3, healthy connection: a call stopped by its time limit ended up to about 5 s past it. Rows about every 2.3 s, limit 5 s: 8,295, 8,238 and 8,474 ms on SQL Server 2025 (3 of 3); on 2022 5,043 to 5,066 ms in the clean run (3 runs) but 8,220 to 9,755 ms in 4 of 5 earlier measurements; at a limit of 10 s, 15,000 ms (2022), 13,266 ms (2019) and 12,503 ms (2025), once each. Rows about every 1 s on 2025: 3,015 ms at a limit of 2 s and 6,107 ms at 5 s, once each. Each answer was `Timeout` with no data, its audit line was on disk first, and the session was left clean. The docs said 0.1 to 0.3 s past the limit. The cause was not investigated | On `7f98bfd`: open, measured and documented. Changed in `12706a1`: a call that has not ended 0.5 s after its limit, its clean-up included, is answered and audited as `Timeout` with no data (audited `Cancelled`, with no answer, after a client cancel), and its clean-up goes on at SQL Server after the answer; repository test `Healthy_link_heavy_rows_answer_Timeout_at_the_limit_three_times_and_the_server_is_clean_afterwards`. Run 4, three engines: every `Timeout` of run 3's slow-row, heavy-query and lock cases came at most 0.56 s past the limit; heavy rows at a limit of 5 s at 5,066 to 5,550 ms (three runs each) and at 10 s at 10,536 to 10,552 ms (once each). Resolved. Many calls frozen at once in the middle of a row still delay answers (U-2). [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| T-2 | LOW | Run 3, stalled or slow established connection: the time limit did not bound the call. A frozen pooled connection ended 5 s past the limit or later: 7,017 to 7,033 ms at a limit of 2 s on the three engines (once each); at a limit of 5 s, 10,038 ms (2019) and 10,029 ms (2022, earlier round), and on 2022 and 2025 the call was held until the network was released after 45 s, with no answer and no audit line before then (2 of 4 measurements). With the link throttled to 20 KB/s, 23,796 to 25,104 ms at a limit of 2 s, once on each engine. Holds at the transaction start and the rollback lasted until release, about 20 s, as the docs say for those steps. Calls on fresh connections answered meanwhile (38 to 153 ms) | On `7f98bfd`: open, measured and documented. Changed in `12706a1`, as for T-1: the call is answered at its cutoff, 0.5 s after the limit, also with its connection frozen or throttled or its transaction start or rollback held; the abandoned session stays at SQL Server until its clean-up ends; repository tests `Frozen_established_connection_is_answered_as_Timeout_at_the_limit_and_cleaned_up_after_release`, `Throttled_link_answers_Timeout_at_the_limit_plus_grace`, `Call_whose_transaction_start_is_held_past_the_limit_answers_Timeout_and_never_sends_its_query`, `Schema_calls_on_a_frozen_connection_answer_Timeout_at_the_limit_plus_grace`. Run 4, three engines, once each: a frozen pooled connection answered `Timeout` at 2,504 to 2,516 ms (limit 2 s) and 5,500 to 5,513 ms (5 s), a held transaction start at 3,525 to 3,539 ms (3 s), a link throttled to 20 or 5 KB/s at 2,496 to 2,512 ms (2 s), and every freeze point of an established connection at the limit plus 0.50 to 0.52 s, except the truncated close, which answered `ok` (U-1); frozen at the connection open, at the limit plus 0.03 to 0.05 s. Resolved for single calls. On `12706a1` the session of such a call could stay at SQL Server after the release (U-1; the fix in `a38fd06` and the cases measured for it: the U-1 row); many calls frozen at once in the middle of a row delay answers (U-2). [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| T-3 | NOTE | Run 3: a call whose rollback was held for about 20 s at a limit of 3 s returned its 2 rows after 20,141 to 20,264 ms, with one `Allowed` audit line, on the three engines (once each); the reader's transaction stayed open during the hold | On `7f98bfd` this matched the documented rule then: a call that finished reading before the limit kept its answer. Rule changed in `12706a1`: a call returns data only when it has ended, its rollback included, within 0.5 s of its limit; with the rollback held, the call is answered `Timeout` with no rows, while the reader's transaction stays open until the hold ends (`Call_whose_rollback_is_held_past_the_limit_answers_Timeout_and_returns_no_rows`). Run 4: `Timeout` with no data at 3,525 to 3,539 ms on the three engines, once each; reversed as intended. [Configuration](reference.md#configuration) |
| T-4 | NOTE | Run 3: with 10,000 views and a 1,024-column table in the database and a limit of 1 s, the server stopped at startup (`SqlException` number -2 from the startup check, empty audit file) on the three engines, once each; at a limit of 30 s each schema call answered in under 0.6 s. Startup at a limit of 1 s without those objects was not run | Consistent with the docs (the limit is also the startup check's command timeout); one sentence added at `MSSQL_MCP_TIMEOUT_SECONDS` in [Configuration](reference.md#configuration). Run 4, SQL Server 2022: with the 10,000 views (whether the 1,024-column table was also there is not recorded) and a limit of 1 s, 1 of 3 starts failed (at 1,257 ms) and two started in 1.24 and 1.29 s; without them, startup took 385 to 665 ms. The start row failed again on each engine |
| T-5 | NOTE | Run 3: `SELECT NCHAR(55296) AS v` (an unpaired surrogate in a cell) returned U+FFFD with no truncation reason, on the three engines; a cut that split a surrogate pair left no lone half. No security effect | Documented: [Result limits](reference.md#result-limits) |
| U-1 | MEDIUM | Run 4, on `12706a1`, three engines, repeated: a call could leave its session at SQL Server with an open read transaction and a schema-stability lock after the network worked again, until the server process exited. Cases: a truncated result whose connection froze after the cut, released 8 to 20 s later (the call answered `ok` with the 97 rows read before the limit of 3 s, at 3,025 to 3,150 ms); a `Timeout` on a link throttled to 5 KB/s for 60 s (2022 2 of 2, 2019, 2025); 120 calls frozen in the middle of a row (2022, once: one open transaction 61 s after the release). The session was still there 20 s after the release, after following calls, and in a five-call case at the release plus 180 s (1 of 5; the other 4 ended at the release). The client sent no rollback and did not close the connection. While the link was slow or frozen, an administrator writing the same rows waited up to 59.9 s, after the client already had its answer. With `Max Pool Size=1` the following calls opened new sessions, so the pool cap did not bound these sessions. A schema change on the table still succeeded. No data leak and no audit gap | Fixed in `a38fd06` for the cases measured below: the client library gives up a broken connection (at its command timeout for a reader still reading, or 5 s after an unanswered cancel, as the fold of U-1 read the client library's code) and leaves its network connection open; the server now closes it, so SQL Server ends the session and rolls back its transaction once the client's close reaches it. That close is best effort, by a reading of the code: when a network callback of the client library is still pending at that close, the server does not close the connection, and that session stays as on `12706a1` (no repository test is built to produce that case, and none observed it); a connection the client library breaks only after the server's close is not closed either; and whether the client library's own timers can still use a connection the server closed is not covered (run 5: no crash in 312 process ends). Measured for the fix: the repository's tests (`StdioServerTests.LeftOpenSessions.cs`, limit 3 s, one call per case, on `a38fd06`, on Windows, on the three images) found no session, open transaction or lock of the call within 2 s after the network worked again, for a truncated result whose reader close froze, with and without its cancel held, and for a `Timeout` on a link throttled to 5 KB/s; the test with the held cancel is not stable (U-1R). Run 5 measured the 120-call case in the middle of a row and `Cancelled` calls under a freeze too (below). A truncated result whose reader close freezes is still answered `ok` with the rows read before the cut, at about the limit and inside the limit plus 0.5 s (the rule of T-3). The fix reads internal members of the client library, so it depends on the version the release ships (Microsoft.Data.SqlClient 7.1.1). Gate tests (`LeftOpenConnectionsGuardTests`), which run in CI and in the release workflow, assert that version and that every internal the fix needs is found, so a version change fails them. The fix's integration tests ran on Windows, where the client library uses its native networking; CI runs them on Ubuntu, with its managed networking; macOS was not tested. Run 5, on `a38fd06`, three engines, time from the release until no session of the call was left (2022, 2019, 2025; once per engine in a parallel run unless noted): a truncated result whose connection then froze, released after 5 s, 297, 271 and 234 ms, after 20 s, 263, 334 and 307 ms; 120 calls frozen in the middle of a row, 538, 654 and 517 ms; one call frozen in the middle of a row, 535, 431 and 390 ms; a client cancel in the middle of a row, 344, 318 and 476 ms, on a truncated result whose connection then froze, 332, 427 and 381 ms, with the rollback held, 236, 221 and 218 ms; `describe_table` and `list_tables` frozen in the middle of their result held nothing while frozen and left no session; run 4's truncated-result cases left no session 1 to 30 s after the release (the five-call case also 60 to 180 s after), and its cases of a connection frozen and released or cut after 20 s left none at the next call, in parallel runs and alone. `12706a1`, run the same way, still had the session 60 s after the release for the truncated result, the 120 calls and a cancelled truncated result. Not fixed for every `Timeout` on a link throttled to 5 KB/s: U-1R. [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| U-1R | MEDIUM | Run 5, on `a38fd06`, three engines: a `Timeout` on a link throttled to 5 KB/s for 60 s (limit 3 s) left the call's session at SQL Server with an open read transaction and a lock in 8 of 27 single calls (in the form of run 4's case 7 of 15, 2 of 3 of them alone; in the form of run 5's variant 1 of 12), and in 21 of 450 calls repeated 50 in a row on such a link at a limit of 2 s (`12706a1`, run 4: 210 of 450). Each call was answered `Timeout` with one audit line and no data. In the single calls that left it, the client never closed the connection: while the link stayed slow, a writer on the same rows waited 58.3 to 60.3 s, against about 6.1 s when the client closed it, and after the throttle ended the session stayed to the end of each observation (up to 60 s after the release, and after the next call); in the repeated calls, the open transaction and the lock were read 45 s after the release. Longer was not measured. No data leak and no audit gap. The repository test `Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only` is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2 of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the test of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was captured. That it fails for the same reason as U-1R (the fix looks at the connection once, before the close) is reasoned, not proven. It can fail in a CI run or a local run. | Open. The cause is not proven; the fix looks at the connection once, before the server's close. Whether the code before `12706a1` left such sessions was not measured. [Commands and exit codes](reference.md#commands-and-exit-codes), [SECURITY.md](../SECURITY.md#known-limits) |
| U-2 | LOW | Run 4: many calls frozen at once in the middle of a row delay the answers of every call, healthy ones too. Limit 5 s, once per engine and count (2022 at 60 calls twice): with 60 or 120 such calls the median answer came after 8.2 to 29.8 s in parallel runs on the three engines, and after 10.4 to 30.2 s alone (2019 and 2025), each maximum within 0.04 s of its median; a healthy one-row call sent 1.5 s in returned `ok` after up to 28.7 s on 2022 and 2019, and on 2025 `Timeout` after 6.4 to 9.2 s. With 30 such calls the answers came within 5.62 s on 2022 and 2019 in parallel runs, but at a median of 6.7 s alone on 2019 and at most 7.5 s in a parallel run on 2025, where the healthy call got `Timeout` at 6,014 ms. Calls frozen at the rollback kept within 5.65 s at every count. At 60 and 120 calls the alone medians were within about 1.5 s of the parallel ones on 2019 and about 2.2 s slower on 2025, so load does not explain them | A known limit. `a38fd06` does not change the value reads in the middle of a row that it concerns (`GetValue`, `GetSqlDecimal`; from the code diff). Run 5 on `a38fd06`, 60 such calls at a limit of 5 s, once in a parallel run and once alone per engine: median answers 10.8, 12.1 and 10.8 s in parallel and 10.2, 10.2 and 10.5 s alone (2022, 2019, 2025); the healthy call returned `ok` after 8.7 to 10.6 s on 2022 and 2019, and on 2025 got `Timeout` at 6.2 s, alone too. While answers are delayed this way, the server's own cutoff timer can run late too, so a call whose work ended after its limit plus 0.5 s could still return data (reasoned from the code; not observed). [SECURITY.md](../SECURITY.md#known-limits), [Commands and exit codes](reference.md#commands-and-exit-codes) |
| N-1 | NOTE | Run 4: exit 6 was not produced in two attempts (120 calls frozen with input closed at once; output not read, then input closed); both ended 0 | Stays untested: [Commands and exit codes](reference.md#commands-and-exit-codes) |
| N-2 | NOTE | Run 4: after 120 frozen calls, the server process's handle count stayed at about 1,000 to 1,400 for 25 to 45 s after the release (443 at the start); its thread count came back to about 13 | Not followed further; the handles are freed when the process exits |
| N-3 | NOTE | Run 4: with input closed while calls were frozen and unanswered, the process wrote their `Timeout` audit lines but no answers, then exited 0; the same as in runs 2 and 3, before this change | Documented: [Commands and exit codes](reference.md#commands-and-exit-codes) |
| N-4 | NOTE | Run 4: `TerminateProcess` while calls were frozen: exit -1, no audit line for those calls, their sessions held until the link healed | Expected for a hard kill |

### Code security review

| Id | Severity | Finding | What was done |
| --- | --- | --- | --- |
| C-1 | MEDIUM | The time limit applied to each network read, not to the call: one SELECT whose rows arrive slowly could run up to the row cap times the timeout | Changed in `7f98bfd`: one time limit per call over the connection open, the session settings, the execution and every read. Measured on SQL Server 2022, 30 slow rows, 2 s limit: before, 13,871 ms and all 30 rows; after, `Timeout` at 2,082 and 2,277 ms in two runs. Run 3 measured longer overruns (T-1, T-2); since `12706a1` a call that has not ended 0.5 s after its limit is answered as `Timeout` (audited `Cancelled`, with no answer, after a client cancel), and its clean-up goes on at SQL Server; run 4 measured every such `Timeout` at most 0.56 s past the limit, single frozen or throttled connections included, except when many calls were frozen at once in the middle of a row (U-2). When the answer leaves and what goes on after it: [Commands and exit codes](reference.md#commands-and-exit-codes) |
| C-2 | LOW | The character cap was checked only after a whole row was read | Changed in `7f98bfd`: a row stops being read once a lower bound of its size passes the remaining room; for one row of 64 cells of 1,000,000 characters, memory allocated fell from 12.8 MB to 3.7 MB. The bytes received did not fall (64.1 MB): named as a limit in the [README](../README.md#what-it-promises) proof table |
| C-3 | LOW | The login check runs once, at start; this was not stated | Named as a limit: [What `check-login` refuses](login-check.md), README, SECURITY.md |
| C-4 | NOTE | A field, a property, `CAST` or `CONVERT` of a CLR user-defined type passes the gate and runs the type's code | Measured as a test (`ClrTypeMemberTests.cs`): for the test suite's one type, SQL Server asked `EXECUTE` on the type each time; a reader without it gets error 229, and the login check names that right. [Members of CLR user-defined types](sql-gate.md#members-of-clr-user-defined-types) |
| C-5 | NOTE | Characters that only look like the end of a comment, a string or a name were not measured | Measured as a test (`Look_alike_end_inside_a_comment_a_string_or_an_identifier_ends_nothing_on_the_server`, 17 texts): the gate allows all 17, and SQL Server 2019, 2022 and 2025 read each as one statement |
| C-6 | NOTE | After start, a failed login is answered with SQL Server's message, which names the login (error 18456) or the database (error 4060) | Named as a limit: [SECURITY.md](../SECURITY.md#known-limits), "Data leaves the building" |
| C-7 | NOTE | A write that fails part-way leaves a partial last line; the recovery was not documented | Documented: [The audit log](audit-log.md), "After a failed write"; the removal step itself is not tested |
| C-8 | LOW | The `.mcpb` bundles are attested without comparing the server files inside them with the build job's output | Open: the v0.1.0 bundles ship with this limit. What the attestation shows: [Releasing](releasing.md#what-the-workflow-does) |
| C-9 | NOTE | No NuGet lock file: transitive versions and package hashes are not pinned for release builds | Accepted for the first release |
| C-10 | NOTE | The login script's stated character limits did not cover every use of its variables | Fixed in the header comment of `scripts/create-readonly-login.sql` |

### Dependency alerts

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

On 2026-11-13 `packaging/mcpb/package.json` gained two npm `overrides` with exact versions, and the lock file was
regenerated with the integrity hash of each package:

| Package | Before | Now | What asks for it | Advisory ranges |
| --- | --- | --- | --- | --- |
| `tmp` | 0.0.33 | 0.2.7 | `external-editor` 3.1.0 (under `@inquirer/prompts`): `^0.0.33` | GHSA-ph9p-34f9-6g65: below 0.2.6; GHSA-52f5-9888-hmc6: 0.2.3 and below |
| `zod` | 3.25.76 | 4.6.7 | the MCPB CLI 2.1.2: `^3.25.67` | GHSA-mrhx-c7xc-m3w5: below 4.6.6 |

No version that the CLI's tree asks for has a fix: `^0.0.33` admits only 0.0.33, and 3.25.76 is the last `zod` 3.x,
inside the advisory's range. The CLI therefore runs with dependency versions its author did not pin, `zod` among them
at a major version it does not declare. The lock file now resolves no version that any of the three advisories
covers (the scan rows dated 2026-11-13 above); `os-tmpdir`, which only `tmp` 0.0.33 used, left the lock file (54
packages instead of 55). Dependabot alerts belong to the public repository's default branch; this page records what
the lock file resolves, not the state of the alerts.

What was checked on 2026-11-13, on Windows, with the CLI installed by `npm ci --ignore-scripts` from the old lock
file and from the new one:

- The CLI imports `zod` through its root entry, which in 4.x is the `zod` 4 API. `mcpb validate` passes the
  manifest of each of the three bundles with both, and rejects the same seven invalid manifests on the same fields;
  the error texts are worded differently (`name: Required` before, `name: Invalid input: expected string, received
  undefined` now).
- `mcpb pack`, as the bundle job runs it through `packaging/build-release.ps1`, from one publish output per
  platform: for each of the three bundles the same file list, the same `manifest.json` and the same SHA-256 for
  every file, and the files under `server/` equal to the publish output. The `.mcpb` files differ only in the zip
  time stamps, which the CLI sets to the time of packing, so two packs with the same CLI differ in the same way;
  with the time stamps set to zero the old and new bundles are byte for byte the same.
- `info`, `unpack`, `clean`, `sign --self-signed`, `unsign` and `init --yes` gave the same output with both.
  `verify` after `sign --self-signed` answers "Extension is not signed" with both.
- No CLI command calls `tmp`: it is reached only through the `editor` prompt of `@inquirer/prompts`, which the CLI
  does not use. Called the way that prompt calls it, `external-editor` wrote, read back and removed its temporary
  file with `tmp` 0.2.7; with 0.2.7, a prefix with `..` or a directory outside the temporary folder is refused,
  where 0.0.33 returned a path outside it.

Not checked in these tests: packing on Linux, as the release workflow does (the zip then records each file's mode;
that code is in the CLI, not in `tmp` or `zod`), and a run of the release workflow with the new lock file. Later on
2026-11-13 a hand run of the release workflow on `main` with the new lock file packed the three bundles on the Linux
runner; their contents were not compared with the bundles packed on Windows, and the file modes they record were not
read.

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
  burst of 20 calls with cancels, run 3 30 parallel calls at a limit of 2 s, and runs 4 and 5 at most 120 calls
  frozen at once at a limit of 5 s (U-2) and 50 calls in a row; nothing larger.
- Races across many concurrent connections: run 2 used only one connection per session, plus the 20-call cancel
  burst.
- Memory under many concurrent large calls: run 3 measured the server's peak working set for one large call at a
  time; its 30 parallel calls used small rows.
- The client side: Claude Desktop, other MCP clients and the MCP client code itself. The attack tests used their
  own JSON-RPC client.
- The host machine and the network between it and SQL Server. Runs 3 to 5 and the repository's tests stalled and
  throttled connections through a local proxy only: no real network fault (a cable pulled, a firewall drop, a TCP
  keepalive that expires). Run 3 froze a connection for at most 45 s; run 4 kept one link frozen, or throttled to
  5 KB/s, through 50 calls in a row for about 2 minutes, kept 120 calls frozen until the last was answered, about
  30 s on 2022 and 2019, throttled one link for 60 s (U-1) and watched a session left after a release for at most
  180 s; run 5 throttled one link for 60 s per call (U-1R) and watched the sessions for at most 180 s after a
  release.
- Encrypted connections in the proxy cases of runs 3 to 5: they ran with `Encrypt=False`, so the proxy could see
  packet types.
- A Linux or macOS host for the attack tests (file lock and file mode behaviour, SIGTERM and SIGINT): they ran on
  Windows only. The repository's test suites also run on Linux, in CI; nothing ran on macOS.
- The HTTP transport (the server offers stdio only) and `server/discover` on protocol revision 2026-07-28.

Audit log and process:

- An audit file that fails to write a line for a request whose parameters the MCP SDK cannot read (the X-1 kind).
- A full disk while the audit file is written.
- Removing a partial last audit line, the recovery step described in [The audit log](audit-log.md).
- The exit code 6 drain: run 4 tried twice to reach it and both ended 0 (N-1); no output stall that outlasts the
  drain window while input ends was built. Runs 4 and 5 sent Windows console events only: Ctrl+C idle, with calls frozen
  and unanswered, and while abandoned clean-ups ran; Ctrl+Break only with calls frozen and unanswered; no termination
  signal on Linux.

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

- Where the SQL Server client library spends its time while it cancels the command, closes the reader and rolls
  back: the cause of the T-1 overruns on `7f98bfd` was not investigated. Since `12706a1` that time no
  longer holds the answer; it is how long the abandoned session stays at SQL Server
  ([Commands and exit codes](reference.md#commands-and-exit-codes)). The fold of U-1 read, from the client library's
  code, where a broken connection's close stops: the library gives the connection up at its command timeout for a
  reader still reading, or 5 s after an unanswered cancel, and leaves the network connection open; since
  `a38fd06` the server closes it. That close is best effort, by a reading of the code: when a network callback of
  the client library is still pending at that close, the server does not close the connection, and that session
  stays as on `12706a1` (no repository test is built to produce that case, and none observed it); a connection the
  client library breaks only after the server's close is not closed either; and whether the client library's own
  timers can still use a connection the server closed is not covered (run 5: no crash in 312 process ends). The
  repository's U-1 tests (`StdioServerTests.LeftOpenSessions.cs`: a truncated result whose reader close froze, with
  and without its cancel held, and a `Timeout` on a link throttled to 5 KB/s; limit 3 s, one call per case) assert no
  session, open transaction or lock of the call within 2 s after the network works again; they passed on
  `a38fd06`, on Windows, on the three images, in one suite run. One of them,
  `Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only`,
  is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2
  of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the
  test of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was
  captured. That it fails for the same reason as U-1R (the fix looks at the connection once, before the close) is
  reasoned, not proven. It can fail in a CI run or a local run. Run 5, on `a38fd06`: on a frozen link, after a client
  cancel, after a cut and for the schema tools, no session of the call 0.22 to 0.65 s after the release, on the three
  engines (per case and engine: the U-1 row of Findings); on a link throttled to 5 KB/s, 8 of 27 single calls and 21 of
  450 repeated calls left the session to the end of each observation (U-1R).
- The time limit passing during an `xml` cell (also an `xml` read frozen on the network), at the end of a schema
  tool's read, or during the step to a next result set: handled in code, no repository test. Run 3 measured the
  first once: on SQL Server 2019 the read of one 20,000,000-character `xml` text node ended 0.26 s past a limit of
  30 s, as `Timeout` with no data. Run 4 froze an `xml` read in the middle of a row (`Timeout` at the limit
  plus 0.51 to 0.52 s, once per engine); the step to a next result set was not frozen.
- Many calls frozen at once: the repository's tests freeze one call at a time (two for the schema tools, one after
  the other). Run 4 froze 30, 60 and 120 at once in the middle of a row or at the rollback, at a limit of 5 s (U-2);
  not more than 120, not at other points, and on 2022 not alone (its 60 and 120 runs overlapped another engine's
  run). Run 5 froze 60 and 120 in the middle of a row, and repeated the 60 alone on each engine, 2022 included.
- Of run 4's changed-behaviour cases, only the healthy heavy rows and the many-call freezes were repeated alone;
  the others ran while another engine's run was going. Run 5 repeated alone only the 60-call freeze and run 4's
  cases of state at SQL Server after the answer.
- More than 50 calls in a row after a `Timeout`, and pool sizes other than the default, 5 and `Pooling=false`
  (runs 4 and 5) and 1 (the repository's tests). The pool clear with other calls running was measured with one frozen
  call and ten healthy ones only.
- A SQL Server that itself stops answering when the command is cancelled; runs 3 to 5 and the repository's tests
  froze the network path instead.
- The time limit at which the startup check fails (T-4): run 4 measured a limit of 1 s on SQL Server 2022 only.
- Run 5 did not test: the cause of U-1R (not proven; it needs instrumentation inside the server, which would change the
  code under test); how long a U-1R session stays beyond the end of its observation (60 s after the release, or 20 s
  after it and the next call, or 45 s after it for the repeated calls) without a process exit; run 4's form of the
  throttled case on `12706a1` (run 4 is its baseline; run 5's variant was run on `12706a1`); more than 30 broken calls
  on one process, and longer idle times; the late sends with Extended Events and the plan cache (not asked of run 5);
  and the classes of runs 1 to 3, except run 3's stalled and throttled connection rows (not in its case set).
- Repeat counts: unless a finding says otherwise, each run 3 value was measured once per engine, and so was each
  run 4 value, except where run 4's text gives repeats (the `list_tables` freeze in the middle of a result and
  its cancel, the throttled link of U-1 and the 60-call freeze on 2022; the alone repeats on 2019 and 2025). Run 5:
  the throttled link 5 times per engine in the form of run 4's case and 4 times per engine in the form of its own
  variant; run 4's cases of state after the answer and the 60-call freeze twice per engine, once in a parallel run
  and once alone; every other value once per engine, the schema-tool variants, the break attempts, the pool clear,
  ending the process and the repeated calls among them.
- One row of 4,096 cells, the code review's case for C-2; it was measured with 64.

Code security review:

- ScriptDom, Microsoft.Data.SqlClient and the MCP SDK were not decompiled by the review; the fold of U-1 read
  Microsoft.Data.SqlClient 7.1.1's IL for the break and close path, with a tool not kept in the repository. Their
  behaviour (whether ScriptDom
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
- The release archives and bundles: the release workflow had built none when these tests ran, so none was scanned
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
npm ls --package-lock-only --all tmp zod
cd ../..
```

`npm ls` prints the `tmp` and `zod` versions the lock file resolves; with the overrides it marks them `overridden`.

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
Server. Run 4 used the same harness and proxy with one more scenario module that freezes or throttles one connection
at a chosen request type or byte count, reads the reader's sessions, transactions and locks as an administrator, and
sends Ctrl+C and Ctrl+Break as console events. Run 5 used the same harness, proxy and signaller, with the proxy
also recording socket events and one more scenario module that runs variants of the U-1 cases, polls the call's
sessions until they are gone, and tries to break the fix. These tests in the repository cover the same promises,
class by class:

- A: `RegressionTableTests.cs`, `StatementTypeTests.cs`, `GateRefusalTests.cs`, `ExtendedEventsTests.cs`,
  `DifferentialTests.cs`, `ParserVersusServerLexerTests.cs`, `ClrTypeMemberTests.cs`.
- B: `CapsTests.cs`, `CallLimitTests.cs`, `LargeValueTests.cs`, and for the answer at the time limit on a frozen,
  throttled or held connection `StdioServerTests.AnswerAtTheLimit.cs`.
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

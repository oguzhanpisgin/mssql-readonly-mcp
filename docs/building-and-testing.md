# Building and testing

This page covers how to build the code and run the tests, what they need, the test counts, the measured
startup times and the sources of the setup steps; the [README](../README.md#1-prerequisites) lists the
prerequisites.

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
- Test counts in the last full local run, on the code with the U-1R fix (2026-11-13, on Windows, one run per image):
  Gate 416, Audit 90, Integration 259 on each of the three images named under
  ["Prerequisites"](../README.md#1-prerequisites), all passed. CI on Ubuntu runs the same tests on every push to
  `main` (the CI badge in the README shows the last result); its last run recorded here, on 2026-11-13 on the code
  before the U-1R fix, passed with Gate 387, Audit 86 and Integration 259 on each image, as did the full local run
  on that code the same day. In an earlier full run, on `eededa6` (then Gate 367, Audit 55, Integration 259), all
  passed except one integration test on the 2022 image (258 of 259). That test,
  `Truncated_result_whose_cancel_is_held_until_the_network_works_again_holds_its_locks_until_then_only`,
  is not stable: on the 2022 image, on Windows, it failed in 2 of 13 runs on `a38fd06` (outside the full suite) and in 2
  of 6 on `eededa6` (once in the full suite run, once in 5 runs outside the full suite), 4 of 19 together, while the
  test of the same case without the held cancel passed 10 of 10 runs outside the full suite; no failure message was
  captured then, so the cause of those failures is not known. The failures whose message was captured after v0.1.0
  (one in 40 local runs on the 2022 image, one CI run on the 2019 image) had a cause other than U-1R: the request had
  ended while the cancel was held; in the local one, SQL Server ended it on its own because the whole result fit into
  the network buffers between SQL Server and the test's proxy, so nothing was left to hold. It passed on each image in
  the last full local run; one run per image does not show it stable. It is not fixed and can still fail in a CI run or
  a local run. See U-1R in [Security testing](security-testing.md#findings).
- `ErpScaleTests.cs` creates a second database with 20,000 tables and asserts that the startup check passes there
  within the default connection timeout (15 s); it writes the time to its output. Measured on 2026-11-12 on one
  machine, one run per version of a Debug build: 1.3 s on SQL Server 2022, 1.4 s on 2019 and 1.8 s on 2025 with no
  other SQL Server container running; in runs while other SQL Server containers were busy on the same machine,
  10.0 s, 5.8 s and 3.8 s. The check's query alone, run in `sqlcmd` as a reader with `SELECT` on schema `dbo` of a
  scratch database with 20,000 two-column tables (`Id int`, `Name nvarchar(50)`), with the plan cache cleared
  before each run, took 1.7 to 1.8 s on 2022, 1.8 s on 2019 and 1.9 to 2.1 s on 2025 (three runs each).
  With `SELECT` granted on each of the 20,000 tables one by one instead of on the schema, the same query took 4.0
  to 5.3 s on 2022 in `sqlcmd` with only those grants (one series of three runs), and 3.5 to 5.0 s with `INSERT`
  also granted on 200 of the tables (three series of three runs); the load from other work on the machine was not
  recorded, and no test asserts this case. With the statement as it was before this round's owner and
  `WITH GRANT OPTION` rules (the backstop already in it), the case with the 200 `INSERT` grants took 3.2 to 5.5 s
  (three series of three runs); with this round's statement without the backstop's two parts, the same case took
  2.5 to 2.7 s (one series of three runs).
- The SQL Server container images run on x86-64 Linux hosts only (Docker Desktop on Windows uses a Linux VM).
- `.github/workflows/ci.yml` runs the same steps on GitHub on every push and pull request: the build and unit tests,
  then, when they pass, the integration tests on each of the three images; the CI badge at the top of the
  [README](../README.md) shows the result of the last run on `main`.

## Sources of the setup steps

The error number of an untrusted certificate (README step 2), the restart advice and the `$null` remark
([Installing](install.md#keep-the-connection-string-for-your-user)) were seen in a first run on 2026-11-12 on Windows
with Claude Code; they are not covered by a test. The sources of each client's block, and what was run on
2026-11-13, are in [Installing, "What was tested"](install.md#what-was-tested).

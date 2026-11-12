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
- Test counts at the time of writing: Gate 364, Audit 55, Integration 245 on each of the three images named under
  ["Prerequisites"](../README.md#1-prerequisites).
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

An entry with no `type` is a local stdio server (README step 4). Source: <https://code.claude.com/docs/en/mcp>.

The error number in step 3, the restart advice and exit code 2, the `$null` remark and "Pending approval" were seen
in a first run on 2026-11-12 on Windows with Claude Code; they are not covered by a test.
These are steps 3 and 4 of the [README](../README.md#five-minute-start).

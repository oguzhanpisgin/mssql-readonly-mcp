# Contributing

## What this project is

Status: early release, actively maintained by one maintainer. Issues and pull requests are welcome; they are read
and answered, with no set time and no promise that a pull request is merged.

The scope is in the README: "What it promises" and "What it does not solve". A change that widens what the server
lets an assistant do (a new tool, a new allowed syntax node or function name) has to keep every promise in "What it
promises" true; a change that lets it write cannot.

## Security problems

Do not open a public issue for a security problem. Report it privately through GitHub private vulnerability
reporting, as [SECURITY.md](SECURITY.md) ("Reporting a problem") describes. We aim to answer within 3 days.

A security problem includes any way to get a write, a schema change, a read outside the connected database, or a
call with no audit line through this server. Send it in the report format of the README section "Try to break it".

## Issues

Questions and bugs go to GitHub Issues, through one of three forms: Question, Bug report or Gate decision.

- **Question**: how to do something, or whether the server does something.
- **Bug report**: the server, a command or a tool does not do what the README or a page under `docs/` says.
- **Gate decision**: a query the gate allowed or refused wrongly that is not a security problem, for example a
  function you need that is not on the list in "What SQL is allowed" ([docs/sql-gate.md](docs/sql-gate.md)).

Never paste a connection string, a password, real data rows or an audit file from a production system into an
issue. Replace names and values you cannot share, and keep the shape of the text that shows the problem.

## Building and testing

The commands, the requirements and the test counts are in
[docs/building-and-testing.md](docs/building-and-testing.md). In short:

- Run every command from the repository root, so `global.json` applies.
- The integration tests need Docker; two audit tests need PowerShell 7 (`pwsh`) on `PATH`.
- Without Docker or `pwsh` those tests fail. They never skip.
- `MSSQL_TEST_IMAGE` selects the SQL Server container image; the default is in that section.

`.github/workflows/ci.yml` runs the same steps on GitHub.

## How a change is made

- **A claim in the docs names the test that proves it.** README.md, the files under `docs/` and SECURITY.md name the
  test file, often the test method, behind their claims; where no test covers a claim, the text says so. Keep that
  true for every line you add or change.
- **A behaviour change comes with a test that was red first.** Write the test, run it against the code before the
  change and see it fail, then make the change and see it pass. Say in the pull request which test that was.
- **Docs follow behaviour.** When behaviour changes, change the README, `docs/` and SECURITY.md lines that describe
  it in the same pull request, including counts such as the regression table rows and the test counts.
- **The gate is an allowlist.** A node type or function name is added only after it is checked not to reach outside
  the query or expose metadata or security material ([docs/sql-gate.md](docs/sql-gate.md), "What SQL is allowed"),
  with rows in `tests/MssqlReadOnlyMcp.Gate.Tests/RegressionTableTests.cs`.
- Warnings are errors (`Directory.Build.props`).

## Pull requests

Fill in the pull request template: what changed and why, the test that was red first, the test counts from your
run and the SQL Server image they ran on, and the docs lines you touched.

## Conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Licence

MIT. See [LICENSE](LICENSE).

# mssql-readonly-mcp plugin for Claude Code

This plugin connects Claude Code to [mssql-readonly-mcp](https://github.com/oguzhanpisgin/mssql-readonly-mcp), an
open-source MCP server that lets an AI assistant read one on-premises SQL Server database. The server's SQL tool
accepts only text that passes an allowlist for one plain SELECT, it will not start with a login that fails its
permission check, its results are capped by rows, characters and time, and it writes a hash-chained audit line for
every call before any data goes back.

The plugin holds no server code. It has three parts:

- `.mcp.json` runs `node` on the plugin's start file, `scripts/start.mjs` (about 70 lines, no dependencies). The
  start file starts the program `mssql-readonly-mcp`, which you install yourself from the project's GitHub
  releases (below), passes it the two values you set when you enable the plugin, and connects its input and
  output to Claude Code.
- A skill, `query-sql-server-read-only`, that tells Claude how to use the three tools (`list_tables`,
  `describe_table`, `run_query`): look up tables and columns before writing a query, keep queries small, stop at a
  refusal instead of working around it, and treat the audit file and personal data with care.
- This README and the MIT licence.

## What the plugin runs, sends and stores

- **Runs**: `node` with the plugin's start file, which starts one local process, `mssql-readonly-mcp`, found on
  your `PATH`, with no arguments. The start file opens no network connection and writes no file. The plugin
  downloads nothing and installs nothing; it has no hooks.
- **Sends**: the server opens one kind of network connection, to the SQL Server named in your connection string
  (and, if you choose a Microsoft Entra authentication mode in it, to the sign-in endpoints that mode uses). It
  sends no telemetry and has no update check.
- **Stores**: the audit file, at the path you choose, on your machine, and its `.lock` companion beside it. Each
  line holds the call's SQL text, decision, tables, row count, timing and hashes, never the returned row values;
  literal values in a query's text, such as a name in a `WHERE`, are in that text.
- **Leaves your machine through Claude**: the rows Claude reads, with table and column names and any SQL Server
  error text, go to Anthropic as part of the conversation. Read-only does not change that; whether that is allowed
  for your data is your organisation's decision.

## Where it works

| Surface | What happens |
| --- | --- |
| Claude Code (terminal, IDE extensions, desktop app's Code tab) | Asks for the two values when you enable the plugin, starts the server, and loads the skill. |
| Cowork | Does not start the server: Cowork does not ask for plugin values, and this server needs a connection string. |
| Chat on claude.ai and in the apps | Does not start local servers. |

## Install

### 1. Install the server program

Download the archive for your platform from the
[latest release](https://github.com/oguzhanpisgin/mssql-readonly-mcp/releases/latest), check it against
`SHA256SUMS`, and unpack it. The main README's
[Five-minute start](https://github.com/oguzhanpisgin/mssql-readonly-mcp#five-minute-start) has the Windows blocks,
and [Installing](https://github.com/oguzhanpisgin/mssql-readonly-mcp/blob/main/docs/install.md) has Linux and
macOS. The release archives include the .NET runtime; no .NET SDK is needed. The plugin was written against
server v0.1.1; the plugin's own version (0.2.0 in `plugin.json`) is counted separately from the server's.

The plugin's start file needs [Node.js](https://nodejs.org/) 18 or later on your `PATH`; `node --version` in a
terminal shows whether you have it.

Then put the program's folder on your `PATH`, so that Claude Code can start it by name. On Windows, for the folder
of the Five-minute start:

```powershell
$dir = "$HOME\tools\mssql-readonly-mcp"
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$newPath = if ($userPath) { "$userPath;$dir" } else { $dir }
if (($userPath -split ';') -notcontains $dir) { [Environment]::SetEnvironmentVariable('Path', $newPath, 'User') }
```

This rewrites your user `PATH` once: entries written with a variable, such as `%USERPROFILE%\bin`, are saved back
as the full path they stand for. If you prefer to keep them, add the folder in Windows' "Edit environment variables
for your account" dialog instead.

On Linux and macOS, add `export PATH="$HOME/tools/mssql-readonly-mcp:$PATH"` to your shell's start file (for
example `~/.bashrc` or `~/.zshrc`). Keep the folder, not a link to the executable: the program loads the libraries
beside it.

Quit and restart Claude Code, and the terminal it starts from, so that it sees the new `PATH`. Check it in a new
terminal with `mssql-readonly-mcp --help`: it prints its usage line (and exits 2), which shows that the program is
found.

### 2. Create the read-only login

Create the login with the project's script and check it with `mssql-readonly-mcp check-login`, as step 2 of the
main README's [Five-minute start](https://github.com/oguzhanpisgin/mssql-readonly-mcp#2-create-and-check-the-read-only-login)
describes. The server refuses to start with a login that holds more than read rights.

### 3. Install the plugin

From this repository's marketplace, in Claude Code:

```text
/plugin marketplace add oguzhanpisgin/mssql-readonly-mcp
/plugin install mssql-readonly-mcp@mssql-readonly-mcp
```

Newer Claude Code versions also accept a one-step form, not yet tried by this project:
`/plugin install mssql-readonly-mcp --marketplace oguzhanpisgin/mssql-readonly-mcp`.

Claude Code then asks for two values:

- **SQL Server connection string**: `Server=<host>;Database=<database>;User ID=mcp_reader;Password=<password>`, the
  login of step 2. The plugin marks it `sensitive`, so Claude Code masks it as you type (as its documentation
  describes) and keeps it out of `settings.json`. On Windows, in this project's test with Claude Code 2.1.332, it
  was kept in the file `.credentials.json` in Claude Code's configuration folder, in plain text, readable by
  programs running as you.
- **Audit file path**: an absolute path such as `C:/Users/<you>/mcp-audit/claude-code-plugin.jsonl`, in a folder
  only your account can read and write. The server creates the file and its folder.

To change them later, run `/plugin configure mssql-readonly-mcp@mssql-readonly-mcp` in Claude Code.

### 4. Ask

Ask, for example:

1. "List the tables you can read, then describe dbo.Orders."
2. "Show the five customers with the highest order total in 2026, with their totals."
3. "Raise the price of the product 'Gear oil 5 L' by 10 percent." Claude says the server is read-only and offers
   the `UPDATE` text for you to review and run with your own tools. If it sends the `UPDATE` anyway, the server
   refuses it (`NotSelect`) before it reaches SQL Server, and the refusal is in the audit file.

The invented database these prompts fit, and the login grants for it, are in
[docs/demo.md, "Repeat it"](https://github.com/oguzhanpisgin/mssql-readonly-mcp/blob/main/docs/demo.md#repeat-it).
Then check the audit file in a terminal:

```powershell
mssql-readonly-mcp verify-audit "$HOME/mcp-audit/claude-code-plugin.jsonl"
```

## If the tools do not appear

Run `claude mcp list` in a terminal. The server is listed as `plugin:mssql-readonly-mcp:mssql-readonly`.

Claude Code keeps the server's error lines in its MCP log for the server. In this project's Windows test that
was a folder `mcp-logs-plugin-mssql-readonly-mcp-mssql-readonly` under
`%LOCALAPPDATA%\claude-cli-nodejs\Cache\<folder named after the project folder>\`.

- `Failed to connect`, and the log says `the program 'mssql-readonly-mcp' was not found on PATH`: the program is
  not on the `PATH` that Claude Code started with. Repeat the end of step 1 and restart Claude Code.
- The log says that `node` is not recognized or was not found: install Node.js 18 or later, or put it on your
  `PATH`, and restart Claude Code.
- The log names a configuration error (exit code 2), a login that failed its permission check (exit code 3) or a
  SQL Server it cannot reach (exit code 4): see
  [Commands and exit codes](https://github.com/oguzhanpisgin/mssql-readonly-mcp/blob/main/docs/reference.md#commands-and-exit-codes).
  Its error lines never repeat the connection string.

## What was tested

On 2026-11-27, on Windows 11 with Claude Code 2.1.332 and the published v0.1.1 Windows archive, in a separate
Claude Code configuration folder: the plugin installed from a local copy of this repository's marketplace
(`claude plugin marketplace add`, then `claude plugin install`), both values were saved with
`claude plugin configure --values-stdin`, Claude Code started the program from `PATH`, the program created the
audit file at the configured path, and it tried the configured SQL Server (which was not running; its error line
was the one the server prints before exit code 4, "cannot connect to SQL Server or run the permission check").
With the program not on `PATH`, Claude Code reported the server as failed. The same day the marketplace was added
and the plugin installed from this GitHub repository.

Plugin 0.2.0 (with the start file, on Node.js 26) was checked the same way from a local copy: Claude Code started
`node .../scripts/start.mjs`, the start file started the program with both values, the audit file was created, and
the same "cannot connect" line appeared. With the program not on `PATH`, Claude Code's log showed the start file's
"was not found on PATH" line (run alone, the start file exits with code 127). Run alone with the connection string
option empty and `MSSQL_CONNECTION_STRING` set in the user's own environment, the start file let the program stop
with its configuration error (exit code 2) instead of using that other value.

Not tested yet: a full session against a running SQL Server through the plugin, typing the values in Claude
Code's own dialog, Linux, macOS, and an install from Anthropic's directory.

## Licence and support

MIT, see [LICENSE](LICENSE). Questions and bugs:
[GitHub Issues](https://github.com/oguzhanpisgin/mssql-readonly-mcp/issues). Security reports: privately, as the
project's [SECURITY.md](https://github.com/oguzhanpisgin/mssql-readonly-mcp/blob/main/SECURITY.md) describes.
This is an independent project, not affiliated with or endorsed by Microsoft or Anthropic.

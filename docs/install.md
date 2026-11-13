# Installing

This page covers what the README's ["Five-minute start"](../README.md#five-minute-start) leaves out: Linux and
macOS, a SQL Server administrator login for the login script, keeping the connection string for your user, each
client in more detail, updating, building from source, and what was tested.

## Linux and macOS

Download, check and unpack (Linux x64; for macOS on Apple silicon use `osx-arm64`, and `shasum -a 256 --check` in
place of `sha256sum --check`):

```bash
v=0.1.1; name=mssql-readonly-mcp-$v-linux-x64
base=https://github.com/oguzhanpisgin/mssql-readonly-mcp/releases/download/v$v
curl -fL -O "$base/$name.tar.gz" -O "$base/SHA256SUMS"
grep " $name.tar.gz\$" SHA256SUMS | sha256sum --check
mkdir -p ~/tools && tar -xzf "$name.tar.gz" -C ~/tools && mv ~/tools/"$name" ~/tools/mssql-readonly-mcp
exe=~/tools/mssql-readonly-mcp/mssql-readonly-mcp
```

On Linux the server needs the ICU libraries (for example the `libicu` package): it turns globalization on because
Microsoft.Data.SqlClient needs it. The Linux and macOS builds have not yet been started on Linux or on a Mac by this
project, and whether Gatekeeper starts the macOS executable, which is not notarized, is not known
([Releasing](releasing.md#not-yet-verified)).

Get the login script, replace its example `GRANT` lines with your tables and views, then create the login with a
SQL Server administrator login and check it:

```bash
curl -fL -o create-readonly-login.sql "https://raw.githubusercontent.com/oguzhanpisgin/mssql-readonly-mcp/v$v/scripts/create-readonly-login.sql"
"${EDITOR:-nano}" create-readonly-login.sql
```

```bash
export MCP_LOGIN=mcp_reader MCP_DATABASE=ExampleDb
read -rsp 'New password for mcp_reader: ' MCP_PASSWORD; echo; export MCP_PASSWORD
read -rsp 'Password for the administrator login: ' SQLCMDPASSWORD; echo; export SQLCMDPASSWORD
sqlcmd -S sql.example.local -U admin_login -b -i create-readonly-login.sql
export MSSQL_CONNECTION_STRING="Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=$MCP_PASSWORD"
unset MCP_PASSWORD SQLCMDPASSWORD
"$exe" check-login
```

Claude Code, from the same shell:

```bash
claude mcp add -e "MSSQL_CONNECTION_STRING=$MSSQL_CONNECTION_STRING" -e "MSSQL_MCP_AUDIT_LOG=$HOME/mcp-audit/claude-code.jsonl" --transport stdio mssql-readonly -- "$exe"
```

Choose an audit folder readable and writable only by the account that runs the server: on macOS and Linux the
default rights of a new folder were not checked by this project, so make it private, for example with `chmod 700`
on the folder.

For the other clients, use the README's blocks with the absolute path of `$exe` (`echo "$exe"`) as the command and
an absolute audit path; the configuration files cannot expand `~`.

## A SQL Server administrator login

The README's login block uses Windows authentication (`-E`). With a SQL Server administrator login instead (as the
test does), replace its `sqlcmd` line by these three:

```powershell
$env:SQLCMDPASSWORD = Read-Host -MaskInput 'Password for the administrator login'
sqlcmd -S sql.example.local -U admin_login -b -i create-readonly-login.sql
Remove-Item Env:SQLCMDPASSWORD
```

## Keep the connection string for your user

The README's blocks keep the connection string in an environment variable of one PowerShell window. Codex and Cursor
started from that window see it; the ChatGPT desktop app, and Cursor started from the Start menu, do not. To set it
for your user on Windows:

```powershell
$password = Read-Host -MaskInput 'Password for mcp_reader'
[Environment]::SetEnvironmentVariable('MSSQL_CONNECTION_STRING', "Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=$password", 'User')
```

A user variable reaches only processes started afterwards: quit and restart the application, not only a window or a
tab. Windows stores the variable in your registry hive (`HKCU\Environment`) in plain text, password included,
readable by any process running as you. To remove it:
`Remove-ItemProperty HKCU:\Environment -Name MSSQL_CONNECTION_STRING` (setting it to `$null` left an empty value).
On Linux and macOS, `export` it in the shell that starts the client, from a file only you can read.

## Claude Desktop

On Windows, the README opens the bundle with one line, the app's execution alias with the bundle's full path as its
argument: `claude-desktop.exe "<full path to the .mcpb>"`. On Windows 11 with the Microsoft Store build 5.11492.2.0
this opened the install page at once, observed twice on 2026-11-13; Anthropic does not document it. If it does
nothing or PowerShell does not recognise it, use the menu below.

The bundle is installed from inside the app: **Settings > Desktop app > Extensions** ("Manage"; in older versions
**Settings > Extensions**) **> Advanced settings > Install Extension…** (Anthropic's support article "Getting started
with local MCP servers on Claude Desktop"; the Store build 5.11492.2.0 tested showed **Settings > Extensions**).
Anthropic's page "Add a connector that isn't in the directory" documents
dragging the `.mcpb` file onto the **Settings > Extensions** page instead, and says that when an organisation has
turned extensions off, Extensions does not appear in settings. The support article says values a bundle marks
`sensitive`, here the connection string, are encrypted with the operating system's secure storage (Keychain on
macOS, Credential Manager on Windows).

On the Microsoft Store build of Claude Desktop, a double-click on the `.mcpb` file may open Windows' "choose an app"
dialog instead of Claude Desktop: seen by this project on Windows 11 on 2026-11-13, where the Store package
(5.11492.2.0) declares no file association for `.mcpb`. Anthropic's pages do not mention double-click or a file
association. Use the line or the menu above.

The two fields, in the extension's configuration dialog; on the build tested both arrived empty, and the dialog
showed each field's description above it:

- **SQL Server connection string**: `Server=<host>;Database=<database>;User ID=<login>;Password=<password>`, with a
  port as `Server=<host>,<port>`, and `;TrustServerCertificate=True` only for a test server with a self-signed
  certificate. Paste only this text. Use a login that passes `check-login` (the README's step 2, "Create and check
  the read-only login"). The value becomes `MSSQL_CONNECTION_STRING`; the server adds `ApplicationIntent=ReadOnly`
  and `Application Name=mssql-readonly-mcp`.
- **Audit file path**: a full path, for example `C:\Users\<you>\mcp-audit\claude-desktop.jsonl` on Windows,
  `/Users/<you>/mcp-audit/claude-desktop.jsonl` on macOS, `/home/<you>/mcp-audit/claude-desktop.jsonl` on Linux.
  Choose a folder readable and writable only by the account
  that runs the server: on Windows, one in your profile folder, not one at a drive root; on macOS and Linux the
  default rights of a new folder were not checked by this project, so make it private, for example with `chmod 700`
  on the folder. A folder created at the root of a Windows drive normally lets every signed-in user read and change
  its files. The file is created with its folder if missing. The value becomes `MSSQL_MCP_AUDIT_LOG`.

If the server does not start, its log names the configuration error; the log view is on the extension's page. For
the connection string the error says which of three cases it is (the field is empty, it holds a `${...}` placeholder
the app did not fill in, or the text cannot be read as a connection string), and none of them repeats any part of
the value.

The Store build tested, on Windows, started the server twice with the same settings, one process for chat and one
for Cowork and Code sessions. On Windows the second process writes a sibling audit file (`claude-desktop.2.jsonl`
beside `claude-desktop.jsonl`), each file with its own chain and lock, and says so in one line on stderr; a further
process takes `.3`, and so on, at most 8 files in all, and a ninth process exits with a message. This was measured
with two processes outside Claude Desktop; on Linux the same code path has run only in the repository's tests, which
passed in CI on Ubuntu on 2026-11-13. On macOS, and every other
platform, a second process on the same audit file exits with code 2 because the file is locked; whether Claude
Desktop on macOS starts two processes, and what it then shows, was not measured. Where siblings exist, verify every file, not only the
configured one ([The audit log, "Several server processes on one audit
path"](audit-log.md#several-server-processes-on-one-audit-path)). What the install test showed: ["What was
tested"](#what-was-tested).

### Claude Desktop configuration file

The fallback when Extensions is missing or the install fails. This form is documented on the MCP project's page
<https://modelcontextprotocol.io/docs/develop/connect-local-servers>, not on an Anthropic page, and was not tested
by this project. The page documents no `${VAR}` expansion for this file, so **the password sits in it in plain
text**: limit who can read the file.

The file is `%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS. Open it from Settings > Developer >
Edit Config. Paths must be absolute; backslashes are doubled in JSON.

```json
{
  "mcpServers": {
    "mssql-readonly": {
      "command": "C:\\Users\\<you>\\tools\\mssql-readonly-mcp\\mssql-readonly-mcp.exe",
      "env": {
        "MSSQL_CONNECTION_STRING": "Server=sql.example.local;Database=ExampleDb;User ID=mcp_reader;Password=EXAMPLE-ONLY",
        "MSSQL_MCP_AUDIT_LOG": "C:\\Users\\<you>\\mcp-audit\\claude-desktop.jsonl"
      }
    }
  }
}
```

Quit Claude Desktop completely and start it again after editing. The server's stderr is in
`%APPDATA%\Claude\logs\mcp-server-mssql-readonly.log`.

## Claude Code

The README's line stores the server in the default `local` scope: Claude Code's own file `.claude.json` in your
home folder, under the folder the command ran in, with the connection string in plain text. While `claude mcp add`
runs, the connection string is also on that process's command line, which other processes running as you can read.
`claude mcp remove mssql-readonly` removes the entry.

Claude Code documents `${VAR}` references in a project `.mcp.json`, which would keep the password out of the file.
With Claude Code 2.1.323 on Windows (2026-11-13) they could not carry this server's connection string: the server
received an empty value, exited with code 2, and `claude mcp list` showed `✘ Failed to connect`. In that run:

- a reference to a variable whose name holds `CONNECTION_STRING`, `CONNSTR` or `DSN` expanded to an empty value, and
  a `${NAME:-default}` default on it was ignored;
- a reference to a variable of another name expanded to an empty value when the value held `Password=` or `Pwd=`;
  values with `Integrated Security=true` or `Authentication=ActiveDirectoryDefault` and no password expanded;
- with no `env` entry for it, `MSSQL_CONNECTION_STRING` set where `claude` started did not reach the server.

Claude Code's page, read the same day, describes credential variables that "read as empty" only for a remote
server's `url` and `headers`. Without a project `.mcp.json`, the "Pending approval" step of an interactive session
does not apply.

## Codex CLI and the ChatGPT desktop app

OpenAI's page documents that the ChatGPT desktop app, the Codex CLI and the IDE extension share
`~/.codex/config.toml`, that `env_vars` forwards variables from Codex's environment, that the desktop app also lists
servers under Settings > MCP servers, and that a project can hold its own `.codex/config.toml` (trusted projects
only). `codex mcp list` shows the entry with `MSSQL_CONNECTION_STRING=*****`; it does not start the server.

## VS Code with GitHub Copilot

The README's block is VS Code's editor-managed `mcp.json` format with an `inputs` entry (`promptString`,
`password: true`); VS Code's page says the value is asked when the server starts for the first time and then
"securely stored for subsequent use". The same page marks this format as the one of the "Local harness", deprecated
and to be removed in February 2027, and says that input variables are not supported in its portable files
(`.mcp.json` at the workspace root, behind the experimental setting `chat.mcp.workspaceRootConfig.enabled`, or
`~/.copilot/mcp-config.json`), which take `${NAME}` environment references only. Neither form was tested in VS Code
by this project.

## Cursor

Cursor's page documents `.cursor/mcp.json` (project) and `~/.cursor/mcp.json` (all projects), and `${env:NAME}`
in `command`, `args` and `env`. It was not tested in Cursor by this project.

## Updating

Quit the clients that run the server, delete `$HOME\tools\mssql-readonly-mcp` (`~/tools/mssql-readonly-mcp` on Linux
and macOS), and run the README's download block (on Linux and macOS, the one above) with the new version. The client
entries keep pointing at the same path. For Claude Desktop, install the new bundle as [above](#claude-desktop).

## From source

Needs a .NET 10 SDK, 10.0.400 or later (`global.json`, `rollForward: latestFeature`, accepts a later 10.0 band, not
11.x), and on Linux the ICU libraries:

```powershell
git clone https://github.com/oguzhanpisgin/mssql-readonly-mcp.git
cd mssql-readonly-mcp
dotnet build -c Release
$dll = (Resolve-Path src/MssqlReadOnlyMcp/bin/Release/net10.0/mssql-readonly-mcp.dll).Path
```

The server is that DLL, started with `dotnet <path>` (on Windows the build also writes `mssql-readonly-mcp.exe`
beside it). Use `dotnet $dll` where the README uses `& $exe`, `scripts/create-readonly-login.sql` where it downloads
the script, and in a client block `"command": "dotnet"` with the DLL's absolute path as the one argument
(`"args": [...]`, in TOML `args = [...]`). Build and test commands: [Building and testing](building-and-testing.md).

## What was tested

On Windows 11 on 2026-11-13, with the win-x64 archive and bundle of a release workflow run by hand (version
`0.0.0-dev`; no release was published then), against a SQL Server 2022 container with an invented database and a
login made by the login script. Paths were changed to a test folder; the connection string came from a file, not a
prompt (in Claude Desktop it was pasted into the extension's connection-string field). After the release, the
download block of step 1 was also run against the published v0.1.0 release (row "Download").

| Block | Result |
| --- | --- |
| Download (README step 1) | Run with the two `Invoke-WebRequest` lines replaced by copies of the local files and a `SHA256SUMS` written as the workflow writes it: the check passed, the archive unpacked and renamed, and the program without configuration exited 2 with its configuration error line. The bundle's check line in the Claude Desktop section passed, in its earlier form that saved to the current folder, not to Downloads. A `SHA256SUMS` with a wrong hash stopped the block. Against the published v0.1.0 release, on 2026-11-13, with the home folder replaced by a test folder: both downloads, the hash check, the unpack and the rename passed, and the program exited 2 with its configuration error line; the bundle's lines were not run against it. |
| Login (README step 2) | The `check-login` line: pass line, exit 0. The `sqlcmd` lines were not run here; the script itself is run by `StdioServerTests.cs`. |
| Claude Desktop | Microsoft Store build of Claude Desktop, app build 5.11492.2.0 for the first install (version `0.0.0-dev`): double-click opened Windows' "choose an app" dialog; the first install window opened after `claude-desktop.exe "<full path to the .mcpb>"` was started (whether another step was taken in between was not confirmed), showed the unsigned-source warning and the version, and installed the bundle. The documented menu route, **Settings > Extensions > Advanced settings > Install Extension…**, was not shown to complete an install in this test. The host stored the connection string encrypted and passed it to the program correctly (a first attempt failed on a wrong paste into the field). The host started the server twice with the same settings, for chat and for Cowork and Code sessions; with one audit file the second process exited because the file was locked, and the app showed a warning; the chat-side process answered `initialize` and `tools/list`. The installed program, started directly with the same command, answered `list_tables` and refused a table without permission. Not shown: a tool call typed in the Claude Desktop chat window. Later on 2026-11-13, outside Claude Desktop on Windows, two processes of the changed program (sibling audit files) started together on one audit path each wrote a file of their own, exactly one printed the sibling line on stderr, and both files verified (`AuditSiblingProcessTests.cs`). Later the same day, in the Microsoft Store build of Claude Desktop (app build not recorded for this install), with a bundle rebuilt from the changed program (version `0.0.0-dev.2`): the maintainer, standing in for a first-time user, did not get the file selected in the file dialog of **Install Extension…** and cancelled; `claude-desktop.exe "<full path to the .mcpb>"` opened the install page at once, and the app log shows the extension installed. The **Configure** dialog showed each field's description in full above its field, about 140 words for the audit field (shortened since), and both fields empty; the maintainer found this harder, not easier. The packed server of that bundle, outside Claude Desktop: two processes on one audit path both answered `list_tables`, the second wrote `audit.2.jsonl`, and `verify-audit` exited 0 on both files. Not shown: a tool call typed in the Claude Desktop chat with the rebuilt bundle. |
| Claude Code | Claude Code 2.1.323: the README's line wrote a local-scope entry and `claude mcp list` reported `✔ Connected`; `verify-audit` exited 0. The `.mcp.json` results above are from the same day and version. |
| Codex CLI | codex-cli 0.168.0, with `CODEX_HOME` set to a test folder: `codex mcp list` and `codex mcp get` read the block with its `env_vars`. Codex itself did not start the server (that needs a session); the program started with the block's command and the variable set as `env_vars` would forward it answered `initialize` and `tools/list`. |
| VS Code, Cursor | Not tested in the clients. The program started with each block's command and variables answered `initialize` and `tools/list`, and `verify-audit` exited 0. |
| Linux and macOS blocks | Not run on Linux or macOS, nor against the published release. In Git Bash on Windows, on the linux-x64 archive: the checksum, unpack and rename lines passed, and a wrong hash failed the check (exit 1); the Claude Code line, with the Windows program, was listed as `✔ Connected`. |

Still open: a tool call typed in the Claude Desktop chat window, the rebuilt bundle's two processes in Claude
Desktop, the shortened field descriptions in its **Configure** dialog, and each other client starting the server
itself.

Sources, read on 2026-11-13:

- Claude Desktop: <https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop>,
  <https://claude.com/docs/connectors/custom/add-unlisted>,
  <https://modelcontextprotocol.io/docs/develop/connect-local-servers>
- Claude Code: <https://code.claude.com/docs/en/mcp>
- Codex CLI and the ChatGPT desktop app: <https://learn.chatgpt.com/docs/extend/mcp>
- VS Code: <https://code.visualstudio.com/docs/copilot/reference/mcp-configuration>
- Cursor: <https://cursor.com/docs/context/mcp>

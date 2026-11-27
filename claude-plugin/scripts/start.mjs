// Starts the user's own installation of mssql-readonly-mcp for Claude Code.
//
// The plugin ships no server code. This file finds the program `mssql-readonly-mcp` on the user's PATH
// (installed from the project's GitHub releases), hands it the two plugin options under the names the
// server reads, and connects its standard input and output straight to Claude Code. It opens no
// network connection, writes no file and downloads nothing.
//
// Claude Code sets the two options as CLAUDE_PLUGIN_OPTION_CONNECTION_STRING and
// CLAUDE_PLUGIN_OPTION_AUDIT_LOG (see .mcp.json). The server reads MSSQL_CONNECTION_STRING and
// MSSQL_MCP_AUDIT_LOG.

import { spawn } from "node:child_process";
import process from "node:process";

const PROGRAM = "mssql-readonly-mcp";

const OPTIONS = [
  ["CLAUDE_PLUGIN_OPTION_CONNECTION_STRING", "MSSQL_CONNECTION_STRING"],
  ["CLAUDE_PLUGIN_OPTION_AUDIT_LOG", "MSSQL_MCP_AUDIT_LOG"],
];

const env = { ...process.env };
// Windows treats variable names case-insensitively, so remove every spelling of a name.
const removeAll = (name) => {
  for (const key of Object.keys(env)) {
    if (key.toUpperCase() === name) {
      delete env[key];
    }
  }
};
for (const [pluginName, serverName] of OPTIONS) {
  const value = process.env[pluginName];
  removeAll(pluginName);
  // A value inherited from the user's own environment must not stand in for an empty plugin option.
  removeAll(serverName);
  if (value !== undefined && value !== "") {
    env[serverName] = value;
  }
}

const child = spawn(PROGRAM, [], { env, stdio: "inherit", shell: false, windowsHide: true });

child.on("error", (error) => {
  if (error && error.code === "ENOENT") {
    process.stderr.write(
      `mssql-readonly-mcp plugin: the program '${PROGRAM}' was not found on PATH. Install it from ` +
        "https://github.com/oguzhanpisgin/mssql-readonly-mcp/releases/latest, put its folder on PATH, " +
        "and restart Claude Code (the plugin README has the steps).\n",
    );
    process.exit(127);
  }
  process.stderr.write(`mssql-readonly-mcp plugin: could not start '${PROGRAM}': ${error.message}\n`);
  process.exit(1);
});

for (const signal of ["SIGINT", "SIGTERM", "SIGHUP"]) {
  process.on(signal, () => {
    if (child.exitCode === null && child.signalCode === null) {
      try {
        child.kill(signal);
      } catch {
        // Some signals are not supported on Windows; the child still stops when its input ends.
      }
    }
  });
}

child.on("exit", (code, signal) => {
  if (signal) {
    process.exit(1);
  }
  process.exit(code ?? 1);
});

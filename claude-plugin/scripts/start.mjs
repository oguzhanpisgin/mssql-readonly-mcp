// Starts the user's own installation of mssql-readonly-mcp for Claude Code.
//
// The plugin ships no server code. This file starts the program `mssql-readonly-mcp` from the user's PATH
// (installed from the project's releases) and connects its standard input and output straight to Claude
// Code. Claude Code hands the program the two plugin options itself (see .mcp.json); this file does not
// read them. It opens no network connection, writes no file and downloads nothing.

import { spawn } from "node:child_process";
import process from "node:process";

const PROGRAM = "mssql-readonly-mcp";

const child = spawn(PROGRAM, [], { stdio: "inherit", shell: false, windowsHide: true });

child.on("error", (error) => {
  if (error && error.code === "ENOENT") {
    process.stderr.write(
      `mssql-readonly-mcp plugin: the program '${PROGRAM}' was not found on PATH. Install it from the ` +
        "project's GitHub releases, put its folder on PATH, and restart Claude Code (the plugin README " +
        "has the steps).\n",
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

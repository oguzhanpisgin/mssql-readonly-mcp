# Prior art, and when to prefer it

This page compares the project with two related servers and lists what was not seen elsewhere; the
[README](../README.md) has the overview.

Facts below are from public repositories and documentation read on 2026-11-11.

- **Microsoft's SQL MCP Server (part of Data API builder).** <https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/overview>.
  It has no free-form SQL tool by design: typed tools over entities you configure, with per-role permissions, and
  SQL generated from structured parameters. Prefer it when you can describe the data the assistant needs as a set
  of entities and you do not want the assistant to write SQL at all. Note that it is a create, read, update and
  delete tool: writes are switched off by configuration, not absent.
- **alyiox/mcp-mssql.** <https://github.com/alyiox/mcp-mssql>. C#, MIT, stdio. It parses with ScriptDom, requires
  exactly one SELECT in one batch, and rejects a list of unsafe node types; it has row and time caps with ceilings.
  It is the closest project to this one. Prefer it if its features fit your case, if you want write tools that a
  profile can switch on, or if you want a project with a release history (v1.8.0 on 2026-10-26).

This project does not claim to be first. These points were not seen in the ten servers read on 2026-11-11 (a
statement about the files read, not proof that nobody does them):

- an allowlist of syntax node types and function names, instead of a list of forbidden ones;
- a startup check that refuses a login holding any right listed in ["What `check-login` refuses"](login-check.md);
- a hash-chained audit line with the SQL text, written and flushed before the result is returned, failing closed;
- tests that replay the publicly reported bypass classes against a real SQL Server container;
- a session lock timeout and low deadlock priority for reports.

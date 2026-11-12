# Demo: one Claude Code run

This page shows one recorded run of Claude Code against the server; the [README](../README.md#five-minute-start)
has the setup it follows, with the differences named under "Repeat it".

It is one run, not a test and not a benchmark. Another run, or another model, may choose other calls and other SQL.

| | |
| --- | --- |
| Date | 2026-11-12 |
| Server commit | `991036ff782692fcd90545892743262a2c5b4d34`, Release build |
| Client | Claude Code 2.1.322, model `claude-sonnet-5-5`, started as `claude -p` with `--mcp-config` |
| SQL Server | Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64), container image `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` |
| Database | `DemoErp`, invented data only (script below) |
| Login | `mcp_reader`, made by `scripts/create-readonly-login.sql` with `SELECT` on four tables |

## Before the run

The database has five tables: `Customers`, `Products`, `Orders`, `OrderLines` and `Salaries`. The login was granted
`SELECT` on the first four only; `dbo.Salaries` has no grant. `check-login` printed:

```text
Login mcp_reader on database DemoErp: passed, no permission to change data was found.
```

and exited 0. Claude Code reported the server `mssql-readonly` as connected, with the tools `list_tables`,
`describe_table` and `run_query`.

## The question

The prompt, as sent:

```text
You have a read-only connection to a SQL Server database through the mssql-readonly tools. Use only those tools.
Do these three things and report each briefly in English:
1. List the tables you can see.
2. Which five customers have the highest order total in 2026 (quantity times unit price)? Show customer name,
   city, number of orders and total.
3. The price of the product 'Gear oil 5 L' should go up by 10 percent. Try to apply that with an UPDATE statement
   through the tool, and quote exactly what the server answered.
```

## The calls

Claude Code first loaded the three tool definitions with its own `ToolSearch` tool; that step is inside Claude Code
and does not reach the server. Then it made these calls. Each answer is the text the server returned. Answers marked
"trimmed" have parts replaced by `...`; no value was changed.

1. `list_tables`, no arguments:

   ```json
   {"tables":[{"schema":"dbo","name":"Customers","kind":"table"},{"schema":"dbo","name":"OrderLines","kind":"table"},{"schema":"dbo","name":"Orders","kind":"table"},{"schema":"dbo","name":"Products","kind":"table"}],"row_count":4,"total_count":4,"truncated":false,"truncated_reason":null}
   ```

2. `describe_table` with `{"table":"dbo.Customers"}`:

   ```json
   {"schema":"dbo","name":"Customers","kind":"table","columns":[{"name":"CustomerId","type":"int","nullable":false,"identity":false,"computed":false},{"name":"Name","type":"nvarchar(80)","nullable":false,"identity":false,"computed":false},{"name":"City","type":"nvarchar(40)","nullable":false,"identity":false,"computed":false}],"primary_key":{"name":"PK__Customer__A4AE64D86CD4C218","columns":["CustomerId"]},"foreign_keys":[],"column_count":3,"foreign_key_count":0,"truncated":false,"truncated_reason":null}
   ```

3. `describe_table` with `{"table":"dbo.Orders"}` (trimmed):

   ```json
   {"schema":"dbo","name":"Orders","kind":"table","columns":[...],"primary_key":{"name":"PK__Orders__C3905BCF79B4A0ED","columns":["OrderId"]},"foreign_keys":[{"name":"FK__Orders__Customer__3B75D760","columns":["CustomerId"],"references":{"schema":"dbo","table":"Customers","columns":["CustomerId"]}}],"column_count":3,"foreign_key_count":1,"truncated":false,"truncated_reason":null}
   ```

4. `describe_table` with `{"table":"dbo.OrderLines"}` (trimmed):

   ```json
   {"schema":"dbo","name":"OrderLines","kind":"table","columns":[...],"primary_key":{"name":"PK__OrderLin__8A0F39BFE276285E","columns":["OrderId","LineNumber"]},"foreign_keys":[...],"column_count":5,"foreign_key_count":2,"truncated":false,"truncated_reason":null}
   ```

5. `describe_table` with `{"table":"dbo.Products"}` (trimmed):

   ```json
   {"schema":"dbo","name":"Products","kind":"table","columns":[...],"primary_key":{"name":"PK__Products__B40CC6CDE363C07E","columns":["ProductId"]},"foreign_keys":[],"column_count":3,"foreign_key_count":0,"truncated":false,"truncated_reason":null}
   ```

6. `run_query` with this `sql` argument:

   ```sql
   SELECT TOP 5 c.Name, c.City, COUNT(DISTINCT o.OrderId) AS OrderCount, SUM(ol.Quantity * ol.UnitPrice) AS Total FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.CustomerId JOIN dbo.OrderLines ol ON ol.OrderId = o.OrderId WHERE o.OrderDate >= '2026-01-01' AND o.OrderDate < '2027-01-01' GROUP BY c.CustomerId, c.Name, c.City ORDER BY Total DESC
   ```

   ```json
   {"columns":[{"name":"Name","type":"nvarchar"},{"name":"City","type":"nvarchar"},{"name":"OrderCount","type":"int"},{"name":"Total","type":"decimal"}],"rows":[["Customer 06","Izmir",10,11843.50],["Customer 13","Konya",10,11131.50],["Customer 12","Bursa",10,10743.25],["Customer 05","Ankara",10,10611.50],["Customer 14","Adana",10,9469.50]],"row_count":5,"truncated":false,"truncated_reason":null}
   ```

7. `run_query` with this `sql` argument:

   ```sql
   UPDATE dbo.Products SET UnitPrice = UnitPrice * 1.10 WHERE Name = 'Gear oil 5 L'
   ```

   The answer is an error result (`is_error: true`) with this exact text:

   ```text
   NotSelect: refused by the read-only gate; UpdateStatement at line 1, column 1
   ```

## The answer

The assistant's final answer, in full, as Markdown source:

```markdown
**1. Tables visible (all in `dbo`):** `Customers`, `OrderLines`, `Orders`, `Products`.

**2. Top 5 customers by 2026 order total (Quantity × UnitPrice from `OrderLines`):**

| Customer | City | Orders | Total |
|---|---|---|---|
| Customer 06 | Izmir | 10 | 11,843.50 |
| Customer 13 | Konya | 10 | 11,131.50 |
| Customer 12 | Bursa | 10 | 10,743.25 |
| Customer 05 | Ankara | 10 | 10,611.50 |
| Customer 14 | Adana | 10 | 9,469.50 |

**3. UPDATE attempt:** I ran `UPDATE dbo.Products SET UnitPrice = UnitPrice * 1.10 WHERE Name = 'Gear oil 5 L'` through `run_query`. The tool answered exactly:

`NotSelect: refused by the read-only gate; UpdateStatement at line 1, column 1`

The gate refused it before it reached SQL Server, so the price is unchanged. The connection is read-only, so I did not try another route. Someone with write access will need to run this update.
```

## The audit file

The run wrote eight lines: the `startup` line, one `list_tables`, four `describe_table` and two `run_query` lines.

| `seq` | `tool` | `decision` | `reason` | `rows` |
| --- | --- | --- | --- | --- |
| 1 | `startup` | `allowed` | `Allowed` | null |
| 2 | `list_tables` | `allowed` | `Allowed` | 4 |
| 3 to 6 | `describe_table` | `allowed` | `Allowed` | 3, 3, 5, 3 |
| 7 | `run_query` | `allowed` | `Allowed` | 5 |
| 8 | `run_query` | `refused` | `NotSelect` | null |

The lines for the SELECT and for the refused UPDATE, with the hash values shortened to their first eight characters
and `...`:

```json
{"seq":7,"ts":"2026-11-12T05:09:12.018Z","session":"be3bfc937428448ea1761ff5c3618a4e","tool":"run_query","decision":"allowed","reason":"Allowed","sql":"SELECT TOP 5 c.Name, c.City, COUNT(DISTINCT o.OrderId) AS OrderCount, SUM(ol.Quantity * ol.UnitPrice) AS Total FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.CustomerId JOIN dbo.OrderLines ol ON ol.OrderId = o.OrderId WHERE o.OrderDate >= '2026-01-01' AND o.OrderDate < '2027-01-01' GROUP BY c.CustomerId, c.Name, c.City ORDER BY Total DESC","sql_sha256":"b39fc4a8...","tables":["dbo.Customers","dbo.Orders","dbo.OrderLines"],"rows":5,"truncated":false,"duration_ms":75,"result_sha256":"0fc12c93...","prev_hash":"63277712...","hash":"03ca2ee4..."}
{"seq":8,"ts":"2026-11-12T05:09:12.301Z","session":"be3bfc937428448ea1761ff5c3618a4e","tool":"run_query","decision":"refused","reason":"NotSelect","sql":"UPDATE dbo.Products SET UnitPrice = UnitPrice * 1.10 WHERE Name = 'Gear oil 5 L'","sql_sha256":"a18e8d47...","tables":["dbo.Products"],"rows":null,"truncated":false,"duration_ms":3,"result_sha256":null,"prev_hash":"03ca2ee4...","hash":"6d196330..."}
```

`verify-audit` on the file printed this line and exited 0:

```text
Audit file audit.jsonl: 8 lines verified.
```

The run's password for `mcp_reader` did not appear in the Claude Code output, the audit file or the login script's
output (a text search for it after the run).

## What this run shows

- **The write was refused by the gate, inside the server process.** The answer to the `UPDATE` is a gate refusal
  (`NotSelect: refused by the read-only gate; ...`), and its audit line has no rows and no result hash. This run
  did not trace SQL Server itself; that a refused statement opens no connection and leaves no event on the server is
  what `GateRefusalTests.cs` and `ExtendedEventsTests.cs` check (see [the promises](promises.md)).
- **The refused call is in the audit file** (line 8, with its SQL text), and the file verified.
- **The table without a grant was not listed.** `dbo.Salaries` exists in the database, but `list_tables` returned
  four tables, with `total_count` 4, and `Salaries` is not among them.
- **The login made by the repository's script passed `check-login`.**

## What this run does not show

- It used `claude -p` with `--mcp-config` and `--strict-mcp-config`, so the approval of a project `.mcp.json` in an
  interactive session ("Pending approval", README step 4) was not exercised, and neither was Claude Desktop.
- It did not read the price again after the refusal. "The price is unchanged" in the final answer is the assistant's
  own conclusion from the refusal.
- It shows one model on one SQL Server version, with the seven calls this model made in this run.

## Repeat it

<details>
<summary>The demo database script and the steps</summary>

The script that created the database (`demo-db.sql`), as run:

```sql
-- Demo database for the first live Claude Code call. Invented data only.
SET NOCOUNT ON;
IF DB_ID(N'DemoErp') IS NULL CREATE DATABASE [DemoErp];
GO
USE [DemoErp];
GO
CREATE TABLE dbo.Customers (
    CustomerId int NOT NULL PRIMARY KEY,
    Name nvarchar(80) NOT NULL,
    City nvarchar(40) NOT NULL);
CREATE TABLE dbo.Products (
    ProductId int NOT NULL PRIMARY KEY,
    Name nvarchar(80) NOT NULL,
    UnitPrice decimal(10,2) NOT NULL);
CREATE TABLE dbo.Orders (
    OrderId int NOT NULL PRIMARY KEY,
    CustomerId int NOT NULL REFERENCES dbo.Customers(CustomerId),
    OrderDate date NOT NULL);
CREATE TABLE dbo.OrderLines (
    OrderId int NOT NULL REFERENCES dbo.Orders(OrderId),
    LineNumber int NOT NULL,
    ProductId int NOT NULL REFERENCES dbo.Products(ProductId),
    Quantity int NOT NULL,
    UnitPrice decimal(10,2) NOT NULL,
    PRIMARY KEY (OrderId, LineNumber));
CREATE TABLE dbo.Salaries (
    EmployeeId int NOT NULL PRIMARY KEY,
    Name nvarchar(80) NOT NULL,
    MonthlySalary decimal(10,2) NOT NULL);
GO
INSERT dbo.Customers (CustomerId, Name, City)
SELECT v.n, CONCAT(N'Customer ', RIGHT(CONCAT(N'00', v.n), 2)),
       CHOOSE(1 + v.n % 5, N'Ankara', N'Izmir', N'Bursa', N'Konya', N'Adana')
FROM (SELECT TOP (20) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS v;

INSERT dbo.Products (ProductId, Name, UnitPrice)
VALUES (1, N'Steel bolt M8', 0.45), (2, N'Steel nut M8', 0.20), (3, N'Washer 8 mm', 0.05),
       (4, N'Bearing 6204', 6.80), (5, N'Drive belt A42', 9.50), (6, N'Hydraulic hose 2 m', 24.00),
       (7, N'Gear oil 5 L', 38.00), (8, N'Safety gloves', 4.25);

INSERT dbo.Orders (OrderId, CustomerId, OrderDate)
SELECT v.n, 1 + (v.n * 7) % 20, DATEADD(DAY, (v.n * 3) % 300, CONVERT(date, '2026-01-05'))
FROM (SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS v;

INSERT dbo.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
SELECT o.OrderId, l.LineNumber, p.ProductId, 1 + (o.OrderId * l.LineNumber) % 40, p.UnitPrice
FROM dbo.Orders AS o
CROSS JOIN (VALUES (1), (2), (3)) AS l(LineNumber)
JOIN dbo.Products AS p ON p.ProductId = 1 + (o.OrderId + l.LineNumber * 3) % 8;

INSERT dbo.Salaries (EmployeeId, Name, MonthlySalary)
VALUES (1, N'Employee A', 4100.00), (2, N'Employee B', 3900.00);
GO
```

The steps, with the README's commands except where the run differed (named at the end):

1. A SQL Server 2022 the machine can reach. The run used the container image named above, with the test-server
   settings of README step 3: `-C` with `sqlcmd` and `TrustServerCertificate=True` in the connection string.
2. Build the server (README step 1).
3. Save the script above as `demo-db.sql` (the file is not in the repository). Run it with `sqlcmd` as an
   administrator, the same way as the login script in README step 2.
4. Create the login (README step 2) with `MCP_DATABASE` set to `DemoErp` and the `GRANT` lines replaced by:

   ```sql
   GRANT SELECT ON OBJECT::[dbo].[Customers] TO [$(MCP_LOGIN)];
   GRANT SELECT ON OBJECT::[dbo].[Orders] TO [$(MCP_LOGIN)];
   GRANT SELECT ON OBJECT::[dbo].[OrderLines] TO [$(MCP_LOGIN)];
   GRANT SELECT ON OBJECT::[dbo].[Products] TO [$(MCP_LOGIN)];
   ```

5. Run `check-login` (README step 3); it should print the pass line shown above.
6. Register the server with Claude Code (README step 4). The run did not use a project `.mcp.json`: it passed a
   file with an `mcpServers` entry of the same form, pointing at the run's build, to `claude -p --mcp-config`, with
   `--strict-mcp-config`,
   `--allowedTools mcp__mssql-readonly`, `--model claude-sonnet-5-5`,
   `--append-system-prompt "Answer in English only."`, `--no-session-persistence`, `--disable-slash-commands` and
   `--output-format stream-json --verbose`, and sent the prompt above on standard input.
7. Run `verify-audit` on the audit file ([The audit log](audit-log.md)).

In the run, the server was published with `dotnet publish src/MssqlReadOnlyMcp -c Release` instead of built with
`dotnet build`, and both SQL files ran with `sqlcmd` inside the container, as the administrator login; the
PowerShell lines of the README were not used.

</details>

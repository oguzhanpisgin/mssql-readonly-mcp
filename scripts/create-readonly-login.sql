/*
create-readonly-login.sql

Creates the SQL Server login the MCP server runs as, a user for it in one database, and SELECT grants on the
tables and views listed in the EDIT section below. It adds the user to no role: not db_datareader (SQL Server makes
that role the owner of the schema of the same name, so its members can create objects there), not any other.

Before you run it:
  1. Edit the GRANT lines in the EDIT section: one line per table or view the assistant may read.
  2. Set three sqlcmd variables. sqlcmd reads them from environment variables, so the password is neither in this
     file nor on a command line:
       MCP_LOGIN     login and user name, for example mcp_reader (no ] character)
       MCP_DATABASE  the one database the assistant may read
       MCP_PASSWORD  the new login's password (no ' character)

Run it as an administrator, from PowerShell 7:
  $env:MCP_LOGIN = 'mcp_reader'
  $env:MCP_DATABASE = 'ExampleDb'
  $env:MCP_PASSWORD = Read-Host -MaskInput 'New password for mcp_reader'
  sqlcmd -S sql.example.local -E -b -i scripts/create-readonly-login.sql
  Remove-Item Env:MCP_PASSWORD

Afterwards this must pass (exit code 0), with MSSQL_CONNECTION_STRING set for the new login:
  mssql-readonly-mcp check-login

Running it again is safe: an existing login keeps its password, an existing user is reused, and GRANT is
idempotent. It stops with an error if the existing user belongs to another login or is a member of any role,
because check-login would refuse that user; remove the membership yourself if you mean to. A GRANT on an object
that does not exist also stops the script; fix the line and run it again.
*/

:on error exit
SET NOCOUNT ON;
GO

USE [master];
GO

IF SUSER_ID(N'$(MCP_LOGIN)') IS NULL
BEGIN
    CREATE LOGIN [$(MCP_LOGIN)]
        WITH PASSWORD = N'$(MCP_PASSWORD)',
             DEFAULT_DATABASE = [$(MCP_DATABASE)],
             CHECK_POLICY = ON;
    PRINT N'Created login $(MCP_LOGIN).';
END
ELSE
    PRINT N'Login $(MCP_LOGIN) exists; its password was not changed.';
GO

USE [$(MCP_DATABASE)];
GO

IF DATABASE_PRINCIPAL_ID(N'$(MCP_LOGIN)') IS NULL
BEGIN
    CREATE USER [$(MCP_LOGIN)] FOR LOGIN [$(MCP_LOGIN)] WITH DEFAULT_SCHEMA = [dbo];
    PRINT N'Created user $(MCP_LOGIN) in database $(MCP_DATABASE).';
END
ELSE IF NOT EXISTS (
    SELECT 1
    FROM sys.database_principals AS d
    WHERE d.principal_id = DATABASE_PRINCIPAL_ID(N'$(MCP_LOGIN)') AND d.sid = SUSER_SID(N'$(MCP_LOGIN)'))
    THROW 50001, N'A user with this name exists in the database but belongs to another login. Nothing was granted.', 1;
ELSE
    PRINT N'User $(MCP_LOGIN) exists in database $(MCP_DATABASE); reused.';
GO

IF EXISTS (
    SELECT 1
    FROM sys.database_role_members AS m
    WHERE m.member_principal_id = DATABASE_PRINCIPAL_ID(N'$(MCP_LOGIN)'))
    THROW 50002, N'The user is a member of a database role. check-login would refuse it. Remove the membership first. Nothing was granted.', 1;
GO

-- ===================================================================================================================
-- EDIT: the tables and views the assistant may read. One line per object. Keep only SELECT.
-- Every name here is an example; replace them with your own objects.
-- To grant a whole schema instead, use: GRANT SELECT ON SCHEMA::[reporting] TO [$(MCP_LOGIN)];
-- check-login refuses such a grant on a schema that holds a temporal or ledger history table (docs/login-check.md).
-- Check views and synonyms before you grant them: they can read other tables or other databases (see SECURITY.md).
-- ===================================================================================================================
GRANT SELECT ON OBJECT::[dbo].[ExampleCustomers] TO [$(MCP_LOGIN)];
GRANT SELECT ON OBJECT::[dbo].[ExampleOrders] TO [$(MCP_LOGIN)];
GRANT SELECT ON OBJECT::[dbo].[ExampleOrderLines] TO [$(MCP_LOGIN)];
-- ===================================================================================================================
-- END OF EDIT
-- ===================================================================================================================
GO

PRINT N'Permissions now held by user $(MCP_LOGIN) in database $(MCP_DATABASE):';

SELECT
    p.state_desc AS state,
    p.permission_name AS permission,
    p.class_desc AS scope,
    CASE p.class
        WHEN 0 THEN QUOTENAME(DB_NAME())
        WHEN 1 THEN QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN QUOTENAME(SCHEMA_NAME(p.major_id))
        ELSE CONVERT(nvarchar(20), p.major_id)
    END AS securable
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'$(MCP_LOGIN)')
ORDER BY scope, securable, permission;

PRINT N'Role memberships of user $(MCP_LOGIN) (expected: none):';

SELECT r.name AS role_name
FROM sys.database_role_members AS m
JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
WHERE m.member_principal_id = DATABASE_PRINCIPAL_ID(N'$(MCP_LOGIN)');
GO

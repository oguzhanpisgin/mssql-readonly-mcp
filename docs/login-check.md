# What `check-login` refuses

This page covers what the startup login check refuses, what it allows and what it does not inspect; the
[README](../README.md#2-create-and-check-the-read-only-login) shows how to run it.

The server runs the check once, when it starts (`src/MssqlReadOnlyMcp/Program.cs`, `ServerStartup.cs`); nothing
checks again later. Rights granted afterwards to the login, to a role it is in or to `public` are not seen until
the server restarts, and until then the gate is the only layer against them. The server sets
`ApplicationIntent=ReadOnly` (`ServerConfig.cs`), so behind an availability group listener with read-only routing
the check and later calls could reach different replicas, whose server-level rights can differ; this is not tested.

The check runs one query (`src/MssqlReadOnlyMcp/PermissionCheck.cs`) and refuses the login when it finds any of:

- an effective server permission other than `CONNECT SQL` and `VIEW ANY DATABASE`;
- an effective permission on the connection's database other than `CONNECT`,
  `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` and `VIEW ANY COLUMN MASTER KEY DEFINITION`;
- membership of any fixed server role other than `public`, or of `db_owner`, `db_datawriter`, `db_ddladmin`,
  `db_securityadmin`, `db_accessadmin` or `db_backupoperator`;
- below database scope, on every object of the asked types in the connection's database's `sys.objects`, those
  SQL Server marks as shipped (`is_ms_shipped = 1`) included: write, alter, control and ownership rights on tables,
  views and inline functions, column `UPDATE`, sequences, procedures and functions (also `EXECUTE`), and synonyms
  (also `EXECUTE`);
- write, alter, control and ownership rights on schemas (also `EXECUTE` and `CREATE ...`), `ALTER`, `CONTROL` and
  `TAKE OWNERSHIP` on roles, `ALTER` and `CONTROL` on application roles (scope `application role`), `IMPERSONATE`
  on users and logins, and `ALTER`, `CONTROL` and `TAKE OWNERSHIP` on user-defined server roles (scope
  `server role`, with the role's name);
- on Service Broker queues, the built-in ones in schema `dbo` included (three in a user database), a fixed list:
  `RECEIVE`, `ALTER`, `CONTROL` and `TAKE OWNERSHIP` (scope `queue`);
- on services, certificates, asymmetric keys, symmetric keys, assemblies (the built-in `Microsoft.SqlServer.Types`
  included), user types, XML schema collections outside schema `sys`, full-text catalogs, message types,
  contracts, remote service bindings, routes, full-text stoplists, search property lists, database scoped
  credentials and endpoints (scopes `service`, `certificate`, `asymmetric key`, `symmetric key`, `assembly`, `type`,
  `xml schema collection`, `full-text catalog`, `message type`, `contract`, `remote service binding`, `route`,
  `full-text stoplist`, `search property list`, `database scoped credential`, `endpoint`): every right SQL Server
  lists for the class (`sys.fn_builtin_permissions`) except `VIEW DEFINITION`, `REFERENCES`, `EXECUTE` on a table
  type, an alias type or an XML schema collection, and `CONNECT` on an endpoint; an owner holds `CONTROL` without a
  grant row and is found here;
- as a backstop, any right granted (state `G` or `W`) on a securable of the connection's database
  (`sys.database_permissions`) or of the server (`sys.server_permissions`), whatever its class, to the login, to a
  role or group it is in, or to `public`, except `CONNECT` and `VIEW DEFINITION`, and on a database securable also
  `SELECT`, `REFERENCES`, `VIEW CHANGE TRACKING`, `UNMASK` and `EXECUTE` on a table type, an alias type or an XML
  schema collection, and, in `master` only, `public`'s `EXECUTE` on system objects (see the backstop paragraphs
  below). Database-wide and server-wide grants are left to the first two items;
- any right those items allow, held `WITH GRANT OPTION` (state `W`) by the login, a role or group it is in, or
  `public`, also the rights allowed on the database and the server: the login can grant it to others. The finding
  is `<right> WITH GRANT OPTION on <scope> [<name>]`, for example `SELECT WITH GRANT OPTION on schema [dbo]`,
  `CONNECT WITH GRANT OPTION on database ShopDb` or `VIEW ANY DATABASE WITH GRANT OPTION on server`. A refused
  right held `WITH GRANT OPTION` keeps its one finding (`INSERT on object [dbo].[Customers]`).

The server and database allowed sets are what a login made by `scripts/create-readonly-login.sql` holds on SQL
Server 2019, 2022 and 2025 (code comment in `PermissionCheck.cs`). Any other permission is a finding by its own
name, so one that a later SQL Server version adds is refused without a code change. "Effective"
(`sys.fn_my_permissions`) includes rights that roles and higher scopes imply, and rights that only widen reading:
database-wide `SELECT`, `UNMASK`, `CONNECT ANY DATABASE` and `SELECT ALL USER SECURABLES` are refused too, as are
read-only database grants such as `VIEW DEFINITION`, `SHOWPLAN` and `VIEW DATABASE STATE`, so a platform that grants
such extras by default will see its logins refused. The query counts a NULL answer as a finding
(`ISNULL(state, 1) = 1`, printed as `could not be checked`), never a pass; its test
(`Null_answer_is_a_finding_named_could_not_be_checked`) checks only how the finding is printed, and no test produces
a NULL answer.
`PermissionCheckTests.cs` covers the reader passing and, among others, `sa`, `db_owner`, `db_datawriter`, single
table and column `UPDATE`, `IMPERSONATE`, `ALTER TRACE` (with the `SHOWPLAN` it implies), `BACKUP DATABASE`,
`UNMASK`, `TAKE OWNERSHIP` on a role and `UPDATE` on a synonym (`UPDATE on synonym [dbo].[CustomerAlias]`).
`CONNECT ANY DATABASE`, `SELECT ALL USER SECURABLES`, `VIEW DEFINITION` and `VIEW DATABASE STATE` follow from the
rule and have no test of their own. A synonym counts because, with an unbroken ownership chain, `UPDATE` granted on
the synonym alone changes the table it names, measured on 2019, 2022 and 2025 (`ValueTypeTests.cs`,
`Update_grant_on_a_synonym_alone_lets_the_login_change_the_table_it_names`; inside this server the call still fails
as `RowsChanged` and is rolled back). The check reports such a grant even when the synonym's target is in another
database, on a linked server or behind a broken ownership chain, where SQL Server also asks for the right on the
target (not tested here); the login is then refused although it may not be able to write through that synonym.

Objects that SQL Server marks as shipped (`is_ms_shipped = 1`) in the connected database are asked like any other
object of the asked types. Two cases were measured on 2019, 2022 and 2025 (`PermissionCheckTests.cs`): a procedure
and a table marked with `sp_MS_marksystemobject`, where a login with `EXECUTE` on the procedure and `INSERT`,
`DELETE` on the table gets `DELETE on object [dbo].[MarkedNotes]`, `EXECUTE on object [dbo].[AddLedger]` and
`INSERT on object [dbo].[MarkedNotes]` and can run the procedure (it writes a row) and the insert; and the tables
that change data capture adds in schema `cdc` (`sys.sp_cdc_enable_db`), where `DELETE` on `cdc.ddl_history` gives
`DELETE on object [cdc].[ddl_history]` and the delete runs. The reader can do none of it (error 229).

Connected to `msdb`, a login with a user there is refused (measured for a reader with `SELECT` on schema `dbo`;
every user is in `public`), because `public` may execute shipped procedures and functions in schema `dbo` there:
`fn_sysdac_is_currentuser_sa`, `fn_sysdac_is_dac_creator`, `fn_sysdac_is_login_creator`,
`fn_syspolicy_is_automation_enabled`, ten procedures named `sp_sysdac_*` and, on 2022 and 2025,
`sp_sysutility_cag_create_db`: 14 objects on 2019 and 15 on 2022 and 2025, reported as one finding,
`EXECUTE on object [dbo].[fn_sysdac_is_currentuser_sa], ...`, that names the first ten and ends `and 4 more` on
2019 or `and 5 more` on 2022 and 2025. A login without a user in `master` or `msdb` enters as `guest` and is
refused for schema `[guest]` (`ALTER`, `CONTROL`, `CREATE SEQUENCE`, `DELETE`, `EXECUTE`, `INSERT`,
`TAKE OWNERSHIP` and `UPDATE`, measured). Point the server at the database it reads, with a user for the login
there.

A finding on a shipped object that you did not grant to the login comes from a grant that reaches it another way,
for example through `public` or a role it is in, as in `msdb`; find it in that database's
`sys.database_permissions`. A grant to `public` reaches every user there. A plain reader (a user with `SELECT` on
schema `dbo`) had no such finding, and no finding at all, in `master`, in a new user database, in a database with
change data capture enabled (`sys.sp_cdc_enable_db`), in a database where two tables were also enabled for capture
with `sys.sp_cdc_enable_table`, one with a gating role and one without (SQL Server Agent was not running and did
not have to run), and in a database with change tracking enabled on the database and on one table. With captured
tables SQL Server grants `SELECT` on the change functions and on `cdc.lsn_time_mapping` to `public` or to the
gating role, a right the check allows. Measured on the three versions. A database with replication or with
database diagrams was not measured.

A plain reader is refused in a database with a system-versioned temporal table, and on 2022 and 2025 with an
updatable ledger table. SQL Server reports `INSERT` on the history table for a login that holds `SELECT` on it
(`sys.fn_my_permissions` lists `SELECT` and `INSERT`), so the check finds `INSERT on object` naming the history
table (`[dbo].[PricesHistory]`, `[dbo].[MSSQL_LedgerHistoryFor_<object_id>]`; one finding names both when both are
in the database). On SQL Server 2019 that answer holds for the temporal history table: as the plain reader,
`INSERT INTO @t OUTPUT ... INTO dbo.PricesHistory` ran and added a row, seen inside the transaction, which was
rolled back. On 2022 and 2025 that statement failed with error 13559. On all three versions a direct `INSERT`,
`INSERT ... SELECT` and an insert through a view or a synonym failed with error 13559, `MERGE` with 13562, `UPDATE`
with 13561 and `DELETE` with 13560, for `sa` too; on the ledger history table `INSERT` and `OUTPUT ... INTO` failed
with error 37361, also for `sa`. The check refuses the reader on all three versions. `DENY INSERT` on the history
table, or on the schema, did not change that answer: the reader was still refused. A reader granted `SELECT` on the
current tables only passes, but
cannot read the history (`FOR SYSTEM_TIME ALL` fails with error 229). No grant was found that lets a login read the
history and pass the check. Measured on the three versions (the ledger table on 2022 and 2025). In such a
database, grant `SELECT` on the current tables by name, as `scripts/create-readonly-login.sql` does, not on their
schema; a login that passes the check cannot read the history.

`ALTER` on an application role lets the login change the role's default schema; a new name, a new password or
`DROP` failed with error 15151 under that right (SQL Server documents `ALTER ANY APPLICATION ROLE` for them).
`CONTROL` granted alone on an application role is found as `ALTER` and `CONTROL`, and allowed the same act and
none of the other three. `ALTER` on a user-defined server role lets the login add itself as a member, and a member
holds the role's rights (measured with a role holding `ALTER ANY LOGIN`). `CONTROL` granted alone on such a role is
found as `ALTER`, `CONTROL` and `TAKE OWNERSHIP` and also let the login add itself; `TAKE OWNERSHIP` granted alone
is found as itself and let the login make itself the role's owner (`ALTER AUTHORIZATION`). The findings name the
role (`ALTER on application role [reporting_app]`, `ALTER on server role [mcp_login_admins]`). Each act was rolled
back, the plain reader got error 15151 for each, and all of it was measured on the three versions; what an owner
of a server role can do after taking it was not measured. These rights are not asked of the fixed server roles
and `public`: SQL Server refuses those three grants on them (error 15151). Membership of the fixed server roles is
asked instead.

A `db_datareader` member is refused: the role gives `SELECT` on the whole database, and SQL Server makes the role
the owner of schema `db_datareader`, so its members can create objects there (`CONTROL on schema [db_datareader]`,
`CREATE SEQUENCE on schema [db_datareader]`, `SELECT on database ...`). `PermissionCheckTests.cs` shows that such a
member really can create a sequence in that schema. Grant SELECT on named tables, views or schemas instead, as
`scripts/create-readonly-login.sql` does.

The class rules were measured on 2019, 2022 and 2025 by granting each right alone and trying what it enables.
The queue list is fixed because SQL Server cannot grant `INSERT`, `UPDATE`, `DELETE` or `EXECUTE` on a queue (error
4606) and refuses `INSERT` and `DELETE` on one even for `sa` (error 416); `RECEIVE` takes messages off it, and `SELECT`
only reads it. The built-in queues are asked like user ones (three in a user database or `master`, six in
`msdb`): `RECEIVE` on `dbo.QueryNotificationErrorsQueue` lets the login begin a dialog from that queue's built-in
service, owned by `dbo`, and send a message to a user service's queue. For the other sixteen classes the rule is the
one used at server and database scope: allow what is listed, refuse the rest, so a right that a later version adds
is refused without a code change. The built-in assembly `Microsoft.SqlServer.Types` (owner `sys`) is asked too.
SQL Server accepts a grant of `ALTER`, `CONTROL` or `TAKE OWNERSHIP` on it, and the check finds each. On this system
assembly SQL Server still refuses `ALTER ASSEMBLY` and `DROP ASSEMBLY` to all three logins (error 15151; `DROP`
under `CONTROL` gives 6560, "a system assembly"). `ALTER AUTHORIZATION` works under `CONTROL` or `TAKE OWNERSHIP`
and makes the login the assembly's owner; under `ALTER` alone it fails with 15151. Built-in types and the XML schema
collection `sys.sys` are not asked because SQL Server refuses every right the class lists on them (error 15151 on a
built-in type, 4606 on `sys.sys`). `VIEW DEFINITION` and `REFERENCES` changed nothing and ran no code: on a
certificate or an asymmetric key they let the login encrypt with the public key only, on a symmetric key protected
by a password they let a login that knows the password use the key (see the limits below), and the `CREATE`
statements that use `REFERENCES` on an assembly, a type or an XML schema collection were refused for want of the
`CREATE` right (error 262). `EXECUTE` on a table type or an XML schema collection only declares a variable, and on
an alias type it adds nothing; `EXECUTE` on a CLR type runs the assembly's code and is a finding
(`EXECUTE on type [dbo].[Point]`). An owner holds `CONTROL`, which is a finding.
Message types, contracts, remote service bindings, routes, full-text stoplists, search property lists, database
scoped credentials and endpoints are asked so that an owner is found, who holds `CONTROL` without a grant row.
Measured on the three versions for each of these eight classes: the owner (made so with `ALTER AUTHORIZATION`) saw
the securable, `HAS_PERMS_BY_NAME` answered 1 for every right the class lists, the check found
`ALTER`, `CONTROL` and `TAKE OWNERSHIP` on it, and the owner ran a change the reader could not (listed below).
`VIEW DEFINITION` and `REFERENCES` are allowed on them as on the other classes, and `CONNECT` on an endpoint is
allowed because `public` holds it on the T-SQL endpoints. A member of a role that owns a securable is found the
same way: measured on the three versions for a schema, a message type and a full-text stoplist owned by a database
role, and for a database mirroring endpoint and a user-defined server role owned by a server role
(`PermissionCheckTests.cs`; no act was run through a role). Ownership through a nested role counts too: the check
named `ALTER`, `CONTROL` and `TAKE OWNERSHIP` on the route for a member of a role inside the role that owns it
(measured on the three versions; no act; `PermissionCheckTests.cs`,
`Member_of_a_role_inside_the_role_that_owns_a_route_is_named_for_it`).

`SEND` on a service lets a dialog begun in a `TRUSTWORTHY` database put a message on the service's queue. `SEND`
alone does not make the service visible to the login, so the backstop (below) finds the grant in
`sys.database_permissions` and names the service by id, once however many grants reach the login:
`SEND on service id <service_id>`. Some Service Broker setups grant `SEND` on
a service to `public`; that grant reaches every login, so every login is refused in that database (measured for the
plain reader: `SEND on service id 65536`). As with a platform that grants extras by default, revoke it from
`public` and grant `SEND` to the senders that need it, or point the server at another database.

The check finds these securables in the catalog views, which show a login only those it holds some right on.
Measured on all three versions, a single grant of any right the check refuses on these classes made the securable
visible, except `SEND` on a service (read from `sys.database_permissions`, as above); of the allowed rights,
`EXECUTE` on a table type made it visible and `EXECUTE` on an XML schema collection did not. Visibility under
`VIEW DEFINITION` or `REFERENCES` alone was not measured. The built-in queues and `Microsoft.SqlServer.Types` are
visible to the plain reader. The owner of a message type, a contract, a remote service binding, a route, a
full-text stoplist, a search property list, a database scoped credential or an endpoint saw it and the plain
reader did not (measured on the three versions).

`PermissionCheckTests.cs` gives one or more logins per class their exact findings (a built-in queue and the
built-in assembly included, and a login holding `SEND` both directly and through a role, named once), passes a
login holding `VIEW DEFINITION` on each of these classes, `REFERENCES` on each except the queue and the service (a
service has no `REFERENCES` right; the queue has one, not granted to this login) and `EXECUTE` on a table type, an
alias type and an XML schema collection (the eight classes added for owners have no such login: that
`VIEW DEFINITION` and `REFERENCES` pass on them follows from the rule, no test), and runs at least one act for each
refused login except the two that hold
`SEND` through a role, while the reader cannot: a message received, a message sent from a built-in queue's service,
a service's contract dropped, a certificate signing, a private key removed, a symmetric key's owner changed, the
built-in assembly's owner changed, an assembly file added, a CLR method run, an XML schema collection altered, a
catalog rebuilt, a message delivered from another database. The other found rights (for example `TAKE OWNERSHIP`
on a certificate or a catalog) have no act of their own. The owners of the eight classes added for owners get
`ALTER`, `CONTROL` and `TAKE OWNERSHIP` on their securable and each runs one change while the reader cannot: a
message type's validation changed, a contract dropped, a remote service binding's `ANONYMOUS` setting changed, a
route's address changed, a credential's identity changed, a stoplist dropped and created again, a property added
to a search property list, an endpoint's port changed and set back. A member of a role that owns a schema, a
message type, a full-text stoplist, an endpoint or a server role, or a route through a nested role, gets its exact
findings, with no act.

Limits of the class rules:

- `REFERENCES` on a full-text catalog is allowed; what it is for, `CREATE FULLTEXT INDEX`, could not run in the
  test images because full-text search is not installed, so that use was not measured.
- `REFERENCES` or `VIEW DEFINITION` on a symmetric key protected by a password, plus that password, opens the key:
  the login can then encrypt and decrypt with it (measured). The check allows it because it changes nothing,
  although it widens what the login can read, unlike `UNMASK` or database-wide `SELECT`, which the check refuses.
  Do not give such a login the key's password unless it should read that data.
- On a queue the class rule asks only `RECEIVE`, `ALTER`, `CONTROL` and `TAKE OWNERSHIP`; a right a later SQL
  Server version adds on a queue is not asked by that rule, and a grant of it is found only by the backstop (not
  measured).
- `REFERENCES` on a message type, a contract, a full-text stoplist, a search property list or a database scoped
  credential is allowed; what it enables was not measured.
- The owner of an external model (SQL Server 2025) is not found: its catalog view, `sys.external_models`, is
  missing on 2019 and 2022, so the statement cannot ask it. Measured on 2025: the check reported nothing for such an
  owner, whose `ALTER EXTERNAL MODEL` ran and changed the model in a transaction that was rolled back (reader
  15151). Using the model takes a call to its endpoint over the network and was not measured (the server option
  `external rest endpoint enabled` was 0). The owner of an availability group, an external language or an
  external table (not among the object types the check asks) is not asked either; none could be created in the
  test images (see below). The other object types the check does not ask cannot take an owner of their own:
  `ALTER AUTHORIZATION` fails with 15344 on a default or a rule, 15346 on a constraint or a DML trigger, and 15151
  on an internal or system table; no such row had an owner in a user database, `master`, `msdb` or `tempdb`
  (measured on the three images; CLR triggers and edge constraints were not measured). A table type's owner is set
  with `ON TYPE::` and asked by the class question. On a default or a rule `HAS_PERMS_BY_NAME` answers 1 for
  `CONTROL` and `ALTER` to a login with `SELECT` on the schema, who is still refused `DROP` (3701) and
  `sp_bindefault` or `sp_bindrule` (297); the check does not ask those types (measured on the three images).
- In the backstop, a grant to a Windows or Microsoft Entra group is asked with `IS_MEMBER`; that branch was not
  measured (the test containers have no such principals).
- The backstop does not read a `DENY` next to a grant, so the grant is still reported.
- A `WITH GRANT OPTION` row whose grantee the login cannot see counts as not the login's: `HAS_PERMS_BY_NAME` does
  not tell a grant option apart from the right itself.

The backstop reads the grant rows the login can see. Measured on the three versions, those are its own grants,
those of a role it is in (also of a role that role is in: `ALTER on route [AutoCreatedLocal]`, granted to the
outer role, is found, `PermissionCheckTests.cs`) and those of `public`, each with the securable's name; a right on
a user, an application role or a login (`ALTER` or `VIEW DEFINITION`) and `CONTROL` on a type also show every
other principal's grant on that securable, also to principals the login cannot see. A row counts for the login
when it is the login's own grant, when `IS_MEMBER` (`IS_SRVROLEMEMBER` for a server role) answers 1 for the
grantee, or, when the grantee cannot be seen, when `HAS_PERMS_BY_NAME` answers 1 for the login on that object,
column, schema, database principal, type, login or server role. That path was exercised for a user, an
application role and a login only; for an object, a column and a schema it was not. A grant to another principal
the login can see does not count, and neither does another principal's grant on the login's own user or login,
where `HAS_PERMS_BY_NAME` answers 1 for `IMPERSONATE` (measured on the user). A row in state `W` (granted
`WITH GRANT OPTION`) of a right the check allows counts the same way, also one held by `public` (measured:
`SELECT WITH GRANT OPTION on object [dbo].[PublicNotes]`, and a refused right `public` holds so keeps its one
finding, `INSERT on object [dbo].[PublicNotes]`; `PermissionCheckTests.cs`; at server scope, after
`GRANT VIEW ANY DATABASE TO public WITH GRANT OPTION` (accepted), a plain reader got the finding
`VIEW ANY DATABASE WITH GRANT OPTION on server`, measured with sqlcmd on the three images, no test), except that
one whose grantee cannot be seen does not count (see the limits above). Any other row is a finding marked
`could not be checked`; no test produces one. A securable whose name the login cannot see is named by its id, and a
right that an earlier item also finds is printed once. A login holding `VIEW DEFINITION` on a user, an application
role and a login, each with other principals' grants on it, passes (`PermissionCheckTests.cs`).

In `master`, a default installation grants `public` `EXECUTE` on system objects in schema `sys`: 1623 on 2019,
1739 on 2022 and 1838 on 2025, all with negative object ids (measured on the three images). The backstop leaves
exactly those rows out, in `master` only: `public`'s `EXECUTE` on an object with a negative id. A grant of
`EXECUTE` on a system object to the login or to a role it is in is found (`EXECUTE on object [sys].[xp_regwrite]`;
for the login in `PermissionCheckTests.cs`, for a role by running the check's statement as a member, no test);
one made to `public` is not told apart from the default and is not found. In the other
databases measured (a new user database, `tempdb`, databases with change data capture, change tracking, a temporal
or a ledger table) `public` held no grant on system objects other than `SELECT`. In `msdb`, `public` held `SELECT`
grants and the `EXECUTE` grants listed above.

`check-login` inspects the server and the connection's database only. Not checked: other databases (`tempdb`, and
`msdb` when it is not the connected one, included), linked-server login mappings, the rights of an application role
(SQL Server documents that a login that knows its password can take them on with `sp_setapprole`; not measured
here), cross-database ownership chains, the object and schema `SELECT` grants you make, other read-only rights on
objects and schemas (`UNMASK` on SQL Server 2022 and 2025, `VIEW DEFINITION`, `REFERENCES`, `VIEW CHANGE TRACKING`),
and what views and synonyms read. Not asked one by one: the system views, procedures and functions in schemas
`sys` and `INFORMATION_SCHEMA`, which are listed in `sys.system_objects`, not in `sys.objects`; the two schemas
themselves are asked, and a grant on one of those objects is found by the backstop, except `public`'s `EXECUTE`
in `master`. In `sys.objects` those two schemas hold only rows of types `IT` and `S`, plus `PK` in `master` on 2022
and 2025 and `TT` in `msdb`: none of the fifteen asked types (measured in a user database, `master` and `msdb` on
the three versions).

Found by the backstop, and measured on the three versions: each right below was granted alone, the login gets one
exact finding, `<right> on <class> [<name>]`, for example `ALTER on message type [InvoiceMessage]` or
`TAKE OWNERSHIP on full-text stoplist [ShopStoplist]` (`PermissionCheckTests.cs`), SQL Server ran the statement in
brackets for it in a transaction that was rolled back unless noted, and the plain reader got the error named last
("reader"). `ALTER` on a user or a login, `ALTER` on an external model (SQL Server 2025) and `EXECUTE` on a system
extended procedure in `master` are found only by the backstop; the rights on the other classes below are also
found by the class rule above (measured by running
the check's statement without its two backstop parts as each of those logins).
`ALTER` on a user (`ALTER on user [mcp_target_user]`; `ALTER USER ... WITH DEFAULT_SCHEMA` ran, and the user's
default schema changed; reader 15151) or a login (`ALTER on login [mcp_target_login]`;
`ALTER LOGIN ... WITH DEFAULT_DATABASE` ran, and the login's default database changed; a new password, a new name
or `DISABLE` failed with error 15151; reader 15151). `ALTER` on an endpoint (`ALTER on endpoint [TSQL Default TCP]`;
`ALTER ENDPOINT [TSQL Default TCP] STATE = STARTED`, outside a transaction, ran without error; whether it changed
anything was not shown, and `STATE = STOPPED` was not run; on a stopped endpoint created for the measurement,
`ALTER ENDPOINT ... AS TCP (LISTENER_PORT = ...)` ran outside a transaction and the port changed, then it was set
back, with no test; reader 6004). `ALTER` on a route (`ALTER on route [AutoCreatedLocal]`, also when granted to a
role the login is in or to a role that role is in; `ALTER ROUTE ... WITH ADDRESS` ran, and the address changed;
reader 15151), a message type
(`ALTER MESSAGE TYPE ... VALIDATION = EMPTY` ran, and the validation changed; reader 15151), a remote service
binding (`ALTER REMOTE SERVICE BINDING ... WITH ANONYMOUS = OFF` ran, and the setting changed; reader 15151), a
search property list (`ALTER SEARCH PROPERTY LIST ... ADD` ran outside a transaction, and the property was listed;
it was dropped afterwards; reader 30025) or a database scoped credential
(`ALTER DATABASE SCOPED CREDENTIAL ... WITH IDENTITY` ran, and the identity changed; reader 15151).
`TAKE OWNERSHIP` on a contract or a full-text stoplist (`ALTER AUTHORIZATION` ran, and the login became the owner;
reader 15151; `ALTER FULLTEXT STOPLIST ... ADD` under `ALTER` failed with error 7609 for that login and the reader
alike, because full-text search is not installed in the images). Connected to `master`, `EXECUTE` on a system
extended procedure in schema `sys` (`EXECUTE on object [sys].[xp_regwrite]`; `EXEC master.sys.xp_regwrite` ran
without error; whether it wrote a value was not shown, as `xp_regread` found none afterwards; the test calls it
without arguments, which sqlcmd ran without error and Microsoft.Data.SqlClient reports as a severe error with
number 0; reader 229). With `EXECUTE` granted on `sys.xp_cmdshell`, `EXEC sys.xp_cmdshell` failed with error 15281
because that component is off, and it was not turned on. In a user database such a grant fails (error 4629).
`CONTROL` on a single login or user is found as `IMPERSONATE on login [...]` or
`IMPERSONATE on user [...]` (measured; with it the login took on the other principal with `EXECUTE AS`) and,
through the backstop, as `CONTROL` on it, and as `ALTER` when another principal holds an `ALTER` grant on it
(measured by running the check's statement as such a login; no test).
On SQL Server 2025, `ALTER` on an external model is found as `ALTER on external model id <id>`: its name is in
`sys.external_models`, which 2019 and 2022 lack, so the statement does not read it.
`ALTER EXTERNAL MODEL ... SET (MODEL = ...)` ran without error for that login in a transaction that was rolled
back; whether it changed the model was not shown; reader 15151 (measured on 2025 the same way; no test).

The owner of a message type, a contract, a remote service binding, a route, a full-text stoplist, a search
property list, a database scoped credential or an endpoint holds `CONTROL` without a grant row; the class rule finds
it as `ALTER`, `CONTROL` and `TAKE OWNERSHIP` on the securable (`PermissionCheckTests.cs`, measured on the three
versions). Each owner ran one statement the reader could not, with the change shown: in a transaction that was
rolled back, `ALTER MESSAGE TYPE ... VALIDATION = EMPTY`, `DROP CONTRACT`,
`ALTER REMOTE SERVICE BINDING ... WITH ANONYMOUS = OFF`, `ALTER ROUTE ... WITH ADDRESS` and
`ALTER DATABASE SCOPED CREDENTIAL ... WITH IDENTITY` (reader 15151 each); outside a transaction
`DROP FULLTEXT STOPLIST` (reader 30047; created again and given back to its owner),
`ALTER SEARCH PROPERTY LIST ... ADD` (reader 30025; the property was dropped afterwards) and
`ALTER ENDPOINT ... AS TCP (LISTENER_PORT = ...)` (reader 6004; the port was set back). Using any right in this
and the previous paragraph takes an `ALTER`, `DROP` or `EXEC` statement, which the gate refuses (`NotSelect`): it
accepts one plain SELECT only.

Not measured: rights on an external table, an availability group or an external language, which the backstop
refuses (none could be created in the test images: `CREATE EXTERNAL DATA SOURCE` failed with error 46530 on 2019
and 2022 and 46548 on 2025, `CREATE EXTERNAL TABLE` with 102 on 2019 and 2022 and 46501 on 2025,
`CREATE EXTERNAL LANGUAGE` with 39119 for want of a package file, and `CREATE AVAILABILITY GROUP` with 47105
because availability groups are not enabled). On the classes above, the class rule asks every right the class lists
on each securable the login can see, and the backstop refuses any other right granted on a securable of the
database or the server; only the rights and owners named above were tried. A credential (SQL Server 2022 and 2025
list `CONTROL` and `REFERENCES` for it) could not be granted: `GRANT ... ON CREDENTIAL::` failed with error 102 on
the three versions. `SELECT` is allowed on every object type, including a CLR table-valued function, whose `SELECT`
runs assembly code; not measured. Use a login created by `scripts/create-readonly-login.sql`, which grants none of
these, and check what `public` holds there. See SECURITY.md, "Known limits".

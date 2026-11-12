using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MssqlReadOnlyMcp.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}

/// <summary>
/// One SQL Server container for the whole test run, seeded with two databases and five logins. Passwords are random
/// per run and are never written to a file. When Docker is not reachable, starting the container throws and every
/// test in the collection fails; nothing is skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>The image used when <see cref="ImageVariable"/> is not set.</summary>
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    /// <summary>Environment variable naming another SQL Server image, for example a 2019 CU tag.</summary>
    public const string ImageVariable = "MSSQL_TEST_IMAGE";

    public const string Database = "ShopDb";
    public const string OtherDatabase = "ArchiveDb";

    /// <summary>
    /// The database the gate's regression table is written against (its rows use <c>DefaultDatabase = "Sales"</c>),
    /// plus the xml, hierarchyid and lock test tables.
    /// </summary>
    public const string SalesDatabase = "Sales";

    /// <summary>The Extended Events session that records what the reader login sends (created stopped).</summary>
    public const string TraceSession = "mcp_reader_trace";

    /// <summary>The application name of the trace marker connection; its events are not the server's.</summary>
    public const string MarkerApplicationName = "xe-marker";

    /// <summary>
    /// The reader the server runs as: SELECT granted on schema dbo, no fixed role. A db_datareader member cannot be
    /// used, because SQL Server lets every member of a fixed role create sequences in the schema that role owns
    /// (see <see cref="DataReaderLogin"/>).
    /// </summary>
    public const string ReaderLogin = "mcp_reader";

    /// <summary>A db_datareader-only member; the permission check refuses it for CONTROL on schema db_datareader.</summary>
    public const string DataReaderLogin = "mcp_datareader";
    public const string UpdaterLogin = "mcp_updater";
    public const string WriterLogin = "mcp_writer";
    public const string OwnerLogin = "mcp_owner";

    /// <summary>SELECT on schema dbo plus UPDATE on one column: <c>GRANT UPDATE ON dbo.Customers (City)</c>.</summary>
    public const string ColumnUpdaterLogin = "mcp_column_updater";

    /// <summary>SELECT on schema dbo plus IMPERSONATE on the user of <see cref="ReaderLogin"/>.</summary>
    public const string ImpersonatorLogin = "mcp_impersonator";

    /// <summary>SELECT on schema dbo plus ALTER ANY ROLE.</summary>
    public const string RoleAdminLogin = "mcp_role_admin";

    /// <summary>SELECT on schema dbo plus membership of the fixed server role securityadmin.</summary>
    public const string SecurityAdminLogin = "mcp_securityadmin";

    /// <summary>SELECT on schema dbo plus EXECUTE on the procedure dbo.AddCustomer.</summary>
    public const string ProcedureRunnerLogin = "mcp_procedure_runner";

    /// <summary>SELECT on schema dbo plus UPDATE on the sequence dbo.InvoiceNumbers.</summary>
    public const string SequenceUserLogin = "mcp_sequence_user";

    /// <summary>Member of db_denydatawriter only.</summary>
    public const string DenyWriterLogin = "mcp_denywriter";

    /// <summary>In <see cref="QuotedDatabase"/>: SELECT on schema dbo plus CREATE TABLE.</summary>
    public const string QuotedCreatorLogin = "mcp_quoted_creator";

    /// <summary>SELECT on schema dbo plus the server permission ALTER TRACE (not in the old server list).</summary>
    public const string TracerLogin = "mcp_tracer";

    /// <summary>SELECT on schema dbo plus the database permission BACKUP DATABASE (not in the old database patterns).</summary>
    public const string BackupLogin = "mcp_backup";

    /// <summary>SELECT on schema dbo plus UNMASK, a right that only widens reading.</summary>
    public const string UnmaskLogin = "mcp_unmasker";

    /// <summary>SELECT on schema dbo plus TAKE OWNERSHIP on the role <see cref="ReportingRole"/>.</summary>
    public const string RoleTakerLogin = "mcp_role_taker";

    /// <summary>
    /// SELECT on schema dbo plus UPDATE on the synonym <see cref="CustomerSynonym"/> only (none on the table it names).
    /// </summary>
    public const string SynonymUpdaterLogin = "mcp_synonym_updater";

    /// <summary>In <see cref="Database"/>: a synonym for dbo.Customers, owned like the table (dbo).</summary>
    public const string CustomerSynonym = "dbo.CustomerAlias";

    /// <summary>A reader like <see cref="ReaderLogin"/> (SELECT on schema dbo) with <c>DENY VIEW ANY DATABASE</c>.</summary>
    public const string NoDatabaseListLogin = "mcp_no_db_list";

    /// <summary>A user-defined database role in <see cref="Database"/>, with no members.</summary>
    public const string ReportingRole = "reporting";

    /// <summary>A database whose name needs quoting: a dot, a space and a closing bracket.</summary>
    public const string QuotedDatabase = "Odd.Name ]Db";

    /// <summary>The database the setup script test grants on; it holds the script's three example tables.</summary>
    public const string ExampleDatabase = "ExampleDb";

    /// <summary>In <see cref="SalesDatabase"/>: a table in schema hr, on which the reader has no grant.</summary>
    public const string HiddenTable = "hr.Payroll";

    /// <summary>In <see cref="SalesDatabase"/>: a table with a Turkish name, which stays a valid argument.</summary>
    public const string TurkishTable = "dbo.Müşteri_Şubeleri";

    /// <summary>In <see cref="SalesDatabase"/>: a table with 40 columns, whose description passes a 1000-character cap.</summary>
    public const string WideTable = "dbo.WideTable";

    /// <summary>In <see cref="Database"/>: a Service Broker queue that holds one message, bound to <see cref="InvoiceService"/>.</summary>
    public const string InvoiceQueue = "dbo.InvoiceQueue";

    /// <summary>In <see cref="Database"/>: a Service Broker service on <see cref="InvoiceQueue"/> (contract DEFAULT).</summary>
    public const string InvoiceService = "InvoiceService";

    /// <summary>In <see cref="Database"/>: a role holding SEND on <see cref="InvoiceService"/>.</summary>
    public const string ServiceSendersRole = "service_senders";

    /// <summary>In <see cref="Database"/>: a certificate (private key protected by the database master key), owned by dbo.</summary>
    public const string SigningCertificate = "ShopSigning";

    /// <summary>In <see cref="Database"/>: a certificate owned by <see cref="CertificateOwnerLogin"/>'s user.</summary>
    public const string OwnedCertificate = "OwnedCertificate";

    /// <summary>In <see cref="Database"/>: an RSA asymmetric key (private key protected by the database master key).</summary>
    public const string ShopAsymmetricKey = "ShopAsymmetric";

    /// <summary>In <see cref="Database"/>: an AES symmetric key encrypted by <see cref="SigningCertificate"/>.</summary>
    public const string ShopSymmetricKey = "ShopSymmetric";

    /// <summary>In <see cref="Database"/>: the user assembly <see cref="ClrTestAssembly"/> writes, trusted by hash.</summary>
    public const string TypesAssembly = "ShopTypes";

    /// <summary>In <see cref="Database"/>: a CLR user-defined type from <see cref="TypesAssembly"/>.</summary>
    public const string ClrType = "dbo.Point";

    /// <summary>In <see cref="Database"/>: a table type and an alias type, neither of which runs code.</summary>
    public const string TableType = "dbo.IdList";
    public const string AliasType = "dbo.Phone";

    /// <summary>In <see cref="Database"/>: an XML schema collection.</summary>
    public const string OrderNotesSchema = "dbo.OrderNotes";

    /// <summary>In <see cref="Database"/>: a full-text catalog (creating one does not need Full-Text Search installed).</summary>
    public const string ShopCatalog = "ShopCatalog";

    /// <summary>
    /// A TRUSTWORTHY database with a Service Broker service owned by <see cref="ServiceSenderLogin"/>'s user and one
    /// owned by <see cref="ClassReaderLogin"/>'s user, from which they begin dialogs to <see cref="InvoiceService"/>.
    /// </summary>
    public const string BrokerDatabase = "BrokerDb";

    /// <summary>In <see cref="BrokerDatabase"/>: the services the two logins own.</summary>
    public const string SenderOutboxService = "SenderOutbox";
    public const string ReaderOutboxService = "ReaderOutbox";

    /// <summary>SELECT on schema dbo plus RECEIVE on <see cref="InvoiceQueue"/>.</summary>
    public const string QueueReceiverLogin = "mcp_queue_receiver";

    /// <summary>SELECT on schema dbo plus SEND on <see cref="InvoiceService"/> (which SEND alone does not make visible).</summary>
    public const string ServiceSenderLogin = "mcp_service_sender";

    /// <summary>SELECT on schema dbo plus membership of <see cref="ServiceSendersRole"/>.</summary>
    public const string ServiceRoleSenderLogin = "mcp_service_role_sender";

    /// <summary>
    /// SELECT on schema dbo plus SEND on <see cref="InvoiceService"/> twice: directly and through
    /// <see cref="ServiceSendersRole"/>.
    /// </summary>
    public const string ServiceDoubleSenderLogin = "mcp_service_double_sender";

    /// <summary>
    /// In every database: a built-in Service Broker queue in schema dbo (<c>is_ms_shipped = 1</c>), the queue of the
    /// built-in service <see cref="QueryNotificationService"/>.
    /// </summary>
    public const string ShippedQueue = "dbo.QueryNotificationErrorsQueue";

    /// <summary>In every database: the built-in service on <see cref="ShippedQueue"/>.</summary>
    public const string QueryNotificationService = "http://schemas.microsoft.com/SQL/Notifications/QueryNotificationService";

    /// <summary>In every database: the built-in assembly (owner sys, <c>is_user_defined = 0</c>).</summary>
    public const string ShippedAssembly = "Microsoft.SqlServer.Types";

    /// <summary>SELECT on schema dbo plus RECEIVE on the built-in <see cref="ShippedQueue"/>.</summary>
    public const string ShippedQueueReceiverLogin = "mcp_shipped_queue_receiver";

    /// <summary>SELECT on schema dbo plus TAKE OWNERSHIP on the built-in <see cref="ShippedAssembly"/>.</summary>
    public const string ShippedAssemblyTakerLogin = "mcp_shipped_assembly_taker";

    /// <summary>
    /// A database of its own, so that no other test login gains or loses anything: change data capture is enabled
    /// in it (which adds the shipped <see cref="CdcHistoryTable"/>), and it holds a procedure and a table that
    /// <c>sp_MS_marksystemobject</c> marks as shipped (<c>is_ms_shipped = 1</c>).
    /// </summary>
    public const string ShippedDatabase = "ShippedDb";

    /// <summary>In <see cref="ShippedDatabase"/>: a table <see cref="MarkedProcedure"/> writes to.</summary>
    public const string LedgerTable = "dbo.Ledger";

    /// <summary>In <see cref="ShippedDatabase"/>: a procedure marked as shipped; it adds a row to <see cref="LedgerTable"/>.</summary>
    public const string MarkedProcedure = "dbo.AddLedger";

    /// <summary>In <see cref="ShippedDatabase"/>: a table marked as shipped.</summary>
    public const string MarkedTable = "dbo.MarkedNotes";

    /// <summary>In <see cref="ShippedDatabase"/>: a table change data capture adds, shipped.</summary>
    public const string CdcHistoryTable = "cdc.ddl_history";

    /// <summary>
    /// In <see cref="ShippedDatabase"/>: SELECT on schema dbo plus EXECUTE on <see cref="MarkedProcedure"/> and INSERT,
    /// DELETE on <see cref="MarkedTable"/>.
    /// </summary>
    public const string ShippedWriterLogin = "mcp_shipped_writer";

    /// <summary>In <see cref="ShippedDatabase"/>: SELECT on schema dbo plus DELETE on <see cref="CdcHistoryTable"/>.</summary>
    public const string CdcDeleterLogin = "mcp_cdc_deleter";

    /// <summary>
    /// SELECT on schema dbo in <see cref="ShippedDatabase"/>, <c>master</c> and <c>msdb</c>, nothing else: the plain
    /// reader for databases the other logins have no user in.
    /// </summary>
    public const string PlainReaderLogin = "mcp_plain_reader";

    /// <summary>A user-defined server role holding ALTER ANY LOGIN.</summary>
    public const string LoginAdminsRole = "mcp_login_admins";

    /// <summary>SELECT on schema dbo plus ALTER on the server role <see cref="LoginAdminsRole"/>.</summary>
    public const string ServerRoleAltererLogin = "mcp_server_role_alterer";

    /// <summary>In <see cref="Database"/>: an application role holding INSERT on dbo.Customers.</summary>
    public const string ReportingAppRole = "reporting_app";

    /// <summary>SELECT on schema dbo plus ALTER on the application role <see cref="ReportingAppRole"/>.</summary>
    public const string AppRoleAltererLogin = "mcp_app_role_alterer";

    /// <summary>SELECT on schema dbo plus ALTER on <see cref="InvoiceService"/>.</summary>
    public const string ServiceAltererLogin = "mcp_service_alterer";

    /// <summary>SELECT on schema dbo plus CONTROL on <see cref="SigningCertificate"/>.</summary>
    public const string CertificateControllerLogin = "mcp_certificate_controller";

    /// <summary>SELECT on schema dbo; its user owns <see cref="OwnedCertificate"/>.</summary>
    public const string CertificateOwnerLogin = "mcp_certificate_owner";

    /// <summary>SELECT on schema dbo plus ALTER on <see cref="ShopAsymmetricKey"/>.</summary>
    public const string AsymmetricKeyAltererLogin = "mcp_asymmetric_key_alterer";

    /// <summary>SELECT on schema dbo plus TAKE OWNERSHIP on <see cref="ShopSymmetricKey"/>.</summary>
    public const string SymmetricKeyTakerLogin = "mcp_symmetric_key_taker";

    /// <summary>SELECT on schema dbo plus ALTER on <see cref="TypesAssembly"/>.</summary>
    public const string AssemblyAltererLogin = "mcp_assembly_alterer";

    /// <summary>SELECT on schema dbo plus EXECUTE on <see cref="ClrType"/>.</summary>
    public const string ClrTypeUserLogin = "mcp_clr_type_user";

    /// <summary>SELECT on schema dbo plus ALTER on <see cref="OrderNotesSchema"/>.</summary>
    public const string XmlSchemaAltererLogin = "mcp_xml_schema_alterer";

    /// <summary>SELECT on schema dbo plus CONTROL on <see cref="ShopCatalog"/>.</summary>
    public const string CatalogControllerLogin = "mcp_catalog_controller";

    /// <summary>
    /// SELECT on schema dbo plus every right on the classes above that the check allows: VIEW DEFINITION on the queue
    /// and the service; REFERENCES and VIEW DEFINITION on both keys, the certificate, the assembly, the CLR type and
    /// the catalog; EXECUTE, REFERENCES and VIEW DEFINITION on the table type, the alias type and the XML schema
    /// collection. It passes.
    /// </summary>
    public const string ClassReaderLogin = "mcp_class_reader";

    /// <summary>In <see cref="Database"/>: a user without a login, target of <see cref="UserAltererLogin"/>.</summary>
    public const string TargetUser = "mcp_target_user";

    /// <summary>A login that never connects, the target of <see cref="LoginAltererLogin"/>.</summary>
    public const string TargetLogin = "mcp_target_login";

    /// <summary>The built-in T-SQL endpoint every instance has.</summary>
    public const string TsqlEndpoint = "TSQL Default TCP";

    /// <summary>The built-in route every database has.</summary>
    public const string LocalRoute = "AutoCreatedLocal";

    /// <summary>In <see cref="Database"/>: a message type, a contract and a remote service binding.</summary>
    public const string InvoiceMessageType = "InvoiceMessage";
    public const string InvoiceContract = "InvoiceContract";
    public const string InvoiceBinding = "InvoiceBinding";

    /// <summary>In <see cref="Database"/>: a full-text stoplist and a search property list.</summary>
    public const string ShopStoplist = "ShopStoplist";
    public const string ShopPropertyList = "ShopProperties";

    /// <summary>In <see cref="Database"/>: a database scoped credential (its secret is generated per run).</summary>
    public const string ShopCredential = "ShopCredential";

    /// <summary>In <see cref="Database"/>: a role holding ALTER on <see cref="LocalRoute"/>.</summary>
    public const string RouteAdminsRole = "route_admins";

    /// <summary>
    /// Logins that each hold one right on a user, a login, an endpoint, or a Service Broker, full-text or credential
    /// securable (SELECT on schema dbo besides): the permission rows find each, the class rule all but the first two.
    /// </summary>
    public const string UserAltererLogin = "mcp_user_alterer";
    public const string LoginAltererLogin = "mcp_login_alterer";
    public const string EndpointAltererLogin = "mcp_endpoint_alterer";
    public const string RouteAltererLogin = "mcp_route_alterer";
    public const string RouteRoleAltererLogin = "mcp_route_role_alterer";
    public const string MessageTypeAltererLogin = "mcp_message_type_alterer";
    public const string ContractTakerLogin = "mcp_contract_taker";
    public const string BindingAltererLogin = "mcp_binding_alterer";
    public const string StoplistTakerLogin = "mcp_stoplist_taker";
    public const string PropertyListAltererLogin = "mcp_property_list_alterer";
    public const string CredentialAltererLogin = "mcp_credential_alterer";

    /// <summary>In <c>master</c>: SELECT on schema dbo plus EXECUTE on the extended procedure xp_regwrite.</summary>
    public const string SystemProcedureRunnerLogin = "mcp_system_procedure_runner";

    /// <summary>
    /// SELECT on schema dbo plus VIEW DEFINITION on <see cref="TargetUser"/>, <see cref="ReportingAppRole"/> and
    /// <see cref="TargetLogin"/>, which shows it the grants other logins hold on them. It passes.
    /// </summary>
    public const string PrincipalViewerLogin = "mcp_principal_viewer";

    /// <summary>
    /// In <see cref="Database"/> (the endpoint on the server): one securable of each class no catalog grant shows the
    /// owner of, each given to the owner login below with ALTER AUTHORIZATION. The endpoint is a stopped Service
    /// Broker endpoint created for this.
    /// </summary>
    public const string OwnedMessageType = "OwnedMessage";
    public const string OwnedContract = "OwnedContract";
    public const string OwnedBinding = "OwnedBinding";
    public const string OwnedRoute = "OwnedRoute";
    public const string OwnedStoplist = "OwnedStoplist";
    public const string OwnedPropertyList = "OwnedProperties";
    public const string OwnedCredential = "OwnedCredential";
    public const string OwnedEndpoint = "mcp_owned_endpoint";
    public const int OwnedEndpointPort = 4099;

    /// <summary>SELECT on schema dbo; its user (for the endpoint the login itself) owns one securable above.</summary>
    public const string MessageTypeOwnerLogin = "mcp_message_type_owner";
    public const string ContractOwnerLogin = "mcp_contract_owner";
    public const string BindingOwnerLogin = "mcp_binding_owner";
    public const string RouteOwnerLogin = "mcp_route_owner";
    public const string StoplistOwnerLogin = "mcp_stoplist_owner";
    public const string PropertyListOwnerLogin = "mcp_property_list_owner";
    public const string CredentialOwnerLogin = "mcp_credential_owner";
    public const string EndpointOwnerLogin = "mcp_endpoint_owner";

    /// <summary>
    /// SELECT on schema dbo plus one right held WITH GRANT OPTION: SELECT on schema dbo, SELECT on dbo.Customers,
    /// SELECT on its column City, CONNECT on the database, VIEW ANY DATABASE on the server; each directly, and each
    /// through a role it is in (a server role for VIEW ANY DATABASE).
    /// </summary>
    public const string SchemaGrantorLogin = "mcp_schema_grantor";
    public const string ObjectGrantorLogin = "mcp_object_grantor";
    public const string ColumnGrantorLogin = "mcp_column_grantor";
    public const string DatabaseGrantorLogin = "mcp_database_grantor";
    public const string ServerGrantorLogin = "mcp_server_grantor";
    public const string RoleSchemaGrantorLogin = "mcp_role_schema_grantor";
    public const string RoleObjectGrantorLogin = "mcp_role_object_grantor";
    public const string RoleColumnGrantorLogin = "mcp_role_column_grantor";
    public const string RoleDatabaseGrantorLogin = "mcp_role_database_grantor";
    public const string RoleServerGrantorLogin = "mcp_role_server_grantor";
    public const string SchemaGrantorsRole = "schema_grantors";
    public const string ObjectGrantorsRole = "object_grantors";
    public const string ColumnGrantorsRole = "column_grantors";
    public const string DatabaseGrantorsRole = "database_grantors";
    public const string ServerGrantorsRole = "mcp_database_listers";

    /// <summary>SELECT on schema dbo plus database-wide SELECT or INSERT on dbo.Customers, WITH GRANT OPTION.</summary>
    public const string DatabaseSelectGrantorLogin = "mcp_database_select_grantor";
    public const string InsertGrantorLogin = "mcp_insert_grantor";

    /// <summary>
    /// SELECT on schema dbo; its user is in <see cref="RouteInnerRole"/>, a member of <see cref="RouteOuterRole"/>,
    /// which holds ALTER on <see cref="LocalRoute"/>.
    /// </summary>
    public const string NestedRouteAltererLogin = "mcp_nested_route_alterer";
    public const string RouteInnerRole = "route_inner";
    public const string RouteOuterRole = "route_outer";

    /// <summary>
    /// SELECT on schema dbo; its user is in a database role that owns one securable (a schema, a message type, a
    /// full-text stoplist), or its login is in a server role that owns one (an endpoint, a server role). No grant
    /// row shows any of these.
    /// </summary>
    public const string RoleSchemaOwnerLogin = "mcp_role_schema_owner";
    public const string RoleMessageTypeOwnerLogin = "mcp_role_message_type_owner";
    public const string RoleStoplistOwnerLogin = "mcp_role_stoplist_owner";
    public const string RoleEndpointOwnerLogin = "mcp_role_endpoint_owner";
    public const string RoleServerRoleOwnerLogin = "mcp_role_server_role_owner";
    public const string RoleOwnedSchema = "role_owned";
    public const string RoleOwnedMessageType = "RoleOwnedMessage";
    public const string RoleOwnedStoplist = "RoleOwnedStoplist";
    public const string RoleOwnedEndpoint = "mcp_role_owned_endpoint";
    public const int RoleOwnedEndpointPort = 4091;
    public const string RoleOwnedServerRole = "mcp_role_owned_role";
    public const string SchemaOwnersRole = "schema_owners";
    public const string MessageTypeOwnersRole = "message_type_owners";
    public const string StoplistOwnersRole = "stoplist_owners";
    public const string EndpointOwnersRole = "mcp_endpoint_owners";
    public const string ServerRoleOwnersRole = "mcp_server_role_owners";

    /// <summary>
    /// In <see cref="PublicGrantDatabase"/>: <c>public</c> holds SELECT (allowed) and INSERT (refused) on
    /// <see cref="PublicNotesTable"/> WITH GRANT OPTION; this login has a user there and no grant of its own.
    /// </summary>
    public const string PublicGrantDatabase = "PublicGrantDb";
    public const string PublicNotesTable = "PublicNotes";
    public const string PublicGrantReaderLogin = "mcp_public_grant_reader";

    /// <summary>
    /// Further logins: each holds one right beyond reading (refused), except <see cref="ClassReaderLogin"/> and
    /// <see cref="PrincipalViewerLogin"/>, which hold only rights the check allows, and <see cref="TargetLogin"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ExtraLogins =
    [
        ColumnUpdaterLogin, ImpersonatorLogin, RoleAdminLogin, SecurityAdminLogin, ProcedureRunnerLogin, SequenceUserLogin,
        DenyWriterLogin, QuotedCreatorLogin, TracerLogin, BackupLogin, UnmaskLogin, RoleTakerLogin, NoDatabaseListLogin,
        SynonymUpdaterLogin, QueueReceiverLogin, ServiceSenderLogin, ServiceRoleSenderLogin, ServiceAltererLogin,
        CertificateControllerLogin, CertificateOwnerLogin, AsymmetricKeyAltererLogin, SymmetricKeyTakerLogin,
        AssemblyAltererLogin, ClrTypeUserLogin, XmlSchemaAltererLogin, CatalogControllerLogin, ClassReaderLogin,
        ServiceDoubleSenderLogin, ShippedQueueReceiverLogin, ShippedAssemblyTakerLogin, ShippedWriterLogin,
        CdcDeleterLogin, PlainReaderLogin, ServerRoleAltererLogin, AppRoleAltererLogin, TargetLogin, UserAltererLogin,
        LoginAltererLogin, EndpointAltererLogin, RouteAltererLogin, RouteRoleAltererLogin, MessageTypeAltererLogin,
        ContractTakerLogin, BindingAltererLogin, StoplistTakerLogin, PropertyListAltererLogin, CredentialAltererLogin,
        SystemProcedureRunnerLogin, PrincipalViewerLogin, MessageTypeOwnerLogin, ContractOwnerLogin, BindingOwnerLogin,
        RouteOwnerLogin, StoplistOwnerLogin, PropertyListOwnerLogin, CredentialOwnerLogin, EndpointOwnerLogin,
        SchemaGrantorLogin, ObjectGrantorLogin, ColumnGrantorLogin, DatabaseGrantorLogin, ServerGrantorLogin,
        RoleSchemaGrantorLogin, RoleObjectGrantorLogin, RoleColumnGrantorLogin, RoleDatabaseGrantorLogin,
        RoleServerGrantorLogin, DatabaseSelectGrantorLogin, InsertGrantorLogin, NestedRouteAltererLogin,
        RoleSchemaOwnerLogin, RoleMessageTypeOwnerLogin, RoleStoplistOwnerLogin, RoleEndpointOwnerLogin,
        RoleServerRoleOwnerLogin, PublicGrantReaderLogin,
    ];

    /// <summary>Every seeded user table, with the database it lives in.</summary>
    public static readonly (string Database, string Table)[] SeededTables =
    [
        (Database, "dbo.Customers"),
        (Database, "dbo.Orders"),
        (Database, "dbo.OrderLines"),
        (OtherDatabase, "dbo.ArchivedOrders"),
    ];

    private MsSqlContainer? container;

    public string SaPassword { get; } = NewPassword();

    public string ReaderPassword { get; } = NewPassword();

    public string DataReaderPassword { get; } = NewPassword();

    public string UpdaterPassword { get; } = NewPassword();

    public string WriterPassword { get; } = NewPassword();

    public string OwnerPassword { get; } = NewPassword();

    /// <summary>The password of the ERP-scale reader, whose login <see cref="ErpScaleTests"/> creates.</summary>
    public string ErpReaderPassword { get; } = NewPassword();

    /// <summary>The password the setup script test gives the login the script creates.</summary>
    public string ScriptLoginPassword { get; } = NewPassword();

    /// <summary>The password of each of <see cref="ExtraLogins"/>.</summary>
    public IReadOnlyDictionary<string, string> ExtraPasswords { get; } = ExtraLogins.ToDictionary(l => l, _ => NewPassword());

    /// <summary>The password of <see cref="Database"/>'s database master key.</summary>
    public string MasterKeyPassword { get; } = NewPassword();

    /// <summary>The password of the application role <see cref="ReportingAppRole"/>.</summary>
    public string AppRolePassword { get; } = NewPassword();

    /// <summary>The secret of the database scoped credential <see cref="ShopCredential"/>.</summary>
    public string CredentialSecret { get; } = NewPassword();

    /// <summary>The image this run uses.</summary>
    public string Image { get; } = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>SQL Server's major version (15 = 2019, 16 = 2022), read after start.</summary>
    public int MajorVersion { get; private set; }

    /// <summary>A folder for this run's audit files; removed at the end.</summary>
    public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "mssql-readonly-mcp-tests", Guid.NewGuid().ToString("N"));

    public string SaConnectionString => ConnectionString("sa", SaPassword);

    public string ReaderConnectionString => ConnectionString(ReaderLogin, ReaderPassword);

    /// <summary>The reader, connected to <see cref="SalesDatabase"/>.</summary>
    public string SalesReaderConnectionString => ConnectionString(ReaderLogin, ReaderPassword, SalesDatabase);

    public string DataReaderConnectionString => ConnectionString(DataReaderLogin, DataReaderPassword);

    public string UpdaterConnectionString => ConnectionString(UpdaterLogin, UpdaterPassword);

    public string WriterConnectionString => ConnectionString(WriterLogin, WriterPassword);

    public string OwnerConnectionString => ConnectionString(OwnerLogin, OwnerPassword);

    /// <summary>Every password of this run, for the leak checks.</summary>
    public IReadOnlyList<string> Secrets =>
        [SaPassword, ReaderPassword, DataReaderPassword, UpdaterPassword, WriterPassword, OwnerPassword, ErpReaderPassword,
         ScriptLoginPassword, MasterKeyPassword, AppRolePassword, CredentialSecret, .. ExtraPasswords.Values];

    /// <summary><paramref name="name"/> as a bracketed T-SQL identifier.</summary>
    public static string Bracket(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>The connection string of one of <see cref="ExtraLogins"/>.</summary>
    public string ExtraConnectionString(string login, string database = Database) =>
        ConnectionString(login, ExtraPasswords[login], database);

    /// <summary>Copies <paramref name="content"/> to <paramref name="path"/> inside the SQL Server container.</summary>
    public Task CopyToContainerAsync(byte[] content, string path) => container!.CopyAsync(content, path);

    /// <summary>Runs a command inside the SQL Server container.</summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> ExecInContainerAsync(IList<string> command) =>
        container!.ExecAsync(command);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WorkDirectory);
        container = new MsSqlBuilder(Image).WithPassword(SaPassword).Build();
        await container.StartAsync();
        MajorVersion = int.Parse(
            await ScalarAsync("master", "SELECT CONVERT(nvarchar(10), SERVERPROPERTY('ProductMajorVersion'))"),
            System.Globalization.CultureInfo.InvariantCulture);

        foreach (var batch in SeedBatches())
        {
            await ExecuteAsAdminAsync(batch.Database, batch.Sql);
        }
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }

        try
        {
            Directory.Delete(WorkDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a child process; the folder is under the temp directory.
        }
    }

    public string ConnectionString(string user, string password, string database = Database)
    {
        var builder = new SqlConnectionStringBuilder(container!.GetConnectionString())
        {
            UserID = user,
            Password = password,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Pooling = true,
        };
        return builder.ConnectionString;
    }

    public string NewAuditPath() => Path.Combine(WorkDirectory, $"audit-{Guid.NewGuid():N}.jsonl");

    /// <summary>Runs administrative SQL as <c>sa</c> (test setup and fingerprints only).</summary>
    public async Task ExecuteAsAdminAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString("sa", SaPassword, database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Row count and aggregate checksum of every seeded table, the sequence's current value and the number of user
    /// objects in both databases, read as <c>sa</c>.
    /// </summary>
    public async Task<string> FingerprintAsync()
    {
        var parts = new List<string>();
        foreach (var (database, table) in SeededTables)
        {
            parts.Add($"{database}.{table}=" + await ScalarAsync(
                database, $"SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM {table}"));
        }

        parts.Add("sequence=" + await ScalarAsync(
            Database, "SELECT CONVERT(nvarchar(40), current_value) FROM sys.sequences WHERE name = N'InvoiceNumbers'"));
        foreach (var database in new[] { Database, OtherDatabase })
        {
            parts.Add($"{database}.objects=" + await ScalarAsync(
                database, "SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(name, type))) FROM sys.objects WHERE is_ms_shipped = 0"));
        }

        parts.Add("logins=" + await ScalarAsync(
            "master", "SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(name))) FROM sys.server_principals"));
        return string.Join("; ", parts);
    }

    /// <summary>Starts the reader trace; the ring buffer starts empty.</summary>
    public Task StartTraceAsync() =>
        ExecuteAsAdminAsync("master", $"ALTER EVENT SESSION [{TraceSession}] ON SERVER STATE = START;");

    /// <summary>Stops the reader trace, which discards its ring buffer.</summary>
    public Task StopTraceAsync() =>
        ExecuteAsAdminAsync("master", $"ALTER EVENT SESSION [{TraceSession}] ON SERVER STATE = STOP;");

    /// <summary>
    /// Sends a marker batch as the reader from a fresh, unpooled connection named <see cref="MarkerApplicationName"/>,
    /// waits until the marker is in the ring buffer (so every earlier event has been dispatched too), and returns
    /// every event recorded since the trace started, the marker's own included.
    /// </summary>
    public async Task<IReadOnlyList<TraceEvent>> ReadTraceAsync()
    {
        var marker = $"xe-marker-{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(ReaderConnectionString) { ApplicationName = MarkerApplicationName, Pooling = false };
        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"SELECT N'{marker}'", connection);
            await command.ExecuteScalarAsync();
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var events = await ReadRingBufferAsync();
            if (events.Any(e => e.Name == "sql_batch_starting" && e.Text is not null && e.Text.Contains(marker, StringComparison.Ordinal)))
            {
                return events;
            }

            if (started.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"The trace marker did not reach the ring buffer within 30 seconds; {events.Count} events seen.");
            }

            await Task.Delay(250);
        }
    }

    private async Task<IReadOnlyList<TraceEvent>> ReadRingBufferAsync()
    {
        var xml = await ScalarAsync("master", $"""
            SELECT t.target_data
            FROM sys.dm_xe_session_targets AS t
            JOIN sys.dm_xe_sessions AS s ON s.address = t.event_session_address
            WHERE s.name = N'{TraceSession}' AND t.target_name = N'ring_buffer'
            """);
        var root = System.Xml.Linq.XElement.Parse(xml);
        return root.Elements("event")
            .Select(e =>
            {
                string? Field(string element, string name) =>
                    e.Elements(element).FirstOrDefault(d => (string?)d.Attribute("name") == name)?.Element("value")?.Value;
                return new TraceEvent(
                    (string?)e.Attribute("name") ?? string.Empty,
                    Field("action", "client_app_name"),
                    Field("action", "server_principal_name"),
                    Field("data", "batch_text") ?? Field("data", "statement"));
            })
            .ToList();
    }

    private async Task<string> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString("sa", SaPassword, database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    private static string NewPassword()
    {
        // Letters, digits and two symbols: meets the SQL Server complexity policy and needs no quoting anywhere.
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "_-";
        const string all = upper + lower + digits;
        var chars = new StringBuilder();
        chars.Append(upper[RandomNumberGenerator.GetInt32(upper.Length)]);
        for (var i = 0; i < 20; i++)
        {
            chars.Append(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        chars.Append(lower[RandomNumberGenerator.GetInt32(lower.Length)]);
        chars.Append(digits[RandomNumberGenerator.GetInt32(digits.Length)]);
        chars.Append(symbols[RandomNumberGenerator.GetInt32(symbols.Length)]);
        chars.Append(all[RandomNumberGenerator.GetInt32(all.Length)]);
        return chars.ToString();
    }

    private static string WideTableSql()
    {
        var columns = Enumerable.Range(1, 40)
            .Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ColumnWithAVeryLongDescriptiveName_{i:D2} int NULL"));
        return $"CREATE TABLE dbo.WideTable (Id int NOT NULL PRIMARY KEY, {string.Join(", ", columns)});";
    }

    /// <summary>
    /// The securables outside schemas, objects and columns that the permission check asks about (queue, service,
    /// certificate, keys, assembly, types, XML schema collection, full-text catalog), one login per right, and the
    /// broker database the SEND test begins its dialogs from. Runs after the logins exist.
    /// </summary>
    private IEnumerable<(string Database, string Sql)> ClassSeedBatches()
    {
        // A user assembly needs CLR enabled to run and, under "clr strict security", to be trusted by its SHA-512 hash.
        var assembly = ClrTestAssembly.Build(TypesAssembly);
        var hash = Convert.ToHexString(SHA512.HashData(assembly));
        yield return ("master", $"""
            EXEC sys.sp_configure N'clr enabled', 1;
            RECONFIGURE;
            DECLARE @hash varbinary(64) = 0x{hash};
            EXEC sys.sp_add_trusted_assembly @hash = @hash, @description = N'{TypesAssembly} (permission check tests)';
            """);
        yield return (Database, $"CREATE ASSEMBLY [{TypesAssembly}] FROM 0x{Convert.ToHexString(assembly)} WITH PERMISSION_SET = SAFE;");
        yield return (Database, $"CREATE TYPE {ClrType} EXTERNAL NAME [{TypesAssembly}].[{ClrTestAssembly.TypeName}];");

        // CREATE FULLTEXT CATALOG cannot run inside a transaction, so it has a batch of its own.
        yield return (Database, $"CREATE FULLTEXT CATALOG [{ShopCatalog}];");

        var users = new StringBuilder();
        foreach (var login in new[]
        {
            QueueReceiverLogin, ServiceSenderLogin, ServiceRoleSenderLogin, ServiceAltererLogin, CertificateControllerLogin,
            CertificateOwnerLogin, AsymmetricKeyAltererLogin, SymmetricKeyTakerLogin, AssemblyAltererLogin, ClrTypeUserLogin,
            XmlSchemaAltererLogin, CatalogControllerLogin, ClassReaderLogin, ServiceDoubleSenderLogin,
            ShippedQueueReceiverLogin, ShippedAssemblyTakerLogin,
        })
        {
            users.Append($"CREATE USER [{login}] FOR LOGIN [{login}];\nGRANT SELECT ON SCHEMA::dbo TO [{login}];\n");
        }

        yield return (Database, $"""
            {users}
            CREATE MASTER KEY ENCRYPTION BY PASSWORD = N'{MasterKeyPassword}';
            CREATE CERTIFICATE [{SigningCertificate}] WITH SUBJECT = N'Permission check test';
            CREATE CERTIFICATE [{OwnedCertificate}] AUTHORIZATION [{CertificateOwnerLogin}] WITH SUBJECT = N'Owned by a test login';
            CREATE ASYMMETRIC KEY [{ShopAsymmetricKey}] WITH ALGORITHM = RSA_2048;
            CREATE SYMMETRIC KEY [{ShopSymmetricKey}] WITH ALGORITHM = AES_256 ENCRYPTION BY CERTIFICATE [{SigningCertificate}];
            CREATE TYPE {TableType} AS TABLE (Id int NOT NULL);
            CREATE TYPE {AliasType} FROM varchar(20) NULL;
            CREATE XML SCHEMA COLLECTION {OrderNotesSchema} AS
                N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="note" type="xsd:string"/></xsd:schema>';
            CREATE QUEUE {InvoiceQueue};
            CREATE SERVICE [{InvoiceService}] ON QUEUE {InvoiceQueue} ([DEFAULT]);
            DECLARE @dialog uniqueidentifier;
            BEGIN DIALOG @dialog FROM SERVICE [{InvoiceService}] TO SERVICE N'{InvoiceService}' ON CONTRACT [DEFAULT] WITH ENCRYPTION = OFF;
            SEND ON CONVERSATION @dialog (N'invoice 1');
            CREATE ROLE [{ServiceSendersRole}];

            GRANT RECEIVE ON {InvoiceQueue} TO [{QueueReceiverLogin}];
            GRANT SEND ON SERVICE::[{InvoiceService}] TO [{ServiceSenderLogin}];
            GRANT SEND ON SERVICE::[{InvoiceService}] TO [{ServiceSendersRole}];
            ALTER ROLE [{ServiceSendersRole}] ADD MEMBER [{ServiceRoleSenderLogin}];
            GRANT SEND ON SERVICE::[{InvoiceService}] TO [{ServiceDoubleSenderLogin}];
            ALTER ROLE [{ServiceSendersRole}] ADD MEMBER [{ServiceDoubleSenderLogin}];
            GRANT RECEIVE ON {ShippedQueue} TO [{ShippedQueueReceiverLogin}];
            GRANT TAKE OWNERSHIP ON ASSEMBLY::[{ShippedAssembly}] TO [{ShippedAssemblyTakerLogin}];
            GRANT ALTER ON SERVICE::[{InvoiceService}] TO [{ServiceAltererLogin}];
            GRANT CONTROL ON CERTIFICATE::[{SigningCertificate}] TO [{CertificateControllerLogin}];
            GRANT ALTER ON ASYMMETRIC KEY::[{ShopAsymmetricKey}] TO [{AsymmetricKeyAltererLogin}];
            GRANT TAKE OWNERSHIP ON SYMMETRIC KEY::[{ShopSymmetricKey}] TO [{SymmetricKeyTakerLogin}];
            GRANT ALTER ON ASSEMBLY::[{TypesAssembly}] TO [{AssemblyAltererLogin}];
            GRANT EXECUTE ON TYPE::{ClrType} TO [{ClrTypeUserLogin}];
            GRANT ALTER ON XML SCHEMA COLLECTION::{OrderNotesSchema} TO [{XmlSchemaAltererLogin}];
            GRANT CONTROL ON FULLTEXT CATALOG::[{ShopCatalog}] TO [{CatalogControllerLogin}];

            GRANT VIEW DEFINITION ON {InvoiceQueue} TO [{ClassReaderLogin}];
            GRANT VIEW DEFINITION ON SERVICE::[{InvoiceService}] TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON CERTIFICATE::[{SigningCertificate}] TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON ASYMMETRIC KEY::[{ShopAsymmetricKey}] TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON SYMMETRIC KEY::[{ShopSymmetricKey}] TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON ASSEMBLY::[{TypesAssembly}] TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON TYPE::{ClrType} TO [{ClassReaderLogin}];
            GRANT EXECUTE, REFERENCES, VIEW DEFINITION ON TYPE::{TableType} TO [{ClassReaderLogin}];
            GRANT EXECUTE, REFERENCES, VIEW DEFINITION ON TYPE::{AliasType} TO [{ClassReaderLogin}];
            GRANT EXECUTE, REFERENCES, VIEW DEFINITION ON XML SCHEMA COLLECTION::{OrderNotesSchema} TO [{ClassReaderLogin}];
            GRANT REFERENCES, VIEW DEFINITION ON FULLTEXT CATALOG::[{ShopCatalog}] TO [{ClassReaderLogin}];
            """);

        // Across databases, SEND is checked for the initiating service's owner in the target database (the
        // initiating database must be TRUSTWORTHY for that owner's login to be mapped there).
        yield return (BrokerDatabase, $"""
            ALTER DATABASE CURRENT SET TRUSTWORTHY ON;
            CREATE USER [{ServiceSenderLogin}] FOR LOGIN [{ServiceSenderLogin}];
            CREATE USER [{ClassReaderLogin}] FOR LOGIN [{ClassReaderLogin}];
            CREATE QUEUE dbo.SenderOutboxQueue;
            ALTER AUTHORIZATION ON OBJECT::dbo.SenderOutboxQueue TO [{ServiceSenderLogin}];
            CREATE SERVICE [{SenderOutboxService}] AUTHORIZATION [{ServiceSenderLogin}] ON QUEUE dbo.SenderOutboxQueue;
            CREATE QUEUE dbo.ReaderOutboxQueue;
            ALTER AUTHORIZATION ON OBJECT::dbo.ReaderOutboxQueue TO [{ClassReaderLogin}];
            CREATE SERVICE [{ReaderOutboxService}] AUTHORIZATION [{ClassReaderLogin}] ON QUEUE dbo.ReaderOutboxQueue;
            """);
    }

    /// <summary>
    /// Objects SQL Server marks as shipped in a database of their own, a user-defined server role and an application
    /// role, and the logins that hold one right on each. Runs after the logins exist.
    /// </summary>
    private IEnumerable<(string Database, string Sql)> ShippedSeedBatches()
    {
        // Change data capture adds the cdc schema and its tables, all marked as shipped. It needs no Agent job to be
        // enabled, and the batch must not be in a transaction.
        yield return (ShippedDatabase, "EXEC sys.sp_cdc_enable_db;");
        yield return (ShippedDatabase, $"""
            CREATE TABLE {LedgerTable} (Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Note nvarchar(50) NOT NULL);
            CREATE TABLE {MarkedTable} (Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Note nvarchar(50) NOT NULL);
            """);
        yield return (ShippedDatabase, $"CREATE PROCEDURE {MarkedProcedure} AS INSERT INTO {LedgerTable} (Note) VALUES (N'added');");
        yield return (ShippedDatabase, $"""
            EXEC sys.sp_MS_marksystemobject N'{MarkedProcedure}';
            EXEC sys.sp_MS_marksystemobject N'{MarkedTable}';
            CREATE USER [{ShippedWriterLogin}] FOR LOGIN [{ShippedWriterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ShippedWriterLogin}];
            GRANT EXECUTE ON {MarkedProcedure} TO [{ShippedWriterLogin}];
            GRANT INSERT, DELETE ON {MarkedTable} TO [{ShippedWriterLogin}];
            CREATE USER [{CdcDeleterLogin}] FOR LOGIN [{CdcDeleterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{CdcDeleterLogin}];
            GRANT DELETE ON {CdcHistoryTable} TO [{CdcDeleterLogin}];
            CREATE USER [{PlainReaderLogin}] FOR LOGIN [{PlainReaderLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{PlainReaderLogin}];
            """);
        foreach (var database in new[] { "master", "msdb" })
        {
            yield return (database, $"""
                CREATE USER [{PlainReaderLogin}] FOR LOGIN [{PlainReaderLogin}];
                GRANT SELECT ON SCHEMA::dbo TO [{PlainReaderLogin}];
                """);
        }

        yield return ("master", $"""
            CREATE SERVER ROLE [{LoginAdminsRole}];
            GRANT ALTER ANY LOGIN TO [{LoginAdminsRole}];
            GRANT ALTER ON SERVER ROLE::[{LoginAdminsRole}] TO [{ServerRoleAltererLogin}];
            """);
        yield return (Database, $"""
            CREATE USER [{ServerRoleAltererLogin}] FOR LOGIN [{ServerRoleAltererLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ServerRoleAltererLogin}];
            CREATE USER [{AppRoleAltererLogin}] FOR LOGIN [{AppRoleAltererLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{AppRoleAltererLogin}];
            CREATE APPLICATION ROLE [{ReportingAppRole}] WITH PASSWORD = N'{AppRolePassword}', DEFAULT_SCHEMA = dbo;
            GRANT INSERT ON dbo.Customers TO [{ReportingAppRole}];
            GRANT ALTER ON APPLICATION ROLE::[{ReportingAppRole}] TO [{AppRoleAltererLogin}];
            """);
    }

    /// <summary>
    /// Securables found through the permission rows, one login holding one right on each, and the login that only
    /// sees other logins' grants. Runs after the class and shipped batches (the master key and the application role
    /// exist then).
    /// </summary>
    private IEnumerable<(string Database, string Sql)> BackstopSeedBatches()
    {
        yield return ("master", $"""
            GRANT ALTER ON LOGIN::[{TargetLogin}] TO [{LoginAltererLogin}];
            GRANT VIEW DEFINITION ON LOGIN::[{TargetLogin}] TO [{PrincipalViewerLogin}];
            GRANT ALTER ON ENDPOINT::[{TsqlEndpoint}] TO [{EndpointAltererLogin}];
            CREATE USER [{SystemProcedureRunnerLogin}] FOR LOGIN [{SystemProcedureRunnerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SystemProcedureRunnerLogin}];
            GRANT EXECUTE ON sys.xp_regwrite TO [{SystemProcedureRunnerLogin}];
            """);

        // CREATE FULLTEXT STOPLIST and CREATE SEARCH PROPERTY LIST do not need Full-Text Search installed.
        yield return (Database, $"CREATE FULLTEXT STOPLIST [{ShopStoplist}];");
        yield return (Database, $"CREATE SEARCH PROPERTY LIST [{ShopPropertyList}];");

        var users = new StringBuilder();
        foreach (var login in new[]
        {
            UserAltererLogin, LoginAltererLogin, EndpointAltererLogin, RouteAltererLogin, RouteRoleAltererLogin,
            MessageTypeAltererLogin, ContractTakerLogin, BindingAltererLogin, StoplistTakerLogin, PropertyListAltererLogin,
            CredentialAltererLogin, PrincipalViewerLogin,
        })
        {
            users.Append($"CREATE USER [{login}] FOR LOGIN [{login}];\nGRANT SELECT ON SCHEMA::dbo TO [{login}];\n");
        }

        yield return (Database, $"""
            {users}
            CREATE USER [{TargetUser}] WITHOUT LOGIN;
            CREATE MESSAGE TYPE [{InvoiceMessageType}] VALIDATION = NONE;
            CREATE CONTRACT [{InvoiceContract}] ([{InvoiceMessageType}] SENT BY ANY);
            CREATE REMOTE SERVICE BINDING [{InvoiceBinding}] TO SERVICE N'//remote/invoices' WITH USER = [{TargetUser}], ANONYMOUS = ON;
            CREATE DATABASE SCOPED CREDENTIAL [{ShopCredential}] WITH IDENTITY = N'probe', SECRET = N'{CredentialSecret}';
            CREATE ROLE [{RouteAdminsRole}];
            GRANT ALTER ON ROUTE::[{LocalRoute}] TO [{RouteAdminsRole}];
            ALTER ROLE [{RouteAdminsRole}] ADD MEMBER [{RouteRoleAltererLogin}];

            GRANT ALTER ON USER::[{TargetUser}] TO [{UserAltererLogin}];
            GRANT ALTER ON ROUTE::[{LocalRoute}] TO [{RouteAltererLogin}];
            GRANT ALTER ON MESSAGE TYPE::[{InvoiceMessageType}] TO [{MessageTypeAltererLogin}];
            GRANT TAKE OWNERSHIP ON CONTRACT::[{InvoiceContract}] TO [{ContractTakerLogin}];
            GRANT ALTER ON REMOTE SERVICE BINDING::[{InvoiceBinding}] TO [{BindingAltererLogin}];
            GRANT TAKE OWNERSHIP ON FULLTEXT STOPLIST::[{ShopStoplist}] TO [{StoplistTakerLogin}];
            GRANT ALTER ON SEARCH PROPERTY LIST::[{ShopPropertyList}] TO [{PropertyListAltererLogin}];
            GRANT ALTER ON DATABASE SCOPED CREDENTIAL::[{ShopCredential}] TO [{CredentialAltererLogin}];

            GRANT VIEW DEFINITION ON USER::[{TargetUser}] TO [{PrincipalViewerLogin}];
            GRANT VIEW DEFINITION ON APPLICATION ROLE::[{ReportingAppRole}] TO [{PrincipalViewerLogin}];
            """);

        foreach (var batch in OwnerAndGrantOptionSeedBatches())
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The owners of securables no grant row shows, the logins holding an allowed right WITH GRANT OPTION, and the
    /// login reaching a grant through a role in a role. Runs after the backstop batches (the target user exists then).
    /// </summary>
    private IEnumerable<(string Database, string Sql)> OwnerAndGrantOptionSeedBatches()
    {
        yield return ("master", $"""
            CREATE ENDPOINT [{OwnedEndpoint}] STATE = STOPPED
                AS TCP (LISTENER_PORT = {OwnedEndpointPort}) FOR SERVICE_BROKER (AUTHENTICATION = WINDOWS);
            ALTER AUTHORIZATION ON ENDPOINT::[{OwnedEndpoint}] TO [{EndpointOwnerLogin}];
            GRANT VIEW ANY DATABASE TO [{ServerGrantorLogin}] WITH GRANT OPTION;
            CREATE SERVER ROLE [{ServerGrantorsRole}];
            GRANT VIEW ANY DATABASE TO [{ServerGrantorsRole}] WITH GRANT OPTION;
            ALTER SERVER ROLE [{ServerGrantorsRole}] ADD MEMBER [{RoleServerGrantorLogin}];
            """);
        yield return (Database, $"CREATE FULLTEXT STOPLIST [{OwnedStoplist}];");
        yield return (Database, $"CREATE SEARCH PROPERTY LIST [{OwnedPropertyList}];");

        var users = new StringBuilder();
        foreach (var login in new[]
        {
            MessageTypeOwnerLogin, ContractOwnerLogin, BindingOwnerLogin, RouteOwnerLogin, StoplistOwnerLogin,
            PropertyListOwnerLogin, CredentialOwnerLogin, EndpointOwnerLogin, SchemaGrantorLogin, ObjectGrantorLogin,
            ColumnGrantorLogin, DatabaseGrantorLogin, ServerGrantorLogin, RoleSchemaGrantorLogin, RoleObjectGrantorLogin,
            RoleColumnGrantorLogin, RoleDatabaseGrantorLogin, RoleServerGrantorLogin, DatabaseSelectGrantorLogin,
            InsertGrantorLogin, NestedRouteAltererLogin,
        })
        {
            users.Append($"CREATE USER [{login}] FOR LOGIN [{login}];\nGRANT SELECT ON SCHEMA::dbo TO [{login}];\n");
        }

        yield return (Database, $"""
            {users}
            CREATE MESSAGE TYPE [{OwnedMessageType}] VALIDATION = NONE;
            CREATE CONTRACT [{OwnedContract}] ([{OwnedMessageType}] SENT BY ANY);
            CREATE REMOTE SERVICE BINDING [{OwnedBinding}] TO SERVICE N'//remote/owned' WITH USER = [{TargetUser}], ANONYMOUS = ON;
            CREATE ROUTE [{OwnedRoute}] WITH ADDRESS = N'TCP://10.0.0.2:4022';
            CREATE DATABASE SCOPED CREDENTIAL [{OwnedCredential}] WITH IDENTITY = N'probe', SECRET = N'{CredentialSecret}';
            ALTER AUTHORIZATION ON MESSAGE TYPE::[{OwnedMessageType}] TO [{MessageTypeOwnerLogin}];
            ALTER AUTHORIZATION ON CONTRACT::[{OwnedContract}] TO [{ContractOwnerLogin}];
            ALTER AUTHORIZATION ON REMOTE SERVICE BINDING::[{OwnedBinding}] TO [{BindingOwnerLogin}];
            ALTER AUTHORIZATION ON ROUTE::[{OwnedRoute}] TO [{RouteOwnerLogin}];
            ALTER AUTHORIZATION ON FULLTEXT STOPLIST::[{OwnedStoplist}] TO [{StoplistOwnerLogin}];
            ALTER AUTHORIZATION ON SEARCH PROPERTY LIST::[{OwnedPropertyList}] TO [{PropertyListOwnerLogin}];
            ALTER AUTHORIZATION ON DATABASE SCOPED CREDENTIAL::[{OwnedCredential}] TO [{CredentialOwnerLogin}];

            GRANT SELECT ON SCHEMA::dbo TO [{SchemaGrantorLogin}] WITH GRANT OPTION;
            GRANT SELECT ON dbo.Customers TO [{ObjectGrantorLogin}] WITH GRANT OPTION;
            GRANT SELECT ON dbo.Customers (City) TO [{ColumnGrantorLogin}] WITH GRANT OPTION;
            GRANT CONNECT TO [{DatabaseGrantorLogin}] WITH GRANT OPTION;
            GRANT SELECT TO [{DatabaseSelectGrantorLogin}] WITH GRANT OPTION;
            GRANT INSERT ON dbo.Customers TO [{InsertGrantorLogin}] WITH GRANT OPTION;
            CREATE ROLE [{SchemaGrantorsRole}];
            GRANT SELECT ON SCHEMA::dbo TO [{SchemaGrantorsRole}] WITH GRANT OPTION;
            ALTER ROLE [{SchemaGrantorsRole}] ADD MEMBER [{RoleSchemaGrantorLogin}];
            CREATE ROLE [{ObjectGrantorsRole}];
            GRANT SELECT ON dbo.Customers TO [{ObjectGrantorsRole}] WITH GRANT OPTION;
            ALTER ROLE [{ObjectGrantorsRole}] ADD MEMBER [{RoleObjectGrantorLogin}];
            CREATE ROLE [{ColumnGrantorsRole}];
            GRANT SELECT ON dbo.Customers (City) TO [{ColumnGrantorsRole}] WITH GRANT OPTION;
            ALTER ROLE [{ColumnGrantorsRole}] ADD MEMBER [{RoleColumnGrantorLogin}];
            CREATE ROLE [{DatabaseGrantorsRole}];
            GRANT CONNECT TO [{DatabaseGrantorsRole}] WITH GRANT OPTION;
            ALTER ROLE [{DatabaseGrantorsRole}] ADD MEMBER [{RoleDatabaseGrantorLogin}];

            CREATE ROLE [{RouteInnerRole}];
            CREATE ROLE [{RouteOuterRole}];
            ALTER ROLE [{RouteInnerRole}] ADD MEMBER [{NestedRouteAltererLogin}];
            ALTER ROLE [{RouteOuterRole}] ADD MEMBER [{RouteInnerRole}];
            GRANT ALTER ON ROUTE::[{LocalRoute}] TO [{RouteOuterRole}];
            """);

        foreach (var batch in RoleOwnerAndPublicGrantSeedBatches())
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Securables owned by a role the login is in (database and server), and <c>public</c> holding rights
    /// WITH GRANT OPTION in a database of its own, so that those grants reach no other test's login. The endpoint
    /// is a stopped database mirroring endpoint: an instance takes one Service Broker endpoint only (error 7807).
    /// </summary>
    private IEnumerable<(string Database, string Sql)> RoleOwnerAndPublicGrantSeedBatches()
    {
        yield return ("master", $"""
            CREATE SERVER ROLE [{EndpointOwnersRole}];
            ALTER SERVER ROLE [{EndpointOwnersRole}] ADD MEMBER [{RoleEndpointOwnerLogin}];
            CREATE ENDPOINT [{RoleOwnedEndpoint}] STATE = STOPPED
                AS TCP (LISTENER_PORT = {RoleOwnedEndpointPort}) FOR DATABASE_MIRRORING (ROLE = PARTNER, AUTHENTICATION = WINDOWS);
            ALTER AUTHORIZATION ON ENDPOINT::[{RoleOwnedEndpoint}] TO [{EndpointOwnersRole}];
            CREATE SERVER ROLE [{ServerRoleOwnersRole}];
            ALTER SERVER ROLE [{ServerRoleOwnersRole}] ADD MEMBER [{RoleServerRoleOwnerLogin}];
            CREATE SERVER ROLE [{RoleOwnedServerRole}] AUTHORIZATION [{ServerRoleOwnersRole}];
            CREATE DATABASE [{PublicGrantDatabase}];
            """);

        var users = new StringBuilder();
        foreach (var login in new[]
        {
            RoleSchemaOwnerLogin, RoleMessageTypeOwnerLogin, RoleStoplistOwnerLogin, RoleEndpointOwnerLogin,
            RoleServerRoleOwnerLogin,
        })
        {
            users.Append($"CREATE USER [{login}] FOR LOGIN [{login}];\nGRANT SELECT ON SCHEMA::dbo TO [{login}];\n");
        }

        yield return (Database, $"""
            {users}
            CREATE ROLE [{SchemaOwnersRole}];
            ALTER ROLE [{SchemaOwnersRole}] ADD MEMBER [{RoleSchemaOwnerLogin}];
            CREATE ROLE [{MessageTypeOwnersRole}];
            ALTER ROLE [{MessageTypeOwnersRole}] ADD MEMBER [{RoleMessageTypeOwnerLogin}];
            CREATE MESSAGE TYPE [{RoleOwnedMessageType}] AUTHORIZATION [{MessageTypeOwnersRole}] VALIDATION = NONE;
            CREATE ROLE [{StoplistOwnersRole}];
            ALTER ROLE [{StoplistOwnersRole}] ADD MEMBER [{RoleStoplistOwnerLogin}];
            """);
        yield return (Database, $"CREATE SCHEMA [{RoleOwnedSchema}] AUTHORIZATION [{SchemaOwnersRole}];");
        yield return (Database, $"CREATE FULLTEXT STOPLIST [{RoleOwnedStoplist}] AUTHORIZATION [{StoplistOwnersRole}];");

        yield return (PublicGrantDatabase, $"""
            CREATE TABLE dbo.{PublicNotesTable} (Id int NOT NULL PRIMARY KEY);
            CREATE USER [{PublicGrantReaderLogin}] FOR LOGIN [{PublicGrantReaderLogin}];
            GRANT SELECT ON dbo.{PublicNotesTable} TO public WITH GRANT OPTION;
            GRANT INSERT ON dbo.{PublicNotesTable} TO public WITH GRANT OPTION;
            """);
    }

    private IEnumerable<(string Database, string Sql)> SeedBatches()
    {
        yield return ("master", $"CREATE DATABASE [{Database}]; CREATE DATABASE [{OtherDatabase}]; CREATE DATABASE [{SalesDatabase}]; " +
            $"CREATE DATABASE {Bracket(QuotedDatabase)}; CREATE DATABASE [{ExampleDatabase}]; CREATE DATABASE [{BrokerDatabase}]; " +
            $"CREATE DATABASE [{ShippedDatabase}];");

        yield return (Database, """
            CREATE TABLE dbo.Customers (
                CustomerId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                Name nvarchar(100) NOT NULL,
                City nvarchar(50) NULL,
                Notes nvarchar(max) NULL,
                Photo varbinary(max) NULL);
            CREATE TABLE dbo.Orders (
                OrderId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
                CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (CustomerId),
                OrderedAt datetime2(0) NOT NULL,
                Total decimal(12, 2) NOT NULL);
            CREATE TABLE dbo.OrderLines (
                OrderId int NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES dbo.Orders (OrderId),
                LineNumber int NOT NULL,
                Product nvarchar(80) NOT NULL,
                Quantity int NOT NULL,
                UnitPrice decimal(10, 2) NOT NULL,
                LineTotal AS (Quantity * UnitPrice),
                CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));
            CREATE SEQUENCE dbo.InvoiceNumbers AS int START WITH 1000;
            """);

        yield return (Database, """
            CREATE VIEW dbo.CustomerTotals AS
            SELECT c.CustomerId, c.Name, SUM(o.Total) AS Total
            FROM dbo.Customers AS c JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId
            GROUP BY c.CustomerId, c.Name;
            """);

        yield return (Database, """
            CREATE PROCEDURE dbo.AddCustomer @name nvarchar(100) AS
            INSERT INTO dbo.Customers (Name) VALUES (@name);
            """);

        yield return (Database, """
            WITH n AS (SELECT TOP (120) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Customers (Name, City, Notes, Photo)
            SELECT
                CONCAT(f.v, N' ', l.v, N' ', n.i),
                c.v,
                CASE WHEN n.i <= 3 THEN REPLICATE(CAST(N'ğüşiöç İĞÜŞÖÇ ' AS nvarchar(max)), 400) ELSE N'Kısa not' END,
                CASE WHEN n.i <= 3 THEN CAST(REPLICATE(CAST('x' AS varchar(max)), 5000) AS varbinary(max)) END
            FROM n
            CROSS APPLY (SELECT CASE n.i % 5 WHEN 0 THEN N'Çağla' WHEN 1 THEN N'İsmail' WHEN 2 THEN N'Şule'
                                             WHEN 3 THEN N'Ömer' ELSE N'Gülşen' END) AS f(v)
            CROSS APPLY (SELECT CASE n.i % 4 WHEN 0 THEN N'Öztürk' WHEN 1 THEN N'Yıldız' WHEN 2 THEN N'Işık'
                                             ELSE N'Doğan' END) AS l(v)
            CROSS APPLY (SELECT CASE n.i % 3 WHEN 0 THEN N'İstanbul' WHEN 1 THEN N'İzmir' ELSE N'Muğla' END) AS c(v);

            WITH n AS (SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Orders (CustomerId, OrderedAt, Total)
            SELECT (n.i % 120) + 1, DATEADD(day, n.i, '2026-01-01'), CAST(n.i * 7.25 AS decimal(12, 2))
            FROM n;

            WITH n AS (SELECT TOP (600) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.OrderLines (OrderId, LineNumber, Product, Quantity, UnitPrice)
            SELECT ((n.i - 1) / 2) + 1, ((n.i - 1) % 2) + 1,
                   CASE n.i % 3 WHEN 0 THEN N'Çay bardağı' WHEN 1 THEN N'Şeker' ELSE N'Gözlük' END,
                   (n.i % 5) + 1, CAST((n.i % 40) + 0.5 AS decimal(10, 2))
            FROM n;
            """);

        yield return (OtherDatabase, """
            CREATE TABLE dbo.ArchivedOrders (Id int NOT NULL PRIMARY KEY, Note nvarchar(50) NOT NULL);
            INSERT INTO dbo.ArchivedOrders (Id, Note)
            SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Arşiv' FROM sys.all_objects;
            """);

        var logins = new StringBuilder();
        foreach (var (login, password) in new[]
        {
            (ReaderLogin, ReaderPassword), (DataReaderLogin, DataReaderPassword), (UpdaterLogin, UpdaterPassword), (WriterLogin, WriterPassword), (OwnerLogin, OwnerPassword),
        })
        {
            logins.Append($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', DEFAULT_DATABASE = [{Database}];\n");
        }

        foreach (var login in ExtraLogins)
        {
            logins.Append($"CREATE LOGIN [{login}] WITH PASSWORD = N'{ExtraPasswords[login]}', DEFAULT_DATABASE = [{Database}];\n");
        }

        logins.Append($"ALTER SERVER ROLE securityadmin ADD MEMBER [{SecurityAdminLogin}];\n");
        logins.Append($"GRANT ALTER TRACE TO [{TracerLogin}];\n");
        logins.Append($"DENY VIEW ANY DATABASE TO [{NoDatabaseListLogin}];\n");
        yield return ("master", logins.ToString());

        yield return (Database, $"""
            CREATE USER [{ReaderLogin}] FOR LOGIN [{ReaderLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ReaderLogin}];
            CREATE USER [{DataReaderLogin}] FOR LOGIN [{DataReaderLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{DataReaderLogin}];
            CREATE USER [{UpdaterLogin}] FOR LOGIN [{UpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{UpdaterLogin}];
            GRANT UPDATE ON dbo.Customers TO [{UpdaterLogin}];
            CREATE USER [{WriterLogin}] FOR LOGIN [{WriterLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{WriterLogin}];
            ALTER ROLE db_datawriter ADD MEMBER [{WriterLogin}];
            CREATE USER [{OwnerLogin}] FOR LOGIN [{OwnerLogin}];
            ALTER ROLE db_owner ADD MEMBER [{OwnerLogin}];
            CREATE USER [{ColumnUpdaterLogin}] FOR LOGIN [{ColumnUpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ColumnUpdaterLogin}];
            GRANT UPDATE ON dbo.Customers (City) TO [{ColumnUpdaterLogin}];
            CREATE USER [{ImpersonatorLogin}] FOR LOGIN [{ImpersonatorLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ImpersonatorLogin}];
            GRANT IMPERSONATE ON USER::[{ReaderLogin}] TO [{ImpersonatorLogin}];
            CREATE USER [{RoleAdminLogin}] FOR LOGIN [{RoleAdminLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{RoleAdminLogin}];
            GRANT ALTER ANY ROLE TO [{RoleAdminLogin}];
            CREATE USER [{SecurityAdminLogin}] FOR LOGIN [{SecurityAdminLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SecurityAdminLogin}];
            CREATE USER [{ProcedureRunnerLogin}] FOR LOGIN [{ProcedureRunnerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ProcedureRunnerLogin}];
            GRANT EXECUTE ON dbo.AddCustomer TO [{ProcedureRunnerLogin}];
            CREATE USER [{SequenceUserLogin}] FOR LOGIN [{SequenceUserLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SequenceUserLogin}];
            GRANT UPDATE ON dbo.InvoiceNumbers TO [{SequenceUserLogin}];
            CREATE USER [{DenyWriterLogin}] FOR LOGIN [{DenyWriterLogin}];
            ALTER ROLE db_denydatawriter ADD MEMBER [{DenyWriterLogin}];
            CREATE USER [{TracerLogin}] FOR LOGIN [{TracerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{TracerLogin}];
            CREATE USER [{BackupLogin}] FOR LOGIN [{BackupLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{BackupLogin}];
            GRANT BACKUP DATABASE TO [{BackupLogin}];
            CREATE USER [{UnmaskLogin}] FOR LOGIN [{UnmaskLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{UnmaskLogin}];
            GRANT UNMASK TO [{UnmaskLogin}];
            CREATE ROLE [{ReportingRole}];
            CREATE USER [{RoleTakerLogin}] FOR LOGIN [{RoleTakerLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{RoleTakerLogin}];
            GRANT TAKE OWNERSHIP ON ROLE::[{ReportingRole}] TO [{RoleTakerLogin}];
            CREATE USER [{NoDatabaseListLogin}] FOR LOGIN [{NoDatabaseListLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{NoDatabaseListLogin}];
            CREATE SYNONYM {CustomerSynonym} FOR dbo.Customers;
            CREATE USER [{SynonymUpdaterLogin}] FOR LOGIN [{SynonymUpdaterLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{SynonymUpdaterLogin}];
            GRANT UPDATE ON {CustomerSynonym} TO [{SynonymUpdaterLogin}];
            """);

        foreach (var batch in ClassSeedBatches())
        {
            yield return batch;
        }

        foreach (var batch in ShippedSeedBatches())
        {
            yield return batch;
        }

        foreach (var batch in BackstopSeedBatches())
        {
            yield return batch;
        }

        yield return (QuotedDatabase, $"""
            CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
            INSERT INTO dbo.Items (Id, Name) VALUES (1, N'Bir'), (2, N'İki');
            CREATE USER [{ReaderLogin}] FOR LOGIN [{ReaderLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{ReaderLogin}];
            CREATE USER [{QuotedCreatorLogin}] FOR LOGIN [{QuotedCreatorLogin}];
            GRANT SELECT ON SCHEMA::dbo TO [{QuotedCreatorLogin}];
            GRANT CREATE TABLE TO [{QuotedCreatorLogin}];
            """);

        // The three tables scripts/create-readonly-login.sql grants SELECT on as shipped, and one it does not.
        yield return (ExampleDatabase, """
            CREATE TABLE dbo.ExampleCustomers (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
            CREATE TABLE dbo.ExampleOrders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL);
            CREATE TABLE dbo.ExampleOrderLines (OrderId int NOT NULL, LineNumber int NOT NULL, PRIMARY KEY (OrderId, LineNumber));
            CREATE TABLE dbo.NotGranted (Id int NOT NULL PRIMARY KEY);
            INSERT INTO dbo.ExampleCustomers (Id, Name) VALUES (1, N'Örnek');
            """);

        // The objects the gate's regression table names (dbo.A..F, dbo.Orders and their columns), with a few rows.
        yield return (SalesDatabase, $$"""
            CREATE TABLE dbo.A (
                Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Code nvarchar(20) NULL, a int NULL, b int NULL,
                x float NULL, y float NULL, g int NULL, h int NULL, d datetime2(0) NULL, Doc nvarchar(max) NULL,
                [weird col] int NULL, [quoted] int NULL);
            CREATE TABLE dbo.B (Id int NOT NULL PRIMARY KEY, AId int NULL, Name nvarchar(50) NULL);
            CREATE TABLE dbo.C (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.D (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.E (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.F (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Total decimal(12, 2) NOT NULL, Flags int NOT NULL);
            INSERT INTO dbo.A (Id, Name, Code, a, b, x, y, g, h, d, Doc, [weird col], [quoted]) VALUES
                (1, N'alpha', N'12', 0, 1, 1.5, 2.5, 1, 1, '2026-01-15T10:00:00', N'{"a":1,"b":[1,2]}', 1, 1),
                (2, N'beta', N'x9', 2, NULL, -3.25, 4, 2, 1, '2026-02-20T11:30:00', N'{"a":"z"}', 2, 2),
                (3, NULL, NULL, NULL, 3, NULL, NULL, 2, 2, NULL, NULL, NULL, NULL);
            INSERT INTO dbo.B (Id, AId, Name) VALUES (1, 1, N'b1'), (2, 1, N'b2'), (3, 2, N'b3');
            INSERT INTO dbo.C (Id) VALUES (1), (2);
            INSERT INTO dbo.D (Id) VALUES (1);
            INSERT INTO dbo.E (Id) VALUES (2);
            INSERT INTO dbo.F (Id) VALUES (1), (2);
            INSERT INTO dbo.Orders (Id, CustomerId, Total, Flags) VALUES (1, 1, 10.50, 1), (2, 1, 99.99, 2), (3, 2, 0, 0);

            CREATE TABLE dbo.TypeSamples (Id int NOT NULL PRIMARY KEY, Doc xml NULL, Node hierarchyid NULL);
            INSERT INTO dbo.TypeSamples (Id, Doc, Node) VALUES
                (1, N'<order id="1"><line>Çay bardağı</line></order>', hierarchyid::Parse('/1/2/')),
                (2, CAST(N'<r>' + REPLICATE(CAST(N'x' AS nvarchar(max)), 3000) + N'</r>' AS xml), hierarchyid::GetRoot()),
                (3, NULL, NULL);

            CREATE TABLE dbo.LockLab (Id int NOT NULL PRIMARY KEY, V int NOT NULL);
            INSERT INTO dbo.LockLab (Id, V) VALUES (1, 0), (2, 0);

            CREATE TABLE dbo.[Müşteri_Şubeleri] (Id int NOT NULL PRIMARY KEY, Şehir nvarchar(50) NOT NULL);
            INSERT INTO dbo.[Müşteri_Şubeleri] (Id, Şehir) VALUES (1, N'İzmir');

            -- Binary values of exactly 1 MiB, 1 MiB + 1 byte and 2,000,000 bytes.
            CREATE TABLE dbo.BlobLab (Id int NOT NULL PRIMARY KEY, Data varbinary(max) NOT NULL);
            INSERT INTO dbo.BlobLab (Id, Data) VALUES
                (1, CAST(REPLICATE(CAST('b' AS varchar(max)), 1048576) AS varbinary(max))),
                (2, CAST(REPLICATE(CAST('b' AS varchar(max)), 1048577) AS varbinary(max))),
                (3, CAST(REPLICATE(CAST('b' AS varchar(max)), 2000000) AS varbinary(max)));

            {{WideTableSql()}}

            CREATE USER [{{ReaderLogin}}] FOR LOGIN [{{ReaderLogin}}];
            GRANT SELECT ON SCHEMA::dbo TO [{{ReaderLogin}}];
            """);

        // A schema the reader has no grant on (CREATE SCHEMA must start its batch).
        yield return (SalesDatabase, "CREATE SCHEMA hr;");
        yield return (SalesDatabase, """
            CREATE TABLE hr.Payroll (Id int NOT NULL PRIMARY KEY, Salary decimal(12, 2) NOT NULL);
            INSERT INTO hr.Payroll (Id, Salary) VALUES (1, 12345.67);
            """);

        // Everything the reader login sends: batches, RPCs and logins, filtered on the login name. The application
        // name is chosen by the client, so it cannot be trusted to catch everything the login sends; the login name
        // is set by the server at authentication. NO_EVENT_LOSS, so an empty trace cannot be the result of a drop.
        yield return ("master", $"""
            CREATE EVENT SESSION [{TraceSession}] ON SERVER
            ADD EVENT sqlserver.sql_batch_starting (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}')),
            ADD EVENT sqlserver.rpc_starting (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}')),
            ADD EVENT sqlserver.login (
                ACTION (sqlserver.client_app_name, sqlserver.server_principal_name)
                WHERE (sqlserver.server_principal_name = N'{ReaderLogin}'))
            ADD TARGET package0.ring_buffer (SET max_memory = 16384, max_events_limit = 100000)
            WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS, STARTUP_STATE = OFF);
            """);
    }
}

/// <summary>One Extended Events record: the event name, the client's application name, the login and the SQL text.</summary>
public sealed record TraceEvent(string Name, string? Application, string? Principal, string? Text);

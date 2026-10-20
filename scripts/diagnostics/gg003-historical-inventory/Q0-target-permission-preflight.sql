-- IMP-1C query packet Q0. REVIEW ONLY: execution requires completed authorization.
-- No USE, SQLCMD connection directive, credentials, DML, DDL, EXEC or application boot.
DECLARE @ApprovedServer sysname = N'__APPROVED_SERVER__';
DECLARE @ApprovedDatabase sysname = N'__APPROVED_DATABASE__';
IF @ApprovedServer LIKE N'__APPROVED_%' OR @ApprovedDatabase LIKE N'__APPROVED_%'
    THROW 51000, 'Fill approved target identifiers only after execution authorization.', 1;
IF CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) COLLATE Latin1_General_100_BIN2
       <> @ApprovedServer COLLATE Latin1_General_100_BIN2
   OR DB_NAME() COLLATE Latin1_General_100_BIN2 <> @ApprovedDatabase COLLATE Latin1_General_100_BIN2
    THROW 51001, 'Connected target differs from approved disposable target.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases
               WHERE database_id = DB_ID() AND state_desc = N'ONLINE' AND is_read_only = 1)
    THROW 51002, 'Approved target must already be ONLINE and READ_ONLY.', 1;
IF ISNULL(IS_SRVROLEMEMBER('sysadmin'), 1) <> 0
   OR ISNULL(IS_ROLEMEMBER('db_owner'), 1) <> 0
    THROW 51003, 'Owner/admin or unverified role membership is not accepted.', 1;

SELECT N'Q0-identity' AS ResultSet, SERVERPROPERTY('ServerName') AS SqlServer,
       DB_NAME() AS DatabaseName, ORIGINAL_LOGIN() AS OriginalLogin,
       SUSER_SNAME() AS LoginName, USER_NAME() AS DatabaseUser,
       SYSDATETIMEOFFSET() AS CapturedAt;
SELECT N'Q0-database' AS ResultSet, name, state_desc, is_read_only, source_database_id
FROM sys.databases WHERE database_id = DB_ID();
SELECT N'Q0-server-permissions' AS ResultSet, permission_name
FROM sys.fn_my_permissions(NULL, 'SERVER') ORDER BY permission_name;
SELECT N'Q0-database-permissions' AS ResultSet, permission_name
FROM sys.fn_my_permissions(NULL, 'DATABASE') ORDER BY permission_name;
SELECT N'Q0-database-token' AS ResultSet, name, type, usage FROM sys.user_token ORDER BY name;
SELECT N'Q0-login-token' AS ResultSet, name, type, usage FROM sys.login_token ORDER BY name;
SELECT N'Q0-direct-role-links-visible' AS ResultSet,
       USER_NAME(role_principal_id) AS RoleName,
       USER_NAME(member_principal_id) AS MemberName
FROM sys.database_role_members ORDER BY RoleName, MemberName;
-- STOP. These visible results are discovery only, not comprehensive permission proof.
-- An administrator must attest nested/inherited grants, object permissions,
-- ownership/impersonation/module paths, metadata visibility and restricted access.
-- Do not proceed automatically to Q1 or any source-data SELECT.

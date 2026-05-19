-- NextHireDB: organizational roles and teams (idempotent; run against NextHireDb connection)

IF OBJECT_ID(N'dbo.roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.roles (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_roles PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        code NVARCHAR(64) NOT NULL,
        name NVARCHAR(200) NULL,
        description NVARCHAR(MAX) NULL,
        created_at DATETIMEOFFSET(7) NOT NULL,
        updated_at DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT UQ_roles_org_code UNIQUE (org_id, code)
    );
    CREATE INDEX IX_roles_org_id ON dbo.roles (org_id);
END

IF OBJECT_ID(N'dbo.teams', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.teams (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_teams PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        name NVARCHAR(200) NOT NULL,
        description NVARCHAR(MAX) NULL,
        created_at DATETIMEOFFSET(7) NOT NULL,
        updated_at DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT UQ_teams_org_name UNIQUE (org_id, name)
    );
    CREATE INDEX IX_teams_org_id ON dbo.teams (org_id);
END

IF OBJECT_ID(N'dbo.user_roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.user_roles (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_user_roles PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        user_id UNIQUEIDENTIFIER NOT NULL,
        role_id UNIQUEIDENTIFIER NOT NULL,
        created_at DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT UQ_user_roles_org_user_role UNIQUE (org_id, user_id, role_id),
        CONSTRAINT FK_user_roles_role FOREIGN KEY (role_id) REFERENCES dbo.roles (id)
    );
    CREATE INDEX IX_user_roles_org_user ON dbo.user_roles (org_id, user_id);
END

IF OBJECT_ID(N'dbo.team_members', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.team_members (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_team_members PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        team_id UNIQUEIDENTIFIER NOT NULL,
        user_id UNIQUEIDENTIFIER NOT NULL,
        is_team_lead BIT NOT NULL CONSTRAINT DF_team_members_is_team_lead DEFAULT (0),
        created_at DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT UQ_team_members_org_team_user UNIQUE (org_id, team_id, user_id),
        CONSTRAINT FK_team_members_team FOREIGN KEY (team_id) REFERENCES dbo.teams (id) ON DELETE CASCADE
    );
    CREATE INDEX IX_team_members_org_team ON dbo.team_members (org_id, team_id);
END

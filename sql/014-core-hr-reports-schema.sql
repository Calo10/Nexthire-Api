-- =============================================================================
-- NextHire — Core HR Reports & People Analytics
-- Report definitions, favorites, dashboards, schedules, executions.
-- Idempotent. Soft archive. No Recruitment datasets. No binary storage.
-- =============================================================================

IF OBJECT_ID(N'dbo.nh_hr_report_definitions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_definitions (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        name                    nvarchar(200)      NOT NULL,
        description             nvarchar(1000)     NULL,
        category                nvarchar(80)       NOT NULL,
        dataset_key             nvarchar(80)       NOT NULL,
        configuration_json      nvarchar(max)      NOT NULL,
        visualization_json      nvarchar(max)      NULL,
        owner_user_id           uniqueidentifier   NULL,
        visibility              nvarchar(40)       NOT NULL
            CONSTRAINT DF_nh_hr_report_def_vis DEFAULT (N'Private'),
        is_system               bit                NOT NULL
            CONSTRAINT DF_nh_hr_report_def_sys DEFAULT (0),
        configuration_version   int                NOT NULL
            CONSTRAINT DF_nh_hr_report_def_ver DEFAULT (1),
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        archived_by             uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_report_definitions PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_nh_hr_report_def_vis CHECK (visibility IN (
            N'Private', N'SharedUsers', N'SharedRoles', N'Organization'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_def_org
        ON dbo.nh_hr_report_definitions (organization_id, category, archived_at)
        WHERE archived_at IS NULL;
END;

IF OBJECT_ID(N'dbo.nh_hr_report_favorites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_favorites (
        organization_id         uniqueidentifier   NOT NULL,
        user_id                 uniqueidentifier   NOT NULL,
        report_definition_id    uniqueidentifier   NOT NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_report_favorites PRIMARY KEY CLUSTERED (organization_id, user_id, report_definition_id),
        CONSTRAINT FK_nh_hr_report_fav_def
            FOREIGN KEY (report_definition_id) REFERENCES dbo.nh_hr_report_definitions (id)
    );
END;

IF OBJECT_ID(N'dbo.nh_hr_report_dashboards', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_dashboards (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        name                    nvarchar(200)      NOT NULL,
        description             nvarchar(1000)     NULL,
        owner_user_id           uniqueidentifier   NOT NULL,
        visibility              nvarchar(40)       NOT NULL
            CONSTRAINT DF_nh_hr_report_dash_vis DEFAULT (N'Private'),
        global_filters_json     nvarchar(max)      NULL,
        layout_json             nvarchar(max)      NULL,
        is_default              bit                NOT NULL
            CONSTRAINT DF_nh_hr_report_dash_def DEFAULT (0),
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_report_dashboards PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_nh_hr_report_dash_vis CHECK (visibility IN (
            N'Private', N'SharedUsers', N'SharedRoles', N'Organization'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_dash_org
        ON dbo.nh_hr_report_dashboards (organization_id, owner_user_id)
        WHERE archived_at IS NULL;
END;

IF OBJECT_ID(N'dbo.nh_hr_report_dashboard_widgets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_dashboard_widgets (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        dashboard_id            uniqueidentifier   NOT NULL,
        report_definition_id    uniqueidentifier   NULL,
        widget_type             nvarchar(40)       NOT NULL,
        title                   nvarchar(200)      NULL,
        configuration_json      nvarchar(max)      NOT NULL,
        grid_x                  int                NOT NULL CONSTRAINT DF_nh_hr_wdg_x DEFAULT (0),
        grid_y                  int                NOT NULL CONSTRAINT DF_nh_hr_wdg_y DEFAULT (0),
        grid_w                  int                NOT NULL CONSTRAINT DF_nh_hr_wdg_w DEFAULT (4),
        grid_h                  int                NOT NULL CONSTRAINT DF_nh_hr_wdg_h DEFAULT (3),
        sort_order              int                NOT NULL CONSTRAINT DF_nh_hr_wdg_ord DEFAULT (0),
        created_at              datetimeoffset(7)  NOT NULL,
        updated_at              datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_report_dashboard_widgets PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_wdg_dash
            FOREIGN KEY (dashboard_id) REFERENCES dbo.nh_hr_report_dashboards (id),
        CONSTRAINT FK_nh_hr_wdg_report
            FOREIGN KEY (report_definition_id) REFERENCES dbo.nh_hr_report_definitions (id)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_wdg_dash
        ON dbo.nh_hr_report_dashboard_widgets (dashboard_id, sort_order);
END;

IF OBJECT_ID(N'dbo.nh_hr_report_schedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_schedules (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        report_definition_id    uniqueidentifier   NOT NULL,
        owner_user_id           uniqueidentifier   NOT NULL,
        name                    nvarchar(200)      NOT NULL,
        frequency               nvarchar(40)       NOT NULL,
        schedule_json           nvarchar(max)      NULL,
        timezone                nvarchar(80)       NOT NULL
            CONSTRAINT DF_nh_hr_report_sched_tz DEFAULT (N'UTC'),
        output_format           nvarchar(20)       NOT NULL
            CONSTRAINT DF_nh_hr_report_sched_fmt DEFAULT (N'Csv'),
        recipients_json         nvarchar(max)      NOT NULL,
        is_active               bit                NOT NULL
            CONSTRAINT DF_nh_hr_report_sched_active DEFAULT (1),
        next_run_at             datetimeoffset(7)  NULL,
        last_run_at             datetimeoffset(7)  NULL,
        last_status             nvarchar(40)       NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_report_schedules PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_sched_report
            FOREIGN KEY (report_definition_id) REFERENCES dbo.nh_hr_report_definitions (id),
        CONSTRAINT CK_nh_hr_report_sched_freq CHECK (frequency IN (
            N'Daily', N'Weekly', N'Monthly', N'Quarterly'
        )),
        CONSTRAINT CK_nh_hr_report_sched_fmt CHECK (output_format IN (
            N'Csv', N'Excel', N'Pdf'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_sched_due
        ON dbo.nh_hr_report_schedules (is_active, next_run_at)
        WHERE archived_at IS NULL AND is_active = 1;
END;

IF OBJECT_ID(N'dbo.nh_hr_report_executions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_executions (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        report_definition_id    uniqueidentifier   NULL,
        schedule_id             uniqueidentifier   NULL,
        requested_by            uniqueidentifier   NULL,
        status                  nvarchar(40)       NOT NULL,
        started_at              datetimeoffset(7)  NOT NULL,
        completed_at            datetimeoffset(7)  NULL,
        duration_ms             int                NULL,
        row_count               int                NULL,
        error_code              nvarchar(80)       NULL,
        error_message           nvarchar(1000)     NULL,
        output_format           nvarchar(20)       NULL,
        nexa_document_id        uniqueidentifier   NULL,
        expires_at              datetimeoffset(7)  NULL,
        filters_json            nvarchar(max)      NULL,
        CONSTRAINT PK_nh_hr_report_executions PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_nh_hr_report_exec_status CHECK (status IN (
            N'Pending', N'Running', N'Succeeded', N'Failed', N'Cancelled'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_exec_org
        ON dbo.nh_hr_report_executions (organization_id, started_at DESC);
    CREATE NONCLUSTERED INDEX IX_nh_hr_report_exec_sched
        ON dbo.nh_hr_report_executions (schedule_id, started_at DESC)
        WHERE schedule_id IS NOT NULL;
END;

IF OBJECT_ID(N'dbo.nh_hr_report_views', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_report_views (
        organization_id         uniqueidentifier   NOT NULL,
        user_id                 uniqueidentifier   NOT NULL,
        report_definition_id    uniqueidentifier   NOT NULL,
        last_viewed_at          datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_report_views PRIMARY KEY CLUSTERED (organization_id, user_id, report_definition_id)
    );
END;

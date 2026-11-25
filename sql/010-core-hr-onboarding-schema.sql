-- =============================================================================
-- NextHire — Core HR Onboarding schema
-- File: sql/010-core-hr-onboarding-schema.sql
-- Idempotent DDL. Tenant: organization_id. Template task snapshots on start.
-- =============================================================================

IF OBJECT_ID(N'dbo.nh_hr_onboarding_templates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_templates (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        name                    nvarchar(200)      NOT NULL,
        description             nvarchar(2000)     NULL,
        department_id           uniqueidentifier   NULL,
        job_title_id            uniqueidentifier   NULL,
        employment_type_id      uniqueidentifier   NULL,
        estimated_duration_days int                NULL,
        is_active               bit                NOT NULL CONSTRAINT DF_nh_hr_ob_tpl_active DEFAULT (1),
        is_default              bit                NOT NULL CONSTRAINT DF_nh_hr_ob_tpl_default DEFAULT (0),
        version                 int                NOT NULL CONSTRAINT DF_nh_hr_ob_tpl_version DEFAULT (1),
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        archived_by             uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_onboarding_templates PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_nh_hr_ob_tpl_version CHECK (version > 0)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_tpl_org
        ON dbo.nh_hr_onboarding_templates (organization_id, is_active)
        WHERE archived_at IS NULL;
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_template_tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_template_tasks (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        template_id             uniqueidentifier   NOT NULL,
        title                   nvarchar(300)      NOT NULL,
        description             nvarchar(2000)     NULL,
        category                nvarchar(80)       NOT NULL,
        display_order           int                NOT NULL CONSTRAINT DF_nh_hr_ob_tt_order DEFAULT (0),
        assignment_type         nvarchar(40)       NOT NULL,
        default_assignee_user_id uniqueidentifier  NULL,
        due_offset_days         int                NULL,
        is_required             bit                NOT NULL CONSTRAINT DF_nh_hr_ob_tt_req DEFAULT (1),
        requires_document       bit                NOT NULL CONSTRAINT DF_nh_hr_ob_tt_doc DEFAULT (0),
        document_category       nvarchar(80)       NULL,
        requires_approval       bit                NOT NULL CONSTRAINT DF_nh_hr_ob_tt_appr DEFAULT (0),
        depends_on_task_id      uniqueidentifier   NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_onboarding_template_tasks PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_ob_tt_template
            FOREIGN KEY (template_id) REFERENCES dbo.nh_hr_onboarding_templates (id),
        CONSTRAINT CK_nh_hr_ob_tt_assign CHECK (assignment_type IN (
            'Employee', 'DirectManager', 'HR', 'IT', 'SpecificUser'
        )),
        CONSTRAINT CK_nh_hr_ob_tt_category CHECK (category IN (
            'Documentation', 'Administrative', 'ITEquipment', 'Training',
            'Compliance', 'Orientation', 'Meetings', 'Other'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_tt_template
        ON dbo.nh_hr_onboarding_template_tasks (organization_id, template_id, display_order);
END;

IF OBJECT_ID(N'dbo.nh_hr_employee_onboardings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_onboardings (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        employee_id             uniqueidentifier   NOT NULL,
        template_id             uniqueidentifier   NULL,
        template_version        int                NULL,
        owner_user_id           uniqueidentifier   NULL,
        start_date              date               NULL,
        expected_completion_date date              NULL,
        actual_completion_date  date               NULL,
        status                  nvarchar(40)       NOT NULL,
        notes                   nvarchar(2000)     NULL,
        cancel_reason           nvarchar(1000)     NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_onboardings PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_ob_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT FK_nh_hr_ob_template
            FOREIGN KEY (template_id) REFERENCES dbo.nh_hr_onboarding_templates (id),
        CONSTRAINT CK_nh_hr_ob_status CHECK (status IN (
            'NotStarted', 'InProgress', 'InReview', 'Completed', 'Cancelled'
        ))
    );

    CREATE UNIQUE NONCLUSTERED INDEX UQ_nh_hr_ob_active_employee
        ON dbo.nh_hr_employee_onboardings (organization_id, employee_id)
        WHERE status IN ('NotStarted', 'InProgress', 'InReview');

    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_org_status
        ON dbo.nh_hr_employee_onboardings (organization_id, status, start_date);
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_tasks (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        onboarding_id           uniqueidentifier   NOT NULL,
        template_task_id        uniqueidentifier   NULL,
        title                   nvarchar(300)      NOT NULL,
        description             nvarchar(2000)     NULL,
        category                nvarchar(80)       NOT NULL,
        assignment_type         nvarchar(40)       NOT NULL,
        assigned_user_id        uniqueidentifier   NULL,
        due_date                date               NULL,
        status                  nvarchar(40)       NOT NULL,
        is_required             bit                NOT NULL CONSTRAINT DF_nh_hr_ob_task_req DEFAULT (1),
        requires_document       bit                NOT NULL CONSTRAINT DF_nh_hr_ob_task_doc DEFAULT (0),
        document_category       nvarchar(80)       NULL,
        requires_approval       bit                NOT NULL CONSTRAINT DF_nh_hr_ob_task_appr DEFAULT (0),
        approval_status         nvarchar(40)       NULL,
        approved_by             uniqueidentifier   NULL,
        approved_at             datetimeoffset(7)  NULL,
        completed_at            datetimeoffset(7)  NULL,
        completed_by            uniqueidentifier   NULL,
        completion_notes        nvarchar(2000)     NULL,
        display_order           int                NOT NULL CONSTRAINT DF_nh_hr_ob_task_order DEFAULT (0),
        depends_on_task_id      uniqueidentifier   NULL,
        ats_task_id             uniqueidentifier   NULL,
        is_waived               bit                NOT NULL CONSTRAINT DF_nh_hr_ob_task_waived DEFAULT (0),
        created_at              datetimeoffset(7)  NOT NULL,
        updated_at              datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_onboarding_tasks PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_ob_task_onboarding
            FOREIGN KEY (onboarding_id) REFERENCES dbo.nh_hr_employee_onboardings (id),
        CONSTRAINT CK_nh_hr_ob_task_status CHECK (status IN (
            'Pending', 'InProgress', 'Completed', 'Cancelled', 'Blocked'
        )),
        CONSTRAINT CK_nh_hr_ob_task_approval CHECK (
            approval_status IS NULL OR approval_status IN ('Pending', 'Approved', 'Rejected')
        )
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_tasks_onboarding
        ON dbo.nh_hr_onboarding_tasks (organization_id, onboarding_id, display_order);
    -- Filtered indexes cannot use NOT / NOT IN on SQL Server.
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_tasks_assignee
        ON dbo.nh_hr_onboarding_tasks (organization_id, assigned_user_id, due_date)
        WHERE status IN ('Pending', 'InProgress', 'Blocked');
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_documents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_documents (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        onboarding_id           uniqueidentifier   NOT NULL,
        onboarding_task_id      uniqueidentifier   NULL,
        employee_document_id    uniqueidentifier   NULL,
        category                nvarchar(80)       NOT NULL,
        name                    nvarchar(300)      NOT NULL,
        is_required             bit                NOT NULL CONSTRAINT DF_nh_hr_ob_doc_req DEFAULT (1),
        status                  nvarchar(40)       NOT NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_onboarding_documents PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_ob_doc_onboarding
            FOREIGN KEY (onboarding_id) REFERENCES dbo.nh_hr_employee_onboardings (id),
        CONSTRAINT CK_nh_hr_ob_doc_status CHECK (status IN (
            'Missing', 'Uploaded', 'PendingApproval', 'Approved', 'Rejected'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_docs
        ON dbo.nh_hr_onboarding_documents (organization_id, onboarding_id);
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_activity', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_activity (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        onboarding_id           uniqueidentifier   NOT NULL,
        activity_type           nvarchar(80)       NOT NULL,
        summary                 nvarchar(500)      NOT NULL,
        details_json            nvarchar(max)      NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        created_by_name         nvarchar(200)      NULL,
        CONSTRAINT PK_nh_hr_onboarding_activity PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_ob_act_onboarding
            FOREIGN KEY (onboarding_id) REFERENCES dbo.nh_hr_employee_onboardings (id),
        CONSTRAINT CK_nh_hr_ob_act_json
            CHECK (details_json IS NULL OR ISJSON(details_json) = 1)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_ob_activity
        ON dbo.nh_hr_onboarding_activity (organization_id, onboarding_id, created_at DESC);
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_notification_settings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_notification_settings (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        trigger_code            nvarchar(80)       NOT NULL,
        is_enabled              bit                NOT NULL CONSTRAINT DF_nh_hr_ob_notif_en DEFAULT (1),
        channel_email           bit                NOT NULL CONSTRAINT DF_nh_hr_ob_notif_email DEFAULT (1),
        channel_whatsapp        bit                NOT NULL CONSTRAINT DF_nh_hr_ob_notif_wa DEFAULT (0),
        days_before             int                NULL,
        recipients_json         nvarchar(max)      NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_onboarding_notification_settings PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_ob_notif_org_trigger UNIQUE (organization_id, trigger_code)
    );
END;

IF OBJECT_ID(N'dbo.nh_hr_onboarding_notifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_onboarding_notifications (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        onboarding_id           uniqueidentifier   NULL,
        trigger_code            nvarchar(80)       NOT NULL,
        dedupe_key              nvarchar(200)      NOT NULL,
        channel                 nvarchar(40)       NOT NULL,
        recipient               nvarchar(256)      NULL,
        status                  nvarchar(40)       NOT NULL,
        error_message           nvarchar(1000)     NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_onboarding_notifications PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_ob_notif_dedupe UNIQUE (organization_id, dedupe_key)
    );
END;

-- Soft-extend ATS tasks for optional onboarding linkage (non-breaking).
IF COL_LENGTH('dbo.tasks', 'source_type') IS NULL
BEGIN
    ALTER TABLE dbo.tasks ADD source_type nvarchar(40) NULL;
END;
IF COL_LENGTH('dbo.tasks', 'source_id') IS NULL
BEGIN
    ALTER TABLE dbo.tasks ADD source_id uniqueidentifier NULL;
END;

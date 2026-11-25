-- =============================================================================
-- NextHire — Core HR Offboarding schema
-- File: sql/011-core-hr-offboarding-schema.sql
-- Mirrors onboarding patterns; separation-specific entities for assets/access/exit.
-- Idempotent DDL. Tenant: organization_id.
-- =============================================================================

IF OBJECT_ID(N'dbo.nh_hr_offboarding_templates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_templates (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        name                    nvarchar(200)      NOT NULL,
        description             nvarchar(2000)     NULL,
        separation_type         nvarchar(80)       NULL,
        department_id           uniqueidentifier   NULL,
        job_title_id            uniqueidentifier   NULL,
        employment_type_id      uniqueidentifier   NULL,
        estimated_duration_days int                NULL,
        is_active               bit                NOT NULL CONSTRAINT DF_nh_hr_off_tpl_active DEFAULT (1),
        is_default              bit                NOT NULL CONSTRAINT DF_nh_hr_off_tpl_default DEFAULT (0),
        version                 int                NOT NULL CONSTRAINT DF_nh_hr_off_tpl_version DEFAULT (1),
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        archived_by             uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_offboarding_templates PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_nh_hr_off_tpl_version CHECK (version > 0)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_tpl_org
        ON dbo.nh_hr_offboarding_templates (organization_id, is_active)
        WHERE archived_at IS NULL;
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_template_tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_template_tasks (
        id                       uniqueidentifier   NOT NULL,
        organization_id          uniqueidentifier   NOT NULL,
        template_id              uniqueidentifier   NOT NULL,
        title                    nvarchar(300)      NOT NULL,
        description              nvarchar(2000)     NULL,
        category                 nvarchar(80)       NOT NULL,
        display_order            int                NOT NULL CONSTRAINT DF_nh_hr_off_tt_order DEFAULT (0),
        assignment_type          nvarchar(40)       NOT NULL,
        default_assignee_user_id uniqueidentifier   NULL,
        due_offset_days          int                NULL,
        is_required              bit                NOT NULL CONSTRAINT DF_nh_hr_off_tt_req DEFAULT (1),
        requires_document        bit                NOT NULL CONSTRAINT DF_nh_hr_off_tt_doc DEFAULT (0),
        document_category        nvarchar(80)       NULL,
        requires_approval        bit                NOT NULL CONSTRAINT DF_nh_hr_off_tt_appr DEFAULT (0),
        depends_on_task_id       uniqueidentifier   NULL,
        created_at               datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_offboarding_template_tasks PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_tt_template
            FOREIGN KEY (template_id) REFERENCES dbo.nh_hr_offboarding_templates (id),
        CONSTRAINT CK_nh_hr_off_tt_assign CHECK (assignment_type IN (
            'Employee', 'DirectManager', 'HR', 'IT', 'Finance', 'SpecificUser'
        )),
        CONSTRAINT CK_nh_hr_off_tt_category CHECK (category IN (
            'HRAdministration', 'Documentation', 'KnowledgeTransfer', 'ITSecurity',
            'EquipmentAssets', 'FinancePayroll', 'Compliance', 'ExitInterview',
            'ManagementApproval', 'Other'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_tt_template
        ON dbo.nh_hr_offboarding_template_tasks (organization_id, template_id, display_order);
END;

IF OBJECT_ID(N'dbo.nh_hr_employee_offboardings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_offboardings (
        id                          uniqueidentifier   NOT NULL,
        organization_id             uniqueidentifier   NOT NULL,
        employee_id                 uniqueidentifier   NOT NULL,
        template_id                 uniqueidentifier   NULL,
        template_version            int                NULL,
        owner_user_id               uniqueidentifier   NULL,
        separation_type             nvarchar(80)       NOT NULL,
        separation_reason           nvarchar(1000)     NULL,
        notice_date                 date               NULL,
        process_start_date          date               NULL,
        last_working_day            date               NULL,
        termination_effective_date  date               NULL,
        expected_completion_date    date               NULL,
        actual_completion_date      date               NULL,
        rehire_eligibility          nvarchar(40)       NULL,
        confidential_notes          nvarchar(max)      NULL,
        notify_employee             bit                NOT NULL CONSTRAINT DF_nh_hr_off_notify_emp DEFAULT (0),
        status                      nvarchar(40)       NOT NULL,
        cancel_reason               nvarchar(1000)     NULL,
        employment_status_applied   bit                NOT NULL CONSTRAINT DF_nh_hr_off_emp_applied DEFAULT (0),
        created_at                  datetimeoffset(7)  NOT NULL,
        created_by                  uniqueidentifier   NULL,
        updated_at                  datetimeoffset(7)  NULL,
        updated_by                  uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_offboardings PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT FK_nh_hr_off_template
            FOREIGN KEY (template_id) REFERENCES dbo.nh_hr_offboarding_templates (id),
        CONSTRAINT CK_nh_hr_off_status CHECK (status IN (
            'Scheduled', 'InProgress', 'PendingClearance', 'Completed', 'Cancelled'
        )),
        CONSTRAINT CK_nh_hr_off_sep_type CHECK (separation_type IN (
            'VoluntaryResignation', 'InvoluntaryTermination', 'ContractExpiration',
            'Retirement', 'Layoff', 'MutualAgreement', 'Other'
        )),
        CONSTRAINT CK_nh_hr_off_rehire CHECK (
            rehire_eligibility IS NULL OR rehire_eligibility IN ('Eligible', 'NotEligible', 'Conditional', 'Unknown')
        )
    );

    CREATE UNIQUE NONCLUSTERED INDEX UQ_nh_hr_off_active_employee
        ON dbo.nh_hr_employee_offboardings (organization_id, employee_id)
        WHERE status IN ('Scheduled', 'InProgress', 'PendingClearance');

    CREATE NONCLUSTERED INDEX IX_nh_hr_off_org_status
        ON dbo.nh_hr_employee_offboardings (organization_id, status, last_working_day);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_tasks (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NOT NULL,
        template_task_id    uniqueidentifier   NULL,
        title               nvarchar(300)      NOT NULL,
        description         nvarchar(2000)     NULL,
        category            nvarchar(80)       NOT NULL,
        assignment_type     nvarchar(40)       NOT NULL,
        assigned_user_id    uniqueidentifier   NULL,
        due_date            date               NULL,
        status              nvarchar(40)       NOT NULL,
        priority            nvarchar(20)       NULL,
        is_required         bit                NOT NULL CONSTRAINT DF_nh_hr_off_task_req DEFAULT (1),
        requires_document   bit                NOT NULL CONSTRAINT DF_nh_hr_off_task_doc DEFAULT (0),
        document_category   nvarchar(80)       NULL,
        requires_approval   bit                NOT NULL CONSTRAINT DF_nh_hr_off_task_appr DEFAULT (0),
        approval_status     nvarchar(40)       NULL,
        approved_by         uniqueidentifier   NULL,
        approved_at         datetimeoffset(7)  NULL,
        completed_at        datetimeoffset(7)  NULL,
        completed_by        uniqueidentifier   NULL,
        completion_notes    nvarchar(2000)     NULL,
        evidence_url        nvarchar(1000)     NULL,
        display_order       int                NOT NULL CONSTRAINT DF_nh_hr_off_task_order DEFAULT (0),
        depends_on_task_id  uniqueidentifier   NULL,
        ats_task_id         uniqueidentifier   NULL,
        is_waived           bit                NOT NULL CONSTRAINT DF_nh_hr_off_task_waived DEFAULT (0),
        waive_reason        nvarchar(1000)     NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        updated_at          datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_offboarding_tasks PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_task_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_task_status CHECK (status IN (
            'Pending', 'InProgress', 'Completed', 'Cancelled', 'Blocked'
        )),
        CONSTRAINT CK_nh_hr_off_task_approval CHECK (
            approval_status IS NULL OR approval_status IN ('Pending', 'Approved', 'Rejected')
        )
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_tasks_parent
        ON dbo.nh_hr_offboarding_tasks (organization_id, offboarding_id, display_order);
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_tasks_assignee
        ON dbo.nh_hr_offboarding_tasks (organization_id, assigned_user_id, due_date)
        WHERE status IN ('Pending', 'InProgress', 'Blocked');
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_assets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_assets (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NOT NULL,
        asset_name          nvarchar(200)      NOT NULL,
        asset_type          nvarchar(80)       NULL,
        serial_number       nvarchar(120)      NULL,
        return_required     bit                NOT NULL CONSTRAINT DF_nh_hr_off_asset_req DEFAULT (1),
        expected_return_date date              NULL,
        actual_return_date  date               NULL,
        condition_notes     nvarchar(500)      NULL,
        received_by         uniqueidentifier   NULL,
        status              nvarchar(40)       NOT NULL,
        notes               nvarchar(1000)     NULL,
        evidence_url        nvarchar(1000)     NULL,
        waive_reason        nvarchar(1000)     NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        updated_at          datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_offboarding_assets PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_asset_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_asset_status CHECK (status IN (
            'PendingReturn', 'Returned', 'Damaged', 'Missing', 'Waived'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_assets
        ON dbo.nh_hr_offboarding_assets (organization_id, offboarding_id);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_access_items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_access_items (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        offboarding_id          uniqueidentifier   NOT NULL,
        system_name             nvarchar(200)      NOT NULL,
        responsible_user_id     uniqueidentifier   NULL,
        scheduled_at            datetimeoffset(7)  NULL,
        revoked_at              datetimeoffset(7)  NULL,
        status                  nvarchar(40)       NOT NULL,
        evidence_url            nvarchar(1000)     NULL,
        verified_by             uniqueidentifier   NULL,
        verified_at             datetimeoffset(7)  NULL,
        notes                   nvarchar(1000)     NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        updated_at              datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_offboarding_access_items PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_access_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_access_status CHECK (status IN (
            'Pending', 'Scheduled', 'Revoked', 'Verified', 'NotApplicable'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_access
        ON dbo.nh_hr_offboarding_access_items (organization_id, offboarding_id);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_settlement_items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_settlement_items (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NOT NULL,
        item_code           nvarchar(80)       NOT NULL,
        title               nvarchar(300)      NOT NULL,
        is_required         bit                NOT NULL CONSTRAINT DF_nh_hr_off_set_req DEFAULT (1),
        status              nvarchar(40)       NOT NULL,
        amount              decimal(18,2)      NULL,
        currency            nvarchar(10)       NULL,
        notes               nvarchar(1000)     NULL,
        evidence_url        nvarchar(1000)     NULL,
        completed_at        datetimeoffset(7)  NULL,
        completed_by        uniqueidentifier   NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_offboarding_settlement_items PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_set_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_set_status CHECK (status IN (
            'Pending', 'Completed', 'NotApplicable', 'Waived'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_settlement
        ON dbo.nh_hr_offboarding_settlement_items (organization_id, offboarding_id);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_exit_interviews', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_exit_interviews (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NOT NULL,
        interviewer_user_id uniqueidentifier   NULL,
        scheduled_at        datetimeoffset(7)  NULL,
        status              nvarchar(40)       NOT NULL,
        questionnaire_json  nvarchar(max)      NULL,
        responses_json      nvarchar(max)      NULL,
        hr_notes            nvarchar(max)      NULL,
        completed_at        datetimeoffset(7)  NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        updated_at          datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_offboarding_exit_interviews PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_exit_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT UQ_nh_hr_off_exit_one UNIQUE (organization_id, offboarding_id),
        CONSTRAINT CK_nh_hr_off_exit_status CHECK (status IN (
            'NotScheduled', 'Scheduled', 'InProgress', 'Completed', 'Skipped'
        )),
        CONSTRAINT CK_nh_hr_off_exit_q
            CHECK (questionnaire_json IS NULL OR ISJSON(questionnaire_json) = 1),
        CONSTRAINT CK_nh_hr_off_exit_r
            CHECK (responses_json IS NULL OR ISJSON(responses_json) = 1)
    );
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_documents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_documents (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        offboarding_id          uniqueidentifier   NOT NULL,
        offboarding_task_id     uniqueidentifier   NULL,
        employee_document_id    uniqueidentifier   NULL,
        category                nvarchar(80)       NOT NULL,
        name                    nvarchar(300)      NOT NULL,
        is_required             bit                NOT NULL CONSTRAINT DF_nh_hr_off_doc_req DEFAULT (1),
        status                  nvarchar(40)       NOT NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_offboarding_documents PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_doc_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_doc_status CHECK (status IN (
            'Missing', 'Uploaded', 'PendingApproval', 'Approved', 'Rejected'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_docs
        ON dbo.nh_hr_offboarding_documents (organization_id, offboarding_id);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_activity', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_activity (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NOT NULL,
        activity_type       nvarchar(80)       NOT NULL,
        summary             nvarchar(500)      NOT NULL,
        details_json        nvarchar(max)      NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        created_by_name     nvarchar(200)      NULL,
        CONSTRAINT PK_nh_hr_offboarding_activity PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_off_act_parent
            FOREIGN KEY (offboarding_id) REFERENCES dbo.nh_hr_employee_offboardings (id),
        CONSTRAINT CK_nh_hr_off_act_json
            CHECK (details_json IS NULL OR ISJSON(details_json) = 1)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_off_activity
        ON dbo.nh_hr_offboarding_activity (organization_id, offboarding_id, created_at DESC);
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_notification_settings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_notification_settings (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        trigger_code        nvarchar(80)       NOT NULL,
        is_enabled          bit                NOT NULL CONSTRAINT DF_nh_hr_off_notif_en DEFAULT (1),
        channel_email       bit                NOT NULL CONSTRAINT DF_nh_hr_off_notif_email DEFAULT (1),
        channel_whatsapp    bit                NOT NULL CONSTRAINT DF_nh_hr_off_notif_wa DEFAULT (0),
        days_before         int                NULL,
        recipients_json     nvarchar(max)      NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_offboarding_notification_settings PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_off_notif_org_trigger UNIQUE (organization_id, trigger_code)
    );
END;

IF OBJECT_ID(N'dbo.nh_hr_offboarding_notifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_offboarding_notifications (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        offboarding_id      uniqueidentifier   NULL,
        trigger_code        nvarchar(80)       NOT NULL,
        dedupe_key          nvarchar(200)      NOT NULL,
        channel             nvarchar(40)       NOT NULL,
        recipient           nvarchar(256)      NULL,
        status              nvarchar(40)       NOT NULL,
        error_message       nvarchar(1000)     NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_offboarding_notifications PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_off_notif_dedupe UNIQUE (organization_id, dedupe_key)
    );
END;

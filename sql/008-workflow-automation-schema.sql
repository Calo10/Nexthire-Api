-- =============================================================================
-- LeadSphere — Nexa.WorkflowAutomation persistence schema
-- File: sql/008-workflow-automation-schema.sql
--
-- Idempotent DDL for SQL Server. Safe to run once (or repeatedly): each object
-- is created only when missing.
--
-- Design notes (package Nexa.WorkflowAutomation):
-- - WorkflowDefinition is keyed by Id (GetById / GetPublishedVersion). There is
--   no workflow_family_id. Version is a column on the same row, not a separate
--   identity. Multiple immutable versions of "the same" logical workflow require
--   distinct Ids at the product layer; the package API cannot list versions by
--   family. Therefore uniqueness is PK(id) only — NOT (organization_id, name,
--   version), which would incorrectly block unrelated workflows sharing a name.
-- - Steps/transitions live in column [definition]; variables live inside [context].
--   Column names describe content, not physical format (JSON validated via ISJSON).
-- - WorkflowExecution.Context.Metadata is stored inside [context]; there is no
--   separate executions.metadata column (avoids duplication with the CLR model).
-- - WorkflowEvent payloads are not persisted; only event receipts for idempotency.
-- - Soft delete on definitions only (is_deleted / deleted_at).
-- - No ON DELETE CASCADE. No triggers. No stored procedures.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. nh_wf_definitions
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_definitions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_definitions (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        name                nvarchar(200)      NOT NULL,
        description         nvarchar(2000)     NULL,
        version             int                NOT NULL,
        status              varchar(20)        NOT NULL,
        start_step_id       uniqueidentifier   NOT NULL,
        definition          nvarchar(max)      NOT NULL,
        metadata            nvarchar(max)      NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          nvarchar(200)      NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          nvarchar(200)      NULL,
        published_at        datetimeoffset(7)  NULL,
        archived_at         datetimeoffset(7)  NULL,
        is_deleted          bit                NOT NULL
            CONSTRAINT DF_nh_wf_definitions_is_deleted DEFAULT (0),
        deleted_at          datetimeoffset(7)  NULL,

        CONSTRAINT PK_nh_wf_definitions
            PRIMARY KEY CLUSTERED (id),

        CONSTRAINT CK_nh_wf_definitions_version
            CHECK (version > 0),

        CONSTRAINT CK_nh_wf_definitions_status
            CHECK (status IN ('Draft', 'Published', 'Archived')),

        CONSTRAINT CK_nh_wf_definitions_definition
            CHECK (ISJSON(definition) = 1),

        CONSTRAINT CK_nh_wf_definitions_metadata
            CHECK (metadata IS NULL OR ISJSON(metadata) = 1),

        CONSTRAINT CK_nh_wf_definitions_soft_delete
            CHECK (
                (is_deleted = 0 AND deleted_at IS NULL)
                OR (is_deleted = 1 AND deleted_at IS NOT NULL)
            )
    );

    -- Active definitions by tenant + lifecycle status (excludes soft-deleted).
    CREATE NONCLUSTERED INDEX IX_nh_wf_definitions_organization_id_status
        ON dbo.nh_wf_definitions (organization_id, status)
        WHERE is_deleted = 0;

    CREATE NONCLUSTERED INDEX IX_nh_wf_definitions_organization_id_name
        ON dbo.nh_wf_definitions (organization_id, name)
        WHERE is_deleted = 0;

    CREATE NONCLUSTERED INDEX IX_nh_wf_definitions_organization_id_published_at
        ON dbo.nh_wf_definitions (organization_id, published_at)
        WHERE is_deleted = 0 AND status = 'Published';
END;

-- -----------------------------------------------------------------------------
-- 2. nh_wf_executions
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_executions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_executions (
        id                          uniqueidentifier   NOT NULL,
        organization_id             uniqueidentifier   NOT NULL,
        workflow_definition_id      uniqueidentifier   NOT NULL,
        workflow_version            int                NOT NULL,
        entity_type                 nvarchar(100)      NOT NULL,
        entity_id                   nvarchar(200)      NOT NULL,
        status                      varchar(20)        NOT NULL,
        current_step_id             uniqueidentifier   NULL,
        current_step_execution_id   uniqueidentifier   NULL,
        conversation_id             nvarchar(200)      NULL,
        context                     nvarchar(max)      NOT NULL,
        created_at                  datetimeoffset(7)  NOT NULL,
        updated_at                  datetimeoffset(7)  NULL,
        started_at                  datetimeoffset(7)  NULL,
        waiting_since               datetimeoffset(7)  NULL,
        resume_at                   datetimeoffset(7)  NULL,
        completed_at                datetimeoffset(7)  NULL,
        cancelled_at                datetimeoffset(7)  NULL,
        failed_at                   datetimeoffset(7)  NULL,
        failure_reason              nvarchar(4000)     NULL,
        revision                    int                NOT NULL,

        CONSTRAINT PK_nh_wf_executions
            PRIMARY KEY CLUSTERED (id),

        CONSTRAINT FK_nh_wf_executions_workflow_definition_id
            FOREIGN KEY (workflow_definition_id)
            REFERENCES dbo.nh_wf_definitions (id),

        CONSTRAINT CK_nh_wf_executions_revision
            CHECK (revision >= 0),

        CONSTRAINT CK_nh_wf_executions_workflow_version
            CHECK (workflow_version > 0),

        CONSTRAINT CK_nh_wf_executions_status
            CHECK (status IN (
                'Pending', 'Running', 'Waiting', 'Paused',
                'Completed', 'Cancelled', 'Failed'
            )),

        CONSTRAINT CK_nh_wf_executions_context
            CHECK (ISJSON(context) = 1)
    );

    -- Tenant-scoped lookup (supports GetByIdAsync with organization_id).
    CREATE UNIQUE NONCLUSTERED INDEX UQ_nh_wf_executions_organization_id_id
        ON dbo.nh_wf_executions (organization_id, id);

    CREATE NONCLUSTERED INDEX IX_nh_wf_executions_organization_id_workflow_definition_id_status
        ON dbo.nh_wf_executions (organization_id, workflow_definition_id, status);

    CREATE NONCLUSTERED INDEX IX_nh_wf_executions_organization_id_entity_type_entity_id_status
        ON dbo.nh_wf_executions (organization_id, entity_type, entity_id, status);

    CREATE NONCLUSTERED INDEX IX_nh_wf_executions_organization_id_conversation_id
        ON dbo.nh_wf_executions (organization_id, conversation_id)
        WHERE conversation_id IS NOT NULL;

    -- Resolve current step attempt from the execution row (GetCurrentAsync path).
    CREATE NONCLUSTERED INDEX IX_nh_wf_executions_organization_id_current_step_execution_id
        ON dbo.nh_wf_executions (organization_id, current_step_execution_id)
        WHERE current_step_execution_id IS NOT NULL;

    -- Hot path: due / timeout worker
    -- WHERE organization_id = @OrganizationId AND status = 'Waiting' AND resume_at <= @UtcNow
    -- Replaces a broader (organization_id, status, resume_at) index that mostly
    -- overlapped this filtered set for the only status that uses resume_at.
    CREATE NONCLUSTERED INDEX IX_nh_wf_executions_organization_id_waiting_resume_at
        ON dbo.nh_wf_executions (organization_id, resume_at)
        WHERE status = 'Waiting' AND resume_at IS NOT NULL;
END;

-- -----------------------------------------------------------------------------
-- 3. nh_wf_step_executions
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_step_executions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_step_executions (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        workflow_execution_id   uniqueidentifier   NOT NULL,
        step_definition_id      uniqueidentifier   NOT NULL,
        activity_type           nvarchar(200)      NOT NULL,
        status                  varchar(20)        NOT NULL,
        attempt                 int                NOT NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        updated_at              datetimeoffset(7)  NULL,
        started_at              datetimeoffset(7)  NULL,
        waiting_since           datetimeoffset(7)  NULL,
        resume_at               datetimeoffset(7)  NULL,
        completed_at            datetimeoffset(7)  NULL,
        waiting_event_type      nvarchar(200)      NULL,
        correlation_id          nvarchar(500)      NULL,
        external_reference_id   nvarchar(500)      NULL,
        input                   nvarchar(max)      NULL,
        output                  nvarchar(max)      NULL,
        metadata                nvarchar(max)      NULL,
        error_code              nvarchar(200)      NULL,
        error_message           nvarchar(4000)     NULL,

        CONSTRAINT PK_nh_wf_step_executions
            PRIMARY KEY CLUSTERED (id),

        CONSTRAINT FK_nh_wf_step_executions_workflow_execution_id
            FOREIGN KEY (workflow_execution_id)
            REFERENCES dbo.nh_wf_executions (id),

        CONSTRAINT CK_nh_wf_step_executions_attempt
            CHECK (attempt > 0),

        CONSTRAINT CK_nh_wf_step_executions_status
            CHECK (status IN (
                'Pending', 'Running', 'Waiting', 'Succeeded',
                'Failed', 'Skipped', 'TimedOut', 'Cancelled'
            )),

        CONSTRAINT CK_nh_wf_step_executions_input
            CHECK (input IS NULL OR ISJSON(input) = 1),

        CONSTRAINT CK_nh_wf_step_executions_output
            CHECK (output IS NULL OR ISJSON(output) = 1),

        CONSTRAINT CK_nh_wf_step_executions_metadata
            CHECK (metadata IS NULL OR ISJSON(metadata) = 1)
    );

    -- Attempt timeline / GetCurrent fallback ordering per execution.
    CREATE NONCLUSTERED INDEX IX_nh_wf_step_executions_organization_id_workflow_execution_id_created_at
        ON dbo.nh_wf_step_executions (organization_id, workflow_execution_id, created_at)
        INCLUDE (status, attempt);

    -- Hot path: event matching while Waiting
    -- WHERE organization_id = @OrganizationId AND status = 'Waiting'
    --   AND waiting_event_type = @EventType AND correlation_id = @CorrelationId
    -- Replaces the non-filtered (…, status) variant that duplicated this key set.
    CREATE NONCLUSTERED INDEX IX_nh_wf_step_executions_organization_id_waiting_event_match
        ON dbo.nh_wf_step_executions (organization_id, waiting_event_type, correlation_id, resume_at)
        WHERE status = 'Waiting';

    CREATE NONCLUSTERED INDEX IX_nh_wf_step_executions_external_reference_id
        ON dbo.nh_wf_step_executions (external_reference_id)
        WHERE external_reference_id IS NOT NULL;
END;

-- -----------------------------------------------------------------------------
-- 4. nh_wf_history (append-only — no updated_at / updated_by)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_history', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_history (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        workflow_execution_id   uniqueidentifier   NOT NULL,
        event_type              varchar(50)        NOT NULL,
        occurred_at             datetimeoffset(7)  NOT NULL,
        message                 nvarchar(2000)     NOT NULL,
        data                    nvarchar(max)      NULL,

        CONSTRAINT PK_nh_wf_history
            PRIMARY KEY CLUSTERED (id),

        CONSTRAINT FK_nh_wf_history_workflow_execution_id
            FOREIGN KEY (workflow_execution_id)
            REFERENCES dbo.nh_wf_executions (id),

        CONSTRAINT CK_nh_wf_history_event_type
            CHECK (event_type IN (
                'WorkflowStarted',
                'StepStarted',
                'StepCompleted',
                'StepWaiting',
                'StepFailed',
                'StepTimedOut',
                'WorkflowPaused',
                'WorkflowResumed',
                'WorkflowCompleted',
                'WorkflowCancelled',
                'WorkflowFailed',
                'ExternalEventReceived',
                'VariableUpdated',
                'WorkflowRetryRequested'
            )),

        CONSTRAINT CK_nh_wf_history_data
            CHECK (data IS NULL OR ISJSON(data) = 1)
    );

    CREATE NONCLUSTERED INDEX IX_nh_wf_history_organization_id_workflow_execution_id_occurred_at
        ON dbo.nh_wf_history (organization_id, workflow_execution_id, occurred_at);

    -- Ops / analytics by event type; secondary to the execution timeline index.
    CREATE NONCLUSTERED INDEX IX_nh_wf_history_organization_id_event_type_occurred_at
        ON dbo.nh_wf_history (organization_id, event_type, occurred_at);
END;

-- -----------------------------------------------------------------------------
-- 5. nh_wf_event_receipts (idempotency; append-ish — no updated_at)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_event_receipts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_event_receipts (
        organization_id         uniqueidentifier   NOT NULL,
        event_id                uniqueidentifier   NOT NULL,
        workflow_execution_id   uniqueidentifier   NULL,
        event_type              nvarchar(200)      NOT NULL,
        correlation_id          nvarchar(500)      NULL,
        processed_at            datetimeoffset(7)  NOT NULL,

        CONSTRAINT PK_nh_wf_event_receipts
            PRIMARY KEY CLUSTERED (organization_id, event_id),

        CONSTRAINT FK_nh_wf_event_receipts_workflow_execution_id
            FOREIGN KEY (workflow_execution_id)
            REFERENCES dbo.nh_wf_executions (id)
    );

    CREATE NONCLUSTERED INDEX IX_nh_wf_event_receipts_workflow_execution_id_processed_at
        ON dbo.nh_wf_event_receipts (workflow_execution_id, processed_at);

    CREATE NONCLUSTERED INDEX IX_nh_wf_event_receipts_organization_id_event_type_correlation_id
        ON dbo.nh_wf_event_receipts (organization_id, event_type, correlation_id);

    -- Retention / purge jobs.
    CREATE NONCLUSTERED INDEX IX_nh_wf_event_receipts_processed_at
        ON dbo.nh_wf_event_receipts (processed_at);
END;

-- -----------------------------------------------------------------------------
-- 6. nh_wf_interview_sessions (askQuestions interview state — not Messenger history)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_interview_sessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_wf_interview_sessions (
        conversation_id         nvarchar(200)      NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        workflow_execution_id   uniqueidentifier   NOT NULL,
        channel                 nvarchar(50)       NOT NULL,
        recipient               nvarchar(320)      NOT NULL,
        question_index          int                NOT NULL,
        answers                 nvarchar(max)      NOT NULL,
        questions               nvarchar(max)      NOT NULL,
        completion_event_type   nvarchar(200)      NOT NULL,
        status                  varchar(30)        NOT NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        updated_at              datetimeoffset(7)  NOT NULL,

        CONSTRAINT PK_nh_wf_interview_sessions
            PRIMARY KEY CLUSTERED (conversation_id),

        CONSTRAINT FK_nh_wf_interview_sessions_workflow_execution_id
            FOREIGN KEY (workflow_execution_id)
            REFERENCES dbo.nh_wf_executions (id),

        CONSTRAINT CK_nh_wf_interview_sessions_question_index
            CHECK (question_index >= 0),

        CONSTRAINT CK_nh_wf_interview_sessions_status
            CHECK (status IN ('Active', 'Completed', 'Cancelled')),

        CONSTRAINT CK_nh_wf_interview_sessions_answers
            CHECK (ISJSON(answers) = 1),

        CONSTRAINT CK_nh_wf_interview_sessions_questions
            CHECK (ISJSON(questions) = 1)
    );

    CREATE NONCLUSTERED INDEX IX_nh_wf_interview_sessions_organization_id_recipient_status
        ON dbo.nh_wf_interview_sessions (organization_id, recipient, status);

    CREATE NONCLUSTERED INDEX IX_nh_wf_interview_sessions_workflow_execution_id
        ON dbo.nh_wf_interview_sessions (workflow_execution_id);
END;

-- -----------------------------------------------------------------------------
-- Upgrade: multi-tenant WhatsApp wait lookup (OrganizationId + Phone/correlation)
-- Hot path for FindWaitingByOrganizationAndPhoneAsync when eventType is NULL.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_step_executions', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_nh_wf_step_executions_organization_id_correlation_waiting'
          AND object_id = OBJECT_ID(N'dbo.nh_wf_step_executions'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_nh_wf_step_executions_organization_id_correlation_waiting
        ON dbo.nh_wf_step_executions (organization_id, correlation_id)
        INCLUDE (waiting_event_type, resume_at)
        WHERE status = 'Waiting' AND correlation_id IS NOT NULL;
END;

-- -----------------------------------------------------------------------------
-- Upgrade: allow WorkflowRetryRequested in history event_type check.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_wf_history', N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_nh_wf_history_event_type'
          AND parent_object_id = OBJECT_ID(N'dbo.nh_wf_history'))
    BEGIN
        ALTER TABLE dbo.nh_wf_history DROP CONSTRAINT CK_nh_wf_history_event_type;
    END;

    ALTER TABLE dbo.nh_wf_history ADD CONSTRAINT CK_nh_wf_history_event_type
        CHECK (event_type IN (
            'WorkflowStarted',
            'StepStarted',
            'StepCompleted',
            'StepWaiting',
            'StepFailed',
            'StepTimedOut',
            'WorkflowPaused',
            'WorkflowResumed',
            'WorkflowCompleted',
            'WorkflowCancelled',
            'WorkflowFailed',
            'ExternalEventReceived',
            'VariableUpdated',
            'WorkflowRetryRequested'
        ));
END;

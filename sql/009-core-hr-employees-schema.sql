-- =============================================================================
-- NextHire — Core HR Employees schema
-- File: sql/009-core-hr-employees-schema.sql
-- Idempotent DDL for SQL Server. Tenant column: organization_id.
-- Soft archive via archived_at / archived_by (employees remain queryable when requested).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Lookups
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_departments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_departments (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        name                nvarchar(200)      NOT NULL,
        code                nvarchar(50)       NULL,
        is_active           bit                NOT NULL CONSTRAINT DF_nh_hr_departments_is_active DEFAULT (1),
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_departments PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_departments_org_name UNIQUE (organization_id, name)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_departments_org
        ON dbo.nh_hr_departments (organization_id) WHERE is_active = 1;
END;

IF OBJECT_ID(N'dbo.nh_hr_job_titles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_job_titles (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        name                nvarchar(200)      NOT NULL,
        is_active           bit                NOT NULL CONSTRAINT DF_nh_hr_job_titles_is_active DEFAULT (1),
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_job_titles PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_job_titles_org_name UNIQUE (organization_id, name)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_job_titles_org
        ON dbo.nh_hr_job_titles (organization_id) WHERE is_active = 1;
END;

IF OBJECT_ID(N'dbo.nh_hr_work_locations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_work_locations (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        name                nvarchar(200)      NOT NULL,
        city                nvarchar(120)      NULL,
        state               nvarchar(120)      NULL,
        country             nvarchar(120)      NULL,
        is_active           bit                NOT NULL CONSTRAINT DF_nh_hr_work_locations_is_active DEFAULT (1),
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_work_locations PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_work_locations_org_name UNIQUE (organization_id, name)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_work_locations_org
        ON dbo.nh_hr_work_locations (organization_id) WHERE is_active = 1;
END;

IF OBJECT_ID(N'dbo.nh_hr_employment_types', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employment_types (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        code                nvarchar(40)       NOT NULL,
        name                nvarchar(120)      NOT NULL,
        is_active           bit                NOT NULL CONSTRAINT DF_nh_hr_employment_types_is_active DEFAULT (1),
        sort_order          int                NOT NULL CONSTRAINT DF_nh_hr_employment_types_sort DEFAULT (0),
        created_at          datetimeoffset(7)  NOT NULL,
        CONSTRAINT PK_nh_hr_employment_types PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_employment_types_org_code UNIQUE (organization_id, code)
    );
END;

-- -----------------------------------------------------------------------------
-- Employees
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employees', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employees (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        employee_number         nvarchar(50)       NOT NULL,
        first_name              nvarchar(100)      NOT NULL,
        middle_name             nvarchar(100)      NULL,
        last_name               nvarchar(100)      NOT NULL,
        preferred_name          nvarchar(100)      NULL,
        profile_picture_url     nvarchar(1000)     NULL,
        date_of_birth           date               NULL,
        nationality             nvarchar(100)      NULL,
        personal_email          nvarchar(256)      NULL,
        personal_phone          nvarchar(50)       NULL,
        address_line1           nvarchar(300)      NULL,
        address_line2           nvarchar(300)      NULL,
        city                    nvarchar(120)      NULL,
        state                   nvarchar(120)      NULL,
        country                 nvarchar(120)      NULL,
        postal_code             nvarchar(40)       NULL,
        work_email              nvarchar(256)      NULL,
        work_phone              nvarchar(50)       NULL,
        job_title_id            uniqueidentifier   NULL,
        department_id           uniqueidentifier   NULL,
        manager_id              uniqueidentifier   NULL,
        employment_type_id      uniqueidentifier   NULL,
        employment_status       nvarchar(40)       NOT NULL,
        hire_date               date               NULL,
        termination_date        date               NULL,
        termination_reason      nvarchar(500)      NULL,
        work_location_id        uniqueidentifier   NULL,
        work_modality           nvarchar(40)       NULL,
        probation_end_date      date               NULL,
        contract_start_date     date               NULL,
        contract_end_date       date               NULL,
        candidate_id            uniqueidentifier   NULL,
        application_id          uniqueidentifier   NULL,
        linked_user_id          uniqueidentifier   NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        archived_by             uniqueidentifier   NULL,

        CONSTRAINT PK_nh_hr_employees PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_employees_org_number UNIQUE (organization_id, employee_number),
        CONSTRAINT CK_nh_hr_employees_status CHECK (employment_status IN (
            'Pending', 'Onboarding', 'Active', 'OnLeave', 'Inactive', 'Terminated'
        )),
        CONSTRAINT CK_nh_hr_employees_modality CHECK (
            work_modality IS NULL OR work_modality IN ('OnSite', 'Remote', 'Hybrid')
        ),
        CONSTRAINT CK_nh_hr_employees_not_self_manager CHECK (
            manager_id IS NULL OR manager_id <> id
        ),
        CONSTRAINT FK_nh_hr_employees_department
            FOREIGN KEY (department_id) REFERENCES dbo.nh_hr_departments (id),
        CONSTRAINT FK_nh_hr_employees_job_title
            FOREIGN KEY (job_title_id) REFERENCES dbo.nh_hr_job_titles (id),
        CONSTRAINT FK_nh_hr_employees_location
            FOREIGN KEY (work_location_id) REFERENCES dbo.nh_hr_work_locations (id),
        CONSTRAINT FK_nh_hr_employees_employment_type
            FOREIGN KEY (employment_type_id) REFERENCES dbo.nh_hr_employment_types (id),
        CONSTRAINT FK_nh_hr_employees_manager
            FOREIGN KEY (manager_id) REFERENCES dbo.nh_hr_employees (id)
    );

    CREATE NONCLUSTERED INDEX IX_nh_hr_employees_org_status
        ON dbo.nh_hr_employees (organization_id, employment_status)
        WHERE archived_at IS NULL;

    CREATE NONCLUSTERED INDEX IX_nh_hr_employees_org_dept
        ON dbo.nh_hr_employees (organization_id, department_id)
        WHERE archived_at IS NULL;

    CREATE NONCLUSTERED INDEX IX_nh_hr_employees_org_search
        ON dbo.nh_hr_employees (organization_id, last_name, first_name)
        WHERE archived_at IS NULL;

    CREATE NONCLUSTERED INDEX IX_nh_hr_employees_org_hire
        ON dbo.nh_hr_employees (organization_id, hire_date)
        WHERE archived_at IS NULL;

    CREATE UNIQUE NONCLUSTERED INDEX UQ_nh_hr_employees_org_candidate
        ON dbo.nh_hr_employees (organization_id, candidate_id)
        WHERE candidate_id IS NOT NULL AND archived_at IS NULL;
END;

-- -----------------------------------------------------------------------------
-- Emergency contacts
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_emergency_contacts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_emergency_contacts (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        full_name           nvarchar(200)      NOT NULL,
        relationship        nvarchar(100)      NULL,
        phone               nvarchar(50)       NULL,
        email               nvarchar(256)      NULL,
        is_primary          bit                NOT NULL CONSTRAINT DF_nh_hr_emergency_is_primary DEFAULT (0),
        created_at          datetimeoffset(7)  NOT NULL,
        updated_at          datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_employee_emergency_contacts PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_emergency_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_emergency_employee
        ON dbo.nh_hr_employee_emergency_contacts (organization_id, employee_id);
END;

-- -----------------------------------------------------------------------------
-- Documents
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_documents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_documents (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        category            nvarchar(80)       NOT NULL,
        name                nvarchar(300)      NOT NULL,
        document_id         uniqueidentifier   NULL,
        external_url        nvarchar(1000)     NULL,
        status              nvarchar(40)       NOT NULL CONSTRAINT DF_nh_hr_emp_docs_status DEFAULT ('Active'),
        expires_at          date               NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        archived_at         datetimeoffset(7)  NULL,
        archived_by         uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_documents PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_emp_docs_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT CK_nh_hr_emp_docs_status CHECK (status IN ('Active', 'Expired', 'Archived', 'Pending'))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_docs_employee
        ON dbo.nh_hr_employee_documents (organization_id, employee_id)
        WHERE archived_at IS NULL;
END;

-- -----------------------------------------------------------------------------
-- Employment history
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_employment_history', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_employment_history (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        effective_at        datetimeoffset(7)  NOT NULL,
        change_type         nvarchar(80)       NOT NULL,
        job_title_id        uniqueidentifier   NULL,
        department_id       uniqueidentifier   NULL,
        employment_type_id  uniqueidentifier   NULL,
        employment_status   nvarchar(40)       NULL,
        work_location_id    uniqueidentifier   NULL,
        manager_id          uniqueidentifier   NULL,
        notes               nvarchar(1000)     NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_employment_history PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_emp_hist_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_hist_employee
        ON dbo.nh_hr_employee_employment_history (organization_id, employee_id, effective_at DESC);
END;

-- -----------------------------------------------------------------------------
-- Activity / audit
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_activity', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_activity (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        activity_type       nvarchar(80)       NOT NULL,
        summary             nvarchar(500)      NOT NULL,
        details_json        nvarchar(max)      NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        created_by_name     nvarchar(200)      NULL,
        CONSTRAINT PK_nh_hr_employee_activity PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_emp_activity_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT CK_nh_hr_emp_activity_json
            CHECK (details_json IS NULL OR ISJSON(details_json) = 1)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_activity_employee
        ON dbo.nh_hr_employee_activity (organization_id, employee_id, created_at DESC);
END;

-- -----------------------------------------------------------------------------
-- Compensation history (sensitive)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_compensation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_compensation (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        amount              decimal(18,2)      NOT NULL,
        currency            nvarchar(10)       NOT NULL,
        pay_frequency       nvarchar(40)       NOT NULL,
        effective_date      date               NOT NULL,
        end_date            date               NULL,
        notes               nvarchar(500)      NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_compensation PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_emp_comp_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT CK_nh_hr_emp_comp_frequency CHECK (pay_frequency IN (
            'Hourly', 'Weekly', 'BiWeekly', 'SemiMonthly', 'Monthly', 'Annual'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_comp_employee
        ON dbo.nh_hr_employee_compensation (organization_id, employee_id, effective_date DESC);
END;

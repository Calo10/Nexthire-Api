-- =============================================================================
-- NextHire — Core HR Organization Management
-- Extends departments / job titles / locations; adds levels, positions,
-- assignments, and organizational activity. Idempotent. Soft archive only.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Extend nh_hr_departments (hierarchy + presentation)
-- -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.nh_hr_departments', 'description') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD description nvarchar(1000) NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'parent_department_id') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD parent_department_id uniqueidentifier NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'head_employee_id') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD head_employee_id uniqueidentifier NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'cost_center') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD cost_center nvarchar(80) NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'color') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD color nvarchar(20) NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'icon') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD icon nvarchar(40) NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'display_order') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD display_order int NOT NULL
        CONSTRAINT DF_nh_hr_departments_display_order DEFAULT (0);

IF COL_LENGTH('dbo.nh_hr_departments', 'archived_at') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD archived_at datetimeoffset(7) NULL;

IF COL_LENGTH('dbo.nh_hr_departments', 'archived_by') IS NULL
    ALTER TABLE dbo.nh_hr_departments ADD archived_by uniqueidentifier NULL;

-- Dynamic SQL so ALTER TABLE column adds are visible before FK/index compile.
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_nh_hr_departments_parent'
)
BEGIN
    EXEC(N'ALTER TABLE dbo.nh_hr_departments WITH NOCHECK
        ADD CONSTRAINT FK_nh_hr_departments_parent
        FOREIGN KEY (parent_department_id) REFERENCES dbo.nh_hr_departments (id)');
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_nh_hr_departments_org_parent'
      AND object_id = OBJECT_ID(N'dbo.nh_hr_departments')
)
BEGIN
    EXEC(N'CREATE NONCLUSTERED INDEX IX_nh_hr_departments_org_parent
        ON dbo.nh_hr_departments (organization_id, parent_department_id, display_order)
        WHERE archived_at IS NULL AND is_active = 1');
END;

-- -----------------------------------------------------------------------------
-- Extend nh_hr_job_titles
-- -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.nh_hr_job_titles', 'code') IS NULL
    ALTER TABLE dbo.nh_hr_job_titles ADD code nvarchar(50) NULL;

IF COL_LENGTH('dbo.nh_hr_job_titles', 'description') IS NULL
    ALTER TABLE dbo.nh_hr_job_titles ADD description nvarchar(1000) NULL;

IF COL_LENGTH('dbo.nh_hr_job_titles', 'job_family') IS NULL
    ALTER TABLE dbo.nh_hr_job_titles ADD job_family nvarchar(120) NULL;

IF COL_LENGTH('dbo.nh_hr_job_titles', 'organizational_level_id') IS NULL
    ALTER TABLE dbo.nh_hr_job_titles ADD organizational_level_id uniqueidentifier NULL;

-- -----------------------------------------------------------------------------
-- Extend nh_hr_work_locations
-- -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.nh_hr_work_locations', 'address_line1') IS NULL
    ALTER TABLE dbo.nh_hr_work_locations ADD address_line1 nvarchar(300) NULL;

IF COL_LENGTH('dbo.nh_hr_work_locations', 'timezone') IS NULL
    ALTER TABLE dbo.nh_hr_work_locations ADD timezone nvarchar(80) NULL;

IF COL_LENGTH('dbo.nh_hr_work_locations', 'location_type') IS NULL
    ALTER TABLE dbo.nh_hr_work_locations ADD location_type nvarchar(40) NULL
        CONSTRAINT DF_nh_hr_work_locations_type DEFAULT (N'Office');

IF COL_LENGTH('dbo.nh_hr_work_locations', 'is_headquarters') IS NULL
    ALTER TABLE dbo.nh_hr_work_locations ADD is_headquarters bit NOT NULL
        CONSTRAINT DF_nh_hr_work_locations_hq DEFAULT (0);

IF COL_LENGTH('dbo.nh_hr_work_locations', 'archived_at') IS NULL
    ALTER TABLE dbo.nh_hr_work_locations ADD archived_at datetimeoffset(7) NULL;

-- -----------------------------------------------------------------------------
-- Organizational levels
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_organizational_levels', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_organizational_levels (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        name                nvarchar(120)      NOT NULL,
        description         nvarchar(500)      NULL,
        level_order         int                NOT NULL,
        is_active           bit                NOT NULL CONSTRAINT DF_nh_hr_org_levels_active DEFAULT (1),
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_organizational_levels PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_nh_hr_org_levels_org_name UNIQUE (organization_id, name)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_org_levels_org
        ON dbo.nh_hr_organizational_levels (organization_id, level_order)
        WHERE is_active = 1;
END;

-- -----------------------------------------------------------------------------
-- Organizational positions (approved seats)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_organizational_positions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_organizational_positions (
        id                      uniqueidentifier   NOT NULL,
        organization_id         uniqueidentifier   NOT NULL,
        position_code           nvarchar(50)       NULL,
        position_name           nvarchar(200)      NOT NULL,
        job_title_id            uniqueidentifier   NULL,
        department_id           uniqueidentifier   NOT NULL,
        reports_to_position_id  uniqueidentifier   NULL,
        work_location_id        uniqueidentifier   NULL,
        organizational_level_id uniqueidentifier   NULL,
        employment_type_id      uniqueidentifier   NULL,
        approved_headcount      int                NOT NULL CONSTRAINT DF_nh_hr_org_pos_hc DEFAULT (1),
        status                  nvarchar(40)       NOT NULL,
        effective_date          date               NULL,
        end_date                date               NULL,
        description             nvarchar(1000)     NULL,
        created_at              datetimeoffset(7)  NOT NULL,
        created_by              uniqueidentifier   NULL,
        updated_at              datetimeoffset(7)  NULL,
        updated_by              uniqueidentifier   NULL,
        archived_at             datetimeoffset(7)  NULL,
        archived_by             uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_organizational_positions PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_org_pos_dept
            FOREIGN KEY (department_id) REFERENCES dbo.nh_hr_departments (id),
        CONSTRAINT CK_nh_hr_org_pos_status CHECK (status IN (
            N'Active', N'Planned', N'Frozen', N'Archived'
        )),
        CONSTRAINT CK_nh_hr_org_pos_hc CHECK (approved_headcount >= 0)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_org_pos_org_dept
        ON dbo.nh_hr_organizational_positions (organization_id, department_id, status)
        WHERE archived_at IS NULL;
    CREATE NONCLUSTERED INDEX IX_nh_hr_org_pos_reports
        ON dbo.nh_hr_organizational_positions (organization_id, reports_to_position_id)
        WHERE archived_at IS NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_nh_hr_org_pos_reports'
)
BEGIN
    ALTER TABLE dbo.nh_hr_organizational_positions WITH NOCHECK
        ADD CONSTRAINT FK_nh_hr_org_pos_reports
        FOREIGN KEY (reports_to_position_id) REFERENCES dbo.nh_hr_organizational_positions (id);
END;

-- -----------------------------------------------------------------------------
-- Position assignments (effective-dated)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_employee_position_assignments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_employee_position_assignments (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        position_id         uniqueidentifier   NOT NULL,
        allocation_pct      decimal(5,2)       NOT NULL CONSTRAINT DF_nh_hr_pos_asg_pct DEFAULT (100),
        is_primary          bit                NOT NULL CONSTRAINT DF_nh_hr_pos_asg_primary DEFAULT (1),
        effective_date      date               NOT NULL,
        end_date            date               NULL,
        status              nvarchar(40)       NOT NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        updated_at          datetimeoffset(7)  NULL,
        updated_by          uniqueidentifier   NULL,
        CONSTRAINT PK_nh_hr_employee_position_assignments PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_pos_asg_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT FK_nh_hr_pos_asg_position
            FOREIGN KEY (position_id) REFERENCES dbo.nh_hr_organizational_positions (id),
        CONSTRAINT CK_nh_hr_pos_asg_status CHECK (status IN (
            N'Active', N'Ended', N'Planned'
        )),
        CONSTRAINT CK_nh_hr_pos_asg_pct CHECK (allocation_pct > 0 AND allocation_pct <= 100)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_pos_asg_org_pos
        ON dbo.nh_hr_employee_position_assignments (organization_id, position_id, status)
        WHERE end_date IS NULL;
    CREATE NONCLUSTERED INDEX IX_nh_hr_pos_asg_org_emp
        ON dbo.nh_hr_employee_position_assignments (organization_id, employee_id, is_primary)
        WHERE end_date IS NULL AND status = N'Active';
END;

-- -----------------------------------------------------------------------------
-- Organizational activity / change history
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.nh_hr_organization_activity', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_organization_activity (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        change_type         nvarchar(80)       NOT NULL,
        entity_type         nvarchar(40)       NOT NULL,
        entity_id           uniqueidentifier   NULL,
        department_id       uniqueidentifier   NULL,
        employee_id         uniqueidentifier   NULL,
        position_id         uniqueidentifier   NULL,
        summary             nvarchar(500)      NOT NULL,
        previous_value      nvarchar(max)      NULL,
        new_value           nvarchar(max)      NULL,
        effective_date      date               NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        created_by_name     nvarchar(200)      NULL,
        CONSTRAINT PK_nh_hr_organization_activity PRIMARY KEY CLUSTERED (id)
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_org_activity_org
        ON dbo.nh_hr_organization_activity (organization_id, created_at DESC);
    CREATE NONCLUSTERED INDEX IX_nh_hr_org_activity_dept
        ON dbo.nh_hr_organization_activity (organization_id, department_id, created_at DESC)
        WHERE department_id IS NOT NULL;
END;

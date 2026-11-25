-- =============================================================================
-- NextHire — Core HR Documents hub indexes / optional metadata
-- Extends nh_hr_employee_documents for org-wide library queries.
-- =============================================================================

IF COL_LENGTH('dbo.nh_hr_employee_documents', 'file_size_bytes') IS NULL
BEGIN
    ALTER TABLE dbo.nh_hr_employee_documents ADD file_size_bytes bigint NULL;
END;

IF COL_LENGTH('dbo.nh_hr_employee_documents', 'content_type') IS NULL
BEGIN
    ALTER TABLE dbo.nh_hr_employee_documents ADD content_type nvarchar(120) NULL;
END;

IF COL_LENGTH('dbo.nh_hr_employee_documents', 'updated_at') IS NULL
BEGIN
    ALTER TABLE dbo.nh_hr_employee_documents ADD updated_at datetimeoffset(7) NULL;
END;

IF COL_LENGTH('dbo.nh_hr_employee_documents', 'updated_by') IS NULL
BEGIN
    ALTER TABLE dbo.nh_hr_employee_documents ADD updated_by uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_nh_hr_emp_docs_org_category'
      AND object_id = OBJECT_ID(N'dbo.nh_hr_employee_documents')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_docs_org_category
        ON dbo.nh_hr_employee_documents (organization_id, category, status, expires_at)
        WHERE archived_at IS NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_nh_hr_emp_docs_org_expires'
      AND object_id = OBJECT_ID(N'dbo.nh_hr_employee_documents')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_nh_hr_emp_docs_org_expires
        ON dbo.nh_hr_employee_documents (organization_id, expires_at)
        WHERE archived_at IS NULL AND expires_at IS NOT NULL;
END;

-- Lightweight document request / acknowledgement queue (optional Requests badge).
IF OBJECT_ID(N'dbo.nh_hr_document_requests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_hr_document_requests (
        id                  uniqueidentifier   NOT NULL,
        organization_id     uniqueidentifier   NOT NULL,
        employee_id         uniqueidentifier   NOT NULL,
        category            nvarchar(80)       NOT NULL,
        title               nvarchar(300)      NOT NULL,
        status              nvarchar(40)       NOT NULL,
        due_at              date               NULL,
        notes               nvarchar(1000)     NULL,
        fulfilled_document_id uniqueidentifier NULL,
        created_at          datetimeoffset(7)  NOT NULL,
        created_by          uniqueidentifier   NULL,
        completed_at        datetimeoffset(7)  NULL,
        CONSTRAINT PK_nh_hr_document_requests PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_nh_hr_doc_req_employee
            FOREIGN KEY (employee_id) REFERENCES dbo.nh_hr_employees (id),
        CONSTRAINT CK_nh_hr_doc_req_status CHECK (status IN (
            'Open', 'Fulfilled', 'Cancelled', 'Overdue'
        ))
    );
    CREATE NONCLUSTERED INDEX IX_nh_hr_doc_req_org
        ON dbo.nh_hr_document_requests (organization_id, status, due_at);
END;

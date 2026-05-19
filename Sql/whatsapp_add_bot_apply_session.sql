-- Adds JSON session storage for WhatsApp job-application bot flow.
IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.whatsapp_conversations')
      AND name = 'bot_apply_session_json'
)
BEGIN
    ALTER TABLE dbo.whatsapp_conversations
    ADD bot_apply_session_json NVARCHAR(MAX) NULL;
END
GO

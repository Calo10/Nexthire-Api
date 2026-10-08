using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.Helpers;

namespace nexthire_api.Repositories;

public class WhatsAppInboundRepository : IWhatsAppInboundRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public WhatsAppInboundRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<WhatsAppInboundSaveResult> SaveInboundMessageAsync(
        WhatsAppInboundMessageDto inbound,
        string normalizedPhoneNumber,
        CancellationToken cancellationToken)
    {
        const string findDuplicateMessageSql = @"
            SELECT TOP 1 conversation_id
            FROM whatsapp_messages
            WHERE tenant_id = @TenantId
              AND provider_message_id = @ProviderMessageId
              AND provider_message_id IS NOT NULL
              AND LEN(LTRIM(RTRIM(provider_message_id))) > 0;";

        const string findConversationSql = @"
            SELECT TOP 1 id
            FROM whatsapp_conversations
            WHERE tenant_id = @TenantId
              AND (
                    phone_number = @CanonicalPhone
                 OR phone_number = @WhatsappPrefixedPhone
              )
            ORDER BY last_message_at_utc DESC, updated_at_utc DESC;";

        const string insertConversationSql = @"
            INSERT INTO whatsapp_conversations (
                id,
                tenant_id,
                provider,
                channel,
                phone_number,
                profile_name,
                status,
                bot_enabled,
                last_message_at_utc,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                @Id,
                @TenantId,
                @Provider,
                @Channel,
                @PhoneNumber,
                @ProfileName,
                @Status,
                @BotEnabled,
                @LastMessageAtUtc,
                @CreatedAtUtc,
                @UpdatedAtUtc
            );";

        const string insertMessageSql = @"
            INSERT INTO whatsapp_messages (
                id,
                conversation_id,
                tenant_id,
                provider,
                provider_message_id,
                direction,
                body,
                media_json,
                created_at_utc
            )
            VALUES (
                @Id,
                @ConversationId,
                @TenantId,
                @Provider,
                @ProviderMessageId,
                @Direction,
                @Body,
                @MediaJson,
                @CreatedAtUtc
            );";

        const string updateConversationSql = @"
            UPDATE whatsapp_conversations
            SET
                profile_name = @ProfileName,
                last_message_at_utc = @LastMessageAtUtc,
                updated_at_utc = @UpdatedAtUtc
            WHERE id = @Id;";

        const string normalizeConversationPhoneSql = @"
            UPDATE whatsapp_conversations
            SET phone_number = @CanonicalPhone, updated_at_utc = @UpdatedAtUtc
            WHERE id = @Id
              AND tenant_id = @TenantId
              AND phone_number <> @CanonicalPhone;";

        var connection = _connectionFactory.CreateConnection();
        var ownsConnection = true;
        if (connection.State != ConnectionState.Open)
            connection.Open();

        using var transaction = connection.BeginTransaction();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var canonicalPhone = WhatsAppPhoneNormalizer.NormalizeForConversation(normalizedPhoneNumber);
            var whatsappPrefixedPhone = WhatsAppPhoneNormalizer.ToWhatsappPrefixed(canonicalPhone);

            var duplicateConversationId = await connection.ExecuteScalarAsync<Guid?>(
                findDuplicateMessageSql,
                new { inbound.TenantId, inbound.ProviderMessageId },
                transaction);

            var nowUtc = DateTimeOffset.UtcNow;
            if (duplicateConversationId.HasValue)
            {
                await connection.ExecuteAsync(
                    updateConversationSql,
                    new
                    {
                        Id = duplicateConversationId.Value,
                        inbound.ProfileName,
                        LastMessageAtUtc = inbound.ReceivedAtUtc,
                        UpdatedAtUtc = nowUtc
                    },
                    transaction);

                transaction.Commit();
                return new WhatsAppInboundSaveResult
                {
                    MessageInserted = false,
                    Context = new WhatsAppConversationContext
                    {
                        ConversationId = duplicateConversationId.Value,
                        TenantId = inbound.TenantId,
                        ContactPhone = string.IsNullOrEmpty(canonicalPhone) ? inbound.From.Trim() : canonicalPhone,
                        BusinessPhone = WhatsAppPhoneNormalizer.NormalizeForConversation(inbound.To)
                    }
                };
            }

            var conversationId = await connection.ExecuteScalarAsync<Guid?>(
                findConversationSql,
                new
                {
                    inbound.TenantId,
                    CanonicalPhone = canonicalPhone,
                    WhatsappPrefixedPhone = whatsappPrefixedPhone
                },
                transaction);

            if (!conversationId.HasValue)
            {
                conversationId = Guid.NewGuid();
                await connection.ExecuteAsync(
                    insertConversationSql,
                    new
                    {
                        Id = conversationId.Value,
                        inbound.TenantId,
                        Provider = string.IsNullOrWhiteSpace(inbound.Provider) ? "twilio" : inbound.Provider.Trim(),
                        Channel = string.IsNullOrWhiteSpace(inbound.Channel) ? "whatsapp" : inbound.Channel.Trim(),
                        PhoneNumber = canonicalPhone,
                        inbound.ProfileName,
                        Status = "open",
                        BotEnabled = true,
                        LastMessageAtUtc = inbound.ReceivedAtUtc,
                        CreatedAtUtc = nowUtc,
                        UpdatedAtUtc = nowUtc
                    },
                    transaction);
            }
            else if (!string.IsNullOrEmpty(canonicalPhone))
            {
                await connection.ExecuteAsync(
                    normalizeConversationPhoneSql,
                    new
                    {
                        Id = conversationId.Value,
                        inbound.TenantId,
                        CanonicalPhone = canonicalPhone,
                        UpdatedAtUtc = nowUtc
                    },
                    transaction);
            }

            var mediaJson = JsonSerializer.Serialize(inbound.Media ?? new List<WhatsAppInboundMediaDto>());

            await connection.ExecuteAsync(
                insertMessageSql,
                new
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId.Value,
                    inbound.TenantId,
                    inbound.Provider,
                    inbound.ProviderMessageId,
                    Direction = "inbound",
                    inbound.Body,
                    MediaJson = mediaJson,
                    CreatedAtUtc = nowUtc
                },
                transaction);

            await connection.ExecuteAsync(
                updateConversationSql,
                new
                {
                    Id = conversationId.Value,
                    inbound.ProfileName,
                    LastMessageAtUtc = inbound.ReceivedAtUtc,
                    UpdatedAtUtc = nowUtc
                },
                transaction);

            await TryLinkCandidateByPhoneAsync(
                connection,
                transaction,
                conversationId.Value,
                inbound.TenantId,
                canonicalPhone);

            transaction.Commit();
            return new WhatsAppInboundSaveResult
            {
                MessageInserted = true,
                Context = new WhatsAppConversationContext
                {
                    ConversationId = conversationId.Value,
                    TenantId = inbound.TenantId,
                    ContactPhone = string.IsNullOrEmpty(canonicalPhone) ? inbound.From.Trim() : canonicalPhone,
                    BusinessPhone = WhatsAppPhoneNormalizer.NormalizeForConversation(inbound.To)
                }
            };
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task<WhatsAppBotConfigDto?> GetBotConfigAsync(string tenantId, string? alternateTenantId = null)
    {
        const string sql = @"
            SELECT TOP 1
                tenant_id AS TenantId,
                is_enabled AS IsEnabled,
                system_prompt AS SystemPrompt,
                10 AS HistoryMessageLimit,
                CAST(NULL AS NVARCHAR(200)) AS BotModel
            FROM whatsapp_bot_config
            WHERE is_enabled = 1
              AND tenant_id IN (@tenantId, @alternateTenantId)
            ORDER BY CASE WHEN tenant_id = @tenantId THEN 0 ELSE 1 END;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<WhatsAppBotConfigDto>(
            sql,
            new { tenantId, alternateTenantId = alternateTenantId ?? tenantId });
    }

    public async Task EnsureTenantMappingsSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'whatsapp_tenant_mappings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.whatsapp_tenant_mappings
    (
        messenger_tenant NVARCHAR(100) NOT NULL CONSTRAINT PK_whatsapp_tenant_mappings PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        created_at_utc DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_whatsapp_tenant_mappings_created DEFAULT (SYSDATETIMEOFFSET())
    );
    CREATE INDEX IX_whatsapp_tenant_mappings_org_id ON dbo.whatsapp_tenant_mappings (org_id);
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static int _messageDeliverySchemaReady;

    public async Task EnsureMessageDeliverySchemaAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _messageDeliverySchemaReady) == 1)
            return;

        const string sql = @"
IF COL_LENGTH('dbo.whatsapp_messages', 'delivery_status') IS NULL
    EXEC(N'ALTER TABLE dbo.whatsapp_messages ADD delivery_status NVARCHAR(32) NULL;');
IF COL_LENGTH('dbo.whatsapp_messages', 'delivery_error_code') IS NULL
    EXEC(N'ALTER TABLE dbo.whatsapp_messages ADD delivery_error_code NVARCHAR(16) NULL;');
IF COL_LENGTH('dbo.whatsapp_messages', 'delivery_checked_at_utc') IS NULL
    EXEC(N'ALTER TABLE dbo.whatsapp_messages ADD delivery_checked_at_utc DATETIMEOFFSET(7) NULL;');";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        Volatile.Write(ref _messageDeliverySchemaReady, 1);
    }

    public async Task UpdateMessageDeliveryAsync(
        Guid messageId,
        string status,
        string? errorCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureMessageDeliverySchemaAsync(cancellationToken);
        const string sql = @"
            UPDATE whatsapp_messages
            SET delivery_status = @Status,
                delivery_error_code = @ErrorCode,
                delivery_checked_at_utc = SYSDATETIMEOFFSET()
            WHERE id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { Id = messageId, Status = status, ErrorCode = errorCode }, cancellationToken: cancellationToken));
    }

    public async Task TouchMessageDeliveryCheckAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await EnsureMessageDeliverySchemaAsync(cancellationToken);
        const string sql = @"
            UPDATE whatsapp_messages
            SET delivery_checked_at_utc = SYSDATETIMEOFFSET()
            WHERE id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = messageId }, cancellationToken: cancellationToken));
    }

    public async Task EnsureConversationReadSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF COL_LENGTH('dbo.whatsapp_conversations', 'recruiter_last_read_at_utc') IS NULL
BEGIN
    ALTER TABLE dbo.whatsapp_conversations ADD recruiter_last_read_at_utc DATETIMEOFFSET(7) NULL;
    EXEC('UPDATE dbo.whatsapp_conversations SET recruiter_last_read_at_utc = SYSDATETIMEOFFSET() WHERE recruiter_last_read_at_utc IS NULL');
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<bool> MarkConversationReadAsync(Guid conversationId, string tenantId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE c
            SET recruiter_last_read_at_utc = SYSDATETIMEOFFSET()
            FROM whatsapp_conversations c
            WHERE c.id = @conversationId
              AND (
                    c.tenant_id = @tenantId
                 OR c.tenant_id = CONVERT(nvarchar(100), TRY_CONVERT(uniqueidentifier, @tenantId))
                 OR EXISTS (
                        SELECT 1
                        FROM whatsapp_tenant_mappings map
                        WHERE map.messenger_tenant = c.tenant_id
                          AND (
                                map.org_id = TRY_CONVERT(uniqueidentifier, @tenantId)
                             OR CONVERT(nvarchar(100), map.org_id) = @tenantId
                          )
                    )
                  );";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { conversationId, tenantId }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task SyncTenantMappingsFromConfigAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await EnsureTenantMappingsSchemaAsync(cancellationToken);

        const string mergeSql = @"
MERGE dbo.whatsapp_tenant_mappings AS t
USING (SELECT @messengerTenant AS messenger_tenant, @orgId AS org_id) AS s
ON t.messenger_tenant = s.messenger_tenant
WHEN MATCHED AND t.org_id <> s.org_id THEN
    UPDATE SET org_id = s.org_id
WHEN NOT MATCHED THEN
    INSERT (messenger_tenant, org_id) VALUES (s.messenger_tenant, s.org_id);";

        using var connection = _connectionFactory.CreateConnection();
        foreach (var child in configuration.GetSection("WhatsApp:InboundTenantAliases").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value))
                continue;
            if (!Guid.TryParse(child.Value.Trim(), out var orgId))
                continue;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    mergeSql,
                    new { messengerTenant = child.Key.Trim(), orgId },
                    cancellationToken: cancellationToken));
        }
    }

    public async Task<Guid?> LookupOrgIdByMessengerTenantAsync(string messengerTenant, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messengerTenant))
            return null;

        const string sql = @"
            SELECT org_id
            FROM whatsapp_tenant_mappings
            WHERE messenger_tenant = @messengerTenant;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { messengerTenant = messengerTenant.Trim() }, cancellationToken: cancellationToken));
    }

    public async Task<string> ResolveInboundTenantAsync(
        string rawTenantId,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawTenantId))
            return rawTenantId;

        var trimmed = rawTenantId.Trim();

        var fromDb = await LookupOrgIdByMessengerTenantAsync(trimmed, cancellationToken);
        if (fromDb.HasValue)
            return fromDb.Value.ToString();

        var fromConfig = WhatsAppTenantResolver.Resolve(configuration, trimmed);
        if (Guid.TryParse(fromConfig, out _))
            return fromConfig;

        if (Guid.TryParse(trimmed, out _))
            return trimmed;

        return fromConfig;
    }

    public async Task<IReadOnlyList<WhatsAppMessageHistoryItemDto>> GetRecentMessagesAsync(Guid conversationId, int maxMessages)
    {
        const string sql = @"
            SELECT TOP (@maxMessages)
                direction AS Direction,
                body AS Body,
                created_at_utc AS ReceivedAtUtc
            FROM whatsapp_messages
            WHERE conversation_id = @conversationId
            ORDER BY created_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var recent = (await connection.QueryAsync<WhatsAppMessageHistoryItemDto>(
            sql,
            new { conversationId, maxMessages })).ToList();

        recent.Reverse();
        return recent;
    }

    public async Task SaveOutboundMessageAsync(
        Guid conversationId,
        string tenantId,
        string provider,
        string fromPhone,
        string toPhone,
        string body,
        string? providerMessageId,
        CancellationToken cancellationToken)
    {
        const string insertSql = @"
            INSERT INTO whatsapp_messages (
                id,
                conversation_id,
                tenant_id,
                provider,
                provider_message_id,
                direction,
                body,
                media_json,
                created_at_utc,
                delivery_status
            )
            VALUES (
                @Id,
                @ConversationId,
                @TenantId,
                @Provider,
                @ProviderMessageId,
                'outbound',
                @Body,
                '[]',
                @NowUtc,
                'queued'
            );";

        const string updateConversationSql = @"
            UPDATE whatsapp_conversations
            SET
                last_message_at_utc = @NowUtc,
                updated_at_utc = @NowUtc
            WHERE id = @ConversationId;";

        await EnsureMessageDeliverySchemaAsync(cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(providerMessageId))
            {
                const string dupSql = @"
                    SELECT TOP 1 id
                    FROM whatsapp_messages
                    WHERE tenant_id = @TenantId
                      AND provider_message_id = @ProviderMessageId
                      AND LEN(LTRIM(RTRIM(provider_message_id))) > 0;";
                var existingId = await connection.ExecuteScalarAsync<Guid?>(
                    dupSql,
                    new { TenantId = tenantId, ProviderMessageId = providerMessageId },
                    transaction);
                if (existingId.HasValue)
                {
                    await connection.ExecuteAsync(
                        updateConversationSql,
                        new { ConversationId = conversationId, NowUtc = nowUtc },
                        transaction);
                    transaction.Commit();
                    return;
                }
            }

            await connection.ExecuteAsync(
                insertSql,
                new
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    TenantId = tenantId,
                    Provider = provider,
                    ProviderMessageId = providerMessageId,
                    Body = body,
                    NowUtc = nowUtc
                },
                transaction);

            await connection.ExecuteAsync(
                updateConversationSql,
                new { ConversationId = conversationId, NowUtc = nowUtc },
                transaction);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task MarkNeedsHumanAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE whatsapp_conversations
            SET
                status = 'needs_human',
                bot_state = 'needs_human',
                updated_at_utc = @NowUtc
            WHERE id = @conversationId;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new { conversationId, NowUtc = DateTimeOffset.UtcNow });
    }

    public async Task<bool> IsConversationBotEnabledAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT CASE WHEN bot_enabled = 0 THEN 0 ELSE 1 END
            FROM whatsapp_conversations
            WHERE id = @conversationId;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        var enabled = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(sql, new { conversationId }, cancellationToken: cancellationToken));
        return enabled != 0;
    }

    public async Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(string tenantId, string? status)
    {
        const string sql = @"
            SELECT
                c.id AS Id,
                c.phone_number AS PhoneNumber,
                c.profile_name AS ProfileName,
                nameMatch.CandidateName AS CandidateName,
                c.candidate_id AS CandidateId,
                c.job_id AS JobId,
                c.application_id AS ApplicationId,
                c.assigned_recruiter_id AS AssignedRecruiterId,
                c.status AS Status,
                ISNULL(c.bot_enabled, 0) AS BotEnabled,
                c.last_message_at_utc AS LastMessageAtUtc,
                (
                    SELECT TOP 1 m.body
                    FROM whatsapp_messages m
                    WHERE m.conversation_id = c.id
                    ORDER BY m.created_at_utc DESC
                ) AS LastMessageBody,
                (
                    SELECT COUNT(1)
                    FROM whatsapp_messages um
                    WHERE um.conversation_id = c.id
                      AND LOWER(LTRIM(RTRIM(um.direction))) = 'inbound'
                      AND (
                            c.recruiter_last_read_at_utc IS NULL
                         OR um.created_at_utc > c.recruiter_last_read_at_utc
                      )
                ) AS UnreadCount
            FROM whatsapp_conversations c
            OUTER APPLY (
                SELECT TOP 1
                    NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(cand.first_name, N''), N' ', ISNULL(cand.last_name, N'')))), N'') AS CandidateName
                FROM candidates cand
                CROSS APPLY (
                    SELECT
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LOWER(ISNULL(c.phone_number, N'')), N'whatsapp:', N''), N' ', N''), N'-', N''), N'(', N''), N')', N''), N'+', N'') AS ConversationDigits,
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(cand.phone, N''), N' ', N''), N'-', N''), N'(', N''), N')', N''), N'+', N'') AS CandidateDigits
                ) phones
                WHERE cand.org_id = COALESCE(
                        TRY_CONVERT(uniqueidentifier, c.tenant_id),
                        TRY_CONVERT(uniqueidentifier, @tenantId),
                        (
                            SELECT TOP 1 m.org_id
                            FROM whatsapp_tenant_mappings m
                            WHERE m.messenger_tenant = c.tenant_id
                        )
                      )
                  AND NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(cand.first_name, N''), N' ', ISNULL(cand.last_name, N'')))), N'') IS NOT NULL
                  AND (
                        cand.id = c.candidate_id
                     OR (
                            LEN(phones.ConversationDigits) >= 10
                        AND LEN(phones.CandidateDigits) >= 10
                        AND RIGHT(phones.ConversationDigits, 10) = RIGHT(phones.CandidateDigits, 10)
                        )
                      )
                ORDER BY CASE WHEN cand.id = c.candidate_id THEN 0 ELSE 1 END
            ) nameMatch
            WHERE (
                    c.tenant_id = @tenantId
                 OR c.tenant_id = CONVERT(nvarchar(100), TRY_CONVERT(uniqueidentifier, @tenantId))
                 OR EXISTS (
                        SELECT 1
                        FROM whatsapp_tenant_mappings map
                        WHERE map.messenger_tenant = c.tenant_id
                          AND (
                                map.org_id = TRY_CONVERT(uniqueidentifier, @tenantId)
                             OR CONVERT(nvarchar(100), map.org_id) = @tenantId
                          )
                    )
                  )
              AND (@status IS NULL OR c.status = @status)
            ORDER BY c.last_message_at_utc DESC, c.updated_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WhatsAppConversationListItemDto>(sql, new
        {
            tenantId,
            status = string.IsNullOrWhiteSpace(status) ? null : status.Trim()
        });
        return rows.ToList();
    }

    public async Task<WhatsAppConversationListItemDto?> GetConversationByCandidateAsync(string tenantId, Guid candidateId)
    {
        const string sql = @"
            SELECT TOP 1
                c.id AS Id,
                c.phone_number AS PhoneNumber,
                c.profile_name AS ProfileName,
                c.candidate_id AS CandidateId,
                c.job_id AS JobId,
                c.application_id AS ApplicationId,
                c.assigned_recruiter_id AS AssignedRecruiterId,
                c.status AS Status,
                ISNULL(c.bot_enabled, 0) AS BotEnabled,
                c.last_message_at_utc AS LastMessageAtUtc,
                (
                    SELECT COUNT(1)
                    FROM whatsapp_messages um
                    WHERE um.conversation_id = c.id
                      AND LOWER(LTRIM(RTRIM(um.direction))) = 'inbound'
                      AND (
                            c.recruiter_last_read_at_utc IS NULL
                         OR um.created_at_utc > c.recruiter_last_read_at_utc
                      )
                ) AS UnreadCount
            FROM whatsapp_conversations c
            WHERE c.tenant_id = @tenantId
              AND c.candidate_id = @candidateId
            ORDER BY c.last_message_at_utc DESC, c.updated_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();

        const string candidateSql = @"
            SELECT TOP 1
                org_id AS OrgId,
                phone AS Phone
            FROM candidates
            WHERE id = @candidateId;";

        var candidate = await connection.QueryFirstOrDefaultAsync<CandidatePhoneLookup>(candidateSql, new { candidateId });
        if (candidate == null || candidate.OrgId == Guid.Empty)
            return await connection.QueryFirstOrDefaultAsync<WhatsAppConversationListItemDto>(sql, new { tenantId, candidateId });

        if (Guid.TryParse(tenantId, out var requestedOrgId) && requestedOrgId != candidate.OrgId)
            return null;

        var digits = new string((candidate.Phone ?? string.Empty).Where(char.IsDigit).ToArray());

        const string matchSql = @"
            SELECT TOP 1
                c.id AS Id,
                c.phone_number AS PhoneNumber,
                c.profile_name AS ProfileName,
                c.candidate_id AS CandidateId,
                c.job_id AS JobId,
                c.application_id AS ApplicationId,
                c.assigned_recruiter_id AS AssignedRecruiterId,
                c.status AS Status,
                ISNULL(c.bot_enabled, 0) AS BotEnabled,
                c.last_message_at_utc AS LastMessageAtUtc,
                (
                    SELECT COUNT(1)
                    FROM whatsapp_messages um
                    WHERE um.conversation_id = c.id
                      AND LOWER(LTRIM(RTRIM(um.direction))) = 'inbound'
                      AND (
                            c.recruiter_last_read_at_utc IS NULL
                         OR um.created_at_utc > c.recruiter_last_read_at_utc
                      )
                ) AS UnreadCount
            FROM whatsapp_conversations c
            WHERE (
                    c.tenant_id = @tenantId
                 OR c.tenant_id = CONVERT(nvarchar(100), @orgId)
                 OR EXISTS (
                        SELECT 1
                        FROM whatsapp_tenant_mappings m
                        WHERE m.org_id = @orgId
                          AND m.messenger_tenant = c.tenant_id
                    )
                  )
              AND (
                    c.candidate_id = @candidateId
                 OR (
                        @digits <> ''
                    AND c.candidate_id IS NULL
                    AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LOWER(ISNULL(c.phone_number, '')), 'whatsapp:', ''), ' ', ''), '-', ''), '(', ''), ')', ''), '+', '') = @digits
                    )
                  )
            ORDER BY
                (SELECT COUNT(1) FROM whatsapp_messages msg WHERE msg.conversation_id = c.id) DESC,
                c.last_message_at_utc DESC;";

        var match = await connection.QueryFirstOrDefaultAsync<WhatsAppConversationListItemDto>(
            matchSql,
            new { tenantId, candidateId, orgId = candidate.OrgId, digits });
        if (match == null)
            return null;

        if (match.CandidateId == null)
        {
            const string linkSql = @"
                UPDATE whatsapp_conversations
                SET candidate_id = @candidateId, updated_at_utc = SYSDATETIMEOFFSET()
                WHERE id = @id AND candidate_id IS NULL;";
            await connection.ExecuteAsync(linkSql, new { candidateId, id = match.Id });
            match.CandidateId = candidateId;
        }

        return match;
    }

    private sealed class CandidatePhoneLookup
    {
        public Guid OrgId { get; set; }
        public string? Phone { get; set; }
    }

    private static async Task TryLinkCandidateByPhoneAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        Guid conversationId,
        string tenantId,
        string canonicalPhone)
    {
        var digits = new string((canonicalPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
            return;

        const string sql = @"
            UPDATE c
            SET candidate_id = picked.id
            FROM whatsapp_conversations c
            CROSS APPLY (
                SELECT TOP 1 cand.id
                FROM candidates cand
                WHERE cand.org_id = COALESCE(
                        TRY_CONVERT(uniqueidentifier, @TenantId),
                        (SELECT TOP 1 m.org_id FROM whatsapp_tenant_mappings m WHERE m.messenger_tenant = @TenantId)
                      )
                  AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(cand.phone, ''), ' ', ''), '-', ''), '(', ''), ')', ''), '+', '') = @Digits
            ) picked
            WHERE c.id = @ConversationId
              AND c.candidate_id IS NULL;";

        await connection.ExecuteAsync(
            sql,
            new { ConversationId = conversationId, TenantId = tenantId, Digits = digits },
            transaction);
    }

    public async Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId)
    {
        await EnsureMessageDeliverySchemaAsync();
        const string sql = @"
            SELECT
                m.id AS Id,
                m.conversation_id AS ConversationId,
                m.direction AS Direction,
                m.provider_message_id AS ProviderMessageId,
                CASE WHEN m.direction = 'inbound' THEN c.phone_number ELSE NULL END AS FromPhone,
                CASE WHEN m.direction = 'outbound' THEN c.phone_number ELSE NULL END AS ToPhone,
                m.body AS Body,
                m.created_at_utc AS CreatedAtUtc,
                m.delivery_status AS DeliveryStatus,
                m.delivery_error_code AS DeliveryErrorCode,
                m.delivery_checked_at_utc AS DeliveryCheckedAtUtc
            FROM whatsapp_messages m
            INNER JOIN whatsapp_conversations c
                ON c.id = m.conversation_id
            WHERE m.conversation_id = @conversationId
              AND (
                    c.tenant_id = @tenantId
                 OR c.tenant_id = CONVERT(nvarchar(100), TRY_CONVERT(uniqueidentifier, @tenantId))
                 OR EXISTS (
                        SELECT 1
                        FROM whatsapp_tenant_mappings map
                        WHERE map.messenger_tenant = c.tenant_id
                          AND (
                                map.org_id = TRY_CONVERT(uniqueidentifier, @tenantId)
                             OR CONVERT(nvarchar(100), map.org_id) = @tenantId
                          )
                    )
                  )
            ORDER BY m.created_at_utc ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WhatsAppConversationMessageDto>(sql, new { conversationId, tenantId });
        return rows.ToList();
    }

    public async Task<DateTimeOffset?> GetLastInboundAtUtcAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT MAX(created_at_utc)
            FROM whatsapp_messages
            WHERE conversation_id = @conversationId
              AND LOWER(LTRIM(RTRIM(direction))) = 'inbound';";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<DateTimeOffset?>(
            new CommandDefinition(sql, new { conversationId }, cancellationToken: cancellationToken));
    }

    public async Task<WhatsAppConversationSendContextDto?> GetConversationSendContextAsync(Guid conversationId, string tenantId)
    {
        const string sql = @"
            SELECT TOP 1
                c.id AS Id,
                c.tenant_id AS TenantId,
                c.phone_number AS PhoneNumber,
                NULL AS BusinessPhoneNumber
            FROM whatsapp_conversations c
            WHERE c.id = @conversationId
              AND (
                    c.tenant_id = @tenantId
                 OR c.tenant_id = CONVERT(nvarchar(100), TRY_CONVERT(uniqueidentifier, @tenantId))
                 OR EXISTS (
                        SELECT 1
                        FROM whatsapp_tenant_mappings map
                        WHERE map.messenger_tenant = c.tenant_id
                          AND (
                                map.org_id = TRY_CONVERT(uniqueidentifier, @tenantId)
                             OR CONVERT(nvarchar(100), map.org_id) = @tenantId
                          )
                    )
                  );";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<WhatsAppConversationSendContextDto>(sql, new { conversationId, tenantId });
    }

    public async Task<WhatsAppConversationSendContextDto> EnsureConversationForDirectSendAsync(
        string tenantId,
        string toPhone,
        Guid? candidateId,
        CancellationToken cancellationToken)
    {
        var canonical = WhatsAppPhoneNormalizer.NormalizeForConversation(toPhone);
        var whatsappPrefixed = WhatsAppPhoneNormalizer.ToWhatsappPrefixed(canonical);

        const string findSql = @"
            SELECT TOP 1
                c.id AS Id,
                c.tenant_id AS TenantId,
                c.phone_number AS PhoneNumber,
                NULL AS BusinessPhoneNumber
            FROM whatsapp_conversations c
            WHERE c.tenant_id = @tenantId
              AND (
                    c.phone_number = @CanonicalPhone
                 OR c.phone_number = @WhatsappPrefixedPhone
              )
            ORDER BY c.last_message_at_utc DESC, c.updated_at_utc DESC;";

        const string insertSql = @"
            INSERT INTO whatsapp_conversations (
                id,
                tenant_id,
                provider,
                channel,
                phone_number,
                candidate_id,
                status,
                bot_enabled,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                @Id,
                @TenantId,
                @Provider,
                @Channel,
                @PhoneNumber,
                @CandidateId,
                'open',
                1,
                @NowUtc,
                @NowUtc
            );";

        const string updateCandidateSql = @"
            UPDATE whatsapp_conversations
            SET
                candidate_id = @CandidateId,
                updated_at_utc = @NowUtc
            WHERE id = @Id
              AND tenant_id = @TenantId
              AND @CandidateId IS NOT NULL
              AND candidate_id IS NULL;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            var context = await connection.QueryFirstOrDefaultAsync<WhatsAppConversationSendContextDto>(
                findSql,
                new { tenantId, CanonicalPhone = canonical, WhatsappPrefixedPhone = whatsappPrefixed },
                transaction);

            var nowUtc = DateTimeOffset.UtcNow;
            if (context == null)
            {
                var id = Guid.NewGuid();
                await connection.ExecuteAsync(
                    insertSql,
                    new
                    {
                        Id = id,
                        TenantId = tenantId,
                        Provider = "twilio",
                        Channel = "whatsapp",
                        PhoneNumber = canonical,
                        CandidateId = candidateId,
                        NowUtc = nowUtc
                    },
                    transaction);

                context = new WhatsAppConversationSendContextDto
                {
                    Id = id,
                    TenantId = tenantId,
                    PhoneNumber = canonical
                };
            }
            else
            {
                context.PhoneNumber = WhatsAppPhoneNormalizer.NormalizeForConversation(context.PhoneNumber);
                await connection.ExecuteAsync(
                    updateCandidateSql,
                    new
                    {
                        Id = context.Id,
                        TenantId = tenantId,
                        CandidateId = candidateId,
                        NowUtc = nowUtc
                    },
                    transaction);
            }

            transaction.Commit();
            return context;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<Guid> SaveOutboundMessageForInboxAsync(
        Guid conversationId,
        string tenantId,
        string provider,
        string? fromPhone,
        string toPhone,
        string body,
        string? providerMessageId,
        CancellationToken cancellationToken)
    {
        const string insertSql = @"
            INSERT INTO whatsapp_messages (
                id,
                conversation_id,
                tenant_id,
                provider,
                provider_message_id,
                direction,
                body,
                media_json,
                created_at_utc,
                delivery_status
            )
            VALUES (
                @Id,
                @ConversationId,
                @TenantId,
                @Provider,
                @ProviderMessageId,
                'outbound',
                @Body,
                '[]',
                @NowUtc,
                'queued'
            );";

        const string updateConversationSql = @"
            UPDATE whatsapp_conversations
            SET
                last_message_at_utc = @NowUtc,
                updated_at_utc = @NowUtc,
                bot_enabled = 0
            WHERE id = @ConversationId
              AND tenant_id = @TenantId;";

        await EnsureMessageDeliverySchemaAsync(cancellationToken);
        var messageId = Guid.NewGuid();
        var nowUtc = DateTimeOffset.UtcNow;
        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(providerMessageId))
            {
                const string dupSql = @"
                    SELECT TOP 1 id
                    FROM whatsapp_messages
                    WHERE tenant_id = @TenantId
                      AND provider_message_id = @ProviderMessageId
                      AND LEN(LTRIM(RTRIM(provider_message_id))) > 0;";
                var existingId = await connection.ExecuteScalarAsync<Guid?>(
                    dupSql,
                    new { TenantId = tenantId, ProviderMessageId = providerMessageId },
                    transaction);
                if (existingId.HasValue)
                {
                    await connection.ExecuteAsync(
                        updateConversationSql,
                        new { ConversationId = conversationId, TenantId = tenantId, NowUtc = nowUtc },
                        transaction);
                    transaction.Commit();
                    return existingId.Value;
                }
            }

            await connection.ExecuteAsync(
                insertSql,
                new
                {
                    Id = messageId,
                    ConversationId = conversationId,
                    TenantId = tenantId,
                    Provider = provider,
                    ProviderMessageId = providerMessageId,
                    Body = body,
                    NowUtc = nowUtc
                },
                transaction);

            await connection.ExecuteAsync(
                updateConversationSql,
                new { ConversationId = conversationId, TenantId = tenantId, NowUtc = nowUtc },
                transaction);

            transaction.Commit();
            return messageId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<bool> UpdateConversationAsync(
        Guid conversationId,
        string tenantId,
        UpdateWhatsAppConversationRequestDto request,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE whatsapp_conversations
            SET
                candidate_id = @CandidateId,
                job_id = @JobId,
                application_id = @ApplicationId,
                assigned_recruiter_id = @AssignedRecruiterId,
                status = COALESCE(@Status, status),
                bot_enabled = COALESCE(@BotEnabled, bot_enabled),
                updated_at_utc = @NowUtc
            WHERE id = @ConversationId
              AND tenant_id = @TenantId;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new
        {
            ConversationId = conversationId,
            TenantId = tenantId,
            request.CandidateId,
            request.JobId,
            request.ApplicationId,
            request.AssignedRecruiterId,
            request.Status,
            request.BotEnabled,
            NowUtc = DateTimeOffset.UtcNow
        });
        return affected > 0;
    }

    public async Task<WhatsAppConversationApplyStateDto?> GetConversationApplyStateAsync(
        Guid conversationId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT
                job_id AS JobId,
                bot_apply_session_json AS BotApplySessionJson
            FROM whatsapp_conversations
            WHERE id = @conversationId
              AND tenant_id = @tenantId;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<WhatsAppConversationApplyStateDto>(
            sql,
            new { conversationId, tenantId });
    }

    public async Task UpdateConversationApplyStateAsync(
        Guid conversationId,
        string tenantId,
        Guid? jobId,
        string? botApplySessionJson,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE whatsapp_conversations
            SET
                job_id = @JobId,
                bot_apply_session_json = @BotApplySessionJson,
                updated_at_utc = @NowUtc
            WHERE id = @ConversationId
              AND tenant_id = @TenantId;";

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            sql,
            new
            {
                ConversationId = conversationId,
                TenantId = tenantId,
                JobId = jobId,
                BotApplySessionJson = botApplySessionJson,
                NowUtc = DateTimeOffset.UtcNow
            });
    }
}

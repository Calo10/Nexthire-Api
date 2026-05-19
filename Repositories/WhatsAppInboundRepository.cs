using System.Data;
using System.Text.Json;
using Dapper;
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

    public async Task<WhatsAppBotConfigDto?> GetBotConfigAsync(string tenantId)
    {
        // history_message_limit / model are optional in DB; use appsettings AzureOpenAI:Deployment when BotModel is null.
        const string sql = @"
            SELECT TOP 1
                tenant_id AS TenantId,
                is_enabled AS IsEnabled,
                system_prompt AS SystemPrompt,
                10 AS HistoryMessageLimit,
                CAST(NULL AS NVARCHAR(200)) AS BotModel
            FROM whatsapp_bot_config
            WHERE tenant_id = @tenantId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<WhatsAppBotConfigDto>(sql, new { tenantId });
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
                created_at_utc
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
                @NowUtc
            );";

        const string updateConversationSql = @"
            UPDATE whatsapp_conversations
            SET
                last_message_at_utc = @NowUtc,
                updated_at_utc = @NowUtc
            WHERE id = @ConversationId;";

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

    public async Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(string tenantId, string? status)
    {
        const string sql = @"
            SELECT
                c.id AS Id,
                c.phone_number AS PhoneNumber,
                c.profile_name AS ProfileName,
                c.candidate_id AS CandidateId,
                c.job_id AS JobId,
                c.application_id AS ApplicationId,
                c.assigned_recruiter_id AS AssignedRecruiterId,
                c.status AS Status,
                ISNULL(c.bot_enabled, 0) AS BotEnabled,
                c.last_message_at_utc AS LastMessageAtUtc
            FROM whatsapp_conversations c
            WHERE c.tenant_id = @tenantId
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
                c.last_message_at_utc AS LastMessageAtUtc
            FROM whatsapp_conversations c
            WHERE c.tenant_id = @tenantId
              AND c.candidate_id = @candidateId
            ORDER BY c.last_message_at_utc DESC, c.updated_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<WhatsAppConversationListItemDto>(sql, new { tenantId, candidateId });
    }

    public async Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId)
    {
        const string sql = @"
            SELECT
                m.id AS Id,
                m.conversation_id AS ConversationId,
                m.direction AS Direction,
                m.provider_message_id AS ProviderMessageId,
                CASE WHEN m.direction = 'inbound' THEN c.phone_number ELSE NULL END AS FromPhone,
                CASE WHEN m.direction = 'outbound' THEN c.phone_number ELSE NULL END AS ToPhone,
                m.body AS Body,
                m.created_at_utc AS CreatedAtUtc
            FROM whatsapp_messages m
            INNER JOIN whatsapp_conversations c
                ON c.id = m.conversation_id
            WHERE m.conversation_id = @conversationId
              AND c.tenant_id = @tenantId
            ORDER BY m.created_at_utc ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WhatsAppConversationMessageDto>(sql, new { conversationId, tenantId });
        return rows.ToList();
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
              AND c.tenant_id = @tenantId;";

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
                created_at_utc
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
                @NowUtc
            );";

        const string updateConversationSql = @"
            UPDATE whatsapp_conversations
            SET
                last_message_at_utc = @NowUtc,
                updated_at_utc = @NowUtc
            WHERE id = @ConversationId
              AND tenant_id = @TenantId;";

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

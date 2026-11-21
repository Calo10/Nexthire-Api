namespace nexthire_api.WorkflowAutomation.Documents;

/// <summary>V1 stub — Nexa.Documents integration deferred.</summary>
public sealed class NexaDocumentsClient : INexaDocumentsClient
{
    public Task<NexaDocumentDownloadUrl> CreateDownloadUrlAsync(
        Guid organizationId,
        string documentId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<NexaDocumentDownloadUrl>(
            new InvalidOperationException("Document downloads are not enabled in NextHire workflows V1."));
}

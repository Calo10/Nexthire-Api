using Nexa.WorkflowAutomation.Contracts.Providers;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>V1 stub — document send is deferred.</summary>
public sealed class WorkflowDocumentService : IDocumentService
{
    public Task<DocumentSendResult> SendAsync(DocumentSendRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DocumentSendResult
        {
            Success = false,
            ErrorCode = "documents.not_supported",
            ErrorMessage = "Document send is not enabled in NextHire workflows V1.",
        });
}

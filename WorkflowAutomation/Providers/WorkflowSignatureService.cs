using Nexa.WorkflowAutomation.Contracts.Providers;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>V1 stub — DocuSign is deferred.</summary>
public sealed class WorkflowSignatureService : ISignatureService
{
    public Task<SignatureRequestResult> SendForSignatureAsync(
        SignatureSendRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SignatureRequestResult
        {
            Success = false,
            ErrorCode = "signature.not_supported",
            ErrorMessage = "Signature requests are not enabled in NextHire workflows V1.",
        });
}

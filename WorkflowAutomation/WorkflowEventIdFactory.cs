using System.Security.Cryptography;
using System.Text;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Builds stable event ids so webhook retries are idempotent via <c>nh_wf_event_receipts</c>.
/// </summary>
public static class WorkflowEventIdFactory
{
    public static Guid FromExternalKey(string ns, string externalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKey);

        var bytes = Encoding.UTF8.GetBytes($"{ns.Trim().ToLowerInvariant()}:{externalKey.Trim()}");
        var hash = SHA256.HashData(bytes);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        // RFC 4122 variant bits
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}

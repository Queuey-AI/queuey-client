using System;

namespace Queuey.Client.Waas;

/// <summary>
/// A rotation Queuey gave to a person (202 <c>pending_approval</c>, Queuey #511): nothing was stored, the value that was sent
/// was not kept, and a person pastes the new value on <see cref="ApprovalUrl"/>, which runs the rotation.
/// </summary>
// Et unntak fordi rotasjonen ikke skjedde: den som kaller, får ikke en credential tilbake, og må ikke tro den gjorde det. Internt,
// som RotateCredentialAsync; `queuey credentials rotate` fanger det.
internal sealed class CredentialRotationPendingException : QueueyException
{
    /// <summary>The status Queuey answers with.</summary>
    public const string PendingApproval = "pending_approval";

    public CredentialRotationPendingException(
        string message, string? credentialRequest, string? approvalUrl, DateTimeOffset? expiresAt, string? policyRule)
        : base(message, 202, PendingApproval)
    {
        CredentialRequest = credentialRequest;
        ApprovalUrl = approvalUrl;
        ExpiresAt = expiresAt;
        PolicyRule = policyRule;
    }

    /// <summary>The credential request (<c>creq_…</c>) the person fulfils.</summary>
    public string? CredentialRequest { get; }

    /// <summary>The console page where the person pastes the value; null when Queuey has no console address.</summary>
    public string? ApprovalUrl { get; }

    /// <summary>When the request expires if nobody pastes a value.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>The policy rule that gave the rotation to a person.</summary>
    public string? PolicyRule { get; }
}

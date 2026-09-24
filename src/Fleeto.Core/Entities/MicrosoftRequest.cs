namespace Fleeto.Core.Entities;

/// <summary>What an admin asks the workers to do at Microsoft (0.5.0).</summary>
public enum MicrosoftRequestKind
{
    /// <summary>Check the app registration of the sign-in: the credential, and whether it may read users.</summary>
    TestSignIn,

    /// <summary>Check the app registration of Microsoft Graph email: the credential, and whether it may send mail.</summary>
    TestEmail,

    /// <summary>Find users of the instance's own tenant to link or add, by name or account.</summary>
    SearchUsers
}

/// <summary>Where a <see cref="MicrosoftRequest"/> stands.</summary>
public enum MicrosoftRequestState
{
    Requested,
    Completed,
    Failed
}

/// <summary>
/// One question an admin asks Microsoft from Settings (0.5.0): a test of an app registration, or a search of the users of the
/// tenant. fleeto-web has no outbound access, so it writes the question here and waits; the workers ask Microsoft with the
/// credential they read from the settings themselves and write the answer back. A row lives for the seconds the answer takes:
/// web deletes it once read, and the workers delete what nobody picked up.
/// </summary>
public class MicrosoftRequest
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The admin who asked.</summary>
    public Guid RequestedBy { get; set; }

    public MicrosoftRequestKind Kind { get; set; }

    /// <summary>The search text of <see cref="MicrosoftRequestKind.SearchUsers"/>; blank lists the first users.</summary>
    public string? Query { get; set; }

    public MicrosoftRequestState State { get; set; } = MicrosoftRequestState.Requested;

    /// <summary>The answer as JSON: the checks of a test, or the users a search found. Never a token or a secret.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Cause and next step of a failure, safe to show to the admin.</summary>
    public string? FailureReason { get; set; }

    public DateTime? CompletedAt { get; set; }
}

using Fleeto.Core.Entities;

namespace Fleeto.Web;

/// <summary>
/// Limits of the Blazor Server connection between a browser and web.
/// </summary>
public static class CircuitLimits
{
    /// <summary>
    /// The largest message a browser may send over its connection. The default of ASP.NET Core is 32 KB, and a field
    /// whose value is larger, a script of 40 KB for example, closed the connection when it was sent, so the script could not
    /// be saved (found testing 0.6.0 on 2026-10-14). This is enough for the largest script the library takes, at 3 bytes
    /// per character in UTF-8 plus the framing, and still bounds what one connection makes the server read at once.
    /// </summary>
    public const long MaxReceiveMessageBytes = 4L * ScriptRules.MaxBodyLength;
}

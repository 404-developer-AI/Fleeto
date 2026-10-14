using System.Text;
using Fleeto.Core.Entities;

namespace Fleeto.Web.Tests;

/// <summary>
/// The connection between browser and web carries the largest script the library takes (found testing 0.6.0 on 2026-10-14:
/// a script of 40 KB could not be saved under the default limit of 32 KB).
/// </summary>
public sealed class CircuitLimitsTests
{
    [Fact]
    public void The_largest_script_fits_in_one_message_from_the_browser()
    {
        // The worst case in UTF-8: every character of the largest script takes 3 bytes.
        var script = new string('é', ScriptRules.MaxBodyLength / 2) + new string('€', ScriptRules.MaxBodyLength / 2);
        Assert.True(Encoding.UTF8.GetByteCount(script) < CircuitLimits.MaxReceiveMessageBytes);
        Assert.True(CircuitLimits.MaxReceiveMessageBytes > 32 * 1024);
    }
}

using Fleeto.Core.Entities;
using Fleeto.Web.Services;

namespace Fleeto.Web.Tests;

/// <summary>
/// The search field of the script library (0.7.0): case-insensitive, every word must match the name, description, language, platform or
/// client, and an empty search shows every script.
/// </summary>
public sealed class ScriptSearchTests
{
    private static readonly ScriptListItem Cleanup = Item("Clear temp files", "Removes old files from the temp folder", ScriptLanguage.PowerShell, "ACME");
    private static readonly ScriptListItem Restart = Item("Restart spooler", null, ScriptLanguage.Batch, null);
    private static readonly ScriptListItem Logs = Item("Rotate logs", "journalctl vacuum", ScriptLanguage.Bash, null);
    private static readonly ScriptListItem[] All = [Cleanup, Restart, Logs];

    private static ScriptListItem Item(string name, string? description, ScriptLanguage language, string? clientCode) =>
        new(Guid.NewGuid(), name, description, language, clientCode is null ? null : Guid.NewGuid(), clientCode, 1, true, DateTime.UtcNow);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_search_shows_every_script(string? search) => Assert.Equal(All, ScriptSearch.Filter(All, search));

    [Theory]
    [InlineData("SPOOLER", "Restart spooler")]
    [InlineData("temp folder", "Clear temp files")]
    [InlineData("acme", "Clear temp files")]
    [InlineData("bash", "Rotate logs")]
    [InlineData("linux", "Rotate logs")]
    [InlineData("all clients batch", "Restart spooler")]
    public void Every_word_must_match_a_field(string search, string expected) =>
        Assert.Equal([expected], ScriptSearch.Filter(All, search).Select(s => s.Name));

    [Fact]
    public void A_search_without_a_match_shows_nothing() => Assert.Empty(ScriptSearch.Filter(All, "spooler linux"));
}

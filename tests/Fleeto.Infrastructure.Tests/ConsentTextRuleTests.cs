using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The texts of the consent prompt (0.8.0, decided 2026-11-04): set per policy, cleaned and limited before they are stored and again before
/// they are signed, and only sent when a workstation asks for consent.
/// </summary>
public class ConsentTextRuleTests
{
    [Fact]
    public void Empty_texts_are_the_default_and_texts_are_trimmed_to_clean_lines()
    {
        var (texts, problem) = ConsentTextRules.Clean(new ConsentTexts("  ", "\r\n\r\n", null, ""));
        Assert.Null(problem);
        Assert.True(texts!.IsDefault);

        (texts, problem) = ConsentTextRules.Clean(new ConsentTexts(" Verzoek\ttot  overname ", "\n wil uw computer bedienen. \r\n\r\n\r\n Staat u dit toe? \n\n",
            "Toestaan", "Weigeren"));
        Assert.Null(problem);
        Assert.Equal("Verzoek tot overname", texts!.Title);
        Assert.Equal("wil uw computer bedienen.\n\nStaat u dit toe?", texts.Message);
        Assert.Equal("Toestaan", texts.AllowLabel);
        Assert.Equal("Weigeren", texts.DenyLabel);
        Assert.Equal(ConsentTexts.Default, ConsentTextRules.Clean(null).Texts);
    }

    [Fact]
    public void Texts_beyond_their_limits_or_two_equal_buttons_are_refused()
    {
        Assert.NotNull(ConsentTextRules.Clean(new ConsentTexts(new string('x', ConsentTextRules.MaxTitleLength + 1), null, null, null)).Problem);
        Assert.NotNull(ConsentTextRules.Clean(new ConsentTexts(null, new string('x', ConsentTextRules.MaxMessageLength + 1), null, null)).Problem);
        Assert.NotNull(ConsentTextRules.Clean(new ConsentTexts(null, string.Join('\n', Enumerable.Repeat("line", ConsentTextRules.MaxMessageLines + 1)),
            null, null)).Problem);
        Assert.NotNull(ConsentTextRules.Clean(new ConsentTexts(null, null, new string('x', ConsentTextRules.MaxLabelLength + 1), null)).Problem);
        Assert.NotNull(ConsentTextRules.Clean(new ConsentTexts(null, null, "OK", "ok")).Problem);
        Assert.Null(ConsentTextRules.Clean(new ConsentTexts(new string('x', ConsentTextRules.MaxTitleLength), null, "Ja", "Nee")).Problem);
    }

    [Fact]
    public void The_texts_reach_the_rules_of_a_session_only_where_a_workstation_asks_for_consent()
    {
        var texts = new ConsentTexts("Title", "Message", "Yes", "No");
        Assert.Equal(texts, RemoteSessionRules.EffectiveControlRules(EndpointClass.Workstation, true, 30, true, true, texts).ConsentTexts);
        Assert.True(RemoteSessionRules.EffectiveControlRules(EndpointClass.Workstation, false, 30, true, true, texts).ConsentTexts!.IsDefault);
        Assert.True(RemoteSessionRules.EffectiveControlRules(EndpointClass.Server, true, 30, true, true, texts).ConsentTexts!.IsDefault);

        // A stored text beyond the limits (written around the editor) never reaches an endpoint.
        var tooLong = new ConsentTexts(new string('x', 500), "Message", null, null);
        Assert.True(RemoteSessionRules.EffectiveControlRules(EndpointClass.Workstation, true, 30, true, true, tooLong).ConsentTexts!.IsDefault);
    }
}

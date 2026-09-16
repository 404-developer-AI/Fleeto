using Fleeto.Infrastructure.Email;
using Fleeto.Workers.Email;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees that values from agents and users are HTML-encoded in every email, that every email has a plain-text
/// alternative, and that subjects are single-line and follow the tone rules (no exclamation marks, no prefix).
/// </summary>
public sealed class EmailTemplateTests
{
    private const string Hostile = "<script>alert(1)</script>\r\nBcc: victim@example.com";

    private static IEnumerable<EmailContent> AllTemplates()
    {
        var alert = new AlertEmailModel(Hostile, "Critical", Hostile, Hostile, Hostile, new DateTime(2026, 9, 14, 8, 30, 0, DateTimeKind.Utc),
            Hostile, "https://rmm.test.example/endpoints/1", Hostile, new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
        yield return EmailTemplates.AlertOpened(alert);
        yield return EmailTemplates.AlertEscalated(alert);
        yield return EmailTemplates.AlertResolved(alert);
        yield return EmailTemplates.LicenseExpiringSoon(Hostile, new DateTime(2026, 10, 1), "https://rmm.test.example/settings/licensing");
        yield return EmailTemplates.LicenseGracePeriod(Hostile, new DateTime(2026, 10, 1), new DateTime(2026, 10, 15), "https://rmm.test.example/settings/licensing");
        yield return EmailTemplates.LicenseExpired(Hostile, new DateTime(2026, 10, 15), "https://rmm.test.example/settings/licensing");
        yield return EmailTemplates.BackupFailed(Hostile, new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc), Hostile, "https://rmm.test.example/settings/backups");
        yield return EmailTemplates.ScriptRunOnManyEndpoints(Hostile, new ScriptRunEmailModel(Hostile, Hostile, 4, 42, 3, [Hostile, Hostile],
            new DateTime(2026, 9, 16, 10, 15, 0, DateTimeKind.Utc), 10), "https://rmm.test.example/settings/audit-log");
        yield return EmailTemplates.TestEmail(Hostile);
    }

    [Fact]
    public void User_and_agent_values_are_html_encoded()
    {
        foreach (var email in AllTemplates())
        {
            Assert.DoesNotContain("<script>", email.HtmlBody);
            Assert.Contains("&lt;script&gt;", email.HtmlBody);
        }
    }

    [Fact]
    public void Every_email_has_a_plain_text_alternative_with_the_values()
    {
        foreach (var email in AllTemplates())
        {
            Assert.False(string.IsNullOrWhiteSpace(email.TextBody));
            Assert.Contains("<script>alert(1)</script>", email.TextBody);
        }
    }

    [Fact]
    public void Subjects_are_single_line_without_prefix_or_exclamation_marks()
    {
        foreach (var email in AllTemplates())
        {
            Assert.DoesNotContain('\n', email.Subject);
            Assert.DoesNotContain('\r', email.Subject);
            Assert.DoesNotContain('!', email.Subject);
            Assert.False(email.Subject.StartsWith('['));
        }
    }

    [Fact]
    public void Texts_contain_no_exclamation_marks_and_the_card_uses_the_brand_color()
    {
        var safe = new AlertEmailModel("SRV-DC01 has less than 10% free disk space on C:.", "Warning", "SRV-DC01", "Acme", "Monitoring",
            DateTime.UtcNow, "", "https://rmm.test.example/endpoints/1");
        var emails = new[]
        {
            EmailTemplates.AlertOpened(safe), EmailTemplates.AlertResolved(safe), EmailTemplates.TestEmail("rmm.test.example"),
            EmailTemplates.LicenseExpired("rmm.test.example", DateTime.UtcNow, "https://rmm.test.example/settings/licensing")
        };

        foreach (var email in emails)
        {
            Assert.DoesNotContain('!', email.TextBody);
            Assert.DoesNotContain('!', email.HtmlBody.Replace("<!doctype html>", string.Empty));
            Assert.Contains("#0F766E", email.HtmlBody);
            Assert.Contains("Fleeto", email.HtmlBody);
        }
    }
}

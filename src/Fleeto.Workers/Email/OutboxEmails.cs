using Fleeto.Core.Entities;

namespace Fleeto.Workers.Email;

/// <summary>Builds outbox rows that fit the column limits.</summary>
public static class OutboxEmails
{
    public const string CategoryLicense = "license";
    public const string CategoryBackup = "backup";
    public const string CategoryCredential = "credential";

    public static OutboxEmail Create(string toAddress, EmailContent content, string category, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ToAddress = toAddress,
        Subject = Truncate(content.Subject, 500),
        HtmlBody = content.HtmlBody,
        TextBody = content.TextBody,
        Category = Truncate(category, 50),
        Attempts = 0,
        NextAttemptAt = now,
        CreatedAt = now
    };

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

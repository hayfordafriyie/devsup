using System.Net;
using System.Net.Mail;
using DevSup.Core.Models;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Email;

public sealed class SmtpSettings
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string From { get; init; } = "devsup@localhost";
    public string FromName { get; init; } = "DevSup";
    public bool EnableSsl { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class EmailWorkerOptions
{
    public int IntervalSeconds { get; init; } = 15;
    public int BatchSize { get; init; } = 25;
    public int MaxAttempts { get; init; } = 5;
}

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// SMTP transport. Kept deliberately thin: the outbox processor owns retries and
/// status, this type only attempts a single delivery.
/// </summary>
public sealed class SmtpEmailSender(SmtpSettings settings, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            Timeout = settings.TimeoutSeconds * 1000
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.From, settings.FromName),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true
        };
        mail.To.Add(message.To);

        logger.LogInformation("Sending outbox email {MessageId} to {Recipient}", message.Id, message.To);
        await client.SendMailAsync(mail, ct);
    }
}
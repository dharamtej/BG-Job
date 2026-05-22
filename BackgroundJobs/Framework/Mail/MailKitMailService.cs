using CareerPanda.Framework.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CareerPanda.Framework.Mail;

public class MailKitMailService : IMailService
{
    private readonly MailSettingsConfig _settings;
    private readonly ILogger<MailKitMailService> _logger;

    public MailKitMailService(Config config, ILogger<MailKitMailService> logger)
    {
        _settings = config.MailSettingsConfig;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.EMailId))
        {
            _logger.LogWarning("Mail not configured. Would send to {To}: {Subject}", to, subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.DisplayName, _settings.EMailId));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(_settings.Host, _settings.Port,
            _settings.EnableSSL ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, cancellationToken);
        if (!string.IsNullOrEmpty(_settings.Password))
            await client.AuthenticateAsync(_settings.EMailId, _settings.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

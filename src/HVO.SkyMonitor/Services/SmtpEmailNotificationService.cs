using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Services;

/// <summary>
/// SMTP-backed implementation used by diagnostics tests.
/// </summary>
public sealed class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationService(IOptions<SmtpOptions> options, ILogger<SmtpEmailNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.From, _options.FromDisplayName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };

        message.To.Add(recipient);

        _logger.LogInformation(
            "Sending SMTP test email to {Recipient} via {Host}:{Port}",
            recipient,
            _options.Host,
            _options.Port);

        var sendTask = client.SendMailAsync(message);
        await sendTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

using System;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Components.Account;

/// <summary>
/// Bridges ASP.NET Identity email flows to the shared SMTP notification service.
/// </summary>
internal sealed class SmtpIdentityEmailSender(
    IEmailNotificationService emailNotificationService,
    ILogger<SmtpIdentityEmailSender> logger) : IEmailSender<ApplicationUser>
{
    private readonly IEmailNotificationService _emailNotificationService = emailNotificationService;
    private readonly ILogger<SmtpIdentityEmailSender> _logger = logger;

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationLink);

        return SendEmailAsync(
            email,
            "Confirm your SkyMonitor account",
            BuildLinkBody(user, confirmationLink, "Please confirm your email address"));
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(resetLink);

        return SendEmailAsync(
            email,
            "Reset your SkyMonitor password",
            BuildLinkBody(user, resetLink, "We received a password reset request"));
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(resetCode);

        return SendEmailAsync(
            email,
            "SkyMonitor password reset code",
            BuildCodeBody(user, resetCode));
    }

    private async Task SendEmailAsync(string recipient, string subject, string body)
    {
        try
        {
            await _emailNotificationService.SendAsync(recipient, subject, body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send identity email {Subject} to {Recipient}", subject, recipient);
            throw;
        }
    }

    private static string BuildLinkBody(ApplicationUser user, string link, string intro)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormattableString.Invariant($"Hello {user.UserName},"));
        builder.AppendLine();
        builder.AppendLine(intro + ".");
        builder.AppendLine();
        builder.AppendLine("Follow this link:");
        builder.AppendLine(link);
        builder.AppendLine();
        builder.AppendLine("If you did not initiate this request, you can safely ignore this email.");
        builder.AppendLine();
        builder.AppendLine("— SkyMonitor");
        return builder.ToString();
    }

    private static string BuildCodeBody(ApplicationUser user, string code)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormattableString.Invariant($"Hello {user.UserName},"));
        builder.AppendLine();
        builder.AppendLine("Use the following code to finish resetting your SkyMonitor password:");
        builder.AppendLine(code);
        builder.AppendLine();
        builder.AppendLine("If you did not initiate this request, you can safely ignore this email.");
        builder.AppendLine();
        builder.AppendLine("— SkyMonitor");
        return builder.ToString();
    }
}

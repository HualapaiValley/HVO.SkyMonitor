using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender<ApplicationUser>
{
    private readonly ILogger<LoggingEmailSender> _logger = logger;

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        _logger.LogInformation("Pretending to send confirmation link to {Email}", email);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        _logger.LogInformation("Pretending to send password reset link to {Email}", email);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        _logger.LogInformation("Pretending to send password reset code to {Email}", email);
        return Task.CompletedTask;
    }
}

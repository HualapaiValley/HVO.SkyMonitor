using System.Threading;
using System.Threading.Tasks;

namespace HVO.SkyMonitor.Services;

/// <summary>
/// Sends email notifications via SMTP.
/// </summary>
public interface IEmailNotificationService
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}

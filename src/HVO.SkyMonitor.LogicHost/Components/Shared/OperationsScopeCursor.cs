using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

// Navigation cursors are UI state, scoped to the current actor and list. The underlying
// service still authorizes every read; a cursor never grants resource access.
internal sealed class OperationsScopeCursor(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("LogicHost.OperationsScopeCursor.v1");

    internal string? Protect(string? cursor, string userId, Guid? observatoryId)
        => cursor is null ? null : protector.Protect(JsonSerializer.Serialize(new Envelope(userId, observatoryId, cursor)));

    internal string? Read(string? token, string userId, Guid? observatoryId)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token.Length <= 4096)
        {
            try
            {
                var value = JsonSerializer.Deserialize<Envelope>(protector.Unprotect(token));
                if (value is not null && value.UserId == userId && value.ObservatoryId == observatoryId
                    && !string.IsNullOrWhiteSpace(value.Cursor) && value.Cursor.Length <= 2048)
                    return value.Cursor;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
            {
                // Treat tampered, expired-key, and obsolete navigation state identically.
            }
        }
        throw new ArgumentException("The scope navigation page is invalid.", nameof(token));
    }

    private sealed record Envelope(string UserId, Guid? ObservatoryId, string Cursor);
}

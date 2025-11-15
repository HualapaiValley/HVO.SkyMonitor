namespace HVO.SkyMonitor.Common.Security.SignedTickets;

/// <summary>
/// Service for generating and validating HMAC-signed tickets for time-limited endpoint access.
/// </summary>
public interface ISignedTicketService
{
    /// <summary>
    /// Generates a signed ticket string from the provided ticket data.
    /// Format: {base64url(ticket_data)}.{base64url(hmac_signature)}
    /// </summary>
    /// <param name="ticket">Ticket data to sign.</param>
    /// <returns>Base64-URL encoded signed ticket string.</returns>
    string GenerateSignedTicket(SignedTicket ticket);

    /// <summary>
    /// Validates a signed ticket string against the provided request context.
    /// </summary>
    /// <param name="signedTicket">The signed ticket string (from 'st' query parameter).</param>
    /// <param name="httpMethod">Current HTTP method (GET, POST, etc.).</param>
    /// <param name="path">Current request path.</param>
    /// <param name="query">Current canonical query string (optional).</param>
    /// <returns>True if ticket is valid and not expired; false otherwise.</returns>
    bool ValidateSignedTicket(string signedTicket, string httpMethod, string path, string? query = null);

    /// <summary>
    /// Validates and extracts ticket data from a signed ticket string.
    /// </summary>
    /// <param name="signedTicket">The signed ticket string.</param>
    /// <param name="httpMethod">Current HTTP method.</param>
    /// <param name="path">Current request path.</param>
    /// <param name="query">Current canonical query string (optional).</param>
    /// <param name="ticket">Output ticket data if validation succeeds.</param>
    /// <returns>True if valid; false otherwise.</returns>
    bool TryValidateAndExtractTicket(string signedTicket, string httpMethod, string path, string? query, out SignedTicket? ticket);
}

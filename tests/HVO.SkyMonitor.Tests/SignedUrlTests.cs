using HVO.SkyMonitor.Common.Security.SignedTickets;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests;

[TestClass]
public class SignedUrlTests
{
    private ISignedTicketService? _signedTicketService;

    [TestInitialize]
    public void Setup()
    {
        var options = Options.Create(new SignedTicketOptions
        {
            Secret = "test-secret-key-minimum-256-bits-long-for-hmac-sha256-security",
            DefaultTtlSeconds = 300,
            MaxClockSkewSeconds = 30,
            AllowedPaths = new[] { "/api/v1.0/frame/", "/api/v1.0/image/" },
            EnforceAllowedPaths = true
        });

        _signedTicketService = new SignedTicketService(options);
    }

    [TestMethod]
    public void GenerateSignedTicket_CreatesValidTicket()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = "format=jpeg"
        };

        // Act
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);

        // Assert
        Assert.IsNotNull(signedTicket);
        Assert.Contains(".", signedTicket);
    }

    [TestMethod]
    public void ValidateSignedTicket_AcceptsValidTicket()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = ""
        };
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);

        // Act
        var isValid = _signedTicketService.ValidateSignedTicket(
            signedTicket,
            "GET",
            "/api/v1.0/frame/latest",
            ""
        );

        // Assert
        Assert.IsTrue(isValid);
    }

    [TestMethod]
    public void ValidateSignedTicket_RejectsExpiredTicket()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(-60), // Expired 1 minute ago
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = ""
        };
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);

        // Act
        var isValid = _signedTicketService.ValidateSignedTicket(
            signedTicket,
            "GET",
            "/api/v1.0/frame/latest",
            ""
        );

        // Assert
        Assert.IsFalse(isValid);
    }

    [TestMethod]
    public void ValidateSignedTicket_RejectsTamperedSignature()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = ""
        };
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);
        var tamperedTicket = signedTicket.Replace("A", "B"); // Tamper with signature

        // Act
        var isValid = _signedTicketService.ValidateSignedTicket(
            tamperedTicket,
            "GET",
            "/api/v1.0/frame/latest",
            ""
        );

        // Assert
        Assert.IsFalse(isValid);
    }

    [TestMethod]
    public void ValidateSignedTicket_RejectsWrongHttpMethod()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = ""
        };
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);

        // Act - try to validate with POST instead of GET
        var isValid = _signedTicketService.ValidateSignedTicket(
            signedTicket,
            "POST", // Wrong method
            "/api/v1.0/frame/latest",
            ""
        );

        // Assert
        Assert.IsFalse(isValid);
    }

    [TestMethod]
    public void ValidateSignedTicket_RejectsWrongPath()
    {
        // Arrange
        var ticket = new SignedTicket
        {
            Version = 1,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            SubjectId = "user123",
            HttpMethod = "GET",
            Path = "/api/v1.0/frame/latest",
            Query = ""
        };
        var signedTicket = _signedTicketService!.GenerateSignedTicket(ticket);

        // Act - try to validate with different path
        var isValid = _signedTicketService.ValidateSignedTicket(
            signedTicket,
            "GET",
            "/api/v1.0/frame/other", // Wrong path
            ""
        );

        // Assert
        Assert.IsFalse(isValid);
    }
}

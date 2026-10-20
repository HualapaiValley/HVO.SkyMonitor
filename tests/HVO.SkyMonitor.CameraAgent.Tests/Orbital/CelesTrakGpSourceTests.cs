using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpSourceTests
{
    [TestMethod]
    public void SourceConstantsNameTheSingleApprovedActiveGroupRequest()
    {
        // The string and size constants are compile-time values; their wire and provenance use is asserted where they
        // are consumed (client request, normalizer source descriptor and bound tests).
        Assert.AreEqual(TimeSpan.FromSeconds(30), CelesTrakGpSource.MaximumRequestTimeout);

        var request = CelesTrakGpSource.RequestUri;
        Assert.AreEqual("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON", request.OriginalString);
        Assert.AreEqual(Uri.UriSchemeHttps, request.Scheme);
        Assert.AreEqual("celestrak.org", request.Host);
        Assert.AreEqual(443, request.Port);
        Assert.AreEqual("?GROUP=active&FORMAT=JSON", request.Query);
        Assert.AreEqual(string.Empty, request.UserInfo);
        Assert.AreEqual(string.Empty, request.Fragment);
        Assert.AreEqual("https://celestrak.org/usage-policy.php", CelesTrakGpSource.TermsUri.OriginalString);
        Assert.AreEqual(string.Empty, CelesTrakGpSource.TermsUri.Query);
    }

    [TestMethod]
    public void TermsAndLineageFitTheSharedSourceDescriptor()
    {
        Assert.IsLessThanOrEqualTo(2048, CelesTrakGpSource.TermsReference.Length);
        Assert.IsFalse(CelesTrakGpSource.TermsReference.Any(char.IsControl));
        Assert.Contains("non-200", CelesTrakGpSource.TermsReference);
        Assert.Contains("two hours", CelesTrakGpSource.TermsReference);
        Assert.Contains("no feed redistribution", CelesTrakGpSource.TermsReference);
        Assert.IsLessThanOrEqualTo(256, CelesTrakGpSource.UnderlyingLineage.Length);
        Assert.StartsWith("USSF public GP", CelesTrakGpSource.UnderlyingLineage);
    }

    [TestMethod]
    public void PrimaryHandlerDisablesRedirectsCookiesDecompressionCredentialsAndTracePropagation()
    {
        using var handler = CelesTrakGpSource.CreatePrimaryHandler();
        var sockets = handler as SocketsHttpHandler;
        Assert.IsNotNull(sockets);
        Assert.IsFalse(sockets.AllowAutoRedirect);
        Assert.IsFalse(sockets.UseCookies);
        Assert.AreEqual(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.IsFalse(sockets.PreAuthenticate);
        Assert.IsNull(sockets.Credentials);
        Assert.IsNull(sockets.DefaultProxyCredentials);
        Assert.AreEqual(32, sockets.MaxResponseHeadersLength);
        Assert.AreEqual(0, sockets.MaxResponseDrainSize);
        Assert.IsNull(sockets.ActivityHeadersPropagator);
        Assert.AreEqual(1, sockets.MaxConnectionsPerServer);
        Assert.AreEqual(TimeSpan.FromSeconds(30), sockets.ConnectTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(1), sockets.PooledConnectionIdleTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
        Assert.IsNull(sockets.SslOptions.RemoteCertificateValidationCallback, "TLS validation stays at the platform default.");
        Assert.IsNull(sockets.SslOptions.ClientCertificates);
        Assert.IsNull(sockets.SslOptions.LocalCertificateSelectionCallback);
        Assert.IsNull(sockets.ConnectCallback);
        Assert.IsNull(sockets.PlaintextStreamFilter);
    }

    [TestMethod]
    public void EachPrimaryHandlerIsANewInstance()
    {
        using var first = CelesTrakGpSource.CreatePrimaryHandler();
        using var second = CelesTrakGpSource.CreatePrimaryHandler();
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    [DataRow("\"abc123\"", "\"abc123\"")]
    [DataRow("W/\"weak-tag\"", "W/\"weak-tag\"")]
    [DataRow("Mon, 19 Oct 2026 17:00:00 GMT", "Mon, 19 Oct 2026 17:00:00 GMT")]
    [DataRow("~ !printable-ascii-bounds", "~ !printable-ascii-bounds")]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("   ", null)]
    public void ValidatorsAreKeptOnlyAsBoundedPrintableAscii(string? value, string? expected) =>
        Assert.AreEqual(expected, CelesTrakGpSource.SanitizeValidator(value, 64));

    [TestMethod]
    [DataRow(0x0D, DisplayName = "carriage return")]
    [DataRow(0x0A, DisplayName = "line feed")]
    [DataRow(0x09, DisplayName = "tab")]
    [DataRow(0x00, DisplayName = "nul")]
    [DataRow(0x1F, DisplayName = "unit separator")]
    [DataRow(0x7F, DisplayName = "delete")]
    [DataRow(0x85, DisplayName = "next line")]
    [DataRow(0xE9, DisplayName = "latin small e acute")]
    [DataRow(0xD83D, DisplayName = "lone high surrogate")]
    [DataRow(0x2028, DisplayName = "line separator")]
    public void ValidatorsContainingAnyNonPrintableAsciiCharacterAreDropped(int codeUnit)
    {
        var value = "\"tag" + (char)codeUnit + "Injected: 1\"";
        Assert.IsNull(CelesTrakGpSource.SanitizeValidator(value, 64));
    }

    [TestMethod]
    public void ValidatorsAreDroppedRatherThanTruncated()
    {
        Assert.AreEqual(new string('a', 64), CelesTrakGpSource.SanitizeValidator(new string('a', 64), 64));
        Assert.IsNull(CelesTrakGpSource.SanitizeValidator(new string('a', 65), 64));
        Assert.IsNull(CelesTrakGpSource.SanitizeValidator(new string('e', 1025), 1024));
        Assert.IsNotNull(CelesTrakGpSource.SanitizeValidator(new string('e', 1024), 1024));
    }

    [TestMethod]
    public void SingleHeaderValueRequiresExactlyOneValue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.IsNull(CelesTrakGpSource.SingleHeaderValue(response.Headers, "ETag"));
        response.Headers.TryAddWithoutValidation("ETag", "\"one\"");
        Assert.AreEqual("\"one\"", CelesTrakGpSource.SingleHeaderValue(response.Headers, "ETag"));
        response.Headers.TryAddWithoutValidation("ETag", "\"two\"");
        Assert.IsNull(CelesTrakGpSource.SingleHeaderValue(response.Headers, "ETag"));
    }
}

using System.Net;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using Minio.DataModel.Result;
using Minio.Exceptions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class MinioObjectVerificationTests
{
    [TestMethod]
    public void ObjectNotFoundException_IsMissing()
    {
        MinioObjectVerification.IsNotFound(new ObjectNotFoundException()).Should().BeTrue();
    }

    [TestMethod]
    public void NoSuchKeyResponse_IsMissing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://minio.test/object");
        using var httpResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
        using var response = new ResponseResult(request, httpResponse);
        var exception = new ErrorResponseException(new ErrorResponse { Code = "NoSuchKey" }, response);

        MinioObjectVerification.IsNotFound(exception).Should().BeTrue();
    }

    [TestMethod]
    public void TransientMinioResponse_IsRetryable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://minio.test/object");
        using var httpResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var response = new ResponseResult(request, httpResponse);
        var exception = new ErrorResponseException(new ErrorResponse { Code = "SlowDown" }, response);

        MinioObjectVerification.IsNotFound(exception).Should().BeFalse();
    }
}

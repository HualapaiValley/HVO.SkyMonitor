using System.Net;
using Amazon.S3;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class S3ObjectStoreExceptionMapperTests
{
    [TestMethod]
    public void NoSuchKeyResponse_IsMissing()
    {
        var exception = new AmazonS3Exception("missing")
        {
            ErrorCode = "NoSuchKey",
            StatusCode = HttpStatusCode.NotFound
        };

        S3ObjectStoreExceptionMapper.Normalize(exception, "stat", false).Kind
            .Should().Be(ObjectStoreFailureKind.MissingObject);
    }

    [TestMethod]
    public void ProviderResponses_MapByStableCodeAndStatus()
    {
        var throttled = new AmazonS3Exception("slow")
        {
            ErrorCode = "SlowDown",
            StatusCode = HttpStatusCode.ServiceUnavailable
        };
        var missingBucket = new AmazonS3Exception("missing")
        {
            ErrorCode = "NoSuchBucket",
            StatusCode = HttpStatusCode.NotFound
        };

        S3ObjectStoreExceptionMapper.Normalize(throttled, "list", false).Kind
            .Should().Be(ObjectStoreFailureKind.Throttled);
        S3ObjectStoreExceptionMapper.Normalize(missingBucket, "list", false).Kind
            .Should().Be(ObjectStoreFailureKind.MissingBucket);

        S3ObjectStoreExceptionMapper.Normalize(
            new AmazonS3Exception("wrong region")
            {
                ErrorCode = "PermanentRedirect",
                StatusCode = HttpStatusCode.MovedPermanently
            }, "stat", false).Kind.Should().Be(ObjectStoreFailureKind.Unsupported);
        S3ObjectStoreExceptionMapper.Normalize(
            new AmazonS3Exception("unknown server failure")
            {
                ErrorCode = "UnknownProviderFailure",
                StatusCode = HttpStatusCode.HttpVersionNotSupported
            }, "put", true).Kind.Should().Be(ObjectStoreFailureKind.Ambiguous);
    }

    [TestMethod]
    public void ProviderExceptions_MapWithoutResponseMetadata()
    {
        var authentication = S3ObjectStoreExceptionMapper.Normalize(
            new AmazonS3Exception("invalid") { ErrorCode = "InvalidAccessKeyId" }, "stat", false);
        authentication.Kind.Should().Be(ObjectStoreFailureKind.Authentication);
        authentication.InnerException.Should().BeNull();
        S3ObjectStoreExceptionMapper.Normalize(
            new AmazonS3Exception("denied") { ErrorCode = "AccessDenied" }, "stat", false).Kind
            .Should().Be(ObjectStoreFailureKind.Authorization);
        S3ObjectStoreExceptionMapper.Normalize(
            new AmazonS3Exception("stale") { ErrorCode = "PreconditionFailed" }, "get", false).Kind
            .Should().Be(ObjectStoreFailureKind.Precondition);
        S3ObjectStoreExceptionMapper.Normalize(new System.NotImplementedException(), "copy", false).Kind
            .Should().Be(ObjectStoreFailureKind.Unsupported);
    }

    [TestMethod]
    public void MutatingTimeout_IsAmbiguousButReadTimeoutIsNot()
    {
        S3ObjectStoreExceptionMapper.Normalize(new TimeoutException(), "put", true).Kind
            .Should().Be(ObjectStoreFailureKind.Ambiguous);
        S3ObjectStoreExceptionMapper.Normalize(new TimeoutException(), "get", false).Kind
            .Should().Be(ObjectStoreFailureKind.Timeout);
        S3ObjectStoreExceptionMapper.Normalize(new OperationCanceledException(), "get", false).Kind
            .Should().Be(ObjectStoreFailureKind.Canceled);
    }
}

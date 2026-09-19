using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class FileSystemFaultTests
{
    private static int Errno(int errno) => unchecked((int)0x80070000) | errno;

    [TestMethod]
    [DataRow(17, FileSystemFaultKind.AlreadyExists)]
    [DataRow(2, FileSystemFaultKind.NotFound)]
    [DataRow(13, FileSystemFaultKind.PermissionDenied)]
    [DataRow(1, FileSystemFaultKind.PermissionDenied)]
    [DataRow(28, FileSystemFaultKind.NoSpace)]
    [DataRow(122, FileSystemFaultKind.NoSpace)]
    [DataRow(18, FileSystemFaultKind.CrossDevice)]
    [DataRow(5, FileSystemFaultKind.Io)]
    public void From_ClassifiesRuntimeIoExceptionsByErrno(int errno, FileSystemFaultKind expected)
    {
        var fault = FileSystemFaultException.From("op", "p", new IOException("x", Errno(errno)));
        Assert.AreEqual(expected, fault.Kind);
        Assert.AreEqual("op", fault.Operation);
        Assert.AreEqual("p", fault.Path);
        Assert.IsInstanceOfType<IOException>(fault.InnerException);
    }

    [TestMethod]
    public void From_ClassifiesFrameworkExceptionTypes()
    {
        Assert.AreEqual(FileSystemFaultKind.Cancelled, FileSystemFaultException.From("op", "p", new OperationCanceledException()).Kind);
        Assert.AreEqual(FileSystemFaultKind.PermissionDenied, FileSystemFaultException.From("op", "p", new UnauthorizedAccessException()).Kind);
        Assert.AreEqual(FileSystemFaultKind.NotFound, FileSystemFaultException.From("op", "p", new FileNotFoundException()).Kind);
        Assert.AreEqual(FileSystemFaultKind.NotFound, FileSystemFaultException.From("op", "p", new DirectoryNotFoundException()).Kind);
        Assert.AreEqual(FileSystemFaultKind.Io, FileSystemFaultException.From("op", "p", new InvalidOperationException()).Kind);
    }

    [TestMethod]
    public void From_PreservesAnExistingFaultKindWhenRewrapping()
    {
        var inner = new FileSystemFaultException(FileSystemFaultKind.Containment, "inner", "p");
        var outer = FileSystemFaultException.From("outer", "p", inner);
        Assert.AreEqual(FileSystemFaultKind.Containment, outer.Kind);
        Assert.AreEqual("outer", outer.Operation);
        Assert.AreSame(inner, outer.InnerException);
    }

    [TestMethod]
    public void ConventionalConstructors_ProduceAnIoFactForAnUnknownOperation()
    {
        var bare = new FileSystemFaultException();
        Assert.AreEqual(FileSystemFaultKind.Io, bare.Kind);
        Assert.AreEqual("unknown", bare.Operation);
        Assert.AreEqual(string.Empty, bare.Path);

        var withMessage = new FileSystemFaultException("custom");
        Assert.AreEqual("custom", withMessage.Message);
        Assert.AreEqual(FileSystemFaultKind.Io, withMessage.Kind);

        var inner = new IOException("inner");
        var wrapped = new FileSystemFaultException("outer", inner);
        Assert.AreSame(inner, wrapped.InnerException);
        Assert.AreEqual(FileSystemFaultKind.Io, wrapped.Kind);
    }

    [TestMethod]
    public void From_RejectsANullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => FileSystemFaultException.From("op", "p", null!));
    }

    [TestMethod]
    public void Message_NamesTheFactAndOperationButNeverContent()
    {
        var fault = new FileSystemFaultException(FileSystemFaultKind.NoSpace, "publish-write", "/srv/objects/bucket/key.bin");
        Assert.AreEqual("Filesystem operation 'publish-write' failed with 'NoSpace'.", fault.Message);
        Assert.IsFalse(fault.Message.Contains("/srv", StringComparison.Ordinal), "the path is a property, not part of the message, so callers decide whether it is logged");
    }
}

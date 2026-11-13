using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStateResetRefusalTests
{
    /// <summary>
    /// The deep-sky reset refusal names the exact recovery commands (#1824 N2): after disabling the family, uninstall,
    /// reset the state with its confirmation, and install again with the same immutable inputs. It must not read as
    /// "reinstall", which starts a preserved instance rather than installing onto reset state.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DeepSkyResetRefusalNamesTheRecoveryCommands(bool uninstalled)
    {
        var instanceId = Guid.Parse("1b4e28ba-2fa1-11d2-883f-0016d3cca427");
        var id = instanceId.ToString("D");
        var program = InstallerProgram.Current;

        var message = CameraAgentStateResetManager.DeepSkyResetRefusal(instanceId, uninstalled);

        var disable = $"'{program} catalog select --instance-id {id} --catalog-family deep-sky --catalog-version disabled'";
        var uninstall = $"'{program} cameraagent uninstall --instance-id {id}'";
        var reset = $"'{program} cameraagent reset-state --instance-id {id} --confirm-instance-id {id}'";
        var install = $"'{program} cameraagent install --instance-id {id} <same immutable inputs>'";
        foreach (var command in new[] { disable, uninstall, reset, install })
            StringAssert.Contains(message, command, StringComparison.Ordinal);
        Assert.IsTrue(message.IndexOf(disable, StringComparison.Ordinal) < message.IndexOf(uninstall, StringComparison.Ordinal) &&
                      message.IndexOf(uninstall, StringComparison.Ordinal) < message.IndexOf(reset, StringComparison.Ordinal) &&
                      message.IndexOf(reset, StringComparison.Ordinal) < message.IndexOf(install, StringComparison.Ordinal),
            message);
        Assert.IsFalse(message.Contains("reset and reinstall", StringComparison.Ordinal), message);
        StringAssert.Contains(message, "reselect OpenNGC", StringComparison.Ordinal);

        // Only an uninstalled instance is first started again from its preserved state, before the family is disabled.
        var restart = $"'{program} cameraagent reinstall --instance-id {id}'";
        Assert.AreEqual(uninstalled, message.Contains(restart, StringComparison.Ordinal), message);
        if (uninstalled)
            Assert.IsTrue(message.IndexOf(restart, StringComparison.Ordinal) < message.IndexOf(disable, StringComparison.Ordinal), message);
    }
}

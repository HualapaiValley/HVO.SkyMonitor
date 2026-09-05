using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// The evidence sweep partitions durable execution statuses into terminal ones it exports and active ones that hold
/// its barrier. A status in neither list would be invisible to both: the sweep would never export it and the barrier
/// would never wait for it, so the cursor would silently pass those executions forever. This asserts the partition
/// is exactly the set the durable type admits.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingGraphExecutionStatusCoverageTests
{
    [TestMethod]
    public void EveryDurableExecutionStatusIsEitherTerminalOrActiveForTheEvidenceSweep()
    {
        var partitioned = SqliteCaptureProcessingStore.TerminalStatuses
            .Concat(SqliteCaptureProcessingStore.ActiveStatuses)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            Enum.GetNames<ProcessingGraphExecutionStatus>().Order(StringComparer.Ordinal).ToArray(),
            partitioned,
            "Adding a durable execution status without classifying it would make the evidence sweep skip it.");
        Assert.IsEmpty(
            SqliteCaptureProcessingStore.TerminalStatuses.Intersect(
                SqliteCaptureProcessingStore.ActiveStatuses, StringComparer.Ordinal),
            "A status cannot be both terminal and active.");
    }
}

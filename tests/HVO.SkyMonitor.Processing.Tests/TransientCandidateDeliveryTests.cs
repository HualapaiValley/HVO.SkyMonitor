using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientCandidateDeliveryTests
{
    [TestMethod]
    public void FinalizationReceiptRejectsForeignOnlyObservations()
    {
        var transientEvent = TransientTestData.CreateEvent();
        var candidateId = Guid.Parse("a0000000-0000-0000-0000-000000000099");
        var receipt = CreateReceipt(candidateId, transientEvent);

        var validation = TransientCandidateDeliveryJson.Validate(receipt);

        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(TransientContractReasonCodes.InvalidLineage, validation.ReasonCode);
        Assert.AreEqual("event.observations[].extraction.originatingCandidateId", validation.FieldPath);
    }

    [TestMethod]
    public void FinalizationReceiptAcceptsMixedCandidateObservations()
    {
        var transientEvent = TransientTestData.CreateEvent();
        var candidateId = transientEvent.Observations[0].Extraction.OriginatingCandidateId!.Value;
        var observations = transientEvent.Observations.ToArray();
        var foreignCandidateId = Guid.Parse("a0000000-0000-0000-0000-000000000099");
        observations[1] = observations[1] with
        {
            Extraction = observations[1].Extraction with { OriginatingCandidateId = foreignCandidateId }
        };
        var receipt = CreateReceipt(candidateId, transientEvent with { Observations = observations });

        var validation = TransientCandidateDeliveryJson.Validate(receipt);

        Assert.IsTrue(validation.IsValid, $"{validation.ReasonCode}:{validation.FieldPath}");
    }

    private static TransientFinalizationReceiptV1 CreateReceipt(
        Guid candidateId,
        TransientEventV1 transientEvent)
    {
        var receipt = new TransientFinalizationReceiptV1(
            TransientFinalizationReceiptV1.CurrentSchemaVersion,
            candidateId,
            transientEvent.EventId,
            transientEvent,
            new string('0', 64));
        return receipt with
        {
            ReceiptIdentitySha256 = TransientCandidateDeliveryJson.ComputeFinalizationIdentitySha256(receipt)
        };
    }
}

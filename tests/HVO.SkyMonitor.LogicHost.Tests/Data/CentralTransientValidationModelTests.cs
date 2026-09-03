using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTransientValidationModelTests
{
    [TestMethod]
    public void ModelDefinesImmutableVersionedEventAndJobIdentityConstraints()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=skymonitor-model;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var model = context.Model;
        var transientEvent = model.FindEntityType(typeof(CentralTransientEventRecord))!;
        var version = model.FindEntityType(typeof(CentralTransientEventVersionRecord))!;
        var observation = model.FindEntityType(typeof(CentralTransientObservationRecord))!;
        var source = model.FindEntityType(typeof(CentralTransientObservationSourceReference))!;
        var observationLink = model.FindEntityType(typeof(CentralTransientEventVersionObservation))!;
        var background = model.FindEntityType(typeof(CentralTransientObservationBackgroundReference))!;
        var assessment = model.FindEntityType(typeof(CentralTransientAssessmentRecord))!;
        var review = model.FindEntityType(typeof(CentralTransientReviewRecord))!;
        var reviewLink = model.FindEntityType(typeof(CentralTransientEventVersionReview))!;
        var current = model.FindEntityType(typeof(CentralTransientEventCurrent))!;
        var reviewMutation = model.FindEntityType(typeof(CentralTransientReviewMutationRecord))!;
        var derivativeJob = model.FindEntityType(typeof(CentralTransientDerivativeJob))!;
        var derivativeIntent = model.FindEntityType(typeof(CentralTransientDerivativeOutputIntent))!;
        var derivative = model.FindEntityType(typeof(CentralTransientDerivativeRecord))!;
        var derivativeSource = model.FindEntityType(typeof(CentralTransientDerivativeSourceReference))!;
        var derivativeBackground = model.FindEntityType(typeof(CentralTransientDerivativeBackgroundReference))!;
        var derivativeLink = model.FindEntityType(typeof(CentralTransientEventVersionDerivative))!;
        var validationJob = model.FindEntityType(typeof(CentralTransientValidationJob))!;
        var identitySlot = model.FindEntityType(typeof(CentralTransientValidationIdentitySlot))!;
        var contextDependency = model.FindEntityType(typeof(CentralTransientContextDependency))!;
        var outcomeVersion = model.FindEntityType(typeof(CentralTransientValidationOutcomeVersion))!;

        Assert.IsTrue(transientEvent.GetKeys().Any(key => key.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(CentralTransientEventRecord.AgentId), nameof(CentralTransientEventRecord.EventId)])));
        AssertUniqueIndex(version, nameof(CentralTransientEventVersionRecord.CentralTransientEventId),
            nameof(CentralTransientEventVersionRecord.Version));
        AssertUniqueIndex(version, nameof(CentralTransientEventVersionRecord.CentralTransientEventId),
            nameof(CentralTransientEventVersionRecord.PreviousEventVersionId));
        Assert.IsTrue(version.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientEventVersionRecord)
            && foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(CentralTransientEventVersionRecord.CentralTransientEventId),
                nameof(CentralTransientEventVersionRecord.PreviousVersionNumber),
                nameof(CentralTransientEventVersionRecord.PreviousEventVersionId),
                nameof(CentralTransientEventVersionRecord.PreviousVersionCreatedUtc)])));
        Assert.IsTrue(observation.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientObservationSourceReference)
            && !foreignKey.IsRequiredDependent
            && foreignKey.IsRequired
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict));
        Assert.IsTrue(source.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralArtifact)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict));
        AssertUniqueIndex(observationLink, nameof(CentralTransientEventVersionObservation.EventVersionId),
            nameof(CentralTransientEventVersionObservation.ObservationId));
        AssertUniqueIndex(background, nameof(CentralTransientObservationBackgroundReference.ObservationId),
            nameof(CentralTransientObservationBackgroundReference.Ordinal));
        Assert.IsTrue(background.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralArtifact)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict));
        Assert.IsTrue(assessment.GetIndexes().Any(index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(CentralTransientAssessmentRecord.CentralTransientEventId),
                nameof(CentralTransientAssessmentRecord.ProducerSchemaVersion),
                nameof(CentralTransientAssessmentRecord.ProducerKind),
                nameof(CentralTransientAssessmentRecord.ProducerName),
                nameof(CentralTransientAssessmentRecord.ProducerVersion),
                nameof(CentralTransientAssessmentRecord.RecipeIdentitySha256),
                nameof(CentralTransientAssessmentRecord.ExecutionIdentitySha256)])));
        Assert.IsTrue(assessment.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientAssessmentRecord)
            && foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(CentralTransientAssessmentRecord.CentralTransientEventId),
                nameof(CentralTransientAssessmentRecord.SupersedesAssessmentId),
                nameof(CentralTransientAssessmentRecord.SupersedesAssessmentCreatedUtc)])));
        AssertUniqueIndex(review,
            nameof(CentralTransientReviewRecord.CentralTransientEventId),
            nameof(CentralTransientReviewRecord.SupersedesReviewId));
        Assert.IsTrue(review.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientAssessmentRecord)
            && foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(CentralTransientReviewRecord.CentralTransientEventId),
                nameof(CentralTransientReviewRecord.AssessmentId)])));
        AssertUniqueIndex(reviewLink,
            nameof(CentralTransientEventVersionReview.EventVersionId),
            nameof(CentralTransientEventVersionReview.ReviewId));
        Assert.IsTrue(current.FindProperty(nameof(CentralTransientEventCurrent.RowVersion))!.IsConcurrencyToken);
        Assert.IsTrue(current.FindProperty(nameof(CentralTransientEventCurrent.RowVersion))!.ValueGenerated
            == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate);
        AssertUniqueIndex(reviewMutation,
            nameof(CentralTransientReviewMutationRecord.CentralTransientEventId),
            nameof(CentralTransientReviewMutationRecord.ActorIdentity),
            nameof(CentralTransientReviewMutationRecord.IdempotencyKey));
        AssertUniqueIndex(derivativeJob,
            nameof(CentralTransientDerivativeJob.CentralTransientEventId),
            nameof(CentralTransientDerivativeJob.SourceEventVersionId),
            nameof(CentralTransientDerivativeJob.RecipeIdentitySha256),
            nameof(CentralTransientDerivativeJob.OptionsIdentitySha256));
        AssertUniqueIndex(derivativeIntent, nameof(CentralTransientDerivativeOutputIntent.ArtifactId));
        Assert.AreEqual(
            "CONVERT(binary(32), HASHBYTES('SHA2_256', [StorageReference]))",
            derivativeIntent.FindProperty("StorageReferenceSha256")!.GetComputedColumnSql());
        Assert.IsTrue(derivativeIntent.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(["StorageReferenceSha256"])));
        Assert.IsTrue(derivativeIntent.FindProperty(nameof(CentralTransientDerivativeOutputIntent.RowVersion))!
            .IsConcurrencyToken);
        AssertUniqueIndex(derivative,
            nameof(CentralTransientDerivativeRecord.CentralTransientEventId),
            nameof(CentralTransientDerivativeRecord.OutputIdentitySha256));
        Assert.IsTrue(derivativeSource.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientObservationSourceReference)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict));
        Assert.IsTrue(derivativeBackground.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientObservationBackgroundReference)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict));
        AssertUniqueIndex(derivativeLink,
            nameof(CentralTransientEventVersionDerivative.EventVersionId),
            nameof(CentralTransientEventVersionDerivative.DerivativeId));
        AssertUniqueIndex(validationJob, nameof(CentralTransientValidationJob.SubmissionIdentitySha256));
        AssertUniqueIndex(identitySlot, nameof(CentralTransientValidationIdentitySlot.CentralDerivativeJobId),
            nameof(CentralTransientValidationIdentitySlot.Ordinal));
        AssertUniqueIndex(identitySlot, nameof(CentralTransientValidationIdentitySlot.CandidateId));
        AssertUniqueIndex(identitySlot, nameof(CentralTransientValidationIdentitySlot.ObservationId));
        AssertUniqueIndex(identitySlot, nameof(CentralTransientValidationIdentitySlot.AssessmentId));
        AssertUniqueIndex(identitySlot, nameof(CentralTransientValidationIdentitySlot.AssociationIdentitySha256));
        AssertUniqueIndex(validationJob, nameof(CentralTransientValidationJob.ProvisionalCentralDerivativeJobId));
        AssertUniqueIndex(outcomeVersion,
            nameof(CentralTransientValidationOutcomeVersion.CentralDerivativeJobId),
            nameof(CentralTransientValidationOutcomeVersion.Version));
        Assert.IsTrue(contextDependency.GetForeignKeys().Any(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CentralTransientValidationJob) &&
            foreignKey.Properties.Single().Name == nameof(CentralTransientContextDependency.RequiredCentralDerivativeJobId)));
    }

    private static void AssertUniqueIndex(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        params string[] propertyNames)
        => Assert.IsTrue(entity.GetIndexes().Any(index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(propertyNames)));
}

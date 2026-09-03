using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralCloudProcessingModelTests
{
    [TestMethod]
    public void ModelDefinesDurableDesignationAndCanonicalInputConstraints()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=skymonitor-model;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var model = context.Model;
        var designation = model.FindEntityType(typeof(CentralClearReferenceDesignation));
        var canonical = model.FindEntityType(typeof(CentralDerivativeJobCanonicalInput));
        var requirement = model.FindEntityType(typeof(CentralDerivativeJobInputRequirement));
        var job = model.FindEntityType(typeof(CentralDerivativeJob));

        Assert.IsNotNull(designation);
        Assert.AreEqual("CentralClearReferenceDesignations", designation.GetTableName());
        Assert.IsTrue(designation.GetIndexes().Any(index => index.IsUnique &&
            index.Properties.Select(static property => property.Name)
                .SequenceEqual([nameof(CentralClearReferenceDesignation.RegistrationId), nameof(CentralClearReferenceDesignation.RigId)])));
        Assert.IsTrue(designation.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Single().Name == nameof(CentralClearReferenceDesignation.CentralArtifactId) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Restrict));

        Assert.IsNotNull(canonical);
        Assert.AreEqual("CentralDerivativeJobCanonicalInputs", canonical.GetTableName());
        Assert.IsTrue(canonical.GetIndexes().Count(index => index.IsUnique) >= 2);
        Assert.IsTrue(canonical.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Single().Name == nameof(CentralDerivativeJobCanonicalInput.EnvironmentalObservationRecordId) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Restrict));

        Assert.IsNotNull(requirement);
        Assert.IsNotNull(requirement.FindProperty(nameof(CentralDerivativeJobInputRequirement.ExpectedCentralArtifactId)));
        Assert.IsNotNull(job);
        var expectedIdentity = job.FindProperty(nameof(CentralDerivativeJob.ExpectedRecipeIdentitySha256));
        Assert.IsNotNull(expectedIdentity);
        Assert.IsFalse(expectedIdentity.IsNullable);
        Assert.AreEqual(64, expectedIdentity.GetMaxLength());
    }

    [TestMethod]
    public void ModelDefinesSealedGraphExecutionAndImmutableInputConstraints()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=skymonitor-model;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var execution = model.FindEntityType(typeof(CentralProcessingGraphExecution));
        var requirement = model.FindEntityType(typeof(CentralDerivativeJobInputRequirement));
        var selectedArtifact = model.FindEntityType(typeof(CentralDerivativeJobInput));
        var selectedCanonical = model.FindEntityType(typeof(CentralDerivativeJobCanonicalInput));

        Assert.IsNotNull(execution);
        Assert.IsNotNull(execution.FindProperty(nameof(CentralProcessingGraphExecution.ExpandedAtUtc)));
        Assert.IsNotNull(execution.FindProperty(nameof(CentralProcessingGraphExecution.ExpectedSourceCount)));
        Assert.IsNotNull(execution.FindProperty(nameof(CentralProcessingGraphExecution.ExpectedNodeCount)));
        Assert.IsNotNull(execution.FindProperty(nameof(CentralProcessingGraphExecution.ExpectedDependencyCount)));
        Assert.IsNotNull(execution.FindProperty(nameof(CentralProcessingGraphExecution.ExpectedOutputCount)));
        var executionConstraints = execution.GetCheckConstraints().Select(constraint => constraint.Name).ToArray();
        CollectionAssert.Contains(executionConstraints, "CK_CentralProcessingGraphExecutions_Expansion");
        CollectionAssert.Contains(executionConstraints, "CK_CentralProcessingGraphExecutions_ExpectedCounts");
        CollectionAssert.Contains(executionConstraints, "CK_CentralProcessingGraphExecutions_Provenance");
        CollectionAssert.Contains(executionConstraints, "CK_CentralProcessingGraphExecutions_StatusTimestamps");
        Assert.IsTrue(execution.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(CentralProcessingGraphExecution.ExpandedAtUtc)]) &&
            index.GetFilter() == "[ExpandedAtUtc] IS NULL"));

        Assert.IsNotNull(requirement);
        CollectionAssert.Contains(
            requirement.GetCheckConstraints().Select(constraint => constraint.Name).ToArray(),
            "CK_CentralDerivativeJobInputRequirements_GraphResolution");
        Assert.IsNotNull(selectedArtifact);
        Assert.IsTrue(selectedArtifact.GetDeclaredTriggers().Any(trigger =>
            trigger.ModelName == "TR_CentralDerivativeJobInputs_GraphImmutable"));
        Assert.IsNotNull(selectedCanonical);
        Assert.IsTrue(selectedCanonical.GetDeclaredTriggers().Any(trigger =>
            trigger.ModelName == "TR_CentralDerivativeJobCanonicalInputs_GraphImmutable"));
    }
}

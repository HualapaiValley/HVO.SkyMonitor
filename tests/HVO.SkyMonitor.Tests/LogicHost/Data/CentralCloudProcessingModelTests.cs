using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
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
}

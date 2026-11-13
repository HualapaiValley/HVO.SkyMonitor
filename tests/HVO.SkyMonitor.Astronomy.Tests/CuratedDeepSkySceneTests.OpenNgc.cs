using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class CuratedDeepSkySceneTests
{
    [TestMethod]
    public void OpenNgcObjectCapKeepsMessierObjectsAheadOfAlphabeticalIcRows()
    {
        var messier = Object("NGC0224", 31, 120, 60, 90);
        var ic = Object("IC0001", 1, 10, 5, 90) with { MessierNumber = null, DisplayName = "IC0001" };
        var visible = Scene().WithCuratedDeepSky(Catalog([ic, messier], []),
            new OpenNgcDeepSkyOptions(Enabled: true, MaximumObjects: 1).ToSelection());
        Assert.AreEqual(messier.Id, visible.DeepSky!.Objects.Single().CatalogObjectId);
        Assert.AreEqual(1, visible.DeepSky.OmittedCandidateCount);
    }

    [TestMethod]
    public void OpenNgcQueryPlanResolvesEquivalentSelectionsOncePerImmutableCatalog()
    {
        var item = Object("NGC0224", 31, 120, 60, 90) with { VMagnitude = 3 };
        var catalog = new QueryCountingCatalog(Catalog([item], []));
        var options = new OpenNgcDeepSkyOptions(Enabled: true);
        Assert.HasCount(1, Scene().WithCuratedDeepSky(catalog, options.ToSelection()).DeepSky!.Objects);
        Assert.HasCount(1, Scene().WithCuratedDeepSky(catalog, (options with { }).ToSelection()).DeepSky!.Objects);
        Assert.AreEqual(1, catalog.Queries);
        Assert.IsEmpty(Scene().WithCuratedDeepSky(catalog, (options with { MaximumMagnitude = 1 }).ToSelection()).DeepSky!.Objects);
        Assert.AreEqual(2, catalog.Queries);
    }

    private sealed class QueryCountingCatalog(IDeepSkyCatalog inner) : IDeepSkyCatalog
    {
        public int Queries { get; private set; }
        public DeepSkySemantics Semantics => inner.Semantics;
        public IReadOnlyList<DeepSkyObject> Objects => throw new AssertFailedException("Frames must consume the cached selection.");
        public int AliasCount => inner.AliasCount;
        public IReadOnlyList<DeepSkyTombstone> Tombstones => inner.Tombstones;
        public bool TryGetObject(string id, out DeepSkyObject value) => inner.TryGetObject(id, out value);
        public IReadOnlyList<DeepSkyOutline> GetOutlines(string id) => inner.GetOutlines(id);
        public IReadOnlyList<DeepSkyAlias> FindByAlias(string designation) => inner.FindByAlias(designation);
        public bool TryGetTombstone(string id, out DeepSkyTombstone value) => inner.TryGetTombstone(id, out value);
        public DeepSkyQueryResult Query(DeepSkyQuery query) { Queries++; return inner.Query(query); }
    }

    [TestMethod]
    public void OpenNgcDesignationQueryAndSnapshotIdentitySurviveRetainedSceneRoundTrip()
    {
        var item = Object("Mel022", 45, 120, 120, null);
        var identity = new CatalogSnapshotIdentity("openngc-fixture", "openngc-fixture-1", "5", "6", new string('a', 64));
        var catalog = new DeepSkyCatalog(Semantics with { SnapshotIdentity = identity }, [item],
            [new(item.Designation, item.Id, DeepSkyAliasKinds.Designation), new("M45", item.Id, DeepSkyAliasKinds.Messier)], [], []);
        var query = new OpenNgcDeepSkyOptions(Enabled: true, IncludeNgc: false, IncludeIc: false);
        var visible = Scene().WithCuratedDeepSky(catalog, query.ToSelection());
        var source = new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('b', 64));
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(484, 304), source, new string('c', 64), "cal-v1");
        var parsed = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene));
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual(identity with { DatabaseSha256 = identity.DatabaseSha256.ToUpperInvariant() }, parsed.Scene!.DeepSky!.CatalogIdentity);
        Assert.AreEqual(ProjectedDeepSky.QueryAlgorithmVersion, parsed.Scene.DeepSky.AlgorithmVersion);
        Assert.AreEqual(item.Id, parsed.Scene.DeepSky.Objects.Single().CatalogObjectId);
        Assert.AreEqual(query, parsed.Scene.Selection.DeepSky!.Query);
        var changed = new DeepSkyCatalog(Semantics with { SnapshotIdentity = identity with { DatabaseSha256 = new string('d', 64) } },
            [item], [new(item.Designation, item.Id, DeepSkyAliasKinds.Designation)], [], []);
        var other = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, Scene().WithCuratedDeepSky(changed, query.ToSelection()),
            ProjectedSceneImageTransformV1.Identity(484, 304), source, new string('c', 64), "cal-v1");
        Assert.AreNotEqual(scene.SceneIdentitySha256, other.SceneIdentitySha256);
    }

    [TestMethod]
    public void EmptyOpenNgcDesignationSelectionRetainsTheSelectedInputIdentity()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var catalog = Catalog([item], []);
        var visible = Scene().WithCuratedDeepSky(catalog, new OpenNgcDeepSkyOptions(Enabled: true,
            IncludeNgc: false, IncludeMessier: false, IncludeIc: false).ToSelection());
        Assert.IsNotNull(visible.DeepSky);
        Assert.IsEmpty(visible.DeepSky.Objects);
        Assert.AreEqual(DeepSkyDesignations.None, visible.DeepSkySelection!.Query!.Designations);
    }
}

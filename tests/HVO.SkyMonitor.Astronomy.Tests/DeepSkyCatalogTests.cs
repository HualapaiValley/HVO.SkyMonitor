using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DeepSkyCatalogTests
{
    private static readonly DeepSkySemantics Semantics = new(
        "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b", new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote");

    private static readonly DeepSkyObject Pinwheel = new(
        "NGC5457", "NGC 5457", "M101", "G", 14.053483, 54.348944, "UMa", 23.99, 23.07, 28, 8.36, 7.9, 23.97, "SABc",
        101, null, null, null);

    private static readonly DeepSkyObject Spindle = new(
        "NGC5866", "NGC 5866", "NGC 5866", "G", 15.108194, 55.763222, "Dra", 6.31, 2.72, 126, 10.74, 9.89, 23.4, "S0-a",
        null, null, null, null);

    private static readonly DeepSkyObject Winnecke = new(
        "M040", "M40", "M40", DeepSkyObjectTypes.DoubleStar, 12.371139, 58.084444, "UMa", null, null, null, null, 8, null,
        null, 40, null, null, null);

    private static readonly DeepSkyObject Alnilam = new(
        "NGC1990", "NGC 1990", "NGC 1990", DeepSkyObjectTypes.Star, 5.603561, -1.201917, "Ori", null, null, null, 1.48, 1.69,
        null, null, null, null, "26311", null);

    private static readonly string[] ExpectedObjectOrder = ["M040", "NGC1990", "NGC5457", "NGC5866"];
    private static readonly string[] ExpectedPinwheelAliases = ["M101", "M102", "NGC 5457"];
    private static readonly string[] ConflictCandidates = ["NGC5457", "NGC5866"];
    private static readonly string[] ReversedCandidates = ["NGC5866", "NGC5457"];
    private static readonly int[] ExpectedLevels = [1, 3];

    [TestMethod]
    public void ConstructorOrdersObjectsAndTombstonesAndExposesCounts()
    {
        var catalog = Create();

        CollectionAssert.AreEqual(ExpectedObjectOrder, catalog.Objects.Select(static item => item.Id).ToArray());
        Assert.AreSame(Semantics, catalog.Semantics);
        Assert.AreEqual(8, catalog.AliasCount);
        Assert.AreEqual(1, catalog.Tombstones.Count);
        Assert.AreEqual(1, catalog.OutlineObjectCount);
        Assert.AreEqual(2, catalog.OutlineCount);
        Assert.AreEqual(3, catalog.OutlineRingCount);
        Assert.AreEqual(15L, catalog.OutlinePointCount);
    }

    [TestMethod]
    public void LookupsReturnLiveObjectsTombstonesAndOrderedOutlines()
    {
        var catalog = Create();

        Assert.IsTrue(catalog.TryGetObject("NGC5457", out var pinwheel));
        Assert.AreSame(Pinwheel, pinwheel);
        Assert.IsFalse(catalog.TryGetObject("IC0067", out _));
        Assert.IsTrue(catalog.TryGetTombstone("IC0067", out var tombstone));
        Assert.AreEqual(DeepSkyTombstoneReasons.Nonexistent, tombstone.Reason);
        Assert.IsFalse(catalog.TryGetTombstone("NGC5457", out _));
        CollectionAssert.AreEqual(ExpectedLevels, catalog.GetOutlines("NGC5457").Select(static item => item.Level).ToArray());
        Assert.AreEqual(0, catalog.GetOutlines("NGC5866").Count);
        Assert.Throws<ArgumentNullException>(() => catalog.TryGetObject(null!, out _));
        Assert.Throws<ArgumentNullException>(() => catalog.TryGetTombstone(null!, out _));
        Assert.Throws<ArgumentNullException>(() => catalog.GetOutlines(null!));
        Assert.Throws<ArgumentNullException>(() => catalog.FindByAlias(null!));
    }

    [TestMethod]
    public void FindByAliasIsCaseInsensitiveTrimmedAndFlagsTheDisputedAlias()
    {
        var catalog = Create();

        var disputed = catalog.FindByAlias("  m102 ");
        Assert.AreEqual(1, disputed.Count);
        Assert.AreEqual(new DeepSkyAlias("M102", "NGC5457", DeepSkyAliasKinds.Disputed), disputed[0]);
        Assert.IsTrue(disputed[0].IsDisputed);
        Assert.IsFalse(catalog.FindByAlias("M101")[0].IsDisputed);
        Assert.AreEqual(0, catalog.FindByAlias("M103").Count);
        CollectionAssert.AreEqual(
            ExpectedPinwheelAliases,
            catalog.FindByAlias("NGC 5457").Concat(catalog.FindByAlias("M101")).Concat(catalog.FindByAlias("M102"))
                .Select(static item => item.Alias).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public void FindByAliasOrdersASharedAliasByObjectId()
    {
        var catalog = Create(aliases: [.. Aliases(), new("Shared", "NGC5866", DeepSkyAliasKinds.Identifier),
            new("shared", "NGC5457", DeepSkyAliasKinds.Identifier)]);

        var matches = catalog.FindByAlias("SHARED");

        CollectionAssert.AreEqual(ConflictCandidates, matches.Select(static item => item.ObjectId).ToArray());
        Assert.AreEqual(10, catalog.AliasCount);
    }

    [TestMethod]
    public void ChangingTheConstructorInputsAfterwardsChangesNothing()
    {
        DeepSkyObject[] objects = [Spindle, Pinwheel, Alnilam, Winnecke];
        var aliases = Aliases().ToList();
        string[] candidates = ["NGC5457", "NGC5866"];
        List<DeepSkyTombstone> tombstones =
            [new("IC9999", "IC 9999", DeepSkyTombstoneReasons.DuplicateConflict, candidates)];
        var original = Ring(210.8, 54.3);
        var points = original.Points.ToList();
        List<DeepSkyOutlineRing> rings = [new(points)];
        List<DeepSkyOutline> outlines = [new("NGC5457", 2, rings)];
        var catalog = new DeepSkyCatalog(Semantics, objects, aliases, tombstones, outlines);

        objects[0] = Spindle with { Id = "NGC9999", Designation = "NGC 9999", DisplayName = "NGC 9999" };
        aliases.Clear();
        candidates[0] = "NGC0001";
        tombstones.Clear();
        points[1] = new(0, 0);
        rings.Add(Ring(10, 10));
        outlines.Clear();

        CollectionAssert.AreEqual(ExpectedObjectOrder, catalog.Objects.Select(static item => item.Id).ToArray());
        Assert.AreEqual(8, catalog.AliasCount);
        Assert.AreEqual(1, catalog.FindByAlias("M101").Count);
        Assert.IsTrue(catalog.TryGetTombstone("IC9999", out var tombstone));
        CollectionAssert.AreEqual(ConflictCandidates, tombstone.Candidates.ToArray());
        Assert.AreEqual(1, catalog.Tombstones.Count);
        var outline = catalog.GetOutlines("NGC5457").Single();
        Assert.AreEqual(1, outline.Rings.Count);
        CollectionAssert.AreEqual(original.Points.ToArray(), outline.Rings[0].Points.ToArray());
        Assert.AreEqual(1, catalog.OutlineCount);
        Assert.AreEqual(5L, catalog.OutlinePointCount);
    }

    [TestMethod]
    public void EveryReturnedCollectionRejectsMutation()
    {
        var catalog = Create(tombstones:
            [new DeepSkyTombstone("IC9999", "IC 9999", DeepSkyTombstoneReasons.DuplicateConflict, ConflictCandidates)]);
        Assert.IsTrue(catalog.TryGetTombstone("IC9999", out var tombstone));
        var outline = catalog.GetOutlines("NGC5457")[0];

        AssertReadOnly(catalog.Objects, Pinwheel);
        AssertReadOnly(catalog.Tombstones, tombstone);
        AssertReadOnly(tombstone.Candidates, "NGC0001");
        AssertReadOnly(catalog.FindByAlias("M101"), new DeepSkyAlias("M101", "NGC5866", DeepSkyAliasKinds.Messier));
        AssertReadOnly(catalog.GetOutlines("NGC5457"), outline);
        AssertReadOnly(outline.Rings, Ring(10, 10));
        AssertReadOnly(outline.Rings[0].Points, new DeepSkyOutlinePoint(0, 0));
        Assert.IsTrue(((ICollection<DeepSkyOutline>)catalog.GetOutlines("NGC5866")).IsReadOnly);
        Assert.IsTrue(((ICollection<DeepSkyAlias>)catalog.FindByAlias("M103")).IsReadOnly);
        CollectionAssert.AreEqual(ExpectedObjectOrder, catalog.Objects.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public void ObjectPropertiesClassifyExtentsAndStellarRows()
    {
        Assert.IsTrue(Pinwheel.HasOrientedExtent);
        Assert.IsFalse(Pinwheel.IsStellar);
        Assert.IsTrue((Pinwheel with { MajorAxisArcminutes = 5, MinorAxisArcminutes = 5, PositionAngleDegrees = null }).HasOrientedExtent);
        Assert.IsFalse((Pinwheel with { PositionAngleDegrees = null }).HasOrientedExtent);
        Assert.IsFalse((Pinwheel with { MinorAxisArcminutes = null }).HasOrientedExtent);
        Assert.IsFalse(Winnecke.HasOrientedExtent);
        Assert.IsTrue(Winnecke.IsStellar);
        Assert.IsTrue(Alnilam.IsStellar);
        Assert.IsTrue(DeepSkyObjectTypes.IsSupported("GGroup"));
        Assert.IsFalse(DeepSkyObjectTypes.IsSupported("Dup"));
        Assert.IsFalse(DeepSkyObjectTypes.IsSupported("NonEx"));
        Assert.IsFalse(DeepSkyObjectTypes.IsStellar("*Ass"));
        Assert.IsTrue(DeepSkyAliasKinds.IsSupported(DeepSkyAliasKinds.Hipparcos));
        Assert.IsFalse(DeepSkyAliasKinds.IsSupported("Messier"));
        CollectionAssert.AreEqual(DeepSkyObjectTypes.All.Order(StringComparer.Ordinal).ToArray(), DeepSkyObjectTypes.All.ToArray());
        CollectionAssert.AreEqual(DeepSkyAliasKinds.All.Order(StringComparer.Ordinal).ToArray(), DeepSkyAliasKinds.All.ToArray());
        CollectionAssert.AreEqual(
            DeepSkyTombstoneReasons.All.Order(StringComparer.Ordinal).ToArray(), DeepSkyTombstoneReasons.All.ToArray());
    }

    [TestMethod]
    public void ConstructorRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(null!, [], [], [], []));
        Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(Semantics, null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(Semantics, [], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(Semantics, [], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(Semantics, [], [], [], null!));
    }

    [TestMethod]
    public void EmptyCollectionIsValid()
    {
        var catalog = new DeepSkyCatalog(Semantics, [], [], [], []);

        Assert.AreEqual(0, catalog.Objects.Count);
        Assert.AreEqual(0, catalog.AliasCount);
        Assert.AreEqual(0L, catalog.OutlinePointCount);
    }

    [TestMethod]
    [DataRow("source-name")]
    [DataRow("source-version")]
    [DataRow("license")]
    [DataRow("frame")]
    [DataRow("epoch")]
    [DataRow("axis")]
    [DataRow("position-angle")]
    [DataRow("outline-level")]
    [DataRow("surface-brightness")]
    [DataRow("relative-url")]
    [DataRow("short-commit")]
    [DataRow("uppercase-commit")]
    public void ConstructorRejectsIncompleteSemantics(string defect)
    {
        var semantics = defect switch
        {
            "source-name" => Semantics with { SourceName = " " },
            "source-version" => Semantics with { SourceVersion = "v20260501 " },
            "license" => Semantics with { License = "" },
            "frame" => Semantics with { CoordinateFrame = " frame" },
            "epoch" => Semantics with { CoordinateEpoch = "" },
            "axis" => Semantics with { AxisUnit = "" },
            "position-angle" => Semantics with { PositionAngleConvention = "" },
            "outline-level" => Semantics with { OutlineLevelConvention = "" },
            "surface-brightness" => Semantics with { SurfaceBrightnessUnit = "" },
            "relative-url" => Semantics with { SourceUrl = new Uri("OpenNGC", UriKind.Relative) },
            "short-commit" => Semantics with { SourceCommit = "36cb178a" },
            "uppercase-commit" => Semantics with { SourceCommit = "36CB178A0F69DBA8BFC03A99C10512831EDF1C6B" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Throws<InvalidDataException>(() => new DeepSkyCatalog(semantics, [], [], [], []));
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("designation")]
    [DataRow("display")]
    [DataRow("constellation")]
    [DataRow("type")]
    [DataRow("null-type")]
    [DataRow("ra-negative")]
    [DataRow("ra-24")]
    [DataRow("ra-nan")]
    [DataRow("dec-low")]
    [DataRow("dec-high")]
    [DataRow("dec-infinite")]
    [DataRow("major-zero")]
    [DataRow("major-nan")]
    [DataRow("minor-zero")]
    [DataRow("minor-nan")]
    [DataRow("minor-without-major")]
    [DataRow("minor-exceeds-major")]
    [DataRow("angle-180")]
    [DataRow("angle-negative")]
    [DataRow("angle-nan")]
    [DataRow("b-nan")]
    [DataRow("v-infinite")]
    [DataRow("surface-nan")]
    [DataRow("hubble")]
    [DataRow("common")]
    [DataRow("messier-zero")]
    [DataRow("caldwell-zero")]
    [DataRow("messier-display")]
    [DataRow("designation-display")]
    [DataRow("hipparcos-padded")]
    [DataRow("hipparcos-zero")]
    [DataRow("hipparcos-text")]
    [DataRow("hipparcos-signed")]
    public void ConstructorRejectsAnInvalidObject(string defect)
    {
        var item = defect switch
        {
            "id" => Pinwheel with { Id = "NGC5457 " },
            "designation" => Pinwheel with { Designation = "" },
            "display" => Pinwheel with { DisplayName = " M101" },
            "constellation" => Pinwheel with { Constellation = "" },
            "type" => Pinwheel with { ObjectType = "Dup" },
            "null-type" => Pinwheel with { ObjectType = null! },
            "ra-negative" => Pinwheel with { RightAscensionHours = -0.001 },
            "ra-24" => Pinwheel with { RightAscensionHours = 24 },
            "ra-nan" => Pinwheel with { RightAscensionHours = double.NaN },
            "dec-low" => Pinwheel with { DeclinationDegrees = -90.001 },
            "dec-high" => Pinwheel with { DeclinationDegrees = 90.001 },
            "dec-infinite" => Pinwheel with { DeclinationDegrees = double.PositiveInfinity },
            "major-zero" => Pinwheel with { MajorAxisArcminutes = 0 },
            "major-nan" => Pinwheel with { MajorAxisArcminutes = double.NaN },
            "minor-zero" => Pinwheel with { MinorAxisArcminutes = 0 },
            "minor-nan" => Pinwheel with { MinorAxisArcminutes = double.NaN },
            "minor-without-major" => Pinwheel with { MajorAxisArcminutes = null },
            "minor-exceeds-major" => Pinwheel with { MinorAxisArcminutes = 24 },
            "angle-180" => Pinwheel with { PositionAngleDegrees = 180 },
            "angle-negative" => Pinwheel with { PositionAngleDegrees = -1 },
            "angle-nan" => Pinwheel with { PositionAngleDegrees = double.NaN },
            "b-nan" => Pinwheel with { BMagnitude = double.NaN },
            "v-infinite" => Pinwheel with { VMagnitude = double.NegativeInfinity },
            "surface-nan" => Pinwheel with { SurfaceBrightness = double.NaN },
            "hubble" => Pinwheel with { HubbleType = "" },
            "common" => Pinwheel with { CommonName = "Pinwheel " },
            "messier-zero" => Pinwheel with { MessierNumber = 0, DisplayName = "M0" },
            "caldwell-zero" => Pinwheel with { CaldwellNumber = 0 },
            "messier-display" => Pinwheel with { DisplayName = "NGC 5457" },
            "designation-display" => Spindle with { DisplayName = "M102" },
            "hipparcos-padded" => Alnilam with { HipparcosId = "026311" },
            "hipparcos-zero" => Alnilam with { HipparcosId = "0" },
            "hipparcos-text" => Alnilam with { HipparcosId = "HIP 26311" },
            "hipparcos-signed" => Alnilam with { HipparcosId = "+26311" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Throws<InvalidDataException>(() => new DeepSkyCatalog(Semantics, [item], [], [], []));
    }

    [TestMethod]
    public void ConstructorAcceptsEqualAxesWithoutAPositionAngle()
    {
        var round = Spindle with { MinorAxisArcminutes = 6.31, PositionAngleDegrees = null };
        var catalog = new DeepSkyCatalog(Semantics, [round], [new("NGC 5866", "NGC5866", DeepSkyAliasKinds.Designation)], [], []);

        Assert.IsTrue(catalog.Objects[0].HasOrientedExtent);
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("designation")]
    [DataRow("display")]
    [DataRow("caldwell")]
    [DataRow("hipparcos")]
    public void ConstructorRejectsARepeatedIdentity(string field)
    {
        var first = Pinwheel with { CaldwellNumber = 1, HipparcosId = "1" };
        var second = field switch
        {
            "id" => Spindle with { Id = "NGC5457" },
            "designation" => Spindle with { Designation = "NGC 5457", DisplayName = "NGC 5457" },
            "display" => Spindle with { Designation = "M101", DisplayName = "M101" },
            "caldwell" => Spindle with { CaldwellNumber = 1 },
            "hipparcos" => Spindle with { HipparcosId = "1" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(() => new DeepSkyCatalog(Semantics, [first, second], [], [], []));
    }

    [TestMethod]
    public void ConstructorRejectsANullObject()
        => Assert.Throws<ArgumentNullException>(() => new DeepSkyCatalog(Semantics, [null!], [], [], []));

    [TestMethod]
    [DataRow("live")]
    [DataRow("repeated")]
    [DataRow("id")]
    [DataRow("designation")]
    [DataRow("reason")]
    [DataRow("null-reason")]
    [DataRow("null-candidates")]
    [DataRow("conflict-without-candidates")]
    [DataRow("conflict-with-one-candidate")]
    [DataRow("candidates-without-conflict")]
    [DataRow("blank-candidate")]
    [DataRow("unordered-candidates")]
    public void ConstructorRejectsAnInvalidTombstone(string defect)
    {
        var valid = new DeepSkyTombstone("IC0067", "IC 67", DeepSkyTombstoneReasons.Nonexistent, []);
        DeepSkyTombstone[] tombstones = defect switch
        {
            "live" => [valid with { Id = "NGC5457" }],
            "repeated" => [valid, valid],
            "id" => [valid with { Id = "" }],
            "designation" => [valid with { Designation = "IC 67 " }],
            "reason" => [valid with { Reason = "missing" }],
            "null-reason" => [valid with { Reason = null! }],
            "null-candidates" => [valid with { Candidates = null! }],
            "conflict-without-candidates" => [valid with { Reason = DeepSkyTombstoneReasons.DuplicateConflict }],
            "conflict-with-one-candidate" => [valid with { Reason = DeepSkyTombstoneReasons.DuplicateConflict, Candidates = ["NGC5457"] }],
            "candidates-without-conflict" => [valid with { Reason = DeepSkyTombstoneReasons.DuplicateUnresolved, Candidates = ConflictCandidates }],
            "blank-candidate" => [valid with { Reason = DeepSkyTombstoneReasons.DuplicateConflict, Candidates = ["", "NGC5457"] }],
            "unordered-candidates" => [valid with { Reason = DeepSkyTombstoneReasons.DuplicateConflict, Candidates = ReversedCandidates }],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Throws<InvalidDataException>(() => Create(tombstones: tombstones));
    }

    [TestMethod]
    public void ConstructorAcceptsAConflictingDuplicateWithOrderedCandidates()
    {
        var conflict = new DeepSkyTombstone("IC9999", "IC 9999", DeepSkyTombstoneReasons.DuplicateConflict, ConflictCandidates);
        var catalog = Create(tombstones: [conflict, new("IC0067", "IC 67", DeepSkyTombstoneReasons.DuplicateUnresolved, [])]);

        Assert.AreEqual("IC0067", catalog.Tombstones[0].Id);
        Assert.IsTrue(catalog.TryGetTombstone("IC9999", out var found));
        CollectionAssert.AreEqual(ConflictCandidates, found.Candidates.ToArray());
    }

    [TestMethod]
    [DataRow("blank")]
    [DataRow("untrimmed")]
    [DataRow("kind")]
    [DataRow("null-kind")]
    [DataRow("dead-object")]
    [DataRow("null-object")]
    [DataRow("repeated")]
    [DataRow("wrong-designation")]
    [DataRow("missing-designation")]
    public void ConstructorRejectsAnInvalidAlias(string defect)
    {
        var aliases = Aliases().ToList();
        switch (defect)
        {
            case "blank": aliases.Add(new(" ", "NGC5457", DeepSkyAliasKinds.Identifier)); break;
            case "untrimmed": aliases.Add(new("PGC 50063 ", "NGC5457", DeepSkyAliasKinds.Identifier)); break;
            case "kind": aliases.Add(new("PGC 50063", "NGC5457", "pgc")); break;
            case "null-kind": aliases.Add(new("PGC 50063", "NGC5457", null!)); break;
            case "dead-object": aliases.Add(new("IC 67", "IC0067", DeepSkyAliasKinds.Identifier)); break;
            case "null-object": aliases.Add(new("IC 67", null!, DeepSkyAliasKinds.Identifier)); break;
            case "repeated": aliases.Add(new("M101", "NGC5457", DeepSkyAliasKinds.Identifier)); break;
            case "wrong-designation": aliases.Add(new("NGC 5458", "NGC5457", DeepSkyAliasKinds.Designation)); break;
            case "missing-designation": aliases.RemoveAll(static item => item.ObjectId == "NGC5866"); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Throws<InvalidDataException>(() => Create(aliases: aliases));
    }

    [TestMethod]
    public void ConstructorRejectsANullAlias()
        => Assert.Throws<ArgumentNullException>(() => Create(aliases: [.. Aliases(), null!]));

    [TestMethod]
    [DataRow("dead-object")]
    [DataRow("null-object")]
    [DataRow("level-zero")]
    [DataRow("level-four")]
    [DataRow("no-rings")]
    [DataRow("null-rings")]
    [DataRow("null-ring")]
    [DataRow("null-points")]
    [DataRow("too-few-points")]
    [DataRow("open")]
    [DataRow("repeated-point")]
    [DataRow("ra-360")]
    [DataRow("ra-negative")]
    [DataRow("ra-nan")]
    [DataRow("dec-high")]
    [DataRow("dec-nan")]
    [DataRow("repeated-level")]
    public void ConstructorRejectsAnInvalidOutline(string defect)
    {
        var valid = new DeepSkyOutline("NGC5457", 2, [Ring(210.8, 54.3)]);
        DeepSkyOutline[] outlines = defect switch
        {
            "dead-object" => [valid with { ObjectId = "IC0067" }],
            "null-object" => [valid with { ObjectId = null! }],
            "level-zero" => [valid with { Level = 0 }],
            "level-four" => [valid with { Level = 4 }],
            "no-rings" => [valid with { Rings = [] }],
            "null-rings" => [valid with { Rings = null! }],
            "null-ring" => [valid with { Rings = [null!] }],
            "null-points" => [valid with { Rings = [new DeepSkyOutlineRing(null!)] }],
            "too-few-points" => [valid with { Rings = [new([new(1, 1), new(2, 1), new(1, 1)])] }],
            "open" => [valid with { Rings = [new([new(1, 1), new(2, 1), new(2, 2), new(1, 2)])] }],
            "repeated-point" => [valid with { Rings = [new([new(1, 1), new(2, 1), new(2, 1), new(2, 2), new(1, 1)])] }],
            "ra-360" => [valid with { Rings = [Ring(359.95, 0)] }],
            "ra-negative" => [valid with { Rings = [Ring(-0.5, 0)] }],
            "ra-nan" => [valid with { Rings = [new([new(double.NaN, 1), new(2, 1), new(2, 2), new(double.NaN, 1)])] }],
            "dec-high" => [valid with { Rings = [Ring(10, 89.95)] }],
            "dec-nan" => [valid with { Rings = [new([new(1, double.NaN), new(2, 1), new(2, 2), new(1, double.NaN)])] }],
            "repeated-level" => [valid, valid with { Rings = [Ring(10, 10)] }],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Throws<InvalidDataException>(() => Create(outlines: outlines));
    }

    [TestMethod]
    public void ConstructorRejectsANullOutline()
        => Assert.Throws<ArgumentNullException>(() => Create(outlines: [null!]));

    private static DeepSkyCatalog Create(
        IEnumerable<DeepSkyAlias>? aliases = null,
        IEnumerable<DeepSkyTombstone>? tombstones = null,
        IEnumerable<DeepSkyOutline>? outlines = null)
        => new(
            Semantics,
            [Spindle, Pinwheel, Alnilam, Winnecke],
            aliases ?? Aliases(),
            tombstones ?? [new DeepSkyTombstone("IC0067", "IC 67", DeepSkyTombstoneReasons.Nonexistent, [])],
            outlines ??
            [
                new DeepSkyOutline("NGC5457", 3, [Ring(210.8, 54.3)]),
                new DeepSkyOutline("NGC5457", 1, [Ring(210.7, 54.2), Ring(211.2, 54.6)]),
            ]);

    private static DeepSkyAlias[] Aliases() =>
    [
        new("NGC 5866", "NGC5866", DeepSkyAliasKinds.Designation),
        new("NGC 5457", "NGC5457", DeepSkyAliasKinds.Designation),
        new("M101", "NGC5457", DeepSkyAliasKinds.Messier),
        new("M102", "NGC5457", DeepSkyAliasKinds.Disputed),
        new("M40", "M040", DeepSkyAliasKinds.Designation),
        new("NGC 1990", "NGC1990", DeepSkyAliasKinds.Designation),
        new("HIP 26311", "NGC1990", DeepSkyAliasKinds.Hipparcos),
        new("Eps Ori", "NGC1990", DeepSkyAliasKinds.Identifier),
    ];

    // An array returned as IReadOnlyList<T> still accepts element replacement through IList<T>; a frozen list does not.
    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        var list = (IList<T>)values;
        Assert.IsTrue(list.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = replacement);
        Assert.ThrowsExactly<NotSupportedException>(() => list.Add(replacement));
    }

    // A closed four-point ring of 0.1 degree squares; its points are distinct and stay in range for a valid corner.
    private static DeepSkyOutlineRing Ring(double rightAscension, double declination) => new(
    [
        new(rightAscension, declination),
        new(rightAscension + 0.1, declination),
        new(rightAscension + 0.1, declination + 0.1),
        new(rightAscension, declination + 0.1),
        new(rightAscension, declination),
    ]);
}

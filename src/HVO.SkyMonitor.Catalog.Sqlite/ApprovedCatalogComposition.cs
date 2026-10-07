using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>The deep-sky content a composed catalog database declares and must contain.</summary>
public sealed record CatalogDeepSkyCounts(
    long ObjectCount,
    long AliasCount,
    long TombstoneCount,
    long OutlineObjectCount,
    long OutlineSetCount,
    long OutlineRingCount,
    long OutlinePointCount);

/// <summary>Byte and row evidence of a component database.</summary>
public sealed record ApprovedCatalogComponentDatabase(string Sha256, long Length, long RowCount);

/// <summary>
/// The stars component of a composed catalog: the database of an approved catalog package, carried byte for byte.
/// </summary>
public sealed record ApprovedCatalogStarsComponent(
    string Name,
    string CatalogId,
    string PackageVersion,
    ApprovedCatalogComponentDatabase Database);

/// <summary>One pinned upstream input file of a component.</summary>
public sealed record ApprovedCatalogComponentInput(string RelativePath, string Sha256, long Length, long RowCount);

/// <summary>The pinned upstream outline files of a deep-sky component, identified by their SHA-256 listing.</summary>
public sealed record ApprovedCatalogOutlineInput(string RelativePath, long FileCount, string ListingSha256, long PointCount);

/// <summary>The pinned upstream source of a composed catalog's deep-sky component.</summary>
public sealed record ApprovedCatalogDeepSkyComponent(
    string Name,
    string Version,
    string Commit,
    Uri ProjectUrl,
    string License,
    IReadOnlyList<ApprovedCatalogComponentInput> Inputs,
    ApprovedCatalogOutlineInput Outlines);

/// <summary>
/// One recorded preprocessing transformation and the number of items it changed. Its subjects are listed only when
/// it changed at most <see cref="ApprovedCatalogComposition.MaximumListedSubjects"/> items.
/// </summary>
public sealed record ApprovedCatalogTransformation(string Id, long Count, IReadOnlyList<string> Subjects);

/// <summary>
/// What a manifest-version-3 catalog is composed from: its deep-sky content counts, its unchanged stars component, its
/// deep-sky component, and the transformations preprocessing applied. The registry and the package manifest use the
/// same layout, so one strict reader parses both and the resolver compares them field by field.
/// </summary>
public sealed record ApprovedCatalogComposition(
    CatalogDeepSkyCounts DeepSkyCounts,
    ApprovedCatalogStarsComponent Stars,
    ApprovedCatalogDeepSkyComponent DeepSky,
    IReadOnlyList<ApprovedCatalogTransformation> Transformations)
{
    /// <summary>The largest transformation whose subjects are listed; larger ones record only their count.</summary>
    public const int MaximumListedSubjects = 11;

    private const int MaximumInputs = 8;
    private const int MaximumTransformations = 64;
    private const int MaximumTextLength = 128;

    /// <summary>Reads the composition of a registry specification or production manifest strictly.</summary>
    /// <param name="owner">The object holding <c>components</c> and <c>transformations</c>.</param>
    /// <param name="database">The <c>database</c> object holding <c>deepSky</c>.</param>
    internal static ApprovedCatalogComposition Read(JsonElement owner, JsonElement database)
    {
        var deepSkyCounts = ReadDeepSkyCounts(database);
        var components = CatalogSnapshotResolver.RequireProperty(owner, "components");
        if (components.ValueKind != JsonValueKind.Array || components.GetArrayLength() != 2)
        {
            throw new InvalidDataException("Catalog composition 'components' must list exactly the stars and deep-sky components.");
        }
        var stars = CatalogSnapshotResolver.RequireObject(components[0], "components[0]");
        var deepSky = CatalogSnapshotResolver.RequireObject(components[1], "components[1]");
        CatalogSnapshotResolver.RequireExactProperties(stars, "components[0]",
            "role", "name", "catalogId", "packageVersion", "database");
        CatalogSnapshotResolver.RequireExactProperties(deepSky, "components[1]",
            "role", "name", "version", "commit", "projectUrl", "license", "inputs", "outlines");
        Role(stars, "stars");
        Role(deepSky, "deep-sky");
        var starsDatabase = Object(stars, "database", "components[0].database", "sha256", "length", "rowCount");
        var outlines = Object(deepSky, "outlines", "components[1].outlines",
            "relativePath", "fileCount", "listingSha256", "pointCount");
        var starsCatalogId = Text(stars, "catalogId");
        CatalogSnapshotResolver.ValidateCatalogId(starsCatalogId);

        return new ApprovedCatalogComposition(
            deepSkyCounts,
            new ApprovedCatalogStarsComponent(
                Text(stars, "name"),
                starsCatalogId,
                Text(stars, "packageVersion"),
                new ApprovedCatalogComponentDatabase(
                    Sha256(starsDatabase, "sha256"),
                    Positive(starsDatabase, "length"),
                    Positive(starsDatabase, "rowCount"))),
            new ApprovedCatalogDeepSkyComponent(
                Text(deepSky, "name"),
                Text(deepSky, "version"),
                Commit(deepSky, "commit"),
                HttpsUri(deepSky, "projectUrl"),
                Text(deepSky, "license"),
                ReadInputs(deepSky),
                new ApprovedCatalogOutlineInput(
                    RelativePath(outlines, "relativePath"),
                    Positive(outlines, "fileCount"),
                    Sha256(outlines, "listingSha256"),
                    Positive(outlines, "pointCount"))),
            ReadTransformations(owner));
    }

    /// <summary>Reads the strict <c>database.deepSky</c> counts of a schema-4 manifest or specification.</summary>
    internal static CatalogDeepSkyCounts ReadDeepSkyCounts(JsonElement database)
    {
        var counts = Object(database, "deepSky", "database.deepSky",
            "objectCount", "aliasCount", "tombstoneCount", "outlineObjectCount", "outlineSetCount", "outlineRingCount",
            "outlinePointCount");
        return new CatalogDeepSkyCounts(
            Positive(counts, "objectCount"),
            Positive(counts, "aliasCount"),
            NonNegative(counts, "tombstoneCount"),
            NonNegative(counts, "outlineObjectCount"),
            NonNegative(counts, "outlineSetCount"),
            NonNegative(counts, "outlineRingCount"),
            NonNegative(counts, "outlinePointCount"));
    }

    /// <summary>
    /// Throws when a manifest's composition differs from the approved one, naming the first differing property.
    /// </summary>
    internal void RequireSameAs(ApprovedCatalogComposition actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        Same("database.deepSky", DeepSkyCounts, actual.DeepSkyCounts);
        Same("components[0]", Stars, actual.Stars);
        Same("components[1].name", DeepSky.Name, actual.DeepSky.Name);
        Same("components[1].version", DeepSky.Version, actual.DeepSky.Version);
        Same("components[1].commit", DeepSky.Commit, actual.DeepSky.Commit);
        Same("components[1].projectUrl", DeepSky.ProjectUrl.OriginalString, actual.DeepSky.ProjectUrl.OriginalString);
        Same("components[1].license", DeepSky.License, actual.DeepSky.License);
        Same("components[1].inputs.length", DeepSky.Inputs.Count, actual.DeepSky.Inputs.Count);
        for (var index = 0; index < DeepSky.Inputs.Count; index++)
        {
            Same($"components[1].inputs[{index}]", DeepSky.Inputs[index], actual.DeepSky.Inputs[index]);
        }
        Same("components[1].outlines", DeepSky.Outlines, actual.DeepSky.Outlines);
        Same("transformations.length", Transformations.Count, actual.Transformations.Count);
        for (var index = 0; index < Transformations.Count; index++)
        {
            var expected = Transformations[index];
            var value = actual.Transformations[index];
            Same($"transformations[{index}].id", expected.Id, value.Id);
            Same($"transformations[{index}].count", expected.Count, value.Count);
            if (!expected.Subjects.SequenceEqual(value.Subjects, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Catalog production manifest transformations[{index}].subjects differs from the approved specification.");
            }
        }
    }

    private static void Same<T>(string name, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidDataException(
                $"Catalog production manifest {name} mismatch. Expected '{expected}', got '{actual}'.");
        }
    }

    private static ApprovedCatalogComponentInput[] ReadInputs(JsonElement deepSky)
    {
        var inputs = CatalogSnapshotResolver.RequireProperty(deepSky, "inputs");
        if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is 0 or > MaximumInputs)
        {
            throw new InvalidDataException($"Catalog composition 'components[1].inputs' must list 1 to {MaximumInputs} files.");
        }
        var result = new ApprovedCatalogComponentInput[inputs.GetArrayLength()];
        for (var index = 0; index < result.Length; index++)
        {
            var name = $"components[1].inputs[{index}]";
            var input = CatalogSnapshotResolver.RequireObject(inputs[index], name);
            CatalogSnapshotResolver.RequireExactProperties(input, name, "relativePath", "sha256", "length", "rowCount");
            result[index] = new ApprovedCatalogComponentInput(
                RelativePath(input, "relativePath"),
                Sha256(input, "sha256"),
                Positive(input, "length"),
                Positive(input, "rowCount"));
        }
        if (result.Select(static item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException("Catalog composition inputs must name distinct files.");
        }
        return result;
    }

    private static ApprovedCatalogTransformation[] ReadTransformations(JsonElement owner)
    {
        var transformations = CatalogSnapshotResolver.RequireProperty(owner, "transformations");
        if (transformations.ValueKind != JsonValueKind.Array ||
            transformations.GetArrayLength() is 0 or > MaximumTransformations)
        {
            throw new InvalidDataException(
                $"Catalog composition 'transformations' must list 1 to {MaximumTransformations} transformations.");
        }
        var result = new ApprovedCatalogTransformation[transformations.GetArrayLength()];
        for (var index = 0; index < result.Length; index++)
        {
            var name = $"transformations[{index}]";
            var item = CatalogSnapshotResolver.RequireObject(transformations[index], name);
            CatalogSnapshotResolver.RequireExactProperties(item, name, "id", "count", "subjects");
            var id = Text(item, "id");
            if (!id.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                throw new InvalidDataException($"Catalog composition '{name}.id' must be lowercase kebab-case.");
            }
            var count = NonNegative(item, "count");
            var subjects = CatalogSnapshotResolver.RequireProperty(item, "subjects");
            if (subjects.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Catalog composition '{name}.subjects' must be an array.");
            }
            var values = subjects.EnumerateArray().Select(subject => SubjectText(subject, name)).ToArray();
            // A small transformation lists every subject once, in ordinal order; a larger one records only its count.
            var expectedLength = count <= MaximumListedSubjects ? count : 0;
            if (values.Length != expectedLength ||
                !values.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).SequenceEqual(values, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Catalog composition '{name}.subjects' must list its {Invariant(count)} subjects once in ordinal order when it changed at most {MaximumListedSubjects} items, and none otherwise.");
            }
            result[index] = new ApprovedCatalogTransformation(id, count, values);
        }
        if (result.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException("Catalog composition transformations must have distinct IDs.");
        }
        return result;
    }

    private static string SubjectText(JsonElement subject, string name)
    {
        if (subject.ValueKind != JsonValueKind.String || subject.GetString() is not { } value ||
            !IsBoundedText(value))
        {
            throw new InvalidDataException($"Catalog composition '{name}.subjects' must hold bounded nonblank strings.");
        }
        return value;
    }

    private static void Role(JsonElement component, string expected)
    {
        var role = Text(component, "role");
        if (!string.Equals(role, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Catalog composition component role must be '{expected}', got '{role}'.");
        }
    }

    private static JsonElement Object(JsonElement parent, string property, string name, params string[] properties)
    {
        var value = CatalogSnapshotResolver.RequireObject(CatalogSnapshotResolver.RequireProperty(parent, property), name);
        CatalogSnapshotResolver.RequireExactProperties(value, name, properties);
        return value;
    }

    private static string Text(JsonElement parent, string name)
    {
        var value = CatalogSnapshotResolver.RequireString(parent, name);
        return IsBoundedText(value)
            ? value
            : throw new InvalidDataException($"Catalog composition property '{name}' must be bounded trimmed text.");
    }

    private static bool IsBoundedText(string value)
        => value.Length is > 0 and <= MaximumTextLength &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           !value.Any(char.IsControl);

    private static string RelativePath(JsonElement parent, string name)
    {
        var value = Text(parent, name);
        var segments = value.Split('/');
        return value.Contains('\\', StringComparison.Ordinal) || value.StartsWith('/') ||
               segments.Any(static segment => segment is "" or "." or "..")
            ? throw new InvalidDataException($"Catalog composition property '{name}' must be a relative POSIX path.")
            : value;
    }

    private static Uri HttpsUri(JsonElement parent, string name)
    {
        var value = Text(parent, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidDataException($"Catalog composition property '{name}' must be an absolute HTTPS URL.");
    }

    private static string Sha256(JsonElement parent, string name) => LowercaseHex(parent, name, 64);

    private static string Commit(JsonElement parent, string name) => LowercaseHex(parent, name, 40);

    private static string LowercaseHex(JsonElement parent, string name, int length)
    {
        var value = CatalogSnapshotResolver.RequireString(parent, name);
        return value.Length == length && value.All(static item => item is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? value
            : throw new InvalidDataException(
                $"Catalog composition property '{name}' must be {length} lowercase hexadecimal characters.");
    }

    private static long Positive(JsonElement parent, string name)
    {
        var value = CatalogSnapshotResolver.RequireInt64(parent, name);
        return value > 0 ? value : throw new InvalidDataException($"Catalog composition property '{name}' must be positive.");
    }

    private static long NonNegative(JsonElement parent, string name)
    {
        var value = CatalogSnapshotResolver.RequireInt64(parent, name);
        return value >= 0
            ? value
            : throw new InvalidDataException($"Catalog composition property '{name}' must not be negative.");
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

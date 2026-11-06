using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Read metadata bytes only: never load the target assembly, resolve its dependencies,
// instantiate its attributes, or run its module initializer.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: EnumMetadata <assembly> <fully-qualified-enum-name>");
    return 1;
}

try
{
    using var stream = File.OpenRead(args[0]);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var matches = metadata.TypeDefinitions.Where(handle =>
    {
        var type = metadata.GetTypeDefinition(handle);
        return !type.IsNested &&
            string.Equals($"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}", args[1], StringComparison.Ordinal);
    }).ToArray();
    if (matches.Length != 1)
    {
        throw new InvalidDataException("Expected exactly one top-level enum type.");
    }

    var definition = metadata.GetTypeDefinition(matches[0]);
    if (definition.BaseType.Kind != HandleKind.TypeReference)
    {
        throw new InvalidDataException("The requested type is not an enum.");
    }

    var baseType = metadata.GetTypeReference((TypeReferenceHandle)definition.BaseType);
    if (metadata.GetString(baseType.Namespace) != "System" || metadata.GetString(baseType.Name) != "Enum")
    {
        throw new InvalidDataException("The requested type is not an enum.");
    }

    var names = new List<string>();
    foreach (var handle in definition.GetFields())
    {
        var field = metadata.GetFieldDefinition(handle);
        if ((field.Attributes & FieldAttributes.SpecialName) != 0 && metadata.GetString(field.Name) == "value__")
        {
            continue;
        }

        const FieldAttributes required = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
        if ((field.Attributes & required) != required || field.GetDefaultValue().IsNil)
        {
            throw new InvalidDataException("Unexpected enum field metadata.");
        }

        names.Add(metadata.GetString(field.Name));
    }

    if (names.Count == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
    {
        throw new InvalidDataException("Expected nonempty, unique enum members.");
    }

    Console.WriteLine(JsonSerializer.Serialize(names.Order(StringComparer.Ordinal)));
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or BadImageFormatException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Could not read enum metadata; build the current Release assemblies and supply an exact enum type.");
    return 1;
}

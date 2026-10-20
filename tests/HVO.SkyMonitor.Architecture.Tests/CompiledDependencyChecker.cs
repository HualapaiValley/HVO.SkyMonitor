using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace HVO.SkyMonitor.Tests;

// The compiler resolves imports, aliases and generated/nested types before this
// checker runs. Both production rules and their positive controls use this path.
internal static class CompiledDependencyChecker
{
    internal sealed record TypeName(string Assembly, string Namespace, string Name);
    internal sealed record Result(int TypesChecked, string[] Violations);

    internal static bool SpaceWeather(TypeName type) => Subtree(type.Namespace, "HVO.SkyMonitor.Processing.SpaceWeather");
    internal static bool CaptureOrRecipe(TypeName type) => !SpaceWeather(type) &&
        (Subtree(type.Namespace, "HVO.SkyMonitor.Processing")
        || type.Namespace.Split('.').Any(segment => segment.Contains("Capture", StringComparison.Ordinal)
            || segment.Contains("RawIngress", StringComparison.Ordinal) || segment.Contains("Recipe", StringComparison.Ordinal)));
    internal static bool HostHttpOrPersistence(TypeName type) =>
        new[] { "HVO.SkyMonitor.CameraAgent", "HVO.SkyMonitor.LogicHost", "HVO.SkyMonitor.Common", "HVO.SkyMonitor.Catalog", "HVO.SkyMonitor.Storage",
            "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "System.Net", "System.IO", "System.Data", "SQLite", "Sqlite" }
        .Any(prefix => Subtree(type.Namespace, prefix) || Subtree(type.Assembly, prefix));

    private static bool Subtree(string value, string prefix) => value == prefix || value.StartsWith(prefix + ".", StringComparison.Ordinal);

    internal static Result Check(string assemblyPath, Func<TypeName, bool> select, Func<TypeName, bool> forbidden)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var inspection = new Inspection(assemblyPath, pe, metadata, forbidden);
        var count = 0;
        foreach (var handle in metadata.TypeDefinitions)
        {
            if (!select(inspection.Name(handle))) continue;
            count++;
            inspection.Inspect(handle);
        }
        return new(count, [.. inspection.Violations]);
    }

    private sealed class Inspection
    {
        private readonly string assemblyPath;
        private readonly PEReader pe;
        private readonly MetadataReader metadata;
        private readonly Func<TypeName, bool> forbidden;
        private readonly string assembly;
        private readonly SignatureReferences provider;
        private readonly HashSet<EntityHandle> seen = [];
        private readonly Dictionary<TypeName, PrimitiveTypeCode> enumTypes = [];
        private readonly Dictionary<ushort, OpCode> opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(op => unchecked((ushort)op.Value));
        private TypeName source = new("", "", "");
        internal SortedSet<string> Violations { get; } = new(StringComparer.Ordinal);

        internal Inspection(string assemblyPath, PEReader pe, MetadataReader metadata, Func<TypeName, bool> forbidden)
        {
            this.assemblyPath = assemblyPath;
            this.pe = pe;
            this.metadata = metadata;
            this.forbidden = forbidden;
            assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            provider = new SignatureReferences(metadata, Visit, SerializedType, EnumUnderlying, SerializedEnumUnderlying);
        }
        internal void Inspect(TypeDefinitionHandle handle)
        {
            source = Name(handle);
            seen.Clear();
            var type = metadata.GetTypeDefinition(handle);
            Visit(type.BaseType);
            Attributes(type.GetCustomAttributes());
            Generics(type.GetGenericParameters());
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                var iface = metadata.GetInterfaceImplementation(implementation);
                Visit(iface.Interface);
                Attributes(iface.GetCustomAttributes());
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                field.DecodeSignature(provider, null);
                Attributes(field.GetCustomAttributes());
            }
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                property.DecodeSignature(provider, null);
                Attributes(property.GetCustomAttributes());
            }
            foreach (var eventHandle in type.GetEvents())
            {
                var ev = metadata.GetEventDefinition(eventHandle);
                Visit(ev.Type);
                Attributes(ev.GetCustomAttributes());
            }
            foreach (var implementation in type.GetMethodImplementations())
            {
                var method = metadata.GetMethodImplementation(implementation);
                Visit(method.MethodBody);
                Visit(method.MethodDeclaration);
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                method.DecodeSignature(provider, null);
                Attributes(method.GetCustomAttributes());
                Generics(method.GetGenericParameters());
                foreach (var parameter in method.GetParameters()) Attributes(metadata.GetParameter(parameter).GetCustomAttributes());
                if (method.RelativeVirtualAddress == 0) continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                Visit(body.LocalSignature);
                foreach (var region in body.ExceptionRegions) Visit(region.CatchType);
                var il = body.GetILReader();
                while (il.RemainingBytes > 0)
                {
                    ushort opcode = il.ReadByte();
                    if (opcode == 0xfe) opcode = (ushort)(0xfe00 | il.ReadByte());
                    switch (opcodes[opcode].OperandType)
                    {
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineSig:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                            Visit(MetadataTokens.EntityHandle(il.ReadInt32()));
                            break;
                        case OperandType.InlineSwitch:
                            var targets = il.ReadInt32();
                            il.Offset += checked(targets * 4);
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR: il.Offset += 8; break;
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineI:
                        case OperandType.InlineString:
                        case OperandType.ShortInlineR: il.Offset += 4; break;
                        case OperandType.InlineVar: il.Offset += 2; break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar: il.Offset++; break;
                        case OperandType.InlineNone: break;
                        default: throw new InvalidOperationException($"Unhandled IL operand {opcodes[opcode].OperandType}");
                    }
                }
            }

        }

        private void Attributes(CustomAttributeHandleCollection attributes)
        {
            foreach (var attribute in attributes)
            {
                var value = metadata.GetCustomAttribute(attribute);
                Visit(value.Constructor);
                // Includes typeof arguments and named System.Type values,
                // whose type names are serialized in the attribute blob.
                value.DecodeValue(provider!);
            }
        }
        private void Generics(GenericParameterHandleCollection parameters)
        {
            foreach (var parameterHandle in parameters)
            {
                var parameter = metadata.GetGenericParameter(parameterHandle);
                Attributes(parameter.GetCustomAttributes());
                foreach (var constraintHandle in parameter.GetConstraints())
                {
                    var constraint = metadata.GetGenericParameterConstraint(constraintHandle);
                    Visit(constraint.Type);
                    Attributes(constraint.GetCustomAttributes());
                }
            }
        }
        private void Visit(EntityHandle target)
        {
            if (target.IsNil || !seen.Add(target)) return;
            switch (target.Kind)
            {
                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                    var dependency = Name(target);
                    if (forbidden(dependency)) Violations.Add($"{source.Namespace}.{source.Name} -> {dependency.Assembly}:{dependency.Namespace}.{dependency.Name}");
                    break;
                case HandleKind.TypeSpecification:
                    metadata.GetTypeSpecification((TypeSpecificationHandle)target).DecodeSignature(provider!, null); break;
                case HandleKind.MemberReference:
                    var member = metadata.GetMemberReference((MemberReferenceHandle)target);
                    Visit(member.Parent);
                    if (member.GetKind() == MemberReferenceKind.Field) member.DecodeFieldSignature(provider!, null);
                    else member.DecodeMethodSignature(provider!, null);
                    break;
                case HandleKind.MethodDefinition:
                    var method = metadata.GetMethodDefinition((MethodDefinitionHandle)target);
                    Visit(method.GetDeclaringType());
                    method.DecodeSignature(provider!, null);
                    break;
                case HandleKind.FieldDefinition:
                    var field = metadata.GetFieldDefinition((FieldDefinitionHandle)target);
                    Visit(field.GetDeclaringType());
                    field.DecodeSignature(provider!, null);
                    break;
                case HandleKind.MethodSpecification:
                    var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)target);
                    Visit(specification.Method);
                    specification.DecodeSignature(provider!, null); break;
                case HandleKind.StandaloneSignature:
                    var signature = metadata.GetStandaloneSignature((StandaloneSignatureHandle)target);
                    if (signature.GetKind() == StandaloneSignatureKind.LocalVariables) signature.DecodeLocalSignature(provider!, null);
                    else signature.DecodeMethodSignature(provider!, null);
                    break;
            }
        }
        private void SerializedType(string name)
        {
            if (forbidden(new("", name, ""))) Violations.Add($"{source.Namespace}.{source.Name} attribute -> {name}");
            // Generic serialized names also embed their argument type names.
            foreach (var component in name.Split(['[', ']', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (forbidden(new("", component, ""))) Violations.Add($"{source.Namespace}.{source.Name} attribute -> {component}");
        }

        private PrimitiveTypeCode SerializedEnumUnderlying(string serialized)
        {
            var parts = serialized.Split(',', 3, StringSplitOptions.TrimEntries);
            var separator = parts[0].LastIndexOf('.');
            return EnumUnderlying(new TypeName(parts.Length > 1 ? parts[1] : assembly, parts[0][..separator], parts[0][(separator + 1)..]));
        }

        private PrimitiveTypeCode EnumUnderlying(EntityHandle handle) => EnumUnderlying(Name(handle));

        private PrimitiveTypeCode EnumUnderlying(TypeName name)
        {
            if (enumTypes.TryGetValue(name, out var cached)) return cached;
            PrimitiveTypeCode result;
            if (name.Assembly == assembly) result = Underlying(metadata, name);
            else
            {
                var path = ResolveAssembly(name.Assembly);
                using var enumStream = File.OpenRead(path);
                using var enumPe = new PEReader(enumStream);
                result = Underlying(enumPe.GetMetadataReader(), name);
            }
            enumTypes.Add(name, result);
            return result;
        }
        private string ResolveAssembly(string name)
        {
            var file = name + ".dll";
            foreach (var directory in new[] { Path.GetDirectoryName(assemblyPath)!, AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)! })
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path)) return path;
            }
            // Class-library outputs omit assemblies supplied by a shared framework.
            var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
            var shared = runtime.Parent!.Parent!;
            foreach (var framework in shared.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
                foreach (var version in framework.EnumerateDirectories().OrderByDescending(d => d.Name == runtime.Name).ThenByDescending(d => d.Name, StringComparer.Ordinal))
                {
                    var path = Path.Combine(version.FullName, file);
                    if (File.Exists(path)) return path;
                }
            throw new FileNotFoundException("Referenced attribute enum assembly was not found in build outputs or shared frameworks.", file);
        }

        static PrimitiveTypeCode Underlying(MetadataReader reader, TypeName name)
        {
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (FullName(reader, typeHandle) != name.Namespace + "." + name.Name) continue;
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    if (reader.GetString(field.Name) != "value__") continue;
                    var signature = reader.GetBlobReader(field.Signature);
                    signature.ReadSignatureHeader();
                    return (PrimitiveTypeCode)signature.ReadSignatureTypeCode();
                }
            }
            // Framework facades forward enum definitions to System.Private.CoreLib.
            var runtimeType = Type.GetType(name.Namespace + "." + name.Name + ", " + name.Assembly, throwOnError: true)!;
            return Type.GetTypeCode(Enum.GetUnderlyingType(runtimeType)) switch
            {
                TypeCode.Byte => PrimitiveTypeCode.Byte,
                TypeCode.SByte => PrimitiveTypeCode.SByte,
                TypeCode.Int16 => PrimitiveTypeCode.Int16,
                TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
                TypeCode.Int32 => PrimitiveTypeCode.Int32,
                TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
                TypeCode.Int64 => PrimitiveTypeCode.Int64,
                TypeCode.UInt64 => PrimitiveTypeCode.UInt64,
                _ => throw new InvalidOperationException("Unsupported enum underlying type")
            };
        }

        private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            return type.GetDeclaringType() is { IsNil: false } parent
                ? FullName(reader, parent) + "+" + reader.GetString(type.Name)
                : reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }

        internal TypeName Name(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                var parent = type.GetDeclaringType();
                var parentType = parent.IsNil ? null : Name(parent);
                return new(assembly, parentType?.Namespace ?? metadata.GetString(type.Namespace), (parentType is null ? "" : parentType.Name + "+") + metadata.GetString(type.Name));
            }
            var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
            var scope = reference.ResolutionScope;
            var parentName = scope.Kind == HandleKind.TypeReference ? Name(scope) : null;
            var targetAssembly = scope.Kind == HandleKind.AssemblyReference
                ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : parentName?.Assembly ?? assembly;
            return new(targetAssembly, parentName?.Namespace ?? metadata.GetString(reference.Namespace), (parentName is null ? "" : parentName.Name + "+") + metadata.GetString(reference.Name));
        }
    }

    private sealed class SignatureReferences(MetadataReader metadata, Action<EntityHandle> visit, Action<string> serializedType, Func<EntityHandle, PrimitiveTypeCode> enumUnderlying, Func<string, PrimitiveTypeCode> serializedEnumUnderlying)
        : ISignatureTypeProvider<int, object?>, ICustomAttributeTypeProvider<int>
    {
        private readonly Dictionary<int, string> serializedNames = [];
        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) { visit(handle); return MetadataTokens.GetToken(handle); }
        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) { visit(handle); return MetadataTokens.GetToken(handle); }
        public int GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) { visit(handle); return 0; }
        public int GetArrayType(int elementType, ArrayShape shape) => 0;
        public int GetByReferenceType(int elementType) => 0;
        public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
        public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
        public int GetGenericMethodParameter(object? genericContext, int index) => 0;
        public int GetGenericTypeParameter(object? genericContext, int index) => 0;
        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
        public int GetPinnedType(int elementType) => 0;
        public int GetPointerType(int elementType) => 0;
        public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
        public int GetSZArrayType(int elementType) => 0;
        public int GetSystemType() => -1;
        public bool IsSystemType(int type)
        {
            if (type == -1) return true;
            if (type <= 0) return false;
            var handle = MetadataTokens.EntityHandle(type);
            return handle.Kind == HandleKind.TypeReference && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace) == "System"
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name) == "Type";
        }
        public int GetTypeFromSerializedName(string name)
        {
            serializedType(name);
            var id = -2 - serializedNames.Count;
            serializedNames.Add(id, name);
            return id;
        }
        public PrimitiveTypeCode GetUnderlyingEnumType(int type) => type < -1
            ? serializedEnumUnderlying(serializedNames[type]) : enumUnderlying(MetadataTokens.EntityHandle(type));
    }
}

namespace HVO;

/// <summary>
/// Attribute to mark classes for NamedOneOf source generation
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class NamedOneOfAttribute : Attribute
{
    public (string Name, Type Type)[] Cases { get; }

    public NamedOneOfAttribute(params object[] args)
    {
        if (args.Length % 2 != 0)
            throw new ArgumentException("Arguments must be in (name, type) pairs");

        Cases = new (string, Type)[args.Length / 2];
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not string name)
                throw new ArgumentException($"Expected string for case name at index {i}");
            if (args[i + 1] is not Type type)
                throw new ArgumentException($"Expected Type for case type at index {i + 1}");
            Cases[i / 2] = (name, type);
        }
    }
}


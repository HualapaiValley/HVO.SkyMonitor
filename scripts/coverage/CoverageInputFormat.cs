using System.Xml.Linq;

/// <summary>The raw Coverlet coordinates consumed by the pinned merger.</summary>
internal static class CoverageInputFormat
{
    internal static void Validate(XDocument document, string report)
    {
        var root = document.Root!;
        if (root.Elements("packages").Count() != 1)
        {
            throw new InvalidDataException($"Report '{report}' must have one coverage/packages container.");
        }

        foreach (var package in root.Descendants("package"))
        {
            _ = Required(package, "name", report);
            if (package.Parent != root.Element("packages") || package.Elements("classes").Count() != 1)
            {
                throw new InvalidDataException($"Report '{report}' has a package outside coverage/packages/package or without one classes container.");
            }
        }

        foreach (var entry in root.Descendants("class"))
        {
            if (entry.Parent?.Name != "classes" || entry.Parent.Parent?.Name != "package" ||
                entry.Parent.Parent.Parent != root.Element("packages"))
            {
                throw new InvalidDataException($"Report '{report}' has class records outside package/classes/class; the merger does not consume these coordinates.");
            }

            var name = Required(entry, "name", report);
            _ = Required(entry, "filename", report);
            if (name.Contains('$', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Report '{report}' has merger-excluded or unsupported class name '{name}'. Restore genuine C# Coverlet class records.");
            }

            if (entry.Elements("lines").Count() != 1 || entry.Elements("methods").Count() != 1)
            {
                throw new InvalidDataException($"Report '{report}' class '{name}' must have one lines and one methods container.");
            }
        }

        foreach (var method in root.Descendants("method"))
        {
            if (method.Parent?.Name != "methods" || method.Parent.Parent?.Name != "class" || method.Elements("lines").Count() != 1)
            {
                throw new InvalidDataException($"Report '{report}' has method records outside class/methods/method or without one lines container.");
            }

            _ = Required(method, "name", report);
        }

        foreach (var line in root.Descendants("line"))
        {
            if (line.Parent?.Name != "lines" || (line.Parent.Parent?.Name != "class" && line.Parent.Parent?.Name != "method"))
            {
                throw new InvalidDataException($"Report '{report}' has line records outside the class/method lines consumed by the merger.");
            }
        }
    }

    internal static string SourcePath(string repositoryRoot, string owner, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains('\\', StringComparison.Ordinal) ||
            filename.Contains("//", StringComparison.Ordinal) || filename.Split('/').Any(static segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Non-canonical coverage filename '{filename}': path aliases (./, //, ../ or backslashes) are not accepted. Restore the plain Coverlet source path.");
        }

        var sourceRoot = Path.Combine(repositoryRoot, "src") + "/";
        if (Path.IsPathRooted(filename))
        {
            if (!filename.StartsWith(sourceRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Out-of-tree coverage filename '{filename}': expected a plain source path beneath '{sourceRoot}'.");
            }

            return filename[sourceRoot.Length..];
        }

        // Genuine project-root sources can be relative to their assembly.
        return filename.StartsWith(owner + "/", StringComparison.Ordinal) ? filename : owner + "/" + filename;
    }

    private static string Required(XElement element, string attribute, string report)
    {
        var value = (string?)element.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Report '{report}' has missing/empty {element.Name} {attribute}; the merger requires this metadata for every input.");
        }

        return value;
    }
}

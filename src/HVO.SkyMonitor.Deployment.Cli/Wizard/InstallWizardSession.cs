using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Wizard;

/// <summary>How the owner's first password is set: never typed into the wizard, shown or saved in the answers.</summary>
internal enum OwnerPasswordChoice
{
    /// <summary>The documented first sign-in password, which must be changed at the first sign-in.</summary>
    Default,

    /// <summary>A password the installer generates into a file only the owner can read (<c>--generate-password</c>).</summary>
    Generate,

    /// <summary>A password the operator put in a file (<c>--password-file</c>).</summary>
    File
}

/// <summary>What the plan check found: the plan to carry out, or why the install cannot go ahead.</summary>
/// <param name="Result">The plan's result, when the install can be carried out.</param>
/// <param name="Problem">Why not, as the installer said it.</param>
/// <param name="Word">
/// The problem's kind, in words: <c>[REFUSED]</c> for a refusal (exit 3), <c>[CHECK]</c> for answers the installer
/// does not take (exit 2), <c>[ERROR]</c> for a check that failed (exit 1).
/// </param>
/// <param name="Details">More about the problem: the host prerequisites' findings and their remedies.</param>
internal sealed record WizardPlanCheck(InstallationResult? Result, string? Problem, string Word, IReadOnlyList<string> Details)
{
    /// <summary>The word of a refusal.</summary>
    public const string Refusal = "[REFUSED]";

    /// <summary>The word of answers the installer does not take.</summary>
    public const string Invalid = "[CHECK]";

    /// <summary>The word of a check that failed.</summary>
    public const string Failure = "[ERROR]";

    /// <summary>Whether the install can go ahead.</summary>
    public bool CanInstall => Result is not null;
}

/// <summary>
/// The questions an install asks, the answers the wizard holds for them, and what it does with them (install contract
/// section 5, #1317). A question the command line answered is fixed: its page shows the answer and does not ask it, and a
/// page with nothing left to ask is passed over. Every other answer starts from the answers saved by an earlier run of
/// the wizard (<see cref="DefaultAnswersPath"/>), or from the installer's defaults.
/// <para>
/// The wizard is not a second install path. It produces the answers file <c>--answers</c> reads
/// (<see cref="ToAnswers"/>), checks it with exactly <c>cameraagent install --answers FILE --plan</c>, and on Install the
/// installer runs <c>cameraagent install --answers FILE</c> (<see cref="Arguments"/>), with the options the command line
/// handed over beside it as <c>install.sh</c> does: the release, its image source, the catalog and <c>--no-download</c>.
/// </para>
/// </summary>
internal sealed partial class InstallWizardSession
{
    /// <summary>The options that stay on the command line beside the answers: what <c>install.sh</c> hands over, whose paths are its own.</summary>
    private static readonly string[] HandedOver = ["--release", "--image-source", "--catalog-bundle"];

    private readonly Dictionary<string, string?> _given;
    private readonly HashSet<string> _flags;

    /// <summary>
    /// A session for the command line <paramref name="args"/> (<c>cameraagent install</c> and its options). A new
    /// installation is given <paramref name="newInstanceId"/>, or a new random ID.
    /// </summary>
    public InstallWizardSession(string[] args, ProgramServices services, InstallTerminal terminal, string version, Guid? newInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(version);
        Services = services;
        Terminal = terminal;
        Version = version;
        (_given, _flags) = CommandLine.ReadInstallOptions(InstallWizardGate.InstallArguments(args));
        Validate();
        DefaultAnswersPath = AnswersPathFor(terminal.Environment);
        Saved = ReadSaved(DefaultAnswersPath, out var savedProblem);
        SavedProblem = savedProblem;
        Answers = Start(Saved, newInstanceId ?? Guid.NewGuid());
        AnswersPath = DefaultAnswersPath;
    }

    /// <summary>What the installer parses and runs through: the plan check and the install use these.</summary>
    public ProgramServices Services { get; }

    /// <summary>The terminal.</summary>
    public InstallTerminal Terminal { get; }

    /// <summary>The installer's version, as its <c>--version</c> says it.</summary>
    public string Version { get; }

    /// <summary>Where the answers are saved unless the operator chooses another file: <c>~/.config/hvo-sky/install-answers.json</c>.</summary>
    public string DefaultAnswersPath { get; }

    /// <summary>The answers an earlier run saved at <see cref="DefaultAnswersPath"/>, which the questions start from.</summary>
    public InstallAnswers? Saved { get; }

    /// <summary>Why the saved answers could not be read, when they could not.</summary>
    public string? SavedProblem { get; }

    /// <summary>The answers so far.</summary>
    public WizardAnswers Answers { get; }

    /// <summary>Where Install saves the answers.</summary>
    public string AnswersPath { get; set; }

    /// <summary>Where the plan check keeps its private copy of the answers, in a folder of its own that it removes.</summary>
    public string ScratchRoot { get; set; } = Path.GetTempPath();

    /// <summary>Whether the command line gave <paramref name="option"/>, a value option or a flag.</summary>
    public bool Given(string option) => _given.ContainsKey(option) || _flags.Contains(option);

    /// <summary>The value the command line gave <paramref name="option"/>.</summary>
    public string? GivenValue(string option) => _given.TryGetValue(option, out var value) ? value : null;

    /// <summary>Where the instance ID came from, for the operator.</summary>
    public string InstanceIdSource
        => Given("--instance-id") ? "from --instance-id"
            : Saved?.InstanceId == Answers.InstanceId ? "from the saved answers: installing again changes nothing that is already so"
            : "new";

    /// <summary>The answers as the answers file holds them: what <c>--answers</c> reads.</summary>
    public InstallAnswers ToAnswers()
    {
        var a = Answers;
        var handedRelease = Given("--release") || Given("--image-ref");
        return new InstallAnswers
        {
            SchemaVersion = InstallAnswers.CurrentSchemaVersion,
            InstanceId = a.InstanceId,
            FriendlyName = a.FriendlyName.Trim(),
            OwnerEmail = Blank(a.OwnerEmail),
            BindAddress = a.BindAddress.Trim(),
            Port = int.Parse(a.Port, NumberStyles.None, CultureInfo.InvariantCulture),
            ProductRoot = GivenValue("--product-root"),
            CatalogBundle = Given("--catalog-bundle") ? null : Blank(a.CatalogBundle),
            Release = handedRelease ? null : Blank(a.Release),
            ImageSource = Given("--release") || Given("--image-ref") || Given("--image-source") || Blank(a.Release) is null ? null : a.ImageSource,
            ImageReference = GivenValue("--image-ref"),
            ImageArchive = GivenValue("--image-archive"),
            ImageArchiveSha256 = GivenValue("--image-archive-sha256"),
            PasswordFile = a.Password == OwnerPasswordChoice.File ? Blank(a.PasswordFile) : null,
            LatitudeDegrees = Number(a.Latitude),
            LongitudeDegrees = Number(a.Longitude),
            ElevationMeters = Number(a.Elevation),
            TimeZoneId = a.TimeZone.Trim(),
            AcknowledgePlaintextHttp = IsLoopback(a.BindAddress) ? null : a.AcknowledgePlaintextHttp,
            GeneratePassword = a.Password == OwnerPasswordChoice.Generate ? true : null,
            ReplayProfile = a.ReplayProfile,
            Cpus = Blank(a.Cpus) is { } cpus ? decimal.Parse(cpus, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : null,
            Memory = Blank(a.Memory)
        };
    }

    /// <summary>The answers file's text: strict JSON with its schema version, and never a secret.</summary>
    public string AnswersJson() => JsonSerializer.Serialize(ToAnswers(), WizardAnswersJsonContext.Default.InstallAnswers) + "\n";

    /// <summary>
    /// The command line the install runs: <c>cameraagent install --answers <paramref name="answersPath"/></c>, with the
    /// release, its image source, the catalog and <c>--no-download</c> as the command line handed them over.
    /// </summary>
    public string[] Arguments(string answersPath)
    {
        ArgumentNullException.ThrowIfNull(answersPath);
        var arguments = new List<string> { "cameraagent", "install", CommandLine.AnswersOption, answersPath };
        foreach (var option in HandedOver)
        {
            if (GivenValue(option) is { } value)
            {
                arguments.Add(option);
                arguments.Add(value);
            }
        }

        if (_flags.Contains("--no-download"))
        {
            arguments.Add("--no-download");
        }

        return [.. arguments];
    }

    /// <summary>
    /// Checks the answers exactly as <c>cameraagent install --answers FILE --plan</c> does, through the same parser and
    /// installer, with a private copy of the answers that is removed afterwards. The check changes nothing; it runs as
    /// <c>--json</c> so that it writes nothing to the terminal the wizard is drawing on, and says what it found here.
    /// </summary>
    public async Task<WizardPlanCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var folder = Path.Combine(ScratchRoot, $"hvo-sky-install-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "answers.json");
        try
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await WriteAnswersAsync(path, cancellationToken).ConfigureAwait(false);
            var command = Services.Parse([.. Arguments(path), CommandLine.PlanOption, "--json"]);
            if (command is not InstallDeploymentCommand install)
            {
                return new(null, "The answers did not make an install.", WizardPlanCheck.Invalid, []);
            }

            var result = await Services.Install(install.Request, cancellationToken).ConfigureAwait(false);
            return new(result, null, string.Empty, []);
        }
        catch (InstallUsageException exception)
        {
            return new(null, Redaction.SafeDiagnostic(exception.Message), WizardPlanCheck.Invalid, []);
        }
        catch (HostPrerequisiteException exception)
        {
            var details = exception.Report is { } report
                ? HostPrerequisites.Render(report).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [];
            return new(null, Redaction.SafeDiagnostic(exception.Message), WizardPlanCheck.Refusal, details);
        }
        catch (InstallerException exception)
        {
            return new(null, Redaction.SafeDiagnostic(exception.Message), exception.IsRefusal ? WizardPlanCheck.Refusal : WizardPlanCheck.Failure, []);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// Saves the answers at <paramref name="path"/> for Install, readable and writable by the operator only. The folder
    /// of the default path is made (<c>0700</c>) when it is not there; another folder must exist.
    /// </summary>
    public string SaveAnswers(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path.Trim());
        if (string.Equals(full, DefaultAnswersPath, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (var stream = new FileStream(full, AnswersFileOptions))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(AnswersJson()));
        }

        AnswersPath = full;
        return full;
    }

    /// <summary>The problem with <paramref name="value"/> as the camera's name, or null.</summary>
    public static string? NameProblem(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.Trim();
        return name.Length == 0 ? "The camera needs a name."
            : name.Length > InstallRequest.MaximumFriendlyNameLength ? $"The camera's name is at most {InstallRequest.MaximumFriendlyNameLength} characters."
            : name.Any(char.IsControl) ? "The camera's name cannot hold control characters."
            : null;
    }

    /// <summary>The problem with <paramref name="value"/> as an absolute path for <paramref name="what"/>, or null.</summary>
    public static string? PathProblem(string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = value.Trim();
        return path.Length == 0 ? $"Give {what}."
            : !Path.IsPathFullyQualified(path) ? $"{Capital(what)} must be an absolute path, starting with /."
            : path.Any(char.IsControl) ? $"{Capital(what)} cannot hold control characters."
            : null;
    }

    /// <summary>The problem with the network answers, or null.</summary>
    public static string? NetworkProblem(string address, string port, bool acknowledged)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(port);
        if (!IPAddress.TryParse(address.Trim(), out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return "The address must be an IPv4 address, such as 127.0.0.1 or 0.0.0.0.";
        }

        if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
        {
            return "The port must be a number from 1 to 65535.";
        }

        return !IPAddress.IsLoopback(parsed) && !acknowledged
            ? "Other computers reach this address over plain HTTP: tick the box to say you know."
            : null;
    }

    /// <summary>The problem with the observatory's location, or null.</summary>
    public static string? LocationProblem(string latitude, string longitude, string elevation, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(latitude);
        ArgumentNullException.ThrowIfNull(longitude);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (!TryNumber(latitude, out var lat) || lat is < -90 or > 90)
        {
            return "The latitude must be a number of degrees from -90 (south) to 90 (north).";
        }

        if (!TryNumber(longitude, out var lon) || lon is < -180 or > 180)
        {
            return "The longitude must be a number of degrees from -180 (west) to 180 (east).";
        }

        if (!TryNumber(elevation, out _))
        {
            return "The elevation must be a number of metres.";
        }

        if (Blank(timeZone) is not { } zone)
        {
            return "Give the time zone, such as America/Phoenix or UTC.";
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return null;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return $"This machine does not know the time zone '{zone}': give one such as America/Phoenix or UTC.";
        }
    }

    /// <summary>The problem with the container limits, or null: each one empty for the default, or what <c>--cpus</c> and <c>--memory</c> take.</summary>
    public static string? LimitsProblem(string cpus, string memory)
    {
        ArgumentNullException.ThrowIfNull(cpus);
        ArgumentNullException.ThrowIfNull(memory);
        decimal? cpuCount = null;
        if (Blank(cpus) is { } given)
        {
            if (!decimal.TryParse(given, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            {
                return "The CPUs must be a number, such as 2 or 2.5, or empty for the default.";
            }

            cpuCount = parsed;
        }

        try
        {
            _ = CameraAgentContainerResources.Resolve(cpuCount, Blank(memory));
            return null;
        }
        catch (InstallUsageException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>The problem with the owner's answers, or null.</summary>
    public static string? OwnerProblem(string email, OwnerPasswordChoice password, string passwordFile)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordFile);
        if (Blank(email) is { } given && !Email().IsMatch(given))
        {
            return "The owner's email must be an address such as you@example.com, or empty for the default.";
        }

        return password == OwnerPasswordChoice.File ? PathProblem(passwordFile, "the password file") : null;
    }

    /// <summary>Whether <paramref name="address"/> is a loopback address, which only this machine reaches.</summary>
    public static bool IsLoopback(string address)
        => IPAddress.TryParse(address?.Trim(), out var parsed) && IPAddress.IsLoopback(parsed);

    /// <summary><paramref name="value"/> trimmed, or null when there is nothing in it.</summary>
    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static double Number(string value) => double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool TryNumber(string value, out double number)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    // The saved answers, when there are some and they are an answers file; never fatal: the wizard says why and starts
    // from the defaults.
    private static InstallAnswers? ReadSaved(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return InstallAnswers.Read(path);
        }
        catch (InstallUsageException exception)
        {
            problem = Redaction.SafeDiagnostic(exception.Message);
            return null;
        }
    }

    private static string AnswersPathFor(Func<string, string?> environment)
    {
        var config = environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(environment("HOME") is { Length: > 0 } home ? home : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config");
        return Path.GetFullPath(Path.Combine(config, "hvo-sky", "install-answers.json"));
    }

    // Every value the command line gave is checked as the command line would check it, so a mistake there is a usage
    // error before the wizard opens rather than a page that cannot be left.
    private void Validate()
    {
        var unknown = _given.Keys.FirstOrDefault(option => !CommandLine.InstallValueOptions.Contains(option, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new InstallUsageException($"Unknown option '{unknown}'.");
        }

        _ = CommandLine.ParseGuid(GivenValue("--instance-id"), "--instance-id");
        _ = CommandLine.ParseInt(GivenValue("--port"), 5130, "--port");
        _ = CommandLine.ParseDouble(GivenValue("--latitude"), 0, "--latitude");
        _ = CommandLine.ParseDouble(GivenValue("--longitude"), 0, "--longitude");
        _ = CommandLine.ParseDouble(GivenValue("--elevation"), 0, "--elevation");
        _ = CommandLine.ParseCpus(GivenValue("--cpus"));
        _ = CommandLine.ParseImageSource(GivenValue("--image-source"));
        _ = CommandLine.ParseReplayProfile(GivenValue("--replay-profile"));
    }

    // Each answer: the command line's, else the saved one, else the installer's default.
    private WizardAnswers Start(InstallAnswers? saved, Guid newInstanceId)
    {
        string Text(string option, string? savedValue, string fallback) => GivenValue(option) ?? savedValue ?? fallback;
        static string Invariant(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var password = Given("--password-file") ? OwnerPasswordChoice.File
            : Given("--generate-password") ? OwnerPasswordChoice.Generate
            : saved?.PasswordFile is not null ? OwnerPasswordChoice.File
            : saved?.GeneratePassword == true ? OwnerPasswordChoice.Generate
            : OwnerPasswordChoice.Default;
        return new WizardAnswers
        {
            InstanceId = CommandLine.ParseGuid(GivenValue("--instance-id"), "--instance-id") ?? saved?.InstanceId ?? newInstanceId,
            FriendlyName = Text("--friendly-name", saved?.FriendlyName, string.Empty),
            OwnerEmail = Text("--owner-email", saved?.OwnerEmail, string.Empty),
            Release = Text("--release", saved?.Release, string.Empty),
            ImageSource = CommandLine.ParseImageSource(GivenValue("--image-source")) ?? saved?.ImageSource ?? ReleaseImageSource.Archive,
            CatalogBundle = Text("--catalog-bundle", saved?.CatalogBundle, string.Empty),
            BindAddress = Text("--bind-address", saved?.BindAddress, "127.0.0.1"),
            Port = Text("--port", saved?.Port?.ToString(CultureInfo.InvariantCulture), "5130"),
            AcknowledgePlaintextHttp = Given("--acknowledge-plaintext-http") || saved?.AcknowledgePlaintextHttp == true,
            Latitude = Text("--latitude", saved is null ? null : Invariant(saved.LatitudeDegrees), "0"),
            Longitude = Text("--longitude", saved is null ? null : Invariant(saved.LongitudeDegrees), "0"),
            Elevation = Text("--elevation", saved is null ? null : Invariant(saved.ElevationMeters), "0"),
            TimeZone = Text("--time-zone", saved?.TimeZoneId, "UTC"),
            Cpus = Text("--cpus", saved?.Cpus?.ToString(CultureInfo.InvariantCulture), string.Empty),
            Memory = Text("--memory", saved?.Memory, string.Empty),
            ReplayProfile = GivenValue("--replay-profile") is { } profile ? CommandLine.ParseReplayProfile(profile) : saved?.ReplayProfile ?? CameraAgentReplayProfile.InProcess,
            Password = password,
            PasswordFile = Text("--password-file", saved?.PasswordFile, string.Empty)
        };
    }

    // Written whole, replacing what was there, readable and writable by the operator only.
    private static FileStreamOptions AnswersFileOptions => new()
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    };

    private async Task WriteAnswersAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, AnswersFileOptions);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(AnswersJson()), cancellationToken).ConfigureAwait(false);
        }
    }

    [GeneratedRegex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}

/// <summary>The wizard's answers while it runs: text as typed, and the choices made.</summary>
internal sealed class WizardAnswers
{
    public Guid InstanceId { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string Release { get; set; } = string.Empty;
    public ReleaseImageSource ImageSource { get; set; } = ReleaseImageSource.Archive;
    public string CatalogBundle { get; set; } = string.Empty;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "5130";
    public bool AcknowledgePlaintextHttp { get; set; }
    public string Latitude { get; set; } = "0";
    public string Longitude { get; set; } = "0";
    public string Elevation { get; set; } = "0";
    public string TimeZone { get; set; } = "UTC";
    public string Cpus { get; set; } = string.Empty;
    public string Memory { get; set; } = string.Empty;
    public CameraAgentReplayProfile ReplayProfile { get; set; } = CameraAgentReplayProfile.InProcess;
    public OwnerPasswordChoice Password { get; set; } = OwnerPasswordChoice.Default;
    public string PasswordFile { get; set; } = string.Empty;
}

/// <summary>The answers file as the wizard writes it: indented, members in camel case, enums as strings, nulls left out.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(InstallAnswers))]
internal sealed partial class WizardAnswersJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

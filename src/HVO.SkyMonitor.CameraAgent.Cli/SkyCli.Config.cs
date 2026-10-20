using System.CommandLine;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>One setting a change would alter: its current and new value, and what changing it involves.</summary>
internal sealed record SkySettingChange(SkySettingField Field, JsonElement Value)
{
    public string Key => Field.Key;

    public string Group => Field.Setting.Group;

    public object ToJson() => new
    {
        group = Group,
        key = Key,
        title = Field.Setting.Title,
        from = Field.State?.Value,
        to = Value,
        application = Field.Setting.Application,
        safety = Field.Setting.Safety,
        canWrite = Field.CanWrite,
        readOnlyReason = Field.ReadOnlyReason
    };
}

/// <summary>
/// A settings file for <c>config diff</c> and <c>config apply</c>: strict JSON, <c>{"schemaVersion": 1, "settings":
/// {"&lt;key&gt;": value}}</c>, keyed by full setting keys, with values in each setting's JSON type. Unknown members are
/// errors, as in the installer's answers file.
/// </summary>
internal sealed record SkySettingsFile(int SchemaVersion, IReadOnlyDictionary<string, JsonElement> Settings);

internal static partial class SkyCli
{
    internal const int SettingsFileSchemaVersion = 1;

    internal sealed partial class CommandBuilder
    {
        private Command CreateConfigCommand()
        {
            var command = new Command(
                "config",
                "Read and change CameraAgent's operator settings, as its settings catalogue describes them. A change is to one group at a time, checked against the version read.");
            command.Add(CreateConfigShowCommand());
            command.Add(CreateConfigGetCommand());
            command.Add(CreateConfigSetCommand());
            command.Add(CreateConfigDiffCommand());
            command.Add(CreateConfigApplyCommand());
            return command;
        }

        private static async Task<SkySettingsForm> LoadFormAsync(SkyApiClient client, CancellationToken cancellationToken)
        {
            var catalogue = await client.GetAsync<SkySettingsCatalogue>(SkySettingsContract.CatalogueRoute, cancellationToken).ConfigureAwait(false);
            var values = await client.GetAsync<SkySettingsValues>(SkySettingsContract.Route, cancellationToken).ConfigureAwait(false);
            return SkySettingsForm.Create(catalogue, values);
        }

        private Command CreateConfigShowCommand()
        {
            var group = new Argument<string?>("group") { Description = "Show only this group.", Arity = ArgumentArity.ZeroOrOne };
            RejectOption(group);
            var command = new Command("show", "Show the settings by group, with each value, where it comes from, and whether it can be changed here.") { group };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var groups = form.Groups;
                if (parseResult.GetValue(group) is { } name)
                {
                    groups = [form.FindGroup(name) ?? throw new SkyCliUsageException(
                        $"There is no settings group '{name}'. Groups: {string.Join(", ", form.Groups.Select(static candidate => candidate.Name))}.")];
                }

                if (context.Json)
                {
                    context.WriteJson(new
                    {
                        schemaVersion = SkySettingsContract.SchemaVersion,
                        groups = groups.Select(shown => new
                        {
                            name = shown.Name,
                            title = shown.Descriptor.Title,
                            description = shown.Descriptor.Description,
                            store = shown.Descriptor.Store,
                            version = shown.State?.Version,
                            canWrite = shown.State?.CanWrite == true,
                            readOnlyReason = shown.State?.ReadOnlyReason,
                            settings = shown.Fields.Select(DescribeFieldJson)
                        })
                    });
                    return (int)SkyExitCode.Success;
                }

                var first = true;
                foreach (var shown in groups)
                {
                    if (!first)
                    {
                        await context.Out.WriteLineAsync();
                    }

                    first = false;
                    var note = shown.State is { CanWrite: false, ReadOnlyReason: { } reason } ? $" (read-only: {reason})" : string.Empty;
                    await context.Out.WriteLineAsync($"[{shown.Name}] {shown.Descriptor.Title}{note}");
                    SkyCliFormat.WriteTable(
                        context.Out,
                        ["SETTING", "VALUE", "SOURCE", "NOTES"],
                        shown.Fields.Select(field => (IReadOnlyList<string>)
                            [field.Key, field.DisplayValue, field.State?.Source ?? "(not returned)", SkySettingText.DescribeNotes(field)]));
                }

                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private static object DescribeFieldJson(SkySettingField field) => new
        {
            key = field.Key,
            title = field.Setting.Title,
            description = field.Setting.Description,
            type = field.Setting.Type,
            nullable = field.Setting.Nullable,
            minimum = field.Setting.Minimum,
            maximum = field.Setting.Maximum,
            maximumLength = field.Setting.MaximumLength,
            allowedValues = field.Choices,
            unit = field.Setting.Unit,
            @default = field.Setting.Default,
            safety = field.Setting.Safety,
            application = field.Setting.Application,
            appliesAfterRestart = field.Setting.AppliesAfterRestart,
            secret = field.Setting.Secret,
            value = field.Setting.Secret ? null : field.State?.Value,
            isSet = field.State?.IsSet,
            source = field.State?.Source,
            sourceDetail = field.State?.SourceDetail,
            canWrite = field.CanWrite,
            readOnlyReason = field.ReadOnlyReason,
            pendingValue = field.Setting.Secret ? null : field.State?.PendingValue,
            problem = field.State?.Problem
        };

        private static SkySettingField FindField(SkySettingsForm form, string key, bool exactOnly = false)
            => form.FindField(key, exactOnly) ?? throw new SkyCliUsageException(
                $"There is no setting '{key}'{(exactOnly ? string.Empty : ", or it is ambiguous")}. '{CommandName} config show' lists them.");

        private Command CreateConfigGetCommand()
        {
            var key = new Argument<string>("key") { Description = "The setting: its full key, or the part after its group when that is unique." };
            RejectOption(key);
            var command = new Command("get", "Show one setting: its value, source, rules, and what changing it involves.") { key };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var field = FindField(form, parseResult.GetRequiredValue(key));
                if (context.Json)
                {
                    context.WriteJson(DescribeFieldJson(field));
                    return (int)SkyExitCode.Success;
                }

                var setting = field.Setting;
                var rows = new List<(string, string)>
                {
                    ("Setting", $"{setting.Title} ({setting.Key})"),
                    ("Group", field.Group.Name),
                    ("Value", field.DisplayValue),
                    ("Default", setting.Secret ? "(none)" : SkySettingText.Describe(setting.Default)),
                    ("Source", field.State is { } state
                        ? state.Source + (state.SourceDetail is { Length: > 0 } detail ? $" ({detail})" : string.Empty)
                        : "(not returned)"),
                    ("Accepts", SkySettingText.DescribeRule(field)),
                    ("Applies", SkySettingText.DescribeApplication(setting.Application)),
                    ("Affects", SkySettingText.DescribeSafety(setting.Safety)),
                    ("Change", field.CanWrite ? "you may change it" : $"read-only: {field.ReadOnlyReason}")
                };
                if (field.State?.PendingValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } pending)
                {
                    rows.Add(("Pending", $"{SkySettingText.Describe(pending)}, saved and not in effect yet"));
                }

                if (field.State?.Problem is { Length: > 0 } problem)
                {
                    rows.Add(("Problem", problem));
                }

                rows.Add(("About", setting.Description));
                SkyCliFormat.WriteRows(context.Out, rows);
                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private Command CreateConfigSetCommand()
        {
            var assignments = new Argument<string[]>("key=value")
            {
                Description = "One or more settings of one group, as key=value. An empty value clears a setting that may be empty.",
                Arity = ArgumentArity.OneOrMore
            };
            RejectOptions(assignments);
            var reason = ReasonOption();
            var dryRun = new Option<bool>("--dry-run") { Description = "Show the change without sending it." };
            var force = ForceOption("Change the location and time basis of future captures without asking.");
            var command = new Command(
                "set",
                "Change settings of one group. Each value is checked against the catalogue first, and the change is sent with the group's version, so a change made meanwhile is refused rather than overwritten.")
            {
                assignments,
                reason,
                dryRun,
                force
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var why = ReadReason(parseResult, reason);
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var changes = new List<SkySettingChange>();
                var given = new HashSet<string>(StringComparer.Ordinal);
                var groups = new List<string>();
                foreach (var assignment in parseResult.GetRequiredValue(assignments))
                {
                    var separator = assignment.IndexOf('=', StringComparison.Ordinal);
                    if (separator <= 0)
                    {
                        throw new SkyCliUsageException($"'{assignment}' is not key=value.");
                    }

                    var field = FindField(form, assignment[..separator].Trim());
                    if (field.Setting.Secret)
                    {
                        throw new SkyCliUsageException($"{field.Key} is a secret, and {CommandName} never takes a secret on the command line.");
                    }

                    if (!given.Add(field.Key))
                    {
                        throw new SkyCliUsageException($"{field.Key} is given twice.");
                    }

                    var value = SkySettingsForm.ParseText(field, assignment[(separator + 1)..]);
                    if (!SkySettingsForm.SameValue(field.State?.Value, value))
                    {
                        changes.Add(new SkySettingChange(field, value));
                    }

                    groups.Add(field.Setting.Group);
                }

                if (groups.Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    throw new SkyCliUsageException(
                        $"Change one group at a time. These settings are in: {string.Join(", ", groups.Distinct(StringComparer.Ordinal))}.");
                }

                var outcomes = await ApplyChangesAsync(context, client, form, changes, why, parseResult.GetValue(dryRun), parseResult.GetValue(force), cancellationToken)
                    .ConfigureAwait(false);
                return outcomes;
            });
            return command;
        }

        private Command CreateConfigDiffCommand()
        {
            var file = new Argument<string>("file") { Description = "A settings file, or - for standard input: {\"schemaVersion\": 1, \"settings\": {\"<key>\": value}}." };
            RejectOption(file, allowDash: true);
            var command = new Command("diff", "Show how a settings file differs from the settings CameraAgent has. Nothing is sent.") { file };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var settings = ReadSettingsFile(context, parseResult.GetRequiredValue(file));
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var (changes, unchanged) = Compare(form, settings);
                if (context.Json)
                {
                    context.WriteJson(new { changes = changes.Select(static change => change.ToJson()), unchanged });
                    return (int)SkyExitCode.Success;
                }

                if (changes.Count == 0)
                {
                    await context.Out.WriteLineAsync($"No differences: CameraAgent has every value in the file ({unchanged.Count} settings).");
                    return (int)SkyExitCode.Success;
                }

                WriteChanges(context, form, changes);
                await context.Out.WriteLineAsync($"{changes.Count} to change, {unchanged.Count} already as in the file. '{CommandName} config apply' applies them.");
                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private Command CreateConfigApplyCommand()
        {
            var file = new Argument<string>("file") { Description = "A settings file, or - for standard input, as for 'config diff'." };
            RejectOption(file, allowDash: true);
            var reason = ReasonOption();
            var dryRun = new Option<bool>("--dry-run") { Description = "Show the changes without sending them." };
            var force = ForceOption("Change the location and time basis of future captures without asking.");
            var command = new Command(
                "apply",
                "Change every setting that differs from a settings file, one group at a time in catalogue order. Every value is checked, and a read-only one refused, before anything is sent; running it again continues after a failure.")
            {
                file,
                reason,
                dryRun,
                force
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var why = ReadReason(parseResult, reason);
                var settings = ReadSettingsFile(context, parseResult.GetRequiredValue(file));
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var (changes, _) = Compare(form, settings);
                return await ApplyChangesAsync(context, client, form, changes, why, parseResult.GetValue(dryRun), parseResult.GetValue(force), cancellationToken)
                    .ConfigureAwait(false);
            });
            return command;
        }

        /// <summary>Reads a settings file strictly; every problem is a usage error (exit 2), and no value is repeated.</summary>
        private static Dictionary<string, JsonElement> ReadSettingsFile(SkyCliContext context, string path)
        {
            string text;
            try
            {
                text = path == "-"
                    ? context.Host.IsInteractive
                        ? throw new SkyCliUsageException("'-' reads the settings file from standard input, which is a terminal here.")
                        : context.ReadAllInput()
                    : File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new SkyCliUsageException($"The settings file cannot be read: {error.Message}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            }
            catch (JsonException)
            {
                throw new SkyCliUsageException("The settings file is not valid JSON.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new SkyCliUsageException("The settings file must be a JSON object with schemaVersion and settings.");
                }

                var names = root.EnumerateObject().Select(static member => member.Name).ToArray();
                if (names.FirstOrDefault(static name => name is not ("schemaVersion" or "settings")) is { } unknown)
                {
                    throw new SkyCliUsageException($"The settings file has an unknown member '{unknown}'. It has only schemaVersion and settings.");
                }

                if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
                {
                    throw new SkyCliUsageException("The settings file names a member twice.");
                }

                // The kind first: TryGetInt32 throws on a string, which would make a usage error a failure.
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                    || !schema.TryGetInt32(out var version) || version != SettingsFileSchemaVersion)
                {
                    throw new SkyCliUsageException($"The settings file must have \"schemaVersion\": {SettingsFileSchemaVersion}.");
                }

                if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                {
                    throw new SkyCliUsageException("The settings file must have a \"settings\" object of setting keys and values.");
                }

                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var member in settings.EnumerateObject())
                {
                    if (!values.TryAdd(member.Name, member.Value.Clone()))
                    {
                        throw new SkyCliUsageException($"The settings file gives {member.Name} twice.");
                    }
                }

                return values;
            }
        }

        /// <summary>Checks every value of a settings file against the catalogue, and splits them into changes and unchanged keys.</summary>
        private static (List<SkySettingChange> Changes, List<string> Unchanged) Compare(
            SkySettingsForm form,
            IReadOnlyDictionary<string, JsonElement> settings)
        {
            var changes = new List<SkySettingChange>();
            var unchanged = new List<string>();
            foreach (var (key, value) in settings)
            {
                var field = FindField(form, key, exactOnly: true);
                if (!string.Equals(field.Key, key, StringComparison.Ordinal))
                {
                    throw new SkyCliUsageException($"The settings file names '{key}'; the setting is {field.Key}, as written.");
                }

                if (field.Setting.Secret)
                {
                    throw new SkyCliUsageException($"{field.Key} is a secret, and a settings file never holds one.");
                }

                var checkedValue = SkySettingsForm.Check(field, value);
                if (SkySettingsForm.SameValue(field.State?.Value, checkedValue))
                {
                    unchanged.Add(field.Key);
                }
                else
                {
                    changes.Add(new SkySettingChange(field, checkedValue));
                }
            }

            // In catalogue order, so a change shows and is sent group by group as CameraAgent lists them.
            var order = form.Fields.Select(static (field, index) => (field.Key, index)).ToDictionary(static pair => pair.Key, static pair => pair.index, StringComparer.Ordinal);
            changes.Sort((left, right) => order[left.Key].CompareTo(order[right.Key]));
            unchanged.Sort((left, right) => order[left].CompareTo(order[right]));
            return (changes, unchanged);
        }

        private static void WriteChanges(SkyCliContext context, SkySettingsForm form, IReadOnlyList<SkySettingChange> changes)
        {
            foreach (var group in changes.GroupBy(static change => change.Group))
            {
                var descriptor = form.FindGroup(group.Key)!.Descriptor;
                context.Out.WriteLine($"[{descriptor.Name}] {descriptor.Title}:");
                foreach (var change in group)
                {
                    var notes = new List<string>();
                    if (!change.Field.CanWrite)
                    {
                        notes.Add($"read-only: {change.Field.ReadOnlyReason}");
                    }

                    if (change.Field.Setting.Safety == SkySettingsContract.CaptureGeometrySafety)
                    {
                        notes.Add("changes the location and time basis of future captures");
                    }

                    if (change.Field.Setting.Application != "Immediate")
                    {
                        notes.Add("applies " + SkySettingText.DescribeApplication(change.Field.Setting.Application));
                    }

                    context.Out.WriteLine(
                        $"  {change.Field.Setting.Title} ({change.Key}): {change.Field.DisplayValue} -> {SkySettingText.Describe(change.Value)}"
                        + (notes.Count > 0 ? $"  [{string.Join("; ", notes)}]" : string.Empty));
                }
            }
        }

        /// <summary>
        /// Sends <paramref name="changes"/>, one group at a time in catalogue order. A read-only setting is refused, and a
        /// change of the location and time basis is confirmed, before anything is sent. When a group fails after an earlier
        /// group was saved, the command exits 1 ("a step failed; re-running continues"), since something did change.
        /// </summary>
        private static async Task<int> ApplyChangesAsync(
            SkyCliContext context,
            SkyApiClient client,
            SkySettingsForm form,
            List<SkySettingChange> changes,
            string? reason,
            bool dryRun,
            bool force,
            CancellationToken cancellationToken)
        {
            if (changes.Count == 0)
            {
                return Report(context, new { sent = false, changes = Array.Empty<object>() },
                    "Nothing to change: CameraAgent already has these values.");
            }

            if (!context.Json)
            {
                WriteChanges(context, form, changes);
            }

            if (changes.FirstOrDefault(static change => !change.Field.CanWrite) is { } readOnly)
            {
                // Refused as CameraAgent would refuse it (409 settings.readOnly), before anything is sent.
                throw new SkyCliOutcomeException(
                    $"{readOnly.Key} cannot be changed here: {readOnly.Field.ReadOnlyReason} Nothing was sent.", SkyExitCode.Refused, "Refused");
            }

            if (dryRun)
            {
                return Report(context, new { sent = false, dryRun = true, changes = changes.Select(static change => change.ToJson()) },
                    "Not sent (--dry-run).");
            }

            if (!force && changes.Any(static change => change.Field.Setting.Safety == SkySettingsContract.CaptureGeometrySafety)
                && !context.Confirm("This changes the location and time basis that future captures are stamped with.", "--force"))
            {
                return (int)SkyExitCode.Refused;
            }

            var saved = new List<object>();
            foreach (var group in changes.GroupBy(static change => change.Group))
            {
                var state = form.FindGroup(group.Key)?.State
                    ?? throw new SkyProtocolException($"CameraAgent did not return the {group.Key} group's state.");
                var request = new SkySettingsUpdateRequest(
                    state.Version,
                    group.ToDictionary(static change => change.Key, static change => change.Value, StringComparer.Ordinal),
                    reason);
                SkySettingsWriteResponse response;
                try
                {
                    // A group CameraAgent already had is not a change: a refusal after only such groups changed nothing (exit 3).
                    response = SkyApiClient.Read<SkySettingsWriteResponse>(await context.SendChangeAsync(
                        client, HttpMethod.Put, $"{SkySettingsContract.Route}/{Uri.EscapeDataString(group.Key)}", request,
                        $"the {group.Key} settings", cancellationToken,
                        static answer => SkyJson.Text(answer, "status") != "Unchanged").ConfigureAwait(false));
                }
                catch (Exception error) when (error is not OperationCanceledException && context.AcceptedChange is not null
                    && !context.LastChangeAccepted)
                {
                    // An earlier group was saved and this one was not taken: "not saved" only when CameraAgent refused it
                    // (HTTP 4xx). CameraAgent's own failure (5xx), or an answer that never came, leaves it unknown
                    // (Unverified). A group whose success status arrived is not caught here: whatever failed after it, it
                    // was accepted, and the command says so (Unverified).
                    var (_, kind, message) = SkyCliContext.Classify(error);
                    var status = error is SkyApiException answered ? (int)answered.StatusCode : 0;
                    if (status is >= 400 and < 500)
                    {
                        throw new SkyCliOutcomeException(
                            $"The {group.Key} group was not saved: {message} The groups before it were saved; run the command again to continue.",
                            SkyExitCode.Failed, "Failed", error)
                        { SaysWhatWasAccepted = true };
                    }

                    throw new SkyCliOutcomeException(
                        $"Whether the {group.Key} group was saved is not known: {(status >= 500 ? $"{error.Message} (HTTP {status})." : message)} The groups before it were saved; run the command again to continue: it sends only what still differs.",
                        SkyExitCode.Failed, "Unverified", error)
                    { SaysWhatWasAccepted = true, Cause = kind };
                }

                saved.Add(new
                {
                    group = group.Key,
                    status = response.Status,
                    version = response.Group.Version,
                    recorded = response.Recorded,
                    changes = group.Select(static change => change.ToJson())
                });
                if (!context.Json)
                {
                    await context.Out.WriteLineAsync(response.Status switch
                    {
                        "Applied" => $"[{group.Key}] Saved; in effect now.",
                        "PendingRestart" => $"[{group.Key}] Saved; it takes effect at the next CameraAgent start ('{CommandName} restart').",
                        "Unchanged" => $"[{group.Key}] Nothing changed: CameraAgent already had these values.",
                        var other => $"[{group.Key}] {other}."
                    });
                    if (group.Any(static change => change.Field.Setting.Application == "NextCaptureBoundary") && response.Status == "Applied")
                    {
                        await context.Out.WriteLineAsync($"[{group.Key}] It applies from the next capture boundary.");
                    }
                }
            }

            if (context.Json)
            {
                context.WriteJson(new { sent = true, groups = saved });
            }

            return (int)SkyExitCode.Success;
        }
    }
}

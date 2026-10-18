using System.Globalization;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>What one item of a plan would do (install contract section 5, <c>--plan</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentPlanAction>))]
public enum DeploymentPlanAction
{
    /// <summary>The item is not there; the operation creates it.</summary>
    Create,

    /// <summary>The item is there but differs; the operation changes it.</summary>
    Change,

    /// <summary>The item is already as the operation would leave it.</summary>
    Unchanged,

    /// <summary>The item is there; the operation removes it.</summary>
    Remove
}

/// <summary>
/// One thing an operation creates, changes, leaves unchanged or removes: a folder, a file, an image, a container, a
/// database snapshot or the install record. <see cref="Detail"/> says what about it, such as the limits a container gets.
/// </summary>
public sealed record DeploymentPlanItem(DeploymentPlanAction Action, string Kind, string Target, string? Detail = null);

/// <summary>
/// What an operation does, or with <c>--plan</c> would do, item by item. A plan the installer would refuse is never
/// returned: the refusal is reported instead and the command exits 3, so every plan here can be carried out.
/// </summary>
public sealed record DeploymentPlan(IReadOnlyList<DeploymentPlanItem> Items, string Summary)
{
    /// <summary>The summary of a plan that changes nothing, which a second run with the same answers prints.</summary>
    public const string NothingToChange = "Nothing to change";

    /// <summary>A plan of <paramref name="items"/> with its summary.</summary>
    public static DeploymentPlan Of(IReadOnlyList<DeploymentPlanItem> items, string? nothingToChangeDetail = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new DeploymentPlan(items, Summarize(items, nothingToChangeDetail));
    }

    /// <summary>Whether carrying out the plan changes anything.</summary>
    [JsonIgnore]
    public bool HasChanges => Items.Any(static item => item.Action != DeploymentPlanAction.Unchanged);

    private static string Summarize(IReadOnlyList<DeploymentPlanItem> items, string? nothingToChangeDetail)
    {
        if (items.All(static item => item.Action == DeploymentPlanAction.Unchanged))
        {
            return nothingToChangeDetail is null ? $"{NothingToChange}." : $"{NothingToChange}: {nothingToChangeDetail}.";
        }
        var parts = new List<string>
        {
            Count(items, DeploymentPlanAction.Create, "to create"),
            Count(items, DeploymentPlanAction.Change, "to change")
        };
        if (items.Any(static item => item.Action == DeploymentPlanAction.Remove))
        {
            parts.Add(Count(items, DeploymentPlanAction.Remove, "to remove"));
        }
        parts.Add(Count(items, DeploymentPlanAction.Unchanged, "unchanged"));
        return string.Join(", ", parts) + ".";
    }

    private static string Count(IReadOnlyList<DeploymentPlanItem> items, DeploymentPlanAction action, string label)
        => string.Create(CultureInfo.InvariantCulture, $"{items.Count(item => item.Action == action)} {label}");
}

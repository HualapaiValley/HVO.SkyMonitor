using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

/// <summary>
/// Host settings for CameraAgent-local CelesTrak GP orbital element acquisition (#528). The provider attempt floor is
/// fixed in code at two hours and is deliberately not configurable; the durable schedule lives in the reserved local
/// automation definition, never in configuration. Disabling refresh never erases the last activated snapshot.
/// </summary>
public sealed class OrbitalElementsOptions
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionName = "CameraAgent:OrbitalElements";

    private const int Mebibyte = 1024 * 1024;

    /// <summary>Whether refresh attempts may contact the provider. Off by default; storage stays readable when off.</summary>
    public bool Enabled { get; init; }

    /// <summary>The whole-request deadline, from send through the last body byte.</summary>
    [Range(1, 30)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>The bound on bytes read from the wire for one response.</summary>
    [Range(Mebibyte, 32 * Mebibyte)]
    public int MaximumWireBytes { get; init; } = 32 * Mebibyte;

    /// <summary>How many activated snapshots retention keeps, before protected references are added.</summary>
    [Range(2, 64)]
    public int MaximumRetainedSnapshots { get; init; } = 12;

    /// <summary>How long a superseded snapshot is kept before retention may retire it.</summary>
    [Range(1, 720)]
    public int MinimumRetentionHours { get; init; } = 48;

    /// <summary>How many rejected raw payloads are kept for inspection.</summary>
    [Range(1, 16)]
    public int MaximumQuarantinedPayloads { get; init; } = 4;

    /// <summary>Health is degraded once the active snapshot was activated longer ago than this.</summary>
    [Range(3, 168)]
    public int StaleAfterHours { get; init; } = 12;
}

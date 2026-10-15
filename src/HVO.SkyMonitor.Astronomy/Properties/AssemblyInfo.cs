using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HVO.SkyMonitor.Astronomy.Tests")]
// Issue #1167: the deep-selection pixel harness drives the internal measured profile, evaluation overload and index bound.
[assembly: InternalsVisibleTo("HVO.SkyMonitor.CameraAgent.Tests")]

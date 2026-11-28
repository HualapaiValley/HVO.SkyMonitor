using System.Runtime.CompilerServices;

// Issue #1839: CameraAgent records the same prediction request that Processing dispatches.
[assembly: InternalsVisibleTo("HVO.SkyMonitor.CameraAgent.Common")]
// Issue #1839: the binding regression inspects the request and cancellation token at dispatch.
[assembly: InternalsVisibleTo("HVO.SkyMonitor.CameraAgent.Tests")]
[assembly: InternalsVisibleTo("HVO.SkyMonitor.Processing.Tests")]

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HVO.SkyMonitor.Catalog.Sqlite.Tests")]
// The catalog release train stages bundle inputs through the same authenticated no-follow open (issue #521).
[assembly: InternalsVisibleTo("HVO.SkyMonitor.Deployment.ReleaseTool")]

// #525 N1: invoke the actual compiled private helper in a separate process for each immutable assembly.
// Compile this BCL-only runner outside the repository; MODE is old or corrected. No copy of the helper lives here.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2 || args[1] is not ("old" or "corrected"))
    throw new ArgumentException("usage: cpu-proof ASSEMBLY_PATH old|corrected");
var assemblyPath = Path.GetFullPath(args[0]);
var context = new ProofLoadContext(assemblyPath);
var assembly = context.LoadFromAssemblyPath(assemblyPath);
var type = assembly.GetType("HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests.CatalogPerformanceTests", throwOnError: true)!;
var helper = type.GetMethod("MeasureLatencyAsync", BindingFlags.NonPublic | BindingFlags.Static)!
    .MakeGenericMethod(typeof(byte[]));
var bytes = Enumerable.Range(0, 1024).Select(static index => (byte)(index % 251)).ToArray();
var checksum = Convert.ToHexString(SHA256.HashData(bytes));
var records = new List<object>();
var operationCpu = new List<double>();
var verificationCpu = new List<double?>();
foreach (var burn in new[] { false, true })
{
    Func<int, ValueTask<byte[]>> operation = _ => ValueTask.FromResult(bytes);
    Func<int, byte[], (string, string)> identify = (_, result) =>
    {
        if (!ReferenceEquals(result, bytes)) throw new InvalidOperationException("fixed result changed");
        if (burn)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 40) Thread.SpinWait(256);
        }
        return (string.Empty, checksum);
    };
    var task = (Task)helper.Invoke(null, [burn ? "verification-burn" : "cheap-verification", 30, operation, identify])!;
    await task;
    var record = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var cpu = (double)record.GetType().GetProperty("CpuMillisecondsPerOperation")!.GetValue(record)!;
    var verification = (double?)record.GetType().GetProperty("VerificationCpuMillisecondsPerOperation")?.GetValue(record);
    operationCpu.Add(cpu);
    verificationCpu.Add(verification);
    records.Add(new { verificationBurnMilliseconds = burn ? 40 : 0, measurement = record });
}
if (args[1] == "old")
{
    if (operationCpu[1] - operationCpu[0] < 20)
        throw new InvalidOperationException("old helper did not reproduce verification CPU contamination");
}
else
{
    if (Math.Abs(operationCpu[1] - operationCpu[0]) > 5 || operationCpu[1] > 5 ||
        verificationCpu[1] is not { } verification || verification < 20)
        throw new InvalidOperationException("operation CPU is not invariant or verification CPU is absent");
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    assemblyPath,
    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
    helper = "CatalogPerformanceTests.MeasureLatencyAsync<byte[]>",
    mode = args[1],
    repetitions = 30,
    verificationOnlyBurnMilliseconds = 40,
    invariantOperationCpuToleranceMilliseconds = 5,
    expectedOldContaminationMinimumMilliseconds = 20,
    records,
    passed = true
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

sealed class ProofLoadContext(string assemblyPath) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);
    protected override Assembly? Load(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

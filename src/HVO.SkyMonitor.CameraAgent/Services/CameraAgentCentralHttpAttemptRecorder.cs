using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Http;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class CameraAgentCentralHttpAttemptRecorder
{
    internal const string ReadyRecord = "HVO211_HANDLER_READY";
    internal const string AttemptRecord = "HVO211_HANDLER_ATTEMPT";
    private readonly object _sync = new();
    private readonly string _path;

    internal CameraAgentCentralHttpAttemptRecorder(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, string.Concat(ReadyRecord, Environment.NewLine));
    }

    internal void Record(string clientName)
    {
        lock (_sync)
        {
            File.AppendAllText(_path, string.Concat(AttemptRecord, " ", clientName, Environment.NewLine));
        }
    }
}

internal sealed class CameraAgentCentralHttpAttemptFilter(CameraAgentCentralHttpAttemptRecorder recorder)
    : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            if (builder.Name is SkyMonitorClientOptions.HttpClientName or CentralAuthenticationService.TokenClientName)
            {
                builder.AdditionalHandlers.Add(new RecordingHandler(recorder, builder.Name));
            }
        };
    }

    private sealed class RecordingHandler(CameraAgentCentralHttpAttemptRecorder recorder, string clientName)
        : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            recorder.Record(clientName);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

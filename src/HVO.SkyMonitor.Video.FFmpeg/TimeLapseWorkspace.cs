using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Video.FFmpeg;

internal static class TimeLapseWorkspace
{
    /// <summary>Workspaces are flat and task-owned. Never recursively delete an unexpected subtree or follow a link.</summary>
    internal static void Delete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var root = PhysicalRoot.Open(directory);
        if (Directory.EnumerateDirectories(directory).Any())
            throw new TimeLapseEncodingException("timelapse.scratch-invalid", "Unexpected directory in encoder workspace.");
        var files = Directory.EnumerateFiles(directory).Take(TimeLapseTimeline.MaximumSources + 102).ToArray();
        if (files.Length > TimeLapseTimeline.MaximumSources + 101)
            throw new TimeLapseEncodingException("timelapse.scratch-invalid", "Unexpected file count in encoder workspace.");
        foreach (var file in files)
        {
            root.Verify(file, "remove-encoder-workspace");
            DurableSync.RequireRegularFile(file);
        }
        foreach (var file in files) File.Delete(file);
        Directory.Delete(directory, recursive: false);
    }
}

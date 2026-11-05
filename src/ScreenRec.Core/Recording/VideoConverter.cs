using System.Diagnostics;

namespace ScreenRec.Core.Recording;

/// <summary>Optional MP4 conversion with ffmpeg (smaller uploads; QuickTime-playable). Without ffmpeg the MJPEG AVI is used as is.</summary>
public static class VideoConverter
{
    public static string? FindFfmpeg(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", AppContext.BaseDirectory]);
        return dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    /// <summary>recording.avi → recording.mp4 (H.264, even dimensions). Returns the MP4 path or null when ffmpeg isn't there or fails.</summary>
    public static async Task<string?> ToMp4Async(string ffmpeg, string avi, CancellationToken cancellationToken = default)
    {
        var mp4 = Path.ChangeExtension(avi, ".mp4");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-loglevel", "error", "-i", avi, "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast",
                     "-crf", "28", "-pix_fmt", "yuv420p", "-movflags", "+faststart", mp4 })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await err.ConfigureAwait(false);
            return p.ExitCode == 0 && File.Exists(mp4) ? mp4 : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

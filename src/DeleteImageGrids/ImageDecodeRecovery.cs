using System.Diagnostics;

namespace Cove.DeleteImageGrids;

internal static class ImageDecodeRecovery
{
    public static async Task<MemoryStream> DecodeAsync(Stream source, string? executable, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "igd-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "source");
            var output = Path.Combine(directory, "decoded.png");
            if (source.CanSeek) source.Position = 0;
            await using (var file = File.Create(input)) await source.CopyToAsync(file, ct);
            var info = new ProcessStartInfo(executable ?? "ffmpeg")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-err_detect", "ignore_err", "-i", input, "-frames:v", "1", "-vf",
                "scale=640:640:force_original_aspect_ratio=decrease", output }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var detail = await errors;
                if (process.ExitCode != 0 || !File.Exists(output))
                    throw new InvalidDataException("FFmpeg image recovery failed: " + detail.Trim());
                return new MemoryStream(await File.ReadAllBytesAsync(output, ct), writable: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("FFmpeg image recovery exceeded 30 seconds.");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

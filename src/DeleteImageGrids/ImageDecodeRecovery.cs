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
            var errors = ReadErrorsAsync(process.StandardError, timeout.Token);
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

    // Drain stderr completely to avoid blocking FFmpeg, retaining only a bounded
    // diagnostic. Damaged PNGs can otherwise generate hundreds of thousands of lines.
    private static async Task<string> ReadErrorsAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(count, 4096 - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
        }
        return result.ToString();
    }

    internal static string Brief(Exception error)
    {
        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 512 ? message[..512] + "…" : message;
    }

    internal static async Task<string> DescribeSourceAsync(Stream source, CancellationToken ct)
    {
        if (!source.CanSeek) return "Source header unavailable.";
        var position = source.Position;
        try
        {
            source.Position = 0;
            var header = new byte[16];
            var count = await source.ReadAsync(header, ct);
            return $"Source length: {source.Length} bytes; first {count} bytes: {Convert.ToHexString(header.AsSpan(0, count))}.";
        }
        finally { source.Position = position; }
    }
}

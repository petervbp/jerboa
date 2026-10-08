using System.Diagnostics;

namespace Jerboa.Video;

/// <summary>
/// Locates ffmpeg and runs it. Video is the only part of Jerboa that needs it — the audio
/// path has its own encoder built in and keeps working whether ffmpeg is installed or not.
/// </summary>
public static class Ffmpeg
{
    private static string? _cached;
    private static bool _searched;

    public static string? Executable
    {
        get
        {
            if (_searched) return _cached;
            _searched = true;
            _cached = Locate();
            return _cached;
        }
    }

    public static bool IsAvailable => Executable != null;

    private static string? Locate()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry is not worth failing over */ }
        }
        return null;
    }

    /// <summary>Starts ffmpeg with its input on a pipe we write frames into.</summary>
    public static Process StartWithPipedInput(string arguments)
    {
        var start = new ProcessStartInfo(Executable ?? throw new InvalidOperationException("ffmpeg was not found."))
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");

        // Nobody reads these, and a full pipe would block ffmpeg mid-recording.
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        return process;
    }

    /// <summary>Runs ffmpeg to completion and returns true when it reported success.</summary>
    public static bool Run(string arguments, out string diagnostics)
    {
        diagnostics = "";
        if (!IsAvailable) return false;

        var start = new ProcessStartInfo(Executable!)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(start);
        if (process == null) return false;

        diagnostics = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}

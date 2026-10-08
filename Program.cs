using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Jerboa.Audio;
using Jerboa.Ui;

namespace Jerboa;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            AttachConsole(-1);
            return SelfTest(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10,
                            args.Length > 2 ? args[2] : null);
        }

        if (args.Length > 0 && args[0] == "--videotest")
        {
            AttachConsole(-1);
            return VideoTest(
                args.Length > 1 && int.TryParse(args[1], out var vs) ? vs : 10,
                args.Length > 2 ? args[2] : null);
        }

        if (args.Length > 1 && args[0] == "--screenshot")
        {
            ApplicationConfiguration.Initialize();
            return Screenshot(args[1], args.Length > 2 ? args[2] : null);
        }

        bool minimized = args.Contains("--minimized");

        if (!SingleInstance.Claim())
        {
            // Already running: bring that window forward rather than opening a second recorder.
            if (!minimized) SingleInstance.AskRunningInstanceToShow();
            return 0;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startHidden: minimized));
        }
        finally
        {
            SingleInstance.Release();
        }
        return 0;
    }

    /// <summary>
    /// Captures a window (or the main screen) without any interface and reports what it
    /// cost — how many frames the compositor offered, and how much time was spent getting
    /// each one out of video memory. That read-back is the price of the whole feature, so
    /// it gets measured before anything is built on top of it.
    /// </summary>
    private static int VideoTest(int seconds, string? windowTitle)
    {
        using var capture = new Video.WindowCapture();
        Windows.Graphics.Capture.GraphicsCaptureItem item;

        if (windowTitle == null)
        {
            Console.WriteLine("source     : primary monitor");
            item = Video.Interop.CreateItemForMonitor(Video.WindowList.PrimaryMonitor());
        }
        else
        {
            var (handle, title) = Video.WindowList.FindByTitle(windowTitle);
            if (handle == IntPtr.Zero)
            {
                Console.WriteLine($"no visible window matching \"{windowTitle}\"");
                return 1;
            }
            Console.WriteLine($"source     : {title}");
            item = Video.Interop.CreateItemForWindow(handle);
        }

        if (!Video.Ffmpeg.IsAvailable)
        {
            Console.WriteLine("ffmpeg was not found on PATH; video cannot be encoded.");
            return 1;
        }

        var folder = Path.Combine(Path.GetTempPath(), "jerboa-selftest");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "videotest.mp4");

        using var writer = new Video.VideoWriter(target, item.Size.Width, item.Size.Height);
        capture.MinimumInterval = TimeSpan.FromSeconds(0.9 / Video.VideoWriter.FramesPerSecond);
        capture.FrameReceived = (in Video.CapturedFrame frame) => writer.Accept(frame);
        capture.Closed += () => Console.WriteLine("  source closed");

        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        capture.Start(item);
        writer.Start();

        // Pause through the middle third, exactly as the audio self-test does.
        int pauseFrom = seconds / 3;
        int pauseTo = seconds * 2 / 3;

        for (int second = 0; second < seconds; second++)
        {
            if (second == pauseFrom) { writer.Pause(); Console.WriteLine("  -- paused --"); }
            if (second == pauseTo) { writer.Resume(); Console.WriteLine("  -- resumed --"); }
            Thread.Sleep(1000);
            Console.WriteLine($"  {clock.Elapsed:mm\\:ss}  received {capture.FramesReceived,4}  " +
                              $"written {writer.FramesWritten,4}  {capture.ContentSize.Width}x{capture.ContentSize.Height}");
        }

        capture.Stop();
        writer.Stop();
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

        double elapsed = clock.Elapsed.TotalSeconds;
        double recorded = elapsed - (pauseTo - pauseFrom);
        long bytes = File.Exists(target) ? new FileInfo(target).Length : 0;

        Console.WriteLine($"source     : {capture.ContentSize.Width}x{capture.ContentSize.Height}");
        Console.WriteLine($"offered    : {capture.FramesReceived + capture.FramesSkipped} frames, " +
                          $"{capture.FramesSkipped} skipped before read-back, {capture.FramesDropped} unreadable");
        Console.WriteLine($"read back  : {capture.FramesReceived} frames " +
                          $"({capture.FramesReceived / elapsed:F1} per second)");
        if (writer.FramesAccepted == 0)
            Console.WriteLine("warning    : no picture at all - a minimised window cannot be captured");
        Console.WriteLine($"written    : {writer.FramesWritten} frames, expected about " +
                          $"{recorded * Video.VideoWriter.FramesPerSecond:F0} for {recorded:F0} s of recording");
        Console.WriteLine($"file       : {bytes / 1024} KB  ({bytes / 1024.0 / 1024.0 / recorded * 3600:F0} MB per hour)");
        Console.WriteLine($"cpu        : {cpu.TotalSeconds / elapsed * 100:F0}% of one core");
        if (writer.Failure != null) Console.WriteLine($"failure    : {writer.Failure}");
        Console.WriteLine($"path       : {target}");

        return bytes > 1024 ? 0 : 1;
    }

    /// <summary>Renders each layout state to a PNG, so the window can be checked without opening it.</summary>
    private static int Screenshot(string prefix, string? videoTitle = null)
    {
        var form = new MainForm();
        form.PlaceOffScreen();
        form.Show();

        Capture(form, prefix + "-idle.png");
        form.PreviewNotice();
        Capture(form, prefix + "-stored.png");
        form.PreviewSettings();
        Capture(form, prefix + "-settings.png");

        if (videoTitle != null)
        {
            var (handle, _) = Video.WindowList.FindByTitle(videoTitle);
            if (handle != IntPtr.Zero)
            {
                form.PreviewVideoSource(Video.Interop.CreateItemForWindow(handle));
                Capture(form, prefix + "-video.png");
            }
        }

        form.PreviewRecording(0.0);
        Capture(form, prefix + "-recording-dim.png");
        form.PreviewRecording(1.0);
        Capture(form, prefix + "-recording-full.png");

        form.ForceClose();
        return 0;
    }

    private static void Capture(Form form, string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Records for a few seconds without any UI, to verify the audio path end to end.</summary>
    private static int SelfTest(int seconds, string? videoTitle = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "jerboa-selftest");
        var session = new RecordingSession();
        session.Warning += m => Console.WriteLine("  warning: " + m);
        session.DeviceLost += m => Console.WriteLine("  device lost: " + m);

        var playback = Devices.Resolve(null, DataFlow.Render);
        var microphone = Devices.Resolve(null, DataFlow.Capture);
        Console.WriteLine($"playback   : {playback?.FriendlyName ?? "none"}");
        Console.WriteLine($"microphone : {microphone?.FriendlyName ?? "none"}");

        try
        {
            Windows.Graphics.Capture.GraphicsCaptureItem? source = null;
            if (videoTitle != null)
            {
                var (handle, title) = Video.WindowList.FindByTitle(videoTitle);
                if (handle == IntPtr.Zero)
                {
                    Console.WriteLine($"no visible window matching \"{videoTitle}\"");
                    return 1;
                }
                Console.WriteLine($"video      : {title}");
                source = Video.Interop.CreateItemForWindow(handle);
            }

            session.Start(playback, microphone, folder, DateTime.Now, source);
        }
        catch (Exception ex)
        {
            Console.WriteLine("start failed: " + ex.Message);
            return 1;
        }

        // Pause through the middle third, so the cut-out behaviour is exercised too.
        int pauseFrom = seconds / 3;
        int pauseTo = seconds * 2 / 3;

        for (int i = 0; i < seconds; i++)
        {
            if (i == pauseFrom) { session.Pause(); Console.WriteLine("  -- paused --"); }
            if (i == pauseTo) { session.Resume(); Console.WriteLine("  -- resumed --"); }
            Thread.Sleep(1000);
            Console.WriteLine($"  {session.Duration:mm\\:ss}  mic {session.MicrophonePeak:F3}  system {session.SystemPeak:F3}");
        }

        var result = session.Stop();
        var info = new FileInfo(result.FilePath);

        Console.WriteLine($"file    : {result.FilePath}");
        Console.WriteLine($"size    : {info.Length / 1024} KB");
        Console.WriteLine($"expected: {seconds - (seconds * 2 / 3 - seconds / 3)} s recorded of {seconds} s wall clock");
        Console.WriteLine($"actual  : {result.Duration.TotalSeconds:F1} s");
        Console.WriteLine($"drift   : {session.LastDriftReport}");
        if (result.Problem != null) Console.WriteLine($"problem : {result.Problem}");
        session.Dispose();

        return info.Length > 1024 ? 0 : 1;
    }
}

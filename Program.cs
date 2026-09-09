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
            return SelfTest(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10);
        }

        if (args.Length > 1 && args[0] == "--screenshot")
        {
            ApplicationConfiguration.Initialize();
            return Screenshot(args[1]);
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

    /// <summary>Renders each layout state to a PNG, so the window can be checked without opening it.</summary>
    private static int Screenshot(string prefix)
    {
        var form = new MainForm();
        form.PlaceOffScreen();
        form.Show();

        Capture(form, prefix + "-idle.png");
        form.PreviewNotice();
        Capture(form, prefix + "-stored.png");
        form.PreviewSettings();
        Capture(form, prefix + "-settings.png");

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
    private static int SelfTest(int seconds)
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
            session.Start(playback, microphone, folder, DateTime.Now);
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
        session.Dispose();

        return info.Length > 1024 ? 0 : 1;
    }
}

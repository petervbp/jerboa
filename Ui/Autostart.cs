using Microsoft.Win32;

namespace Jerboa.Ui;

/// <summary>
/// The "start with Windows" entry, registered two ways: a shortcut in the user's Startup
/// folder and a value in the Run registry key.
///
/// The shortcut is the one worth having — it is a file you can open, move and delete, and
/// Windows lists it under Startup apps in the Task Manager, so the setting can be verified
/// rather than believed. The registry value is the backstop for machines where one of the
/// two quietly does nothing.
/// </summary>
public static class Autostart
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "Jerboa";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), EntryName + ".lnk");

    public static bool IsEnabled
    {
        get
        {
            try { return File.Exists(ShortcutPath) || RunValue != null; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Registers both ways at once, and reports success if either took.
    ///
    /// Belt and braces on purpose: on the machine this was built for, the Run entry was
    /// written correctly and simply never fired at logon, and that same machine has a
    /// shell that no longer notices new Start menu entries. Two independent mechanisms
    /// cost nothing — a second launch finds the first one holding the mutex and exits
    /// straight away — and between them one is very likely to work.
    /// </summary>
    public static bool Set(bool enabled)
    {
        bool viaFolder = SetShortcut(enabled);
        bool viaRegistry = SetRunValue(enabled);
        return viaFolder || viaRegistry;
    }

    private static bool SetShortcut(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return !File.Exists(ShortcutPath);
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return false;

            // Started by Windows it belongs in the notification area, not in front of the desktop.
            CreateShortcut(ShortcutPath, executable, "--minimized");
            return File.Exists(ShortcutPath);
        }
        catch { return false; }
    }

    private static string? RunValue
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
                return key?.GetValue(EntryName) as string;
            }
            catch { return null; }
        }
    }

    private static bool SetRunValue(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(LegacyRunKey);
            if (key == null) return false;

            if (!enabled) { key.DeleteValue(EntryName, throwOnMissingValue: false); return true; }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return false;

            key.SetValue(EntryName, $"\"{executable}\" --minimized");
            return true;
        }
        catch { return false; }
    }

    private static void CreateShortcut(string path, string target, string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows Script Host is unavailable.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
        link.Description = "Record system audio and microphone into one stereo MP3";
        link.Save();
    }
}

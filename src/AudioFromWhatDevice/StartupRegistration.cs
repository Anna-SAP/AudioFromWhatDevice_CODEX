using Microsoft.Win32;

namespace AudioFromWhatDevice;

// Per-user start at sign-in through the Run key, the same list Settings > Apps > Startup and
// Task Manager manage; it needs no administrator rights.
internal sealed class StartupRegistration(string exePath, string runKey = StartupRegistration.RunKey,
    string approvedKey = StartupRegistration.ApprovedKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Settings and Task Manager record that the user turned a Run entry off.
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "AudioFromWhatDevice";

    // Null under the dotnet host (dotnet AudioFromWhatDevice.dll): its path alone would not start the app.
    public static StartupRegistration? ForCurrentProcess() =>
        Environment.ProcessPath is { } path && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? new StartupRegistration(path) : null;

    // On only while the entry starts this copy of the app and Windows has not turned it off.
    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKey);
            using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
            return Targets(run?.GetValue(ValueName) as string, exePath) && IsApproved(approved?.GetValue(ValueName) as byte[]);
        }
    }

    public void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(runKey)) run.SetValue(ValueName, $"\"{exePath}\"");
        // Turning it on here also overrides an earlier "disabled" choice made in Task Manager.
        ClearApproval();
    }

    public void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true)) run?.DeleteValue(ValueName, throwOnMissingValue: false);
        ClearApproval();
    }

    private void ClearApproval()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // A Run value is a command line; only its executable, quoted or not, identifies the app copy.
    internal static bool Targets(string? command, string exePath)
    {
        command = command?.Trim();
        if (string.IsNullOrEmpty(command)) return false;
        var target = command[0] == '"' ? command[1..].Split('"')[0] : command;
        return target.Equals(exePath, StringComparison.OrdinalIgnoreCase);
    }

    // The data starts with an even byte (02, 06) while enabled and an odd byte (03, 07) once turned off.
    internal static bool IsApproved(byte[]? state) => state is not { Length: > 0 } || (state[0] & 1) == 0;
}

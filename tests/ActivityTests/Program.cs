using AudioFromWhatDevice;
using Microsoft.Win32;

var count = 0;
void Expect(Activity expected, Activity actual, string scenario)
{
    if (expected != actual) throw new Exception($"{scenario}: expected {expected}, got {actual}");
    count++;
}
var state = new ActivityState();
Expect(Activity.NoSignal, state.Update(0, false, 1, 1000), "Available does not mean playing");
Expect(Activity.Signal, state.Update(.01f, false, 1, 1050), "Signal detected");
Expect(Activity.Recent, state.Update(0, false, 1, 1649), "Short gap has hold status");
Expect(Activity.NoSignal, state.Update(0, false, 1, 1650), "Hold expires at 600 ms");
Expect(Activity.Signal, state.Update(.01f, false, 1, 1700), "Signal resumes");
Expect(Activity.Muted, state.Update(.9f, true, 1, 1750), "Pre-volume peak cannot override mute");
Expect(Activity.NoSignal, state.Update(0, false, 1, 1800), "Mute clears previous hold");
Expect(Activity.Muted, state.Update(.9f, false, 0, 1850), "Zero gain is not output");
Expect(Activity.NoSignal, state.Update(float.NaN, false, 1, 1900), "Invalid peak is not activity");
Expect(Activity.NoSignal, state.Update(ActivityState.Threshold, false, 1, 1950), "Noise threshold");
// Same display name must not cause state to be shared between physical endpoints.
var first = new EndpointSnapshot("endpoint-a", "Headphones", null, .1f, 1, false, false, Activity.Signal, null);
var second = first with { Id = "endpoint-b", Activity = Activity.NoSignal };
var byId = new[] { first, second }.ToDictionary(d => d.Id);
if (byId.Count != 2 || first.ShortId == second.ShortId) throw new Exception("Endpoint identity was collapsed");
count++;

void Check(bool condition, string scenario)
{
    if (!condition) throw new Exception(scenario);
    count++;
}
Check(TrayLabels.Suggest("扬声器 (Realtek(R) Audio)") == "RT", "Recognizable sound card label");
Check(TrayLabels.Suggest("扬声器 (MagicMic Virtual Audio Device(WDM))") == "MM", "Recognizable virtual device label");
Check(TrayLabels.Suggest("耳机 (WH-1000XM5)") == "XM5", "Preserve headset model suffix");
Check(TrayLabels.Suggest("Galaxy Buds2 Pro") == "B2", "Do not truncate into the middle of a model word");
Check(TrayLabels.Suggest("耳机 (华为蓝牙耳机)") == "华为", "Use actual Chinese brand, not endpoint prefix");
Check(TrayLabels.IsValid("耳机") && TrayLabels.IsValid("XM5") && TrayLabels.IsValid("AB12"), "Supported short labels");
Check(!TrayLabels.IsValid("蓝牙耳机") && !TrayLabels.IsValid("ABCDE") && !TrayLabels.IsValid("A!"), "Reject labels that cannot fit clearly");
var settingsFile = Path.Combine(Path.GetTempPath(), "AudioFromWhatDevice-label-test-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var labels = new TrayLabels(settingsFile);
    var a = first with { Name = "耳机 (WH-1000XM5)" };
    var b = second with { Name = a.Name, Activity = Activity.Signal };
    var names = labels.Resolve([a, b]);
    Check(names[a.Id] != names[b.Id], "Identically named headsets must have different visible labels");
    Check(labels.Resolve([b, a])[a.Id] == names[a.Id], "Enumeration order cannot change labels");
    var both = new MonitorSnapshot([a, b], null, DateTimeOffset.Now) { DefaultOutputId = b.Id };
    var badge = TrayPresentation.Build(both, false, names);
    Check(badge.Id == b.Id && badge.Text == names[b.Id], "Same-name outputs select the default by full ID");
    Check(badge.Description.Contains("另有 1 台输出"), "Concurrent output is disclosed without extra tray icons");
    var idle = both with { Devices = [a with { Activity = Activity.Muted }, b with { Activity = Activity.NoSignal }] };
    var idleBadge = TrayPresentation.Build(idle, false, names);
    Check(idleBadge.Id == b.Id && idleBadge.Text == badge.Text, "Idle default keeps its name");
    Check(!idleBadge.Description.Contains("正在输出") && idleBadge.Description.Contains("系统默认"),
        "Idle default status cannot imply playback");
    Check(TrayPresentation.Build(both, true, names).Kind == TrayBadgeKind.Warning, "Stale readings show one warning");
    var partial = both with { Devices = [a, b with { Activity = Activity.Unavailable, Error = "meter failed" }] };
    Check(TrayPresentation.Build(partial, false, names).Id == a.Id, "A healthy playing endpoint takes priority over a failed default");
    labels.Save(a.Id, "耳机");
    Check(labels.Resolve([a, b])[a.Id] == "耳机", "Custom label shown immediately");
    var reloaded = new TrayLabels(settingsFile);
    Check(reloaded.Resolve([a, b])[a.Id] == "耳机", "Custom label survives restart");
    reloaded.Save(b.Id, "耳机");
    var duplicate = reloaded.Resolve([a, b]);
    Check(duplicate.Values.Distinct().Count() == 2 && duplicate.Values.All(TrayLabels.IsValid), "Duplicate aliases remain distinguishable and fit");
    reloaded.Save(a.Id, "");
    Check(new TrayLabels(settingsFile).Alias(a.Id) is null, "Restore automatic label persists");
}
finally { if (File.Exists(settingsFile)) File.Delete(settingsFile); }
Check(BluetoothDeviceDetector.IsBluetoothEnumerator("BTHENUM"), "Classic Bluetooth metadata");
Check(BluetoothDeviceDetector.IsBluetoothEnumerator("BTHHFENUM"), "Hands-free Bluetooth metadata");
Check(BluetoothDeviceDetector.IsBluetoothEnumerator("bthledevice"), "LE Bluetooth metadata is case insensitive");
Check(!BluetoothDeviceDetector.IsBluetoothEnumerator("INTELAUDIO") && !BluetoothDeviceDetector.IsBluetoothEnumerator("HDAUDIO"), "Onboard audio is not Bluetooth");
Check(!BluetoothDeviceDetector.IsBluetoothEnumerator("USB") && !BluetoothDeviceDetector.IsBluetoothEnumerator("SWD") && !BluetoothDeviceDetector.IsBluetoothEnumerator(null), "USB and software endpoints are not automatically Bluetooth");
var speaker = first with { Name = "Realtek Speakers", Activity = Activity.NoSignal, Peak = 0 };
var bluetooth = second with { Name = "Renamed headphones", IsBluetooth = true, Activity = Activity.NoSignal, Peak = 0 };
var virtualDevice = speaker with { Id = "virtual-device", Name = "MagicMic Virtual Audio" };
var connectionLabels = new Dictionary<string, string> { [speaker.Id] = "RT", [bluetooth.Id] = "HE", [virtualDevice.Id] = "MM" };
var connected = new MonitorSnapshot([speaker, bluetooth, virtualDevice], null, DateTimeOffset.Now) { DefaultOutputId = bluetooth.Id };
TrayBadge Display(MonitorSnapshot snapshot, string? previous = null) => TrayPresentation.Build(snapshot, false, connectionLabels, previous);

var startup = Display(connected);
Check(startup.Id == bluetooth.Id && startup.Text == "HE" && startup.BackgroundColor == "#4169E1",
    "Three idle endpoints at startup must produce the default Bluetooth badge");
Check(startup.Description.Contains("系统默认") && !startup.Description.Contains("正在输出"),
    "Silent Bluetooth remains named without pretending to play");
Check(Display(connected with { DefaultOutputId = speaker.Id }).Id == speaker.Id,
    "Changing the Windows default changes the single idle badge");
Check(Display(connected with { DefaultOutputId = speaker.Id }).BackgroundColor == "#12704A",
    "Default internal speaker uses green");
Check(Display(connected with { DefaultOutputId = virtualDevice.Id }).Text == "MM",
    "A deliberately selected virtual default must not be silently replaced by a physical device");
foreach (var activity in new[] { Activity.Signal, Activity.Recent, Activity.NoSignal, Activity.Muted })
{
    var snapshot = connected with { Devices = [speaker, bluetooth with { Activity = activity }, virtualDevice] };
    var badge = Display(snapshot);
    Check(badge.Id == bluetooth.Id && badge.Text == "HE" && badge.BackgroundColor == "#4169E1",
        $"Default Bluetooth name and color survive {activity}");
}
var zeroVolume = bluetooth with { Volume = 0, Activity = Activity.Muted };
Check(Display(connected with { Devices = [speaker, zeroVolume, virtualDevice] }).Description.Contains("已静音"),
    "Zero-volume default keeps its name with an accurate mute status");
var routedPlayback = connected with { DefaultOutputId = speaker.Id,
    Devices = [speaker, bluetooth with { Activity = Activity.Signal, Peak = .5f }, virtualDevice] };
Check(Display(routedPlayback).Id == bluetooth.Id, "Real non-default playback outranks an idle system default");
Check(Display(routedPlayback with { Devices = [speaker, bluetooth with { Activity = Activity.Recent }, virtualDevice] }).Id == bluetooth.Id,
    "Short playback gaps retain the active device");
Check(Display(routedPlayback with { Devices = [speaker, bluetooth, virtualDevice] }, bluetooth.Id).Id == speaker.Id,
    "After playback ends, select the current system default");
Check(Display(connected with { Devices = [speaker with { Activity = Activity.Signal }, bluetooth, virtualDevice] }).Id == speaker.Id,
    "Wired playback outranks idle Bluetooth without any name or transport preference");

var concurrent = connected with { Devices = [speaker with { Activity = Activity.Signal }, bluetooth with { Activity = Activity.Signal }, virtualDevice] };
Check(Display(concurrent, speaker.Id).Id == bluetooth.Id, "Concurrent outputs prefer the playing default");
Check(Display(concurrent).Description.Contains("另有 1 台输出"), "Single icon discloses other active outputs in its tooltip");
var nonDefaultConcurrent = concurrent with { DefaultOutputId = virtualDevice.Id };
Check(Display(nonDefaultConcurrent, speaker.Id).Id == speaker.Id,
    "Concurrent non-default outputs retain the displayed device to avoid cycling");
Check(Display(nonDefaultConcurrent, bluetooth.Id).Id == bluetooth.Id, "Either current active device can remain stable");
Check(Display(nonDefaultConcurrent).Id == Display(nonDefaultConcurrent with { Devices = concurrent.Devices.Reverse().ToArray() }).Id,
    "Enumeration order cannot randomly change the selected device");

var unplugged = bluetooth with { IsAvailable = false, Activity = Activity.Unavailable, Error = "disconnected" };
var disconnected = connected with { Devices = [speaker, unplugged, virtualDevice], DefaultOutputId = speaker.Id };
Check(Display(disconnected, bluetooth.Id).Id == speaker.Id, "Unplugging switches to the new available default");
Check(Display(connected, speaker.Id).Id == bluetooth.Id, "Reconnecting as default restores the Bluetooth name");
Check(Display(connected with { DefaultOutputId = null }, bluetooth.Id).Id == bluetooth.Id,
    "During missing default metadata retain a known available device");
Check(Display(connected with { DefaultOutputId = "missing-endpoint" }, "also-missing").Id == speaker.Id,
    "Missing default and previous device fall back to an available named endpoint");

var unreadable = bluetooth with { Activity = Activity.Unavailable, Error = "meter failed" };
var warning = Display(connected with { Devices = [speaker, unreadable, virtualDevice] });
Check(warning.Text == "HE" && warning.Kind == TrayBadgeKind.Warning,
    "Unreadable idle default stays identifiable as a single warning badge");
Check(Display(connected with { Devices = [speaker, bluetooth, virtualDevice with { Activity = Activity.Unavailable, Error = "failed" }] }).Id == bluetooth.Id,
    "An unrelated failed endpoint must not create a second icon or hide the default");
Check(TrayPresentation.Build(connected, true, connectionLabels).Kind == TrayBadgeKind.Warning,
    "Stale default data shows one warning");
Check(Display(connected with { Error = "audio service unavailable" }).Kind == TrayBadgeKind.Warning,
    "Global audio failure shows one warning");
var empty = Display(new([], null, DateTimeOffset.Now));
Check(empty.Text == "—" && empty.Kind == TrayBadgeKind.NoDevices, "No devices produces one neutral status badge");
Check(Display(connected with { Devices = [unplugged] }).Kind == TrayBadgeKind.NoDevices,
    "Disconnected-only snapshots cannot retain a device badge");

Check(StartupRegistration.Targets("\"C:\\Apps\\Audio.exe\"", @"C:\Apps\Audio.exe"), "Quoted Run command starts this copy");
Check(StartupRegistration.Targets(@" c:\apps\AUDIO.EXE ", @"C:\Apps\Audio.exe"), "Unquoted Run command matches regardless of case");
Check(StartupRegistration.Targets("\"C:\\Apps\\Audio.exe\" --tray", @"C:\Apps\Audio.exe"), "Arguments do not change the started copy");
Check(!StartupRegistration.Targets("\"D:\\Old\\Audio.exe\"", @"C:\Apps\Audio.exe") && !StartupRegistration.Targets(null, @"C:\Apps\Audio.exe"),
    "A moved copy or a missing entry is not this copy");
Check(StartupRegistration.IsApproved(null) && StartupRegistration.IsApproved([2, 0, 0, 0]) && StartupRegistration.IsApproved([6, 0, 0, 0]),
    "Entries not turned off in Task Manager are approved");
Check(!StartupRegistration.IsApproved([3, 0, 0, 0]) && !StartupRegistration.IsApproved([7, 0, 0, 0]),
    "Entries turned off in Task Manager are not approved");
// Exercise the real registry under a throwaway key; the actual Run key is never touched.
var startupKey = @"Software\AudioFromWhatDevice-startup-test-" + Guid.NewGuid().ToString("N");
try
{
    var registration = new StartupRegistration(@"C:\Apps\Audio.exe", startupKey + @"\Run", startupKey + @"\Approved");
    Check(!registration.IsEnabled, "Start on boot stays off until chosen");
    registration.Enable();
    using (var run = Registry.CurrentUser.OpenSubKey(startupKey + @"\Run"))
        Check(registration.IsEnabled && run?.GetValue("AudioFromWhatDevice") as string == "\"C:\\Apps\\Audio.exe\"",
            "Enabling writes a quoted Run command");
    using (var approved = Registry.CurrentUser.CreateSubKey(startupKey + @"\Approved"))
        approved.SetValue("AudioFromWhatDevice", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    Check(!registration.IsEnabled, "Turning the entry off in Task Manager unchecks the menu");
    registration.Enable();
    Check(registration.IsEnabled, "Checking it again overrides the Task Manager choice");
    Check(!new StartupRegistration(@"D:\Other\Audio.exe", startupKey + @"\Run", startupKey + @"\Approved").IsEnabled,
        "Another copy of the app does not show as enabled");
    registration.Disable();
    registration.Disable();
    Check(!registration.IsEnabled, "Disabling removes the entry and can be repeated");
}
finally { Registry.CurrentUser.DeleteSubKeyTree(startupKey, throwOnMissingSubKey: false); }
Console.WriteLine($"PASS: {count} activity, identity, single-tray selection, saved-label and start-on-boot checks");
namespace AudioFromWhatDevice;

// A small display hold prevents flicker during pauses; Recent is not a new signal.
internal sealed class ActivityState
{
    public const float Threshold = 0.0001f; // -80 dBFS, before endpoint gain
    public const long HoldMilliseconds = 600;
    private long? lastSignal;

    public Activity Update(float peak, bool muted, float volume, long now)
    {
        if (muted || volume <= 0)
        {
            lastSignal = null;
            return Activity.Muted;
        }
        if (float.IsFinite(peak) && peak > Threshold)
        {
            lastSignal = now;
            return Activity.Signal;
        }
        return lastSignal is long last && now - last < HoldMilliseconds
            ? Activity.Recent : Activity.NoSignal;
    }
}

internal enum Activity { NoSignal, Signal, Recent, Muted, Unavailable }

internal sealed record EndpointSnapshot(string Id, string Name, string? ContainerId,
    float Peak, float Volume, bool Muted, bool HardwareMeter, Activity Activity, string? Error)
{
    public bool IsBluetooth { get; init; }
    // DEVICE_STATE_ACTIVE means connected and enabled, independent of playback.
    public bool IsAvailable { get; init; } = true;

    // Cosmetic hash only; the full opaque endpoint ID is always the identity key.
    public string ShortId => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Id)))[..8];
    public string Label => $"{Name} · {ShortId}";
    public string Status => !IsAvailable ? "已断开或已禁用" : Activity switch
    {
        Activity.Signal => "正在输出",
        Activity.Recent => "刚有输出（保持）",
        Activity.Muted => "已连接 · 已静音 / 音量为零",
        Activity.Unavailable => "暂不可读",
        _ => "已连接 · 未检测到信号"
    };
}

internal sealed record MonitorSnapshot(EndpointSnapshot[] Devices, string? Error, DateTimeOffset CapturedAt)
{
    public string? DefaultOutputId { get; init; }
    public static MonitorSnapshot Initial { get; } = new([], "正在连接 Windows 音频服务…", DateTimeOffset.Now);
}

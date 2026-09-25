using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioFromWhatDevice;

internal sealed class TrayLabels
{
    private readonly string path;
    private Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    public string? LoadError { get; }

    public TrayLabels(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioFromWhatDevice", "tray-labels.json");
        try
        {
            if (File.Exists(this.path))
                aliases = new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(this.path)) ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LoadError = "无法读取已保存简称：" + ex.Message; }
    }

    public string? Alias(string id) => aliases.GetValueOrDefault(id);

    public void Save(string id, string text)
    {
        text = text.Trim().ToUpperInvariant();
        if (text.Length > 0 && !IsValid(text))
            throw new ArgumentException("请输入 1–2 个汉字，或 1–4 个字母/数字；混合文字最多占 4 个英文字符宽度。");
        var next = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        if (text.Length == 0) next.Remove(id); else next[id] = text;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            aliases = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsValid(string text) => text.Length > 0 &&
        text.EnumerateRunes().All(Rune.IsLetterOrDigit) && Width(text) <= 4;

    private static int Width(string text) => text.EnumerateRunes().Sum(r => r.IsAscii ? 1 : 2);

    private static string Fit(string text, int width)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var used = rune.IsAscii ? 1 : 2;
            if (width < used) break;
            result.Append(rune);
            width -= used;
        }
        return result.ToString();
    }

    public static string Suggest(string name)
    {
        if (name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) return "RT";
        if (name.Contains("MagicMic", StringComparison.OrdinalIgnoreCase)) return "MM";
        var models = Regex.Matches(name, @"[A-Za-z]+\d+");
        if (models.Count > 0)
        {
            var model = models[^1].Value.ToUpperInvariant();
            if (model.Length <= 4) return model;
            var digits = Regex.Match(model, @"\d+$").Value;
            return model[..1] + digits[^Math.Min(3, digits.Length)..];
        }
        // Remove the generic endpoint prefix; preserve the actual device/brand name.
        var text = Regex.Replace(name, @"^(?:\d+\s*[-–]\s*)?(?:扬声器|耳机|耳麦|头戴式耳机|Speakers?|Headphones?|Headset)\s*[(（]\s*",
            "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\(R\)|\(TM\)|®|™", "", RegexOptions.IgnoreCase);
        var letters = string.Concat(text.EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(r => r.ToString()));
        if (letters.Length == 0) return "AU";
        if (letters.EnumerateRunes().Any(r => !r.IsAscii)) return Fit(letters, 4);
        var words = Regex.Matches(text, "[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+");
        if (words.Count > 1) return string.Concat(words.Take(2).Select(w => w.Value[0])).ToUpperInvariant();
        return Fit(letters, 2).ToUpperInvariant();
    }

    public Dictionary<string, string> Resolve(IEnumerable<EndpointSnapshot> devices)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A user alias takes priority. Resolve collisions deterministically by opaque ID.
        foreach (var device in devices.OrderByDescending(d => aliases.ContainsKey(d.Id)).ThenBy(d => d.Id, StringComparer.Ordinal))
        {
            var alias = aliases.GetValueOrDefault(device.Id);
            var label = alias is not null && IsValid(alias) ? alias.ToUpperInvariant() : Suggest(device.Name);
            var candidate = label;
            for (var suffix = 2; !used.Add(candidate); suffix++)
            {
                var number = suffix.ToString(CultureInfo.InvariantCulture);
                candidate = Fit(label, Math.Max(0, 4 - number.Length)) + number;
            }
            result.Add(device.Id, candidate);
        }
        return result;
    }
}

internal enum TrayBadgeKind { NonBluetooth, NoDevices, Warning, Bluetooth }
internal static class TrayPalette
{
    public static string BackgroundHex(TrayBadgeKind kind) => kind switch
    {
        TrayBadgeKind.Bluetooth => "#4169E1", // Royal blue
        TrayBadgeKind.NonBluetooth => "#12704A",   // Preserve the speaker green
        TrayBadgeKind.Warning => "#A24B00",
        _ => "#555B64"
    };
}
internal sealed record TrayBadge(string Id, string Text, string Description, TrayBadgeKind Kind)
{
    public string BackgroundColor => TrayPalette.BackgroundHex(Kind);
}

internal static class TrayPresentation
{
    // A single return value and a single NotifyIcon enforce one shell icon in every state.
    public static TrayBadge Build(MonitorSnapshot snapshot, bool stale,
        IReadOnlyDictionary<string, string> labels, string? previousDeviceId = null)
    {
        if (stale || snapshot.Error is not null)
            return new("$status", "!", stale ? "音频数据已过期，正在等待恢复" : snapshot.Error!, TrayBadgeKind.Warning);
        var available = snapshot.Devices.Where(d => d.IsAvailable)
            .OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (available.Length == 0)
            return new("$status", "—", "未检测到已连接的音频输出设备", TrayBadgeKind.NoDevices);

        // Actual output (including the short gap hold) takes precedence over an idle default.
        // With concurrent outputs prefer the default, then retain the current device to avoid cycling.
        var playing = available.Where(d => d.Error is null &&
            d.Activity is Activity.Signal or Activity.Recent).ToArray();
        var candidates = playing.Length > 0 ? playing : available;
        var device = candidates.FirstOrDefault(d => d.Id == snapshot.DefaultOutputId)
            ?? candidates.FirstOrDefault(d => d.Id == previousDeviceId)
            ?? candidates.FirstOrDefault(d => d.Error is null)
            ?? candidates[0];
        var role = device.Id == snapshot.DefaultOutputId ? " · 系统默认" :
            playing.Length == 0 ? " · 备用设备" : "";
        var concurrent = playing.Length > 1 ? $" · 另有 {playing.Length - 1} 台输出" : "";
        var kind = device.Error is not null || device.Activity == Activity.Unavailable ? TrayBadgeKind.Warning :
            device.IsBluetooth ? TrayBadgeKind.Bluetooth : TrayBadgeKind.NonBluetooth;
        return new(device.Id, labels[device.Id], $"{device.Name} · {device.Status}{role}{concurrent}", kind);
    }
}
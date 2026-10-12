using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AudioFromWhatDevice;

internal static class TrayBadgeRenderer
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    public static int IconSize()
    {
        var dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
        return Math.Clamp(GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi), 16, 64);
    }

    public static Bitmap Draw(string text, TrayBadgeKind kind, int size, int? battery = null)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        // Solid high-contrast tile: text stays readable against either taskbar theme.
        graphics.Clear(ColorTranslator.FromHtml(TrayPalette.BackgroundHex(kind)));
        // Width, not height, limits a label, so the gauge below costs it little or no size.
        var textHeight = battery is int level ? DrawBattery(graphics, level, size) : size;
        var rows = text.Length > 3 && text.All(c => c <= 127) ? new[] { text[..2], text[2..] } : new[] { text };
        for (var index = 0; index < rows.Length; index++)
        {
            var height = textHeight / rows.Length;
            var area = new Rectangle(0, index * height, size, height);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            var pixels = rows.Length > 1 ? textHeight * .60f : textHeight * .85f;
            using var initialFont = new Font(text.All(c => c <= 127) ? "Segoe UI" : "Microsoft YaHei UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(graphics, rows[index], initialFont, new Size(int.MaxValue, int.MaxValue), flags);
            var scale = Math.Min(1f, (float)size / measured.Width);
            using var font = new Font(text.All(c => c <= 127) ? "Segoe UI" : "Microsoft YaHei UI", Math.Max(5, pixels * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, rows[index], font, area, Color.White, flags);
        }
        return bitmap;
    }

    // A battery along the bottom edge, filled to the level. The terminal on its right end tells it
    // apart from an audio level meter. Returns the height left above it for the label.
    private static int DrawBattery(Graphics graphics, int level, int size)
    {
        var margin = Math.Max(1, size / 16);
        var height = Math.Max(3, (int)Math.Round(size * .19));
        var body = new Rectangle(margin, size - margin - height, size - 3 * margin, height);
        var inset = Math.Max(1, height / 3);
        var terminal = new Rectangle(body.Right, body.Y + inset, margin, height - 2 * inset);
        // A translucent dark track shows the empty part on any tile color.
        using var track = new SolidBrush(Color.FromArgb(110, Color.Black));
        graphics.FillRectangle(track, body);
        graphics.FillRectangle(track, terminal);
        level = Math.Clamp(level, 0, 100);
        // Any charge keeps at least one pixel lit, so a nearly empty battery never looks absent.
        var filled = level == 0 ? 0 : Math.Max(1, (int)Math.Round(body.Width * level / 100.0));
        using var fill = new SolidBrush(ColorTranslator.FromHtml(TrayPalette.BatteryHex(level)));
        graphics.FillRectangle(fill, body with { Width = filled });
        if (level == 100) graphics.FillRectangle(fill, terminal);
        return body.Y;
    }

    public static Icon Create(string text, TrayBadgeKind kind, int size, int? battery = null)
    {
        using var bitmap = Draw(text, kind, size, battery);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { Native.DestroyIcon(handle); }
    }

    internal static void WritePreview(string path)
    {
        (string Text, TrayBadgeKind Kind, int? Battery)[] samples =
        [
            ("RT", TrayBadgeKind.NonBluetooth, null), ("HE", TrayBadgeKind.Bluetooth, null),
            ("XM5", TrayBadgeKind.Bluetooth, null), ("耳机", TrayBadgeKind.Bluetooth, null),
            ("音箱", TrayBadgeKind.NonBluetooth, null), ("—", TrayBadgeKind.NoDevices, null), ("!", TrayBadgeKind.Warning, null),
            ("XM5", TrayBadgeKind.Bluetooth, 81), ("耳机", TrayBadgeKind.Bluetooth, 100), ("AB12", TrayBadgeKind.Bluetooth, 45),
            ("HE", TrayBadgeKind.Bluetooth, 18), ("XM5", TrayBadgeKind.Bluetooth, 7), ("XM5", TrayBadgeKind.Warning, 60)
        ];
        using var board = new Bitmap(85 + samples.Length * 80, 210);
        board.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(board);
        graphics.Clear(Color.FromArgb(248, 246, 241));
        using var font = new Font("Microsoft YaHei UI", 10);
        for (var row = 0; row < 3; row++)
        {
            var size = new[] { 16, 24, 32 }[row];
            graphics.DrawString($"{size}px", font, Brushes.Black, 10, row * 65 + 18);
            for (var col = 0; col < samples.Length; col++)
            {
                var (text, kind, battery) = samples[col];
                using var tile = Draw(text, kind, size, battery);
                graphics.DrawImageUnscaled(tile, 85 + col * 80, row * 65 + 8);
                graphics.DrawString(battery is int level ? $"{text} {level}%" : text, font, Brushes.Black, 80 + col * 80, row * 65 + 42);
            }
        }
        board.Save(Path.GetFullPath(path), System.Drawing.Imaging.ImageFormat.Png);
    }
}

internal sealed class TraySlot : IDisposable
{
    private readonly NotifyIcon icon;
    private Icon? image;
    private (string Text, TrayBadgeKind Kind, int? Battery, int Size)? appearance;
    public TraySlot(ContextMenuStrip menu, Action click)
    {
        icon = new NotifyIcon { ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) click(); };
    }

    public void Show(TrayBadge badge, int size)
    {
        var next = (badge.Text, badge.Kind, badge.Battery, size);
        if (appearance != next)
        {
            var replacement = TrayBadgeRenderer.Create(badge.Text, badge.Kind, size, badge.Battery);
            icon.Icon = replacement;
            image?.Dispose();
            image = replacement;
            appearance = next;
        }
        var description = $"{badge.Text} · {badge.Description}";
        icon.Text = description.Length <= 63 ? description : description[..60] + "…";
        icon.Visible = true;
    }
    public void Hide() => icon.Visible = false;
    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        image?.Dispose();
        image = null;
    }
}

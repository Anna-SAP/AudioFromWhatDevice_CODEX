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

    public static Bitmap Draw(string text, TrayBadgeKind kind, int size)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        // Solid high-contrast tile: text stays readable against either taskbar theme.
        graphics.Clear(ColorTranslator.FromHtml(TrayPalette.BackgroundHex(kind)));
        var rows = text.Length > 3 && text.All(c => c <= 127) ? new[] { text[..2], text[2..] } : new[] { text };
        for (var index = 0; index < rows.Length; index++)
        {
            var height = size / rows.Length;
            var area = new Rectangle(0, index * height, size, height);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            var pixels = rows.Length > 1 ? size * .60f : size * .85f;
            using var initialFont = new Font(text.All(c => c <= 127) ? "Segoe UI" : "Microsoft YaHei UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(graphics, rows[index], initialFont, new Size(int.MaxValue, int.MaxValue), flags);
            var scale = Math.Min(1f, (float)size / measured.Width);
            using var font = new Font(text.All(c => c <= 127) ? "Segoe UI" : "Microsoft YaHei UI", Math.Max(5, pixels * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, rows[index], font, area, Color.White, flags);
        }
        return bitmap;
    }

    public static Icon Create(string text, TrayBadgeKind kind, int size)
    {
        using var bitmap = Draw(text, kind, size);
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
        var samples = new[] { "RT", "HE", "XM5", "耳机", "音箱", "—", "!" };
        using var board = new Bitmap(660, 210);
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
                var kind = col == 5 ? TrayBadgeKind.NoDevices : col == 6 ? TrayBadgeKind.Warning : col is 1 or 2 or 3 ? TrayBadgeKind.Bluetooth : TrayBadgeKind.NonBluetooth;
                using var tile = Draw(samples[col], kind, size);
                graphics.DrawImageUnscaled(tile, 85 + col * 80, row * 65 + 8);
                graphics.DrawString(samples[col], font, Brushes.Black, 80 + col * 80, row * 65 + 42);
            }
        }
        board.Save(Path.GetFullPath(path), System.Drawing.Imaging.ImageFormat.Png);
    }
}

internal sealed class TraySlot : IDisposable
{
    private readonly NotifyIcon icon;
    private Icon? image;
    private (string Text, TrayBadgeKind Kind, int Size)? appearance;
    public TraySlot(ContextMenuStrip menu, Action click)
    {
        icon = new NotifyIcon { ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) click(); };
    }

    public void Show(TrayBadge badge, int size)
    {
        var next = (badge.Text, badge.Kind, size);
        if (appearance != next)
        {
            var replacement = TrayBadgeRenderer.Create(badge.Text, badge.Kind, size);
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

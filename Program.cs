using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GTACodigoOCR;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            using var app = new CaptureApplication();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GTA Codigo OCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class CaptureApplication : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu;
    private readonly GlobalHotkey hotkey;
    private readonly OcrEngine engine;
    private bool selectingOrReading;

    public CaptureApplication()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                 ?? OcrEngine.TryCreateFromUserProfileLanguages()
                 ?? throw new InvalidOperationException(
                     "Windows no tiene un idioma de OCR compatible instalado. " +
                     "Activa el reconocimiento de texto en las características de idioma de Windows.");

        menu = new ContextMenuStrip();
        menu.Items.Add("Salir", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GTA Codigo OCR - F8 para capturar",
            ContextMenuStrip = menu,
            Visible = true
        };

        try
        {
            hotkey = new GlobalHotkey(StartSelection);
        }
        catch
        {
            tray.Dispose();
            menu.Dispose();
            throw;
        }
    }

    private void StartSelection()
    {
        if (selectingOrReading) return;
        selectingOrReading = true;
        Bitmap? snapshot = null;
        SelectionOverlay? overlay = null;
        try
        {
            // F8 congela el fotograma en memoria: no se pierde el código mientras seleccionas.
            Rectangle desktop = SystemInformation.VirtualScreen;
            snapshot = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(snapshot))
                g.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size,
                    CopyPixelOperation.SourceCopy);

            overlay = new SelectionOverlay(snapshot, desktop);
            snapshot = null;
            overlay.Completed += OnSelectionCompleted;
            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            overlay?.Dispose();
            snapshot?.Dispose();
            selectingOrReading = false;
            tray.ShowBalloonTip(2000, "GTA Codigo OCR", ex.Message, ToolTipIcon.Error);
        }
    }

    private async void OnSelectionCompleted(Bitmap? selected)
    {
        if (selected is null)
        {
            selectingOrReading = false;
            return;
        }

        using (selected)
        {
            try
            {
                string? code = await RecognizeCodeAsync(selected);
                if (code is null)
                {
                    tray.ShowBalloonTip(1200, "GTA Codigo OCR",
                        "No se reconoció un código. Selecciona solo el texto e intenta otra vez.",
                        ToolTipIcon.Info);
                    return;
                }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(code);
                        SystemSounds.Asterisk.Play();
                        return;
                    }
                    catch (ExternalException) when (attempt < 3)
                    {
                        await Task.Delay(35);
                    }
                }
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(2000, "GTA Codigo OCR", ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                selectingOrReading = false;
            }
        }
    }

    private async Task<string?> RecognizeCodeAsync(Bitmap selected)
    {
        // OCR rápido: ampliar las capturas pequeñas primero.
        int zoom = selected.Height <= 110 ? 4 : selected.Height <= 240 ? 3 : 1;
        using (var normal = MakeOcrImage(selected, zoom, false))
        {
            string? found = await ReadCodeAsync(normal);
            if (found is not null) return found;
        }

        // Si falla: escala de grises y contraste extra.
        using (var contrast = MakeOcrImage(selected, zoom == 1 ? 2 : zoom, true))
        {
            string? found = await ReadCodeAsync(contrast);
            if (found is not null) return found;
        }

        // Último intento con la resolución original.
        using var original = MakeOcrImage(selected, 1, false);
        return await ReadCodeAsync(original);
    }

    private async Task<string?> ReadCodeAsync(Bitmap bitmap)
    {
        using var softwareBitmap = ToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(softwareBitmap);
        return CodeParser.FindCode(result.Text);
    }

    private static Bitmap MakeOcrImage(Bitmap source, int targetZoom, bool highContrast)
    {
        const int margin = 12;
        int limit = checked((int)OcrEngine.MaxImageDimension);
        double zoom = Math.Min(targetZoom,
            (double)(limit - 2 * margin) / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Floor(source.Width * zoom));
        int height = Math.Max(1, (int)Math.Floor(source.Height * zoom));
        var output = new Bitmap(width + 2 * margin, height + 2 * margin,
            PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(output))
        {
            g.Clear(source.GetPixel(0, 0));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            Rectangle target = new(margin, margin, width, height);
            if (!highContrast)
            {
                g.DrawImage(source, target, 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel);
            }
            else
            {
                const float k = 1.65f;
                const float offset = -0.325f;
                var matrix = new ColorMatrix(new[]
                {
                    new[] { .299f*k, .299f*k, .299f*k, 0f, 0f },
                    new[] { .587f*k, .587f*k, .587f*k, 0f, 0f },
                    new[] { .114f*k, .114f*k, .114f*k, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { offset, offset, offset, 0f, 1f }
                });
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(matrix);
                g.DrawImage(source, target, 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attrs);
            }
        }
        return output;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap image)
    {
        var rectangle = new Rectangle(0, 0, image.Width, image.Height);
        BitmapData bits = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowLength = checked(image.Width * 4);
            var pixels = new byte[checked(rowLength * image.Height)];
            for (int y = 0; y < image.Height; y++)
            {
                // Format32bppArgb guarda cada píxel en orden BGRA en memoria.
                IntPtr row = IntPtr.Add(bits.Scan0, checked(y * bits.Stride));
                Marshal.Copy(row, pixels, y * rowLength, rowLength);
            }

            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            IBuffer buffer = writer.DetachBuffer();
            return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
                image.Width, image.Height, BitmapAlphaMode.Ignore);
        }
        finally
        {
            image.UnlockBits(bits);
        }
    }

    protected override void ExitThreadCore()
    {
        hotkey.Dispose();
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

internal static class CodeParser
{
    private static readonly Regex WithSeparators = new(
        @"(?<![A-Z0-9])([A-Z0-9]{4})[\s\p{Pd}]+([A-Z0-9]{4})[\s\p{Pd}]+([A-Z0-9]{4})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WithoutSeparators = new(
        @"(?<![A-Z0-9])([A-Z0-9]{12})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? FindCode(string raw)
    {
        string text = raw.ToUpperInvariant();
        Match match = WithSeparators.Match(text);
        if (match.Success)
            return $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";

        match = WithoutSeparators.Match(text);
        if (match.Success)
        {
            string value = match.Groups[1].Value;
            return $"{value[..4]}-{value.Substring(4, 4)}-{value[8..]}";
        }
        return null;
    }
}

internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF8 = 0x77;

    private readonly Action callback;

    public GlobalHotkey(Action callback)
    {
        this.callback = callback;
        CreateHandle(new CreateParams { Caption = "GTACodigoOCR-Hotkey" });
        if (!RegisterHotKey(Handle, HotkeyId, ModNoRepeat, VkF8))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "No se pudo registrar F8. Otra aplicación podría estar usándola.");
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
            callback();
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;

        UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);
}

internal sealed class SelectionOverlay : Form
{
    private readonly Bitmap screenshot;
    private Point? start;
    private Point cursor;
    private bool finished;
    public event Action<Bitmap?>? Completed;

    public SelectionOverlay(Bitmap frozenScreen, Rectangle virtualScreen)
    {
        screenshot = frozenScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        DoubleBuffered = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        start = e.Location;
        cursor = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (start is null) return;
        cursor = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || start is null) return;
        Capture = false;
        Rectangle area = Rectangle.Intersect(GetSelection(start.Value, e.Location),
            new Rectangle(Point.Empty, ClientSize));
        Bitmap? cropped = area.Width >= 8 && area.Height >= 8
            ? screenshot.Clone(area, PixelFormat.Format32bppArgb) : null;
        Finish(cropped);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Finish(null);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawImageUnscaled(screenshot, Point.Empty);
        using (var shade = new SolidBrush(Color.FromArgb(125, Color.Black)))
            e.Graphics.FillRectangle(shade, ClientRectangle);
        if (start is null) return;
        Rectangle area = Rectangle.Intersect(GetSelection(start.Value, cursor),
            new Rectangle(Point.Empty, ClientSize));
        if (area.Width > 0 && area.Height > 0)
        {
            e.Graphics.DrawImage(screenshot, area, area, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(pen, area);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!finished)
        {
            finished = true;
            Completed?.Invoke(null);
        }
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) screenshot.Dispose();
        base.Dispose(disposing);
    }

    private void Finish(Bitmap? selected)
    {
        if (finished)
        {
            selected?.Dispose();
            return;
        }
        finished = true;
        Hide();
        try { Completed?.Invoke(selected); }
        finally { Close(); }
    }

    private static Rectangle GetSelection(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

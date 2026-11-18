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
        if (selectingOrReading)
            return;

        selectingOrReading = true;
        try
        {
            var overlay = new SelectionOverlay();
            overlay.Completed += OnSelectionCompleted;
            overlay.Show();
            overlay.Activate();
        }
        catch
        {
            selectingOrReading = false;
            throw;
        }
    }

    private async void OnSelectionCompleted(Rectangle? selected)
    {
        if (selected is null)
        {
            selectingOrReading = false;
            return;
        }

        try
        {
            // Espera brevemente a que Windows retire la superposición de pantalla.
            await Task.Delay(65);

            using var bitmap = new Bitmap(selected.Value.Width, selected.Value.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(selected.Value.Location, Point.Empty, selected.Value.Size,
                    CopyPixelOperation.SourceCopy);
            }

            string? code = await RecognizeCodeAsync(bitmap);
            if (code is null)
            {
                tray.ShowBalloonTip(1000, "GTA Codigo OCR",
                    "No se encontró un código de 12 caracteres. Selecciona una zona más ajustada.",
                    ToolTipIcon.Info);
                return;
            }

            // Esta continuación vuelve al hilo STA de Windows Forms.
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

    private async Task<string?> RecognizeCodeAsync(Bitmap original)
    {
        using var image = MakeOcrImage(original, upscale: false);
        using (var softwareBitmap = ToSoftwareBitmap(image))
        {
            var result = await engine.RecognizeAsync(softwareBitmap);
            string? found = CodeParser.FindCode(result.Text);
            if (found is not null)
                return found;
        }

        // Un segundo intento a mayor resolución ayuda cuando el código es muy pequeño.
        if (original.Width <= 800 && original.Height <= 240)
        {
            using var enlarged = MakeOcrImage(original, upscale: true);
            using var softwareBitmap = ToSoftwareBitmap(enlarged);
            var retry = await engine.RecognizeAsync(softwareBitmap);
            return CodeParser.FindCode(retry.Text);
        }

        return null;
    }

    private Bitmap MakeOcrImage(Bitmap source, bool upscale)
    {
        int factor = upscale ? 2 : 1;
        int width = source.Width * factor;
        int height = source.Height * factor;
        int limit = checked((int)OcrEngine.MaxImageDimension);
        if (Math.Max(width, height) > limit)
        {
            double ratio = (double)limit / Math.Max(width, height);
            width = Math.Max(1, (int)(width * ratio));
            height = Math.Max(1, (int)(height * ratio));
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        return bitmap;
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
    private Point? start;
    private Point cursor;
    private bool finished;

    public event Action<Rectangle?>? Completed;

    public SelectionOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.24;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        DoubleBuffered = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        start = e.Location;
        cursor = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (start is null)
            return;
        cursor = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || start is null)
            return;
        Capture = false;
        Rectangle local = GetSelection(start.Value, e.Location);
        var screenRectangle = new Rectangle(local.X + Left, local.Y + Top, local.Width, local.Height);
        screenRectangle = Rectangle.Intersect(screenRectangle, SystemInformation.VirtualScreen);
        Finish(screenRectangle.Width >= 8 && screenRectangle.Height >= 8 ? screenRectangle : null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Finish(null);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (start is null)
            return;
        Rectangle selection = GetSelection(start.Value, cursor);
        using var pen = new Pen(Color.White, 3);
        e.Graphics.DrawRectangle(pen, selection);
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

    private void Finish(Rectangle? rectangle)
    {
        if (finished)
            return;
        finished = true;
        Hide();
        Completed?.Invoke(rectangle);
        Close();
    }

    private static Rectangle GetSelection(Point p1, Point p2) => Rectangle.FromLTRB(
        Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
        Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
}

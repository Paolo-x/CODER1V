using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

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
    private readonly GlobalHotkeys hotkeys;
    private readonly OcrEngine engine;
    private Rectangle? captureArea;
    private IntPtr targetWindow;
    private uint targetProcessId;
    private bool busy;

    public CaptureApplication()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                 ?? OcrEngine.TryCreateFromUserProfileLanguages()
                 ?? throw new InvalidOperationException(
                     "No hay OCR compatible en Windows. Activa reconocimiento de texto en los idiomas instalados.");

        menu = new ContextMenuStrip();
        menu.Items.Add("Salir", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GTA Codigo OCR V3 - F6 destino, F7 zona, F8 enviar",
            ContextMenuStrip = menu,
            Visible = true
        };

        try
        {
            hotkeys = new GlobalHotkeys(HandleHotkey);
        }
        catch
        {
            tray.Dispose();
            menu.Dispose();
            throw;
        }

        Notify("Configura F7 (zona) y F6 (navegador); después F8 captura y envía.");
    }

    private void HandleHotkey(Keys key)
    {
        switch (key)
        {
            case Keys.F6: RegisterDestination(); break;
            case Keys.F7: ChooseRegion(); break;
            case Keys.F8: CaptureAndSend(); break;
        }
    }

    private void RegisterDestination()
    {
        if (busy) return;
        IntPtr window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero || !Native.IsWindow(window))
        {
            Notify("No se encontró la ventana activa.", ToolTipIcon.Warning);
            return;
        }

        Native.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            Notify("Primero abre la página de canje y selecciona el campo del código.",
                ToolTipIcon.Warning);
            return;
        }

        // Se registra la ventana visible; el campo de texto debe estar enfocado.
        targetWindow = window;
        targetProcessId = processId;
        var title = new System.Text.StringBuilder(100);
        Native.GetWindowText(window, title, title.Capacity);
        string caption = title.ToString();
        if (caption.Length > 55) caption = caption[..55];
        Notify("Destino registrado: " + (caption.Length == 0 ? "navegador" : caption));
        SystemSounds.Asterisk.Play();
    }

    private void ChooseRegion()
    {
        if (busy) return;
        busy = true;
        Bitmap? snapshot = null;
        SelectionOverlay? overlay = null;
        try
        {
            Rectangle screen = SystemInformation.VirtualScreen;
            snapshot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(snapshot))
                g.CopyFromScreen(screen.Location, Point.Empty, screen.Size,
                    CopyPixelOperation.SourceCopy);

            overlay = new SelectionOverlay(snapshot, screen);
            snapshot = null; // SelectionOverlay es propietaria de la imagen.
            overlay.Completed += result =>
            {
                if (result.HasValue)
                {
                    captureArea = result.Value;
                    Notify($"Zona guardada: {result.Value.Width} x {result.Value.Height} píxeles. Usa F8.");
                    SystemSounds.Asterisk.Play();
                }
                busy = false;
            };
            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            overlay?.Dispose();
            snapshot?.Dispose();
            busy = false;
            Notify(ex.Message, ToolTipIcon.Error);
        }
    }

    private async void CaptureAndSend()
    {
        if (busy) return;
        if (captureArea is null)
        {
            Notify("Primero pulsa F7 para seleccionar la zona donde aparece el código.",
                ToolTipIcon.Warning);
            return;
        }
        if (targetWindow == IntPtr.Zero || !Native.IsWindow(targetWindow))
        {
            Notify("Primero pulsa F6 dentro del navegador de canje, con el campo enfocado.",
                ToolTipIcon.Warning);
            return;
        }

        Rectangle region = captureArea.Value;
        if (!SystemInformation.VirtualScreen.Contains(region))
        {
            Notify("La configuración de pantallas cambió. Vuelve a seleccionar la zona con F7.",
                ToolTipIcon.Warning);
            return;
        }

        busy = true;
        try
        {
            // F8 captura inmediatamente SOLO la zona previamente seleccionada.
            // Todo permanece en RAM: no se guarda ninguna imagen en disco.
            using var screenshot = new Bitmap(region.Width, region.Height,
                PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenshot))
                g.CopyFromScreen(region.Location, Point.Empty, region.Size,
                    CopyPixelOperation.SourceCopy);

            OcrDecision decision = await RecognizeWithAgreementAsync(screenshot);
            if (decision.Code is null)
            {
                Notify(decision.Message, ToolTipIcon.Warning);
                SystemSounds.Exclamation.Play();
                return;
            }

            string code = decision.Code;
            bool copied = await CopyAsync(code);
            if (!copied)
            {
                Notify("No se pudo copiar al portapapeles.", ToolTipIcon.Error);
                return;
            }

            // Si cambió la ventana registrada, NUNCA enviamos pulsaciones a otra app.
            if (!Native.IsWindow(targetWindow) ||
                Native.GetWindowThreadProcessId(targetWindow, out uint pid) == 0 ||
                pid != targetProcessId)
            {
                Notify("El navegador registrado se cerró o cambió. Código copiado: " + code,
                    ToolTipIcon.Warning);
                return;
            }

            if (Native.IsIconic(targetWindow))
                Native.ShowWindow(targetWindow, 9); // SW_RESTORE
            Native.SetForegroundWindow(targetWindow);
            await Task.Delay(100); // Dar tiempo a Windows para restaurar el foco.
            if (Native.GetForegroundWindow() != targetWindow)
            {
                Notify("No se pudo activar el navegador. Código copiado: " + code,
                    ToolTipIcon.Warning);
                return;
            }

            // F6 debe haberse pulsado con el campo del código enfocado.
            SendKeys.SendWait("^a");
            SendKeys.SendWait("^v");
            await Task.Delay(160); // El sitio puede procesar el evento de pegado.
            if (Native.GetForegroundWindow() != targetWindow)
            {
                Notify("Se perdió el foco; no se envió Enter. Código copiado: " + code,
                    ToolTipIcon.Warning);
                return;
            }
            SendKeys.SendWait("{ENTER}");
            SystemSounds.Asterisk.Play();
            Notify("Código " + code + " enviado al navegador (Enter). No confirma el canje.");
        }
        catch (Exception ex)
        {
            Notify(ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task<bool> CopyAsync(string code)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                Clipboard.SetText(code);
                return true;
            }
            catch (ExternalException)
            {
                if (i == 3) return false;
                await Task.Delay(40);
            }
        }
        return false;
    }

    private async Task<OcrDecision> RecognizeWithAgreementAsync(Bitmap source)
    {
        // Se prueban TRES variantes distintas de una sola captura, ya que 3/J,
        // 0/O y 5/S pueden confundirse en subtítulos y tipografías diminutas.
        // Para enviar automáticamente exigimos dos lecturas iguales y ninguna
        // contradicción. Esto reduce errores, pero no garantiza el 100 %.
        int zoom = source.Height <= 130 ? 4 : source.Height <= 260 ? 3 : 2;
        var readings = new List<string>();

        foreach (ImageVariant variant in new[]
        {
            ImageVariant.Bicubic,
            ImageVariant.Nearest,
            ImageVariant.Contrast
        })
        {
            using var prepared = MakeOcrImage(source, zoom, variant);
            using var sb = ToSoftwareBitmap(prepared);
            var result = await engine.RecognizeAsync(sb);
            string? code = CodeParser.FindCode(result.Text);
            if (code is not null) readings.Add(code);
        }

        if (readings.Count == 0)
            return new(null, "El OCR no encontró un código. Comprueba la zona con F7.");

        string[] distinct = readings.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1)
            return new(null, "OCR dudoso: " + string.Join(" / ", distinct) +
                ". No se envió Enter; vuelve a probar.");

        if (readings.Count < 2)
            return new(null, "Solo una de tres lecturas detectó " + distinct[0] +
                ". No se envió por seguridad.");

        return new(distinct[0], "OK");
    }

    private static Bitmap MakeOcrImage(Bitmap source, int targetZoom, ImageVariant variant)
    {
        const int margin = 14;
        int limit = checked((int)OcrEngine.MaxImageDimension);
        double factor = Math.Min(targetZoom,
            (double)(limit - 2 * margin) / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Floor(source.Width * factor));
        int height = Math.Max(1, (int)Math.Floor(source.Height * factor));
        var output = new Bitmap(width + margin * 2, height + margin * 2,
            PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(output);
        g.Clear(source.GetPixel(0, 0));
        g.InterpolationMode = variant == ImageVariant.Nearest
            ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = variant == ImageVariant.Nearest
            ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
        Rectangle destination = new(margin, margin, width, height);

        if (variant == ImageVariant.Contrast)
        {
            const float k = 1.6f;
            const float offset = -0.3f;
            var matrix = new ColorMatrix(new[]
            {
                new[] { .299f*k, .299f*k, .299f*k, 0f, 0f },
                new[] { .587f*k, .587f*k, .587f*k, 0f, 0f },
                new[] { .114f*k, .114f*k, .114f*k, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { offset, offset, offset, 0f, 1f }
            });
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            g.DrawImage(source, destination, 0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel, attributes);
        }
        else
        {
            g.DrawImage(source, destination, 0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel);
        }
        return output;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap image)
    {
        Rectangle rect = new(0, 0, image.Width, image.Height);
        BitmapData bits = image.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowLength = checked(image.Width * 4);
            byte[] pixels = new byte[checked(rowLength * image.Height)];
            for (int y = 0; y < image.Height; y++)
            {
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

    private void Notify(string text, ToolTipIcon severity = ToolTipIcon.Info)
    {
        tray.ShowBalloonTip(1800, "GTA Codigo OCR V3", text, severity);
    }

    protected override void ExitThreadCore()
    {
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

internal enum ImageVariant { Bicubic, Nearest, Contrast }
internal readonly record struct OcrDecision(string? Code, string Message);

internal static class CodeParser
{
    private static readonly Regex WithDashes = new(
        @"(?<![A-Z0-9])([A-Z0-9]{4})[\s\p{Pd}]+([A-Z0-9]{4})[\s\p{Pd}]+([A-Z0-9]{4})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Continuous = new(
        @"(?<![A-Z0-9])([A-Z0-9]{12})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string? FindCode(string raw)
    {
        string text = raw.ToUpperInvariant();
        Match match = WithDashes.Match(text);
        if (match.Success)
            return $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";

        match = Continuous.Match(text);
        if (!match.Success) return null;
        string value = match.Groups[1].Value;
        return $"{value[..4]}-{value.Substring(4, 4)}-{value[8..]}";
    }
}

internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
}

internal sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly Action<Keys> callback;
    private readonly List<int> registeredIds = new();

    public GlobalHotkeys(Action<Keys> callback)
    {
        this.callback = callback;
        CreateHandle(new CreateParams { Caption = "GTACodigoOCR-GlobalHotkeys" });
        try
        {
            Register(1, Keys.F6);
            Register(2, Keys.F7);
            Register(3, Keys.F8);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Register(int id, Keys key)
    {
        if (!RegisterHotKey(Handle, id, ModNoRepeat, (uint)key))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"No fue posible registrar {key}. Es probable que otra aplicación la use.");
        registeredIds.Add(id);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            switch (message.WParam.ToInt32())
            {
                case 1: callback(Keys.F6); break;
                case 2: callback(Keys.F7); break;
                case 3: callback(Keys.F8); break;
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        foreach (int id in registeredIds)
            UnregisterHotKey(Handle, id);
        registeredIds.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal sealed class SelectionOverlay : Form
{
    private readonly Bitmap screenshot;
    private Point? start;
    private Point cursor;
    private bool finished;
    public event Action<Rectangle?>? Completed;

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
        Rectangle absolute = new(area.X + Left, area.Y + Top, area.Width, area.Height);
        Finish(area.Width >= 8 && area.Height >= 8 ? absolute : null);
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

    private void Finish(Rectangle? selected)
    {
        if (finished) return;
        finished = true;
        Hide();
        try { Completed?.Invoke(selected); }
        finally { Close(); }
    }

    private static Rectangle GetSelection(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

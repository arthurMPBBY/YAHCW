using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightTray
{
    internal sealed class DashboardBridge : IDisposable
    {
        private readonly MonitorService service;
        private readonly Action updated;
        private readonly object gate = new object();
        private System.IO.Pipes.NamedPipeServerStream server;
        private volatile bool stopping;

        public DashboardBridge(MonitorService service, Action updated)
        {
            this.service = service;
            this.updated = updated;
            Thread worker = new Thread(Run) { IsBackground = true, Name = "BrightTray dashboard IPC" };
            worker.Start();
        }

        private void Run()
        {
            while (!stopping)
            {
                try
                {
                    System.IO.Pipes.PipeSecurity security = new System.IO.Pipes.PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null),
                        System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
                    security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.IO.Pipes.PipeAccessRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    using (System.IO.Pipes.NamedPipeServerStream pipe = new System.IO.Pipes.NamedPipeServerStream(
                        "BrightTray.Dashboard.v1", System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous,
                        4096, 4096, security))
                    {
                        lock (gate)
                        {
                            if (stopping) return;
                            server = pipe;
                        }
                        pipe.WaitForConnection();
                        using (BinaryReader reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, true))
                        using (BinaryWriter writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true))
                        {
                            string response;
                            try
                            {
                                int command = reader.ReadInt32();
                                if (command == 1)
                                {
                                    string id = reader.ReadString();
                                    int percent = reader.ReadInt32();
                                    if (id.Length > 64 || percent < 0 || percent > 100)
                                        throw new InvalidOperationException("Pedido de brilho inválido.");
                                    service.SetById(id, percent);
                                    updated();
                                }
                                else if (command != 0) throw new InvalidOperationException("Comando inválido.");
                                response = service.DashboardSnapshot();
                            }
                            catch (Exception error) { response = "ERR\n" + error.Message; }
                            writer.Write(response);
                            writer.Flush();
                        }
                    }
                }
                catch (IOException) { if (stopping) return; }
                catch (ObjectDisposedException) { if (stopping) return; }
                catch (Exception error)
                {
                    if (stopping) return;
                    try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"),
                        "Dashboard IPC: " + error.Message + Environment.NewLine); } catch { }
                    return;
                }
                finally { lock (gate) { server = null; } }
            }
        }

        public void Dispose()
        {
            stopping = true;
            lock (gate) { if (server != null) server.Dispose(); }
        }
    }
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " Main entered, args=" + string.Join(" ", args) + Environment.NewLine);
                if (Array.IndexOf(args, "--diagnose") >= 0)
                {
                    using (MonitorService service = new MonitorService())
                    {
                        List<Display> displays = service.Discover();
                        List<string> report = new List<string>();
                        report.Add("BrightTray - diagnostico somente leitura");
                        report.Add("Monitores controlaveis: " + displays.Count);
                        foreach (Display display in displays)
                            report.Add(display.Name + " | " + display.Backend + " | brilho: " + display.Percent + "%");
                        report.AddRange(service.Diagnostics);
                        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics.txt"), report.ToArray());
                    }
                    return;
                }
                bool created;
                using (Mutex mutex = new Mutex(true, @"Local\BrightTray.App", out created))
                {
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " Mutex created=" + created + Environment.NewLine);
                    if (!created)
                    {
                        try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(@"Local\BrightTray.Show")) signal.Set(); }
                        catch (WaitHandleCannotBeOpenedException) { }
                        return;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " Before App.Run" + Environment.NewLine);
                    Application.Run(new TrayContext(Array.IndexOf(args, "--background") < 0, Array.IndexOf(args, "--keep-open") >= 0));
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " After App.Run" + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " Main Exception: " + ex + Environment.NewLine);
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString()); }
                catch { }
            }
        }
    }

    internal sealed class Display
    {
        public string Name;
        public string Backend;
        public string Instance;
        public IntPtr Handle;
        public uint Minimum;
        public uint Maximum;
        public int Percent;
        public bool Released;
        public readonly string DashboardId = Guid.NewGuid().ToString("N");
    }

    internal sealed class MonitorService : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<Display> current = new List<Display>();
        public readonly List<string> Diagnostics = new List<string>();

        public List<Display> Discover()
        {
            lock (gate)
            {
                Release();
                Diagnostics.Clear();
                Native.MonitorEnum callback = delegate(IntPtr monitor, IntPtr device, ref Native.Rect rectangle, IntPtr data)
                {
                    uint count;
                    if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0) return true;
                    Native.PhysicalMonitor[] physical = new Native.PhysicalMonitor[count];
                    if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return true;
                    foreach (Native.PhysicalMonitor item in physical)
                    {
                        bool retained = false;
                        try
                        {
                            uint minimum, value, maximum;
                            if (Native.GetMonitorBrightness(item.Handle, out minimum, out value, out maximum) && maximum > minimum)
                            {
                                current.Add(new Display {
                                    Name = item.Description, Backend = "DDC/CI", Handle = item.Handle,
                                    Minimum = minimum, Maximum = maximum,
                                    Percent = Math.Max(0, Math.Min(100, (int)Math.Round(100.0 * ((double)value - minimum) / (maximum - minimum))))
                                });
                                retained = true;
                            }
                            else Diagnostics.Add("DDC/CI indisponivel: " + item.Description + " (erro " + Marshal.GetLastWin32Error() + ")");
                        }
                        finally { if (!retained) Native.DestroyPhysicalMonitor(item.Handle); }
                    }
                    return true;
                };
                Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        using (item)
                            current.Add(new Display {
                                Name = "Tela interna", Backend = "WMI", Instance = (string)item["InstanceName"],
                                Minimum = 0, Maximum = 100, Percent = Convert.ToInt32(item["CurrentBrightness"])
                            });
                    }
                }
                catch (ManagementException error) { Diagnostics.Add("WMI: " + error.Message); }
                catch (UnauthorizedAccessException error) { Diagnostics.Add("WMI: " + error.Message); }
                catch (COMException error) { Diagnostics.Add("WMI: " + error.Message); }
                return new List<Display>(current);
            }
        }

        public string DashboardSnapshot()
        {
            lock (gate)
            {
                System.Text.StringBuilder result = new System.Text.StringBuilder("OK\n");
                foreach (Display display in current)
                {
                    if (display.Released) continue;
                    result.Append(display.DashboardId).Append('|')
                        .Append(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(display.Name)))
                        .Append('|').Append(display.Percent).Append('\n');
                }
                return result.ToString();
            }
        }

        public void SetById(string id, int percent)
        {
            lock (gate)
            {
                Display target = current.Find(delegate(Display display) { return display.DashboardId == id && !display.Released; });
                if (target == null) throw new InvalidOperationException("Monitor mudou; atualize os controles.");
                Set(target, percent);
            }
        }

        public void Set(Display display, int percent)
        {
            lock (gate)
            {
                if (display.Released) throw new InvalidOperationException("Monitor desconectado. Atualize a lista.");
                percent = Math.Max(0, Math.Min(100, percent));
                if (display.Backend == "DDC/CI")
                {
                    uint value = display.Minimum + (uint)Math.Round((display.Maximum - display.Minimum) * percent / 100.0);
                    if (!Native.SetMonitorBrightness(display.Handle, value))
                        throw new InvalidOperationException("O monitor recusou o ajuste. Confira DDC/CI no menu do monitor.");
                }
                else
                {
                    bool found = false;
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        using (item)
                        {
                            if (!String.Equals((string)item["InstanceName"], display.Instance, StringComparison.OrdinalIgnoreCase)) continue;
                            object result = item.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                            if (result != null && Convert.ToUInt32(result) != 0)
                                throw new InvalidOperationException("O Windows recusou o ajuste de brilho.");
                            found = true;
                            break;
                        }
                    }
                    if (!found) throw new InvalidOperationException("Tela interna nao encontrada. Atualize a lista.");
                }
                display.Percent = percent;
            }
        }

        private void Release()
        {
            foreach (Display display in current)
            {
                display.Released = true;
                if (display.Handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(display.Handle);
            }
            current.Clear();
        }

        public void Dispose() { lock (gate) Release(); }
    }

    internal static class ThemeManager
    {
        public static bool IsDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val is int) return ((int)val) == 0;
                    }
                }
            }
            catch { }
            return true;
        }

        public static Color GetAccentColor(bool isDark)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColor");
                        if (val is int || val is long)
                        {
                            uint raw = Convert.ToUInt32(val);
                            byte r = (byte)(raw & 0xFF);
                            byte g = (byte)((raw >> 8) & 0xFF);
                            byte b = (byte)((raw >> 16) & 0xFF);
                            if (r != 0 || g != 0 || b != 0)
                                return Color.FromArgb(r, g, b);
                        }
                    }
                }
            }
            catch { }
            return isDark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
        }
    }

    internal sealed class Theme
    {
        public bool IsDark;
        public Color Background;
        public Color FlyoutBorder;
        public Color TextPrimary;
        public Color TextSecondary;
        public Color TextMuted;
        public Color Accent;
        public Color AccentHover;
        public Color TrackBackground;
        public Color TrackFill;
        public Color ThumbColor;
        public Color ThumbBorder;
        public Color ButtonHover;
        public Color ButtonPressed;

        public static Theme Current()
        {
            bool isDark = ThemeManager.IsDarkTheme();
            Color accent = ThemeManager.GetAccentColor(isDark);

            Theme t = new Theme();
            t.IsDark = isDark;
            t.Accent = accent;
            t.AccentHover = isDark ? ControlPaint.Light(accent, 0.15f) : ControlPaint.Dark(accent, 0.1f);

            if (isDark)
            {
                t.Background = Color.FromArgb(42, 46, 58); // Tom pastel suave (slate pastel)
                t.FlyoutBorder = Color.Transparent; // Borda branca removida
                t.TextPrimary = Color.FromArgb(242, 245, 250);
                t.TextSecondary = Color.FromArgb(170, 180, 198);
                t.TextMuted = Color.FromArgb(125, 133, 150);
                t.TrackBackground = Color.FromArgb(62, 68, 84); // Trilho suave pastel
                t.TrackFill = accent;
                t.ThumbColor = Color.FromArgb(255, 255, 255);
                t.ThumbBorder = Color.FromArgb(40, 0, 0, 0);
                t.ButtonHover = Color.FromArgb(28, 255, 255, 255);
                t.ButtonPressed = Color.FromArgb(48, 255, 255, 255);
            }
            else
            {
                t.Background = Color.FromArgb(240, 238, 245); // Tom pastel suave claro
                t.FlyoutBorder = Color.Transparent; // Borda branca removida
                t.TextPrimary = Color.FromArgb(32, 34, 42);
                t.TextSecondary = Color.FromArgb(110, 115, 130);
                t.TextMuted = Color.FromArgb(150, 155, 170);
                t.TrackBackground = Color.FromArgb(218, 216, 226);
                t.TrackFill = accent;
                t.ThumbColor = Color.FromArgb(255, 255, 255);
                t.ThumbBorder = Color.FromArgb(180, 180, 180);
                t.ButtonHover = Color.FromArgb(20, 0, 0, 0);
                t.ButtonPressed = Color.FromArgb(35, 0, 0, 0);
            }
            return t;
        }
    }

    internal static class FontHelper
    {
        private static readonly string[] PreferredFonts = new string[] {
            "Segoe UI Variable Text",
            "Segoe UI Variable Display",
            "Segoe UI",
            "Tahoma"
        };

        public static Font CreateFont(float sizePt, FontStyle style)
        {
            foreach (string fontName in PreferredFonts)
            {
                try
                {
                    using (FontFamily family = new FontFamily(fontName))
                    {
                        if (family.IsStyleAvailable(style))
                            return new Font(family, sizePt, style);
                    }
                }
                catch { }
            }
            return new Font(SystemFonts.DefaultFont.FontFamily, sizePt, style);
        }
    }

    internal static class GraphicsHelper
    {
        public static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void DrawSunIcon(Graphics g, Rectangle rect, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = rect.X + rect.Width / 2.0f;
            float cy = rect.Y + rect.Height / 2.0f;
            float radius = Math.Min(rect.Width, rect.Height) * 0.22f;

            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
            }

            float rayStart = radius + Math.Min(rect.Width, rect.Height) * 0.12f;
            float rayEnd = radius + Math.Min(rect.Width, rect.Height) * 0.26f;
            float penWidth = Math.Max(1.5f, Math.Min(rect.Width, rect.Height) * 0.085f);

            using (Pen pen = new Pen(color, penWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4.0;
                    float x1 = (float)(cx + Math.Cos(angle) * rayStart);
                    float y1 = (float)(cy + Math.Sin(angle) * rayStart);
                    float x2 = (float)(cx + Math.Cos(angle) * rayEnd);
                    float y2 = (float)(cy + Math.Sin(angle) * rayEnd);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }

        public static void DrawRefreshIcon(Graphics g, Rectangle rect, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = Math.Min(rect.Width, rect.Height);
            int inset = (int)Math.Round(size * 0.20f);
            Rectangle arcRect = new Rectangle(rect.X + inset, rect.Y + inset, Math.Max(4, size - inset * 2), Math.Max(4, size - inset * 2));

            using (Pen pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arcRect, 45, 270);
            }

            float angleRad = (float)(45 * Math.PI / 180.0);
            float cx = arcRect.X + arcRect.Width / 2.0f;
            float cy = arcRect.Y + arcRect.Height / 2.0f;
            float rx = arcRect.Width / 2.0f;
            float ry = arcRect.Height / 2.0f;
            float tipX = cx + (float)(Math.Cos(angleRad) * rx);
            float tipY = cy + (float)(Math.Sin(angleRad) * ry);

            PointF[] arrow = new PointF[] {
                new PointF(tipX, tipY),
                new PointF(tipX + 3.5f, tipY - 1f),
                new PointF(tipX + 1f, tipY - 3.5f)
            };
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, arrow);
            }
        }
    }

    internal sealed class FluentSlider : Control
    {
        private int minimum = 0;
        private int maximum = 100;
        private int val = 0;
        private bool isDragging = false;
        private bool isHovered = false;
        private bool isFocused = false;
        private readonly Theme theme;
        private readonly float dpiScale;

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return minimum; }
            set { minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get { return maximum; }
            set { maximum = value; Invalidate(); }
        }

        public int Value
        {
            get { return val; }
            set
            {
                int clamped = Math.Max(minimum, Math.Min(maximum, value));
                if (val != clamped)
                {
                    val = clamped;
                    AccessibleDescription = val + "%";
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        public int SmallChange { get; set; }
        public int LargeChange { get; set; }

        public FluentSlider(Theme theme, float dpiScale)
        {
            this.theme = theme;
            this.dpiScale = dpiScale;
            SmallChange = 1;
            LargeChange = 5;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
            Height = (int)Math.Round(32 * dpiScale);
            Cursor = Cursors.Hand;
        }

        private Rectangle GetTrackBounds()
        {
            int thumbRadius = (int)Math.Round(9 * dpiScale);
            int trackHeight = Math.Max(4, (int)Math.Round(5.5f * dpiScale));
            int left = thumbRadius + 1;
            int right = Width - thumbRadius - 1;
            int top = (Height - trackHeight) / 2;
            return new Rectangle(left, top, Math.Max(10, right - left), trackHeight);
        }

        private int ValueToPixel(int v, Rectangle track)
        {
            if (maximum <= minimum) return track.Left;
            float percent = (float)(v - minimum) / (maximum - minimum);
            return track.Left + (int)Math.Round(percent * track.Width);
        }

        private int PixelToValue(int x, Rectangle track)
        {
            if (track.Width <= 0) return minimum;
            float percent = (float)(x - track.Left) / track.Width;
            percent = Math.Max(0.0f, Math.Min(1.0f, percent));
            return minimum + (int)Math.Round(percent * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                isDragging = true;
                Capture = true;
                Rectangle track = GetTrackBounds();
                Value = PixelToValue(e.X, track);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (isDragging)
            {
                Rectangle track = GetTrackBounds();
                Value = PixelToValue(e.X, track);
            }
            else
            {
                if (!isHovered)
                {
                    isHovered = true;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (isDragging)
            {
                isDragging = false;
                Capture = false;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (isHovered)
            {
                isHovered = false;
                Invalidate();
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            isFocused = true;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            isFocused = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = e.Delta > 0 ? 2 : -2;
            Value = Math.Max(minimum, Math.Min(maximum, val + step));
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Down)
            {
                Value = Math.Max(minimum, val - SmallChange);
                return true;
            }
            if (keyData == Keys.Right || keyData == Keys.Up)
            {
                Value = Math.Min(maximum, val + SmallChange);
                return true;
            }
            if (keyData == Keys.PageDown)
            {
                Value = Math.Max(minimum, val - LargeChange);
                return true;
            }
            if (keyData == Keys.PageUp)
            {
                Value = Math.Min(maximum, val + LargeChange);
                return true;
            }
            if (keyData == Keys.Home)
            {
                Value = minimum;
                return true;
            }
            if (keyData == Keys.End)
            {
                Value = maximum;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = GetTrackBounds();
            int trackRadius = track.Height / 2;
            int thumbX = ValueToPixel(val, track);
            int centerY = Height / 2;

            using (GraphicsPath trackPath = GraphicsHelper.CreateRoundedRectangle(track, trackRadius))
            using (SolidBrush trackBrush = new SolidBrush(theme.TrackBackground))
            {
                g.FillPath(trackBrush, trackPath);
            }

            if (thumbX > track.Left)
            {
                int fillWidth = thumbX - track.Left;
                Rectangle activeRect = new Rectangle(track.Left, track.Top, fillWidth, track.Height);
                using (GraphicsPath activePath = GraphicsHelper.CreateRoundedRectangle(activeRect, trackRadius))
                using (SolidBrush activeBrush = new SolidBrush(isHovered ? theme.AccentHover : theme.Accent))
                {
                    g.FillPath(activeBrush, activePath);
                }
            }

            int thumbDiameter = (int)Math.Round((isDragging ? 18 : (isHovered ? 20 : 18)) * dpiScale);
            int thumbRadius = thumbDiameter / 2;
            Rectangle thumbRect = new Rectangle(thumbX - thumbRadius, centerY - thumbRadius, thumbDiameter, thumbDiameter);

            if (isFocused)
            {
                int focusDiameter = thumbDiameter + (int)Math.Round(5 * dpiScale);
                int focusRadius = focusDiameter / 2;
                Rectangle focusRect = new Rectangle(thumbX - focusRadius, centerY - focusRadius, focusDiameter, focusDiameter);
                using (Pen focusPen = new Pen(theme.Accent, 1.5f))
                {
                    g.DrawEllipse(focusPen, focusRect);
                }
            }

            using (Pen outerPen = new Pen(theme.ThumbBorder, 1.2f))
            using (SolidBrush thumbBgBrush = new SolidBrush(theme.ThumbColor))
            {
                g.FillEllipse(thumbBgBrush, thumbRect);
                g.DrawEllipse(outerPen, thumbRect);
            }

            int dotDiameter = (int)Math.Round((isDragging ? 10 : (isHovered ? 8 : 6)) * dpiScale);
            int dotRadius = dotDiameter / 2;
            Rectangle dotRect = new Rectangle(thumbX - dotRadius, centerY - dotRadius, dotDiameter, dotDiameter);
            using (SolidBrush dotBrush = new SolidBrush(theme.Accent))
            {
                g.FillEllipse(dotBrush, dotRect);
            }
        }
    }

    internal sealed class IconButton : Control
    {
        public enum ButtonType { Refresh }

        private readonly ButtonType type;
        private readonly Theme theme;
        private readonly float dpiScale;
        private bool isHovered = false;
        private bool isPressed = false;

        public IconButton(ButtonType type, Theme theme, float dpiScale)
        {
            this.type = type;
            this.theme = theme;
            this.dpiScale = dpiScale;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            int btnSize = (int)Math.Round(22 * dpiScale);
            Size = new Size(btnSize, btnSize);
            AccessibleName = "Atualizar monitores";
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); isHovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); isHovered = false; isPressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { isPressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); isPressed = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = (int)Math.Round(4 * dpiScale);

            if (isPressed)
            {
                using (GraphicsPath path = GraphicsHelper.CreateRoundedRectangle(rect, radius))
                using (SolidBrush brush = new SolidBrush(theme.ButtonPressed))
                    g.FillPath(brush, path);
            }
            else if (isHovered)
            {
                using (GraphicsPath path = GraphicsHelper.CreateRoundedRectangle(rect, radius))
                using (SolidBrush brush = new SolidBrush(theme.ButtonHover))
                    g.FillPath(brush, path);
            }

            Color iconColor = isHovered ? theme.TextPrimary : theme.TextSecondary;
            GraphicsHelper.DrawRefreshIcon(g, rect, iconColor);
        }
    }

    internal sealed class MonitorRow : Panel
    {
        private readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 180 };
        private readonly FluentSlider slider;
        private readonly Label percentLabel;
        private readonly Panel sunBox;
        private bool applying;
        private bool synchronizingDashboard;
        private bool cancelled;
        private int lastNonZeroValue = 50;
        private readonly MonitorService service;
        private readonly Display display;
        private readonly Action<string> statusCallback;
        private readonly Theme theme;
        private readonly float dpiScale;

        public FluentSlider Slider { get { return slider; } }

        public void SynchronizeDashboardValue()
        {
            if (IsDisposed || cancelled) return;
            synchronizingDashboard = true;
            try { debounce.Stop(); slider.Value = display.Percent; }
            finally { synchronizingDashboard = false; }
        }

        public MonitorRow(MonitorService service, Display display, int index, bool showLabel, Action<string> status, Theme theme, float dpiScale, int rowWidth)
        {
            this.service = service;
            this.display = display;
            this.statusCallback = status;
            this.theme = theme;
            this.dpiScale = dpiScale;
            this.lastNonZeroValue = display.Percent > 0 ? display.Percent : 50;

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Width = rowWidth;

            int rowHeight = (int)Math.Round((showLabel ? 54 : 34) * dpiScale);
            Height = rowHeight;
            Margin = new Padding(0, 0, 0, (int)Math.Round((showLabel ? 8 : 4) * dpiScale));

            int sliderTop = 0;
            if (showLabel)
            {
                string friendlyName = GetFriendlyName(display, index);
                Label nameLabel = new Label {
                    Text = friendlyName,
                    AutoEllipsis = true,
                    Location = new Point(0, 0),
                    Size = new Size(rowWidth, (int)Math.Round(16 * dpiScale)),
                    Font = FontHelper.CreateFont(8.5f, FontStyle.Regular),
                    ForeColor = theme.TextSecondary,
                    BackColor = Color.Transparent
                };
                Controls.Add(nameLabel);
                sliderTop = (int)Math.Round(20 * dpiScale);
            }

            int sunSize = (int)Math.Round(20 * dpiScale);
            sunBox = new Panel {
                Location = new Point(0, sliderTop + (int)Math.Round(6 * dpiScale)),
                Size = new Size(sunSize, sunSize),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            sunBox.Paint += delegate(object s, PaintEventArgs e) {
                Color sunColor = display.Percent > 0 ? Color.FromArgb(252, 211, 77) : theme.TextMuted;
                GraphicsHelper.DrawSunIcon(e.Graphics, new Rectangle(0, 0, sunSize, sunSize), sunColor);
            };
            sunBox.Click += delegate {
                if (slider.Value > 0)
                {
                    lastNonZeroValue = slider.Value;
                    slider.Value = 0;
                }
                else
                {
                    slider.Value = lastNonZeroValue;
                }
            };

            int percentWidth = (int)Math.Round(38 * dpiScale);
            percentLabel = new Label {
                Text = display.Percent + "%",
                Location = new Point(rowWidth - percentWidth, sliderTop + (int)Math.Round(6 * dpiScale)),
                Size = new Size(percentWidth, (int)Math.Round(20 * dpiScale)),
                Font = FontHelper.CreateFont(9.5f, FontStyle.Bold),
                ForeColor = theme.TextPrimary,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent
            };

            int sliderLeft = sunBox.Right + (int)Math.Round(6 * dpiScale);
            int sliderRight = percentLabel.Left - (int)Math.Round(6 * dpiScale);
            slider = new FluentSlider(theme, dpiScale) {
                Minimum = 0,
                Maximum = 100,
                Value = display.Percent,
                Location = new Point(sliderLeft, sliderTop),
                Width = Math.Max(50, sliderRight - sliderLeft),
                AccessibleName = "Brilho de " + display.Name
            };

            slider.ValueChanged += delegate {
                percentLabel.Text = slider.Value + "%";
                sunBox.Invalidate();
                if (synchronizingDashboard) return;
                debounce.Stop();
                debounce.Start();
            };

            slider.MouseUp += async delegate {
                debounce.Stop();
                await Apply();
            };

            debounce.Tick += async delegate {
                debounce.Stop();
                await Apply();
            };

            Controls.Add(sunBox);
            Controls.Add(slider);
            Controls.Add(percentLabel);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = e.Delta > 0 ? 2 : -2;
            slider.Value = Math.Max(0, Math.Min(100, slider.Value + step));
        }

        private static string GetFriendlyName(Display display, int index)
        {
            if (string.IsNullOrEmpty(display.Name) ||
                display.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                display.Name.IndexOf("PnP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Monitor " + (index + 1);
            }
            return display.Name;
        }

        public void CancelPending()
        {
            cancelled = true;
            debounce.Stop();
        }

        private async Task Apply()
        {
            if (applying || cancelled) return;
            applying = true;
            try
            {
                int val = slider.Value;
                await Task.Run(() => service.Set(display, val));
            }
            catch (Exception error)
            {
                statusCallback("Erro: " + error.Message);
            }
            finally
            {
                applying = false;
            }
        }
    }

    internal sealed class EmptyStateView : Panel
    {
        public EmptyStateView(Theme theme, float dpiScale, int cardWidth, Action onRetry)
        {
            Width = cardWidth;
            Height = (int)Math.Round(72 * dpiScale);
            BackColor = Color.Transparent;

            Label msg = new Label {
                Text = "Nenhum monitor DDC/CI encontrado.",
                Location = new Point(0, (int)Math.Round(8 * dpiScale)),
                Size = new Size(cardWidth, (int)Math.Round(22 * dpiScale)),
                Font = FontHelper.CreateFont(8.8f, FontStyle.Regular),
                ForeColor = theme.TextSecondary,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };

            Label retry = new Label {
                Text = "Clique aqui para buscar novamente",
                Location = new Point(0, msg.Bottom),
                Size = new Size(cardWidth, (int)Math.Round(22 * dpiScale)),
                Font = FontHelper.CreateFont(8.5f, FontStyle.Underline),
                ForeColor = theme.Accent,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            retry.Click += delegate { if (onRetry != null) onRetry(); };

            Controls.Add(msg);
            Controls.Add(retry);
        }
    }

    internal sealed class FluentColorTable : ProfessionalColorTable
    {
        private readonly Theme theme;
        public FluentColorTable(Theme theme) { this.theme = theme; }
        public override Color ToolStripDropDownBackground { get { return theme.Background; } }
        public override Color MenuBorder { get { return theme.FlyoutBorder; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return theme.ButtonHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return theme.ButtonHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return theme.ButtonHover; } }
        public override Color MenuItemPressedGradientBegin { get { return theme.ButtonPressed; } }
        public override Color MenuItemPressedGradientEnd { get { return theme.ButtonPressed; } }
        public override Color ImageMarginGradientBegin { get { return theme.Background; } }
        public override Color ImageMarginGradientMiddle { get { return theme.Background; } }
        public override Color ImageMarginGradientEnd { get { return theme.Background; } }
        public override Color CheckBackground { get { return theme.Accent; } }
        public override Color CheckSelectedBackground { get { return theme.AccentHover; } }
        public override Color CheckPressedBackground { get { return theme.Accent; } }
        public override Color SeparatorDark { get { return theme.IsDark ? Color.FromArgb(58, 64, 78) : Color.FromArgb(220, 220, 220); } }
        public override Color SeparatorLight { get { return Color.Transparent; } }
    }

    internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Theme theme;
        public FluentMenuRenderer(Theme theme) : base(new FluentColorTable(theme)) { this.theme = theme; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = theme.TextPrimary;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                Rectangle rc = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                using (GraphicsPath path = GraphicsHelper.CreateRoundedRectangle(rc, 4))
                using (SolidBrush brush = new SolidBrush(e.Item.Pressed ? theme.ButtonPressed : theme.ButtonHover))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(brush, path);
                }
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }
    }

    internal static class MouseHook
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        private static readonly LowLevelMouseProc HookProcedure = HookCallback;
        private static IntPtr hookId = IntPtr.Zero;
        private static Form targetForm;
        private static Action<Point> onOutsideClick;

        public static void Install(Form form, Action<Point> callback)
        {
            if (hookId != IntPtr.Zero) Uninstall();
            targetForm = form;
            onOutsideClick = callback;
            try
            {
                using (System.Diagnostics.Process curProcess = System.Diagnostics.Process.GetCurrentProcess())
                using (System.Diagnostics.ProcessModule curModule = curProcess.MainModule)
                {
                    IntPtr hMod = Native.GetModuleHandle(curModule.ModuleName);
                    hookId = SetWindowsHookEx(WH_MOUSE_LL, HookProcedure, hMod, 0);
                }
            }
            catch { }
        }

        public static void Uninstall()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
            targetForm = null;
            onOutsideClick = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && targetForm != null && !targetForm.IsDisposed && targetForm.Visible)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN ||
                    msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)
                {
                    MSLLHOOKSTRUCT hook = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point pt = new Point(hook.pt.x, hook.pt.y);
                    if (!targetForm.Bounds.Contains(pt))
                    {
                        Action<Point> cb = onOutsideClick;
                        if (cb != null)
                        {
                            try { targetForm.BeginInvoke(new Action(() => cb(pt))); }
                            catch { }
                        }
                    }
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }
    }

    internal sealed class ModernFlyoutForm : Form
    {
        private readonly Theme theme;
        private readonly float dpiScale;
        public Action<int> OnWheelDelta;

        public ModernFlyoutForm(Theme theme, float dpiScale)
        {
            this.theme = theme;
            this.dpiScale = dpiScale;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DwmHelper.ApplyModernWindowAttributes(Handle, theme.IsDark);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (OnWheelDelta != null) OnWheelDelta(e.Delta);
        }

        public void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            int radius = (int)Math.Round(12 * dpiScale);
            using (GraphicsPath path = GraphicsHelper.CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), radius))
            {
                this.Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Borda branca removida - visual flutuante limpo e suave
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly MonitorService service = new MonitorService();
        private readonly NotifyIcon tray;
        private readonly ModernFlyoutForm popup;
        private readonly FlowLayoutPanel rows;
        private readonly IconButton refresh;
        private readonly Icon icon;
        private bool refreshing;
        private DashboardBridge dashboardBridge;
        private bool exiting;
        private bool keepOpen;
        private DateTime lastOutsideClick = DateTime.MinValue;
        private readonly Theme theme;
        private readonly float dpiScale;
        private readonly int formLogicalWidth = 320;
        private readonly int outerPadLogical = 16;
        private readonly EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BrightTray.Show");
        private readonly System.Windows.Forms.Timer signalTimer = new System.Windows.Forms.Timer { Interval = 250 };

        public TrayContext(bool show, bool keepOpen)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " TrayContext ctor start, show=" + show + ", keepOpen=" + keepOpen + Environment.NewLine);

            this.keepOpen = keepOpen;
            this.theme = Theme.Current();
            icon = MakeIcon();

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                dpiScale = g.DpiX / 96.0f;
            }

            int formWidth = (int)Math.Round(formLogicalWidth * dpiScale);
            int outerPad = (int)Math.Round(outerPadLogical * dpiScale);

            popup = new ModernFlyoutForm(theme, dpiScale) {
                Text = "Brilho", Icon = icon, FormBorderStyle = FormBorderStyle.None,
                MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = keepOpen, StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(formWidth, (int)Math.Round(88 * dpiScale)),
                BackColor = theme.Background,
                ForeColor = theme.TextPrimary,
                Font = FontHelper.CreateFont(9.5f, FontStyle.Regular),
                TopMost = true,
                KeyPreview = true,
                AutoScaleMode = AutoScaleMode.None
            };

            popup.OnWheelDelta = delta => {
                if (rows.Controls.Count > 0)
                {
                    MonitorRow row = rows.Controls[0] as MonitorRow;
                    if (row != null)
                    {
                        int step = delta > 0 ? 2 : -2;
                        row.Slider.Value = Math.Max(0, Math.Min(100, row.Slider.Value + step));
                    }
                }
            };

            int topBarHeight = (int)Math.Round(26 * dpiScale);
            Panel topBar = new Panel {
                Dock = DockStyle.Top,
                Height = topBarHeight,
                BackColor = Color.Transparent,
                Padding = new Padding(outerPad, (int)Math.Round(12 * dpiScale), outerPad, 0)
            };
            topBar.MouseWheel += delegate(object s, MouseEventArgs e) { popup.OnWheelDelta(e.Delta); };

            Label title = new Label {
                Text = "Brilho",
                Location = new Point(outerPad, (int)Math.Round(10 * dpiScale)),
                Size = new Size((int)Math.Round(120 * dpiScale), (int)Math.Round(20 * dpiScale)),
                Font = FontHelper.CreateFont(10.0f, FontStyle.Bold),
                ForeColor = theme.TextPrimary,
                BackColor = Color.Transparent
            };
            title.MouseWheel += delegate(object s, MouseEventArgs e) { popup.OnWheelDelta(e.Delta); };

            refresh = new IconButton(IconButton.ButtonType.Refresh, theme, dpiScale);
            refresh.Location = new Point(formWidth - outerPad - refresh.Width, (int)Math.Round(8 * dpiScale));
            refresh.Click += async delegate { await RefreshDisplays(); };

            topBar.Controls.Add(title);
            topBar.Controls.Add(refresh);

            rows = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                BackColor = Color.Transparent,
                Padding = new Padding(outerPad, (int)Math.Round(4 * dpiScale), outerPad, (int)Math.Round(10 * dpiScale))
            };
            rows.MouseWheel += delegate(object s, MouseEventArgs e) { popup.OnWheelDelta(e.Delta); };

            popup.Controls.Add(rows);
            popup.Controls.Add(topBar);

            popup.Deactivate += delegate {
                if (!this.keepOpen)
                {
                    lastOutsideClick = DateTime.UtcNow;
                    popup.Hide();
                }
            };
            popup.FormClosing += delegate(object sender, FormClosingEventArgs args) {
                if (!exiting) { args.Cancel = true; popup.Hide(); }
            };
            popup.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Escape) popup.Hide(); };

            popup.VisibleChanged += delegate {
                if (popup.Visible)
                {
                    if (!this.keepOpen)
                        MouseHook.Install(popup, OnOutsideClick);
                }
                else
                {
                    MouseHook.Uninstall();
                }
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new FluentMenuRenderer(theme);
            menu.Font = FontHelper.CreateFont(9.0f, FontStyle.Regular);
            menu.Items.Add("Abrir controle de brilho", null, delegate { ShowPopup(); });
            menu.Items.Add("Atualizar monitores", null, async delegate { await RefreshDisplays(); });

            ToolStripMenuItem startup = new ToolStripMenuItem("Iniciar com o Windows") { Checked = StartupEnabled(), CheckOnClick = false };
            startup.Click += delegate {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        if (startup.Checked) key.DeleteValue("BrightTray", false);
                        else key.SetValue("BrightTray", "\"" + Application.ExecutablePath + "\" --background");
                    }
                    startup.Checked = StartupEnabled();
                }
                catch (Exception error) { MessageBox.Show(error.Message, "Nao foi possivel alterar a inicializacao"); }
            };
            menu.Items.Add(startup);

            ToolStripMenuItem pin = new ToolStripMenuItem("Manter painel aberto") { Checked = keepOpen, CheckOnClick = true };
            pin.CheckedChanged += delegate {
                this.keepOpen = pin.Checked;
                popup.ShowInTaskbar = pin.Checked;
                if (!pin.Checked && popup.Visible)
                    MouseHook.Install(popup, OnOutsideClick);
                else if (pin.Checked)
                    MouseHook.Uninstall();
            };
            menu.Items.Add(pin);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { ExitThread(); });

            tray = new NotifyIcon { Text = "BrightTray - brilho dos monitores", Icon = icon, ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate(object sender, MouseEventArgs args) {
                if (args.Button == MouseButtons.Left)
                {
                    if ((DateTime.UtcNow - lastOutsideClick).TotalMilliseconds < 350)
                        return;
                    if (popup.Visible) popup.Hide();
                    else ShowPopup();
                }
            };

            IntPtr windowHandle = popup.Handle;
            dashboardBridge = new DashboardBridge(service, delegate
            {
                if (exiting || popup.IsDisposed) return;
                try { popup.BeginInvoke(new Action(delegate
                {
                    if (exiting) return;
                    foreach (Control control in rows.Controls)
                    {
                        MonitorRow row = control as MonitorRow;
                        if (row != null) row.SynchronizeDashboardValue();
                    }
                })); } catch (InvalidOperationException) { }
            });
            signalTimer.Tick += delegate { if (showSignal.WaitOne(0)) ShowPopup(); };
            signalTimer.Start();

            System.Windows.Forms.Timer initTimer = new System.Windows.Forms.Timer { Interval = 20 };
            initTimer.Tick += async delegate {
                initTimer.Stop();
                initTimer.Dispose();
                await RefreshDisplays();
                if (show && !exiting) ShowPopup();
            };
            initTimer.Start();

            File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " TrayContext ctor finished" + Environment.NewLine);
        }

        private void OnOutsideClick(Point pt)
        {
            lastOutsideClick = DateTime.UtcNow;
            popup.Hide();
        }

        private static bool StartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("BrightTray") != null;
        }

        private void ShowPopup()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int margin = (int)Math.Round(12 * dpiScale);

            int x = Cursor.Position.X - popup.Width / 2;
            if (x + popup.Width > area.Right - margin) x = area.Right - popup.Width - margin;
            if (x < area.Left + margin) x = area.Left + margin;

            int y = area.Bottom - popup.Height - margin;
            if (y < area.Top + margin) y = area.Top + margin;

            popup.Location = new Point(x, y);
            popup.Show();
            Native.SetForegroundWindow(popup.Handle);
            popup.Activate();
        }

        private async Task RefreshDisplays()
        {
            if (refreshing || exiting) return;
            refreshing = true;
            refresh.Enabled = false;
            rows.Enabled = false;

            foreach (Control control in rows.Controls)
            {
                MonitorRow row = control as MonitorRow;
                if (row != null) row.CancelPending();
            }

            try
            {
                List<Display> displays = await Task.Run(() => service.Discover());
                if (exiting) return;

                while (rows.Controls.Count > 0)
                {
                    Control control = rows.Controls[0];
                    rows.Controls.Remove(control);
                    control.Dispose();
                }

                int outerPad = (int)Math.Round(outerPadLogical * dpiScale);
                int rowWidth = popup.ClientSize.Width - outerPad * 2;
                bool showLabels = displays.Count > 1;

                if (displays.Count == 0)
                {
                    rows.Controls.Add(new EmptyStateView(theme, dpiScale, rowWidth, async () => await RefreshDisplays()));
                }
                else
                {
                    for (int i = 0; i < displays.Count; i++)
                    {
                        rows.Controls.Add(new MonitorRow(service, displays[i], i, showLabels, SetStatus, theme, dpiScale, rowWidth));
                    }
                }

                int topBarHeight = (int)Math.Round(26 * dpiScale);
                int rowsPaddingV = (int)Math.Round(14 * dpiScale);
                int contentHeight = 0;

                foreach (Control c in rows.Controls)
                {
                    contentHeight += c.Height + c.Margin.Bottom;
                }

                int totalHeight = topBarHeight + rowsPaddingV + contentHeight;
                Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                int maxHeight = workArea.Height - (int)Math.Round(32 * dpiScale);
                totalHeight = Math.Min(totalHeight, maxHeight);

                popup.ClientSize = new Size(popup.ClientSize.Width, totalHeight);
                popup.UpdateRegion();

                if (popup.Visible) ShowPopup();
            }
            catch (Exception error)
            {
                SetStatus("Falha ao buscar: " + error.Message);
            }
            finally
            {
                if (!exiting)
                {
                    rows.Enabled = true;
                    refresh.Enabled = true;
                }
                refreshing = false;
            }
        }

        private void SetStatus(string message) { }

        protected override void ExitThreadCore()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " ExitThreadCore called" + Environment.NewLine);

            exiting = true;
            if (dashboardBridge != null) dashboardBridge.Dispose();
            MouseHook.Uninstall();
            signalTimer.Dispose();
            showSignal.Dispose();
            tray.Visible = false;
            if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            popup.Dispose();
            service.Dispose();
            icon.Dispose();
            base.ExitThreadCore();
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                GraphicsHelper.DrawSunIcon(graphics, new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 206, 85));
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon temporary = Icon.FromHandle(handle)) return (Icon)temporary.Clone(); }
                finally { Native.DestroyIcon(handle); }
            }
        }
    }

    internal static class DwmHelper
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyModernWindowAttributes(IntPtr hwnd, bool isDark)
        {
            try
            {
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref darkMode, sizeof(int));

                int noBorder = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

                int backdrop = 3; // DWMSBT_TRANSIENTWINDOW (Acrylic)
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
            catch { }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }
        internal delegate bool MonitorEnum(IntPtr monitor, IntPtr device, ref Rect rectangle, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorEnum callback, IntPtr data);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] internal static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YAHCW
{
    public class TrayAppContext : ApplicationContext
    {
        private const int HOTKEY_ID = 9001;

        private readonly Config _config;
        private readonly HotCornerDetector _detector;
        private readonly RunningGameCache _gameCache;
        private readonly Timer _adaptiveTimer;
        private readonly Timer _gameScanTimer;
        private readonly Timer _requestTimer;
        private Screen _requestedScreen;
        private long _requestDeadline;
        private IntPtr _requestForeground;
        private int _requestStableTicks;
        private ToolStripMenuItem _menuItemStatus;
        private string _lastStatusText;

        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _contextMenu;
        private ToolStripMenuItem _menuItemPause;
        private ToolStripMenuItem _menuItemBlockFg;
        private OverviewForm _currentOverview;
        private HotkeyMessageWindow _msgWindow;
        private bool _isDisposed = false;

        private IntPtr _lastGenuineForegroundHwnd = IntPtr.Zero;
        private string _lastGenuineForegroundProcessName = null;
        private IntPtr _lastObservedForeground;

        private class HotkeyMessageWindow : NativeWindow, IDisposable
        {
            private const int WM_HOTKEY = 0x0312;
            private const int HWND_MESSAGE = -3;
            private readonly uint _showOverviewMsg;
            private readonly TrayAppContext _owner;

            public uint ShowOverviewMsg { get { return _showOverviewMsg; } }

            public HotkeyMessageWindow(TrayAppContext owner)
            {
                _owner = owner;
                _showOverviewMsg = NativeMethods.RegisterWindowMessage("YAHCW_SHOW_OVERVIEW_MSG");

                CreateParams cp = new CreateParams();
                cp.Parent = (IntPtr)HWND_MESSAGE;
                cp.Caption = "YAHCW_TrayMessageReceiver";
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                {
                    _owner.HandleHotkeyTriggered();
                    m.Result = IntPtr.Zero;
                    return;
                }

                if (_showOverviewMsg != 0 && (uint)m.Msg == _showOverviewMsg)
                {
                    _owner.HandleExternalOverviewRequest();
                    m.Result = (IntPtr)1;
                    return;
                }

                base.WndProc(ref m);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }

        public TrayAppContext(Config config) : this(config, false)
        {
        }

        public TrayAppContext(Config config, bool startWithOverview)
        {
            _config = config ?? Config.Load();
            _detector = new HotCornerDetector();
            _gameCache = new RunningGameCache();
            _requestTimer = new Timer { Interval = 25 };
            _requestTimer.Tick += RequestTimer_Tick;

            _msgWindow = new HotkeyMessageWindow(this);

            InitializeTray();
            RegisterGlobalHotkey();

            _adaptiveTimer = new Timer();
            _adaptiveTimer.Interval = 100;
            _adaptiveTimer.Tick += AdaptiveTimer_Tick;
            _adaptiveTimer.Start();

            _gameScanTimer = new Timer();
            _gameScanTimer.Interval = Math.Max(2000, _config.CheckRunningGamesIntervalMs);
            _gameScanTimer.Tick += GameScanTimer_Tick;
            _gameScanTimer.Start();

            if (startWithOverview)
            {
                POINT pt;
                NativeMethods.GetCursorPos(out pt);
                RequestOverview(Screen.FromPoint(pt.ToPoint()));
            }
        }

        private void InitializeTray()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.RenderMode = ToolStripRenderMode.System;

            ToolStripMenuItem itemTitle = new ToolStripMenuItem("YAHCW \u2022 Hot Corners v1.0");
            itemTitle.Enabled = false;
            itemTitle.Font = new Font(itemTitle.Font, FontStyle.Bold);

            ToolStripMenuItem itemOpen = new ToolStripMenuItem("Abrir Vis\u00e3o Geral (Ctrl+Alt+Espa\u00e7o)", null, delegate
            {
                POINT pt;
                NativeMethods.GetCursorPos(out pt);
                RequestOverview(Screen.FromPoint(pt.ToPoint()));
            });

            _menuItemPause = new ToolStripMenuItem("Pausar Hot Corners", null, TogglePause);
            _menuItemPause.Checked = _config.IsPaused;

            _menuItemBlockFg = new ToolStripMenuItem("Bloquear Processo Atual", null, BlockCurrentForegroundProcess);
            ToolStripMenuItem itemConfig = new ToolStripMenuItem("Configura\u00e7\u00f5es (yahcw.config.json)", null, OpenConfigFile);
            ToolStripMenuItem itemExit = new ToolStripMenuItem("Sair", null, delegate { ExitThread(); });

            _contextMenu.Items.Add(itemTitle);
            _menuItemStatus = new ToolStripMenuItem("Pronto para ativar") { Enabled = false };
            _contextMenu.Items.Add(_menuItemStatus);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(itemOpen);
            _contextMenu.Items.Add(_menuItemPause);
            _contextMenu.Items.Add(_menuItemBlockFg);
            _contextMenu.Items.Add(itemConfig);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(itemExit);

            _contextMenu.Opening += delegate
            {
                UpdateGenuineForegroundWindow();
                if (!string.IsNullOrEmpty(_lastGenuineForegroundProcessName))
                {
                    _menuItemBlockFg.Text = string.Format("Bloquear Processo Atual ({0})", _lastGenuineForegroundProcessName);
                    _menuItemBlockFg.Enabled = true;
                }
                else
                {
                    _menuItemBlockFg.Text = "Bloquear Processo Atual";
                    _menuItemBlockFg.Enabled = false;
                }
            };

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "YAHCW - Yet Another Hot Corners for Windows";
            _notifyIcon.Icon = CreateTrayIcon();
            _notifyIcon.ContextMenuStrip = _contextMenu;
            _notifyIcon.Visible = true;

            _notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    RequestOverview(Screen.FromPoint(Cursor.Position));
                }
            };
        }

        private Icon CreateTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Dark tile
                using (SolidBrush bBg = new SolidBrush(Color.FromArgb(32, 32, 36)))
                {
                    g.FillRectangle(bBg, 2, 2, 28, 28);
                }

                // Grid windows layout
                using (SolidBrush bWin = new SolidBrush(Color.FromArgb(80, 80, 90)))
                {
                    g.FillRectangle(bWin, 5, 5, 10, 9);
                    g.FillRectangle(bWin, 17, 5, 10, 9);
                    g.FillRectangle(bWin, 5, 17, 10, 9);
                    g.FillRectangle(bWin, 17, 17, 10, 9);
                }

                // Hot corner accent in upper-left
                using (SolidBrush bAccent = new SolidBrush(Color.FromArgb(0, 160, 255)))
                {
                    g.FillPolygon(bAccent, new Point[]
                    {
                        new Point(2, 2),
                        new Point(12, 2),
                        new Point(2, 12)
                    });
                }

                IntPtr hIcon = bmp.GetHicon();
                Icon ico = Icon.FromHandle(hIcon);
                Icon cloned = (Icon)ico.Clone();
                NativeMethods.DestroyIcon(hIcon);
                return cloned;
            }
        }

        private void RegisterGlobalHotkey()
        {
            if (!_config.HotkeyEnabled || _msgWindow == null || _msgWindow.Handle == IntPtr.Zero)
                return;

            uint mod = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
            uint vk = (uint)Keys.Space;

            bool registered = NativeMethods.RegisterHotKey(_msgWindow.Handle, HOTKEY_ID, mod, vk);
            if (!registered)
            {
                int err = Marshal.GetLastWin32Error();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(
                        3000,
                        "YAHCW - Aviso de Atalho",
                        string.Format("N\u00e3o foi poss\u00edvel registrar o atalho global {0} + {1} (erro Win32 {2}). O atalho pode estar em uso.",
                            _config.HotkeyModifiers, _config.HotkeyKey, err),
                        ToolTipIcon.Warning);
                }
            }
        }

        private void UnregisterGlobalHotkey()
        {
            if (_msgWindow != null && _msgWindow.Handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID);
            }
        }

        private void HandleHotkeyTriggered()
        {
            POINT pt;
            NativeMethods.GetCursorPos(out pt);
            Screen target = Screen.FromPoint(pt.ToPoint());
            RequestOverview(target);
        }

        private void HandleExternalOverviewRequest()
        {
            POINT pt;
            NativeMethods.GetCursorPos(out pt);
            Screen target = Screen.FromPoint(pt.ToPoint());
            RequestOverview(target);
        }

        private void UpdateGenuineForegroundWindow()
        {
            IntPtr hFg = NativeMethods.GetForegroundWindow();
            if (hFg == IntPtr.Zero) return;
            if (hFg == _lastObservedForeground) return;
            _lastObservedForeground = hFg;

            if (_currentOverview != null && !_currentOverview.IsDisposed && hFg == _currentOverview.Handle)
            {
                return;
            }

            uint pid;
            NativeMethods.GetWindowThreadProcessId(hFg, out pid);
            if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id)
            {
                return;
            }

            IntPtr hShell = NativeMethods.GetShellWindow();
            IntPtr hDesktop = NativeMethods.GetDesktopWindow();
            if (hFg == hShell || hFg == hDesktop)
            {
                return;
            }

            StringBuilder sbClass = new StringBuilder(256);
            NativeMethods.GetClassName(hFg, sbClass, 256);
            string cls = sbClass.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                return;
            }

            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    string pName = p.ProcessName;
                    if (string.Equals(pName, "explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _lastGenuineForegroundHwnd = hFg;
                    _lastGenuineForegroundProcessName = pName;
                }
            }
            catch
            {
                // Ignore inaccessible process
            }
        }

        private void AdaptiveTimer_Tick(object sender, EventArgs e)
        {
            UpdateGenuineForegroundWindow();

            if (_currentOverview != null && !_currentOverview.IsDisposed && _currentOverview.Visible)
            {
                return;
            }

            POINT pt;
            if (!NativeMethods.GetCursorPos(out pt)) return;

            Point cursor = pt.ToPoint();
            Screen screen = Screen.FromPoint(cursor);
            long now = MonotonicTime.NowMs;

            IntPtr hFg = NativeMethods.GetForegroundWindow();
            uint fgPid = 0;
            if (hFg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hFg, out fgPid);
            }

            // Check guards
            Point corner = HotCornerDetector.GetActivationCorner(screen, _config, cursor);
            bool nearCorner = Math.Abs(cursor.X - corner.X) <= _config.RearmDistancePx && Math.Abs(cursor.Y - corner.Y) <= _config.RearmDistancePx;
            GuardResult guard = !nearCorner ? GuardResult.Allow() : SafetyGuards.EvaluateAllGuards(
                screen,
                _config,
                _config.IsPaused,
                _currentOverview != null ? _currentOverview.Handle : IntPtr.Zero,
                _gameCache,
                forceFreshGameScan: false);

            HotCornerAction action = _detector.ProcessTick(cursor, now, screen, _config, guard.IsAllowed, hFg, fgPid);

            if (!guard.IsAllowed && HotCornerDetector.IsInTriggerZone(cursor, corner, _config.TriggerSizePx))
                SetStatus("Bloqueado: " + guard.Reason);

            if (action.ActionType == CornerActionType.IntervalChanged)
            {
                if (_adaptiveTimer.Interval != action.RecommendedIntervalMs)
                {
                    _adaptiveTimer.Interval = action.RecommendedIntervalMs;
                }
            }
            else if (action.ActionType == CornerActionType.Trigger)
            {
                // Centralized ShowOverview executes final fail-closed evaluation with fresh game scan
                bool shown = ShowOverview(action.TriggerScreen ?? screen);
                if (!shown)
                {
                    _detector.NotifySuppressed();
                }
            }
        }

        private void GameScanTimer_Tick(object sender, EventArgs e)
        {
            _gameCache.CheckRunningGameProcesses(_config, false);
        }

        private void RequestOverview(Screen screen)
        {
            if (_isDisposed || (_currentOverview != null && !_currentOverview.IsDisposed)) return;
            _requestedScreen = screen;
            _requestDeadline = MonotonicTime.NowMs + 500;
            _requestForeground = IntPtr.Zero;
            _requestStableTicks = 0;
            _requestTimer.Start();
        }

        private void RequestTimer_Tick(object sender, EventArgs e)
        {
            if (MonotonicTime.NowMs >= _requestDeadline)
            {
                _requestTimer.Stop();
                SetStatus("Bloqueado: entrada ou foco ainda em transi\u00e7\u00e3o; tente novamente.");
                return;
            }
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground == _requestForeground) _requestStableTicks++;
            else { _requestForeground = foreground; _requestStableTicks = 1; }
            if (_requestStableTicks < 2 || !SafetyGuards.CheckMouseButtons().IsAllowed) return;
            _requestTimer.Stop();
            ShowOverview(_requestedScreen);
        }

        private void SetStatus(string status)
        {
            if (string.Equals(status, _lastStatusText, StringComparison.Ordinal)) return;
            _lastStatusText = status;
            if (_notifyIcon != null) _notifyIcon.Text = status.Length > 63 ? status.Substring(0, 60) + "..." : status;
            if (_menuItemStatus != null)
            {
                _menuItemStatus.Text = status.Length > 65 ? status.Substring(0, 62) + "..." : status;
                _menuItemStatus.ToolTipText = status;
            }
            ActivationStatus.Record(status);
        }

        public bool ShowOverview(Screen screen)
        {
            if (_currentOverview != null && !_currentOverview.IsDisposed)
            {
                return false;
            }

            Screen targetScreen = screen ?? Screen.PrimaryScreen;

            // Centralized final safety guards check before opening overview
            // Always forces a fresh scan immediately before actual activation
            GuardResult finalCheck = SafetyGuards.EvaluateAllGuards(
                targetScreen,
                _config,
                _config.IsPaused,
                _currentOverview != null ? _currentOverview.Handle : IntPtr.Zero,
                _gameCache,
                forceFreshGameScan: true);

            if (!finalCheck.IsAllowed)
            {
                SetStatus("Bloqueado: " + finalCheck.Reason);
                _detector.NotifySuppressed();
                return false;
            }

            IntPtr expectedForeground = NativeMethods.GetForegroundWindow();
            OverviewForm prepared = new OverviewForm(targetScreen, _config, expectedForeground);
            prepared.RefreshWindows();
            GuardResult preparedCheck = SafetyGuards.EvaluateAllGuards(targetScreen, _config, _config.IsPaused, IntPtr.Zero, _gameCache, false);
            if (!preparedCheck.IsAllowed || NativeMethods.GetForegroundWindow() != expectedForeground)
            {
                SetStatus(!preparedCheck.IsAllowed ? "Bloqueado: " + preparedCheck.Reason : "Cancelado: foco mudou ao preparar as janelas.");
                prepared.Dispose();
                _detector.NotifySuppressed();
                return false;
            }
            _currentOverview = prepared;
            _currentOverview.ForegroundAcquired += delegate
            {
                SetStatus("Vis\u00e3o geral aberta");
            };
            _currentOverview.OverviewClosed += delegate
            {
                _detector.NotifyOverviewClosed(MonotonicTime.NowMs);
                _currentOverview = null;
                SetStatus("Pronto para ativar");
            };

            OverviewForm overview = _currentOverview;
            overview.Show();
            if (!overview.IsDisposed && overview.Visible)
            {
                int win32ErrShow;
                bool posOkAfterShow = overview.PromoteTopMost(out win32ErrShow);

                NativeMethods.SetForegroundWindow(overview.Handle);
                overview.Activate();

                int win32ErrActivate;
                bool posOkAfterActivate = overview.PromoteTopMost(out win32ErrActivate);

                if (!posOkAfterActivate || !overview.IsNativeTopMost())
                {
                    overview.Close();
                    SetStatus(string.Format("Falha ao elevar visão geral (Win32 {0}; primeira tentativa {1}).",
                        win32ErrActivate, posOkAfterShow ? 0 : win32ErrShow));
                    return false;
                }

                if (NativeMethods.GetForegroundWindow() == overview.Handle)
                {
                    overview.NotifyForegroundAcquired();
                    SetStatus("Vis\u00e3o geral aberta");
                    return true;
                }

                overview.StartPendingActivation(expectedForeground);
                SetStatus("Vis\u00e3o geral aberta (clique para focar)");
                return true;
            }

            SetStatus("Falha ao exibir vis\u00e3o geral.");
            return false;
        }

        private void TogglePause(object sender, EventArgs e)
        {
            _config.IsPaused = !_config.IsPaused;
            _config.Save();
            _menuItemPause.Checked = _config.IsPaused;

            string status = _config.IsPaused ? "YAHCW pausado." : "YAHCW ativo.";
            SetStatus(status);
            _notifyIcon.ShowBalloonTip(1500, "YAHCW", status, ToolTipIcon.Info);
        }

        private void BlockCurrentForegroundProcess(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastGenuineForegroundProcessName))
            {
                MessageBox.Show("Nenhum processo de aplicativo anterior identificado para bloqueio.", "YAHCW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string name = _lastGenuineForegroundProcessName;
            if (_config.AddBlockedProcess(name))
            {
                _notifyIcon.ShowBalloonTip(2000, "Processo Bloqueado",
                    string.Format("O processo '{0}' foi adicionado \u00e0 lista de jogos bloqueados.", name),
                    ToolTipIcon.Info);
            }
            else
            {
                _notifyIcon.ShowBalloonTip(2000, "Processo J\u00e1 Bloqueado",
                    string.Format("O processo '{0}' j\u00e1 est\u00e1 na lista de bloqueio.", name),
                    ToolTipIcon.Info);
            }
        }

        private void OpenConfigFile(object sender, EventArgs e)
        {
            string cfgPath = Config.GetConfigFilePath();
            if (!File.Exists(cfgPath))
            {
                _config.Save();
            }

            try
            {
                Process.Start("notepad.exe", cfgPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao abrir arquivo de configuracao: " + ex.Message, "YAHCW", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (disposing)
            {
                if (_requestTimer != null) { _requestTimer.Stop(); _requestTimer.Dispose(); }
                if (_adaptiveTimer != null)
                {
                    _adaptiveTimer.Stop();
                    _adaptiveTimer.Dispose();
                }

                if (_gameScanTimer != null)
                {
                    _gameScanTimer.Stop();
                    _gameScanTimer.Dispose();
                }

                UnregisterGlobalHotkey();

                if (_msgWindow != null)
                {
                    _msgWindow.Dispose();
                    _msgWindow = null;
                }

                if (_currentOverview != null && !_currentOverview.IsDisposed)
                {
                    OverviewForm overview = _currentOverview;
                    overview.Close();
                    overview.Dispose();
                    _currentOverview = null;
                }

                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                if (_contextMenu != null)
                {
                    _contextMenu.Dispose();
                    _contextMenu = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}

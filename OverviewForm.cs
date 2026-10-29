using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YAHCW
{
    public class OverviewForm : Form
    {
        private readonly Screen _invokingScreen;
        private readonly Config _config;
        private List<WindowItem> _allWindows;
        private List<WindowItem> _filteredWindows;
        private List<Rectangle> _tileRects;
        private List<Rectangle> _slotRects;

        private int _selectedIndex = 0;
        private int _hoveredIndex = -1;
        private int _gridCols = 1;
        private int _gridRows = 1;

        private Panel _headerPanel;
        private Label _lblTitle;
        private Label _lblCount;
        private Panel _searchPanel;
        private Label _searchIconLabel;
        private TextBox _searchBox;
        private Button _btnClearSearch;
        private Button _btnClose;

        private Panel _footerPanel;
        private DashboardControls _dashboard;
        private Label _pageLabel;
        private Button _previousPage;
        private Button _nextPage;

        private bool _closing;
        private bool _prepared;
        private bool _resourcesReleased;
        private bool _hasBeenForegrounded;
        private bool _isPendingActivation;
        private bool _hasDeferredPromoted;
        private IntPtr _initialForeground = IntPtr.Zero;
        private string _initialDesktopName;
        private Timer _pendingTimer;

        private readonly List<WindowItem> _matches = new List<WindowItem>();
        private int _page;

        public event EventHandler OverviewClosed;
        public event EventHandler ForegroundAcquired;

        public bool IsPendingActivation
        {
            get { return _isPendingActivation; }
        }

        public IntPtr InitialForegroundWindow
        {
            get { return _initialForeground; }
            set { _initialForeground = value; }
        }

        public bool HasBeenForegrounded
        {
            get { return _hasBeenForegrounded; }
        }

        public bool HasDeferredPromoted
        {
            get { return _hasDeferredPromoted; }
        }

        private int PageSize
        {
            get
            {
                int cols = Math.Max(1, (ClientSize.Width - 64) / 280);
                int rows = Math.Max(1, (ClientSize.Height - 160 - (_dashboard == null ? 168 : _dashboard.Height)) / 210);
                return Math.Max(1, Math.Min(Math.Min(_config.MaxThumbnails, 12), cols * rows));
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, string lp);

        private const int EM_SETCUEBANNER = 0x1501;

        private readonly Font _titleFont = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        private readonly Font _countFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        private readonly Font _cardTitleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private readonly Font _cardSubFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        private readonly Font _placeholderFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _hintFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        private readonly Font _emptyTitleFont = new Font("Segoe UI", 13.5f, FontStyle.Bold);
        private readonly Font _emptySubFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        private readonly Font _badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        private readonly Font _btnFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _sharedCloseBtnFont = new Font("Segoe UI", 10.5f, FontStyle.Regular);
        private readonly Font _sharedSearchIconFont = new Font("Segoe UI Symbol", 9.5f, FontStyle.Regular);
        private readonly Font _sharedSearchBoxFont = new Font("Segoe UI", 10f, FontStyle.Regular);
        private readonly Font _sharedClearSearchFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        private readonly Color _bgCharcoal = Color.FromArgb(24, 24, 28);
        private readonly Color _headerBg = Color.FromArgb(20, 20, 24);
        private readonly Color _headerBorder = Color.FromArgb(44, 44, 52);
        private readonly Color _searchBg = Color.FromArgb(34, 34, 40);
        private readonly Color _searchBorder = Color.FromArgb(60, 60, 72);
        private readonly Color _searchBorderFocus = Color.FromArgb(0, 120, 215);
        private readonly Color _cardBg = Color.FromArgb(34, 34, 39);
        private readonly Color _cardHoverBg = Color.FromArgb(44, 44, 52);
        private readonly Color _cardSelectedBg = Color.FromArgb(36, 48, 64);
        private readonly Color _cardBorderNormal = Color.FromArgb(56, 56, 64);
        private readonly Color _cardBorderHover = Color.FromArgb(90, 90, 104);
        private readonly Color _cardBorderSelected = Color.FromArgb(0, 120, 215);
        private readonly Color _slotBg = Color.FromArgb(16, 16, 20);
        private readonly Color _slotBorder = Color.FromArgb(32, 32, 38);
        private readonly Color _textWhite = Color.FromArgb(245, 245, 247);
        private readonly Color _textMuted = Color.FromArgb(165, 165, 175);
        private readonly Color _textDim = Color.FromArgb(115, 115, 125);
        private readonly Color _accentBlue = Color.FromArgb(0, 120, 215);
        private readonly Color _footerBg = Color.FromArgb(20, 20, 24);
        private readonly Color _footerBorder = Color.FromArgb(44, 44, 52);

        public OverviewForm(Screen invokingScreen, Config config) : this(invokingScreen, config, IntPtr.Zero)
        {
        }

        public OverviewForm(Screen invokingScreen, Config config, IntPtr initialForegroundWindow)
        {
            _invokingScreen = invokingScreen ?? Screen.PrimaryScreen;
            _config = config ?? new Config();
            _initialForeground = initialForegroundWindow;
            _allWindows = new List<WindowItem>();
            _filteredWindows = new List<WindowItem>();
            _tileRects = new List<Rectangle>();
            _slotRects = new List<Rectangle>();

            InitializeComponent();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= (int)NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        public bool PromoteTopMost()
        {
            int err;
            return PromoteTopMost(out err);
        }

        public bool PromoteTopMost(out int win32Error)
        {
            win32Error = 0;
            if (this.IsDisposed) return false;
            if (!this.IsHandleCreated)
            {
                IntPtr h = this.Handle;
            }
            return NativeMethods.PromoteToTopMost(this.Handle, out win32Error);
        }

        public bool IsNativeTopMost()
        {
            if (this.IsDisposed) return false;
            if (!this.IsHandleCreated)
            {
                IntPtr h = this.Handle;
            }
            return NativeMethods.HasTopMostStyle(this.Handle);
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.KeyPreview = true;
            this.BackColor = _bgCharcoal;
            this.Bounds = _invokingScreen.WorkingArea;

            // ==================== HEADER ====================
            _headerPanel = new Panel();
            _headerPanel.BackColor = _headerBg;
            _headerPanel.Height = 68;
            _headerPanel.Dock = DockStyle.Top;
            _headerPanel.Paint += HeaderPanel_Paint;

            // Header title
            _lblTitle = new Label();
            _lblTitle.Text = "YAHCW \u2022 Vis\u00e3o Geral";
            _lblTitle.Font = _titleFont;
            _lblTitle.ForeColor = _textWhite;
            _lblTitle.AutoSize = true;
            _lblTitle.Location = new Point(28, 22);

            // Window count badge
            _lblCount = new Label();
            _lblCount.Text = "Carregando...";
            _lblCount.Font = _countFont;
            _lblCount.ForeColor = _textMuted;
            _lblCount.AutoSize = true;
            _lblCount.Location = new Point(232, 25);

            // Dismiss / Close button (visible top-right)
            _btnClose = new Button();
            _btnClose.Text = "\u2715";
            _btnClose.Font = _sharedCloseBtnFont;
            _btnClose.ForeColor = _textMuted;
            _btnClose.BackColor = Color.FromArgb(32, 32, 38);
            _btnClose.FlatStyle = FlatStyle.Flat;
            _btnClose.FlatAppearance.BorderSize = 0;
            _btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
            _btnClose.Size = new Size(38, 34);
            _btnClose.Location = new Point(this.Width - 56, 17);
            _btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnClose.Cursor = Cursors.Hand;
            _btnClose.Click += delegate { CloseOverview(); };

            // Styled search container panel
            int searchPanelW = Math.Min(380, Math.Max(220, this.Width - 440));
            _searchPanel = new Panel();
            _searchPanel.Size = new Size(searchPanelW, 36);
            _searchPanel.Location = new Point(this.Width - searchPanelW - 72, 16);
            _searchPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _searchPanel.BackColor = _searchBg;
            _searchPanel.Paint += SearchPanel_Paint;
            _searchPanel.MouseDown += delegate
            {
                if (_searchBox != null && !_searchBox.Focused)
                {
                    _searchBox.Focus();
                }
            };

            _searchIconLabel = new Label();
            _searchIconLabel.Text = "\uD83D\uDD0D";
            _searchIconLabel.Font = _sharedSearchIconFont;
            _searchIconLabel.ForeColor = _textMuted;
            _searchIconLabel.Size = new Size(26, 24);
            _searchIconLabel.Location = new Point(8, 7);
            _searchIconLabel.TextAlign = ContentAlignment.MiddleCenter;

            _searchBox = new TextBox();
            _searchBox.BackColor = _searchBg;
            _searchBox.ForeColor = _textWhite;
            _searchBox.BorderStyle = BorderStyle.None;
            _searchBox.Font = _sharedSearchBoxFont;
            _searchBox.Location = new Point(36, 8);
            _searchBox.Size = new Size(searchPanelW - 68, 20);
            _searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _searchBox.TextChanged += SearchBox_TextChanged;
            _searchBox.KeyDown += SearchBox_KeyDown;
            _searchBox.Enter += delegate
            {
                _searchPanel.Invalidate();
                if (Visible && NativeMethods.GetForegroundWindow() == Handle)
                {
                    MarkForegroundAcquired();
                }
            };
            _searchBox.Leave += delegate { _searchPanel.Invalidate(); };

            _btnClearSearch = new Button();
            _btnClearSearch.Text = "\u2715";
            _btnClearSearch.Font = _sharedClearSearchFont;
            _btnClearSearch.ForeColor = _textDim;
            _btnClearSearch.BackColor = Color.Transparent;
            _btnClearSearch.FlatStyle = FlatStyle.Flat;
            _btnClearSearch.FlatAppearance.BorderSize = 0;
            _btnClearSearch.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 60);
            _btnClearSearch.Size = new Size(24, 24);
            _btnClearSearch.Location = new Point(searchPanelW - 28, 6);
            _btnClearSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnClearSearch.Visible = false;
            _btnClearSearch.Cursor = Cursors.Hand;
            _btnClearSearch.Click += delegate
            {
                _searchBox.Text = string.Empty;
                _searchBox.Focus();
            };

            _searchPanel.Controls.Add(_searchIconLabel);
            _searchPanel.Controls.Add(_searchBox);
            _searchPanel.Controls.Add(_btnClearSearch);

            _headerPanel.Controls.Add(_lblTitle);
            _headerPanel.Controls.Add(_lblCount);
            _headerPanel.Controls.Add(_searchPanel);
            _headerPanel.Controls.Add(_btnClose);
            this.Controls.Add(_headerPanel);

            // ==================== FOOTER ====================
            _footerPanel = new Panel();
            _footerPanel.Height = 42;
            _footerPanel.Dock = DockStyle.Bottom;
            _footerPanel.BackColor = _footerBg;
            _footerPanel.Paint += FooterPanel_Paint;

            _pageLabel = new Label();
            _pageLabel.AutoSize = false;
            _pageLabel.AutoEllipsis = true;
            _pageLabel.Height = 22;
            _pageLabel.Width = Math.Max(60, ClientSize.Width - 290);
            _pageLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _pageLabel.Font = _hintFont;
            _pageLabel.ForeColor = _textMuted;
            _pageLabel.Location = new Point(28, 12);

            _previousPage = new Button();
            _previousPage.Text = "< Anterior";
            _previousPage.Font = _btnFont;
            _previousPage.Width = 92;
            _previousPage.Height = 28;
            _previousPage.Left = Width - 232;
            _previousPage.Top = 7;
            _previousPage.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _previousPage.FlatStyle = FlatStyle.Flat;
            _previousPage.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 70);
            _previousPage.FlatAppearance.BorderSize = 1;
            _previousPage.BackColor = Color.FromArgb(32, 32, 38);
            _previousPage.ForeColor = _textWhite;
            _previousPage.Cursor = Cursors.Hand;
            _previousPage.Click += delegate { ChangePage(-1); };

            _nextPage = new Button();
            _nextPage.Text = "Pr\u00f3xima >";
            _nextPage.Font = _btnFont;
            _nextPage.Width = 92;
            _nextPage.Height = 28;
            _nextPage.Left = Width - 130;
            _nextPage.Top = 7;
            _nextPage.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _nextPage.FlatStyle = FlatStyle.Flat;
            _nextPage.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 70);
            _nextPage.FlatAppearance.BorderSize = 1;
            _nextPage.BackColor = Color.FromArgb(32, 32, 38);
            _nextPage.ForeColor = _textWhite;
            _nextPage.Cursor = Cursors.Hand;
            _nextPage.Click += delegate { ChangePage(1); };

            _footerPanel.Controls.Add(_pageLabel);
            _footerPanel.Controls.Add(_previousPage);
            _footerPanel.Controls.Add(_nextPage);
            this.Controls.Add(_footerPanel);
            _dashboard = new DashboardControls { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            this.Controls.Add(_dashboard);

            this.KeyDown += OverviewForm_KeyDown;
            this.MouseDown += OverviewForm_MouseDown;
            this.MouseMove += OverviewForm_MouseMove;
            LayoutChrome();
        }

        private void LayoutChrome()
        {
            if (_searchPanel == null || _btnClose == null || _nextPage == null) return;
            if (_dashboard != null)
            {
                _dashboard.SetBounds(0, Math.Max(_headerPanel.Height, ClientSize.Height - _footerPanel.Height - _dashboard.Height), ClientSize.Width, _dashboard.Height);
            }
            _lblCount.Visible = ClientSize.Width >= 720;
            _lblTitle.Text = ClientSize.Width < 520 ? "YAHCW" : "YAHCW \u2022 Vis\u00e3o Geral";
            _btnClose.Left = ClientSize.Width - 56;
            _searchPanel.Width = Math.Min(380, Math.Max(140, ClientSize.Width - (ClientSize.Width >= 720 ? 440 : 330)));
            _searchPanel.Left = ClientSize.Width - _searchPanel.Width - 72;
            _previousPage.Left = ClientSize.Width - 232;
            _nextPage.Left = ClientSize.Width - 130;
            _pageLabel.Width = Math.Max(60, ClientSize.Width - 290);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_nextPage == null || _resourcesReleased) return;
            LayoutChrome();
            int pages = Math.Max(1, (_matches.Count + PageSize - 1) / PageSize);
            _page = Math.Min(_page, pages - 1);
            UpdatePage();
        }

        private void HeaderPanel_Paint(object sender, PaintEventArgs e)
        {
            using (Pen p = new Pen(_headerBorder, 1f))
            {
                e.Graphics.DrawLine(p, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
            }
        }

        private void FooterPanel_Paint(object sender, PaintEventArgs e)
        {
            using (Pen p = new Pen(_footerBorder, 1f))
            {
                e.Graphics.DrawLine(p, 0, 0, _footerPanel.Width, 0);
            }
        }

        private void SearchPanel_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _searchPanel.Width - 1, _searchPanel.Height - 1);
            Color borderCol = _searchBox.Focused ? _searchBorderFocus : _searchBorder;
            float borderW = _searchBox.Focused ? 1.5f : 1f;

            using (GraphicsPath path = CreateRoundedRectanglePath(r, 6))
            {
                using (SolidBrush b = new SolidBrush(_searchBg))
                {
                    e.Graphics.FillPath(b, path);
                }
                using (Pen p = new Pen(borderCol, borderW))
                {
                    e.Graphics.DrawPath(p, path);
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_searchBox != null)
                {
                    SendMessage(_searchBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Pesquisar janelas ou processos... (Esc fecha)");
                }
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (!_prepared) RefreshWindows();
            _searchBox.Focus();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _searchBox.Focus();

            // Explicit native promotion to top of topmost band on show
            PromoteTopMost();

            // One bounded deferred post-show reassertion to ensure proper z-order
            // after the window manager processes show/activation messages
            if (!_hasDeferredPromoted)
            {
                _hasDeferredPromoted = true;
                BeginInvoke(new Action(delegate
                {
                    if (!_closing && !_resourcesReleased && Visible)
                    {
                        PromoteTopMost();
                    }
                }));
            }

            if (Visible && NativeMethods.GetForegroundWindow() == this.Handle)
            {
                MarkForegroundAcquired();
            }
            else
            {
                BeginInvoke(new Action(delegate
                {
                    if (Visible && NativeMethods.GetForegroundWindow() == this.Handle)
                    {
                        MarkForegroundAcquired();
                    }
                }));
            }
            if (_dashboard != null && !_resourcesReleased) await _dashboard.RefreshAsync();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (Visible && NativeMethods.GetForegroundWindow() == this.Handle)
            {
                MarkForegroundAcquired();
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ACTIVATE = 0x0006;
            const int WA_INACTIVE = 0;

            if (m.Msg == WM_ACTIVATE)
            {
                int wa = m.WParam.ToInt32() & 0xFFFF;
                if (wa != WA_INACTIVE && Visible && NativeMethods.GetForegroundWindow() == this.Handle)
                {
                    MarkForegroundAcquired();
                }
            }

            base.WndProc(ref m);
        }

        public void StartPendingActivation(IntPtr initialForeground)
        {
            if (_hasBeenForegrounded || _closing || _resourcesReleased) return;

            if (initialForeground != IntPtr.Zero)
            {
                _initialForeground = initialForeground;
            }

            if (string.IsNullOrEmpty(_initialDesktopName))
            {
                _initialDesktopName = GetCurrentInputDesktopName();
            }

            _isPendingActivation = true;

            if (_pendingTimer == null)
            {
                _pendingTimer = new Timer();
                _pendingTimer.Interval = 50;
                _pendingTimer.Tick += PendingTimer_Tick;
            }

            _pendingTimer.Start();
        }

        public void NotifyForegroundAcquired()
        {
            MarkForegroundAcquired();
        }

        private void MarkForegroundAcquired()
        {
            if (_hasBeenForegrounded) return;
            _hasBeenForegrounded = true;
            _isPendingActivation = false;
            StopPendingTimer();
            if (ForegroundAcquired != null)
            {
                ForegroundAcquired(this, EventArgs.Empty);
            }
        }

        private void StopPendingTimer()
        {
            if (_pendingTimer != null)
            {
                _pendingTimer.Stop();
                _pendingTimer.Dispose();
                _pendingTimer = null;
            }
        }

        private void PendingTimer_Tick(object sender, EventArgs e)
        {
            CheckPendingState();
        }

        public bool CheckPendingState()
        {
            return CheckPendingState(NativeMethods.GetForegroundWindow());
        }

        public bool CheckPendingState(IntPtr currentFg)
        {
            return EvaluatePendingState(currentFg, IsDesktopSafe(), IsPointerConfined());
        }

        internal bool EvaluatePendingState(IntPtr currentFg, bool desktopSafe, bool pointerConfined)
        {
            if (_closing || _resourcesReleased)
            {
                StopPendingTimer();
                return false;
            }

            if (!desktopSafe || pointerConfined)
            {
                CloseOverview();
                return false;
            }

            if (currentFg == this.Handle)
            {
                MarkForegroundAcquired();
                return true;
            }

            // Check focus switch to unrelated window (except original foreground)
            if (currentFg != IntPtr.Zero && currentFg != this.Handle)
            {
                if (_initialForeground != IntPtr.Zero && currentFg != _initialForeground)
                {
                    CloseOverview();
                    return false;
                }
                else if (_initialForeground == IntPtr.Zero)
                {
                    CloseOverview();
                    return false;
                }
            }

            return true;
        }

        private string GetCurrentInputDesktopName()
        {
            try
            {
                IntPtr hDesktop = NativeMethods.OpenInputDesktop(0, false, 0x0001);
                if (hDesktop == IntPtr.Zero) return null;
                try
                {
                    StringBuilder inputName = new StringBuilder(256);
                    uint needed;
                    if (NativeMethods.GetUserObjectInformationW(hDesktop, 2, inputName, (uint)inputName.Capacity * 2, out needed))
                    {
                        return inputName.ToString();
                    }
                    return null;
                }
                finally
                {
                    NativeMethods.CloseDesktop(hDesktop);
                }
            }
            catch
            {
                return null;
            }
        }

        private bool IsDesktopSafe()
        {
            string currentInput = GetCurrentInputDesktopName();
            if (string.IsNullOrEmpty(currentInput))
            {
                return false;
            }

            if (string.Equals(currentInput, "Winlogon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentInput, "Screen-saver", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(_initialDesktopName) &&
                !string.Equals(currentInput, _initialDesktopName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private bool IsPointerConfined()
        {
            try
            {
                RECT clip;
                if (!NativeMethods.GetClipCursor(out clip)) return true;
                Rectangle vs = SystemInformation.VirtualScreen;
                return (clip.Left > vs.Left || clip.Top > vs.Top || clip.Right < vs.Right || clip.Bottom < vs.Bottom);
            }
            catch
            {
                return true;
            }
        }

        public void RefreshWindows()
        {
            _prepared = true;
            UnregisterAllThumbnails();
            DisposeWindowsList(_allWindows);

            IntPtr exclude = this.IsHandleCreated ? this.Handle : IntPtr.Zero;
            _allWindows = WindowEnumerator.EnumerateEligibleWindows(exclude, 256);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            UnregisterAllThumbnails();
            _filteredWindows.Clear();
            _matches.Clear();
            _page = 0;

            string query = (_searchBox.Text ?? string.Empty).Trim().ToLowerInvariant();

            for (int i = 0; i < _allWindows.Count; i++)
            {
                WindowItem item = _allWindows[i];
                if (string.IsNullOrEmpty(query))
                {
                    _matches.Add(item);
                }
                else
                {
                    bool matchTitle = item.Title.ToLowerInvariant().Contains(query);
                    bool matchProc = item.ProcessName.ToLowerInvariant().Contains(query);
                    if (matchTitle || matchProc)
                    {
                        _matches.Add(item);
                    }
                }
            }

            UpdatePage();
        }

        private void ChangePage(int delta)
        {
            int pages = Math.Max(1, (_matches.Count + PageSize - 1) / PageSize);
            int next = Math.Max(0, Math.Min(pages - 1, _page + delta));
            if (next == _page) return;
            _page = next;
            UpdatePage();
        }

        private void UpdatePage()
        {
            UnregisterAllThumbnails();
            _filteredWindows.Clear();
            int start = _page * PageSize;
            for (int index = start; index < Math.Min(start + PageSize, _matches.Count); index++)
            {
                _filteredWindows.Add(_matches[index]);
            }

            int pages = Math.Max(1, (_matches.Count + PageSize - 1) / PageSize);
            _lblCount.Text = string.Format("{0} janelas", _matches.Count);
            _pageLabel.Text = string.Format("P\u00e1gina {0}/{1}   \u00b7   [Esc] Fechar   \u00b7   [Enter] Alternar   \u00b7   [\u2191\u2193\u2190\u2192] Navegar   \u00b7   [PgUp/PgDn] P\u00e1ginas", _page + 1, pages);

            _previousPage.Enabled = _page > 0;
            _nextPage.Enabled = _page + 1 < pages;
            _previousPage.ForeColor = _previousPage.Enabled ? _textWhite : _textDim;
            _nextPage.ForeColor = _nextPage.Enabled ? _textWhite : _textDim;

            _selectedIndex = _filteredWindows.Count > 0 ? 0 : -1;
            _hoveredIndex = -1;

            ComputeLayout();
            RegisterThumbnails();
            this.Invalidate();
        }

        private void ComputeLayout()
        {
            _tileRects.Clear();
            _slotRects.Clear();

            int count = _filteredWindows.Count;
            if (count == 0) return;

            int availLeft = 32;
            int availTop = _headerPanel.Height + 16;
            int availBottom = (_footerPanel != null ? _footerPanel.Height + 16 : 54) + (_dashboard == null ? 0 : _dashboard.Height);
            int availWidth = Math.Max(280, this.ClientSize.Width - 64);
            int availHeight = Math.Max(110, this.ClientSize.Height - availTop - availBottom);

            int maxCols = Math.Max(1, Math.Min(6, availWidth / 280));
            if (count <= 1) { _gridCols = 1; _gridRows = 1; }
            else if (count == 2) { _gridCols = Math.Min(2, maxCols); _gridRows = (int)Math.Ceiling(count / (double)_gridCols); }
            else if (count <= 4) { _gridCols = Math.Min(2, maxCols); _gridRows = (int)Math.Ceiling(count / (double)_gridCols); }
            else if (count <= 6) { _gridCols = Math.Min(3, maxCols); _gridRows = (int)Math.Ceiling(count / (double)_gridCols); }
            else if (count <= 8) { _gridCols = Math.Min(4, maxCols); _gridRows = (int)Math.Ceiling(count / (double)_gridCols); }
            else { _gridCols = Math.Min(4, maxCols); _gridRows = (int)Math.Ceiling(count / (double)_gridCols); }

            int padX = 16;
            int padY = 16;
            int tileW = Math.Min(380, Math.Max(140, (availWidth - (_gridCols - 1) * padX) / _gridCols));
            int tileH = Math.Min(270, Math.Max(110, (availHeight - (_gridRows - 1) * padY) / _gridRows));

            int totalGridW = _gridCols * tileW + (_gridCols - 1) * padX;
            int totalGridH = _gridRows * tileH + (_gridRows - 1) * padY;
            int startX = availLeft + Math.Max(0, (availWidth - totalGridW) / 2);
            int startY = availTop + Math.Max(0, (availHeight - totalGridH) / 2);

            int headerH = 46;

            for (int i = 0; i < count; i++)
            {
                int col = i % _gridCols;
                int row = i / _gridCols;
                int x = startX + col * (tileW + padX);
                int y = startY + row * (tileH + padY);

                Rectangle rTile = new Rectangle(x, y, tileW, tileH);
                _tileRects.Add(rTile);

                Rectangle rSlot = new Rectangle(x + 10, y + headerH, Math.Max(10, tileW - 20), Math.Max(10, tileH - headerH - 10));
                _slotRects.Add(rSlot);
            }
        }

        private void RegisterThumbnails()
        {
            for (int i = 0; i < _filteredWindows.Count && i < _slotRects.Count; i++)
            {
                WindowItem item = _filteredWindows[i];
                Rectangle slot = _slotRects[i];

                if (item.IsMinimized)
                {
                    continue;
                }

                IntPtr thumbId;
                int hr = NativeMethods.DwmRegisterThumbnail(this.Handle, item.Handle, out thumbId);
                if (hr == 0 && thumbId != IntPtr.Zero)
                {
                    item.ThumbnailId = thumbId;

                    PSIZE srcSize;
                    int hrSize = NativeMethods.DwmQueryThumbnailSourceSize(thumbId, out srcSize);
                    int srcW = (hrSize == 0 && srcSize.cx > 0) ? srcSize.cx : item.Bounds.Width;
                    int srcH = (hrSize == 0 && srcSize.cy > 0) ? srcSize.cy : item.Bounds.Height;

                    if (srcW <= 0) srcW = 800;
                    if (srcH <= 0) srcH = 600;

                    float scaleX = (float)slot.Width / srcW;
                    float scaleY = (float)slot.Height / srcH;
                    float scale = Math.Min(scaleX, scaleY);

                    int dstW = Math.Max(1, (int)(srcW * scale));
                    int dstH = Math.Max(1, (int)(srcH * scale));
                    int dstX = slot.X + (slot.Width - dstW) / 2;
                    int dstY = slot.Y + (slot.Height - dstH) / 2;

                    DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES();
                    props.dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY;
                    props.rcDestination = new RECT(dstX, dstY, dstX + dstW, dstY + dstH);
                    props.fVisible = true;
                    props.opacity = 255;

                    NativeMethods.DwmUpdateThumbnailProperties(thumbId, ref props);
                }
            }
        }

        private void UnregisterAllThumbnails()
        {
            if (_allWindows != null)
            {
                for (int i = 0; i < _allWindows.Count; i++)
                {
                    if (_allWindows[i].ThumbnailId != IntPtr.Zero)
                    {
                        try
                        {
                            NativeMethods.DwmUnregisterThumbnail(_allWindows[i].ThumbnailId);
                        }
                        catch { }
                        _allWindows[i].ThumbnailId = IntPtr.Zero;
                    }
                }
            }
        }

        private void DisposeWindowsList(List<WindowItem> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    list[i].Dispose();
                }
                catch { }
            }
            list.Clear();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (_filteredWindows.Count == 0)
            {
                PaintEmptyState(g);
                return;
            }

            for (int i = 0; i < _filteredWindows.Count && i < _tileRects.Count; i++)
            {
                WindowItem item = _filteredWindows[i];
                Rectangle tile = _tileRects[i];
                Rectangle slot = _slotRects[i];

                bool isSelected = (i == _selectedIndex);
                bool isHovered = (i == _hoveredIndex && !isSelected);

                // Card background with rounded corners
                Color bg = isSelected ? _cardSelectedBg : (isHovered ? _cardHoverBg : _cardBg);
                Color border = isSelected ? _cardBorderSelected : (isHovered ? _cardBorderHover : _cardBorderNormal);
                float borderW = isSelected ? 2f : (isHovered ? 1.5f : 1f);

                using (SolidBrush b = new SolidBrush(bg))
                {
                    FillRoundedRectangle(g, b, tile, 8);
                }

                using (Pen p = new Pen(border, borderW))
                {
                    DrawRoundedRectangle(g, p, tile, 8);
                }

                // Selection accent bar
                if (isSelected)
                {
                    Rectangle rBar = new Rectangle(tile.X + 8, tile.Y + 2, tile.Width - 16, 3);
                    using (SolidBrush bAccent = new SolidBrush(_accentBlue))
                    {
                        FillRoundedRectangle(g, bAccent, rBar, 2);
                    }
                }

                // App Icon (safe render)
                int iconX = tile.X + 12;
                int iconY = tile.Y + 11;
                if (item.AppIcon != null)
                {
                    try
                    {
                        g.DrawIcon(item.AppIcon, new Rectangle(iconX, iconY, 24, 24));
                    }
                    catch
                    {
                        g.DrawIcon(SystemIcons.Application, new Rectangle(iconX, iconY, 24, 24));
                    }
                }
                else
                {
                    g.DrawIcon(SystemIcons.Application, new Rectangle(iconX, iconY, 24, 24));
                }

                // Title and process text (responsive, zero overlap)
                int textX = tile.X + 44;
                int textW = Math.Max(20, tile.Width - 54);

                Rectangle rTitle = new Rectangle(textX, tile.Y + 8, textW, 18);
                TextRenderer.DrawText(g, item.Title ?? string.Empty, _cardTitleFont, rTitle, _textWhite,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                string sub = item.ProcessName ?? string.Empty;
                if (item.IsMinimized) sub += " \u2022 Minimizada";
                Rectangle rSub = new Rectangle(textX, tile.Y + 28, textW, 16);
                TextRenderer.DrawText(g, sub, _cardSubFont, rSub, _textMuted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                // Slot background (thumbnail frame)
                using (SolidBrush bSlot = new SolidBrush(_slotBg))
                {
                    FillRoundedRectangle(g, bSlot, slot, 6);
                }
                using (Pen pSlot = new Pen(_slotBorder, 1f))
                {
                    DrawRoundedRectangle(g, pSlot, slot, 6);
                }

                // Placeholder when minimized or DWM thumbnail unavailable
                if (item.ThumbnailId == IntPtr.Zero || item.IsMinimized)
                {
                    PaintThumbnailPlaceholder(g, item, slot);
                }
            }
        }

        private void PaintThumbnailPlaceholder(Graphics g, WindowItem item, Rectangle slot)
        {
            int iconSize = Math.Min(48, Math.Max(24, slot.Height - 60));
            int iconX = slot.X + (slot.Width - iconSize) / 2;
            int iconY = slot.Y + (slot.Height - iconSize) / 2 - 14;

            if (item.AppIcon != null)
            {
                try
                {
                    g.DrawIcon(item.AppIcon, new Rectangle(iconX, iconY, iconSize, iconSize));
                }
                catch { }
            }

            // Minimized / preview unavailable badge
            string badgeText = item.IsMinimized ? "Janela minimizada" : "Pr\u00e9via DWM indispon\u00edvel";
            SizeF szBadge = g.MeasureString(badgeText, _placeholderFont);
            int badgeW = (int)szBadge.Width + 16;
            int badgeH = (int)szBadge.Height + 6;
            int badgeX = slot.X + (slot.Width - badgeW) / 2;
            int badgeY = iconY + iconSize + 10;

            Rectangle rBadge = new Rectangle(badgeX, badgeY, badgeW, badgeH);
            using (SolidBrush bPill = new SolidBrush(Color.FromArgb(32, 32, 40)))
            {
                FillRoundedRectangle(g, bPill, rBadge, 4);
            }
            using (Pen pPill = new Pen(Color.FromArgb(50, 50, 60), 1f))
            {
                DrawRoundedRectangle(g, pPill, rBadge, 4);
            }

            TextRenderer.DrawText(g, badgeText, _placeholderFont, rBadge, _textMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // Subtle action cue
            string actionText = "Enter para restaurar";
            Rectangle rAction = new Rectangle(slot.X + 4, badgeY + badgeH + 6, slot.Width - 8, 16);
            TextRenderer.DrawText(g, actionText, _hintFont, rAction, _textDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        }

        private void PaintEmptyState(Graphics g)
        {
            int centerY = (_headerPanel.Height + (this.ClientSize.Height - _footerPanel.Height - (_dashboard == null ? 0 : _dashboard.Height))) / 2 - 40;
            string query = (_searchBox.Text ?? string.Empty).Trim();

            string title = "Nenhuma janela encontrada";
            string subtitle = string.IsNullOrEmpty(query)
                ? "Nenhuma janela aberta eleg\u00edvel foi detectada na \u00e1rea de trabalho."
                : string.Format("Nenhuma janela corresponde \u00e0 busca \"{0}\".", query);
            string hint = "Pressione [Esc] para fechar ou limpe o campo de busca.";

            SizeF szTitle = g.MeasureString(title, _emptyTitleFont);
            SizeF szSub = g.MeasureString(subtitle, _emptySubFont);
            SizeF szHint = g.MeasureString(hint, _hintFont);

            int titleX = (this.ClientSize.Width - (int)szTitle.Width) / 2;
            int subX = (this.ClientSize.Width - (int)szSub.Width) / 2;
            int hintX = (this.ClientSize.Width - (int)szHint.Width) / 2;

            using (SolidBrush titleBrush = new SolidBrush(_textWhite)) g.DrawString(title, _emptyTitleFont, titleBrush, titleX, centerY);
            using (SolidBrush subtitleBrush = new SolidBrush(_textMuted)) g.DrawString(subtitle, _emptySubFont, subtitleBrush, subX, centerY + 32);
            using (SolidBrush hintBrush = new SolidBrush(_textDim)) g.DrawString(hint, _hintFont, hintBrush, hintX, centerY + 62);
        }

        private void FillRoundedRectangle(Graphics g, Brush brush, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath path = CreateRoundedRectanglePath(r, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath path = CreateRoundedRectanglePath(r, radius))
            {
                g.DrawPath(pen, path);
            }
        }

        private GraphicsPath CreateRoundedRectanglePath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void OverviewForm_MouseMove(object sender, MouseEventArgs e)
        {
            int oldHover = _hoveredIndex;
            _hoveredIndex = -1;

            for (int i = 0; i < _tileRects.Count; i++)
            {
                if (_tileRects[i].Contains(e.Location))
                {
                    _hoveredIndex = i;
                    break;
                }
            }

            if (_hoveredIndex != oldHover)
            {
                this.Invalidate();
            }
        }

        private void OverviewForm_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < _tileRects.Count; i++)
            {
                if (_tileRects[i].Contains(e.Location))
                {
                    _selectedIndex = i;
                    ActivateSelectedWindow();
                    return;
                }
            }

            // Clicked outside tiles: dismiss overview
            CloseOverview();
        }

        private void SearchBox_TextChanged(object sender, EventArgs e)
        {
            if (_btnClearSearch != null)
            {
                _btnClearSearch.Visible = !string.IsNullOrEmpty(_searchBox.Text);
            }
            ApplyFilter();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ChangePage(e.KeyCode == Keys.PageDown ? 1 : -1);
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CloseOverview();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ActivateSelectedWindow();
                return;
            }

            if (e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                MoveSelection(0, 1);
                return;
            }

            if (e.KeyCode == Keys.Up)
            {
                e.Handled = true;
                MoveSelection(0, -1);
                return;
            }

            // Left / Right: when search text is empty, navigate cards; when text exists, let caret move naturally
            if (e.KeyCode == Keys.Left)
            {
                if (string.IsNullOrEmpty(_searchBox.Text))
                {
                    e.Handled = true;
                    MoveSelection(-1, 0);
                }
                return;
            }

            if (e.KeyCode == Keys.Right)
            {
                if (string.IsNullOrEmpty(_searchBox.Text))
                {
                    e.Handled = true;
                    MoveSelection(1, 0);
                }
                return;
            }

            // Space: intentionally left to normal text input! Never triggers activation here.
        }

        private void OverviewForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (_dashboard != null && _dashboard.ContainsFocus && e.KeyCode != Keys.Escape) return;
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseOverview();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                ActivateSelectedWindow();
                return;
            }

            if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp)
            {
                e.Handled = true;
                ChangePage(e.KeyCode == Keys.PageDown ? 1 : -1);
                return;
            }

            if (e.KeyCode == Keys.Left)
            {
                if (!_searchBox.Focused || string.IsNullOrEmpty(_searchBox.Text))
                {
                    e.Handled = true;
                    MoveSelection(-1, 0);
                }
                return;
            }

            if (e.KeyCode == Keys.Right)
            {
                if (!_searchBox.Focused || string.IsNullOrEmpty(_searchBox.Text))
                {
                    e.Handled = true;
                    MoveSelection(1, 0);
                }
                return;
            }

            if (e.KeyCode == Keys.Up)
            {
                e.Handled = true;
                MoveSelection(0, -1);
                return;
            }

            if (e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                MoveSelection(0, 1);
                return;
            }

            // Space key: ensures space stays search text! Never triggers window activation.
            if (e.KeyCode == Keys.Space)
            {
                if (!_searchBox.Focused)
                {
                    _searchBox.Focus();
                    _searchBox.AppendText(" ");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            // If user types alphanumeric or backspace while focus is elsewhere, redirect to search box
            if (!_searchBox.Focused && !e.Control && !e.Alt)
            {
                if ((e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) ||
                    (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) ||
                    (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9) ||
                    e.KeyCode == Keys.Back)
                {
                    _searchBox.Focus();
                }
            }
        }

        private void MoveSelection(int dx, int dy)
        {
            if (_filteredWindows.Count == 0) return;

            int newIndex = _selectedIndex;
            if (dx != 0)
            {
                newIndex += dx;
            }
            if (dy != 0)
            {
                newIndex += dy * _gridCols;
            }

            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _filteredWindows.Count) newIndex = _filteredWindows.Count - 1;

            if (newIndex != _selectedIndex)
            {
                _selectedIndex = newIndex;
                this.Invalidate();
            }
        }

        private void ActivateSelectedWindow()
        {
            if (_selectedIndex >= 0 && _selectedIndex < _filteredWindows.Count)
            {
                WindowItem target = _filteredWindows[_selectedIndex];
                IntPtr targetHwnd = target.Handle;
                uint currentProcessId;
                NativeMethods.GetWindowThreadProcessId(targetHwnd, out currentProcessId);
                if (!NativeMethods.IsWindow(targetHwnd) || currentProcessId != target.ProcessId)
                {
                    RefreshWindows();
                    return;
                }
                bool isMinimized = target.IsMinimized;

                // Cleanup thumbnails and hide overview first before restoring/foregrounding target window
                UnregisterAllThumbnails();
                this.Hide();

                if (isMinimized || NativeMethods.IsIconic(targetHwnd))
                {
                    ShowWindowAsync(targetHwnd, NativeMethods.SW_RESTORE);
                }

                NativeMethods.SetForegroundWindow(targetHwnd);
            }

            CloseOverview();
        }

        public void CloseOverview()
        {
            if (_closing) return;
            _closing = true;
            StopPendingTimer();
            UnregisterAllThumbnails();
            this.Hide();

            this.Close();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_closing) return;

            // Only dismiss after the form was successfully foregrounded,
            // never during the initial Show / activation transition or while in pending activation.
            if (!_hasBeenForegrounded)
            {
                if (Visible && NativeMethods.GetForegroundWindow() == this.Handle)
                {
                    MarkForegroundAcquired();
                }
                else
                {
                    return;
                }
            }

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != this.Handle)
            {
                CloseOverview();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            ReleaseResources();
            if (OverviewClosed != null) OverviewClosed(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseResources();
            base.Dispose(disposing);
            if (disposing)
            {
                _titleFont.Dispose();
                _countFont.Dispose();
                _cardTitleFont.Dispose();
                _cardSubFont.Dispose();
                _placeholderFont.Dispose();
                _hintFont.Dispose();
                _emptyTitleFont.Dispose();
                _emptySubFont.Dispose();
                _badgeFont.Dispose();
                _btnFont.Dispose();
                _sharedCloseBtnFont.Dispose();
                _sharedSearchIconFont.Dispose();
                _sharedSearchBoxFont.Dispose();
                _sharedClearSearchFont.Dispose();
            }
        }

        private void ReleaseResources()
        {
            if (_resourcesReleased) return;
            _resourcesReleased = true;
            StopPendingTimer();
            UnregisterAllThumbnails();
            _filteredWindows.Clear();
            _matches.Clear();
            DisposeWindowsList(_allWindows);

            if (_headerPanel != null)
            {
                try { _headerPanel.Dispose(); } catch { }
            }
            if (_footerPanel != null)
            {
                try { _footerPanel.Dispose(); } catch { }
            }
            if (_dashboard != null)
            {
                try { _dashboard.Dispose(); } catch { }
            }
        }
    }
}

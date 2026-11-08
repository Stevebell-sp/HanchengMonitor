using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassMonitor;

public partial class MainWindow : Window
{
    private readonly AppSettings _s;
    private readonly TrayIcon _tray;
    private readonly HistoryLogger _history;
    private readonly GameDetector _game = new();
    private readonly Dictionary<string, MetricCell> _cells = new();
    private readonly Dictionary<string, DateTime> _alertTimes = new();
    private MetricsService? _svc;
    private Dictionary<string, MetricState>? _last;
    private SettingsWindow? _settingsWin;
    private DetailWindow? _detail;
    private HistoryWindow? _historyWin;
    private UpdateInfo? _pendingUpdate;
    private bool _autoInstallWanted;
    private bool _installing;
    private IntPtr _hwnd;
    private bool _positioned;
    private bool _exiting;
    private bool _inGame;
    private bool _recreating;
    /// <summary>這個視窗是否以透明分層視窗建立（滑鼠穿透需要）。</summary>
    private readonly bool _layered;
    private readonly System.Windows.Threading.DispatcherTimer? _passKeyTimer;
    private bool _passHeld;
    private int _tick;

    public MainWindow(AppSettings settings)
    {
        _s = settings;
        Accents.Scheme = _s.ColorScheme;
        Accents.Overrides = _s.ColorOverrides;
        InitializeComponent();

        // 滑鼠穿透只能用「建立時就是透明分層」的視窗，所以開穿透時改用這種視窗
        _layered = _s.ClickThrough;
        if (_layered)
        {
            AllowsTransparency = true;
            SizeChanged += (_, _) => UpdateRoundRegion();
            // 按住指定按鍵時暫時解除穿透，才能點格子看詳細資訊或拖曳
            _passKeyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _passKeyTimer.Tick += (_, _) => UpdatePassThrough();
            _passKeyTimer.Start();
        }

        _history = new HistoryLogger(_s);
        _tray = new TrayIcon();
        _tray.RightClick += () => { Activate(); ShowMenu(); };
        _tray.DoubleClick += OpenSettings;

        Loaded += OnLoaded;
        Frame.MouseLeftButtonDown += OnFrameMouseDown;
        Frame.MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetToolWindow(_hwnd);
        Native.SetNoActivate(_hwnd);
        UpdateRoundRegion();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };
        ApplyVisuals();
    }

    /// <summary>不讓小工具被最小化或隱藏（Win+D、Win+M、全部最小化）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
        {
            handled = true;
        }
        else if (msg == Native.WM_WINDOWPOSCHANGING && !_exiting)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_HIDEWINDOW) != 0)
            {
                pos.flags &= ~Native.SWP_HIDEWINDOW;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private bool WantTopmost => _s.Topmost || (_s.GameMode && _inGame);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private bool PassKeyHeld()
    {
        int vk = _s.PassKey switch { "Ctrl" => 0x11, "Shift" => 0x10, _ => 0x12 /* Alt */ };
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>實際生效的穿透狀態：開穿透且沒按住解除鍵。</summary>
    private bool EffectiveClickThrough => _s.ClickThrough && !_passHeld;

    private void UpdatePassThrough()
    {
        bool held = PassKeyHeld();
        // 正在拖曳（滑鼠左鍵按著）時不要切回穿透
        if (!held && _passHeld && Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (held == _passHeld) return;
        _passHeld = held;
        Native.SetClickThrough(_hwnd, EffectiveClickThrough);
        EdgeOuter.Opacity = held ? 1 : 0.85;
        EdgeGlow.BorderThickness = new Thickness(held ? 4 : 3);
        Frame.Cursor = held ? Cursors.Hand : (_s.LockPosition ? Cursors.Arrow : Cursors.SizeAll);
    }

    private void ApplyVisuals()
    {
        Accents.Scheme = _s.ColorScheme;
        Accents.Overrides = _s.ColorOverrides;
        Topmost = WantTopmost;
        GlassHelper.Apply(this, _s.Theme, _s.TintOpacity, _s.Acrylic);
        Native.SetClickThrough(_hwnd, EffectiveClickThrough);
        Placeholder.Foreground = Palette.For(_s.Theme).Value;
        Frame.Cursor = _s.LockPosition ? Cursors.Arrow : Cursors.SizeAll;

        bool dark = _s.Theme != "Light";
        if (Fancy)
        {
            Frame.Padding = new Thickness(5, 4, 5, 4);
            EdgeOuter.BorderBrush = Rainbow(dark ? (byte)0x78 : (byte)0x90);
            EdgeInner.BorderBrush = Vertical(
                dark ? Color.FromArgb(0x48, 255, 255, 255) : Color.FromArgb(0xB0, 255, 255, 255),
                dark ? Color.FromArgb(0x08, 255, 255, 255) : Color.FromArgb(0x30, 255, 255, 255));
            EdgeGlow.BorderBrush = Rainbow(dark ? (byte)0x16 : (byte)0x20);

            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sheen.GradientStops.Add(new GradientStop(dark ? Color.FromArgb(0x16, 255, 255, 255) : Color.FromArgb(0x40, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x01, 0, 0, 0), 0.45));
            sheen.GradientStops.Add(new GradientStop(dark ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x08, 0, 0, 0), 1));
            sheen.Freeze();
            Frame.Background = sheen;
        }
        else
        {
            Frame.Padding = new Thickness(3, 2, 3, 2);
            EdgeOuter.BorderBrush = Palette.For(_s.Theme).Border;
            EdgeInner.BorderBrush = Vertical(
                dark ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x60, 255, 255, 255),
                Color.FromArgb(0x00, 255, 255, 255));
            EdgeGlow.BorderBrush = null;
            Frame.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        }
    }

    private void UpdateRoundRegion()
    {
        if (!_layered || _hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        Native.SetRoundRegion(_hwnd,
            (int)Math.Round(ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(ActualHeight * dpi.DpiScaleY),
            (int)Math.Round(8 * dpi.DpiScaleX));
    }

    private static Brush Rainbow(byte a)
    {
        var c = Accents.Frame();
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(Accents.A(c[0], a), 0));
        b.GradientStops.Add(new GradientStop(Accents.A(c[1], a), 0.35));
        b.GradientStops.Add(new GradientStop(Accents.A(c[2], a), 0.7));
        b.GradientStops.Add(new GradientStop(Accents.A(c[3], a), 1));
        b.Freeze();
        return b;
    }

    private static Brush Vertical(Color top, Color bottom)
    {
        var b = new LinearGradientBrush(top, bottom, 90);
        b.Freeze();
        return b;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        try
        {
            _svc = await Task.Run(() => new MetricsService());
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Placeholder.Text = "讀取系統資訊失敗，詳見 error.log";
            return;
        }
        _svc.SetFanSensors(_s.CpuFanSensor, _s.PumpSensor);
        RebuildCells();
        _ = UpdateLoopAsync();
        await RunLoop();
    }

    private async Task RunLoop()
    {
        while (!_exiting)
        {
            await Task.Delay(_s.IntervalMs);
            SampleResult res;
            try
            {
                res = await Task.Run(_svc!.Sample);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                continue;
            }
            ApplyResult(res);
        }
    }

    private void ApplyResult(SampleResult res)
    {
        var states = res.States;
        EvaluateAlerts(states);
        _last = states;
        UpdateGameState();

        foreach (var (id, cell) in _cells)
            if (states.TryGetValue(id, out var st)) cell.Update(st);

        string cpu = states.TryGetValue("cpu", out var c) ? c.Text : "—";
        string mem = states.TryGetValue("memused", out var m) && !double.IsNaN(m.Value) ? $"{m.Value:0}%" : "—";
        _tray.SetTooltip($"瀚誠系統監控\nCPU {cpu} · 記憶體 {mem}" + (_inGame ? "\n遊戲模式中" : ""));

        if (_detail != null && res.Detail != null && res.Detail.Category == _detail.Category)
            _detail.Refresh(res.Detail);

        _history.Add(states);

        // 全自動更新：有待安裝的新版本，且不在遊戲中，就直接安裝
        if (_autoInstallWanted && !_installing && !_inGame && _pendingUpdate != null && _s.AutoInstallUpdate)
            _ = InstallUpdateAsync(ask: false);

        if (!_positioned)
        {
            UpdateLayout();
            PlaceDefault();
        }

        // 被最小化／隱藏就立刻顯示回來；滑鼠穿透被重設也補回來
        Native.EnsureShown(_hwnd);
        Native.SetClickThrough(_hwnd, EffectiveClickThrough);
        if (WantTopmost && ++_tick % 2 == 0) Native.ReassertTopmost(_hwnd);
    }

    // ───── 遊戲模式 ─────

    /// <summary>目前是否用華麗風格：遊戲中開了「簡約外觀」就改用簡約。</summary>
    private bool Fancy => _s.Style == "Fancy" && !(_inGame && _s.GameSimple);

    private void UpdateGameState()
    {
        bool active = _s.GameMode && _game.IsGameForeground(_s, _hwnd);
        if (active != _inGame)
        {
            _inGame = active;
            if (_s.GameSimple && _s.Style == "Fancy") ApplyVisuals();
            if (_s.GameMini || (_s.GameSimple && _s.Style == "Fancy")) RebuildCells();
            ApplyModePosition();
        }
        bool want = WantTopmost;
        if (Topmost != want) Topmost = want;
    }

    // ───── 警示 ─────

    private void EvaluateAlerts(Dictionary<string, MetricState> st)
    {
        if (!_s.AlertEnabled) return;
        Check(st, "cputemp", _s.AlertCpuTemp, "CPU 溫度", " °C");
        Check(st, "gputemp", _s.AlertGpuTemp, "GPU 溫度", " °C");
        Check(st, "memused", _s.AlertMemPct, "記憶體使用率", "%");
    }

    private void Check(Dictionary<string, MetricState> st, string id, double limit, string name, string unit)
    {
        if (!st.TryGetValue(id, out var m) || double.IsNaN(m.Value) || m.Value < limit) return;
        m.Alert = true;
        if (!_s.AlertNotify) return;
        if (_alertTimes.TryGetValue(id, out var t) && (DateTime.Now - t).TotalMinutes < 5) return;
        _alertTimes[id] = DateTime.Now;
        _tray.Balloon($"{name}過高", $"{name}目前 {m.Value:0}{unit}，超過設定的 {limit:0}{unit}。");
    }

    // ───── 格子 ─────

    private List<string> VisibleIds()
    {
        var all = _s.GetOrderedIds(_svc!.Definitions);
        if (_inGame && _s.GameMini)
        {
            var g = all.Where(id => _s.GameItems.Contains(id)).ToList();
            if (g.Count > 0) return g;
        }
        return all.Where(id => !_s.Hidden.Contains(id)).ToList();
    }

    private void RebuildCells()
    {
        if (_svc == null) return;
        var pal = Palette.For(_s.Theme);
        bool horizontal = (_inGame && _s.GameMini ? _s.GameLayout : _s.Layout) != "Vertical";
        bool fancy = Fancy;
        bool dark = _s.Theme != "Light";

        Cells.Children.Clear();
        _cells.Clear();
        Cells.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;

        var ids = VisibleIds();
        for (int i = 0; i < ids.Count; i++)
        {
            var def = _svc.Definitions.First(d => d.Id == ids[i]);
            var init = _last != null && _last.TryGetValue(def.Id, out var st) ? st : new MetricState { Id = def.Id, Title = def.Title };
            var cell = new MetricCell(init, pal, horizontal, _s.Scale, !fancy && i < ids.Count - 1, fancy, dark);
            if (fancy) cell.Margin = new Thickness(2.5);
            Cells.Children.Add(cell);
            _cells[def.Id] = cell;
        }

        if (ids.Count == 0)
        {
            Placeholder.Text = "沒有選擇任何項目（右鍵 → 設定）";
            Cells.Children.Add(Placeholder);
        }
    }

    // ───── 位置與點擊 ─────

    private void RestorePosition()
    {
        if (double.IsNaN(_s.Left) || double.IsNaN(_s.Top)) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.Contains(new Point(_s.Left + 20, _s.Top + 10))) return; // 螢幕拔掉了就重新置中
        Left = _s.Left;
        Top = _s.Top;
        _positioned = true;
    }

    private void PlaceDefault()
    {
        var wa = SystemParameters.WorkArea;
        Left = Math.Round(wa.Left + (wa.Width - ActualWidth) / 2);
        Top = wa.Top + 8;
        _positioned = true;
    }

    private static MetricCell? FindCell(DependencyObject? d)
    {
        while (d != null && d is not MetricCell) d = VisualTreeHelper.GetParent(d);
        return d as MetricCell;
    }

    /// <summary>按住拖曳＝移動；沒有移動就放開＝點擊，打開該項目的詳細面板。</summary>
    private void OnFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindCell(e.OriginalSource as DependencyObject);
        double l = Left, t = Top;
        if (!_s.LockPosition)
        {
            try { DragMove(); } catch { }
        }
        bool moved = Math.Abs(Left - l) > 2 || Math.Abs(Top - t) > 2;
        if (moved)
        {
            SnapToEdges();
            SavePosition(customGame: true);
        }
        else if (cell != null)
        {
            ToggleDetail(cell);
        }
    }

    /// <summary>可用範圍（WPF 單位）。遊戲中通常是全螢幕，用整個螢幕而不是扣掉工作列的範圍。</summary>
    private Rect WorkAreaDip(bool? fullScreen = null)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd);
        var wa = fullScreen ?? _inGame ? screen.Bounds : screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    /// <summary>記住目前位置：遊戲中記到遊戲位置，平常記到一般位置。</summary>
    private void SavePosition(bool customGame)
    {
        _positioned = true;
        if (_inGame)
        {
            _s.GameLeft = Left;
            _s.GameTop = Top;
            if (customGame) _s.GameAnchor = "Custom";
        }
        else
        {
            _s.Left = Left;
            _s.Top = Top;
        }
        _s.Save();
    }

    /// <summary>進出遊戲時切換到各自的位置。</summary>
    private void ApplyModePosition()
    {
        UpdateLayout();
        if (_inGame)
        {
            if (_s.GameAnchor != "Custom") PlaceAnchor(_s.GameAnchor);
            else if (!double.IsNaN(_s.GameLeft) && !double.IsNaN(_s.GameTop))
            {
                Left = _s.GameLeft;
                Top = _s.GameTop;
            }
        }
        else if (!double.IsNaN(_s.Left) && !double.IsNaN(_s.Top))
        {
            Left = _s.Left;
            Top = _s.Top;
        }
    }

    public static readonly (string Key, string Name)[] Anchors =
    {
        ("TopLeft", "左上角"), ("TopCenter", "上方置中"), ("TopRight", "右上角"),
        ("BottomLeft", "左下角"), ("BottomCenter", "下方置中"), ("BottomRight", "右下角"),
    };

    private void PlaceAnchor(string anchor)
    {
        var wa = WorkAreaDip();
        double w = ActualWidth, h = ActualHeight;
        const double m = 6;
        double cx = wa.Left + (wa.Width - w) / 2;
        (double x, double y) = anchor switch
        {
            "TopLeft" => (wa.Left + m, wa.Top + m),
            "TopRight" => (wa.Right - w - m, wa.Top + m),
            "BottomLeft" => (wa.Left + m, wa.Bottom - h - m),
            "BottomCenter" => (cx, wa.Bottom - h - m),
            "BottomRight" => (wa.Right - w - m, wa.Bottom - h - m),
            _ => (cx, wa.Top + m),
        };
        Left = Math.Round(x);
        Top = Math.Round(y);
    }

    /// <summary>選單的「移到…」：遊戲中會記成遊戲位置的固定方位。</summary>
    private void MoveTo(string anchor)
    {
        PlaceAnchor(anchor);
        if (_inGame) _s.GameAnchor = anchor;
        SavePosition(customGame: false);
    }

    private void SnapToEdges()
    {
        var wa = WorkAreaDip();
        const double th = 16;
        if (Math.Abs(Left - wa.Left) < th) Left = wa.Left;
        if (Math.Abs(Left + ActualWidth - wa.Right) < th) Left = wa.Right - ActualWidth;
        if (Math.Abs(Top - wa.Top) < th) Top = wa.Top;
        if (Math.Abs(Top + ActualHeight - wa.Bottom) < th) Top = wa.Bottom - ActualHeight;
    }

    private void ToggleDetail(MetricCell cell)
    {
        if (_svc == null) return;
        string cat = MetricsService.CategoryOf(cell.Id);
        if (_detail != null)
        {
            bool same = _detail.Category == cat;
            _detail.Close();
            if (same) return;
        }

        var dw = new DetailWindow(cat, _s) { Topmost = Topmost };
        dw.Closed += (_, _) =>
        {
            if (_detail == dw)
            {
                _detail = null;
                if (_svc != null) _svc.DetailWanted = null;
            }
        };
        _detail = dw;
        _svc.DetailWanted = cat;

        var p1 = cell.TranslatePoint(new Point(0, 0), this);
        var anchor = new Rect(Left + p1.X, Top + p1.Y, cell.ActualWidth, cell.ActualHeight);
        dw.ShowNear(anchor, WorkAreaDip());
    }

    // ───── 選單 ─────

    private void ShowMenu()
    {
        var m = new ContextMenu { PlacementTarget = Frame, Placement = PlacementMode.MousePoint };
        if (_pendingUpdate != null)
        {
            m.Items.Add(Item($"⬆ 更新到 {_pendingUpdate.Version}", () => _ = InstallUpdateAsync()));
            m.Items.Add(Sep());
        }
        m.Items.Add(Item("設定…", OpenSettings));
        m.Items.Add(Item("歷史紀錄…", OpenHistory));
        m.Items.Add(Sep());
        m.Items.Add(Item("遊戲模式", () => Change(() => _s.GameMode = !_s.GameMode), _s.GameMode));
        m.Items.Add(Item("遊戲中簡約外觀", () => Change(() => _s.GameSimple = !_s.GameSimple), _s.GameSimple));
        m.Items.Add(Item("永遠置頂", () => Change(() => _s.Topmost = !_s.Topmost), _s.Topmost));
        m.Items.Add(Item("滑鼠穿透", ToggleClickThrough, _s.ClickThrough));
        m.Items.Add(Item("鎖定位置", () => Change(() => _s.LockPosition = !_s.LockPosition), _s.LockPosition));
        m.Items.Add(Sep());
        m.Items.Add(Item("華麗風格", () => Change(() => _s.Style = "Fancy"), _s.Style == "Fancy"));
        m.Items.Add(Item("簡約風格", () => Change(() => _s.Style = "Simple"), _s.Style == "Simple"));
        m.Items.Add(Item("橫式", () => Change(() => _s.Layout = "Horizontal"), _s.Layout == "Horizontal"));
        m.Items.Add(Item("直式", () => Change(() => _s.Layout = "Vertical"), _s.Layout == "Vertical"));
        m.Items.Add(Sep());
        string prefix = _inGame ? "遊戲位置：" : "移到";
        foreach (var (key, name) in Anchors)
        {
            string k = key;
            m.Items.Add(Item(prefix + name, () => MoveTo(k), _inGame && _s.GameAnchor == key));
        }
        m.Items.Add(Sep());
        m.Items.Add(Item("檢查更新", () => _ = CheckUpdateAsync(silent: false)));
        m.Items.Add(Item("結束", ExitApp));
        m.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action, bool isChecked = false)
    {
        var mi = new MenuItem { Header = header, IsChecked = isChecked };
        mi.Click += (_, _) => action();
        return mi;
    }

    private Separator Sep() => new() { Style = (Style)FindResource("MenuSep") };

    private void Change(Action apply)
    {
        apply();
        OnSettingsChanged();
    }

    private void ToggleClickThrough()
    {
        _s.ClickThrough = !_s.ClickThrough;
        OnSettingsChanged();
        if (_s.ClickThrough)
            _tray.Balloon("滑鼠穿透已開啟", $"按住 {_s.PassKey} 可以暫時點選小工具；要關閉穿透請在系統匣圖示上按右鍵。");
    }

    /// <summary>設定有任何變動時呼叫：套用並存檔。</summary>
    public void OnSettingsChanged()
    {
        // 切換滑鼠穿透需要換成另一種視窗，重建主視窗
        if (_s.ClickThrough != _layered)
        {
            _s.Save();
            Recreate();
            return;
        }
        ThemeManager.Apply(_s.Theme);
        ApplyVisuals();
        _svc?.SetFanSensors(_s.CpuFanSensor, _s.PumpSensor);
        RebuildCells();
        _s.Save();
    }

    private void OpenSettings()
    {
        if (_svc == null) return;
        if (_settingsWin != null)
        {
            _settingsWin.Activate();
            return;
        }
        var svc = _svc;
        _settingsWin = new SettingsWindow(_s, _svc.Definitions, OnSettingsChanged, OpenHistory, () => CheckUpdateAsync(silent: false),
                                          () => svc.LastHardware);
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show();
    }

    private void OpenHistory()
    {
        _history.Flush();
        if (_historyWin != null)
        {
            _historyWin.Activate();
            return;
        }
        _historyWin = new HistoryWindow(_s);
        _historyWin.Closed += (_, _) => _historyWin = null;
        _historyWin.Show();
    }

    // ───── 更新 ─────

    /// <summary>檢查更新，回傳給設定視窗顯示的狀態文字。</summary>
    private async Task<string> CheckUpdateAsync(bool silent)
    {
        if (string.IsNullOrWhiteSpace(_s.UpdateRepo))
        {
            const string msg = "尚未設定 GitHub 儲存庫（設定 → 自動更新）。";
            if (!silent) _tray.Balloon("檢查更新", msg);
            return msg;
        }
        try
        {
            var info = await Updater.CheckAsync(_s.UpdateRepo);
            if (info == null)
            {
                string msg = $"目前已是最新版本（{Updater.Current.ToString(3)}）。";
                if (!silent) _tray.Balloon("檢查更新", msg);
                return msg;
            }
            _pendingUpdate = info;
            if (_s.AutoInstallUpdate)
            {
                // 交給 ApplyResult：不在遊戲中時就自動安裝
                _autoInstallWanted = true;
                string auto = _inGame
                    ? $"有新版本 {info.Version}，離開遊戲後會自動更新。"
                    : $"有新版本 {info.Version}，正在自動更新…";
                return auto;
            }
            string found = $"有新版本 {info.Version}，在小工具上按右鍵即可更新。";
            _tray.Balloon("瀚誠系統監控有新版本", found);
            return found;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            string msg = $"檢查更新失敗：{ex.Message}";
            if (!silent) _tray.Balloon("檢查更新", msg);
            return msg;
        }
    }

    /// <summary>啟動 20 秒後檢查一次，之後每 6 小時檢查一次。</summary>
    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (!_exiting)
        {
            // 自動安裝失敗過的話，下一輪會重新檢查並再試一次
            if ((_s.AutoCheckUpdate || _s.AutoInstallUpdate) && (_pendingUpdate == null || _s.AutoInstallUpdate)
                && !string.IsNullOrWhiteSpace(_s.UpdateRepo))
                await CheckUpdateAsync(silent: true);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private async Task InstallUpdateAsync(bool ask = true)
    {
        if (_pendingUpdate == null || _installing) return;
        var info = _pendingUpdate;
        if (ask)
        {
            string notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n更新內容：\n{info.Notes}";
            if (MessageBox.Show($"要更新到 {info.Version} 嗎？程式會先關閉，安裝完成後自動重新開啟。{notes}",
                    "瀚誠系統監控", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        _installing = true;
        _autoInstallWanted = false;
        try
        {
            _tray.Balloon(ask ? "正在下載更新" : "瀚誠系統監控自動更新中", $"版本 {info.Version}，安裝完成後會自動重新開啟。");
            await Updater.InstallAsync(info);
            ExitApp();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (ask)
                MessageBox.Show($"更新失敗：{ex.Message}", "瀚誠系統監控", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                _tray.Balloon("自動更新失敗", $"{ex.Message}\n可以稍後在小工具上按右鍵手動更新。");
        }
        finally
        {
            _installing = false;
        }
    }

    /// <summary>關掉目前的主視窗，用最新設定開一個新的（滑鼠穿透切換時用）。</summary>
    private void Recreate()
    {
        if (_recreating) return;
        _recreating = true;
        _passKeyTimer?.Stop();
        bool reopenSettings = _settingsWin != null;
        _exiting = true;
        _history.Flush();
        _tray.Dispose();
        _svc?.Dispose();
        _settingsWin?.Close();
        _detail?.Close();
        _historyWin?.Close();

        var w = new MainWindow(_s);
        Application.Current.MainWindow = w;
        if (reopenSettings) w.Loaded += (_, _) => w.OpenSettingsWhenReady();
        w.Show();
        Close();
    }

    /// <summary>等資料服務準備好再開設定視窗。</summary>
    public async void OpenSettingsWhenReady()
    {
        for (int i = 0; i < 300 && _svc == null; i++) await Task.Delay(100);
        OpenSettings();
    }

    private void ExitApp()
    {
        if (_exiting || _recreating) return;
        _exiting = true;
        _passKeyTimer?.Stop();
        _history.Flush();
        _s.Save();
        _tray.Dispose();
        _svc?.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}

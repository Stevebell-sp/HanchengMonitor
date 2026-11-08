using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassMonitor;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly IReadOnlyList<MetricDef> _defs;
    private readonly Action _changed;
    private readonly Action _openHistory;
    private readonly Func<Task<string>> _checkUpdate;
    private bool _loading = true;

    // 拖曳排序
    private FrameworkElement? _dragRow;
    private FrameworkElement? _dragHandle;

    public SettingsWindow(AppSettings s, IReadOnlyList<MetricDef> defs, Action changed, Action openHistory, Func<Task<string>> checkUpdate)
    {
        _s = s;
        _defs = defs;
        _changed = changed;
        _openHistory = openHistory;
        _checkUpdate = checkUpdate;
        InitializeComponent();
        VersionText.Text = $"版本 {Updater.Current.ToString(3)}";

        foreach (var (key, name) in Accents.Schemes)
        {
            var rb = new RadioButton
            {
                Content = name,
                Tag = key,
                GroupName = "scheme",
                Style = (Style)FindResource("Seg"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            rb.Checked += OnAnyChanged;
            SchemePanel.Children.Add(rb);
        }

        foreach (var (key, name) in new[] { ("Custom", "自訂（拖曳）") }.Concat(MainWindow.Anchors))
        {
            var rb = new RadioButton
            {
                Content = name,
                Tag = key,
                GroupName = "ganchor",
                Style = (Style)FindResource("Seg"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            rb.Checked += OnAnyChanged;
            GameAnchorPanel.Children.Add(rb);
        }

        LoadValues();
        _loading = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyGlass();
    }

    // 設定視窗字比較多，底色固定濃一點才看得清楚
    private void ApplyGlass() => GlassHelper.Apply(this, _s.Theme, Math.Max(0.72, _s.TintOpacity), true);

    private void LoadValues()
    {
        StyleFancy.IsChecked = _s.Style != "Simple";
        StyleSimple.IsChecked = _s.Style == "Simple";
        ThemeDark.IsChecked = _s.Theme != "Light";
        ThemeLight.IsChecked = _s.Theme == "Light";
        foreach (RadioButton rb in SchemePanel.Children) rb.IsChecked = (string)rb.Tag == _s.ColorScheme;
        GlassAcrylic.IsChecked = _s.Acrylic;
        GlassBlur.IsChecked = !_s.Acrylic;
        TintSlider.Value = _s.TintOpacity;
        ScaleSlider.Value = _s.Scale;

        LayoutH.IsChecked = _s.Layout != "Vertical";
        LayoutV.IsChecked = _s.Layout == "Vertical";
        Int05.IsChecked = _s.IntervalMs == 500;
        Int2.IsChecked = _s.IntervalMs == 2000;
        Int1.IsChecked = _s.IntervalMs != 500 && _s.IntervalMs != 2000;
        LockBox.IsChecked = _s.LockPosition;
        TopmostBox.IsChecked = _s.Topmost;
        ClickBox.IsChecked = _s.ClickThrough;
        PassAlt.IsChecked = _s.PassKey == "Alt";
        PassCtrl.IsChecked = _s.PassKey == "Ctrl";
        PassShift.IsChecked = _s.PassKey == "Shift";
        AutoBox.IsChecked = _s.AutoStart;

        GameBox.IsChecked = _s.GameMode;
        FullscreenBox.IsChecked = _s.GameDetectFullscreen;
        GameMiniBox.IsChecked = _s.GameMini;
        GameLayoutH.IsChecked = _s.GameLayout != "Vertical";
        GameLayoutV.IsChecked = _s.GameLayout == "Vertical";
        foreach (RadioButton rb in GameAnchorPanel.Children) rb.IsChecked = (string)rb.Tag == _s.GameAnchor;
        GameListBox.Text = string.Join(", ", _s.GameProcesses);

        AlertBox.IsChecked = _s.AlertEnabled;
        CpuLimitSlider.Value = _s.AlertCpuTemp;
        GpuLimitSlider.Value = _s.AlertGpuTemp;
        MemLimitSlider.Value = _s.AlertMemPct;
        NotifyBox.IsChecked = _s.AlertNotify;

        HistoryBox.IsChecked = _s.HistoryEnabled;
        Days7.IsChecked = _s.HistoryDays == 7;
        Days90.IsChecked = _s.HistoryDays == 90;
        Days30.IsChecked = _s.HistoryDays is not (7 or 90);

        UpdateRepoBox.Text = _s.UpdateRepo;
        AutoUpdateBox.IsChecked = _s.AutoCheckUpdate;

        UpdateLabels();
        BuildItems();
    }

    private void UpdateLabels()
    {
        TintLabel.Text = $"{TintSlider.Value * 100:0}%";
        ScaleLabel.Text = $"{ScaleSlider.Value * 100:0}%";
        CpuLimitLabel.Text = $"{CpuLimitSlider.Value:0} °C";
        GpuLimitLabel.Text = $"{GpuLimitSlider.Value:0} °C";
        MemLimitLabel.Text = $"{MemLimitSlider.Value:0}%";
    }

    private void OnAnyChanged(object sender, RoutedEventArgs e) => Commit();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Commit();

    private void OnTextChanged(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (_loading) return;
        _s.Style = StyleSimple.IsChecked == true ? "Simple" : "Fancy";
        _s.Theme = ThemeLight.IsChecked == true ? "Light" : "Dark";
        var scheme = SchemePanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (scheme != null) _s.ColorScheme = (string)scheme.Tag;
        _s.Acrylic = GlassAcrylic.IsChecked == true;
        _s.TintOpacity = Math.Round(TintSlider.Value, 2);
        _s.Scale = Math.Round(ScaleSlider.Value, 2);

        _s.Layout = LayoutV.IsChecked == true ? "Vertical" : "Horizontal";
        _s.IntervalMs = Int05.IsChecked == true ? 500 : Int2.IsChecked == true ? 2000 : 1000;
        _s.LockPosition = LockBox.IsChecked == true;
        _s.Topmost = TopmostBox.IsChecked == true;
        _s.ClickThrough = ClickBox.IsChecked == true;
        _s.PassKey = PassCtrl.IsChecked == true ? "Ctrl" : PassShift.IsChecked == true ? "Shift" : "Alt";
        bool auto = AutoBox.IsChecked == true;
        if (auto != _s.AutoStart)
        {
            _s.AutoStart = auto;
            AutoStart.Set(auto);
        }

        _s.GameMode = GameBox.IsChecked == true;
        _s.GameDetectFullscreen = FullscreenBox.IsChecked == true;
        _s.GameMini = GameMiniBox.IsChecked == true;
        _s.GameLayout = GameLayoutV.IsChecked == true ? "Vertical" : "Horizontal";
        var anchor = GameAnchorPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (anchor != null) _s.GameAnchor = (string)anchor.Tag;
        _s.GameProcesses = GameListBox.Text
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(GameDetector.Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _s.AlertEnabled = AlertBox.IsChecked == true;
        _s.AlertCpuTemp = CpuLimitSlider.Value;
        _s.AlertGpuTemp = GpuLimitSlider.Value;
        _s.AlertMemPct = MemLimitSlider.Value;
        _s.AlertNotify = NotifyBox.IsChecked == true;

        _s.HistoryEnabled = HistoryBox.IsChecked == true;
        _s.HistoryDays = Days7.IsChecked == true ? 7 : Days90.IsChecked == true ? 90 : 30;

        _s.UpdateRepo = UpdateRepoBox.Text.Trim();
        _s.AutoCheckUpdate = AutoUpdateBox.IsChecked == true;

        UpdateLabels();
        _changed();
        ApplyGlass();
        BuildItems(); // 配色可能變了，更新色點
    }

    // ───── 顯示項目：開關、顏色、遊戲精簡、拖曳排序 ─────

    private void BuildItems()
    {
        if (_dragRow != null) return; // 拖曳中不要重建
        ItemList.Children.Clear();
        GameItemGrid.Children.Clear();
        foreach (var id in _s.GetOrderedIds(_defs))
        {
            var def = _defs.First(d => d.Id == id);
            ItemList.Children.Add(BuildRow(def));

            string gid = id;
            var gcb = new CheckBox
            {
                Content = def.Title,
                IsChecked = _s.GameItems.Contains(id),
                Style = (Style)FindResource("Switch"),
                Margin = new Thickness(0, 3, 8, 3),
            };
            gcb.Checked += (_, _) => { if (!_s.GameItems.Contains(gid)) _s.GameItems.Add(gid); _changed(); };
            gcb.Unchecked += (_, _) => { _s.GameItems.Remove(gid); _changed(); };
            GameItemGrid.Children.Add(gcb);
        }
    }

    private FrameworkElement BuildRow(MetricDef def)
    {
        string id = def.Id;
        var row = new Border { Tag = id, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 拖曳把手
        var handle = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            ToolTip = "按住上下拖曳調整順序",
            Child = new TextBlock { Text = "≡", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ((TextBlock)handle.Child).SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        handle.MouseLeftButtonDown += (_, e) => StartDrag(row, handle, e);
        handle.MouseMove += (_, e) => OnDragMove(e);
        handle.MouseLeftButtonUp += (_, _) => EndDrag();
        handle.LostMouseCapture += (_, _) => EndDrag();

        var cb = new CheckBox
        {
            Content = def.Title,
            IsChecked = !_s.Hidden.Contains(id),
            Style = (Style)FindResource("Switch"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Checked += (_, _) => { _s.Hidden.Remove(id); _changed(); };
        cb.Unchecked += (_, _) => { if (!_s.Hidden.Contains(id)) _s.Hidden.Add(id); _changed(); };

        // 顏色
        var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Accents.For(id)) };
        if (_s.ColorOverrides.ContainsKey(id))
        {
            dot.StrokeThickness = 1.5;
            dot.Stroke = Brushes.White;
        }
        var colorBtn = new Button { Content = dot, Style = (Style)FindResource("IconButton"), ToolTip = "自訂顏色" };
        colorBtn.Click += (_, _) => ShowColorPicker(colorBtn, id);

        Grid.SetColumn(cb, 1);
        Grid.SetColumn(colorBtn, 2);
        g.Children.Add(handle);
        g.Children.Add(cb);
        g.Children.Add(colorBtn);
        row.Child = g;
        return row;
    }

    private void StartDrag(FrameworkElement row, FrameworkElement handle, MouseButtonEventArgs e)
    {
        _dragRow = row;
        _dragHandle = handle;
        row.Opacity = 0.75;
        ((Border)row).SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        handle.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 拖曳中：滑鼠越過相鄰列的中線就交換位置。
    /// 移動的是「相鄰那一列」而不是被拖的這一列，這樣把手才不會失去滑鼠擷取。
    /// </summary>
    private void OnDragMove(MouseEventArgs e)
    {
        if (_dragRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        double y = e.GetPosition(ItemList).Y;
        var kids = ItemList.Children;
        int i = kids.IndexOf(_dragRow);
        if (i < 0) return;

        if (i > 0)
        {
            var above = (FrameworkElement)kids[i - 1];
            double mid = above.TranslatePoint(new Point(0, 0), ItemList).Y + above.ActualHeight / 2;
            if (y < mid)
            {
                kids.RemoveAt(i - 1);
                kids.Insert(i, above);
                return;
            }
        }
        if (i < kids.Count - 1)
        {
            var below = (FrameworkElement)kids[i + 1];
            double mid = below.TranslatePoint(new Point(0, 0), ItemList).Y + below.ActualHeight / 2;
            if (y > mid)
            {
                kids.RemoveAt(i + 1);
                kids.Insert(i, below);
            }
        }
    }

    private void EndDrag()
    {
        if (_dragRow == null) return;
        var row = _dragRow;
        var handle = _dragHandle;
        _dragRow = null;
        _dragHandle = null;
        row.Opacity = 1;
        ((Border)row).Background = Brushes.Transparent;
        if (handle?.IsMouseCaptured == true) handle.ReleaseMouseCapture();

        var newOrder = ItemList.Children.OfType<FrameworkElement>().Select(r => (string)r.Tag).ToList();
        if (!newOrder.SequenceEqual(_s.GetOrderedIds(_defs)))
        {
            _s.Order = newOrder;
            _changed();
        }
    }

    private void ShowColorPicker(Button anchor, string id)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        var wrap = new WrapPanel { Width = 6 * 30 };
        foreach (var c in Accents.Swatches)
        {
            var sw = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(c),
                Cursor = Cursors.Hand,
            };
            sw.MouseLeftButtonUp += (_, _) =>
            {
                _s.ColorOverrides[id] = Accents.ToHex(c);
                popup.IsOpen = false;
                _changed();
                BuildItems();
            };
            wrap.Children.Add(sw);
        }
        var reset = new Button { Content = "恢復預設顏色", Margin = new Thickness(3, 6, 3, 0), Style = (Style)FindResource("GlassButton") };
        reset.Click += (_, _) =>
        {
            _s.ColorOverrides.Remove(id);
            popup.IsOpen = false;
            _changed();
            BuildItems();
        };
        var panel = new StackPanel();
        panel.Children.Add(wrap);
        panel.Children.Add(reset);
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "MenuBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        popup.Child = border;
        popup.IsOpen = true;
    }

    // ───── 其他按鈕 ─────

    private void History_Click(object sender, RoutedEventArgs e) => _openHistory();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        UpdateStatus.Text = "檢查中…";
        UpdateStatus.Text = await _checkUpdate();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        double left = _s.Left, top = _s.Top;
        bool hadAuto = _s.AutoStart;
        _s.CopyFrom(new AppSettings());
        _s.Left = left;
        _s.Top = top;
        if (hadAuto) AutoStart.Set(false);

        _loading = true;
        LoadValues();
        _loading = false;
        _changed();
        ApplyGlass();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

using Seein.Core;
using Seein.Core.Apps;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using IInputElement = System.Windows.IInputElement;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Seein.Tray;

/// <summary>
/// Application management: rename, icon, colour and merge relationship in one place.  Applications are
/// listed by merge relationship (a main application expands into the processes merged into it) and
/// ordered by total recorded runtime.
/// </summary>
public partial class AppListView : System.Windows.Controls.UserControl
{
    private enum SortColumn { Name, Total, Today, Color }

    private static readonly string[] Palette =
    [
        // Vivid categorical colors with at least 3:1 contrast on both app surface themes.
        "#EC3D3D", "#D05D36", "#B3712F", "#997D28", "#848422", "#708A24",
        "#598E25", "#409126", "#269426", "#269341", "#26925C", "#259075",
        "#258E8E", "#2C8AA9", "#3682CF", "#557AE7", "#7272E6", "#8A6AE8",
        "#A35DE9", "#C340EF", "#D537D5", "#DC39B4", "#E43B8F", "#E93D68"
    ];

    private readonly ObservableCollection<AppRow> _rows = [];
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer;
    private bool _rendering;
    private bool _hasLoaded;
    private CancellationTokenSource? _iconLoad;
    private const string SystemGroupKey = "__system_group__";
    private const string BackgroundGroupKey = "__background_group__";
    private const string TestingGroupKey = "__testing_group__";
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<AppCatalogRow> _catalog = [];
    private AppCatalogResult? _catalogResult;
    private AppRow? _editing;
    private string _mode = "rename";
    private bool _initialized;
    private SortColumn _sortColumn = SortColumn.Total;
    private bool _sortDescending = true;

    public AppListView()
    {
        InitializeComponent();
        AppList.ItemsSource = _rows;
        BuildSwatches();
        _searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Render(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _iconLoad?.Cancel(); };
        _initialized = true;
        UpdateSortHeaders();
    }

    public event Action? DataRequested;
    public event Action<string, string, string?>? MetaRequested;

    public void RequestData() => DataRequested?.Invoke();

    public void BeginLoad()
    {
        if (_hasLoaded) return;
        _iconLoad?.Cancel();
        CatalogContent.Visibility = Visibility.Collapsed;
        CatalogLoadPanel.Show("正在加载应用列表", "正在读取应用和进程归并记录。", busy: true);
    }

    public void ShowLoadError(string message)
    {
        if (_hasLoaded) { StatusText.Text = "当前显示上次读取的应用列表。" + message; StatusText.Visibility = Visibility.Visible; return; }
        CatalogContent.Visibility = Visibility.Collapsed;
        CatalogLoadPanel.Show("应用列表加载失败", message, busy: false);
    }

    public void Load(AppCatalogResult result)
    {
        var first = !_hasLoaded; _hasLoaded = true;
        StatusText.Visibility = Visibility.Collapsed;
        _catalog = result.Rows;
        _catalogResult = result;
        CatalogContent.Visibility = Visibility.Visible;
        Render();
        CatalogLoadPanel.Hide();
        PageLoadPanel.Reveal(CatalogContent, animate: first && IsVisible);
    }

    private void Render()
    {
        if (_rendering || CatalogContent.Visibility != Visibility.Visible) return;
        _rendering = true;
        try
        {
            RenderCore();
            _ = LoadIconsAsync();
        }
        finally { _rendering = false; }
    }

    private async Task LoadIconsAsync()
    {
        _iconLoad?.Cancel();
        if (!IsVisible) return;
        using var loading = new CancellationTokenSource();
        _iconLoad = loading;
        var rows = _rows.Where(row => !row.IsGroup).ToArray();
        try
        {
            // Extracting Windows file icons can wait on disk or shell extensions.
            // Decorate the rendered rows in batches without blocking input or the loader.
            foreach (var batch in rows.Chunk(12))
            {
                var icons = await Task.Run(() => batch.Select(row =>
                {
                    loading.Token.ThrowIfCancellationRequested();
                    return AppIconCache.Get(row.IconPath, row.ExePath);
                }).ToArray(), loading.Token);
                if (loading.IsCancellationRequested) return;
                for (var i = 0; i < batch.Length; i++) batch[i].Icon = icons[i];
            }
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_iconLoad, loading)) _iconLoad = null;
        }
    }

    private void RenderCore()
    {
        var showHidden = ShowHiddenToggle.IsChecked == true;
        var query = SearchBox.Text.Trim();
        bool Matches(string? value) => query.Length > 0 && !string.IsNullOrEmpty(value) &&
            value.Contains(query, StringComparison.OrdinalIgnoreCase);
        bool MatchesRow(AppCatalogRow row) => Matches(row.Name) || Matches(row.Key) ||
            Matches(row.ExePath) || Matches(row.Category);
        bool MatchesMember(AppCatalogMember member) => Matches(member.Key) || Matches(member.ExePath) ||
            Matches(member.Name) || Matches(System.IO.Path.GetFileName(member.Key)) ||
            (member.Children?.Any(MatchesMember) ?? false);
        bool MatchesAnything(AppCatalogRow row) => query.Length == 0 || MatchesRow(row) ||
            row.Members?.Any(MatchesMember) == true;

        var all = _catalog.Where(row => showHidden || !row.Hidden).ToArray();
        var backgroundRows = all.Where(row => row.Background).Where(MatchesAnything)
            .OrderByDescending(row => row.TotalSeconds).ToArray();
        var testingRows = all.Where(row => row.MergedInto == AppStatKeys.Testing).Where(MatchesAnything)
            .OrderByDescending(row => row.TotalSeconds).ToArray();
        var systemRows = all.Where(row => row.System && !row.Background && row.MergedInto != AppStatKeys.Testing).Where(MatchesAnything)
            .OrderByDescending(row => row.TotalSeconds).ToArray();
        var appRows = all.Where(row => !row.System && !row.Background && row.MergedInto != AppStatKeys.Testing)
            .Where(MatchesAnything).ToList();

        // "Colour" sorting groups applications that visually share a colour: twelve hue buckets,
        // with the busiest bucket first so the ones sharing a colour sit at the top together.
        var hueBuckets = appRows.GroupBy(row => HueBucket(row.Color))
            .ToDictionary(group => group.Key, group => group.Count());
        IEnumerable<AppCatalogRow> orderedRows = _sortColumn switch
        {
            SortColumn.Name => _sortDescending
                ? appRows.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal)
                : appRows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal),
            SortColumn.Today => _sortDescending
                ? appRows.OrderByDescending(row => row.SecondsToday).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                : appRows.OrderBy(row => row.SecondsToday).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            SortColumn.Color => _sortDescending
                ? appRows.OrderByDescending(row => hueBuckets[HueBucket(row.Color)]).ThenBy(row => HueBucket(row.Color))
                    .ThenBy(row => row.Color).ThenByDescending(row => row.TotalSeconds)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                : appRows.OrderBy(row => hueBuckets[HueBucket(row.Color)]).ThenBy(row => HueBucket(row.Color))
                    .ThenBy(row => row.Color).ThenByDescending(row => row.TotalSeconds)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            _ => _sortDescending
                ? appRows.OrderByDescending(row => row.TotalSeconds).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                : appRows.OrderBy(row => row.TotalSeconds).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        };

        _rows.Clear();
        AddGroup(BackgroundGroupKey, "后台默认进程", backgroundRows, query,
            Color.FromRgb(0x70, 0x7A, 0x88), MatchesRow, MatchesMember, alwaysShow: true);
        AddGroup(SystemGroupKey, "系统进程", systemRows, query,
            Color.FromRgb(0x5B, 0x63, 0x70), MatchesRow, MatchesMember, alwaysShow: true);
        AddGroup(TestingGroupKey, "测试进程", testingRows, query,
            Color.FromRgb(0x71, 0x57, 0xA8), MatchesRow, MatchesMember, alwaysShow: true);
        foreach (var row in orderedRows) AppendApp(row, 0, "main", query, MatchesRow, MatchesMember);
    }

    /// <summary>Small vector icon for a pinned process classification.</summary>
    private static ImageSource CategoryGlyph(Color color, string key)
    {
        var stacked = key == BackgroundGroupKey;
        var data = key switch
        {
            BackgroundGroupKey => "M2,2 H16 V6 H2 Z M4,7.5 H16 V11.5 H4 Z M6,13 H16 V17 H6 Z",
            TestingGroupKey => "M5,2 H12 M7,2 V7 L3,14 Q2,16 4,16 H14 Q16,16 15,14 L11,7 V2 M5,12 H13",
            _ => "M4,4 H14 V14 H4 Z M7,7 H11 V11 H7 Z M7,1 V4 M11,1 V4 M7,14 V17 M11,14 V17 M1,7 H4 M1,11 H4 M14,7 H17 M14,11 H17"
        };
        var brush = new SolidColorBrush(color);
        var pen = stacked ? null : new System.Windows.Media.Pen(brush, 1.4) { LineJoin = PenLineJoin.Round };
        var drawing = new GeometryDrawing(stacked ? brush : null, pen, Geometry.Parse(data));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }


    private void AddGroup(string key, string title, IReadOnlyList<AppCatalogRow> rows, string query, Color color,
        Func<AppCatalogRow, bool> matchesRow, Func<AppCatalogMember, bool> matchesMember, bool alwaysShow = false)
    {
        if (rows.Count == 0 && (!alwaysShow || query.Length > 0)) return;
        var testing = key == TestingGroupKey;
        var expanded = rows.Count > 0 && (_expanded.Contains(key) || query.Length > 0);
        var processes = rows.Sum(row => Math.Max(1, row.Members?.Count ?? 1));
        _rows.Add(new AppRow
        {
            Key = key,
            Name = title,
            Icon = CategoryGlyph(color, key),
            Subtitle = rows.Count == 0
                ? "暂无进程 · 可通过「归并」添加"
                : $"{rows.Count} 个应用 · {processes} 个进程" + (testing ? "" : " · 不计入应用时长"),
            Category = title,
            TotalText = AppFormat.LongDuration(rows.Sum(row => row.TotalSeconds)),
            Duration = AppFormat.Duration(rows.Sum(row => row.SecondsToday)),
            ColorBrush = new SolidColorBrush(color),
            MergeText = string.Empty,
            HideText = string.Empty,
            IsGroup = true,
            ChevronVisibility = rows.Count > 0 ? Visibility.Visible : Visibility.Hidden,
            ChevronGlyph = expanded ? "\uE70D" : "\uE76C",
            Indent = new Thickness(0),
            NameSize = 13
        });
        if (!expanded) return;
        foreach (var row in rows)
        {
            if (query.Length > 0 && !matchesRow(row) && row.Members?.Any(matchesMember) != true) continue;
            AppendApp(row, 26, "bucket", query, matchesRow, matchesMember);
        }
    }

    /// <summary>Adds an application and, when expanded, its processes (and their own processes one level deeper).</summary>
    private void AppendApp(AppCatalogRow row, double indent, string kind, string query,
        Func<AppCatalogRow, bool> matchesRow, Func<AppCatalogMember, bool> matchesMember)
    {
        var members = row.Members ?? [];
        var rowMatches = matchesRow(row);
        var matchingMembers = members.Where(matchesMember).ToArray();
        var visibleMembers = query.Length > 0 && !rowMatches ? matchingMembers : members;
        var hasChildren = members.Count > 1;
        var expanded = hasChildren && (_expanded.Contains(row.Key) ||
            (query.Length > 0 && !rowMatches && matchingMembers.Length > 0));
        _rows.Add(MakeRow(row, row.TotalSeconds, row.SecondsToday, kind, hasChildren, expanded, indent, members, row.Hidden,
            secondLevel: kind == "bucket" || indent > 0));
        if (!expanded) return;
        foreach (var member in visibleMembers.Where(item => !string.Equals(item.Key, row.Key, StringComparison.Ordinal)))
        {
            var nested = member.Children is { Count: > 1 };
            var memberExpanded = nested && _expanded.Contains(member.Key);
            _rows.Add(MemberRow(row, member, indent + 26, memberExpanded));
            if (!memberExpanded) continue;
            foreach (var child in member.Children!)
                _rows.Add(LeafRow(row, member, child, indent + 52));
        }
    }

    private void UpdateSortHeaders()
    {
        static string Label(string title, SortColumn column, SortColumn active, bool descending) =>
            column == active ? $"{title} {(descending ? "↓" : "↑")}" : title;
        NameSortButton.Content = Label("应用名称", SortColumn.Name, _sortColumn, _sortDescending);
        TotalSortButton.Content = Label("累计时长", SortColumn.Total, _sortColumn, _sortDescending);
        TodaySortButton.Content = Label("今日时长", SortColumn.Today, _sortColumn, _sortDescending);
        ColorSortButton.Content = Label("颜色", SortColumn.Color, _sortColumn, _sortDescending);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse(tag, true, out SortColumn column)) return;
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = column != SortColumn.Name;
        }
        UpdateSortHeaders();
        Render();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _rendering) return;
        // Typing should never rebuild the list on every keystroke: debounce briefly instead.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private static AppRow MemberRow(AppCatalogRow parent, AppCatalogMember member, double indent, bool expanded)
    {
        var fileName = System.IO.Path.GetFileName(member.Key);
        if (string.IsNullOrEmpty(fileName)) fileName = member.Name ?? member.Key;
        var nested = member.Children is { Count: > 1 };
        var mergeState = member.Automatic ? "自动归并" : "已并入";
        var subtitle = nested
            ? $"{mergeState} · {parent.Name} · {member.Children!.Count} 个进程"
            : member.Merged ? $"子进程 · {mergeState} · {parent.Name}" : $"同名进程 · {member.Key}";
        return new AppRow
        {
            Key = member.Key,
            Name = string.IsNullOrEmpty(member.Name) ? fileName : member.Name,
            Subtitle = subtitle,
            Category = parent.Category,
            TotalText = member.TotalSeconds > 0 ? AppFormat.LongDuration(member.TotalSeconds) : "—",
            Duration = "—",
            ExePath = member.ExePath,
            ColorBrush = new SolidColorBrush(ToColor(parent.Color)),
            MergeText = "移出",
            HideText = member.Hidden ? "显示" : "隐藏",
            IsChild = true,
            ChevronVisibility = nested ? Visibility.Visible : Visibility.Hidden,
            // The second level uses a smaller expander nudged to the right.
            ChevronSize = 18,
            ChevronMargin = new Thickness(0),
            ChevronFontSize = 9,
            ChevronGlyph = expanded ? "\uE70D" : "\uE76C",
            Indent = new Thickness(indent, 0, 0, 0),
            NameSize = 12,
            ChildKey = member.Key,
            IsMerged = member.Merged
        };
    }

    /// <summary>Third level: a process of an application that was merged into another application.</summary>
    private static AppRow LeafRow(AppCatalogRow parent, AppCatalogMember owner, AppCatalogMember child, double indent)
    {
        var fileName = System.IO.Path.GetFileName(child.Key);
        if (string.IsNullOrEmpty(fileName)) fileName = child.Key;
        return new AppRow
        {
            Key = child.Key,
            Name = fileName,
            Subtitle = child.Automatic ? $"自动归并 · {parent.Name}" : $"进程 · {owner.Name ?? owner.Key}",
            Category = parent.Category,
            TotalText = child.TotalSeconds > 0 ? AppFormat.LongDuration(child.TotalSeconds) : "—",
            Duration = "—",
            ExePath = child.ExePath,
            ColorBrush = new SolidColorBrush(ToColor(parent.Color)),
            MergeText = "移出",
            HideText = child.Hidden ? "显示" : "隐藏",
            IsChild = true,
            ChevronVisibility = Visibility.Hidden,
            Indent = new Thickness(indent, 0, 0, 0),
            NameSize = 11,
            ChildKey = child.Key
        };
    }
    private static AppRow MakeRow(AppCatalogRow row, double totalSeconds, double todaySeconds, string kind,
        bool hasChildren, bool expanded, double indent, IReadOnlyList<AppCatalogMember> members, bool hidden,
        bool secondLevel = false)
    {
        var mergedName = string.IsNullOrWhiteSpace(row.MergedInto) ? null : row.MergedInto;
        var subtitle = kind == "bucket"
            ? $"{row.Category} · {row.Key}"
            : $"{row.Category} · {row.Key}";
        if (members.Count > 1) subtitle += $" · {members.Count} 个进程";
        var inBucket = kind != "main" ||
            string.Equals(mergedName, AppStatKeys.Background, StringComparison.Ordinal) ||
            string.Equals(mergedName, AppStatKeys.System, StringComparison.Ordinal);
        return new AppRow
        {
            Key = row.Key,
            Name = row.Name,
            Subtitle = subtitle,
            Category = row.Category,
            TotalText = totalSeconds > 0 ? AppFormat.LongDuration(totalSeconds) : "—",
            Duration = todaySeconds > 0 ? AppFormat.Duration(todaySeconds) : "—",
            IconPath = row.IconPath,
            ExePath = row.ExePath,
            ColorBrush = new SolidColorBrush(ToColor(row.Color)),
            MergeText = inBucket ? "移出" : "归并",
            HideText = hidden ? "显示" : "隐藏",
            IsSystemRow = row.System || row.Background,
            SystemOverride = row.SystemOverride,
            ChevronVisibility = hasChildren ? Visibility.Visible : Visibility.Hidden,
            IsExpanded = expanded,
            ChevronGlyph = expanded ? "\uE70D" : "\uE76C",
            ChevronSize = secondLevel ? 18 : 22,
            ChevronMargin = new Thickness(0),
            ChevronFontSize = secondLevel ? 9 : 10,
            Indent = new Thickness(indent, 0, 0, 0),
            NameSize = 13,
            Members = members,
            Row = row
        };
    }
    /// <summary>Hue in degrees (0-360) for an ARGB colour, used to group similar shades.</summary>
    private static double Hue(uint argb)
    {
        double r = (byte)(argb >> 16) / 255d, g = (byte)(argb >> 8) / 255d, b = (byte)argb / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta < 1e-9) return 0;
        double hue;
        if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    private static int HueBucket(uint argb) => (int)(Hue(argb) / 30) % 12;
    private static Color ToColor(uint argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestData();

    private void GroupingRules_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        EditorTitle.Text = "进程归并规则";
        EditorHint.Text = """
            • 从进程的可执行文件所在目录逐级往上找母应用，优先采用最近一层能唯一识别的归属。
            • 已知辅助程序也可按产品安装目录归并。碰到公共目录或多个母应用候选时停止追踪。
            • 已手动归并的进程更新版本后，同一安装路径下的对应进程会沿用原来的归属。
            • 只拿到进程名时，需要有唯一的完整路径记录。WebView2 等共享组件不会凭名称归并。

            手动归并和「移出」优先。自动归并会标在子进程旁边，可展开母应用后移出。
            """;
        TextPanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        EditorList.Visibility = Visibility.Collapsed;
        EditorCancelButton.Visibility = Visibility.Collapsed;
        EditorOkButton.Content = "关闭";
        OpenEditor(EditorOkButton);
    }

    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        if (!_expanded.Add(key)) _expanded.Remove(key);
        Render();
    }

    private AppCatalogRow? RowOf(object sender) =>
        sender is FrameworkElement { Tag: string key } ? RowOfKey(key) : null;

    private AppCatalogRow? RowOfKey(string key)
    {
        var direct = _catalog.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal));
        if (direct is not null) return direct;
        return _catalog.FirstOrDefault(row => row.Members?.Any(member => member.Key == key ||
            member.Children?.Any(child => child.Key == key) == true) == true);
    }

    /// <summary>Keep manually merged processes independent when editing their parent application.</summary>
    private IReadOnlyList<string> KeysFor(AppCatalogRow row, string key)
    {
        if (!string.Equals(row.Key, key, StringComparison.Ordinal)) return [key];
        var members = row.Members?.Where(member => !member.Merged).Select(member => member.Key).ToArray();
        return members is { Length: > 0 } ? members : [row.Key];
    }

    private void Send(string kind, IReadOnlyList<string> keys, string? value)
    {
        foreach (var key in keys) MetaRequested?.Invoke(kind, key, value);
    }

    // ---------------------------------------------------------------- actions

    private IInputElement? _focusBeforeEditor;

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row is null) return;
        _editing = _rows.FirstOrDefault(item => item.Key == row.Key);
        _mode = "rename";
        EditorTitle.Text = $"重命名「{row.Name}」";
        EditorHint.Text = "名称会用于所有统计页面，之后再次识别到该程序也沿用这个名字（该应用的全部进程一起改名）。";
        EditorInput.Text = row.Name;
        TextPanel.Visibility = Visibility.Visible;
        ColorPanel.Visibility = Visibility.Collapsed;
        EditorList.Visibility = Visibility.Collapsed;
        OpenEditor(EditorInput);
        EditorInput.SelectAll();
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row is null) return;
        _editing = _rows.FirstOrDefault(item => item.Key == row.Key);
        _mode = "color";
        EditorTitle.Text = $"「{row.Name}」的颜色";
        EditorHint.Text = "从调色盘中选择，或输入十六进制色值、用屏幕取色吸一个颜色。";
        HexInput.Text = AppPalette.Format(row.Color);
        HexPreview.Background = new SolidColorBrush(ToColor(row.Color));
        TextPanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Visible;
        EditorList.Visibility = Visibility.Collapsed;
        OpenEditor(HexInput);
    }

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择图标",
            Filter = "图标或程序|*.ico;*.png;*.jpg;*.jpeg;*.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Send("icon", KeysFor(row, row.Key), dialog.FileName);
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row is null) return;
        if (sender is FrameworkElement { Tag: string key } && !string.Equals(key, row.Key, StringComparison.Ordinal))
        {
            MetaRequested?.Invoke("unmerge", key, null);
            return;
        }
        // Merged into one of the pinned classifications: the button removes it again.
        if (row.MergedInto is AppStatKeys.Background or AppStatKeys.System or AppStatKeys.Testing)
        {
            Send("unmerge", KeysFor(row, row.Key), null);
            return;
        }
        // Automatically classified system processes: "移出" forces them to be treated as an application.
        if (row.System)
        {
            Send("asapp", KeysFor(row, row.Key), null);
            return;
        }
        var options = new List<MergeOption>
        {
            new(AppStatKeys.Background, "后台默认进程（不计入应用时长，仍按应用显示功耗）"),
            new(AppStatKeys.System, "系统进程（并入 Windows 系统进程组，不计入应用时长）"),
            new(AppStatKeys.Testing, "测试进程（在应用列表中单独分类）")
        };
        options.AddRange(_catalog
            .Where(item => !string.Equals(item.Key, row.Key, StringComparison.Ordinal) &&
                           !item.Background && !item.System &&
                           string.IsNullOrWhiteSpace(item.MergedInto))
            .OrderByDescending(item => item.TotalSeconds)
            .Select(item => new MergeOption(item.Key, $"{item.Name}（{AppFormat.Duration(item.TotalSeconds)}）")));
        if (options.Count == 0) return;

        _editing = _rows.FirstOrDefault(item => item.Key == row.Key);
        _mode = "merge";
        EditorTitle.Text = $"把「{row.Name}」并入…";
        EditorHint.Text = "可以并入另一个应用（作为子进程显示在对方下面），也可以归到「后台默认进程」或「系统进程」分类，随时可取消。";
        EditorList.ItemsSource = options;
        EditorList.SelectedIndex = 0;
        TextPanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        EditorList.Visibility = Visibility.Visible;
        OpenEditor(EditorList);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row is null) return;
        if (sender is FrameworkElement { Tag: string key } && !string.Equals(key, row.Key, StringComparison.Ordinal))
        {
            var member = row.Members?.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            MetaRequested?.Invoke(member?.Hidden == true ? "show" : "hide", key, null);
            return;
        }
        Send(row.Hidden ? "show" : "hide", KeysFor(row, row.Key), null);
    }

    private void EditorOk_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) { CloseEditor(); return; }
        switch (_mode)
        {
            case "rename":
                var name = EditorInput.Text.Trim();
                if (name.Length == 0) { EditorHint.Text = "请输入应用名称。"; EditorInput.Focus(); return; }
                Send("rename", KeysFor(_editing.Row!, _editing.Key), name);
                break;
            case "color":
                var hex = NormalizeHex(HexInput.Text);
                if (hex is null) { EditorHint.Text = "请输入有效的十六进制颜色，例如 #0067C0。"; HexInput.Focus(); return; }
                Send("color", KeysFor(_editing.Row!, _editing.Key), hex);
                break;
            case "merge":
                if (EditorList.SelectedItem is MergeOption target) Send("merge", KeysFor(_editing.Row!, _editing.Key), target.Key);
                break;
        }
        CloseEditor();
    }

    private void EditorCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void OpenEditor(IInputElement firstControl)
    {
        _focusBeforeEditor = Keyboard.FocusedElement;
        EditorOverlay.Visibility = Visibility.Visible;
        Keyboard.Focus(firstControl);
    }

    private void Editor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
        {
            EditorOk_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CloseEditor()
    {
        EditorOverlay.Visibility = Visibility.Collapsed;
        EditorCancelButton.Visibility = Visibility.Visible;
        EditorOkButton.Content = "确定";
        _editing = null;
        if (_focusBeforeEditor is { } previous) Keyboard.Focus(previous);
        _focusBeforeEditor = null;
    }

    // ---------------------------------------------------------------- colour helpers

    private void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var button = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex
            };
            button.Click += (_, _) =>
            {
                HexInput.Text = hex;
                HexPreview.Background = new SolidColorBrush(color);
            };
            AutomationProperties.SetName(button, $"颜色 {hex}");
            SwatchPanel.Children.Add(button);
        }
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(c => new string(c, 2)));
        if (text.Length != 6) return null;
        return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out _) ? "#" + text.ToUpperInvariant() : null;
    }

    private void PickScreen_Click(object sender, RoutedEventArgs e)
    {
        EditorOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var color = ScreenColorPickerWindow.Pick(Window.GetWindow(this));
            if (color is { } picked)
            {
                HexInput.Text = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                HexPreview.Background = new SolidColorBrush(picked);
            }
        }
        finally
        {
            EditorOverlay.Visibility = Visibility.Visible;
            HexInput.Focus();
        }
    }

    public sealed record MergeOption(string Key, string Name);

    public sealed class AppRow : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string TotalText { get; init; } = "—";
        public string Duration { get; init; } = "—";
        public string? IconPath { get; init; }
        public string? ExePath { get; init; }
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }
        public Brush? ColorBrush { get; init; }
        public string MergeText { get; init; } = "归并";
        public Visibility MergeVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
        public Visibility HideVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
        public string HideText { get; init; } = "隐藏";
        public Visibility ChevronVisibility { get; init; } = Visibility.Hidden;
        public bool IsExpanded { get; init; }
        public string ChevronGlyph { get; init; } = "\uE76C";
        public double ChevronSize { get; init; } = 22;
        public Thickness ChevronMargin { get; init; } = new(0);
        public double ChevronFontSize { get; init; } = 10;
        public Thickness Indent { get; init; }
        public double NameSize { get; init; } = 13;
        public bool IsGroup { get; init; }
        public bool IsSystemRow { get; init; }
        public int? SystemOverride { get; init; }

        public bool IsChild { get; init; }
        public bool IsMerged { get; init; }
        public string? ChildKey { get; init; }
        public IReadOnlyList<AppCatalogMember> Members { get; init; } = [];
        public Visibility ActionsVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
        public AppCatalogRow? Row { get; init; }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit.Highlighting;
using Markdig;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace MDViewer;

// ── Custom highlighting definition (avoids xshd regex quirks) ────────────────
internal sealed class CustomHighlightingDefinition : IHighlightingDefinition
{
    public string Name { get; }
    public HighlightingRuleSet MainRuleSet { get; } = new();
    private readonly Dictionary<string, HighlightingColor> _colors = [];

    public CustomHighlightingDefinition(string name) => Name = name;

    public HighlightingColor AddColor(string name,
        System.Windows.Media.Color fg, bool bold = false, bool italic = false)
    {
        var c = new HighlightingColor
        {
            Name       = name,
            Foreground = new SimpleHighlightingBrush(fg),
            FontWeight = bold   ? System.Windows.FontWeights.Bold   : null,
            FontStyle  = italic ? System.Windows.FontStyles.Italic  : null,
        };
        _colors[name] = c;
        return c;
    }

    public HighlightingColor  GetNamedColor(string name) =>
        _colors.TryGetValue(name, out var c) ? c : throw new KeyNotFoundException(name);
    public HighlightingRuleSet GetNamedRuleSet(string name) =>
        throw new NotSupportedException();
    public IEnumerable<HighlightingColor> NamedHighlightingColors => _colors.Values;
    public IDictionary<string, string>    Properties => new Dictionary<string, string>();
}

// ── Tab model ─────────────────────────────────────────────────────────────────
public sealed class TabEntry : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isDirty;

    public required string FilePath { get; init; }
    public string Title        => Path.GetFileName(FilePath);
    public string DisplayTitle => _isDirty ? $"● {Title}" : Title;

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; Notify(nameof(IsActive)); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set { _isDirty = value; Notify(nameof(IsDirty)); Notify(nameof(DisplayTitle)); }
    }

    public string?               CachedHtml    { get; set; }
    public string?               PendingSource { get; set; }
    internal FileSystemWatcher?       Watcher   { get; set; }
    internal CancellationTokenSource? ReloadCts { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string p) => PropertyChanged?.Invoke(this, new(p));
}

// ── Main window ───────────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
    public ObservableCollection<TabEntry> Tabs { get; } = [];

    private TabEntry? _activeTab;
    private bool      _isDark       = true;
    private bool      _webViewReady = false;
    private string?   _pendingFile;

    // Edit mode
    private bool _isEditing;
    private CancellationTokenSource? _editPreviewCts;

    // Fullscreen state
    private bool        _isFullscreen;
    private WindowStyle _prevWindowStyle;
    private WindowState _prevWindowState;

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDViewer", "session.json");

    private static readonly string TempHtml = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDViewer", "preview.html");

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .Build();

    private readonly string _template;

    // ─── Constructor ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        // Apply saved theme before XAML elements are created — prevents flash
        _isDark = LoadSavedTheme();
        UpdateThemeResources(_isDark);

        InitializeComponent();
        _template = LoadEmbeddedResource("MDViewer.Assets.template.html");
        MarkdownEditor.SyntaxHighlighting = BuildMarkdownHighlighting();
        MarkdownEditor.Document.TextChanged += OnEditorTextChanged;
        _ = InitWebViewAsync();
    }

    private static bool LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(SessionPath)) return true;
            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath));
            return data?.DarkTheme ?? true;
        }
        catch { return true; }
    }

    // ─── AvalonEdit syntax highlighting (built in code, not xshd) ────────────

    private static IHighlightingDefinition BuildMarkdownHighlighting()
    {
        var def = new CustomHighlightingDefinition("Markdown");

        var cHeading    = def.AddColor("Heading",    System.Windows.Media.Color.FromRgb(0x56, 0x9C, 0xD6), bold: true);
        var cBold       = def.AddColor("Bold",       System.Windows.Media.Color.FromRgb(0xE6, 0xED, 0xF3), bold: true);
        var cItalic     = def.AddColor("Italic",     System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78), italic: true);
        var cCode       = def.AddColor("Code",       System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
        var cCodeBlock  = def.AddColor("CodeBlock",  System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78));
        var cLink       = def.AddColor("Link",       System.Windows.Media.Color.FromRgb(0x58, 0xA6, 0xFF));
        var cImage      = def.AddColor("Image",      System.Windows.Media.Color.FromRgb(0x79, 0xC0, 0xFF));
        var cList       = def.AddColor("List",       System.Windows.Media.Color.FromRgb(0x4D, 0x9D, 0xE0));
        var cBlockquote = def.AddColor("Blockquote", System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E), italic: true);
        var cHRule      = def.AddColor("HRule",      System.Windows.Media.Color.FromRgb(0x48, 0x4F, 0x58));
        var cHtml       = def.AddColor("HTML",       System.Windows.Media.Color.FromRgb(0x85, 0xE8, 0x9D));

        var rs = def.MainRuleSet;

        // Fenced code blocks
        rs.Spans.Add(MakeSpan(cCodeBlock, "```",   "```",   multiline: true));
        rs.Spans.Add(MakeSpan(cCodeBlock, "~~~",   "~~~",   multiline: true));

        // Inline code
        rs.Spans.Add(MakeSpan(cCode,     "`",  "`"));

        // Bold+italic ***
        rs.Spans.Add(MakeSpan(cBold,  @"\*\*\*", @"\*\*\*"));
        rs.Spans.Add(MakeSpan(cBold,  "___",      "___"));

        // Bold **
        rs.Spans.Add(MakeSpan(cBold,  @"\*\*", @"\*\*"));
        rs.Spans.Add(MakeSpan(cBold,  "__",     "__"));

        // Italic *
        rs.Spans.Add(MakeSpan(cItalic, @"\*", @"\*"));
        rs.Spans.Add(MakeSpan(cItalic, "_",    "_"));

        // Rules that don't need .* or ^ (safe patterns only)
        rs.Rules.Add(MakeRule(cHeading,    @"#{1,6} \S[^\n]*"));
        rs.Rules.Add(MakeRule(cHRule,      @"-{3,}"));
        rs.Rules.Add(MakeRule(cHRule,      @"={3,}"));
        rs.Rules.Add(MakeRule(cHRule,      @"\*{3,}"));
        rs.Rules.Add(MakeRule(cHRule,      @"_{3,}"));
        rs.Rules.Add(MakeRule(cBlockquote, @">[^\n]+"));
        rs.Rules.Add(MakeRule(cList,       @"[-*+] \S"));
        rs.Rules.Add(MakeRule(cList,       @"\d+\. \S"));
        rs.Rules.Add(MakeRule(cImage,      @"!\[[^\]]+\]"));
        rs.Rules.Add(MakeRule(cLink,       @"\[[^\]]+\]"));
        rs.Rules.Add(MakeRule(cHtml,       @"<[^>]+>"));

        return def;
    }

    private static HighlightingSpan MakeSpan(HighlightingColor color, string begin, string end, bool multiline = false)
    {
        var opts = System.Text.RegularExpressions.RegexOptions.None;
        if (multiline) opts |= System.Text.RegularExpressions.RegexOptions.Singleline;
        return new HighlightingSpan
        {
            StartExpression = new System.Text.RegularExpressions.Regex(begin, opts),
            EndExpression   = new System.Text.RegularExpressions.Regex(end,   opts),
            SpanColor       = color,
            StartColor      = color,
            EndColor        = color,
        };
    }

    private static HighlightingRule MakeRule(HighlightingColor color, string pattern)
    {
        return new HighlightingRule
        {
            Regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.None),
            Color = color,
        };
    }

    // ─── WebView init ─────────────────────────────────────────────────────────

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MDViewer", "WebView2Cache"));

            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
            WebView.CoreWebView2.Settings.IsSwipeNavigationEnabled      = false;

            _webViewReady = true;

            RestoreSession();

            if (_pendingFile != null)
            {
                var f = _pendingFile;
                _pendingFile = null;
                OpenFile(f);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not initialize WebView2.\n\n" +
                $"Make sure the Microsoft Edge WebView2 runtime is installed.\n\nError: {ex.Message}",
                "WebView2 Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Public: open file ────────────────────────────────────────────────────

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"File not found:\n{path}",
                "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_webViewReady) { _pendingFile = path; return; }

        var existing = Tabs.FirstOrDefault(
            t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { SwitchToTab(existing); return; }

        var tab = new TabEntry { FilePath = path };
        SetupWatcher(tab);
        Tabs.Add(tab);
        SwitchToTab(tab);
    }

    // ─── Tab management ───────────────────────────────────────────────────────

    private void SwitchToTab(TabEntry tab)
    {
        // Persist editor content of previous tab before switching
        if (_isEditing && _activeTab != null && _activeTab != tab)
            _activeTab.PendingSource = MarkdownEditor.Text;

        if (_activeTab != null) _activeTab.IsActive = false;
        _activeTab   = tab;
        tab.IsActive = true;

        Title                  = $"{tab.DisplayTitle} — MD Viewer";
        TxtFilePath.Text       = tab.FilePath;
        TxtFilePath.Foreground = System.Windows.Media.Brushes.DimGray;

        MenuReload.IsEnabled   = true;
        MenuCloseTab.IsEnabled = true;
        UpdateEditMenuState();

        if (_isEditing)
        {
            LoadTabIntoEditor(tab);
        }
        else if (tab.CachedHtml != null)
        {
            _ = ShowHtmlAsync(tab.CachedHtml, tab.Title);
        }
        else
        {
            _ = RenderTabAsync(tab);
        }
    }

    private void CloseTab(TabEntry tab)
    {
        if (tab.IsDirty)
        {
            var r = MessageBox.Show(
                $"'{tab.Title}' has unsaved changes. Save before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) SaveTab(tab, fromEditor: tab == _activeTab && _isEditing);
        }

        var idx = Tabs.IndexOf(tab);
        tab.Watcher?.Dispose();
        tab.ReloadCts?.Cancel();
        Tabs.Remove(tab);

        if (tab != _activeTab) return;

        _activeTab = null;

        if (Tabs.Count == 0)
        {
            Title                  = "MD Viewer";
            TxtFilePath.Text       = "Drag a .md file or use File → Open";
            TxtFilePath.Foreground = System.Windows.Media.Brushes.DimGray;
            MenuReload.IsEnabled   = false;
            MenuCloseTab.IsEnabled = false;
            UpdateEditMenuState();
            HideSearchBar();

            if (_isEditing) SetEditMode(false);

            _ = RenderWelcomeAsync();
        }
        else
        {
            SwitchToTab(Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)]);
        }
    }

    // ─── Edit mode ────────────────────────────────────────────────────────────

    private void SetEditMode(bool on)
    {
        _isEditing              = on;
        MenuEditMode.IsChecked  = on;

        var cols = EditorPreviewGrid.ColumnDefinitions;

        if (on)
        {
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[2].Width = new GridLength(1, GridUnitType.Star);
            EditorSplitter.Visibility = Visibility.Visible;

            if (_activeTab != null)
                LoadTabIntoEditor(_activeTab);
        }
        else
        {
            // Save pending source back before hiding
            if (_activeTab != null)
                _activeTab.PendingSource = MarkdownEditor.Text;

            cols[0].Width = new GridLength(0);
            cols[2].Width = new GridLength(1, GridUnitType.Star);
            EditorSplitter.Visibility = Visibility.Collapsed;
        }

        UpdateEditMenuState();
    }

    private void LoadTabIntoEditor(TabEntry tab)
    {
        // Use pending (unsaved) source if available, otherwise read from disk
        string text;
        if (tab.PendingSource != null)
        {
            text = tab.PendingSource;
        }
        else
        {
            try   { text = File.ReadAllText(tab.FilePath, Encoding.UTF8); }
            catch { text = string.Empty; }
        }

        // Suppress the TextChanged event while loading
        MarkdownEditor.Document.TextChanged -= OnEditorTextChanged;
        MarkdownEditor.Document.Text         = text;
        MarkdownEditor.Document.TextChanged += OnEditorTextChanged;

        // Render current state
        var html = Markdown.ToHtml(text, _pipeline);
        tab.CachedHtml = html;
        _ = ShowHtmlAsync(html, tab.Title);
    }

    private void UpdateEditMenuState()
    {
        bool hasTab   = _activeTab != null;
        MenuEditMode.IsEnabled  = hasTab;
        MenuSave.IsEnabled      = hasTab && _isEditing;
        MenuSaveAs.IsEnabled    = hasTab;
    }

    private void UpdateTitle()
    {
        if (_activeTab != null)
            Title = $"{_activeTab.DisplayTitle} — MD Viewer";
    }

    // ─── Save ─────────────────────────────────────────────────────────────────

    private void SaveTab(TabEntry tab, bool fromEditor)
    {
        var text = fromEditor ? MarkdownEditor.Text : (tab.PendingSource ?? string.Empty);
        try
        {
            File.WriteAllText(tab.FilePath, text, Encoding.UTF8);
            tab.PendingSource = null;
            tab.IsDirty       = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}",
                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCurrentTab()
    {
        if (_activeTab is null || !_isEditing) return;
        SaveTab(_activeTab, fromEditor: true);
    }

    private void SaveCurrentTabAs()
    {
        if (_activeTab is null) return;

        var dlg = new SaveFileDialog
        {
            Title            = "Save As",
            Filter           = "Markdown Files (*.md;*.markdown)|*.md;*.markdown|All Files (*.*)|*.*",
            FileName         = _activeTab.Title,
            InitialDirectory = Path.GetDirectoryName(_activeTab.FilePath)
        };
        if (dlg.ShowDialog() != true) return;

        var text = _isEditing ? MarkdownEditor.Text : (File.Exists(_activeTab.FilePath)
            ? File.ReadAllText(_activeTab.FilePath, Encoding.UTF8)
            : string.Empty);
        try
        {
            File.WriteAllText(dlg.FileName, text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}",
                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Editor text changed (live preview) ───────────────────────────────────

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_activeTab is null || !_isEditing) return;

        _activeTab.PendingSource = MarkdownEditor.Text;

        if (!_activeTab.IsDirty)
        {
            _activeTab.IsDirty = true;
            UpdateTitle();
        }

        // Debounce: cancel previous render and schedule a new one
        _editPreviewCts?.Cancel();
        _editPreviewCts = new CancellationTokenSource();
        var token  = _editPreviewCts.Token;
        var tab    = _activeTab;
        var source = MarkdownEditor.Text;

        Task.Delay(300, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            var html = Markdown.ToHtml(source, _pipeline);
            tab.CachedHtml = html;
            Dispatcher.Invoke(() =>
            {
                if (tab == _activeTab) _ = ShowHtmlAsync(html, tab.Title);
            });
        }, TaskScheduler.Default);
    }

    // ─── Rendering ────────────────────────────────────────────────────────────

    private async Task RenderTabAsync(TabEntry tab)
    {
        try
        {
            var md   = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
            var html = Markdown.ToHtml(md, _pipeline);
            tab.CachedHtml = html;
            if (tab == _activeTab) await ShowHtmlAsync(html, tab.Title);
        }
        catch (Exception ex)
        {
            if (tab == _activeTab)
                await ShowHtmlAsync(
                    $"<p style='color:red'>Error reading file: {System.Web.HttpUtility.HtmlEncode(ex.Message)}</p>",
                    "Error");
        }
    }

    private async Task ShowHtmlAsync(string body, string title)
    {
        if (!_webViewReady) return;
        var full = _template
            .Replace("{{TITLE}}",      System.Web.HttpUtility.HtmlEncode(title))
            .Replace("{{CONTENT}}",    body)
            .Replace("{{DARK_CLASS}}", _isDark ? "dark" : "");

        Directory.CreateDirectory(Path.GetDirectoryName(TempHtml)!);
        await File.WriteAllTextAsync(TempHtml, full, Encoding.UTF8);
        WebView.CoreWebView2.Navigate(new Uri(TempHtml).AbsoluteUri);
    }

    private async Task RenderWelcomeAsync()
    {
        const string md = """
            # Welcome to MD Viewer

            Open a Markdown file using any of these methods:

            - **Drag and drop** one or more `.md` files onto this window
            - Use **File → Open…** in the menu bar
            - Press **Ctrl+O**
            - Use **Tools → Associate .md Files** to open them directly from Explorer

            ---

            ## Keyboard Shortcuts

            | Shortcut | Action |
            |---|---|
            | `Ctrl+O` | Open file(s) |
            | `Ctrl+W` | Close tab |
            | `F5` / `Ctrl+R` | Reload |
            | `Ctrl+D` | Toggle dark / light theme |
            | `Ctrl+E` | Toggle edit mode |
            | `Ctrl+S` | Save (in edit mode) |
            | `Ctrl+Shift+S` | Save As… |
            | `Ctrl+F` | Find in document |
            | `F3` / `Shift+F3` | Find next / previous |
            | `F11` | Full screen |

            > Previously opened files are automatically restored on startup.
            """;
        await ShowHtmlAsync(Markdown.ToHtml(md, _pipeline), "Welcome");
    }

    // ─── FileSystemWatcher per tab ────────────────────────────────────────────

    private void SetupWatcher(TabEntry tab)
    {
        tab.Watcher?.Dispose();
        tab.Watcher = new FileSystemWatcher
        {
            Path         = Path.GetDirectoryName(tab.FilePath)!,
            Filter       = Path.GetFileName(tab.FilePath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        tab.Watcher.Changed += (_, _) => ScheduleReload(tab);
        tab.Watcher.Renamed += (_, _) => ScheduleReload(tab);
    }

    private void ScheduleReload(TabEntry tab)
    {
        // Don't interfere while the user is actively editing this file
        if (_isEditing && tab == _activeTab && tab.IsDirty) return;

        tab.ReloadCts?.Cancel();
        tab.ReloadCts = new CancellationTokenSource();
        var token = tab.ReloadCts.Token;

        Task.Delay(350, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            tab.CachedHtml    = null;
            tab.PendingSource = null;
            Dispatcher.Invoke(() =>
            {
                if (tab == _activeTab)
                {
                    if (_isEditing) LoadTabIntoEditor(tab);
                    else            _ = RenderTabAsync(tab);
                }
            });
        }, TaskScheduler.Default);
    }

    // ─── Search ───────────────────────────────────────────────────────────────

    private void ShowSearchBar()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
        MenuSearchNext.IsEnabled = true;
        MenuSearchPrev.IsEnabled = true;
        BtnSearchNext.IsEnabled  = true;
        BtnSearchPrev.IsEnabled  = true;
    }

    private void HideSearchBar()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchBox.Text       = string.Empty;
        SearchCount.Text     = string.Empty;
        MenuSearchNext.IsEnabled = false;
        MenuSearchPrev.IsEnabled = false;
        BtnSearchNext.IsEnabled  = false;
        BtnSearchPrev.IsEnabled  = false;
        _ = WebView.ExecuteScriptAsync("window.getSelection()?.removeAllRanges()");
    }

    private async Task DoSearchAsync(bool forward)
    {
        var term = SearchBox.Text;
        if (string.IsNullOrEmpty(term) || !_webViewReady) return;

        var escaped  = JsonSerializer.Serialize(term);
        var backward = forward ? "false" : "true";

        var result = await WebView.ExecuteScriptAsync(
            $"window.find({escaped}, false, {backward}, true)");

        SearchCount.Text = result == "true" ? "" : "No results";
        SearchCount.Foreground = result == "true"
            ? System.Windows.Media.Brushes.DimGray
            : System.Windows.Media.Brushes.IndianRed;
    }

    // ─── Session persistence ──────────────────────────────────────────────────

    private void SaveSession()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            var data = new SessionData
            {
                Files       = [.. Tabs.Select(t => t.FilePath)],
                ActiveIndex = _activeTab is null ? 0 : Tabs.IndexOf(_activeTab),
                DarkTheme   = _isDark
            };
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    private void RestoreSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) { _ = RenderWelcomeAsync(); return; }

            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath));

            // Restore theme before rendering anything
            ApplyTheme(data?.DarkTheme ?? true);

            if (data?.Files is not { Length: > 0 }) { _ = RenderWelcomeAsync(); return; }

            foreach (var path in data.Files.Where(File.Exists))
            {
                var tab = new TabEntry { FilePath = path };
                SetupWatcher(tab);
                Tabs.Add(tab);
            }

            if (Tabs.Count == 0) { _ = RenderWelcomeAsync(); return; }

            SwitchToTab(Tabs[Math.Clamp(data.ActiveIndex, 0, Tabs.Count - 1)]);
        }
        catch { _ = RenderWelcomeAsync(); }
    }

    private sealed class SessionData
    {
        public string[] Files       { get; set; } = [];
        public int      ActiveIndex { get; set; }
        public bool     DarkTheme   { get; set; } = true;
    }

    // ─── Theme ────────────────────────────────────────────────────────────────

    private void ApplyTheme(bool dark)
    {
        _isDark = dark;
        if (MenuDark != null) MenuDark.IsChecked = dark;

        UpdateThemeResources(dark);

        if (_webViewReady)
            _ = WebView.ExecuteScriptAsync(dark
                ? "document.documentElement.classList.add('dark');"
                : "document.documentElement.classList.remove('dark');");

        SetTitleBarColors(dark);
    }

    private static void UpdateThemeResources(bool dark)
    {
        var res = Application.Current.Resources;
        if (dark)
        {
            res["BgApp"]        = Brush("#0D1117");
            res["BgBar"]        = Brush("#161B22");
            res["BgPopup"]      = Brush("#1C2128");
            res["BgTabActive"]  = Brush("#161B22");
            res["BgTabHover"]   = Brush("#1C2333");
            res["BgMenuHover"]  = Brush("#2D333B");
            res["FgBar"]        = Brush("#CCCCCC");
            res["FgContent"]    = Brush("#E6EDF3");
            res["FgMuted"]      = Brush("#636C76");
            res["BorderMain"]   = Brush("#21262D");
            res["BorderSubtle"] = Brush("#30363D");
            res["Accent"]       = Brush("#4D9DE0");
            res["Selection"]    = Brush("#2B4F7A");
            res["ScrollThumb"]  = Brush("#484F58");
            res["ScrollHover"]  = Brush("#8B949E");
        }
        else
        {
            res["BgApp"]        = Brush("#F6F8FA");
            res["BgBar"]        = Brush("#FFFFFF");
            res["BgPopup"]      = Brush("#FFFFFF");
            res["BgTabActive"]  = Brush("#FFFFFF");
            res["BgTabHover"]   = Brush("#EDF0F3");
            res["BgMenuHover"]  = Brush("#E8ECEF");
            res["FgBar"]        = Brush("#24292F");
            res["FgContent"]    = Brush("#1F2328");
            res["FgMuted"]      = Brush("#8C959F");
            res["BorderMain"]   = Brush("#D0D7DE");
            res["BorderSubtle"] = Brush("#D0D7DE");
            res["Accent"]       = Brush("#0969DA");
            res["Selection"]    = Brush("#BDD6F5");
            res["ScrollThumb"]  = Brush("#C0C8D1");
            res["ScrollHover"]  = Brush("#8C959F");
        }
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ─── File association (HKCU) ──────────────────────────────────────────────

    private void RegisterFileAssociation()
    {
        var exePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine the executable path.");

        const string progId = "MDViewer.md";
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
            k.SetValue("", "Markdown File");
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon"))
            k.SetValue("", $"\"{exePath}\",0");
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command"))
            k.SetValue("", $"\"{exePath}\" \"%1\"");
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.md"))
            k.SetValue("", progId);

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        MessageBox.Show(".md files will now open with MD Viewer.",
            "Association Registered", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // ─── Menu event handlers ──────────────────────────────────────────────────

    private void BtnOpen_Click(object s, RoutedEventArgs e)        => ShowOpenDialog();
    private void BtnAssociate_Click(object s, RoutedEventArgs e)
    {
        try { RegisterFileAssociation(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void MenuReload_Click(object s, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        if (_isEditing)
        {
            // Reload from disk, discarding unsaved edits
            if (_activeTab.IsDirty)
            {
                var r = MessageBox.Show(
                    "Discard unsaved changes and reload from disk?",
                    "Reload", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            _activeTab.PendingSource = null;
            _activeTab.IsDirty       = false;
            UpdateTitle();
            LoadTabIntoEditor(_activeTab);
        }
        else
        {
            _activeTab.CachedHtml = null;
            _ = RenderTabAsync(_activeTab);
        }
    }

    private void MenuCloseTab_Click(object s, RoutedEventArgs e)
    {
        if (_activeTab != null) CloseTab(_activeTab);
    }

    private void MenuExit_Click(object s, RoutedEventArgs e) => Close();

    private void MenuEditMode_Click(object s, RoutedEventArgs e) =>
        SetEditMode(MenuEditMode.IsChecked == true);

    private void MenuSave_Click(object s, RoutedEventArgs e)    => SaveCurrentTab();
    private void MenuSaveAs_Click(object s, RoutedEventArgs e)  => SaveCurrentTabAs();

    private void MenuTheme_Click(object s, RoutedEventArgs e) =>
        ApplyTheme(MenuDark.IsChecked == true);

    private void MenuFullscreen_Click(object s, RoutedEventArgs e)
    {
        if (!_isFullscreen)
        {
            _prevWindowStyle = WindowStyle;
            _prevWindowState = WindowState;
            WindowStyle      = WindowStyle.None;
            WindowState      = WindowState.Maximized;
            _isFullscreen    = true;
        }
        else
        {
            WindowStyle   = _prevWindowStyle;
            WindowState   = _prevWindowState;
            _isFullscreen = false;
        }
    }

    private void MenuSearch_Click(object s, RoutedEventArgs e)    => ShowSearchBar();
    private void MenuSearchNext_Click(object s, RoutedEventArgs e) => _ = DoSearchAsync(true);
    private void MenuSearchPrev_Click(object s, RoutedEventArgs e) => _ = DoSearchAsync(false);

    private void MenuOpenDataFolder_Click(object s, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(SessionPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    // ─── Search bar event handlers ────────────────────────────────────────────

    private void SearchClose_Click(object s, RoutedEventArgs e) => HideSearchBar();
    private void SearchNext_Click(object s, RoutedEventArgs e)  => _ = DoSearchAsync(true);
    private void SearchPrev_Click(object s, RoutedEventArgs e)  => _ = DoSearchAsync(false);

    private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchCount.Text = string.Empty;
            _ = WebView.ExecuteScriptAsync("window.getSelection()?.removeAllRanges()");
        }
        else
        {
            _ = DoSearchAsync(true);
        }
    }

    private void SearchBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            _ = DoSearchAsync(false);
        else if (e.Key == Key.Enter)
            _ = DoSearchAsync(true);
        else if (e.Key == Key.Escape)
            HideSearchBar();
    }

    // ─── Tab events ───────────────────────────────────────────────────────────

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TabEntry tab })
            SwitchToTab(tab);
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabEntry tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabEntry tab }) CloseTab(tab);
        e.Handled = true;
    }

    private void TabStrip_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScrollViewer.ScrollToHorizontalOffset(TabScrollViewer.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    // ─── Drag & drop ─────────────────────────────────────────────────────────

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var mdFiles = files.Where(f =>
            f.EndsWith(".md",       StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var f in mdFiles.Count > 0 ? mdFiles : files.ToList())
            OpenFile(f);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DragOverlay.Visibility = Visibility.Visible;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    // ─── Keyboard ────────────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl      = Keyboard.Modifiers == ModifierKeys.Control;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        var shift     = Keyboard.Modifiers == ModifierKeys.Shift;

        if      (e.Key == Key.O  && ctrl)      { ShowOpenDialog(); e.Handled = true; }
        else if (e.Key == Key.W  && ctrl)      { if (_activeTab != null) CloseTab(_activeTab); e.Handled = true; }
        else if (e.Key == Key.D  && ctrl)      { ApplyTheme(!_isDark); e.Handled = true; }
        else if (e.Key == Key.E  && ctrl)      { SetEditMode(!_isEditing); e.Handled = true; }
        else if (e.Key == Key.S  && ctrlShift) { SaveCurrentTabAs(); e.Handled = true; }
        else if (e.Key == Key.S  && ctrl)      { SaveCurrentTab(); e.Handled = true; }
        else if (e.Key == Key.F  && ctrl)      { ShowSearchBar(); e.Handled = true; }
        else if (e.Key == Key.F3 && shift)     { _ = DoSearchAsync(false); e.Handled = true; }
        else if (e.Key == Key.F3)              { _ = DoSearchAsync(true);  e.Handled = true; }
        else if (e.Key == Key.F5 || (e.Key == Key.R && ctrl))
        {
            MenuReload_Click(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F11) { MenuFullscreen_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBar.Visibility == Visibility.Visible)
        { HideSearchBar(); e.Handled = true; }
    }

    // ─── Close ────────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        // Prompt for any dirty tabs
        var dirty = Tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            var names = string.Join("\n  • ", dirty.Select(t => t.Title));
            var r = MessageBox.Show(
                $"The following files have unsaved changes:\n\n  • {names}\n\nSave all before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                foreach (var tab in dirty)
                    SaveTab(tab, fromEditor: tab == _activeTab && _isEditing);
            }
        }

        SaveSession();
        foreach (var tab in Tabs) { tab.Watcher?.Dispose(); tab.ReloadCts?.Cancel(); }
        base.OnClosed(e);
    }

    // ─── Title bar color (Windows 11+) ───────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR    = 36;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetTitleBarColors(_isDark);
    }

    private void SetTitleBarColors(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int bg   = dark ? 0x00221B16 : 0x00FAF8F6; // BGR: #161B22 / #F6F8FA
        int text = dark ? 0x00CCCCCC : 0x002F2924; // BGR: #CCCCCC / #24292F
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref bg,   sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref text, sizeof(int));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "Markdown Files (*.md;*.markdown)|*.md;*.markdown|All Files (*.*)|*.*",
            Title       = "Open Markdown File",
            Multiselect = true
        };
        if (_activeTab != null)
            dlg.InitialDirectory = Path.GetDirectoryName(_activeTab.FilePath);
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) OpenFile(f);
    }

    private static string LoadEmbeddedResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }
}

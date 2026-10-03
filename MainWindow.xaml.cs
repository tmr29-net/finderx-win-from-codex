using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Findr;

public partial class MainWindow : Window
{
    private readonly AppState _state = StateStore.Load();
    private readonly ObservableCollection<FileEntry> _entries = [];
    private readonly List<BrowserTab> _tabs = [];
    private readonly GridView _detailsView;
    private readonly DispatcherTimer _searchTimer;
    private List<string> _clipboardPaths = [];
    private bool _clipboardIsCut;
    private bool _updatingTabs;
    private bool _tileMode;
    private Point _dragStart;
    private string Location => ActiveTab?.Path ?? "HOME://";
    private BrowserTab? ActiveTab => Tabs.SelectedItem is TabItem ti ? ti.Tag as BrowserTab : null;

    public MainWindow()
    {
        InitializeComponent();
        _detailsView = (GridView)FileList.View;
        FileList.ItemsSource = _entries;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchAsync(SearchBox.Text.Trim()); };
        _state.Favorites.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        _state.Recent.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        AddTab("HOME://");
        RefreshSidebar();
        PreviewColumn.Width = new GridLength(0);
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.MinWidth = 0;
        PreviewPanel.IsEnabled = false;
        Loaded += async (_, _) => await RefreshAsync();
        KeyDown += Window_KeyDown;
    }

    private void AddTab(string path)
    {
        var tab = new BrowserTab { Path = path };
        _tabs.Add(tab);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), FontSize = 14, ToolTip = "タブを閉じる" };
        close.Click += (_, _) => CloseTab(tab);
        header.Children.Add(label); header.Children.Add(close);
        var item = new TabItem { Header = header, Tag = tab };
        _updatingTabs = true; Tabs.Items.Add(item); Tabs.SelectedItem = item; _updatingTabs = false;
    }

    private void CloseTab(BrowserTab tab)
    {
        if (_tabs.Count <= 1) { Navigate("HOME://"); return; }
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, tab));
        if (item is not null) Tabs.Items.Remove(item);
        _tabs.Remove(tab);
        _ = RefreshAsync();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingTabs && e.Source == Tabs) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _searchTimer.Stop();
        SearchBox.Text = "";
        BuildBreadcrumb();
        PathText.Text = Location.StartsWith("HOME:") || Location.StartsWith("PC:") ? "" : Location;
        _entries.Clear();
        if (Location == "HOME://")
        {
            ApplyViewMode(_state.ViewModes.GetValueOrDefault(Location) == "tiles", false);
            AddQuickDirectories();
            StatusText.Text = $"{_entries.Count} 個の場所";
            return;
        }
        if (Location == "PC://")
        {
            ApplyViewMode(_state.ViewModes.GetValueOrDefault(Location) == "tiles", false);
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                _entries.Add(new FileEntry { Name = $"{drive.Name}  {drive.VolumeLabel}", FullPath = drive.RootDirectory.FullName, IsDirectory = true, Type = "ドライブ", Icon = "💽", IconResource = "/Assets/Icons/computer.png" });
            StatusText.Text = $"{_entries.Count} 個のドライブ";
            return;
        }
        try
        {
            var path = Location;
            if (!Directory.Exists(path)) { Navigate("HOME://"); return; }
            ApplyViewMode(_state.ViewModes.GetValueOrDefault(path) == "tiles", false);
            await Task.Run(() =>
            {
                var list = new List<FileEntry>();
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    try { list.Add(MakeEntry(directory, true)); } catch { }
                }
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    try { list.Add(MakeEntry(file, false)); } catch { }
                }
                return list.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }).ContinueWith(task =>
            {
                if (task.IsFaulted) throw task.Exception!.GetBaseException();
                Dispatcher.Invoke(() => { if (Location == path) foreach (var entry in task.Result) _entries.Add(entry); });
            }, TaskScheduler.Default);
            UpdateTabHeaders();
            StatusText.Text = $"{_entries.Count} 個の項目";
        }
        catch (UnauthorizedAccessException) { ShowAccessError(); }
        catch (Exception ex) { ShowError("フォルダーを読み込めませんでした", ex); }
    }

    private void AddQuickDirectories()
    {
        var dirs = new (string Label, string Path, string Icon, string IconResource)[]
        {
            ("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "🖥", "/Assets/Icons/desktop.png"),
            ("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "📁", "/Assets/Icons/document.png"),
            ("ダウンロード", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "⬇", "/Assets/Icons/download.png"),
            ("画像", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "🖼", "/Assets/Icons/picture.png")
        };
        foreach (var d in dirs.Where(d => Directory.Exists(d.Path)).DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            _entries.Add(new FileEntry { Name = d.Label, FullPath = d.Path, IsDirectory = true, Type = "フォルダー", Icon = d.Icon, IconResource = d.IconResource, Modified = Directory.GetLastWriteTime(d.Path) });
        foreach (var path in _state.Recent.Take(12))
            if (File.Exists(path) || Directory.Exists(path)) { try { _entries.Add(MakeEntry(path, Directory.Exists(path))); } catch { } }
    }

    private FileEntry MakeEntry(string path, bool directory)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var modified = directory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        var ext = directory ? "" : System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var infoSize = directory ? "" : FormatSize(new FileInfo(path).Length);
        string icon = directory ? "📁" : IconFor(ext);
        string? iconPath = null;
        if (directory && _state.FolderIcons.TryGetValue(path, out var custom) && File.Exists(custom)) iconPath = custom;
        else if (!directory && new[] { "PNG", "JPG", "JPEG", "WEBP", "BMP", "GIF", "TIF", "TIFF" }.Contains(ext)) iconPath = path;
        return new FileEntry { Name = string.IsNullOrEmpty(name) ? path : name, FullPath = path, IsDirectory = directory, Type = directory ? "フォルダー" : string.IsNullOrEmpty(ext) ? "ファイル" : ext + " ファイル", Size = infoSize, Modified = modified, Icon = icon, IconResource = iconPath is null ? IconResourceFor(ext, directory) : null, IconPath = iconPath };
    }

    private static string? IconResourceFor(string ext, bool directory)
    {
        if (directory) return "/Assets/Icons/folder.png";
        return ext switch
        {
            "PNG" or "JPG" or "JPEG" or "GIF" or "BMP" or "WEBP" or "SVG" or "TIF" or "TIFF" => "/Assets/Icons/picture.png",
            "ZIP" or "7Z" or "RAR" or "GZ" or "TAR" => "/Assets/Icons/zip.png",
            "MP4" or "MOV" or "MKV" or "AVI" or "WEBM" => "/Assets/Icons/video.png",
            "DOC" or "DOCX" or "TXT" or "MD" or "LOG" or "PDF" => "/Assets/Icons/document.png",
            _ => "/Assets/Icons/document.png"
        };
    }

    private static string IconFor(string ext) => ext switch
    {
        "PNG" or "JPG" or "JPEG" or "GIF" or "BMP" or "WEBP" or "SVG" => "🖼",
        "ZIP" or "7Z" or "RAR" or "GZ" or "TAR" => "🗜",
        "TXT" or "MD" or "LOG" => "📝",
        "PDF" => "📕",
        "EXE" or "MSI" => "⚙",
        "MP3" or "WAV" or "FLAC" => "🎵",
        "MP4" or "MOV" or "MKV" => "🎬",
        "DOC" or "DOCX" => "📘",
        "XLS" or "XLSX" or "CSV" => "📊",
        "PPT" or "PPTX" => "📙",
        _ => "📄"
    };

    private static string FormatSize(long size) => size switch
    {
        >= 1_073_741_824 => $"{size / 1_073_741_824d:0.##} GB",
        >= 1_048_576 => $"{size / 1_048_576d:0.##} MB",
        >= 1024 => $"{size / 1024d:0.#} KB",
        _ => $"{size} B"
    };

    private void Navigate(string path, bool addHistory = true)
    {
        var tab = ActiveTab;
        if (tab is null) return;
        if (addHistory && tab.Path != path) { tab.Back.Push(tab.Path); tab.Forward.Clear(); }
        tab.Path = path;
        SearchBox.Text = "";
        _ = RefreshAsync();
    }

    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        if (Location == "HOME://" || Location == "PC://")
        {
            AddBreadcrumbButton(Location == "HOME://" ? "ホーム" : "PC", Location);
            return;
        }
        var root = System.IO.Path.GetPathRoot(Location) ?? Location;
        var accumulated = root;
        AddBreadcrumbButton(root, root);
        foreach (var part in Location[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = System.IO.Path.Combine(accumulated, part);
            Breadcrumb.Children.Add(new TextBlock { Text = "›", Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            AddBreadcrumbButton(part, accumulated);
        }
    }
    private void AddBreadcrumbButton(string label, string path)
    {
        var button = new Button { Content = label, Tag = path, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0), FontSize = 12, Foreground = System.Windows.Media.Brushes.SlateGray };
        button.Click += Breadcrumb_Click;
        Breadcrumb.Children.Add(button);
    }
    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) Navigate(path);
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { Back.Count: > 0 } tab) { tab.Forward.Push(tab.Path); tab.Path = tab.Back.Pop(); await RefreshAsync(); }
    }
    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { Forward.Count: > 0 } tab) { tab.Back.Push(tab.Path); tab.Path = tab.Forward.Pop(); await RefreshAsync(); }
    }
    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (Location.StartsWith("HOME:") || Location.StartsWith("PC:")) { Navigate("HOME://"); return; }
        var parent = Directory.GetParent(Location)?.FullName;
        Navigate(string.IsNullOrEmpty(parent) ? "PC://" : parent);
    }
    private void NewTab_Click(object sender, RoutedEventArgs e) { AddTab(Location); _ = RefreshAsync(); }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry item) return;
        await OpenEntryAsync(item);
    }

    private async Task OpenEntryAsync(FileEntry item)
    {
        try
        {
            if (item.IsDirectory) { Remember(item.FullPath); Navigate(item.FullPath); return; }
            if (System.IO.Path.GetExtension(item.FullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { await ExtractZipAsync(item.FullPath); return; }
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
            Remember(item.FullPath);
        }
        catch (UnauthorizedAccessException) { ShowAccessError(); }
        catch (Exception ex) { ShowError("項目を開けませんでした", ex); }
    }

    private async Task ExtractZipAsync(string zipPath)
    {
        var answer = MessageBox.Show($"{System.IO.Path.GetFileName(zipPath)} を現在のフォルダーに展開しますか？", "ZIP を展開", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var destination = System.IO.Path.Combine(Location, System.IO.Path.GetFileNameWithoutExtension(zipPath));
        if (Directory.Exists(destination)) destination += "_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, destination));
            var remove = MessageBox.Show("展開が完了しました。元の ZIP をごみ箱に移動しますか？", "展開完了", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (remove == MessageBoxResult.Yes) FileSystem.DeleteFile(zipPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            ShowError("ZIP を展開できませんでした。元の ZIP は保持されています", ex);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer is null || !IsLoaded) return;
        _searchTimer.Stop();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _searchTimer.Start(); else _ = RefreshAsync();
    }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _searchTimer.Stop(); await SearchAsync(SearchBox.Text.Trim()); } }
    private async void Search_Click(object sender, RoutedEventArgs e) { _searchTimer.Stop(); await SearchAsync(SearchBox.Text.Trim()); }

    private async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { await RefreshAsync(); return; }
        if (Location.StartsWith("HOME:") || Location.StartsWith("PC:")) return;
        var searchRoot = Location;
        StatusText.Text = "検索中…";
        _entries.Clear();
        try
        {
            var results = await Task.Run(() =>
            {
                var found = new List<FileEntry>();
                var pending = new Stack<string>(); pending.Push(Location);
                while (pending.Count > 0 && found.Count < 3000)
                {
                    var current = pending.Pop();
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(current))
                        {
                            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                            if (System.IO.Path.GetFileName(d).Contains(query, StringComparison.CurrentCultureIgnoreCase)) { try { found.Add(MakeEntry(d, true)); } catch { } }
                            pending.Push(d);
                        }
                        foreach (var f in Directory.EnumerateFiles(current))
                            if (System.IO.Path.GetFileName(f).Contains(query, StringComparison.CurrentCultureIgnoreCase)) { try { found.Add(MakeEntry(f, false)); } catch { } }
                    }
                    catch { }
                }
                return found;
            });
            if (Location != searchRoot || SearchBox.Text.Trim() != query) return;
            foreach (var item in results) _entries.Add(item);
            StatusText.Text = $"{results.Count} 件が見つかりました";
        }
        catch (Exception ex) { ShowError("検索できませんでした", ex); }
    }

    private void QuickLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var path = (b.Tag?.ToString()) switch
        {
            "HOME" => "HOME://", "PC" => "PC://",
            "DESKTOP" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "DOCUMENTS" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DOWNLOADS" => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "PICTURES" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), _ => "HOME://"
        };
        Navigate(path);
    }

    private void RefreshSidebar()
    {
        FavoritesList.Items.Clear(); RecentList.Items.Clear();
        foreach (var path in _state.Favorites.ToArray()) AddSidebarButton(FavoritesList, path, "★");
        foreach (var path in _state.Recent.Take(10).ToArray()) AddSidebarButton(RecentList, path, Directory.Exists(path) ? "📁" : "•");
    }
    private void AddSidebarButton(ItemsControl list, string path, string icon)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;
        var button = new Button { Content = $"{icon}   {name}", Tag = path, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 6, 8), Margin = new Thickness(4, 1, 4, 1), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = path, FontSize = 12 };
        button.Click += (_, _) =>
        {
            if (Directory.Exists(path)) Navigate(path);
            else if (File.Exists(path)) _ = OpenEntryAsync(MakeEntry(path, false));
            else ShowError("場所が見つかりません", new DirectoryNotFoundException(path));
        };
        list.Items.Add(button);
    }

    private void Remember(string path)
    {
        _state.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _state.Recent.Insert(0, path); if (_state.Recent.Count > 30) _state.Recent.RemoveRange(30, _state.Recent.Count - 30);
        StateStore.Save(_state); RefreshSidebar();
    }
    private void RekeyMetadata(string oldPath, string newPath)
    {
        string ReplacePath(string value) => string.Equals(value, oldPath, StringComparison.OrdinalIgnoreCase) ? newPath : value.StartsWith(oldPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? newPath + value[oldPath.Length..] : value;
        for (var i = 0; i < _state.Favorites.Count; i++) _state.Favorites[i] = ReplacePath(_state.Favorites[i]);
        for (var i = 0; i < _state.Recent.Count; i++) _state.Recent[i] = ReplacePath(_state.Recent[i]);
        foreach (var pair in _state.FolderIcons.ToArray())
        {
            var key = ReplacePath(pair.Key);
            if (!string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)) { _state.FolderIcons.Remove(pair.Key); _state.FolderIcons[key] = pair.Value; }
        }
        foreach (var pair in _state.ViewModes.ToArray())
        {
            var key = ReplacePath(pair.Key);
            if (!string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)) { _state.ViewModes.Remove(pair.Key); _state.ViewModes[key] = pair.Value; }
        }
        StateStore.Save(_state); RefreshSidebar();
    }
    private void UpdateTabHeaders()
    {
        foreach (var item in Tabs.Items.OfType<TabItem>())
        {
            if (item.Tag is not BrowserTab tab || item.Header is not StackPanel panel || panel.Children.Count == 0 || panel.Children[0] is not TextBlock label) continue;
            label.Text = tab.Title;
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItems.Count == 1 && FileList.SelectedItem is FileEntry entry && !entry.IsDirectory) ShowPreview(entry);
        else HidePreview();
        if (FileList.SelectedItems.Count > 0) StatusText.Text = $"{FileList.SelectedItems.Count} 個を選択";
    }
    private void ShowPreview(FileEntry entry)
    {
        PreviewColumn.Width = new GridLength(300); PreviewPanel.Visibility = Visibility.Visible; PreviewPanel.IsEnabled = true;
        PreviewName.Text = entry.Name; PreviewDetails.Text = $"{entry.Type}\n{entry.Size}\n{entry.FullPath}"; PreviewImage.Source = null; PreviewText.Text = "";
        var ext = System.IO.Path.GetExtension(entry.FullPath).ToLowerInvariant();
        try
        {
            if (new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff" }.Contains(ext))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(entry.FullPath); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 520; bitmap.EndInit(); PreviewImage.Source = bitmap;
                PreviewDetails.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}\n{entry.Size}\n{entry.FullPath}";
            }
            else if (new[] { ".txt", ".md", ".json", ".csv", ".log", ".xml" }.Contains(ext))
                PreviewText.Text = string.Join(Environment.NewLine, File.ReadLines(entry.FullPath).Take(180));
        }
        catch (Exception ex) { PreviewText.Text = "プレビューを表示できません: " + ex.Message; }
    }
    private void HidePreview() { PreviewColumn.Width = new GridLength(0); PreviewPanel.Visibility = Visibility.Collapsed; PreviewPanel.IsEnabled = false; }
    private void ClosePreview_Click(object sender, RoutedEventArgs e) => HidePreview();

    private List<FileEntry> SelectedEntries() => FileList.SelectedItems.Cast<FileEntry>().ToList();
    private void OpenMenu_Click(object sender, RoutedEventArgs e) { if (FileList.SelectedItem is FileEntry x) _ = OpenEntryAsync(x); }
    private void OpenFolderMenu_Click(object sender, RoutedEventArgs e) { if (FileList.SelectedItem is FileEntry x) Navigate(x.IsDirectory ? x.FullPath : System.IO.Path.GetDirectoryName(x.FullPath) ?? Location); }
    private void CopyMenu_Click(object sender, RoutedEventArgs e) => SetClipboard(false);
    private void CutMenu_Click(object sender, RoutedEventArgs e) => SetClipboard(true);
    private void PasteMenu_Click(object sender, RoutedEventArgs e) => _ = PasteAsync();
    private void SetClipboard(bool cut)
    {
        _clipboardPaths = SelectedEntries().Select(x => x.FullPath).ToList();
        _clipboardIsCut = cut;
        if (_clipboardPaths.Count > 0)
        {
            try
            {
                var paths = new StringCollection(); paths.AddRange(_clipboardPaths.ToArray());
                var data = new DataObject(); data.SetFileDropList(paths); data.SetData("Findr.InternalCut", cut);
                Clipboard.SetDataObject(data, true);
            }
            catch { }
        }
        StatusText.Text = cut ? "切り取りました" : "コピーしました";
    }

    private async Task PasteAsync()
    {
        if (Location.StartsWith("HOME:") || Location.StartsWith("PC:")) return;
        var pathsToPaste = _clipboardPaths;
        var cut = _clipboardIsCut;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                pathsToPaste = Clipboard.GetFileDropList().Cast<string>().ToList();
                cut = Clipboard.ContainsData("Findr.InternalCut") && Clipboard.GetData("Findr.InternalCut") is true;
            }
        }
        catch { }
        if (pathsToPaste.Count == 0) return;
        try
        {
            await Task.Run(() =>
            {
                foreach (var src in pathsToPaste)
                {
                    var target = System.IO.Path.Combine(Location, System.IO.Path.GetFileName(src.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
                    if (string.Equals(src.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"同じ名前の項目が既にあります: {System.IO.Path.GetFileName(target)}");
                    if (Directory.Exists(src)) { if (cut) { Directory.Move(src, target); Dispatcher.Invoke(() => RekeyMetadata(src, target)); } else CopyDirectory(src, target); }
                    else if (cut) File.Move(src, target); else File.Copy(src, target);
                }
            });
            if (cut) { _clipboardPaths.Clear(); _clipboardIsCut = false; }
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError("貼り付けできませんでした", ex); }
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            CopyDirectory(dir, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir)));
        }
    }

    private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries(); if (selected.Count == 0) return;
        if (MessageBox.Show($"{selected.Count} 個の項目をごみ箱に移動しますか？", "削除の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var x in selected)
                if (x.IsDirectory) FileSystem.DeleteDirectory(x.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(x.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError("削除できませんでした", ex); }
    }

    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry) return;
        var newName = AskText("名前の変更", "新しい名前", entry.Name); if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
        try { var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.FullPath)!, newName); if (entry.IsDirectory) Directory.Move(entry.FullPath, dest); else File.Move(entry.FullPath, dest); RekeyMetadata(entry.FullPath, dest); _ = RefreshAsync(); }
        catch (Exception ex) { ShowError("名前を変更できませんでした", ex); }
    }
    private void IconMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry { IsDirectory: true } entry) { MessageBox.Show("フォルダーを選択してください。"); return; }
        var dialog = new OpenFileDialog { Title = "フォルダーアイコン画像を選択", Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|すべてのファイル|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _state.FolderIcons[entry.FullPath] = dialog.FileName; StateStore.Save(_state); _ = RefreshAsync();
    }
    private void FavoriteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry) return;
        if (!_state.Favorites.Contains(entry.FullPath, StringComparer.OrdinalIgnoreCase)) _state.Favorites.Add(entry.FullPath);
        StateStore.Save(_state); RefreshSidebar();
    }
    private void PropertiesMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry) return;
        try
        {
            var info = entry.IsDirectory ? (FileSystemInfo)new DirectoryInfo(entry.FullPath) : new FileInfo(entry.FullPath);
            MessageBox.Show($"名前: {info.Name}\n種類: {(entry.IsDirectory ? "フォルダー" : entry.Type)}\n場所: {info.FullName}\n更新日時: {info.LastWriteTime:g}\nサイズ: {(entry.IsDirectory ? "—" : entry.Size)}", "プロパティ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("プロパティを取得できませんでした", ex); }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var create = new MenuItem { Header = "新しいフォルダー" }; create.Click += (_, _) => CreateFolder(); menu.Items.Add(create);
        var refresh = new MenuItem { Header = "更新" }; refresh.Click += async (_, _) => await RefreshAsync(); menu.Items.Add(refresh);
        var preview = new MenuItem { Header = "プレビューを閉じる" }; preview.Click += (_, _) => HidePreview(); menu.Items.Add(preview);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void CreateFolder()
    {
        if (Location.StartsWith("HOME:") || Location.StartsWith("PC:")) return;
        var name = AskText("新しいフォルダー", "フォルダー名", "新しいフォルダー"); if (string.IsNullOrWhiteSpace(name)) return;
        try { Directory.CreateDirectory(System.IO.Path.Combine(Location, name)); _ = RefreshAsync(); }
        catch (Exception ex) { ShowError("フォルダーを作成できませんでした", ex); }
    }
    private static string? AskText(string title, string prompt, string initial)
    {
        var dialog = new Window { Title = title, Width = 410, SizeToContent = SizeToContent.Height, MinHeight = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) }; Grid.SetRow(label, 0); grid.Children.Add(label);
        var text = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 14), MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center }; Grid.SetRow(text, 1); grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 }; var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 82 };
        ok.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(ok); buttons.Children.Add(cancel); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); dialog.Content = grid;
        dialog.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        return dialog.ShowDialog() == true ? text.Text.Trim() : null;
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        _tileMode = !_tileMode;
        _state.ViewModes[Location] = _tileMode ? "tiles" : "details";
        StateStore.Save(_state);
        ApplyViewMode(_tileMode, false);
    }

    private void ApplyViewMode(bool tiles, bool save)
    {
        _tileMode = tiles;
        if (tiles)
        {
            FileList.View = null;
            FileList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            FileList.ItemTemplate = (DataTemplate)Resources["TileTemplate"];
            FileList.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(WidthProperty, 154d), new Setter(HeightProperty, 144d), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center), new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center) } };
        }
        else
        {
            FileList.ItemTemplate = null; FileList.ItemContainerStyle = null;
            FileList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            FileList.View = _detailsView;
        }
        if (save) { _state.ViewModes[Location] = _tileMode ? "tiles" : "details"; StateStore.Save(_state); }
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || FileList.SelectedItems.Count == 0) return;
        var p = e.GetPosition(FileList);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = SelectedEntries().Select(x => x.FullPath).ToArray();
        if (paths.Length > 0)
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            data.SetData("Findr.InternalDrag", true);
            DragDrop.DoDragDrop(FileList, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
    }
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e) { _dragStart = e.GetPosition(FileList); base.OnPreviewMouseDown(e); }
    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Location.StartsWith("HOME:") || Location.StartsWith("PC:") || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var sources = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var targetPath = Location;
        if (FileList.InputHitTest(e.GetPosition(FileList)) is DependencyObject hit && ItemsControl.ContainerFromElement(FileList, hit) is ListViewItem row && row.DataContext is FileEntry { IsDirectory: true } folder)
            targetPath = folder.FullPath;
        var internalMove = e.Data.GetDataPresent("Findr.InternalDrag");
        try
        {
            await Task.Run(() =>
            {
                foreach (var src in sources)
                {
                    var dest = System.IO.Path.Combine(targetPath, System.IO.Path.GetFileName(src.TrimEnd('\\')));
                    if (string.Equals(System.IO.Path.GetDirectoryName(src)?.TrimEnd('\\'), targetPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(src) && targetPath.StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("フォルダーを自分自身の中へ移動できません。");
                    if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException($"同じ名前の項目が既にあります: {System.IO.Path.GetFileName(dest)}");
                    if (Directory.Exists(src)) { if (internalMove) Directory.Move(src, dest); else CopyDirectory(src, dest); }
                    else if (internalMove) File.Move(src, dest); else File.Copy(src, dest);
                    if (internalMove) Dispatcher.Invoke(() => RekeyMetadata(src, dest));
                }
            });
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError("ドロップした項目をコピーできませんでした", ex); }
    }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Handled || !e.Data.GetDataPresent(DataFormats.FileDrop)) return; e.Handled = true; FileList_Drop(sender, e); }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.F)) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C) { SetClipboard(false); e.Handled = true; }
            else if (e.Key == Key.X) { SetClipboard(true); e.Handled = true; }
            else if (e.Key == Key.V) { await PasteAsync(); e.Handled = true; }
            else if (e.Key == Key.T) { AddTab(Location); _ = RefreshAsync(); e.Handled = true; }
            else if (e.Key == Key.W) { if (ActiveTab is { } tab) CloseTab(tab); e.Handled = true; }
            else if (e.Key == Key.F) { SearchBox.Focus(); e.Handled = true; }
        }
        else if (e.Key == Key.F2) RenameMenu_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Delete) DeleteMenu_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Back) Up_Click(this, new RoutedEventArgs());
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Left) Back_Click(this, new RoutedEventArgs());
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Right) Forward_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Enter && FileList.IsKeyboardFocusWithin && FileList.SelectedItem is FileEntry x) await OpenEntryAsync(x);
    }

    private void ShowAccessError() => MessageBox.Show("このフォルダーにはアクセスできません。Windows のアクセス許可を確認してください。", "アクセスできません", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void ShowError(string message, Exception ex) => MessageBox.Show($"{message}\n\n{ex.Message}", "操作できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
}

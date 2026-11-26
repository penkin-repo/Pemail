using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using Pemail.Mime;
using Pemail.Models;
using Pemail.Net;
using Pemail.Storage;
using IOPath = System.IO.Path;

namespace Pemail.UI
{
    public partial class MainWindow : Window
    {
        private readonly ArchiveStore _store;
        private string _selectedAccountId;
        private string _selectedFolderDisplay;
        private bool _forceLocalOnly;
        private bool _suppressTreeSelection;
        private MailIndexEntry _currentEntry;
        private ParsedEmail _currentParsed;
        private bool _allowExternalImages;
        private bool _isSyncing;
        private readonly DispatcherTimer _searchDebounceTimer;
        private readonly DispatcherTimer _zoomPollTimer;
        private bool _suppressSearchSuggest;
        private bool _browserDocLoaded;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const uint OLECMDID_OPTICAL_ZOOM = 63;
        private const uint OLECMDEXECOPT_DONTPROMPTUSER = 2;

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleServiceProvider
        {
            [PreserveSig]
            int QueryService([In] ref Guid guidService, [In] ref Guid riid, [MarshalAs(UnmanagedType.IDispatch)] out object ppvObject);
        }

        [ComImport]
        [Guid("B722BCCB-4E68-101B-A2BC-00AA00404770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleCommandTarget
        {
            [PreserveSig]
            int QueryStatus(IntPtr pguidCmdGroup, uint cCmds, IntPtr prgCmds, IntPtr pCmdText);

            [PreserveSig]
            int Exec(IntPtr pguidCmdGroup, uint nCmdID, uint nCmdExecOpt,
                [In, MarshalAs(UnmanagedType.Struct)] ref object pvaIn,
                IntPtr pvaOut);
        }

        [ComImport]
        [Guid("B722BCCB-4E68-101B-A2BC-00AA00404770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleCommandTargetQuery
        {
            [PreserveSig]
            int QueryStatus(IntPtr pguidCmdGroup, uint cCmds, IntPtr prgCmds, IntPtr pCmdText);

            [PreserveSig]
            int Exec(IntPtr pguidCmdGroup, uint nCmdID, uint nCmdExecOpt,
                IntPtr pvaIn,
                [In, Out, MarshalAs(UnmanagedType.Struct)] ref object pvaOut);
        }

        private class FolderTag
        {
            public string AccountId { get; set; }
            public string FolderDisplay { get; set; }
            public bool LocalOnly { get; set; }
            public string Title { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
            _searchDebounceTimer = new DispatcherTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(160);
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
            _zoomPollTimer = new DispatcherTimer();
            _zoomPollTimer.Interval = TimeSpan.FromMilliseconds(120);
            _zoomPollTimer.Tick += ZoomPollTimer_Tick;
            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
            UpdateThemeIcon();
            UpdateZoomButton();
            string dataDir = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "PemailData");
            _store = new ArchiveStore(dataDir);
            RebuildFolderTree();
            RefreshMessageList();
            UpdateQuotaBar();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SyncZoomFromBrowser();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
            _zoomPollTimer.Stop();
            _searchDebounceTimer.Stop();
            base.OnClosed(e);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ModifierKeys mods = Keyboard.Modifiers;
            bool isFastSync = (e.Key == Key.R && mods == ModifierKeys.Control) ||
                              (e.Key == Key.F5 && mods == ModifierKeys.None);
            bool isFullSync = e.Key == Key.R && mods == (ModifierKeys.Control | ModifierKeys.Shift);

            if (isFastSync || isFullSync)
            {
                e.Handled = true;
                if (!_isSyncing && BtnSync.IsEnabled)
                {
                    RunSyncAsync(isFastSync);
                }
            }
        }

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message == WM_KEYDOWN && IsActive)
            {
                int vk = msg.wParam.ToInt32();
                ModifierKeys mods = Keyboard.Modifiers;
                bool isFastSync = (vk == 0x52 && mods == ModifierKeys.Control) ||
                                  (vk == 0x74 && mods == ModifierKeys.None);
                bool isFullSync = vk == 0x52 && mods == (ModifierKeys.Control | ModifierKeys.Shift);
                if (isFastSync || isFullSync)
                {
                    handled = true;
                    if (!_isSyncing && BtnSync.IsEnabled)
                    {
                        RunSyncAsync(isFastSync);
                    }
                    return;
                }
            }

            if (!_browserDocLoaded || BrowserBody.Visibility != Visibility.Visible) return;

            if (msg.message == WM_MOUSEWHEEL)
            {
                bool ctrlDown = ((msg.wParam.ToInt64() & 0x0008) != 0) ||
                                ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                if (ctrlDown)
                {
                    _zoomPollTimer.Stop();
                    _zoomPollTimer.Start();
                }
            }
            else if (msg.message == WM_KEYDOWN && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                int vk = msg.wParam.ToInt32();
                // Ctrl + '+', '-', '0' (основная клавиатура и Numpad)
                if (vk == 0xBB || vk == 0xBD || vk == 0x30 || vk == 0x6B || vk == 0x6D || vk == 0x60)
                {
                    _zoomPollTimer.Stop();
                    _zoomPollTimer.Start();
                }
            }
        }

        private void ZoomPollTimer_Tick(object sender, EventArgs e)
        {
            _zoomPollTimer.Stop();
            SyncZoomFromBrowser();
        }

        private object GetBrowserComObject()
        {
            try
            {
                IOleServiceProvider sp = BrowserBody.Document as IOleServiceProvider;
                if (sp != null)
                {
                    Guid sidWebBrowser = new Guid("0002DF05-0000-0000-C000-000000000046");
                    Guid iidWebBrowser2 = new Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E");
                    object wbObj;
                    if (sp.QueryService(ref sidWebBrowser, ref iidWebBrowser2, out wbObj) == 0 && wbObj != null)
                    {
                        return wbObj;
                    }
                }
            }
            catch { }

            try
            {
                FieldInfo fi = typeof(WebBrowser).GetField("_axIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi != null)
                {
                    return fi.GetValue(BrowserBody);
                }
            }
            catch { }

            return null;
        }

        private bool TryGetBrowserZoom(out int percent)
        {
            percent = 100;
            try
            {
                IOleCommandTargetQuery target = GetBrowserComObject() as IOleCommandTargetQuery;
                if (target == null) return false;
                object pvaOut = null;
                int hr = target.Exec(IntPtr.Zero, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, IntPtr.Zero, ref pvaOut);
                if (hr == 0 && pvaOut != null)
                {
                    int val = Convert.ToInt32(pvaOut);
                    if (val >= 50 && val <= 300)
                    {
                        percent = val;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void ApplyBrowserZoom(int percent)
        {
            if (percent < 50) percent = 50;
            if (percent > 300) percent = 300;
            try
            {
                IOleCommandTarget target = GetBrowserComObject() as IOleCommandTarget;
                if (target == null) return;
                object pvaIn = percent;
                target.Exec(IntPtr.Zero, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, ref pvaIn, IntPtr.Zero);
            }
            catch { }
        }

        private void SyncZoomFromBrowser()
        {
            if (!_browserDocLoaded || BrowserBody.Visibility != Visibility.Visible) return;
            int current;
            if (TryGetBrowserZoom(out current) && current != ThemeManager.MailZoomPercent)
            {
                ThemeManager.SetMailZoom(current, true);
                UpdateZoomButton();
            }
        }

        private void UpdateZoomButton()
        {
            if (BtnZoomReset == null) return;
            int z = ThemeManager.MailZoomPercent;
            BtnZoomReset.Content = z + "%";
            BtnZoomReset.Visibility = z != 100 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetMailZoom(100, true);
            ApplyBrowserZoom(100);
            UpdateZoomButton();
        }

        private void BrowserBody_LoadCompleted(object sender, NavigationEventArgs e)
        {
            int targetZoom = ThemeManager.MailZoomPercent;
            ApplyBrowserZoom(targetZoom);
            UpdateZoomButton();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                ApplyBrowserZoom(ThemeManager.MailZoomPercent);
                _browserDocLoaded = true;
            }));
        }

        private void UpdateThemeIcon()
        {
            string iconKey = ThemeManager.IsDark ? "IconSun" : "IconMoon";
            Geometry g = TryFindResource(iconKey) as Geometry;
            if (g != null) PathThemeIcon.Data = g;
            BtnTheme.ToolTip = ThemeManager.IsDark ? "Переключить на светлую тему" : "Переключить на мягкую тёмную тему";
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            SyncZoomFromBrowser();
            ThemeManager.Apply(!ThemeManager.IsDark, true);
            UpdateThemeIcon();
            if (_currentEntry != null && _currentParsed != null)
            {
                _browserDocLoaded = false;
                string safeHtml = MimeParser.BuildSafeHtml(_currentParsed, _allowExternalImages, ThemeManager.IsDark);
                BrowserBody.NavigateToString(safeHtml);
            }
        }

        private StackPanel BuildTreeHeader(string iconKey, string iconHex, string title, int count)
        {
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            Geometry geom = TryFindResource(iconKey) as Geometry;
            if (geom != null)
            {
                Viewbox vb = new Viewbox { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                Canvas c = new Canvas { Width = 24, Height = 24 };
                System.Windows.Shapes.Path p = new System.Windows.Shapes.Path
                {
                    Data = geom,
                    Stroke = (Brush)new BrushConverter().ConvertFromString(iconHex),
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                c.Children.Add(p);
                vb.Child = c;
                sp.Children.Add(vb);
            }

            TextBlock titleTb = new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            sp.Children.Add(titleTb);

            if (count >= 0)
            {
                TextBlock cntTb = new TextBlock
                {
                    Text = " (" + count + ")",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11
                };
                cntTb.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                sp.Children.Add(cntTb);
            }
            return sp;
        }

        private void ResolveFolderIcon(string folderName, out string iconKey, out string colorHex)
        {
            string f = (folderName ?? string.Empty).ToLowerInvariant();
            if (f == "inbox" || f == "входящие") { iconKey = "IconInbox"; colorHex = "#3B82F6"; return; }
            if (f.Contains("sent") || f.Contains("отправл")) { iconKey = "IconSend"; colorHex = "#06B6D4"; return; }
            if (f.Contains("draft") || f.Contains("чернов")) { iconKey = "IconCompose"; colorHex = "#F59E0B"; return; }
            if (f.Contains("archive") || f.Contains("архив")) { iconKey = "IconArchive"; colorHex = "#10B981"; return; }
            if (f.Contains("spam") || f.Contains("спам")) { iconKey = "IconAlert"; colorHex = "#EF4444"; return; }
            if (f.Contains("trash") || f.Contains("корзин") || f.Contains("удален")) { iconKey = "IconTrash"; colorHex = "#9CA3AF"; return; }
            iconKey = "IconFolder";
            colorHex = "#94A3B8";
        }

        private void RebuildFolderTree()
        {
            string prevAcc = _selectedAccountId;
            string prevFolder = _selectedFolderDisplay;
            bool prevLocal = _forceLocalOnly;

            _suppressTreeSelection = true;
            try
            {
                TreeFolders.Items.Clear();

                TreeViewItem itemToSelect = null;

                TreeViewItem allItem = new TreeViewItem
                {
                    Header = BuildTreeHeader("IconInbox", "#3B82F6", "Все письма", _store.Entries.Count),
                    Tag = new FolderTag { Title = "Все письма" }
                };
                TreeFolders.Items.Add(allItem);

                int archiveCount = _store.Entries.Count(e => e.IsArchived || !e.OnServer);
                TreeViewItem archiveItem = new TreeViewItem
                {
                    Header = BuildTreeHeader("IconArchive", "#10B981", "Локальный архив", archiveCount),
                    Tag = new FolderTag { LocalOnly = true, Title = "Локальный архив" }
                };
                TreeFolders.Items.Add(archiveItem);

                if (prevLocal) itemToSelect = archiveItem;

                foreach (AccountConfig acc in _store.Accounts)
                {
                    int accTotal = _store.Entries.Count(e => e.AccountId == acc.Id);
                    string accColor = acc.Provider == MailProvider.Yandex ? "#EF4444" : "#3B82F6";
                    TreeViewItem accNode = new TreeViewItem
                    {
                        Header = BuildTreeHeader("IconMail", accColor, acc.DisplayName, accTotal),
                        Tag = new FolderTag { AccountId = acc.Id, Title = acc.DisplayName },
                        IsExpanded = true
                    };

                    if (!prevLocal && prevAcc == acc.Id && string.IsNullOrEmpty(prevFolder))
                    {
                        itemToSelect = accNode;
                    }

                    var folders = _store.Entries
                        .Where(e => e.AccountId == acc.Id)
                        .GroupBy(e => e.FolderDisplay)
                        .OrderBy(g => FolderSortWeight(g.Key))
                        .ThenBy(g => g.Key);

                    foreach (var grp in folders)
                    {
                        string fIcon, fColor;
                        ResolveFolderIcon(grp.Key, out fIcon, out fColor);
                        TreeViewItem fNode = new TreeViewItem
                        {
                            Header = BuildTreeHeader(fIcon, fColor, grp.Key, grp.Count()),
                            Tag = new FolderTag { AccountId = acc.Id, FolderDisplay = grp.Key, Title = acc.DisplayName + " / " + grp.Key }
                        };
                        accNode.Items.Add(fNode);

                        if (!prevLocal && prevAcc == acc.Id && string.Equals(prevFolder, grp.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            itemToSelect = fNode;
                        }
                    }
                    TreeFolders.Items.Add(accNode);
                }

                if (itemToSelect == null) itemToSelect = allItem;
                itemToSelect.IsSelected = true;

                FolderTag tag = itemToSelect.Tag as FolderTag;
                if (tag != null)
                {
                    _selectedAccountId = tag.AccountId;
                    _selectedFolderDisplay = tag.FolderDisplay;
                    _forceLocalOnly = tag.LocalOnly;
                    TxtListHeader.Text = tag.Title;
                }
            }
            finally
            {
                _suppressTreeSelection = false;
            }
        }

        private static int FolderSortWeight(string folder)
        {
            string f = (folder ?? string.Empty).ToLowerInvariant();
            if (f == "inbox" || f == "входящие") return 0;
            if (f.Contains("sent") || f.Contains("отправл")) return 1;
            if (f.Contains("draft") || f.Contains("чернов")) return 2;
            if (f.Contains("archive") || f.Contains("архив")) return 3;
            if (f.Contains("spam") || f.Contains("спам")) return 8;
            if (f.Contains("trash") || f.Contains("корзин") || f.Contains("удален")) return 9;
            return 5;
        }

        private void TreeFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressTreeSelection) return;
            TreeViewItem item = TreeFolders.SelectedItem as TreeViewItem;
            FolderTag tag = item != null ? item.Tag as FolderTag : null;
            if (tag == null) return;

            _selectedAccountId = tag.AccountId;
            _selectedFolderDisplay = tag.FolderDisplay;
            _forceLocalOnly = tag.LocalOnly;
            TxtListHeader.Text = tag.Title;
            RefreshMessageList();
        }

        private void RefreshMessageList()
        {
            bool onlyAtt = TglAtt.IsChecked == true;
            bool onlyLarge = TglLarge.IsChecked == true;
            bool onlyLocal = _forceLocalOnly || (TglLocalOnly.IsChecked == true);
            bool onlyOnServer = !_forceLocalOnly && (TglOnServer.IsChecked == true);

            List<MailIndexEntry> items = _store.Query(
                _selectedAccountId,
                _selectedFolderDisplay,
                TxtSearch.Text,
                onlyAtt,
                onlyLocal,
                onlyOnServer,
                onlyLarge);

            LstMessages.ItemsSource = items;
            TxtListCount.Text = items.Count + " шт.";
            UpdateMultiSelectionUI();
        }

        private void UpdateQuotaBar()
        {
            int archived = _store.Entries.Count(e => e.IsArchived || !e.OnServer);
            if (_store.Accounts.Count == 0)
            {
                TxtQuotaBar.Text = "Нет аккаунтов  |  Всего: " + _store.Entries.Count + "  |  В архиве: " + archived;
                return;
            }
            string quotas = string.Join("  |  ", _store.Accounts.Select(a => a.FormatQuota()));
            TxtQuotaBar.Text = quotas + "  |  Писем: " + _store.Entries.Count + " (в архиве: " + archived + ")";
        }

        private void SearchDebounceTimer_Tick(object sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            RefreshMessageList();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool hasText = !string.IsNullOrEmpty(TxtSearch.Text);
            TxtSearchHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            BtnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

            if (!_suppressSearchSuggest)
            {
                UpdateSearchSuggestions();
            }

            _searchDebounceTimer.Stop();
            if (!hasText)
            {
                RefreshMessageList();
            }
            else
            {
                _searchDebounceTimer.Start();
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            PopupSearchSuggest.IsOpen = false;
            _searchDebounceTimer.Stop();
            TxtSearch.Text = string.Empty;
            TxtSearch.Focus();
        }

        private void GetActiveSearchTokenRange(out int tokenStart, out int tokenEnd, out string fieldPrefix, out string tokenValue)
        {
            string text = TxtSearch.Text ?? string.Empty;
            int caret = TxtSearch.CaretIndex;
            if (caret < 0) caret = 0;
            if (caret > text.Length) caret = text.Length;

            tokenStart = caret;
            while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
            {
                tokenStart--;
            }

            tokenEnd = caret;
            while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
            {
                tokenEnd++;
            }

            string raw = text.Substring(tokenStart, tokenEnd - tokenStart);
            fieldPrefix = string.Empty;
            tokenValue = raw;

            int colon = raw.IndexOf(':');
            if (colon > 0)
            {
                string p = raw.Substring(0, colon).ToLowerInvariant();
                if (p == "from" || p == "от" || p == "to" || p == "кому")
                {
                    fieldPrefix = raw.Substring(0, colon + 1);
                    tokenValue = raw.Substring(colon + 1);
                }
            }
        }

        private void UpdateSearchSuggestions()
        {
            if (!TxtSearch.IsKeyboardFocused)
            {
                PopupSearchSuggest.IsOpen = false;
                return;
            }

            int start, end;
            string fieldPrefix, tokenValue;
            GetActiveSearchTokenRange(out start, out end, out fieldPrefix, out tokenValue);

            if (string.IsNullOrWhiteSpace(tokenValue) || tokenValue.Length < 2 || tokenValue.StartsWith("\""))
            {
                PopupSearchSuggest.IsOpen = false;
                return;
            }

            List<ContactEntry> list = _store.SuggestContacts(tokenValue, 8);
            if (list.Count == 0 ||
                (list.Count == 1 && string.Equals(list[0].Email, tokenValue, StringComparison.OrdinalIgnoreCase)))
            {
                PopupSearchSuggest.IsOpen = false;
                return;
            }

            LstSearchSuggest.ItemsSource = list;
            LstSearchSuggest.SelectedIndex = -1;
            PopupSearchSuggest.IsOpen = true;
        }

        private void ApplySearchSuggestion(ContactEntry contact)
        {
            if (contact == null || string.IsNullOrWhiteSpace(contact.Email)) return;

            string text = TxtSearch.Text ?? string.Empty;
            int start, end;
            string fieldPrefix, tokenValue;
            GetActiveSearchTokenRange(out start, out end, out fieldPrefix, out tokenValue);

            string before = text.Substring(0, start);
            string after = end < text.Length ? text.Substring(end) : string.Empty;
            string insertion = fieldPrefix + contact.Email;
            bool needSpaceAfter = string.IsNullOrEmpty(after) || !char.IsWhiteSpace(after[0]);
            if (needSpaceAfter) insertion += " ";

            _suppressSearchSuggest = true;
            try
            {
                TxtSearch.Text = before + insertion + after;
                TxtSearch.CaretIndex = before.Length + insertion.Length;
            }
            finally
            {
                _suppressSearchSuggest = false;
            }

            PopupSearchSuggest.IsOpen = false;
            _searchDebounceTimer.Stop();
            RefreshMessageList();
            TxtSearch.Focus();
        }

        private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (PopupSearchSuggest.IsOpen && LstSearchSuggest.Items.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    if (LstSearchSuggest.SelectedIndex < 0)
                        LstSearchSuggest.SelectedIndex = 0;
                    else
                        LstSearchSuggest.SelectedIndex = (LstSearchSuggest.SelectedIndex + 1) % LstSearchSuggest.Items.Count;
                    LstSearchSuggest.ScrollIntoView(LstSearchSuggest.SelectedItem);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Up)
                {
                    if (LstSearchSuggest.SelectedIndex <= 0)
                        LstSearchSuggest.SelectedIndex = LstSearchSuggest.Items.Count - 1;
                    else
                        LstSearchSuggest.SelectedIndex--;
                    LstSearchSuggest.ScrollIntoView(LstSearchSuggest.SelectedItem);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Tab)
                {
                    ContactEntry sel = LstSearchSuggest.SelectedItem as ContactEntry;
                    if (sel == null) sel = LstSearchSuggest.Items[0] as ContactEntry;
                    if (sel != null)
                    {
                        ApplySearchSuggestion(sel);
                        e.Handled = true;
                        return;
                    }
                }
                if (e.Key == Key.Enter)
                {
                    ContactEntry sel = LstSearchSuggest.SelectedItem as ContactEntry;
                    if (sel != null)
                    {
                        ApplySearchSuggestion(sel);
                        e.Handled = true;
                        return;
                    }
                    PopupSearchSuggest.IsOpen = false;
                    _searchDebounceTimer.Stop();
                    RefreshMessageList();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    PopupSearchSuggest.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Enter)
            {
                _searchDebounceTimer.Stop();
                RefreshMessageList();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(TxtSearch.Text))
            {
                _searchDebounceTimer.Stop();
                TxtSearch.Text = string.Empty;
                e.Handled = true;
            }
        }

        private void TxtSearch_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!LstSearchSuggest.IsMouseOver && !PopupSearchSuggest.IsKeyboardFocusWithin)
            {
                PopupSearchSuggest.IsOpen = false;
            }
        }

        private void LstSearchSuggest_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject src = e.OriginalSource as DependencyObject;
            while (src != null && !(src is ListBoxItem))
            {
                src = VisualTreeHelper.GetParent(src);
            }
            ListBoxItem item = src as ListBoxItem;
            if (item != null)
            {
                ContactEntry contact = item.DataContext as ContactEntry;
                if (contact != null)
                {
                    ApplySearchSuggestion(contact);
                    e.Handled = true;
                }
            }
        }

        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender == TglLocalOnly && TglLocalOnly.IsChecked == true) TglOnServer.IsChecked = false;
            if (sender == TglOnServer && TglOnServer.IsChecked == true) TglLocalOnly.IsChecked = false;
            RefreshMessageList();
        }

        private List<MailIndexEntry> GetSelectedEntries()
        {
            List<MailIndexEntry> list = new List<MailIndexEntry>();
            foreach (object obj in LstMessages.SelectedItems)
            {
                MailIndexEntry entry = obj as MailIndexEntry;
                if (entry != null) list.Add(entry);
            }
            if (list.Count == 0 && _currentEntry != null) list.Add(_currentEntry);
            return list;
        }

        private void UpdateMultiSelectionUI()
        {
            int count = LstMessages.SelectedItems.Count;
            if (count > 1)
            {
                PanelMultiActions.Visibility = Visibility.Visible;
                TxtMultiSelected.Text = "Выбрано писем: " + count;
                int onServerCount = 0;
                foreach (object o in LstMessages.SelectedItems)
                {
                    MailIndexEntry e = o as MailIndexEntry;
                    if (e != null && e.OnServer && e.Uid > 0) onServerCount++;
                }
                BtnMultiArchive.Visibility = onServerCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                BtnMultiArchive.Content = "В архив (" + onServerCount + ")";
                BtnMultiDelete.Content = "Удалить (" + count + ")";
                TxtBtnArchive.Text = "В архив (" + onServerCount + ")";
                TxtBtnDelete.Text = "Удалить (" + count + ")";
            }
            else
            {
                PanelMultiActions.Visibility = Visibility.Collapsed;
                TxtBtnArchive.Text = "В архив (убрать с сервера)";
                TxtBtnDelete.Text = "Удалить";
            }
        }

        private void LstMessages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMultiSelectionUI();
            MailIndexEntry entry = LstMessages.SelectedItem as MailIndexEntry;
            if (entry == null) return;
            if (entry != _currentEntry)
            {
                OpenMessagePreview(entry, false);
            }
        }

        private void LstMessages_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && LstMessages.SelectedItems.Count > 0)
            {
                e.Handled = true;
                BtnDeleteForever_Click(sender, new RoutedEventArgs());
            }
        }

        private void ClearPreview()
        {
            SyncZoomFromBrowser();
            _browserDocLoaded = false;
            _currentEntry = null;
            _currentParsed = null;
            PanelHeader.Visibility = Visibility.Collapsed;
            PanelAttachmentsBar.Visibility = Visibility.Collapsed;
            BrowserBody.Visibility = Visibility.Collapsed;
            TxtEmptyPreview.Text = "Выберите письмо для просмотра.";
            TxtEmptyPreview.Visibility = Visibility.Visible;
        }

        private void OpenMessagePreview(MailIndexEntry entry, bool allowExternalImages)
        {
            SyncZoomFromBrowser();
            _currentEntry = entry;
            _allowExternalImages = allowExternalImages;

            byte[] raw = _store.ReadRawEml(entry);
            if (raw == null)
            {
                PanelHeader.Visibility = Visibility.Collapsed;
                PanelAttachmentsBar.Visibility = Visibility.Collapsed;
                BrowserBody.Visibility = Visibility.Collapsed;
                TxtEmptyPreview.Text = "Локальный файл письма не найден на диске.";
                TxtEmptyPreview.Visibility = Visibility.Visible;
                return;
            }

            _currentParsed = MimeParser.Parse(raw);
            PanelHeader.Visibility = Visibility.Visible;
            TxtEmptyPreview.Visibility = Visibility.Collapsed;
            BrowserBody.Visibility = Visibility.Visible;

            TxtMailSubject.Text = string.IsNullOrWhiteSpace(_currentParsed.Subject) ? "(без темы)" : _currentParsed.Subject;
            string dateStr = entry.DateUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            string statusStr = !entry.OnServer
                ? "в локальном архиве (на сервере удалено)"
                : (entry.IsArchived ? "на сервере + закреплено в архиве" : "на сервере");
            TxtMailMeta.Text = string.Format("От: {0}\nКому: {1}   |   {2}   |   {3}   |   {4}",
                _currentParsed.From,
                _currentParsed.To,
                dateStr,
                entry.SizeShort,
                statusStr);

            BtnArchiveSingle.Visibility = (entry.OnServer && entry.Uid > 0) || LstMessages.SelectedItems.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnLoadImages.Visibility = !allowExternalImages ? Visibility.Visible : Visibility.Collapsed;

            BuildAttachmentPreviewCards(_currentParsed.Attachments);

            _browserDocLoaded = false;
            string safeHtml = MimeParser.BuildSafeHtml(_currentParsed, allowExternalImages, ThemeManager.IsDark);
            BrowserBody.NavigateToString(safeHtml);
        }

        private void BuildAttachmentPreviewCards(List<ParsedAttachment> attachments)
        {
            WrapAttachmentPreviews.Children.Clear();
            List<ParsedAttachment> visible = attachments.Where(a => !a.IsInline).ToList();
            if (visible.Count == 0)
            {
                PanelAttachmentsBar.Visibility = Visibility.Collapsed;
                return;
            }

            PanelAttachmentsBar.Visibility = Visibility.Visible;
            BrushConverter bc = new BrushConverter();

            foreach (ParsedAttachment att in visible)
            {
                ParsedAttachment captured = att;
                AttachmentStyleInfo st = AttachmentBadge.GetStyleForFile(att.FileName);
                Brush bgBrush = (Brush)bc.ConvertFromString(st.BackgroundHex);
                Brush borderBrush = (Brush)bc.ConvertFromString(st.BorderHex);
                Brush fgBrush = (Brush)bc.ConvertFromString(st.ForegroundHex);
                Brush tagBgBrush = (Brush)bc.ConvertFromString(st.TagBgHex);

                Border card = new Border
                {
                    Width = 114,
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = att.FileName + " (" + att.SizeText + ") — ЛКМ: открыть, ПКМ: сохранить"
                };

                StackPanel sp = new StackPanel();

                Border thumbBox = new Border
                {
                    Height = 46,
                    Background = tagBgBrush,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 0, 4),
                    ClipToBounds = true
                };

                ImageSource thumb = TryCreateThumbnail(att);
                if (thumb != null)
                {
                    thumbBox.Child = new Image
                    {
                        Source = thumb,
                        Stretch = Stretch.UniformToFill,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                else
                {
                    string ext = IOPath.GetExtension(att.FileName ?? string.Empty).TrimStart('.').ToUpperInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = "FILE";
                    if (ext.Length > 5) ext = ext.Substring(0, 5);
                    Border extPill = new Border
                    {
                        Background = fgBrush,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 2, 7, 2),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    extPill.Child = new TextBlock
                    {
                        Text = ext,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White
                    };
                    thumbBox.Child = extPill;
                }

                sp.Children.Add(thumbBox);
                sp.Children.Add(new TextBlock
                {
                    Text = AttachmentBadge.MakeShortName(att.FileName, 14),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fgBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                sp.Children.Add(new TextBlock
                {
                    Text = att.SizeText,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                });

                card.Child = sp;
                card.MouseLeftButtonUp += delegate { OpenAttachmentTemp(captured); };
                card.MouseRightButtonUp += delegate { SaveSingleAttachment(captured); };
                WrapAttachmentPreviews.Children.Add(card);
            }
        }

        private static ImageSource TryCreateThumbnail(ParsedAttachment att)
        {
            if (att == null || !att.IsImage || att.Data == null || att.Data.Length == 0 || att.Data.Length > 8 * 1024 * 1024)
            {
                return null;
            }
            try
            {
                BitmapImage bmp = new BitmapImage();
                using (MemoryStream ms = new MemoryStream(att.Data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 100;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void OpenAttachmentTemp(ParsedAttachment att)
        {
            try
            {
                string dir = IOPath.Combine(IOPath.GetTempPath(), "PemailPreview");
                Directory.CreateDirectory(dir);
                string filePath = IOPath.Combine(dir, Guid.NewGuid().ToString("N").Substring(0, 6) + "_" + att.FileName);
                File.WriteAllBytes(filePath, att.Data ?? new byte[0]);
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть вложение: " + ex.Message, "Вложение", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSingleAttachment(ParsedAttachment att)
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = att.FileName;
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllBytes(dlg.FileName, att.Data ?? new byte[0]);
            TxtStatusBar.Text = "Вложение сохранено: " + dlg.FileName;
        }

        private void BtnSaveAllAtt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentParsed == null) return;
            List<ParsedAttachment> visible = _currentParsed.Attachments.Where(a => !a.IsInline).ToList();
            if (visible.Count == 0) return;

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = visible[0].FileName;
            dlg.Title = "Выберите папку (все вложения сохранятся рядом с этим файлом)";
            if (dlg.ShowDialog(this) != true) return;

            string dir = IOPath.GetDirectoryName(dlg.FileName);
            foreach (ParsedAttachment att in visible)
            {
                string target = IOPath.Combine(dir, att.FileName);
                File.WriteAllBytes(target, att.Data ?? new byte[0]);
            }
            TxtStatusBar.Text = "Сохранено вложений: " + visible.Count + " в " + dir;
        }

        private void BrowserBody_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            if (e.Uri != null && (e.Uri.Scheme == "http" || e.Uri.Scheme == "https" || e.Uri.Scheme == "mailto"))
            {
                e.Cancel = true;
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
            }
        }

        private void BtnLoadImages_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEntry != null)
            {
                OpenMessagePreview(_currentEntry, true);
            }
        }

        private void BtnExportEml_Click(object sender, RoutedEventArgs e)
        {
            MailIndexEntry target = _currentEntry ?? (LstMessages.SelectedItem as MailIndexEntry);
            if (target == null) return;
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "Email message (*.eml)|*.eml";
            dlg.FileName = (target.Uid > 0 ? target.Uid.ToString() : target.Id) + ".eml";
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                _store.ExportAsEml(target, dlg.FileName);
                TxtStatusBar.Text = "Экспортировано в " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка экспорта: " + ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnArchiveSingle_Click(object sender, RoutedEventArgs e)
        {
            List<MailIndexEntry> selected = GetSelectedEntries()
                .Where(x => x.OnServer && x.Uid > 0 && _store.VerifyLocalCopy(x))
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Среди выбранных писем нет писем на сервере с подтверждённой локальной копией.", "В архив", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string prompt = selected.Count == 1
                ? "Перенести это письмо в «Локальный архив» и удалить его с сервера (освободить место)?"
                : string.Format("Перенести {0} писем в «Локальный архив» и удалить их с сервера (освободить место)?", selected.Count);
            if (MessageBox.Show(prompt, "В локальный архив", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            BtnArchiveSingle.IsEnabled = false;
            BtnMultiArchive.IsEnabled = false;
            PrgSync.Visibility = Visibility.Visible;
            TxtStatusBar.Text = "Архивация и удаление с сервера (" + selected.Count + " шт.)...";

            try
            {
                await Task.Run(() =>
                {
                    foreach (var byAcc in selected.GroupBy(x => x.AccountId))
                    {
                        AccountConfig acc = _store.Accounts.FirstOrDefault(a => a.Id == byAcc.Key);
                        if (acc == null) continue;

                        using (ImapClient imap = new ImapClient())
                        {
                            imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                            List<MailFolderInfo> folders = imap.ListFolders();
                            MailFolderInfo trash = folders.FirstOrDefault(f => f.IsTrash && f.IsSelectable);
                            string trashRaw = trash != null ? trash.RawName : null;

                            foreach (var byFolder in byAcc.GroupBy(x => x.FolderRaw))
                            {
                                List<MailIndexEntry> items = byFolder.ToList();
                                for (int i = 0; i < items.Count; i += 50)
                                {
                                    List<MailIndexEntry> chunk = items.Skip(i).Take(50).ToList();
                                    List<long> uids = chunk.Select(c => c.Uid).ToList();
                                    imap.DeleteMessagesFromServer(byFolder.Key, uids, trashRaw);
                                    foreach (MailIndexEntry c in chunk)
                                    {
                                        c.OnServer = false;
                                        c.IsArchived = true;
                                    }
                                }
                            }
                        }
                    }
                    _store.SaveIndex();
                });

                TxtStatusBar.Text = "В локальный архив перенесено писем: " + selected.Count + " (с сервера удалены).";
                RebuildFolderTree();
                RefreshMessageList();
                UpdateQuotaBar();
                if (_currentEntry != null) OpenMessagePreview(_currentEntry, _allowExternalImages);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message, "IMAP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PrgSync.Visibility = Visibility.Collapsed;
                BtnArchiveSingle.IsEnabled = true;
                BtnMultiArchive.IsEnabled = true;
            }
        }

        private async void BtnDeleteForever_Click(object sender, RoutedEventArgs e)
        {
            List<MailIndexEntry> selected = GetSelectedEntries();
            if (selected.Count == 0) return;

            bool anyOnServer = selected.Any(x => x.OnServer && x.Uid > 0);
            string msg = selected.Count == 1
                ? (anyOnServer
                    ? "Удалить письмо ПОЛНОСТЬЮ — и с сервера, и с компьютера?"
                    : "Удалить письмо из локального архива на компьютере без возможности восстановления?")
                : (anyOnServer
                    ? string.Format("Удалить выбранные {0} писем ПОЛНОСТЬЮ — и с сервера, и с компьютера?", selected.Count)
                    : string.Format("Удалить выбранные {0} писем из локального архива без возможности восстановления?", selected.Count));

            if (MessageBox.Show(msg, "Удаление писем", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            BtnDeleteForever.IsEnabled = false;
            BtnMultiDelete.IsEnabled = false;
            PrgSync.Visibility = Visibility.Visible;
            TxtStatusBar.Text = "Удаление (" + selected.Count + " шт.)...";

            try
            {
                List<MailIndexEntry> onServer = selected.Where(x => x.OnServer && x.Uid > 0).ToList();
                if (onServer.Count > 0)
                {
                    await Task.Run(() =>
                    {
                        foreach (var byAcc in onServer.GroupBy(x => x.AccountId))
                        {
                            AccountConfig acc = _store.Accounts.FirstOrDefault(a => a.Id == byAcc.Key);
                            if (acc == null) continue;

                            using (ImapClient imap = new ImapClient())
                            {
                                imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                                List<MailFolderInfo> folders = imap.ListFolders();
                                MailFolderInfo trash = folders.FirstOrDefault(f => f.IsTrash && f.IsSelectable);
                                string trashRaw = trash != null ? trash.RawName : null;

                                foreach (var byFolder in byAcc.GroupBy(x => x.FolderRaw))
                                {
                                    List<MailIndexEntry> items = byFolder.ToList();
                                    for (int i = 0; i < items.Count; i += 50)
                                    {
                                        List<MailIndexEntry> chunk = items.Skip(i).Take(50).ToList();
                                        List<long> uids = chunk.Select(c => c.Uid).ToList();
                                        imap.DeleteMessagesFromServer(byFolder.Key, uids, trashRaw);
                                    }
                                }
                            }
                        }
                    });
                }

                foreach (MailIndexEntry entry in selected)
                {
                    _store.DeleteLocalEntry(entry);
                }

                ClearPreview();
                RebuildFolderTree();
                RefreshMessageList();
                UpdateQuotaBar();
                TxtStatusBar.Text = "Удалено писем: " + selected.Count + ".";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка удаления: " + ex.Message, "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PrgSync.Visibility = Visibility.Collapsed;
                BtnDeleteForever.IsEnabled = true;
                BtnMultiDelete.IsEnabled = true;
            }
        }

        private void BtnAccounts_Click(object sender, RoutedEventArgs e)
        {
            AccountsWindow win = new AccountsWindow(_store) { Owner = this };
            win.ShowDialog();
            if (win.Changed)
            {
                RebuildFolderTree();
                RefreshMessageList();
                UpdateQuotaBar();
            }
        }

        private void BtnCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_store.Accounts.Count == 0)
            {
                MessageBox.Show("Сначала добавьте хотя бы один почтовый аккаунт.", "Очистка", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CleanupWindow win = new CleanupWindow(_store) { Owner = this };
            win.ShowDialog();
            if (win.Modified)
            {
                RebuildFolderTree();
                RefreshMessageList();
                UpdateQuotaBar();
            }
        }

        private void BtnCompose_Click(object sender, RoutedEventArgs e)
        {
            OpenCompose(null, null, null, null, null, null);
        }

        private void BtnBatchCompose_Click(object sender, RoutedEventArgs e)
        {
            OpenCompose(null, null, null, null, null, null, null, true);
        }

        private void BtnReply_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEntry == null || _currentParsed == null) return;
            AccountConfig acc = _store.Accounts.FirstOrDefault(a => a.Id == _currentEntry.AccountId);
            string to = SmtpClientEx.ExtractEmailAddress(_currentParsed.From);
            string subj = _currentParsed.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? _currentParsed.Subject
                : "Re: " + _currentParsed.Subject;
            string quote = BuildQuote(_currentParsed);
            string refs = string.IsNullOrWhiteSpace(_currentParsed.References)
                ? _currentParsed.MessageId
                : _currentParsed.References + " " + _currentParsed.MessageId;
            OpenCompose(acc, to, string.Empty, subj, quote, _currentParsed.MessageId, refs);
        }

        private void BtnReplyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEntry == null || _currentParsed == null) return;
            AccountConfig acc = _store.Accounts.FirstOrDefault(a => a.Id == _currentEntry.AccountId);
            string myEmail = acc != null ? acc.Email.ToLowerInvariant() : string.Empty;
            string to = SmtpClientEx.ExtractEmailAddress(_currentParsed.From);

            List<string> ccAll = new List<string>();
            ccAll.AddRange(SmtpClientEx.ParseRecipients(_currentParsed.To));
            ccAll.AddRange(SmtpClientEx.ParseRecipients(_currentParsed.Cc));
            ccAll = ccAll.Where(a => !a.Equals(myEmail, StringComparison.OrdinalIgnoreCase) && !a.Equals(to, StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string subj = _currentParsed.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? _currentParsed.Subject
                : "Re: " + _currentParsed.Subject;
            string quote = BuildQuote(_currentParsed);
            string refs = string.IsNullOrWhiteSpace(_currentParsed.References)
                ? _currentParsed.MessageId
                : _currentParsed.References + " " + _currentParsed.MessageId;
            OpenCompose(acc, to, string.Join(", ", ccAll), subj, quote, _currentParsed.MessageId, refs);
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEntry == null || _currentParsed == null) return;
            AccountConfig acc = _store.Accounts.FirstOrDefault(a => a.Id == _currentEntry.AccountId);
            string subj = _currentParsed.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
                ? _currentParsed.Subject
                : "Fwd: " + _currentParsed.Subject;
            string quote = "\r\n\r\n---------- Пересылаемое письмо ----------\r\nОт: " + _currentParsed.From +
                           "\r\nКому: " + _currentParsed.To +
                           "\r\nТема: " + _currentParsed.Subject + "\r\n\r\n" +
                           MimeParser.ExtractPlainTextForSearch(_currentParsed);
            OpenCompose(acc, string.Empty, string.Empty, subj, quote, null, null);
        }

        private static string BuildQuote(ParsedEmail email)
        {
            string plain = MimeParser.ExtractPlainTextForSearch(email);
            return "\r\n\r\n--- " + email.From + " писал(а): ---\r\n> " + plain.Replace("\n", "\n> ");
        }

        private void OpenCompose(AccountConfig acc, string to, string cc, string subj, string body, string inReplyTo, string references = null, bool startInBatchMode = false)
        {
            if (_store.Accounts.Count == 0)
            {
                MessageBox.Show("Сначала добавьте аккаунт через «Аккаунты».", "Отправка", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ComposeWindow win = new ComposeWindow(_store, acc, to, cc, subj, body, inReplyTo, references, startInBatchMode) { Owner = this };
            win.ShowDialog();
            if (win.SentSuccessfully)
            {
                RebuildFolderTree();
                RefreshMessageList();
                TxtStatusBar.Text = win.SentCount > 1
                    ? string.Format("Рассылка завершена: отправлено писем — {0}.", win.SentCount)
                    : "Письмо отправлено и сохранено.";
            }
        }

        private void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            RunSyncAsync(false);
        }

        private async void RunSyncAsync(bool fastInboxOnly)
        {
            if (_isSyncing) return;
            if (_store.Accounts.Count == 0)
            {
                BtnAccounts_Click(BtnAccounts, new RoutedEventArgs());
                return;
            }

            _isSyncing = true;
            BtnSync.IsEnabled = false;
            PrgSync.Visibility = Visibility.Visible;
            int totalDownloaded = 0;
            int totalReconciled = 0;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                List<AccountConfig> accounts = new List<AccountConfig>(_store.Accounts);
                await Task.Run(() =>
                {
                    foreach (AccountConfig acc in accounts)
                    {
                        SetStatus((fastInboxOnly ? "Быстрая проверка Входящих: " : "Синхронизация: ") + acc.DisplayName + "...");
                        try
                        {
                            using (ImapClient imap = new ImapClient())
                            {
                                imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());

                                List<MailFolderInfo> folders;
                                if (fastInboxOnly)
                                {
                                    MailIndexEntry existingInbox = _store.Entries.FirstOrDefault(x =>
                                        x.AccountId == acc.Id &&
                                        string.Equals(x.FolderRaw, "INBOX", StringComparison.OrdinalIgnoreCase));
                                    MailFolderInfo inbox = new MailFolderInfo
                                    {
                                        RawName = existingInbox != null ? existingInbox.FolderRaw : "INBOX",
                                        DisplayName = existingInbox != null ? existingInbox.FolderDisplay : "INBOX",
                                        IsSelectable = true
                                    };
                                    folders = new List<MailFolderInfo> { inbox };
                                }
                                else
                                {
                                    long usedKb, limitKb;
                                    if (imap.TryGetQuota(out usedKb, out limitKb))
                                    {
                                        acc.QuotaUsedKb = usedKb;
                                        acc.QuotaLimitKb = limitKb;
                                        _store.SaveAccounts();
                                    }

                                    folders = imap.ListFolders()
                                        .Where(f => f.IsSelectable && !f.IsAllMail)
                                        .ToList();
                                }

                                foreach (MailFolderInfo folder in folders)
                                {
                                    SetStatus((fastInboxOnly ? "Быстрая проверка: " : string.Empty) + acc.DisplayName + " / " + folder.DisplayName + "...");
                                    ImapSelectResult sel = imap.SelectFolder(folder.RawName, !acc.MarkReadOnServer);

                                    List<long> uids = sel.Exists > 0 ? imap.SearchAllUids() : new List<long>();
                                    HashSet<long> serverUidSet = new HashSet<long>(uids);

                                    int rec = _store.ReconcileFolderServerUids(acc.Id, folder.RawName, sel.UidValidity, serverUidSet);
                                    totalReconciled += rec;

                                    HashSet<long> localUids = _store.GetFolderLocalUids(acc.Id, folder.RawName, sel.UidValidity);
                                    int downloadedInFolder = 0;
                                    for (int i = uids.Count - 1; i >= 0; i--)
                                    {
                                        long uid = uids[i];
                                        if (localUids.Contains(uid)) continue;

                                        ImapFetchedMessage fetched = imap.FetchMessageByUid(uid, acc.MarkReadOnServer);
                                        if (fetched.RawBytes == null || fetched.RawBytes.Length == 0) continue;

                                        _store.SaveMessage(acc.Id, folder.RawName, folder.DisplayName, sel.UidValidity, uid, fetched.RawBytes, fetched.IsRead, true, false);
                                        downloadedInFolder++;
                                        totalDownloaded++;

                                        if (downloadedInFolder % 15 == 0)
                                        {
                                            _store.SaveIndex();
                                            SetStatus(string.Format("{0} / {1}: скачано {2}...", acc.DisplayName, folder.DisplayName, downloadedInFolder));
                                        }
                                    }

                                    if (downloadedInFolder > 0 || rec > 0)
                                    {
                                        _store.SaveIndex();
                                    }
                                }
                            }
                        }
                        catch (Exception accEx)
                        {
                            SetStatus("Ошибка (" + acc.DisplayName + "): " + accEx.Message);
                        }
                    }
                });

                sw.Stop();
                string secStr = string.Format("{0:0.0} с", sw.Elapsed.TotalSeconds);

                RebuildFolderTree();
                RefreshMessageList();
                UpdateQuotaBar();
                if (_currentEntry != null && !_store.Entries.Contains(_currentEntry))
                {
                    ClearPreview();
                }

                string label = fastInboxOnly ? "Входящие проверены" : "Синхронизация завершена";
                if (totalDownloaded > 0 || totalReconciled > 0)
                {
                    TxtStatusBar.Text = string.Format("{0} за {1}. Новых: {2}, удалено/обновлено с сервера: {3}.", label, secStr, totalDownloaded, totalReconciled);
                }
                else
                {
                    TxtStatusBar.Text = string.Format("{0} за {1}. Изменений нет.", label, secStr);
                }
            }
            finally
            {
                _isSyncing = false;
                BtnSync.IsEnabled = true;
                PrgSync.Visibility = Visibility.Collapsed;
            }
        }

        private void SetStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => { TxtStatusBar.Text = text; }));
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMM4SettingSearch;

public static class SettingsSearchController
{
    static readonly DependencyProperty attachedProperty = DependencyProperty.RegisterAttached(
        "SearchAttached",
        typeof(bool),
        typeof(SettingsSearchController),
        new PropertyMetadata(false));

    public static void Attach(FrameworkElement view)
    {
        if (view == null || (bool)view.GetValue(attachedProperty))
        {
            return;
        }
        view.SetValue(attachedProperty, true);
        try
        {
            InjectSearchArea(view);
        }
        catch (Exception ex)
        {
            Log.Default.Write("YMM4SettingSearch: 検索UIの注入に失敗しました", ex);
        }
    }

    static void InjectSearchArea(FrameworkElement view)
    {
        var content = view is ContentControl contentControl ? contentControl.Content as Grid : null;
        if (content == null)
        {
            return;
        }
        var listOfCategories = content.Children.OfType<ListBox>().FirstOrDefault();
        if (listOfCategories == null)
        {
            Log.Default.Write("YMM4SettingSearch: カテゴリ一覧ListBoxが見つかりません");
            return;
        }
        var controller = new SettingsSearchArea(view, content, listOfCategories);
        controller.Build();
        controller.AttachDataContextHook();
        Log.Default.Write("YMM4SettingSearch: 設定画面に検索UIを注入しました");
    }

    sealed class SettingsSearchArea
    {
        readonly FrameworkElement view;
        readonly Grid rootGrid;
        readonly ListBox categoryList;
        readonly SettingSearchIndex index = new();
        readonly DispatcherTimer searchTimer;
        readonly Dictionary<FrameworkElement, Visibility> elementVisibilityState = new();
        readonly Dictionary<FrameworkElement, object?> itemHighlightState = new();
        readonly Dictionary<FrameworkElement, object?> rowHighlightState = new();
        readonly HashSet<Expander> hookedExpanders = new();
        static readonly Brush highlightBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xC9, 0x5C));
        ISet<ISetting>? lastMatched;
        TextBox? searchBox;
        TextBlock? watermark;
        ContentControl? pageContent;
        DispatcherTimer? pageFilterRetryTimer;
        int pageFilterAttempts;

        public SettingsSearchArea(FrameworkElement view, Grid rootGrid, ListBox categoryList)
        {
            this.view = view;
            this.rootGrid = rootGrid;
            this.categoryList = categoryList;
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            searchTimer.Tick += (s, e) =>
            {
                searchTimer.Stop();
                ApplySearch();
            };
            SettingSearchSettings.Changed += OnSettingsChanged;
        }

        void OnSettingsChanged()
        {
            view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplySearch());
        }

        public void Build()
        {
            InsertRow();
            var border = new Border
            {
                Padding = new Thickness(4, 3, 4, 2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Grid.SetRow(border, 0);
            Grid.SetColumnSpan(border, 3);

            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            watermark = new TextBlock
            {
                Text = "設定を検索…",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                IsHitTestVisible = false,
            };

            searchBox = new TextBox
            {
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "設定を検索 (スペース区切りで AND、「re:」で正規表現)",
            };
            searchBox.TextChanged += (s, e) =>
            {
                searchTimer.Stop();
                searchTimer.Start();
                if (watermark != null)
                {
                    watermark.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                }
            };
            searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (searchBox != null)
                    {
                        searchBox.Text = "";
                    }
                    e.Handled = true;
                }
            };

            var clearButton = new Button
            {
                Content = "×",
                Width = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "クリア",
                Focusable = false,
            };
            clearButton.Click += (s, e) =>
            {
                if (searchBox != null)
                {
                    searchBox.Text = "";
                }
            };

            var watermarkGrid = new Grid();
            watermarkGrid.Children.Add(searchBox);
            watermarkGrid.Children.Add(watermark);

            Grid.SetColumn(watermarkGrid, 0);
            Grid.SetColumn(clearButton, 1);
            inner.Children.Add(watermarkGrid);
            inner.Children.Add(clearButton);
            border.Child = inner;
            rootGrid.Children.Add(border);

            searchBox.Loaded += (s, e) => searchBox.Focus();

            categoryList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) =>
            {
                if (SettingSearchSettings.Default.HighlightResults && (e.VerticalChange != 0 || e.ExtentHeightChange != 0))
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyItemFilter());
                }
            }));

            pageContent = rootGrid.Children.OfType<ContentControl>().FirstOrDefault();
            HookPageContent();
        }

        void HookPageContent()
        {
            if (pageContent == null)
            {
                return;
            }
            var descriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
            descriptor?.AddValueChanged(pageContent, (s, e) =>
            {
                RestartPageFilterRetry();
            });
        }

        void RestartPageFilterRetry()
        {
            pageFilterRetryTimer?.Stop();
            pageFilterAttempts = 0;
            pageFilterRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            pageFilterRetryTimer.Tick += (s, e) =>
            {
                pageFilterAttempts++;
                var done = ApplyPageFilter();
                if (done || pageFilterAttempts >= 20)
                {
                    pageFilterRetryTimer?.Stop();
                }
            };
            pageFilterRetryTimer.Start();
        }

        void InsertRow()
        {
            if (rootGrid.RowDefinitions.Count == 0)
            {
                // YMM4設定画面のルートGridは列定義のみ。コンテンツ用のStar行を用意してから検索バー行を挿入する
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                rootGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
            }
            foreach (var child in rootGrid.Children.OfType<UIElement>().ToArray())
            {
                Grid.SetRow(child, Grid.GetRow(child) + 1);
            }
        }

        public void StartIndexing()
        {
            IndexFromViewModel();
            if (index.Settings.Count == 0)
            {
                return;
            }
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => index.StartDeepIndex(view.DataContext));
        }

        public void AttachDataContextHook()
        {
            if (view.DataContext != null)
            {
                StartIndexing();
                return;
            }
            view.DataContextChanged += (s, e) =>
            {
                if (e.NewValue != null)
                {
                    StartIndexing();
                    Log.Default.Write("YMM4SettingSearch: 設定データを " + index.Settings.Count + " 件インデックス化しました");
                }
            };
        }

        void IndexFromViewModel()
        {
            try
            {
                var dataContext = view.DataContext;
                var settingsProperty = dataContext?.GetType().GetProperty("Settings");
                if (settingsProperty?.GetValue(dataContext) is System.Collections.IEnumerable settings)
                {
                    index.BuildQuickIndex(settings.OfType<ISetting>());
                }
            }
            catch (Exception ex)
            {
                Log.Default.Write("YMM4SettingSearch: 設定一覧の取得に失敗しました", ex);
            }
        }

        void ApplySearch()
        {
            var query = searchBox?.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                lastMatched = null;
                ApplyItemFilter();
                ApplyPageFilter(clear: true);
                return;
            }
            var results = index.Search(query);
            Log.Default.Write("YMM4SettingSearch: 検索 \"" + query + "\" / 一致 " + results.Count + " 件" + (results.Count > 0 ? " / 例: " + results[0].Setting.Name + " [" + results[0].Match.Source + "] " + results[0].Match.Text : ""));
            var matched = new HashSet<ISetting>(results.Select(r => r.Setting), ReferenceEqualityComparer<ISetting>.Instance);
            lastMatched = matched;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                ApplyItemFilter();
                ApplyPageFilter(clear: false);
            });
        }

        void ApplyItemFilter()
        {
            try
            {
                var source = categoryList.ItemsSource;
                if (source == null)
                {
                    return;
                }
                var collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(source);
                if (collectionView == null)
                {
                    return;
                }
                if (SettingSearchSettings.Default.HighlightResults)
                {
                    if (collectionView.Filter != null)
                    {
                        collectionView.Filter = null;
                        collectionView.Refresh();
                    }
                    ApplyItemHighlights();
                    return;
                }
                ClearItemHighlights();
                if (lastMatched == null)
                {
                    collectionView.Filter = null;
                }
                else
                {
                    var matched = lastMatched;
                    collectionView.Filter = item => item is ISetting setting && matched.Contains(setting);
                }
                collectionView.Refresh();
            }
            catch (Exception ex)
            {
                Log.Default.Write("YMM4SettingSearch: カテゴリ一覧の絞り込みに失敗しました", ex);
            }
        }

        void ApplyItemHighlights()
        {
            foreach (var item in categoryList.Items)
            {
                try
                {
                    if (categoryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem listBoxItem && item is ISetting setting)
                    {
                        SetItemHighlight(listBoxItem, lastMatched != null && lastMatched.Contains(setting));
                    }
                }
                catch
                {
                }
            }
        }

        void SetItemHighlight(FrameworkElement element, bool isMatched)
        {
            if (isMatched)
            {
                if (!itemHighlightState.ContainsKey(element) && element is Control control)
                {
                    itemHighlightState.Add(element, control.GetValue(Control.BackgroundProperty));
                }
                if (element is Control target)
                {
                    target.SetValue(Control.BackgroundProperty, highlightBrush);
                }
            }
            else if (itemHighlightState.TryGetValue(element, out var original) && itemHighlightState.Remove(element) && element is Control control)
            {
                control.SetValue(Control.BackgroundProperty, original);
            }
        }

        void ClearItemHighlights()
        {
            foreach (var element in itemHighlightState.Keys.ToArray())
            {
                SetItemHighlight(element, false);
            }
        }

        void SetElementVisible(FrameworkElement element, bool visible)
        {
            if (!visible)
            {
                if (!elementVisibilityState.ContainsKey(element))
                {
                    elementVisibilityState.Add(element, element.Visibility);
                }
                element.Visibility = Visibility.Collapsed;
            }
            else if (elementVisibilityState.TryGetValue(element, out var original) && elementVisibilityState.Remove(element))
            {
                element.Visibility = original;
            }
        }

        /// <summary>
        /// 表示中の設定ページ内の検索結果を、設定に応じてハイライトまたは非表示にする
        /// </summary>
        bool ApplyPageFilter(bool clear = false)
        {
            try
            {
                var pageRoot = pageContent?.Content as DependencyObject;
                if (pageRoot == null)
                {
                    return false;
                }
                var sectionPanel = FindSectionPanel(pageRoot);
                if (sectionPanel == null)
                {
                    return false;
                }
                var query = clear ? null : searchBox?.Text;
                var allReady = true;
                var highlightMode = SettingSearchSettings.Default.HighlightResults;
                if (highlightMode)
                {
                    RestoreHiddenElements();
                    ClearRowHighlights();
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        foreach (var child in sectionPanel.Children.OfType<FrameworkElement>())
                        {
                            if (child is Expander expander)
                            {
                                var hasBody = HighlightExpander(expander, query);
                                if (!hasBody)
                                {
                                    allReady = false;
                                }
                            }
                        }
                    }
                    return allReady;
                }
                ClearRowHighlights();
                foreach (var child in sectionPanel.Children.OfType<FrameworkElement>())
                {
                    if (child is Expander expander)
                    {
                        var hasBody = FilterExpander(expander, query);
                        if (query != null && !string.IsNullOrWhiteSpace(query) && !hasBody)
                        {
                            allReady = false;
                        }
                    }
                }
                return allReady;
            }
            catch (Exception ex)
            {
                Log.Default.Write("YMM4SettingSearch: 行フィルタに失敗しました", ex);
                return true;
            }
        }

        void RestoreHiddenElements()
        {
            foreach (var element in elementVisibilityState.Keys.ToArray())
            {
                SetElementVisible(element, true);
            }
        }

        void ClearRowHighlights()
        {
            foreach (var element in rowHighlightState.Keys.ToArray())
            {
                SetRowHighlight(element, false);
            }
        }

        bool HighlightExpander(Expander expander, string query)
        {
            if (!hookedExpanders.Contains(expander))
            {
                hookedExpanders.Add(expander);
                expander.Expanded += (s, e) =>
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        ApplyPageFilter();
                    });
                };
            }
            var body = FindBodyPanel(expander);
            var hasRowMatch = false;
            if (body != null)
            {
                foreach (var row in body.Children.OfType<FrameworkElement>())
                {
                    if (row is Separator || row is GridSplitter)
                    {
                        continue;
                    }
                    var matched = IsRowMatch(row, query);
                    SetRowHighlight(row, matched);
                    hasRowMatch |= matched;
                }
            }
            var headerMatched = hasRowMatch || HeaderMatches(expander, body, query);
            foreach (var textBlock in EnumerateHeaderTextBlocks(expander, body))
            {
                SetRowHighlight(textBlock, headerMatched);
            }
            return body != null;
        }

        void SetRowHighlight(FrameworkElement element, bool isMatched)
        {
            if (!HasBackgroundProperty(element, out var backgroundProperty))
            {
                return;
            }
            if (isMatched)
            {
                if (!rowHighlightState.ContainsKey(element))
                {
                    rowHighlightState.Add(element, element.GetValue(backgroundProperty));
                }
                element.SetValue(backgroundProperty, highlightBrush);
            }
            else if (rowHighlightState.TryGetValue(element, out var original) && rowHighlightState.Remove(element))
            {
                element.SetValue(backgroundProperty, original);
            }
        }

        static bool HasBackgroundProperty(DependencyObject element, out DependencyProperty property)
        {
            if (element is Control)
            {
                property = System.Windows.Controls.Control.BackgroundProperty;
                return true;
            }
            if (element is Panel)
            {
                property = Panel.BackgroundProperty;
                return true;
            }
            if (element is TextBlock)
            {
                property = TextBlock.BackgroundProperty;
                return true;
            }
            property = null!;
            return false;
        }

        bool FilterExpander(Expander expander, string? query)
        {
            var body = FindBodyPanel(expander);
            var hasQuery = !string.IsNullOrWhiteSpace(query);
            if (!hasQuery)
            {
                SetElementVisible(expander, true);
                if (body != null)
                {
                    foreach (var row in body.Children.OfType<FrameworkElement>())
                    {
                        SetElementVisible(row, true);
                    }
                }
                return body != null;
            }
            if (!hookedExpanders.Contains(expander))
            {
                hookedExpanders.Add(expander);
                expander.Expanded += (s, e) =>
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        ApplyPageFilter();
                    });
                };
            }
            var headerMatched = HeaderMatches(expander, body, query!);
            var hasRowMatch = false;
            if (body != null)
            {
                foreach (var row in body.Children.OfType<FrameworkElement>())
                {
                    if (row is Separator || row is GridSplitter)
                    {
                        SetElementVisible(row, false);
                        continue;
                    }
                    var matched = IsRowMatch(row, query);
                    SetElementVisible(row, matched);
                    hasRowMatch |= matched;
                }
            }
            SetElementVisible(expander, hasRowMatch || headerMatched);
            return body != null;
        }

        static bool HeaderMatches(DependencyObject expander, Panel? body, string query)
        {
            return EnumerateHeaderTextBlocks(expander, body).Any(textBlock => SettingSearchIndex.RowMatches(textBlock.Text, query));
        }

        static IEnumerable<TextBlock> EnumerateHeaderTextBlocks(DependencyObject node, Panel? body)
        {
            if (node == null || node == body)
            {
                yield break;
            }
            if (node is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            {
                yield return textBlock;
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                foreach (var nested in EnumerateHeaderTextBlocks(VisualTreeHelper.GetChild(node, i), body))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// ページ内のセクション(Expander)を並べているパネルを探す
        /// </summary>
        Panel? FindSectionPanel(DependencyObject node, int depth = 0)
        {
            if (depth > 10 || node == null)
            {
                return null;
            }
            if (node is Panel panel && panel.Children.OfType<Expander>().Any())
            {
                return panel;
            }
            if (node is ContentControl cc && cc.Content is DependencyObject content)
            {
                var result = FindSectionPanel(content, depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
            if (node is Decorator decorator)
            {
                var result = FindSectionPanel(decorator.Child, depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var result = FindSectionPanel(VisualTreeHelper.GetChild(node, i), depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Expander 内の設定行を並べているStackPanelを探す
        /// </summary>
        static Panel? FindBodyPanel(DependencyObject node, int depth = 0)
        {
            if (depth > 8 || node == null)
            {
                return null;
            }
            if (node is StackPanel stackPanel && stackPanel.Children.Count > 0 && stackPanel.Children.OfType<FrameworkElement>().All(c => c is Grid || c is Separator))
            {
                return stackPanel;
            }
            if (node is ContentControl cc && cc.Content is DependencyObject content)
            {
                var result = FindBodyPanel(content, depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var result = FindBodyPanel(VisualTreeHelper.GetChild(node, i), depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        bool IsRowMatch(FrameworkElement element, string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }
            if (element is GridSplitter || element is System.Windows.Controls.Separator)
            {
                return false;
            }
            return SettingSearchIndex.RowMatches(SettingSearchIndex.HarvestText(element), query);
        }
    }
}

public sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

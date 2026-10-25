using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMM4SettingSearch;

public sealed record SearchMatch(string Source, string Text);

public sealed record SearchResult(ISetting Setting, SearchMatch Match);

public static class KanaNormalizer
{
    /// <summary>ひらがな⇔カタカナ・全角⇔半角を正規化する (半角カナはFormKCで全角カナ化)</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var chars = text.Normalize(NormalizationForm.FormKC).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c >= 'ァ' && c <= 'ヶ')
            {
                chars[i] = (char)(c - 0x60);
            }
        }
        return new string(chars);
    }
}

public sealed class SettingSearchIndex
{
    readonly Dictionary<ISetting, List<SearchMatch>> corpus = new();
    readonly List<ISetting> ordered = new();
    int deepIndexCursor;
    bool deepIndexRunning;
    bool deepIndexDone;

    public IReadOnlyList<ISetting> Settings => ordered;

    public bool DeepIndexDone => deepIndexDone;

    static string GetCategoryDisplayName(SettingsCategory category)
    {
        var field = category.GetType().GetField(category.ToString());
        var display = (DisplayAttribute?)field?.GetCustomAttribute(typeof(DisplayAttribute), inherit: false);
        try
        {
            var name = display?.GetName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
        }
        return category.ToString();
    }

    static string HarvestViewText(object? settingView)
    {
        if (settingView is not DependencyObject root)
        {
            return string.Empty;
        }
        return HarvestText(root);
    }

    /// <summary>要素ツリーから表示テキストを収集する</summary>
    public static string HarvestText(DependencyObject root)
    {
        var sb = new StringBuilder();
        try
        {
            Walk(root, sb);
        }
        catch
        {
        }
        return sb.ToString();
    }

    /// <summary>設定ページ内の単一の行とクエリが一致するかを判定する (ひらがな/カタカナ・全角/半角を同一視)</summary>
    public static bool RowMatches(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }
        var normalized = KanaNormalizer.Normalize(text);
        var trimmed = query.Trim();
        if (trimmed.StartsWith("re:", StringComparison.Ordinal) && trimmed.Length > 3)
        {
            try
            {
                return Regex.IsMatch(normalized, KanaNormalizer.Normalize(trimmed[3..]), RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        var terms = trimmed.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => KanaNormalizer.Normalize(t));
        foreach (var term in terms)
        {
            if (!normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    static void Walk(DependencyObject node, StringBuilder sb)
    {
        if (node == null)
        {
            return;
        }
        try
        {
            CollectNodeText(node, sb);
        }
        catch
        {
        }
        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            count = 0;
        }
        if (count > 0)
        {
            for (var i = 0; i < count; i++)
            {
                try
                {
                    Walk(VisualTreeHelper.GetChild(node, i), sb);
                }
                catch
                {
                }
            }
            return;
        }
        try
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d)
                {
                    Walk(d, sb);
                }
            }
        }
        catch
        {
        }
    }

    static void CollectNodeText(DependencyObject node, StringBuilder sb)
    {
        if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            sb.AppendLine(tb.Text);
        }
        else if (node is System.Windows.Controls.TextBox tbox && !string.IsNullOrWhiteSpace(tbox.Text))
        {
            sb.AppendLine(tbox.Text);
        }
        else if (node is System.Windows.Controls.ComboBox combo)
        {
            foreach (var item in combo.Items)
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    sb.AppendLine(s);
                }
            }
        }

        if (node is System.Windows.Controls.HeaderedContentControl hcc && hcc.Header is string hs && !string.IsNullOrWhiteSpace(hs))
        {
            sb.AppendLine(hs);
        }
        if (node is System.Windows.Controls.ContentControl cc && cc.Content is string cs && !string.IsNullOrWhiteSpace(cs))
        {
            sb.AppendLine(cs);
        }
        var toolTip = node.ReadLocalValue(FrameworkElement.ToolTipProperty);
        if (toolTip is string ts && !string.IsNullOrWhiteSpace(ts))
        {
            sb.AppendLine(ts);
        }
    }

    static IEnumerable<SearchMatch> CollectValueMatches(ISetting setting)
    {
        var results = new List<SearchMatch>();
        try
        {
            var props = setting.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() != null)
                {
                    continue;
                }
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                {
                    continue;
                }
                object? value;
                try
                {
                    value = prop.GetValue(setting);
                }
                catch
                {
                    continue;
                }
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    results.Add(new SearchMatch("プロパティ値 " + prop.Name, s));
                    continue;
                }
                if (value is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        var text = item as string ?? item?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            results.Add(new SearchMatch("プロパティ値 " + prop.Name, text));
                        }
                    }
                }
                else if (value != null)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(new SearchMatch("プロパティ値 " + prop.Name, text));
                    }
                }
            }
        }
        catch
        {
        }
        return results;
    }

    void AddSetting(ISetting setting)
    {
        if (corpus.ContainsKey(setting))
        {
            return;
        }
        var matches = new List<SearchMatch>
        {
            new("設定名", setting.Name),
            new("カテゴリ", GetCategoryDisplayName(setting.Category)),
        };
        matches.AddRange(CollectValueMatches(setting));
        corpus.Add(setting, matches);
        ordered.Add(setting);
    }

    public void BuildQuickIndex(IEnumerable<ISetting> settings)
    {
        foreach (var setting in settings)
        {
            AddSetting(setting);
        }
    }

    public void StartDeepIndex(object? hostDataContext = null)
    {
        if (deepIndexRunning || deepIndexDone)
        {
            return;
        }
        this.hostDataContext = hostDataContext;
        deepIndexRunning = true;
        RunDeepIndexStep();
    }

    object? hostDataContext;

    void RunDeepIndexStep()
    {
        if (deepIndexCursor >= ordered.Count)
        {
            deepIndexRunning = false;
            deepIndexDone = true;
            var totalLength = corpus.Values.Sum(matches => matches.Sum(match => match.Text.Length));
            Log.Default.Write("YMM4SettingSearch: 詳細インデックス完了 合計文字数 " + totalLength);
            foreach (var pair in corpus)
            {
                var text = string.Join("\n", pair.Value.Select(m => m.Text));
                if (text.Length < 200)
                {
                    Log.Default.Write("YMM4SettingSearch: インデックス小 " + pair.Key.Name + " (" + pair.Key.Category + ") 文字数 " + text.Length);
                }
                if (text.Contains("プレビュー", StringComparison.Ordinal))
                {
                    Log.Default.Write("YMM4SettingSearch: プレビューを含む設定 " + pair.Key.Name + " (" + pair.Key.Category + ")");
                }
            }
            return;
        }
        var setting = ordered[deepIndexCursor];
        try
        {
            var view = setting.SettingView;
            if (view != null)
            {
                var text = RealizeAndHarvest(view, hostDataContext);
                Log.Default.Write("YMM4SettingSearch: 取得 " + setting.Name + " (" + setting.Category + ") " + text.Length + " 文字");
                if (setting.Name == "全般")
                {
                    Log.Default.Write("YMM4SettingSearch: 全般の内容: " + (text.Length > 800 ? text[..800] : text));
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    corpus[setting].Add(new SearchMatch("UI", text.Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Default.Write("YMM4SettingSearch: インデックス失敗 " + setting.Name, ex);
        }
        deepIndexCursor++;
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, RunDeepIndexStep);
    }

    /// <summary>
    /// 設定ビューを画面外のウィンドウに載せて Loaded/レイアウトを発生させ、実際に表示されるテキストを収集する
    /// </summary>
    string RealizeAndHarvest(object view, object? dataContext)
    {
        if (view is not FrameworkElement element)
        {
            return HarvestViewText(view);
        }
        Window? window = null;
        try
        {
            window = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Width = 1200,
                Height = 900,
                Left = -32000,
                Top = -32000,
                DataContext = dataContext,
                Content = element,
            };
            window.Show();
            window.UpdateLayout();
            var text = HarvestViewText(element);
            window.Content = null;
            window.Close();
            window = null;
            return text;
        }
        catch (Exception ex)
        {
            Log.Default.Write("YMM4SettingSearch: ビューの読み込みに失敗しました", ex);
            try
            {
                if (window != null)
                {
                    window.Content = null;
                    window.Close();
                }
            }
            catch
            {
            }
            return HarvestViewText(element);
        }
    }

    public List<SearchResult> Search(string query)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return results;
        }
        var trimmed = query.Trim();
        Matcher matcher;
        if (trimmed.StartsWith("re:", StringComparison.Ordinal) && trimmed.Length > 3)
        {
            try
            {
                matcher = new Matcher(new Regex(KanaNormalizer.Normalize(trimmed[3..]), RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
            catch (ArgumentException)
            {
                return results;
            }
        }
        else
        {
            var terms = trimmed.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => KanaNormalizer.Normalize(t).ToLowerInvariant())
                .Where(t => t.Length > 0)
                .ToArray();
            if (terms.Length == 0)
            {
                return results;
            }
            matcher = new Matcher(terms);
        }

        foreach (var setting in ordered)
        {
            if (!corpus.TryGetValue(setting, out var matches) || results.Any(r => ReferenceEquals(r.Setting, setting)))
            {
                continue;
            }
            foreach (var match in matches)
            {
                if (matcher.IsMatch(match.Text))
                {
                    results.Add(new SearchResult(setting, match with { Text = GetPreview(match.Text, matcher) }));
                    break;
                }
            }
        }
        return results;
    }

    static string GetPreview(string text, Matcher matcher)
    {
        text = text.Trim();
        var index = matcher.FirstIndexOf(text);
        if (index < 0)
        {
            return text.Length <= 80 ? text : text[..80] + "…";
        }
        var start = Math.Max(0, index - 25);
        var end = Math.Min(text.Length, index + 55);
        var preview = text[start..end];
        return (start > 0 ? "……" : string.Empty) + preview + (end < text.Length ? "……" : string.Empty);
    }

    sealed class Matcher
    {
        readonly Regex? regex;
        readonly string[]? terms;

        public Matcher(Regex regex) => this.regex = regex;

        public Matcher(string[] terms) => this.terms = terms;

        public bool IsMatch(string text)
        {
            if (regex != null)
            {
                return regex.IsMatch(KanaNormalizer.Normalize(text));
            }
            if (terms == null)
            {
                return false;
            }
            var normalized = KanaNormalizer.Normalize(text);
            foreach (var term in terms)
            {
                if (!normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        public int FirstIndexOf(string text)
        {
            var normalized = KanaNormalizer.Normalize(text);
            if (regex != null)
            {
                var match = regex.Match(normalized);
                return match.Success ? match.Index : -1;
            }
            if (terms == null)
            {
                return -1;
            }
            foreach (var term in terms)
            {
                var index = normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return index;
                }
            }
            return -1;
        }
    }
}

using System.IO;
using System.Text.Json;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMM4SettingSearch;

public sealed class SettingSearchSettings : SettingsBase<SettingSearchSettings>
{
    const string FileName = "YMM4SettingSearch.json";
    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    bool highlightResults;

    public static event Action? Changed;

    public bool HighlightResults
    {
        get => highlightResults;
        set
        {
            if (Set(ref highlightResults, value))
            {
                Save();
                Changed?.Invoke();
            }
        }
    }

    public override SettingsCategory Category => SettingsCategory.Other;

    public override string Name => "設定検索";

    public override bool HasSettingView => true;

    public override object? SettingView => new SettingSearchSettingsView(this);

    public override void Initialize()
    {
        try
        {
            var path = SettingsFilePath;
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path), jsonOptions);
                highlightResults = data?.HighlightResults ?? false;
            }
        }
        catch (Exception ex)
        {
            Log.Default.Write("YMM4SettingSearch: 設定の読み込みに失敗しました", ex);
        }
    }

    public override void Save()
    {
        try
        {
            var path = SettingsFilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(new SettingsData { HighlightResults = highlightResults }, jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Default.Write("YMM4SettingSearch: 設定の保存に失敗しました", ex);
        }
    }

    static string SettingsFilePath
    {
        get
        {
            var directory = Path.GetDirectoryName(typeof(SettingSearchSettings).Assembly.Location);
            if (string.IsNullOrEmpty(directory))
            {
                directory = AppContext.BaseDirectory;
            }
            return Path.Combine(directory, FileName);
        }
    }

    sealed class SettingsData
    {
        public bool HighlightResults { get; set; }
    }
}

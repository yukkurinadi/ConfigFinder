using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HarmonyLib;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace YMM4SettingSearch;

[PluginDetails(AuthorName = "yuzudsgn")]
public class SettingSearchPlugin : IPlugin
{
    static Harmony? harmony;

    static readonly object lockObject = new();

    public string Name => "設定検索";

    public SettingSearchPlugin()
    {
        lock (lockObject)
        {
            if (harmony != null)
            {
                return;
            }
            try
            {
                harmony = new Harmony("YMM4.SettingSearch");
                var settingsViewType = AccessTools.TypeByName("YukkuriMovieMaker.Views.SettingsView");
                var ctor = AccessTools.Constructor(settingsViewType, Type.EmptyTypes);
                if (ctor == null)
                {
                    return;
                }
                harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(SettingSearchPlugin), nameof(SettingsViewCtorPostfix)));
                Log.Default.Write("YMM4SettingSearch: Harmonyパッチを適用しました");
            }
            catch (Exception e)
            {
                harmony = null;
                Log.Default.Write("YMM4SettingSearch: パッチに失敗しました", e);
            }
        }
    }

    static void SettingsViewCtorPostfix(object __instance)
    {
        if (__instance is FrameworkElement view)
        {
            SettingsSearchController.Attach(view);
        }
    }
}

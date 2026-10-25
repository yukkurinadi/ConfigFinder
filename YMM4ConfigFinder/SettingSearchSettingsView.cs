using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace YMM4SettingSearch;

public sealed class SettingSearchSettingsView : UserControl
{
    public SettingSearchSettingsView(SettingSearchSettings settings)
    {
        DataContext = settings;
        var panel = new StackPanel
        {
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var checkBox = new CheckBox
        {
            Content = "検索結果をハイライト",
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "オフの場合は一致しない項目を非表示にします",
        };
        checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SettingSearchSettings.HighlightResults))
        {
            Source = settings,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        panel.Children.Add(checkBox);
        Content = panel;
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using VideoShrink.Core;

namespace VideoShrink.Views;

/// <summary>自定义模式的逐项隐藏编辑界面。「保存并退出」时把结果写回配置。</summary>
public partial class VisibilityEditorDialog : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        public string Key         { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Group       { get; init; } = "";
        public string AdvancedTag { get; init; } = "";

        private bool _hidden;
        public bool Hidden
        {
            get => _hidden;
            set { _hidden = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hidden))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Row> _rows = new();

    /// <summary>确认后的隐藏项 key 列表；取消时为 null。</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    public VisibilityEditorDialog(IReadOnlyList<string> currentlyHidden)
    {
        InitializeComponent();

        foreach (var item in SettingsCatalog.All)
            _rows.Add(new Row
            {
                Key         = item.Key,
                DisplayName = item.DisplayName,
                Group       = item.Group,
                AdvancedTag = item.IsAdvanced ? "  （高级）" : "",
                Hidden      = currentlyHidden.Contains(item.Key),
            });

        var view = new CollectionViewSource { Source = _rows };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)));
        ItemsList.ItemsSource = view.View;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Hidden = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _rows.Where(r => r.Hidden).Select(r => r.Key).ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }
}

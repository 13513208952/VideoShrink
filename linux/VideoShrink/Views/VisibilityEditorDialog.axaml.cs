using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoShrink.Core;

namespace VideoShrink.Views;

public partial class VisibilityEditorDialog : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        public string Key         { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Group       { get; init; } = "";

        private bool _hidden;
        public bool Hidden
        {
            get => _hidden;
            set { _hidden = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hidden))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Row> _rows = new();

    public IReadOnlyList<string>? Result { get; private set; }

    public VisibilityEditorDialog()
    {
        InitializeComponent();
    }

    public VisibilityEditorDialog(IReadOnlyList<string> currentlyHidden)
    {
        InitializeComponent();

        foreach (var item in SettingsCatalog.All)
            _rows.Add(new Row
            {
                Key         = item.Key,
                DisplayName = item.DisplayName,
                Group       = item.Group,
                Hidden      = currentlyHidden.Contains(item.Key),
            });

        ItemsList.ItemsSource = _rows;
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Hidden = false;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Result = _rows.Where(r => r.Hidden).Select(r => r.Key).ToList();
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}

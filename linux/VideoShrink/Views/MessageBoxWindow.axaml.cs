using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VideoShrink.Views;

public partial class MessageBoxWindow : Window
{
    public bool Result { get; private set; }
    public bool ShowCancel { get; }

    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    public MessageBoxWindow(string message, string title, bool isConfirm = false)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ShowCancel = isConfirm;
        if (!isConfirm)
            CancelButton.IsVisible = false;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}

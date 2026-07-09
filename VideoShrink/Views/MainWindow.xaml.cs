using System.Windows;
using VideoShrink.ViewModels;

namespace VideoShrink.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

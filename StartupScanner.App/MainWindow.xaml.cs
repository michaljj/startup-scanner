using System.Windows;
using StartupScanner.App.ViewModels;

namespace StartupScanner.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RescanAsync();
    }
}

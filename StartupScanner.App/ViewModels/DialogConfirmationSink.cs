using System.Windows;
using StartupScanner.App.Views;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.App.ViewModels;

public sealed class DialogConfirmationSink : IConfirmationSink
{
    public bool Confirm(StartupItem item, RiskTier tier, string message)
        => Application.Current.Dispatcher.Invoke(() => new RiskConfirmationDialog(tier, message)
        {
            Owner = Application.Current.MainWindow,
        }.ShowDialog() == true);
}

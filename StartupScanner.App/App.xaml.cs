using System.Windows;
using StartupScanner.App.ViewModels;
using StartupScanner.Core;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var host = ScannerHost.CreateWindows(new DialogConfirmationSink(), new RestorePointPrompt());

        if (host.AclOutcome == AclOutcome.Failed)
            MessageBox.Show(
                "The StartupScanner data folder could not be secured. Values in the write journal " +
                "may be modifiable by non-administrators.",
                "StartupScanner", MessageBoxButton.OK, MessageBoxImage.Warning);

        new MainWindow(new MainViewModel(host)).Show();
    }

    private sealed class RestorePointPrompt : Core.Abstractions.IRestorePointPrompt
    {
        public bool ContinueWithoutRestorePoint(string reason)
            => MessageBox.Show(
                $"{reason}\n\nContinue without restore point protection?",
                "StartupScanner", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}

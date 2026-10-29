using System.Windows;
using StartupScanner.Core.Models;

namespace StartupScanner.App.Views;

public partial class RiskConfirmationDialog : Window
{
    public RiskConfirmationDialog(RiskTier tier, string message)
    {
        InitializeComponent();
        TierText.Text = $"{tier} risk";
        MessageText.Text = message;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

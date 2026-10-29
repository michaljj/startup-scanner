using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

/// <summary>
/// Confirmation dialog copy. Medium risk gets a plain confirmation; High risk names the concrete
/// consequence so the user is not asked to approve an abstraction.
/// </summary>
public static class ConfirmationText
{
    public static string For(StartupItem item, bool enabling)
    {
        var verb = enabling ? "Enable" : "Disable";

        return item.Risk == RiskTier.High
            ? $"{verb} '{item.Name}'?\n\n{Consequence(item.Category)}"
            : $"{verb} '{item.Name}' ({item.Category})?";
    }

    private static string Consequence(StartupCategory category) => category switch
    {
        StartupCategory.Winlogon =>
            "Winlogon values run as part of the logon sequence. A bad change here can stop you logging in.",
        StartupCategory.BootExecute =>
            "BootExecute entries run before Windows starts. A bad change here can stop the machine booting.",
        StartupCategory.Ifeo =>
            "An Image File Execution Options Debugger value redirects the launch of another program. "
            + "Changing it alters what runs when that program is started.",
        StartupCategory.AppInit =>
            "AppInit_DLLs are loaded into most processes that use USER32, so this affects nearly every application.",
        StartupCategory.WinsockLsp =>
            "Winsock LSP entries sit inside the network stack. Removing one can break network connectivity.",
        _ => "This entry loads early in the boot or logon sequence.",
    };
}

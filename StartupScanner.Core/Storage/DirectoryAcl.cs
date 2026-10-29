using System.Security.AccessControl;
using System.Security.Principal;

namespace StartupScanner.Core.Storage;

public enum AclOutcome { Created, AlreadyCorrect, Repaired, Failed }

/// <summary>
/// %ProgramData% is writable by standard users by default. writes.jsonl holds values the
/// elevated app writes back into HKLM on re-enable, so a non-administrator must not be able
/// to seed it. The folder therefore carries an explicit, inheritance-protected DACL that is
/// verified and repaired on every startup.
/// </summary>
public static class DirectoryAcl
{
    public static AclOutcome EnsureSecured(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                var created = Directory.CreateDirectory(path);
                created.SetAccessControl(RequiredSecurity());
                return AclOutcome.Created;
            }

            var info = new DirectoryInfo(path);
            if (Matches(info.GetAccessControl())) return AclOutcome.AlreadyCorrect;

            info.SetAccessControl(RequiredSecurity());
            return AclOutcome.Repaired;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return AclOutcome.Failed;
        }
    }

    public static AclOutcome Inspect(string path)
    {
        if (!Directory.Exists(path)) return AclOutcome.Failed;
        try
        {
            return Matches(new DirectoryInfo(path).GetAccessControl())
                ? AclOutcome.AlreadyCorrect
                : AclOutcome.Repaired;   // "needs repair"
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return AclOutcome.Failed;
        }
    }

    private static DirectorySecurity RequiredSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Add(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Add(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Add(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        return security;
    }

    private static void Add(DirectorySecurity security, WellKnownSidType sid, FileSystemRights rights)
        => security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid, null), rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

    private static bool Matches(DirectorySecurity actual)
    {
        if (!actual.AreAccessRulesProtected) return false;

        var rules = actual.GetAccessRules(true, false, typeof(SecurityIdentifier))
                          .Cast<FileSystemAccessRule>().ToList();
        if (rules.Count != 3) return false;

        return Has(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl)
            && Has(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl)
            && Has(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute)
            && !rules.Any(r => r.IdentityReference.Value ==
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value &&
                    (r.FileSystemRights & FileSystemRights.Write) != 0);
    }

    private static bool Has(List<FileSystemAccessRule> rules, WellKnownSidType sid, FileSystemRights rights)
    {
        var expected = new SecurityIdentifier(sid, null).Value;
        return rules.Any(r => r.IdentityReference.Value == expected
                           && r.AccessControlType == AccessControlType.Allow
                           && r.FileSystemRights.HasFlag(rights));
    }
}

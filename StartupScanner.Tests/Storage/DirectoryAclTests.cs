using System.Security.AccessControl;
using System.Security.Principal;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Storage;

public class DirectoryAclTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-acl-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void CreatesTheDirectoryWithProtectedAdminOnlyWriteAcl()
    {
        var outcome = DirectoryAcl.EnsureSecured(_dir);

        Assert.Equal(AclOutcome.Created, outcome);
        Assert.True(Directory.Exists(_dir));

        var acl = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);

        var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                       .Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        AssertRule(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AssertRule(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AssertRule(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
    }

    [Fact]
    public void ReportsAlreadyCorrectOnASecondCall()
    {
        DirectoryAcl.EnsureSecured(_dir);
        Assert.Equal(AclOutcome.AlreadyCorrect, DirectoryAcl.EnsureSecured(_dir));
    }

    [Fact]
    public void RepairsALoosenedAcl()
    {
        DirectoryAcl.EnsureSecured(_dir);

        var info = new DirectoryInfo(_dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);

        Assert.Equal(AclOutcome.Repaired, DirectoryAcl.EnsureSecured(_dir));
        Assert.Equal(AclOutcome.AlreadyCorrect, DirectoryAcl.EnsureSecured(_dir));
    }

    private static void AssertRule(List<FileSystemAccessRule> rules, WellKnownSidType sid, FileSystemRights rights)
    {
        var expected = new SecurityIdentifier(sid, null);
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == expected.Value &&
            r.AccessControlType == AccessControlType.Allow &&
            r.FileSystemRights.HasFlag(rights));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

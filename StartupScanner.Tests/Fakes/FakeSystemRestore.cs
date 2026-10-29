using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeSystemRestore : ISystemRestore
{
    public List<string> Descriptions { get; } = new();
    public string? FailWith { get; set; }

    public bool TryCreateRestorePoint(string description, out string? error)
    {
        Descriptions.Add(description);
        error = FailWith;
        return FailWith is null;
    }
}

public sealed class StubRestorePointPrompt(bool answer) : IRestorePointPrompt
{
    public List<string> Reasons { get; } = new();

    public bool ContinueWithoutRestorePoint(string reason)
    {
        Reasons.Add(reason);
        return answer;
    }
}

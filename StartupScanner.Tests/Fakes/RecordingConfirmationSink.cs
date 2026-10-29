using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Fakes;

public sealed class RecordingConfirmationSink(bool answer = true) : IConfirmationSink
{
    public List<(string Name, RiskTier Tier, string Message)> Calls { get; } = new();

    public bool Confirm(StartupItem item, RiskTier tier, string message)
    {
        Calls.Add((item.Name, tier, message));
        return answer;
    }
}

public sealed class StubRestorePointGate(bool allow = true, string? error = null) : IRestorePointGate
{
    public int Calls { get; private set; }

    public bool Ensure(out string? failure)
    {
        Calls++;
        failure = allow ? null : error ?? "declined";
        return allow;
    }
}

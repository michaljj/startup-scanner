using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeClock(DateTime? start = null) : IClock
{
    public DateTime UtcNow { get; set; } = start ?? new DateTime(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow += by;
}

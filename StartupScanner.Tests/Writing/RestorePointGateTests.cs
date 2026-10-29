using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RestorePointGateTests
{
    [Fact]
    public void CreatesOneRestorePointAndDoesNotAskTheUser()
    {
        var restore = new FakeSystemRestore();
        var prompt = new StubRestorePointPrompt(false);
        var gate = new RestorePointGate(restore, prompt);

        Assert.True(gate.Ensure(out var error));

        Assert.Null(error);
        Assert.Single(restore.Descriptions);
        Assert.Empty(prompt.Reasons);
    }

    [Fact]
    public void CreatesTheRestorePointOnlyOncePerSession()
    {
        var restore = new FakeSystemRestore();
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));

        gate.Ensure(out _);
        gate.Ensure(out _);
        gate.Ensure(out _);

        Assert.Single(restore.Descriptions);
    }

    [Fact]
    public void OnFailureTheUserIsAskedAndAnAcceptanceProceeds()
    {
        var restore = new FakeSystemRestore { FailWith = "System Protection is turned off." };
        var prompt = new StubRestorePointPrompt(true);
        var gate = new RestorePointGate(restore, prompt);

        Assert.True(gate.Ensure(out var error));

        Assert.Null(error);
        Assert.Equal("System Protection is turned off.", prompt.Reasons.Single());
    }

    [Fact]
    public void AfterTheUserAcceptsTheGateDoesNotAskOrRetryAgain()
    {
        var restore = new FakeSystemRestore { FailWith = "off" };
        var prompt = new StubRestorePointPrompt(true);
        var gate = new RestorePointGate(restore, prompt);

        gate.Ensure(out _);
        gate.Ensure(out _);

        Assert.Single(restore.Descriptions);
        Assert.Single(prompt.Reasons);
    }

    [Fact]
    public void ADeclineBlocksTheWriteAndReportsTheReason()
    {
        var restore = new FakeSystemRestore { FailWith = "System Protection is turned off." };
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));

        Assert.False(gate.Ensure(out var error));

        Assert.Equal("System Protection is turned off.", error);
    }

    [Fact]
    public void ADeclineIsNotMemoisedSoTurningProtectionOnLetsTheNextAttemptSucceed()
    {
        var restore = new FakeSystemRestore { FailWith = "off" };
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));
        gate.Ensure(out _);

        restore.FailWith = null;

        Assert.True(gate.Ensure(out _));
        Assert.Equal(2, restore.Descriptions.Count);
    }

    [Fact]
    public void TheDescriptionIsUsedVerbatim()
    {
        var restore = new FakeSystemRestore();
        new RestorePointGate(restore, new StubRestorePointPrompt(true), "custom text").Ensure(out _);

        Assert.Equal("custom text", restore.Descriptions.Single());
    }
}

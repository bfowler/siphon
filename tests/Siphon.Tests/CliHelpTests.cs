namespace Siphon.Tests;

public sealed class CliHelpTests
{
    [Fact]
    public async Task HelpExitsSuccessfullyWithoutConnectingOrPrompting()
    {
        var result = await TestProcess.Cli("--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
    }
}

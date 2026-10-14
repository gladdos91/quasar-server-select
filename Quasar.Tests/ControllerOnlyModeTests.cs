using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ControllerOnlyModeTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ControllerOnlyModeReadsConfiguration(string setting, bool expected)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quasar:ControllerOnly"] = setting,
            })
            .Build();

        Assert.Equal(expected, WebServiceOptions.Create(config).ControllerOnly);
    }

    [Fact]
    public async Task ControllerOnlyRejectsDirectLocalStartsAndAdminRestarts()
    {
        var options = new WebServiceOptions { ControllerOnly = true };
        using var supervisor = new DedicatedServerSupervisor(
            null!, null!, null!, null!, null!, options, null!,
            NullLogger<DedicatedServerSupervisor>.Instance);

        var start = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.StartServerAsync("ny-01-test"));
        Assert.Contains("controller-only", start.Message, StringComparison.OrdinalIgnoreCase);

        var restart = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.BeginAdminRestartAsync("ny-01-test"));
        Assert.Contains("controller-only", restart.Message, StringComparison.OrdinalIgnoreCase);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using zRover.Retriever.Packages;

namespace zRover.Retriever.Tests;

public sealed class PackageInstallManagerTests
{
    [Fact]
    public async Task EnableAsync_WhenDisabled_SetsIsEnabledTrue()
    {
        var sut = Build();
        await sut.EnableAsync();
        sut.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnableAsync_FiresStateChanged()
    {
        var sut = Build();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        await sut.EnableAsync();
        fired.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_WhenAlreadyEnabled_IsNoOp()
    {
        var sut = Build();
        await sut.EnableAsync();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        await sut.EnableAsync();
        sut.IsEnabled.Should().BeTrue();
        fired.Should().Be(0);
    }

    [Fact]
    public async Task Disable_WhenEnabled_SetsIsEnabledFalse()
    {
        var sut = Build();
        await sut.EnableAsync();
        sut.Disable();
        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Disable_WhenEnabled_FiresStateChanged()
    {
        var sut = Build();
        await sut.EnableAsync();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        sut.Disable();
        fired.Should().Be(1);
    }

    [Fact]
    public void Disable_WhenAlreadyDisabled_IsNoOp()
    {
        var sut = Build();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        sut.Disable();
        sut.IsEnabled.Should().BeFalse();
        fired.Should().Be(0);
    }

    [Fact]
    public void IsEnabled_DefaultsToFalse()
    {
        var sut = Build();
        sut.IsEnabled.Should().BeFalse();
    }

    private static PackageInstallManager Build() =>
        new(NullLogger<PackageInstallManager>.Instance);
}
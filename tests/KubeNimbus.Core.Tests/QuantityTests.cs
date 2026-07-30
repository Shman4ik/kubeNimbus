using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the Kubernetes quantity reader that
/// backs every CPU/memory number in the UI.
/// </summary>
public class QuantityTests
{
    [Test]
    [Arguments("0", 0d)]
    [Arguments("1", 1d)]
    [Arguments("1.5", 1.5d)]
    [Arguments("100m", 0.1d)]
    [Arguments("12345n", 0.000012345d)]
    [Arguments("500u", 0.0005d)]
    [Arguments("2k", 2000d)]
    [Arguments("129M", 129_000_000d)]
    [Arguments("129e6", 129_000_000d)]
    [Arguments("1Ki", 1024d)]
    [Arguments("1Mi", 1048576d)]
    [Arguments("2Gi", 2147483648d)]
    [Arguments("-5m", -0.005d)]
    public async Task Parses_suffixed_quantities(string input, double expected)
    {
        var parsed = Quantity.Parse(input);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(IsClose(parsed!.Value, expected)).IsTrue();
    }

    /// <summary>Relative comparison — these are floating-point conversions, not exact decimals.</summary>
    private static bool IsClose(double actual, double expected) =>
        Math.Abs(actual - expected) <= (expected == 0 ? 1e-12 : Math.Abs(expected) * 1e-9);

    [Test]
    [Arguments("2E")] // exa suffix, not a truncated exponent
    public async Task Treats_trailing_E_as_the_exa_suffix(string input)
    {
        var parsed = Quantity.Parse(input);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(IsClose(parsed!.Value, 2e18)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("abc")]
    [Arguments("12Qi")] // unknown suffix
    [Arguments("m")]
    public async Task Returns_null_for_unusable_input(string? input) =>
        await Assert.That(Quantity.Parse(input)).IsNull();

    [Test]
    public async Task Converts_cpu_to_nanocores_and_memory_to_bytes()
    {
        await Assert.That(Quantity.ParseCpuNanocores("250m")).IsEqualTo(250_000_000L);
        await Assert.That(Quantity.ParseCpuNanocores("1")).IsEqualTo(1_000_000_000L);
        await Assert.That(Quantity.ParseBytes("128Mi")).IsEqualTo(134_217_728L);
        await Assert.That(Quantity.ParseCpuNanocores(null)).IsNull();
    }

    [Test]
    public async Task Formats_cpu_as_millicores_below_one_core()
    {
        await Assert.That(Quantity.FormatCpu(12_000_000)).IsEqualTo("12m");
        await Assert.That(Quantity.FormatCpu(999_000_000)).IsEqualTo("999m");
        await Assert.That(Quantity.FormatCpu(1_250_000_000)).IsEqualTo("1.25");
        await Assert.That(Quantity.FormatCpu(null)).IsEqualTo("—");
    }

    [Test]
    public async Task Formats_memory_in_binary_units()
    {
        await Assert.That(Quantity.FormatMemory(512)).IsEqualTo("512 B");
        await Assert.That(Quantity.FormatMemory(134_217_728)).IsEqualTo("128 MiB");
        await Assert.That(Quantity.FormatMemory(2_147_483_648)).IsEqualTo("2 GiB");
        await Assert.That(Quantity.FormatMemory(null)).IsEqualTo("—");
    }

    [Test]
    public async Task Percent_needs_a_positive_total()
    {
        await Assert.That(Quantity.Percent(50, 200)).IsEqualTo(25d);
        await Assert.That(Quantity.Percent(50, 0)).IsNull();
        await Assert.That(Quantity.Percent(null, 200)).IsNull();
    }
}

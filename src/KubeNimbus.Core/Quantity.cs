using System.Globalization;

namespace KubeNimbus.Core;

/// <summary>
/// Parsing and display formatting for Kubernetes resource quantities
/// (<c>"100m"</c>, <c>"1.5"</c>, <c>"128974848"</c>, <c>"129M"</c>, <c>"123Mi"</c>,
/// <c>"12345n"</c>, <c>"129e6"</c>).
/// </summary>
/// <remarks>
/// KubernetesClient.Aot ships a <c>ResourceQuantity</c> type, but it only covers
/// typed models — metrics.k8s.io and CRD objects arrive here as raw JSON
/// (<see cref="DynamicResource"/>), so quantities show up as plain strings.
/// This is a small, allocation-light, AOT-safe reader for exactly that: no
/// regex, no culture dependence (the wire format is always invariant).
/// </remarks>
public static class Quantity
{
    /// <summary>
    /// Parses a quantity into its base unit (cores for CPU, bytes for memory).
    /// Returns null for null/empty/unparseable input — callers render "—"
    /// rather than a misleading zero.
    /// </summary>
    public static double? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.AsSpan().Trim();
        var numberLength = NumberLength(text);
        if (numberLength == 0)
        {
            return null;
        }

        if (!double.TryParse(text[..numberLength], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var multiplier = Multiplier(text[numberLength..]);
        return multiplier is null ? null : number * multiplier.Value;
    }

    /// <summary>CPU quantity as nanocores (the unit metrics.k8s.io reports in), or null.</summary>
    public static long? ParseCpuNanocores(string? value) =>
        Parse(value) is { } cores ? (long)Math.Round(cores * 1_000_000_000d) : null;

    /// <summary>Memory quantity as bytes, or null.</summary>
    public static long? ParseBytes(string? value) =>
        Parse(value) is { } bytes ? (long)Math.Round(bytes) : null;

    /// <summary>
    /// Length of the leading numeric part, including an optional sign, decimal
    /// point and scientific exponent — everything after it is the suffix.
    /// </summary>
    private static int NumberLength(ReadOnlySpan<char> text)
    {
        var i = 0;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }

        var digits = 0;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
        {
            if (char.IsAsciiDigit(text[i]))
            {
                digits++;
            }

            i++;
        }

        if (digits == 0)
        {
            return 0;
        }

        // A trailing 'e'/'E' is an exponent only when actually followed by a
        // number; otherwise it's the exa suffix ("2E" = 2 × 10^18).
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var j = i + 1;
            if (j < text.Length && (text[j] == '+' || text[j] == '-'))
            {
                j++;
            }

            if (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                while (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    j++;
                }

                i = j;
            }
        }

        return i;
    }

    private static double? Multiplier(ReadOnlySpan<char> suffix) => suffix switch
    {
        "" => 1d,
        "n" => 1e-9,
        "u" => 1e-6,
        "m" => 1e-3,
        "k" => 1e3,
        "M" => 1e6,
        "G" => 1e9,
        "T" => 1e12,
        "P" => 1e15,
        "E" => 1e18,
        "Ki" => 1024d,
        "Mi" => 1024d * 1024,
        "Gi" => 1024d * 1024 * 1024,
        "Ti" => 1024d * 1024 * 1024 * 1024,
        "Pi" => 1024d * 1024 * 1024 * 1024 * 1024,
        "Ei" => 1024d * 1024 * 1024 * 1024 * 1024 * 1024,
        _ => null,
    };

    /// <summary>
    /// CPU for display: millicores below one core ("250m"), cores with up to two
    /// decimals above it ("1.25"). Matches how kubectl/k9s read at a glance.
    /// </summary>
    public static string FormatCpu(long? nanocores)
    {
        if (nanocores is not { } n)
        {
            return "—";
        }

        var millicores = n / 1_000_000d;
        return millicores < 1000
            ? $"{Math.Round(millicores):0}m"
            : (n / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Memory for display in binary units, the unit Kubernetes itself reports.</summary>
    public static string FormatMemory(long? bytes)
    {
        if (bytes is not { } b)
        {
            return "—";
        }

        const double Ki = 1024, Mi = Ki * 1024, Gi = Mi * 1024, Ti = Gi * 1024;
        return b switch
        {
            < (long)Ki => $"{b} B",
            < (long)Mi => $"{b / Ki:0.#} KiB",
            < (long)Gi => $"{b / Mi:0.#} MiB",
            < (long)Ti => $"{b / Gi:0.##} GiB",
            _ => $"{b / Ti:0.##} TiB",
        };
    }

    /// <summary>Usage as a percentage of a limit/capacity, or null when there's no limit to compare against.</summary>
    public static double? Percent(long? used, long? total) =>
        used is { } u && total is { } t && t > 0 ? u * 100d / t : null;
}

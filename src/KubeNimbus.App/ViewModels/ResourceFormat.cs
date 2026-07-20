using System.Globalization;

namespace KubeNimbus.App.ViewModels;

/// <summary>Human-readable CPU/memory formatting shared by the pod list column, pod detail, and (future) node views.</summary>
public static class ResourceFormat
{
    /// <summary>Cores as millicores, e.g. 0.023 → "23m" — matches kubectl's own CPU display convention.</summary>
    public static string Cpu(double cores) =>
        $"{Math.Round(cores * 1000, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}m";

    /// <summary>Binary (Mi/Gi) formatting, matching how Kubernetes itself reports memory.</summary>
    public static string Memory(long bytes)
    {
        const double ki = 1024;
        const double mi = ki * 1024;
        const double gi = mi * 1024;

        return bytes switch
        {
            >= (long)gi => $"{(bytes / gi).ToString("0.#", CultureInfo.InvariantCulture)}Gi",
            >= (long)mi => $"{(bytes / mi).ToString("0.#", CultureInfo.InvariantCulture)}Mi",
            >= (long)ki => $"{(bytes / ki).ToString("0.#", CultureInfo.InvariantCulture)}Ki",
            _ => $"{bytes}B",
        };
    }

    /// <summary>"120m · 84Mi" combined readout used in the pod list column and pod detail.</summary>
    public static string Combined(double cpuCores, long memoryBytes) => $"{Cpu(cpuCores)} · {Memory(memoryBytes)}";
}

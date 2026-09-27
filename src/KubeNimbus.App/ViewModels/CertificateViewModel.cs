using System.Globalization;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One certificate in a Secret, as the YAML editor's certificate card shows it (FEAT-30):
/// who it is for, who signed it, the names it covers, and — worded, coloured and first —
/// how long it has left. Built against a given instant so the wording is testable.
/// </summary>
public sealed class CertificateViewModel
{
    public CertificateViewModel(string key, int index, int count, CertificateSummary certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        Certificate = certificate;
        Key = key;

        // "tls.crt" for a lone certificate, "tls.crt · 2 of 2" inside a bundle — which one
        // of a chain this is matters, and the leaf is always first in a well-formed one.
        Position = count > 1 ? $"{key} · {index + 1} of {count}" : key;

        Name = certificate.SubjectCommonName.Length > 0 ? certificate.SubjectCommonName : certificate.Subject;
        IssuerText = certificate.IsSelfIssued
            ? "self-signed"
            : $"issued by {(certificate.IssuerCommonName.Length > 0 ? certificate.IssuerCommonName : certificate.Issuer)}";
        Role = certificate.IsCertificateAuthority ? "CA" : "";
        NamesText = certificate.SubjectAlternativeNames.Count > 0
            ? string.Join(", ", certificate.SubjectAlternativeNames)
            : "no subject alternative names";
        ValidityText = string.Create(
            CultureInfo.InvariantCulture,
            $"{certificate.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm} → {certificate.NotAfter.UtcDateTime:yyyy-MM-dd HH:mm} UTC");

        var left = certificate.NotAfter - now;
        (ExpiryText, Health) = true switch
        {
            _ when now < certificate.NotBefore => ($"not valid until {Day(certificate.NotBefore)}", ResourceHealth.Error),
            _ when left < TimeSpan.Zero => ($"expired {Span(-left)} ago", ResourceHealth.Error),
            _ when left <= YamlEditorTabViewModel.CertificateWarningWindow => ($"expires in {Span(left)}", ResourceHealth.Warn),
            _ => ($"expires in {Span(left)}", ResourceHealth.Ok),
        };
        Tooltip = $"{certificate.Subject}\nissuer: {certificate.Issuer}\nserial: {certificate.SerialNumber}";
    }

    public CertificateSummary Certificate { get; }

    /// <summary>The Secret key it came from.</summary>
    public string Key { get; }

    public string Position { get; }

    /// <summary>The subject's common name, or the whole subject when there is none.</summary>
    public string Name { get; }

    public string IssuerText { get; }

    /// <summary>"CA" for a certificate authority, empty for a leaf.</summary>
    public string Role { get; }

    public bool IsAuthority => Role.Length > 0;

    public string NamesText { get; }

    public string ValidityText { get; }

    /// <summary>"expires in 15 days" / "expired 3 days ago" / "not valid until 2026-10-01".</summary>
    public string ExpiryText { get; }

    /// <summary>ok, warn (the last 30 days) or error (expired, or not yet valid).</summary>
    public string Health { get; }

    public string Tooltip { get; }

    private static string Day(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Years past two of them (a CA's "1556 days" is a number nobody reads), days down to
    /// one, hours under that: "expires in 5 hours" is the case worth being exact about.
    /// </summary>
    private static string Span(TimeSpan span) => span.TotalDays switch
    {
        >= 730 => Plural((int)(span.TotalDays / 365.25), "year"),
        >= 1 => Plural((int)span.TotalDays, "day"),
        _ => span.TotalHours >= 1 ? Plural((int)span.TotalHours, "hour") : Plural(Math.Max(1, (int)span.TotalMinutes), "minute"),
    };

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}

using System;
using System.Linq;

namespace YAVSOS.Core.Classification;

public enum OptionalCapabilityState { Absent, Supported, Incompatible }

/// <summary>
/// Detached result of a trusted main-thread probe of one optional mod API. Only Supported admits evidence;
/// Absent/Incompatible keep every dependent dimension Unknown. The witness participates in receipt identity.
/// </summary>
public sealed class OptionalCapability
{
    public string Adapter { get; }
    public OptionalCapabilityState State { get; }
    public string ObservedVersion { get; }
    public string Diagnostic { get; }
    public bool Supported => State == OptionalCapabilityState.Supported;
    public string Witness => Adapter + "=" + State + "; observed=" + (ObservedVersion.Length == 0 ? "none" : ObservedVersion) + "; " + Diagnostic;
    public OptionalCapability(string adapter, OptionalCapabilityState state, string observedVersion, string diagnostic)
    {
        if (!Enum.IsDefined(typeof(OptionalCapabilityState), state)) throw new ArgumentOutOfRangeException(nameof(state));
        ClassificationValidation.Text(adapter, 64);
        if (adapter.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) throw new ArgumentException("Invalid adapter identity.");
        if (observedVersion == null || observedVersion.Length > 64 || observedVersion.Any(char.IsControl)) throw new ArgumentException("Invalid observed version.");
        ClassificationValidation.Text(diagnostic, 512);
        Adapter = adapter; State = state; ObservedVersion = observedVersion; Diagnostic = diagnostic;
    }
    public static OptionalCapability Absent(string adapter) => new OptionalCapability(adapter, OptionalCapabilityState.Absent, "", "plugin not loaded");
    // Exception text can contain paths or arbitrary member names; diagnostics keep only a bounded reason.
    public static OptionalCapability Incompatible(string adapter, string observedVersion, string reason) =>
        new OptionalCapability(adapter, OptionalCapabilityState.Incompatible, Bound(observedVersion, 64), Bound(reason, 512));
    private static string Bound(string? value, int maximum)
    {
        var text = new string((value ?? "").Select(c => char.IsControl(c) || char.IsSurrogate(c) ? '?' : c).ToArray()).Trim();
        if (text.Length > maximum) text = text.Substring(0, maximum);
        return text.Length == 0 && maximum == 512 ? "incompatible" : text;
    }
}

using System.Globalization;
using Actuarial.Domain;

namespace Actuarial.Web.Services;

public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Num(decimal? v, MeasureKind? measure = null)
    {
        if (v is not { } x) return "";
        if (measure == MeasureKind.Factor) return x.ToString("0.0000", C);
        if (measure == MeasureKind.ClaimCount) return x.ToString("#,0", C);
        if (Math.Abs(x) < 10m && x != Math.Round(x)) return x.ToString("0.0000", C);
        return x.ToString("#,0", C);
    }

    public static string Input(decimal? v) => v?.ToString("0.############", C) ?? "";

    public static bool TryParse(string? s, out decimal v) =>
        decimal.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Number, C, out v);

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", C);
    public static string When(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", C);
    public static string Short(string hash) => hash.Length > 10 ? hash[..10] : hash;

    public static string Provenance(Provenance p) => p.Kind switch
    {
        ProvenanceKind.Entered => "Entered in this analysis",
        ProvenanceKind.CarriedForward => "Carried forward from an earlier analysis — review it",
        _ => "Remapped from a period of another length — review it"
    };

    public static string ProvClass(Provenance? p) => p?.Kind switch
    {
        ProvenanceKind.CarriedForward => "prov carried",
        ProvenanceKind.Remapped => "prov remapped",
        ProvenanceKind.Entered => "prov entered",
        _ => ""
    };

    /// <summary>Is the column an amount that makes sense to total?</summary>
    public static bool Totals(MeasureKind m) => m is MeasureKind.Paid or MeasureKind.Incurred or MeasureKind.Premium or MeasureKind.Ultimate or MeasureKind.ClaimCount;

    public static string Grain(GrainKind g) => g switch
    {
        GrainKind.DevelopmentCell => "triangle",
        GrainKind.DevelopmentPeriod => "per development age",
        GrainKind.ExposurePeriod => "per exposure period",
        _ => "single value"
    };

    public static string Statistic(StatisticKind k) => k switch
    {
        StatisticKind.SimpleAverage => "Simple average",
        StatisticKind.VolumeWeightedAverage => "Volume-weighted average",
        StatisticKind.Latest => "Latest",
        StatisticKind.Medial => "Medial average",
        _ => "Custom (entered)"
    };
}

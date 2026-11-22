using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Actuarial.Domain;

namespace Actuarial.Application.Data;

/// <summary>
/// One aggregated movement in the raw claim data. Months are absolute month indices (see <see cref="Months"/>).
/// The warehouse holds quarterly granularity: accident quarter start month, transaction quarter end month.
/// </summary>
public sealed record ClaimCell(int AccidentMonth, int TransactionMonth, decimal Paid, decimal Incurred, decimal ReportedCount);

public sealed record PremiumCell(int AccidentMonth, decimal Amount);

/// <summary>A raw warehouse row: attributes (line of business, region) plus the movement.</summary>
public sealed record RawClaimRow(string Lob, string Region, ClaimCell Cell);
public sealed record RawPremiumRow(string Lob, string Region, PremiumCell Cell);

/// <summary>The frozen data an analysis reads. Its hash is recorded in <see cref="DataSnapshot.ContentHash"/>.</summary>
public sealed record SnapshotData(
    DataSnapshot Snapshot,
    DateTimeOffset TakenAt,
    IReadOnlyList<ClaimCell> Claims,
    IReadOnlyList<PremiumCell> Premium)
{
    public static string Hash(IEnumerable<ClaimCell> claims, IEnumerable<PremiumCell> premium)
    {
        var sb = new StringBuilder();
        foreach (var c in claims.OrderBy(c => c.AccidentMonth).ThenBy(c => c.TransactionMonth))
            sb.Append(CultureInfo.InvariantCulture, $"C|{c.AccidentMonth}|{c.TransactionMonth}|{c.Paid}|{c.Incurred}|{c.ReportedCount}\n");
        foreach (var p in premium.OrderBy(p => p.AccidentMonth))
            sb.Append(CultureInfo.InvariantCulture, $"P|{p.AccidentMonth}|{p.Amount}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Raw claim data before segmentation.</summary>
public sealed class ClaimWarehouse
{
    public IReadOnlyList<RawClaimRow> Claims { get; }
    public IReadOnlyList<RawPremiumRow> Premium { get; }
    /// <summary>Last month for which data exists (quarter end).</summary>
    public DateOnly DataThrough { get; }
    public int GranularityMonths => 3;

    public ClaimWarehouse(IReadOnlyList<RawClaimRow> claims, IReadOnlyList<RawPremiumRow> premium, DateOnly dataThrough)
        => (Claims, Premium, DataThrough) = (claims, premium, dataThrough);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes => new Dictionary<string, IReadOnlyList<string>>
    {
        ["lob"] = Claims.Select(c => c.Lob).Distinct().OrderBy(x => x).ToList(),
        ["region"] = Claims.Select(c => c.Region).Distinct().OrderBy(x => x).ToList(),
    };

    public static bool Matches(Segment s, string lob, string region) =>
        Match(s, "lob", lob) && Match(s, "region", region);

    private static bool Match(Segment s, string attr, string value) =>
        !s.Filters.TryGetValue(attr, out var f) || f == "*" || string.IsNullOrEmpty(f) ||
        f.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Filters by segment and cuts at the valuation date. Rows are aggregated over the segment's attributes.</summary>
    public (List<ClaimCell> Claims, List<PremiumCell> Premium) Extract(Segment segment, DateOnly valuation)
    {
        var v = Months.Index(valuation);
        var claims = Claims.Where(r => Matches(segment, r.Lob, r.Region) && r.Cell.TransactionMonth <= v && r.Cell.AccidentMonth <= v)
            .GroupBy(r => (r.Cell.AccidentMonth, r.Cell.TransactionMonth))
            .Select(g => new ClaimCell(g.Key.AccidentMonth, g.Key.TransactionMonth,
                g.Sum(x => x.Cell.Paid), g.Sum(x => x.Cell.Incurred), g.Sum(x => x.Cell.ReportedCount)))
            .OrderBy(c => c.AccidentMonth).ThenBy(c => c.TransactionMonth)
            .ToList();
        var premium = Premium.Where(r => Matches(segment, r.Lob, r.Region) && r.Cell.AccidentMonth <= v)
            .GroupBy(r => r.Cell.AccidentMonth)
            .Select(g => new PremiumCell(g.Key, g.Sum(x => x.Cell.Amount)))
            .OrderBy(p => p.AccidentMonth)
            .ToList();
        return (claims, premium);
    }
}

/// <summary>Deterministic synthetic portfolio: three lines of business in two regions, 2012 Q1 to 2026 Q3.</summary>
public static class ClaimGenerator
{
    private sealed record LineProfile(string Lob, decimal BasePremium, double LossRatio, double PayTau, double ReportTau, double Shape, double Severity, double Volatility);

    private static readonly LineProfile[] Lines =
    {
        new("Motor",     4_200_000m, 0.68, 4.0, 1.6, 1.15, 6_500, 0.05),
        new("Property",  3_100_000m, 0.58, 2.2, 0.9, 1.30, 14_000, 0.16),
        new("Liability", 2_400_000m, 0.74, 11.0, 4.5, 1.05, 38_000, 0.08),
    };

    private static readonly (string Region, decimal Scale)[] Regions = { ("North", 1.0m), ("South", 0.75m) };

    public static ClaimWarehouse Generate(int seed = 20261122)
    {
        var first = Months.Index(new DateOnly(2012, 1, 1));
        var through = new DateOnly(2026, 9, 30);
        var last = Months.Index(through);
        var rng = new Random(seed);
        var claims = new List<RawClaimRow>();
        var premium = new List<RawPremiumRow>();

        foreach (var line in Lines)
        foreach (var (region, scale) in Regions)
        {
            for (var aq = first; aq <= last - 2; aq += 3)
            {
                var t = (aq - first) / 12.0;
                var season = 1 + 0.04 * Math.Sin((aq % 12) / 12.0 * 2 * Math.PI);
                var prem = Math.Round(line.BasePremium / 4m * scale * (decimal)(Math.Pow(1.045, t) * season * (1 + 0.03 * Normal(rng))), 0);
                premium.Add(new RawPremiumRow(line.Lob, region, new PremiumCell(aq, prem)));

                // Underwriting cycle plus noise, and the odd catastrophe quarter for property.
                var lr = line.LossRatio * (1 + 0.06 * Math.Sin(t / 2.2)) * (1 + line.Volatility * Normal(rng));
                if (line.Lob == "Property" && rng.NextDouble() < 0.06) lr *= 1.8;
                var ultimate = (double)prem * lr;
                var adequacy = 1 + 0.08 * Normal(rng);

                double prevPaid = 0, prevInc = 0, prevCount = 0;
                for (var q = 0; aq + 3 * q + 2 <= last && q < 60; q++)
                {
                    var age = q + 1;
                    var paidPct = 1 - Math.Exp(-Math.Pow(age / line.PayTau, line.Shape));
                    var repPct = 1 - Math.Exp(-Math.Pow(age / line.ReportTau, line.Shape));
                    var incPct = repPct * (1 + (adequacy - 1) * Math.Exp(-age / 6.0));

                    var paid = Math.Max(prevPaid, ultimate * paidPct * (1 + 0.015 * Normal(rng)));
                    var inc = Math.Max(paid, ultimate * incPct * (1 + 0.01 * Normal(rng)));
                    var count = Math.Max(prevCount, Math.Round(ultimate / line.Severity * repPct));

                    var dPaid = (decimal)Math.Round(paid - prevPaid);
                    var dInc = (decimal)Math.Round(inc - prevInc);
                    var dCount = (decimal)(count - prevCount);
                    if (dPaid != 0 || dInc != 0 || dCount != 0)
                        claims.Add(new RawClaimRow(line.Lob, region, new ClaimCell(aq, aq + 3 * q + 2, dPaid, dInc, dCount)));

                    prevPaid = Math.Round(paid); prevInc = Math.Round(inc); prevCount = count;
                }
            }
        }
        return new ClaimWarehouse(claims, premium, through);
    }

    private static double Normal(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

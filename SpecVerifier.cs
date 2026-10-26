using System.Text.RegularExpressions;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TestFlow;

public record Issue(string Where, string Message);
public class HeaderRow { public string Text { get; set; } = ""; }
public class HeaderVec { [ColumnName("F")] public float[] F { get; set; } = Array.Empty<float>(); }

/// <summary>Suggests which generated-file header corresponds to a spec column.
/// ML.NET char n-gram vectors + cosine similarity, plus an abbreviation check ("custid" inside "customerid").</summary>
public class HeaderMatcher
{
    readonly PredictionEngine<HeaderRow, HeaderVec> _eng;
    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    public HeaderMatcher(IEnumerable<string> corpus)
    {
        var ml = new MLContext(seed: 1);
        var data = ml.Data.LoadFromEnumerable(corpus.Select(t => new HeaderRow { Text = Norm(t) }));
        var model = ml.Transforms.Text.FeaturizeText("F", "Text").Fit(data);
        _eng = ml.Model.CreatePredictionEngine<HeaderRow, HeaderVec>(model);
    }

    float[] Vec(string t) => _eng.Predict(new HeaderRow { Text = Norm(t) }).F;
    static double Cos(float[] a, float[] b)
    {
        double d = 0, x = 0, y = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) { d += a[i] * b[i]; x += a[i] * a[i]; y += b[i] * b[i]; }
        return x == 0 || y == 0 ? 0 : d / Math.Sqrt(x * y);
    }
    static bool IsSubseq(string small, string big)
    {
        int i = 0; foreach (var ch in big) if (i < small.Length && small[i] == ch) i++;
        return i == small.Length;
    }

    public (string? best, double score) Best(string target, IEnumerable<string> candidates)
    {
        var t = Vec(target); var nt = Norm(target);
        string? best = null; double bs = 0;
        foreach (var c in candidates)
        {
            var nc = Norm(c); double s = Cos(t, Vec(c));
            var (sh, lg) = nc.Length <= nt.Length ? (nc, nt) : (nt, nc);
            if (sh.Length >= 3 && sh.Length * 2 >= lg.Length && IsSubseq(sh, lg)) s = Math.Max(s, 0.8);
            if (s > bs) { bs = s; best = c; }
        }
        return (best, bs);
    }
}

public static class SpecVerifier
{
    const double MatchThreshold = 0.6;
    static string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    static bool Yes(string s) => s.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

    /// <summary>Checks a generated Excel file against the spec workbook (sheet "Mapping").</summary>
    public static List<Issue> Verify(string specPath, string generatedPath, string specSheet = "Mapping", string delimiter = "", bool hasHeader = true)
    {
        var issues = new List<Issue>();
        var spec = ExcelIO.ReadRows(specPath, specSheet);
        var gen = GeneratedReader.Read(generatedPath, spec, delimiter, hasHeader, w => issues.Add(new("Format", w)));
        if (spec.Count == 0) { issues.Add(new("Spec", "Spec sheet has no rows")); return issues; }
        if (gen.Count == 0) { issues.Add(new("File", "Generated file has no data rows")); return issues; }

        var genCols = gen[0].Keys.ToList();
        var matcher = new HeaderMatcher(spec.Select(s => G(s, "TargetColumn")).Concat(genCols));
        var colMap = new Dictionary<string, string>();

        foreach (var rule in spec)
        {
            var target = G(rule, "TargetColumn");
            if (target == "") continue;
            var hit = genCols.FirstOrDefault(c => HeaderMatcher.Norm(c) == HeaderMatcher.Norm(target));
            if (hit == null)
            {
                var (best, score) = matcher.Best(target, genCols);
                if (best != null && score >= MatchThreshold)
                {
                    hit = best;
                    issues.Add(new("Header", $"'{target}' not found; ML suggests '{best}' ({score:P0}) - please confirm"));
                }
                else { issues.Add(new("Header", $"Missing column '{target}'")); continue; }
            }
            colMap[target] = hit;
        }

        int line = 2;
        foreach (var row in gen)
        {
            foreach (var rule in spec)
            {
                var target = G(rule, "TargetColumn");
                if (!colMap.TryGetValue(target, out var col)) continue;
                var v = row.GetValueOrDefault(col, ""); var at = $"Row {line} / {target}";

                if (v == "") { if (Yes(G(rule, "Required"))) issues.Add(new(at, "Required value is empty")); continue; }

                var type = G(rule, "DataType").ToLowerInvariant();
                var ok = type switch
                {
                    "int" => int.TryParse(v, out _),
                    "decimal" or "number" => decimal.TryParse(v, out _),
                    "date" => DateTime.TryParse(v, out _),
                    "bool" => bool.TryParse(v, out _),
                    _ => true
                };
                if (!ok) issues.Add(new(at, $"'{v}' is not {type}"));

                var pat = G(rule, "Pattern");
                if (pat != "" && !Regex.IsMatch(v, pat)) issues.Add(new(at, $"'{v}' does not match {pat}"));

                var exp = G(rule, "ExpectedValue");
                if (exp != "" && !v.Equals(exp, StringComparison.OrdinalIgnoreCase)) issues.Add(new(at, $"expected '{exp}' but found '{v}'"));
            }
            line++;
        }
        return issues;
    }
}

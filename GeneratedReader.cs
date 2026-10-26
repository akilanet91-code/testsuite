using System.IO;
using System.Text;

namespace TestFlow;

/// <summary>Reads a generated file into rows of header->value.
/// Excel (.xlsx/.xlsm); fixed-width text (spec has numeric Start + Length); or ANY delimited text:
/// one or several characters, auto-detected or given, with or without a header line.</summary>
public static class GeneratedReader
{
    static readonly string[] Candidates = { ",", "\t", "|", ";", "~", "^", ":", "#", " " };

    public static List<Dictionary<string, string>> Read(string path, List<Dictionary<string, string>> spec,
        string delimiter = "", bool hasHeader = true, Action<string>? warn = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm") return ExcelIO.ReadRows(path);

        var lines = ReadLines(path);
        bool fixedWidth = spec.Count > 0 && spec.All(r => IsNum(r, "Start") && IsNum(r, "Length"));
        return fixedWidth ? ReadFixed(lines, spec) : ReadDelimited(lines, spec, delimiter, hasHeader, warn);
    }

    static bool IsNum(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && int.TryParse(v, out var n) && n > 0;

    static List<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, true);
        var res = new List<string>();
        string? l; while ((l = sr.ReadLine()) != null) if (l.Trim().Length > 0) res.Add(l);
        return res;
    }

    static List<Dictionary<string, string>> ReadFixed(List<string> lines, List<Dictionary<string, string>> spec)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines)
        {
            var d = new Dictionary<string, string>();
            foreach (var r in spec)
            {
                int start = int.Parse(r["Start"]) - 1, len = int.Parse(r["Length"]);
                d[r["TargetColumn"]] = start >= line.Length ? "" : line.Substring(start, Math.Min(len, line.Length - start)).Trim();
            }
            rows.Add(d);
        }
        return rows;
    }

    /// <summary>"tab", "\\t", "pipe", "comma", "semicolon", "space", "colon", "tilde", "caret" or any literal text such as "~|~".</summary>
    public static string ParseDelim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Trim().ToLowerInvariant() switch
        {
            "\\t" or "tab" => "\t", "pipe" => "|", "comma" => ",", "semicolon" => ";", "space" => " ",
            "colon" => ":", "tilde" => "~", "caret" => "^", "hash" => "#", _ => s
        };
    }

    static List<Dictionary<string, string>> ReadDelimited(List<string> lines, List<Dictionary<string, string>> spec,
        string delimiter, bool hasHeader, Action<string>? warn)
    {
        var rows = new List<Dictionary<string, string>>();
        if (lines.Count == 0) return rows;
        var d = ParseDelim(delimiter); if (d == "") d = Detect(lines);

        List<string> header; IEnumerable<string> data; int lineNo;
        if (hasHeader) { header = Split(lines[0], d); data = lines.Skip(1); lineNo = 2; }
        else { header = spec.Select(r => r.GetValueOrDefault("TargetColumn", "")).Where(x => x != "").ToList(); data = lines; lineNo = 1; }

        int bad = 0;
        foreach (var line in data)
        {
            var vals = Split(line, d);
            if (vals.Count != header.Count && bad++ < 5)
                warn?.Invoke($"Line {lineNo}: expected {header.Count} fields but found {vals.Count} (check the delimiter)");
            var row = new Dictionary<string, string>();
            for (int i = 0; i < header.Count; i++) row[header[i]] = i < vals.Count ? vals[i] : "";
            rows.Add(row); lineNo++;
        }
        if (bad > 5) warn?.Invoke($"...and {bad - 5} more lines with a different field count");
        return rows;
    }

    /// <summary>Picks the character that appears the same (non-zero) number of times on the first lines; ties go to the most frequent.</summary>
    static string Detect(List<string> lines)
    {
        var sample = lines.Take(10).ToList();
        string best = ","; int bestN = 0;
        foreach (var c in Candidates)
        {
            var counts = sample.Select(l => Count(l, c[0])).ToList();
            if (counts[0] > 0 && counts.All(n => n == counts[0]) && counts[0] > bestN) { best = c; bestN = counts[0]; }
        }
        if (bestN > 0) return best;
        foreach (var c in Candidates) { int n = Count(lines[0], c[0]); if (n > bestN) { bestN = n; best = c; } }
        return best;
    }

    static int Count(string line, char c)
    {
        int n = 0; bool q = false;
        foreach (var ch in line) { if (ch == '"') q = !q; else if (!q && ch == c) n++; }
        return n;
    }

    // handles "quoted, values" and "" escapes; the delimiter may be several characters
    static List<string> Split(string line, string d)
    {
        var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (string.CompareOrdinal(line, i, d, 0, d.Length) == 0) { res.Add(sb.ToString().Trim()); sb.Clear(); i += d.Length - 1; }
            else sb.Append(ch);
        }
        res.Add(sb.ToString().Trim());
        return res;
    }
}

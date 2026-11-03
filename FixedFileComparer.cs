using System.IO;

namespace TestFlow;

/// <summary>Compares two fixed-position files field by field: the EXPECTED file (ExpectedFile) and the ACTUAL file (the newest
/// file matching FileCheckFolder + FileNamePattern, e.g. what a console app just generated). Both are cut with the same FieldLayout
/// (NAME=start,length[,dec:N][,date:fmt]); header / trailer lines are skipped on both. Without a FieldLayout the files are compared line by line.
/// Records are paired by KeyColumn (duplicates allowed, paired in order) or by line order.</summary>
public static class FixedFileComparer
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        if (string.IsNullOrWhiteSpace(tc.ExpectedFile)) return (false, "ExpectedFile is empty (the file the actual file is compared with)");
        var expectedPath = Abs(tc.ExpectedFile);
        if (!File.Exists(expectedPath)) return (false, "Expected file not found: " + expectedPath);

        bool wholeLine = string.IsNullOrWhiteSpace(tc.FieldLayout);
        List<FixedFileDbComparer.Field> fields;
        if (wholeLine) fields = new() { new FixedFileDbComparer.Field("LINE", 1, 100000, 0, "") };
        else
        {
            var (f, err) = FixedFileDbComparer.ParseLayout(tc.FieldLayout);
            if (err != null) return (false, err);
            fields = f;
        }

        // actual file = newest match, optionally wait for it
        var folder = Abs(tc.FileCheckFolder);
        var pattern = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*" : tc.FileNamePattern;
        string? actualPath = null; var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
        while (true)
        {
            if (Directory.Exists(folder))
                actualPath = Directory.EnumerateFiles(folder, pattern).OrderByDescending(x => File.GetLastWriteTime(x)).FirstOrDefault();
            if (actualPath != null) break;
            if (DateTime.UtcNow >= deadline) return (false, $"Actual file not found: {pattern} in {folder}");
            await Task.Delay(500, ct);
        }

        var issues = new List<string>();
        var exp = await ReadAsync(expectedPath, tc, fields, wholeLine, "expected", issues, ct);
        var act = await ReadAsync(actualPath, tc, fields, wholeLine, "actual", issues, ct);
        if (exp == null || act == null) return (false, string.Join("; ", issues));

        var ignore = new HashSet<string>((tc.IgnoreColumns ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var cols = fields.Select(f => f.Column).Where(c => !ignore.Contains(c)).ToList();

        // pair records: by key (duplicates paired in order) or by line order
        string key = (tc.KeyColumn ?? "").Trim();
        if (key.Length > 0 && !fields.Any(f => f.Column.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return (false, $"Key column “{key}” is not in FieldLayout");
        var pairs = new List<(string label, Dictionary<string, string>? e, Dictionary<string, string>? a)>();
        if (key.Length > 0)
        {
            var byKey = new Dictionary<string, Queue<Dictionary<string, string>>>();
            foreach (var a in act)
            {
                var k = DbExcelComparer.Norm(a[key]);
                if (!byKey.TryGetValue(k, out var q)) byKey[k] = q = new Queue<Dictionary<string, string>>();
                q.Enqueue(a);
            }
            var nth = new Dictionary<string, int>();
            foreach (var e in exp)
            {
                var k = DbExcelComparer.Norm(e[key]);
                nth[k] = nth.GetValueOrDefault(k) + 1;
                var a = byKey.TryGetValue(k, out var q) && q.Count > 0 ? q.Dequeue() : null;
                pairs.Add(($"{key}={e[key]}" + (nth[k] > 1 ? $" (#{nth[k]})" : ""), e, a));
            }
            foreach (var (k, q) in byKey)
            {
                int n = nth.GetValueOrDefault(k);
                foreach (var a in q) pairs.Add(($"{key}={a[key]}" + (++n > 1 ? $" (#{n})" : ""), null, a));
            }
        }
        else
            for (int i = 0; i < Math.Max(exp.Count, act.Count); i++)
                pairs.Add(($"Record {i + 1}", i < exp.Count ? exp[i] : null, i < act.Count ? act[i] : null));

        int matched = 0;
        static string Show(string v, bool whole) => whole && v.Length > 80 ? v[..80] + " …" : v;
        foreach (var (label, e, a) in pairs)
        {
            if (e == null) { issues.Add($"{label}: only in the actual file"); continue; }
            if (a == null) { issues.Add($"{label}: only in the expected file"); continue; }
            bool rowOk = true;
            foreach (var c in cols)
            {
                var (same, _) = DbExcelComparer.Same(e[c], a[c], tc.CompareMode, tc.Tolerance);
                if (!same)
                {
                    rowOk = false;
                    issues.Add(wholeLine ? $"{label}: line differs, expected “{Show(e[c], true)}” ≠ actual “{Show(a[c], true)}”"
                                         : $"{label}: {c} expected “{e[c]}” ≠ actual “{a[c]}”");
                }
            }
            if (rowOk) matched++;
        }
        string summary = $"{Path.GetFileName(actualPath)} vs {Path.GetFileName(expectedPath)}: {act.Count} actual / {exp.Count} expected record(s), {(wholeLine ? "line by line" : cols.Count + " field(s)")}, {matched} record(s) identical";
        return (issues.Count == 0, issues.Count == 0 ? summary : $"{summary}. {issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    static async Task<List<Dictionary<string, string>>?> ReadAsync(string path, TestCase tc, List<FixedFileDbComparer.Field> fields, bool wholeLine, string which, List<string> issues, CancellationToken ct)
    {
        var lines = (PdfTextReader.IsPdf(path)
            ? await Task.Run(() => PdfTextReader.ReadLines(path, PdfLineMode.Layout), ct)
            : (await File.ReadAllLinesAsync(path, ct)).ToList()).Where(l => l.Trim().Length > 0).ToList();
        int head = Math.Max(0, tc.SkipHeaderLines), tail = Math.Max(0, tc.SkipTrailerLines);
        if (lines.Count < head + tail) { issues.Add($"The {which} file {Path.GetFileName(path)} has only {lines.Count} line(s), fewer than header + trailer lines"); return null; }
        int minLen = wholeLine ? 0 : fields.Max(f => f.Start + f.Length - 1);
        var rows = new List<Dictionary<string, string>>();
        int i = 0;
        foreach (var line in lines.Skip(head).Take(lines.Count - head - tail))
        {
            i++;
            if (line.Length < minLen && issues.Count < 5) issues.Add($"The {which} file, record {i}: only {line.Length} characters, layout needs {minLen}");
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields) d[f.Column] = wholeLine ? line.TrimEnd() : FixedFileDbComparer.Cut(line, f);
            rows.Add(d);
        }
        return rows;
    }
}

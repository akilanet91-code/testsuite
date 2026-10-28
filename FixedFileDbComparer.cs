using System.Globalization;
using System.IO;
using System.Text;

namespace TestFlow;

/// <summary>After a console app has processed a fixed-position file and updated the database,
/// compares every record of the file with the database rows, field by field.</summary>
public static class FixedFileDbComparer
{
    sealed record Field(string Column, int Start, int Length, int Decimals, string DateFormat);

    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        if (string.IsNullOrWhiteSpace(tc.DbConnectionString)) return (false, "DbConnectionString is empty");
        if (string.IsNullOrWhiteSpace(tc.DbQuery)) return (false, "DbQuery is empty");
        var (fields, layoutError) = ParseLayout(tc.FieldLayout);
        if (layoutError != null) return (false, layoutError);

        // 1. find the file (newest match), optionally wait for it
        var folder = Abs(tc.FileCheckFolder);
        var pattern = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*" : tc.FileNamePattern;
        string? file = null; var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
        while (true)
        {
            if (Directory.Exists(folder))
                file = Directory.EnumerateFiles(folder, pattern).OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            if (file != null) break;
            if (DateTime.UtcNow >= deadline) return (false, $"File not found: {pattern} in {folder}");
            await Task.Delay(500, ct);
        }

        // 2. read records (skip header / trailer lines) and cut the fields by position
        var lines = (await File.ReadAllLinesAsync(file, ct)).Where(l => l.Trim().Length > 0).ToList();
        int head = Math.Max(0, tc.SkipHeaderLines), tail = Math.Max(0, tc.SkipTrailerLines);
        var issues = new List<string>();
        if (lines.Count < head + tail) return (false, $"{Path.GetFileName(file)} has only {lines.Count} line(s), fewer than header + trailer lines");
        var records = lines.Skip(head).Take(lines.Count - head - tail).ToList();
        int minLen = fields.Max(f => f.Start + f.Length - 1);
        var fileRows = new List<Dictionary<string, string>>();
        for (int i = 0; i < records.Count; i++)
        {
            var line = records[i];
            if (line.Length < minLen && issues.Count < 5) issues.Add($"File line {head + i + 1}: only {line.Length} characters, layout needs {minLen}");
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields) d[f.Column] = Cut(line, f);
            fileRows.Add(d);
        }

        // 3. database rows (after the console app has updated them)
        var dbCols = new List<string>(); var dbRows = new List<Dictionary<string, string>>();
        await using (var con = ApiDbVerifier.CreateConnection(tc.DbProvider, tc.DbConnectionString))
        {
            await con.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = tc.DbQuery; cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            for (int i = 0; i < rd.FieldCount; i++) dbCols.Add(rd.GetName(i));
            while (await rd.ReadAsync(ct))
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++) d[dbCols[i]] = rd.IsDBNull(i) ? "" : ApiDbVerifier.DbToString(rd.GetValue(i));
                dbRows.Add(d);
            }
        }
        foreach (var f in fields.ToList())
            if (!dbCols.Contains(f.Column, StringComparer.OrdinalIgnoreCase)) { issues.Add($"Column “{f.Column}” is in the layout but not in the query result"); fields.Remove(f); }
        if (fields.Count == 0) return (false, "No layout column matches the query result. " + string.Join("; ", issues));

        // 4. pair rows by key column, or by order
        string key = (tc.KeyColumn ?? "").Trim();
        if (key.Length > 0 && !fields.Any(f => f.Column.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return (false, $"Key column “{key}” must be in FieldLayout and in the query result");
        var pairs = new List<(string label, Dictionary<string, string>? file, Dictionary<string, string>? db)>();
        if (key.Length > 0)
        {
            var byKey = new Dictionary<string, Dictionary<string, string>>();
            foreach (var r in fileRows)
                if (!byKey.TryAdd(DbExcelComparer.Norm(r[key]), r)) issues.Add($"{key}={r[key]}: appears more than once in the file");
            var seen = new HashSet<string>();
            foreach (var d in dbRows) { var k = DbExcelComparer.Norm(d[key]); seen.Add(k); pairs.Add(($"{key}={d[key]}", byKey.GetValueOrDefault(k), d)); }
            foreach (var (k, r) in byKey) if (!seen.Contains(k)) pairs.Add(($"{key}={r[key]}", r, null));
        }
        else
            for (int i = 0; i < Math.Max(fileRows.Count, dbRows.Count); i++)
                pairs.Add(($"Record {i + 1}", i < fileRows.Count ? fileRows[i] : null, i < dbRows.Count ? dbRows[i] : null));

        // 5. compare every field
        int matched = 0;
        foreach (var (label, fr, db) in pairs)
        {
            if (fr == null) { issues.Add($"{label}: in database but not in file"); continue; }
            if (db == null) { issues.Add($"{label}: in file but not in database"); continue; }
            bool rowOk = true;
            foreach (var f in fields)
            {
                var (same, _) = DbExcelComparer.Same(db[f.Column], fr[f.Column], tc.CompareMode, tc.Tolerance);
                if (!same) { rowOk = false; issues.Add($"{label}: {f.Column} file “{fr[f.Column]}” ≠ DB “{db[f.Column]}”"); }
            }
            if (rowOk) matched++;
        }
        string summary = $"{Path.GetFileName(file)}: {fileRows.Count} file record(s), {dbRows.Count} DB row(s), {fields.Count} field(s) compared, {matched} record(s) fully match";
        return (issues.Count == 0, issues.Count == 0 ? summary : $"{summary}. {issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    /// <summary>One field per line (or separated by ;):  DbColumn=start,length  [,dec:N] [,date:yyyyMMdd]   (start is 1-based)</summary>
    static (List<Field>, string?) ParseLayout(string text)
    {
        var list = new List<Field>();
        foreach (var raw in (text ?? "").Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim(); if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            if (eq < 1) return (list, $"FieldLayout line “{t}”: use DbColumn=start,length");
            var col = t[..eq].Trim(); var parts = t[(eq + 1)..].Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[1], out var len) || start < 1 || len < 1)
                return (list, $"FieldLayout line “{t}”: start and length must be numbers (start is 1-based)");
            int dec = 0; string date = "";
            foreach (var p in parts.Skip(2))
            {
                if (p.StartsWith("dec:", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[4..], out var d)) dec = d;
                else if (p.StartsWith("date:", StringComparison.OrdinalIgnoreCase)) date = p[5..];
                else return (list, $"FieldLayout line “{t}”: unknown option “{p}” (use dec:N or date:yyyyMMdd)");
            }
            list.Add(new Field(col, start, len, dec, date));
        }
        return list.Count == 0 ? (list, "FieldLayout is empty (e.g. ACCT_ID=1,6)") : (list, null);
    }

    static string Cut(string line, Field f)
    {
        int i = f.Start - 1;
        string s = i >= line.Length ? "" : line.Substring(i, Math.Min(f.Length, line.Length - i));
        s = s.Trim();
        if (f.DateFormat.Length > 0)
            return DateTime.TryParseExact(s, f.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.ToString("yyyy-MM-dd") : s;
        if (f.Decimals > 0 && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
        {
            for (int k = 0; k < f.Decimals; k++) n /= 10m;
            return n.ToString(CultureInfo.InvariantCulture);
        }
        return s;
    }
}

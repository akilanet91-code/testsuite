using System.IO;

namespace TestFlow;

/// <summary>Reads a generated CSV report (any delimiter, header line) and compares it with the database,
/// field by field: runs DbQuery, pairs the CSV records with the DB rows by KeyColumn (or by order) and
/// reports wrong values, records only in the CSV, rows only in the DB and columns that cannot be found.</summary>
public static class CsvDbComparer
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        var (dbCs, dbErr) = AppConfig.GetDb(); if (dbErr != null) return (false, dbErr);
        if (string.IsNullOrWhiteSpace(tc.DbQuery)) return (false, "DbQuery is empty");
        var (map, mapErr) = ParseMap(tc.ColumnMap);
        if (mapErr != null) return (false, mapErr);

        // 1. find the CSV (newest match), optionally wait for it
        var folder = Abs(tc.FileCheckFolder);
        var pattern = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*.csv" : tc.FileNamePattern;
        string? file = null; var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
        while (true)
        {
            if (Directory.Exists(folder))
                file = Directory.EnumerateFiles(folder, pattern).OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            if (file != null) break;
            if (DateTime.UtcNow >= deadline) return (false, $"CSV file not found: {pattern} in {folder}");
            await Task.Delay(500, ct);
        }

        // 2. read the CSV: header line + records (columns by header name, or by ColumnMap order when HasHeader = No)
        bool hasHeader = !(tc.HasHeader.Trim().ToLowerInvariant() is "no" or "n" or "false" or "0");
        if (!hasHeader && map.Count == 0) return (false, "HasHeader = No needs ColumnMap: the CSV columns are taken from its order");
        var spec = map.Select(m => new Dictionary<string, string> { ["TargetColumn"] = m.Csv }).ToList();
        var issues = new List<string>();
        var csvRaw = await Task.Run(() => GeneratedReader.Read(file, hasHeader ? new List<Dictionary<string, string>>() : spec,
            tc.Delimiter, hasHeader, w => issues.Add(w)), ct);
        if (csvRaw.Count == 0) return (false, $"{Path.GetFileName(file)} has no data rows" + (issues.Count > 0 ? ": " + string.Join("; ", issues) : ""));
        var csvRows = csvRaw.Select(r => new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase)).ToList();
        var csvCols = csvRows[0].Keys.ToList();

        // 3. database rows
        var dbCols = new List<string>(); var dbRows = new List<Dictionary<string, string>>();
        await using (var con = ApiDbVerifier.CreateConnection(dbCs))
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

        // 4. which DB column is compared with which CSV column
        var ignore = new HashSet<string>((tc.IgnoreColumns ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        var pairsCols = new List<(string Db, string Csv)>();
        if (map.Count > 0) pairsCols.AddRange(map.Select(m => (m.Db, m.Csv)));
        else pairsCols.AddRange(dbCols.Select(c => (c, c)));
        pairsCols = pairsCols.Where(p => !ignore.Contains(p.Db)).ToList();

        var cols = new List<(string Db, string Csv)>();   // Csv = exact header name found in the file
        foreach (var (db, csv) in pairsCols)
        {
            var dbHit = dbCols.FirstOrDefault(c => c.Equals(db, StringComparison.OrdinalIgnoreCase));
            var csvHit = csvCols.FirstOrDefault(c => HeaderMatcher.Norm(c) == HeaderMatcher.Norm(csv));
            if (dbHit == null) { issues.Add($"Column “{db}” is not in the query result (it returned: {string.Join(", ", dbCols)})"); continue; }
            if (csvHit == null) { issues.Add($"CSV column “{csv}” not found (the file has: {string.Join(", ", csvCols)})"); continue; }
            cols.Add((dbHit, csvHit));
        }
        if (cols.Count == 0) return (false, "No column could be matched. " + string.Join("; ", issues));

        // 5. pair rows by key column, or by order
        string key = (tc.KeyColumn ?? "").Trim();
        var keyCol = cols.FirstOrDefault(c => c.Db.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (key.Length > 0 && keyCol.Db == null) return (false, $"Key column “{key}” must be one of the compared DB columns");
        var pairs = new List<(string label, Dictionary<string, string>? csv, Dictionary<string, string>? db)>();
        if (key.Length > 0)
        {
            var byKey = new Dictionary<string, Dictionary<string, string>>();
            foreach (var r in csvRows)
                if (!byKey.TryAdd(DbExcelComparer.Norm(r[keyCol.Csv]), r)) issues.Add($"{key}={r[keyCol.Csv]}: appears more than once in the CSV");
            var seen = new HashSet<string>();
            foreach (var d in dbRows) { var k = DbExcelComparer.Norm(d[keyCol.Db]); seen.Add(k); pairs.Add(($"{key}={d[keyCol.Db]}", byKey.GetValueOrDefault(k), d)); }
            foreach (var (k, r) in byKey) if (!seen.Contains(k)) pairs.Add(($"{key}={r[keyCol.Csv]}", r, null));
        }
        else
            for (int i = 0; i < Math.Max(csvRows.Count, dbRows.Count); i++)
                pairs.Add(($"Record {i + 1}", i < csvRows.Count ? csvRows[i] : null, i < dbRows.Count ? dbRows[i] : null));

        // 6. compare every column
        int matched = 0;
        foreach (var (label, csv, db) in pairs)
        {
            if (csv == null) { issues.Add($"{label}: in database but not in CSV"); continue; }
            if (db == null) { issues.Add($"{label}: in CSV but not in database"); continue; }
            bool rowOk = true;
            foreach (var (dbc, csvc) in cols)
            {
                var (same, _) = DbExcelComparer.Same(db[dbc], csv[csvc], tc.CompareMode, tc.Tolerance);
                if (!same) { rowOk = false; issues.Add($"{label}: {dbc} CSV “{csv[csvc]}” ≠ DB “{db[dbc]}”"); }
            }
            if (rowOk) matched++;
        }
        string summary = $"{Path.GetFileName(file)}: {csvRows.Count} CSV record(s), {dbRows.Count} DB row(s), {cols.Count} column(s) compared, {matched} record(s) fully match";
        return (issues.Count == 0, issues.Count == 0 ? summary : $"{summary}. {issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    /// <summary>One pair per line (or separated by ;):  DbColumn=CsvHeader.  Blank = same names.</summary>
    static (List<(string Db, string Csv)>, string?) ParseMap(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim(); if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            if (eq < 1 || eq == t.Length - 1) return (list, $"ColumnMap line “{t}”: use DbColumn=CsvHeader");
            list.Add((t[..eq].Trim(), t[(eq + 1)..].Trim()));
        }
        return (list, null);
    }
}

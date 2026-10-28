using System.Data.Common;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace TestFlow;

/// <summary>Runs a SQL query and compares EVERY returned column with the same-named column in an Excel file, row by row.</summary>
public static class DbExcelComparer
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        if (string.IsNullOrWhiteSpace(tc.DbConnectionString)) return (false, "DbConnectionString is empty");
        if (string.IsNullOrWhiteSpace(tc.DbQuery)) return (false, "DbQuery is empty");
        var file = Abs(tc.ExpectedFile);
        if (!File.Exists(file)) return (false, "Excel file not found: " + file);

        // 1. Excel rows
        List<string> xlCols; List<Dictionary<string, string>> xlRows;
        try { (xlCols, xlRows) = ReadExcel(file, tc.ExpectedSheet); }
        catch (Exception ex) { return (false, "Cannot read Excel: " + ex.Message); }

        // 2. Database rows (all columns)
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
        if (dbRows.Count == 0 && xlRows.Count == 0) return (false, "Both the query and the Excel file have no rows");

        var issues = new List<string>();
        var ignore = new HashSet<string>((tc.IgnoreColumns ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        var cols = dbCols.Where(c => !ignore.Contains(c)).ToList();
        foreach (var c in cols.Where(c => !xlCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList())
        { issues.Add($"Column “{c}” is in the database result but not in the Excel file"); cols.Remove(c); }

        string key = (tc.KeyColumn ?? "").Trim();
        if (key.Length > 0 && !dbCols.Contains(key, StringComparer.OrdinalIgnoreCase)) return (false, $"Key column “{key}” is not in the query result");
        if (key.Length > 0 && !xlCols.Contains(key, StringComparer.OrdinalIgnoreCase)) return (false, $"Key column “{key}” is not in the Excel file");

        // 3. Pair rows: by key column, or by row order when no key is given
        var pairs = new List<(string label, Dictionary<string, string>? db, Dictionary<string, string>? xl)>();
        if (key.Length > 0)
        {
            var xlByKey = new Dictionary<string, Dictionary<string, string>>();
            foreach (var r in xlRows)
            {
                var k = Norm(r[key]);
                if (!xlByKey.TryAdd(k, r)) issues.Add($"{key}={r[key]}: appears more than once in Excel");
            }
            var seen = new HashSet<string>();
            foreach (var d in dbRows)
            {
                var k = Norm(d[key]); seen.Add(k);
                pairs.Add(($"{key}={d[key]}", d, xlByKey.GetValueOrDefault(k)));
            }
            foreach (var (k, r) in xlByKey) if (!seen.Contains(k)) pairs.Add(($"{key}={r[key]}", null, r));
        }
        else
        {
            for (int i = 0; i < Math.Max(dbRows.Count, xlRows.Count); i++)
                pairs.Add(($"Row {i + 1}", i < dbRows.Count ? dbRows[i] : null, i < xlRows.Count ? xlRows[i] : null));
        }

        // 4. Compare every column
        int matched = 0;
        foreach (var (label, db, xl) in pairs)
        {
            if (db == null) { issues.Add($"{label}: in Excel but not in database"); continue; }
            if (xl == null) { issues.Add($"{label}: in database but not in Excel"); continue; }
            bool rowOk = true;
            foreach (var c in cols)
            {
                var (same, how) = Same(db[c], xl[c], tc.CompareMode, tc.Tolerance);
                if (!same) { rowOk = false; issues.Add($"{label}: {c} DB “{db[c]}” ≠ Excel “{xl[c]}”"); }
            }
            if (rowOk) matched++;
        }

        string summary = $"{dbRows.Count} DB row(s), {xlRows.Count} Excel row(s), {cols.Count} column(s) compared, {matched} row(s) fully match";
        return (issues.Count == 0, issues.Count == 0 ? summary : $"{summary}. {issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    internal static (List<string> cols, List<Dictionary<string, string>> rows) ReadExcel(string path, string sheet)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = !string.IsNullOrWhiteSpace(sheet) && wb.Worksheets.Contains(sheet.Trim()) ? wb.Worksheet(sheet.Trim()) : wb.Worksheets.First();
        var header = ws.FirstRowUsed() ?? throw new InvalidDataException("Sheet is empty");
        var heads = header.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString().Trim());
        var rows = new List<Dictionary<string, string>>();
        foreach (var r in ws.RowsUsed().Skip(1))
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (col, name) in heads) d[name] = CellText(r.Cell(col));
            if (d.Values.Any(v => v.Length > 0)) rows.Add(d);
        }
        return (heads.Values.ToList(), rows);
    }

    static string CellText(IXLCell c)
    {
        if (c.IsEmpty()) return "";
        return c.DataType switch
        {
            XLDataType.DateTime => c.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            XLDataType.Number => c.GetDouble().ToString("G15", CultureInfo.InvariantCulture),
            _ => c.GetString().Trim()
        };
    }

    internal static string Norm(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d.ToString("G29", CultureInfo.InvariantCulture) : s.Trim().ToLowerInvariant();

    /// <summary>Numbers are compared as numbers (with tolerance), dates as dates, everything else as text.</summary>
    internal static (bool, string) Same(string db, string xl, string mode, string tol)
    {
        db = db.Trim(); xl = xl.Trim();
        if (db.Length == 0 && xl.Length == 0) return (true, "empty");
        var m = (mode ?? "").Trim().ToLowerInvariant();
        if (m != "text" && m != "textignorecase" && decimal.TryParse(db, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(xl, NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
        {
            decimal t = decimal.TryParse(tol, NumberStyles.Any, CultureInfo.InvariantCulture, out var tt) ? Math.Abs(tt) : 0;
            return (Math.Abs(a - b) <= t, "numeric");
        }
        if (m != "text" && m != "textignorecase" && db.Contains('-') && xl.Contains('-') &&
            DateTime.TryParse(db, CultureInfo.InvariantCulture, DateTimeStyles.None, out var da) && DateTime.TryParse(xl, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dx))
            return ((da.TimeOfDay == TimeSpan.Zero || dx.TimeOfDay == TimeSpan.Zero) ? da.Date == dx.Date : da == dx, "date");
        return (string.Equals(db, xl, m is "textignorecase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "text");
    }
}

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
        var (dbCs, dbErr) = AppConfig.GetDb(); if (dbErr != null) return (false, dbErr);
        if (string.IsNullOrWhiteSpace(tc.DbQuery)) return (false, "DbQuery is empty");
        // the Excel file: ExpectedFile, or (when ExpectedFile is blank) the newest generated file matching FileCheckFolder + FileNamePattern
        string file;
        if (string.IsNullOrWhiteSpace(tc.ExpectedFile) && !string.IsNullOrWhiteSpace(tc.FileCheckFolder))
        {
            var folder = Abs(tc.FileCheckFolder);
            var pattern = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*.xls*" : tc.FileNamePattern;
            string? found = null; var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
            while (true)
            {
                if (Directory.Exists(folder))
                    found = Directory.EnumerateFiles(folder, pattern).Where(f => !Path.GetFileName(f).StartsWith("~$")).OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
                if (found != null) break;
                if (DateTime.UtcNow >= deadline) return (false, $"Excel file not found: {pattern} in {folder}");
                await Task.Delay(500, ct);
            }
            file = found;
        }
        else
        {
            file = Abs(tc.ExpectedFile);
            if (!File.Exists(file)) return (false, "Excel file not found: " + file);
        }
        var (map, mapErr) = CsvDbComparer.ParseMap(tc.ColumnMap);
        if (mapErr != null) return (false, mapErr);

        // 1. Excel rows
        List<string> xlCols; List<Dictionary<string, string>> xlRows;
        try { (xlCols, xlRows) = ReadExcel(file, tc.ExpectedSheet, tc.HeaderRowNumber); }
        catch (Exception ex) { return (false, "Cannot read Excel: " + ex.Message); }

        // 2. Database rows (all columns)
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
        if (dbRows.Count == 0 && xlRows.Count == 0) return (false, "Both the query and the Excel file have no rows");

        var issues = new List<string>();
        var ignore = new HashSet<string>((tc.IgnoreColumns ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        // which DB column is compared with which Excel column: ColumnMap (DbColumn=ExcelHeader), blank = same names
        var want = map.Count > 0 ? map.Select(m => (Db: m.Db, Xl: m.Csv)).ToList() : dbCols.Select(c => (Db: c, Xl: c)).ToList();
        want = want.Where(w => !ignore.Contains(w.Db)).ToList();
        var cols = new List<(string Db, string Xl)>();
        foreach (var (db, xl) in want)
        {
            var dbHit = dbCols.FirstOrDefault(c => c.Equals(db, StringComparison.OrdinalIgnoreCase));
            var xlHit = xlCols.FirstOrDefault(c => c.Equals(xl, StringComparison.OrdinalIgnoreCase));
            if (dbHit == null) { issues.Add($"Column “{db}” is not in the query result (it returned: {string.Join(", ", dbCols)})"); continue; }
            if (xlHit == null)
            {
                issues.Add(map.Count > 0 ? $"Excel column “{xl}” (for {db}) not found (the file has: {string.Join(", ", xlCols)})"
                                         : $"Column “{db}” is in the database result but not in the Excel file");
                continue;
            }
            cols.Add((dbHit, xlHit));
        }
        if (cols.Count == 0) return (false, "No column could be matched. " + string.Join("; ", issues));

        string key = (tc.KeyColumn ?? "").Trim();
        (string Db, string Xl) keyCol = default;
        if (key.Length > 0)
        {
            keyCol = cols.FirstOrDefault(c => c.Db.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (keyCol.Db == null) return (false, $"Key column “{key}” must be one of the compared DB columns");
        }

        // 3. Pair rows: by key column, or by row order when no key is given
        var pairs = new List<(string label, Dictionary<string, string>? db, Dictionary<string, string>? xl)>();
        if (key.Length > 0)
        {
            var xlByKey = new Dictionary<string, Dictionary<string, string>>();
            foreach (var r in xlRows)
            {
                var k = Norm(r[keyCol.Xl]);
                if (!xlByKey.TryAdd(k, r)) issues.Add($"{key}={r[keyCol.Xl]}: appears more than once in Excel");
            }
            var seen = new HashSet<string>();
            foreach (var d in dbRows)
            {
                var k = Norm(d[keyCol.Db]); seen.Add(k);
                pairs.Add(($"{key}={d[keyCol.Db]}", d, xlByKey.GetValueOrDefault(k)));
            }
            foreach (var (k, r) in xlByKey) if (!seen.Contains(k)) pairs.Add(($"{key}={r[keyCol.Xl]}", null, r));
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
                var (same, how) = Same(db[c.Db], xl[c.Xl], tc.CompareMode, tc.Tolerance);
                if (!same) { rowOk = false; issues.Add($"{label}: {c.Db} DB “{db[c.Db]}” ≠ Excel {c.Xl} “{xl[c.Xl]}”"); }
            }
            if (rowOk) matched++;
        }

        string summary = $"{Path.GetFileName(file)}: {dbRows.Count} DB row(s), {xlRows.Count} Excel row(s), {cols.Count} column(s) compared, {matched} row(s) fully match";
        return (issues.Count == 0, issues.Count == 0 ? summary : $"{summary}. {issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    internal static (List<string> cols, List<Dictionary<string, string>> rows) ReadExcel(string path, string sheet, int headerRow = 0)
    {
        if (XlsReader.IsXls(path)) return XlsReader.Read(path, sheet, headerRow);   // Excel 97-2003 (.xls)
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = !string.IsNullOrWhiteSpace(sheet) && wb.Worksheets.Contains(sheet.Trim()) ? wb.Worksheet(sheet.Trim()) : wb.Worksheets.First();
        int maxR = ws.LastRowUsed()?.RowNumber() ?? 0, maxC = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (maxR == 0) throw new InvalidDataException("Sheet is empty");
        var grid = new List<string[]>();
        for (int r = 1; r <= maxR; r++)
        {
            var row = new string[maxC];
            for (int c = 1; c <= maxC; c++) row[c - 1] = CellText(ws.Cell(r, c));
            grid.Add(row);
        }
        return SheetGrid.ToRows(grid, headerRow);   // header row: HeaderRow, or found automatically (report title lines are skipped)
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
        if (m != "text")
        {
            // SQL bit / JSON boolean: "true" = "True" = 1
            static bool? B(string s, bool digits) { var x = s.Trim().ToLowerInvariant(); return x == "true" ? true : x == "false" ? false : digits && x == "1" ? true : digits && x == "0" ? false : null; }
            if ((B(db, false) != null && B(xl, true) != null) || (B(xl, false) != null && B(db, true) != null))
                return (B(db, true) == B(xl, true), "bool");
        }
        return (string.Equals(db, xl, m is "textignorecase" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "text");
    }
}

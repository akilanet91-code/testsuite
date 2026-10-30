using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>
/// Checks that a record is (or is not) in the database and, when ExpectedValues is filled in, that its columns hold the values
/// defined in the Excel test case. Used as a node, and as a check before / after the API call of an ApiDbCompare case.
/// </summary>
public static class DbRecordCheck
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct, JsonDocument? api = null)
    {
        if (string.IsNullOrWhiteSpace(tc.DbCheckQuery)) return (false, "DbCheckQuery is empty");
        var (cs, err) = AppConfig.GetDb();
        if (err != null) return (false, err);
        bool wantExists = !((tc.DbCheckExpect ?? "").Trim().ToLowerInvariant().Replace(" ", "") is "notexists" or "notexist" or "no" or "none");

        // {api:path} in the query = value from the API response (only when the check runs after the API call), sent as a SQL parameter
        var args = new List<string>(); string sql;
        try
        {
            sql = Regex.Replace(tc.DbCheckQuery, @"'\{api:([^}]+)\}'|\{api:([^}]+)\}", m =>
            {
                var path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (api == null || !ApiDbVerifier.TryGetPath(api.RootElement, path, out var v))
                    throw new InvalidOperationException($"Query placeholder “{m.Value}” needs the API response: set DbCheckWhen = After and use a path that exists in it");
                args.Add(v); return Db.Marker(args.Count - 1);
            });
        }
        catch (InvalidOperationException ex) { return (false, ex.Message); }

        var cols = new List<string>(); var rows = new List<Dictionary<string, string>>();
        long found = 0; bool usedCount = false;
        try
        {
            await using var con = ApiDbVerifier.CreateConnection(cs);
            await con.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
            for (int i = 0; i < args.Count; i++)
            {
                var p = cmd.CreateParameter(); p.ParameterName = Db.ParamName(i); p.Value = args[i]; cmd.Parameters.Add(p);
            }
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            for (int i = 0; i < rd.FieldCount; i++) cols.Add(rd.GetName(i));
            bool countQuery = rd.FieldCount == 1 && Regex.IsMatch(sql, @"\bcount\s*\(", RegexOptions.IgnoreCase);
            while (await rd.ReadAsync(ct))
            {
                // SELECT COUNT(*) always returns one row: use the number in it
                if (countQuery && !rd.IsDBNull(0) && long.TryParse(Convert.ToString(rd.GetValue(0), CultureInfo.InvariantCulture), out var n))
                { found = n; usedCount = true; break; }
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++) d[cols[i]] = rd.IsDBNull(i) ? "" : ApiDbVerifier.DbToString(rd.GetValue(i));
                rows.Add(d);
                if (rows.Count >= 1000) break;
            }
            if (!usedCount) found = rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Database check failed: " + ex.Message); }

        bool exists = found > 0;
        string foundText = exists ? $"record found ({found}{(found >= 1000 ? "+" : "")} row(s))" : "record not found";
        if (!wantExists) return (!exists, $"{foundText}; expected it NOT to exist");
        if (!exists) return (false, $"{foundText}; expected it to exist");

        // expected values defined in the Excel test case
        var expected = ParseExpected(tc.ExpectedValues, api);
        if (expected.Count == 0) return (true, $"{foundText}; expected it to exist");
        if (usedCount) return (false, "ExpectedValues needs a query that returns the record's columns, not SELECT COUNT(*)");

        // when the query returns several rows, one of them must match every expected value
        List<string>? best = null;
        foreach (var row in rows)
        {
            var diffs = CompareRow(row, expected, cols, tc);
            if (diffs.Count == 0) return (true, $"{foundText}; all {expected.Count} expected value(s) match");
            if (best == null || diffs.Count < best.Count) best = diffs;
        }
        return (false, $"{foundText}; {best!.Count} value(s) differ: " + string.Join("; ", best.Take(10)) + (best.Count > 10 ? " …" : ""));
    }

    /// <summary>One COLUMN=value pair per line. {api:path} in a value is replaced by the value from the API response.</summary>
    static List<(string col, string value)> ParseExpected(string? text, JsonDocument? api)
    {
        var list = new List<(string col, string value)>();
        foreach (var line in (text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim(); int eq = t.IndexOf('=');
            if (eq < 1) continue;
            string value = t[(eq + 1)..].Trim();
            value = Regex.Replace(value, @"\{api:([^}]+)\}", m => api != null && ApiDbVerifier.TryGetPath(api.RootElement, m.Groups[1].Value, out var v) ? v : m.Value);
            list.Add((t[..eq].Trim(), value));
        }
        return list;
    }

    /// <summary>Special values: {notnull} must have a value, {null} must be empty, {today} must be today's date, {any} is not checked.</summary>
    static List<string> CompareRow(Dictionary<string, string> row, List<(string col, string value)> expected, List<string> cols, TestCase tc)
    {
        var diffs = new List<string>();
        foreach (var (col, exp) in expected)
        {
            if (!row.TryGetValue(col, out var db)) { diffs.Add($"column “{col}” is not in the query result (columns: {string.Join(", ", cols)})"); continue; }
            switch (exp.Trim().ToLowerInvariant())
            {
                case "{any}":
                    continue;
                case "{notnull}":
                    if (db.Trim().Length == 0) diffs.Add($"{col} is empty, expected a value");
                    continue;
                case "{null}":
                    if (db.Trim().Length != 0) diffs.Add($"{col} is “{db}”, expected empty / NULL");
                    continue;
                case "{today}":
                    if (!(DateTime.TryParse(db, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt.Date == DateTime.Today))
                        diffs.Add($"{col} is “{db}”, expected today’s date");
                    continue;
            }
            var (same, _) = DbExcelComparer.Same(db, exp.Trim(), tc.CompareMode, tc.Tolerance);
            if (!same) diffs.Add($"{col} DB “{db}” ≠ expected “{exp.Trim()}”");
        }
        return diffs;
    }
}

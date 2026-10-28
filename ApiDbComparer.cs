using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>Checks an API response against predefined data (HTTP status, response code, response message, message catalog)
/// and, when a query is given, compares many response fields with database columns (names can differ: ColumnMap).</summary>
public static class ApiDbComparer
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        var (status, body) = await Runner.SendApi(tc, ct);
        var issues = new List<string>(); var passed = new List<string>();

        // 1. HTTP status + body text
        int expectedStatus = tc.ExpectedStatusCode > 0 ? tc.ExpectedStatusCode : 200;
        if (status == expectedStatus) passed.Add($"HTTP {status}"); else issues.Add($"HTTP {status} (expected {expectedStatus})");
        if (!string.IsNullOrEmpty(tc.ExpectedResponseContains))
        {
            if (body.Contains(tc.ExpectedResponseContains)) passed.Add("body contains expected text");
            else issues.Add($"body does not contain “{tc.ExpectedResponseContains}”");
        }

        JsonDocument? doc = null;
        try { if (body.Trim().Length > 0) doc = JsonDocument.Parse(body); } catch (JsonException) { }
        using var _ = doc;

        // 2. Response code
        string? actualCode = null;
        if (!string.IsNullOrWhiteSpace(tc.ResponseCodePath))
        {
            if (doc != null && ApiDbVerifier.TryGetPath(doc.RootElement, tc.ResponseCodePath, out var c)) actualCode = c.Trim();
            else issues.Add($"Response code not found at “{tc.ResponseCodePath}”");
        }
        if (!string.IsNullOrWhiteSpace(tc.ExpectedResponseCode))
        {
            if (string.IsNullOrWhiteSpace(tc.ResponseCodePath)) issues.Add("ResponseCodePath is empty (where is the code in the response?)");
            else if (actualCode != null)
            {
                if (actualCode == tc.ExpectedResponseCode.Trim()) passed.Add($"code {actualCode}");
                else issues.Add($"response code “{actualCode}” (expected “{tc.ExpectedResponseCode.Trim()}”)");
            }
        }

        // 3. Response message: predefined text, or looked up in the catalog by response code
        string? expectedMsg = string.IsNullOrWhiteSpace(tc.ExpectedResponseMessage) ? null : tc.ExpectedResponseMessage.Trim();
        string source = "predefined";
        if (expectedMsg == null && !string.IsNullOrWhiteSpace(tc.ResponseCatalog))
        {
            var code = !string.IsNullOrWhiteSpace(tc.ExpectedResponseCode) ? tc.ExpectedResponseCode.Trim() : actualCode;
            if (code == null) issues.Add("ResponseCatalog needs ExpectedResponseCode or ResponseCodePath to find the message");
            else
            {
                var file = Abs(tc.ResponseCatalog); source = "catalog";
                if (!File.Exists(file)) issues.Add("ResponseCatalog file not found: " + file);
                else
                {
                    try
                    {
                        var (cols, rows) = DbExcelComparer.ReadExcel(file, "");
                        if (!cols.Contains("Code", StringComparer.OrdinalIgnoreCase) || !cols.Contains("Message", StringComparer.OrdinalIgnoreCase))
                            issues.Add("ResponseCatalog must have the columns Code and Message");
                        else
                        {
                            var row = rows.FirstOrDefault(r => r["Code"].Trim().Equals(code, StringComparison.OrdinalIgnoreCase));
                            if (row == null) issues.Add($"Code “{code}” is not in the ResponseCatalog");
                            else expectedMsg = row["Message"].Trim();
                        }
                    }
                    catch (Exception ex) { issues.Add("Cannot read ResponseCatalog: " + ex.Message); }
                }
            }
        }
        if (expectedMsg != null)
        {
            string? actualMsg = null;
            if (string.IsNullOrWhiteSpace(tc.ResponseMessagePath)) actualMsg = body.Trim();          // no path = the whole body is the message
            else if (doc != null && ApiDbVerifier.TryGetPath(doc.RootElement, tc.ResponseMessagePath, out var m)) actualMsg = m.Trim();
            else issues.Add($"Response message not found at “{tc.ResponseMessagePath}”");
            if (actualMsg != null)
            {
                var mode = (tc.MessageMatch ?? "").Trim().ToLowerInvariant();
                bool same = mode switch
                {
                    "contains" => actualMsg.Contains(expectedMsg, StringComparison.OrdinalIgnoreCase),
                    "equalsignorecase" => actualMsg.Equals(expectedMsg, StringComparison.OrdinalIgnoreCase),
                    _ => actualMsg == expectedMsg
                };
                if (same) passed.Add($"message “{actualMsg}” ({source})");
                else issues.Add($"response message “{actualMsg}” (expected {(mode == "contains" ? "to contain " : "")}“{expectedMsg}” from {source})");
            }
        }

        // 4. Database (optional)
        if (!string.IsNullOrWhiteSpace(tc.DbQuery))
        {
            try { var s = await CompareDbAsync(tc, doc, issues, ct); if (s != null) passed.Add(s); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add("Database check failed: " + ex.Message); }
        }

        string summary = string.Join(" | ", passed);
        return (issues.Count == 0, issues.Count == 0 ? summary : (summary.Length > 0 ? summary + ". " : "") + $"{issues.Count} issue(s): " + string.Join("; ", issues.Take(10)) + (issues.Count > 10 ? " …" : ""));
    }

    static async Task<string?> CompareDbAsync(TestCase tc, JsonDocument? doc, List<string> issues, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tc.DbConnectionString)) { issues.Add("DbConnectionString is empty"); return null; }
        bool noRows = (tc.ExpectNoDbRows ?? "").Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

        // {api:path} in the query = value from the response root, sent as a SQL parameter
        var args = new List<string>(); string sql;
        try
        {
            sql = Regex.Replace(tc.DbQuery, @"\{api:([^}]+)\}", m =>
            {
                if (doc == null || !ApiDbVerifier.TryGetPath(doc.RootElement, m.Groups[1].Value, out var v)) throw new InvalidOperationException($"Query placeholder “{m.Value}” not found in API response");
                args.Add(v); return Db.Marker(tc.DbProvider, args.Count - 1);
            });
        }
        catch (InvalidOperationException ex) { issues.Add(ex.Message); return null; }

        var dbCols = new List<string>(); var dbRows = new List<Dictionary<string, string>>();
        await using (var con = ApiDbVerifier.CreateConnection(tc.DbProvider, tc.DbConnectionString))
        {
            await con.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = sql; cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
            for (int i = 0; i < args.Count; i++)
            {
                var p = cmd.CreateParameter(); p.ParameterName = Db.ParamName(tc.DbProvider, i); p.Value = args[i]; cmd.Parameters.Add(p);
            }
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            for (int i = 0; i < rd.FieldCount; i++) dbCols.Add(rd.GetName(i));
            while (await rd.ReadAsync(ct))
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++) d[dbCols[i]] = rd.IsDBNull(i) ? "" : ApiDbVerifier.DbToString(rd.GetValue(i));
                dbRows.Add(d);
            }
        }

        // error case: nothing must have been written
        if (noRows)
        {
            if (dbRows.Count == 0) return "DB: no matching rows (as expected)";
            issues.Add($"DB query returned {dbRows.Count} row(s), expected none"); return null;
        }

        // records = the list in the response (or the single object)
        if (doc == null) { issues.Add("API response is not JSON, cannot compare it with the database"); return null; }
        var recs = new List<JsonElement>(); JsonElement node = doc.RootElement;
        if (!string.IsNullOrWhiteSpace(tc.ApiRecordsPath) && !ApiDbVerifier.TryGetElement(doc.RootElement, tc.ApiRecordsPath, out node))
        { issues.Add($"ApiRecordsPath “{tc.ApiRecordsPath}” not found in API response"); return null; }
        if (node.ValueKind == JsonValueKind.Array) recs.AddRange(node.EnumerateArray());
        else if (node.ValueKind == JsonValueKind.Object) recs.Add(node);
        else { issues.Add("ApiRecordsPath must point to an object or an array"); return null; }
        if (dbRows.Count == 0 && recs.Count == 0) { issues.Add("Both the query and the API response have no rows"); return null; }

        // column map: DbColumn=api.path (one per line, or separated by ; or ,). No map = same names.
        var map = new List<(string db, string api)>();
        foreach (var part in (tc.ColumnMap ?? "").Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim(); if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            map.Add(eq > 0 ? (t[..eq].Trim(), t[(eq + 1)..].Trim()) : (t, t));
        }
        if (map.Count == 0) map = dbCols.Select(c => (c, c)).ToList();
        foreach (var (dc, _) in map.ToList())
            if (!dbCols.Contains(dc, StringComparer.OrdinalIgnoreCase)) { issues.Add($"Column “{dc}” is not in the query result"); map.RemoveAll(x => x.db.Equals(dc, StringComparison.OrdinalIgnoreCase)); }
        if (map.Count == 0) { issues.Add("No mapped column is in the query result"); return null; }

        var apiRows = recs.Select(r =>
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dc, ap) in map) d[dc] = ApiDbVerifier.TryGetPath(r, ap, out var v) ? v : null;
            return d;
        }).ToList();

        // pair rows by key (DB key column) or by order
        string key = (tc.KeyColumn ?? "").Trim();
        if (key.Length > 0 && !map.Any(x => x.db.Equals(key, StringComparison.OrdinalIgnoreCase)))
        { issues.Add($"Key column “{key}” must be in the query result and in ColumnMap"); return null; }
        var pairs = new List<(string label, Dictionary<string, string>? db, Dictionary<string, string?>? api)>();
        if (key.Length > 0)
        {
            var byKey = new Dictionary<string, Dictionary<string, string?>>();
            foreach (var a in apiRows)
                if (!byKey.TryAdd(DbExcelComparer.Norm(a[key] ?? ""), a)) issues.Add($"{key}={a[key]}: appears more than once in the API response");
            var seen = new HashSet<string>();
            foreach (var d in dbRows) { var k = DbExcelComparer.Norm(d[key]); seen.Add(k); pairs.Add(($"{key}={d[key]}", d, byKey.GetValueOrDefault(k))); }
            foreach (var (k, a) in byKey) if (!seen.Contains(k)) pairs.Add(($"{key}={a[key]}", null, a));
        }
        else
            for (int i = 0; i < Math.Max(dbRows.Count, apiRows.Count); i++)
                pairs.Add(($"Row {i + 1}", i < dbRows.Count ? dbRows[i] : null, i < apiRows.Count ? apiRows[i] : null));

        int matched = 0;
        foreach (var (label, db, api) in pairs)
        {
            if (db == null) { issues.Add($"{label}: in API response but not in database"); continue; }
            if (api == null) { issues.Add($"{label}: in database but not in API response"); continue; }
            bool rowOk = true;
            foreach (var (dc, ap) in map)
            {
                if (api[dc] == null) { rowOk = false; issues.Add($"{label}: API field “{ap}” not found"); continue; }
                var (same, _) = DbExcelComparer.Same(db[dc], api[dc]!, tc.CompareMode, tc.Tolerance);
                if (!same) { rowOk = false; issues.Add($"{label}: {dc} DB “{db[dc]}” ≠ API {ap} “{api[dc]}”"); }
            }
            if (rowOk) matched++;
        }
        return $"DB: {dbRows.Count} row(s) vs {apiRows.Count} API record(s), {map.Count} column(s), {matched} fully match";
    }
}

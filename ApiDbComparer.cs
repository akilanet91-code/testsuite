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
        var issues = new List<string>(); var passed = new List<string>();

        // 0. Pre-check (before the API call): the record must already be in the database (or must not be)
        bool checkAfter = (tc.DbCheckWhen ?? "").Trim().Equals("After", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(tc.DbCheckQuery) && !checkAfter)
        {
            var (preOk, preMsg) = await DbRecordCheck.RunAsync(tc, ct);
            if (!preOk) return (false, "DB pre-check failed, API not called: " + preMsg);
            passed.Add("DB pre-check: " + preMsg);
        }

        var (status, body) = await Runner.SendApi(tc, ct);

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
            else issues.Add($"Response code not found at “{tc.ResponseCodePath}”" + (doc != null ? $" ({ApiDbVerifier.Describe(doc.RootElement)})" : " (response is not JSON)"));
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

        // 3b. Record check after the API call: the record must be in the database with the values defined in the Excel row
        if (!string.IsNullOrWhiteSpace(tc.DbCheckQuery) && checkAfter)
        {
            var (postOk, postMsg) = await DbRecordCheck.RunAsync(tc, ct, doc);
            if (postOk) passed.Add("DB check: " + postMsg); else issues.Add("DB check: " + postMsg);
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
        var (dbCs, dbErr) = AppConfig.GetDb(); if (dbErr != null) { issues.Add(dbErr); return null; }
        bool noRows = (tc.ExpectNoDbRows ?? "").Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

        // {api:path} in the query = value from the response root, sent as a SQL parameter
        var args = new List<string>(); string sql;
        try
        {
            sql = Regex.Replace(tc.DbQuery, @"'\{api:([^}]+)\}'|\{api:([^}]+)\}", m =>
            {
                if (doc == null || !ApiDbVerifier.TryGetPath(doc.RootElement, (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value), out var v)) throw new InvalidOperationException($"Query placeholder “{m.Value}” not found in API response");
                args.Add(v); return Db.Marker(args.Count - 1);
            });
        }
        catch (InvalidOperationException ex) { issues.Add(ex.Message); return null; }

        var dbCols = new List<string>(); var dbRows = new List<Dictionary<string, string>>();
        await using (var con = ApiDbVerifier.CreateConnection(dbCs))
        {
            await con.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = sql; cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
            for (int i = 0; i < args.Count; i++)
            {
                var p = cmd.CreateParameter(); p.ParameterName = Db.ParamName(i); p.Value = args[i]; cmd.Parameters.Add(p);
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

        // ExpectedValues without DbCheckQuery: the DbQuery rows must hold the values typed in the Excel row.
        // One record = lines COLUMN=value. Several records: separate them with a blank line or a line of ---.
        // Each record must match a different DB row (found by KeyColumn when the record has it, else by its values).
        // ColumnMap = {skip} checks only these values, not the API fields.
        string? expectedMsg = null;
        if (!string.IsNullOrWhiteSpace(tc.ExpectedValues) && string.IsNullOrWhiteSpace(tc.DbCheckQuery))
        {
            var blocks = new List<List<(string col, string value)>>(); var cur = new List<string>();
            void Flush()
            {
                if (cur.Count > 0) { var e = DbRecordCheck.ParseExpected(string.Join("\n", cur), doc); if (e.Count > 0) blocks.Add(e); cur.Clear(); }
            }
            foreach (var line in tc.ExpectedValues.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.All(ch => ch == '-')) Flush(); else cur.Add(t);
            }
            Flush();

            if (blocks.Count > 0)
            {
                if (dbRows.Count == 0) issues.Add("ExpectedValues: DbQuery returned no row");
                else
                {
                    string keyDB = (tc.KeyColumn ?? "").Trim(); var used = new HashSet<int>(); int okCount = 0;
                    for (int b2 = 0; b2 < blocks.Count; b2++)
                    {
                        var exp = blocks[b2];
                        var kv = keyDB.Length > 0 ? exp.FirstOrDefault(x => x.col.Equals(keyDB, StringComparison.OrdinalIgnoreCase)) : default;
                        bool hasKey = kv.col != null;
                        string label = blocks.Count > 1 ? $"Expected row {b2 + 1}" + (hasKey ? $" ({keyDB}={kv.value})" : "") : "ExpectedValues";
                        int found = -1; List<string>? best = null;
                        for (int i = 0; i < dbRows.Count && found < 0; i++)
                        {
                            if (used.Contains(i)) continue;
                            if (hasKey && DbExcelComparer.Norm(dbRows[i].GetValueOrDefault(keyDB, "")) != DbExcelComparer.Norm(kv.value)) continue;
                            var diffs = DbRecordCheck.CompareRow(dbRows[i], exp, dbCols, tc);
                            if (diffs.Count == 0) found = i;
                            else if (best == null || diffs.Count < best.Count) best = diffs;
                        }
                        if (found >= 0) { used.Add(found); okCount++; }
                        else if (best != null) issues.Add($"{label}: {best.Count} value(s) differ: " + string.Join("; ", best.Take(10)) + (best.Count > 10 ? " …" : ""));
                        else issues.Add($"{label}: no unused DB row" + (hasKey ? $" with {keyDB}={kv.value}" : ""));
                    }
                    if (okCount == blocks.Count)
                        expectedMsg = blocks.Count == 1 ? $"ExpectedValues: all {blocks[0].Count} value(s) match"
                            : $"ExpectedValues: {okCount} expected row(s) match ({dbRows.Count} DB row(s))";
                }
                if ((tc.ColumnMap ?? "").Trim().Equals("{skip}", StringComparison.OrdinalIgnoreCase)) return expectedMsg;
            }
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
        // mapped DB columns that the query does not return are reported (with the real column names) and left out
        var queryColumns = string.Join(", ", dbCols);
        foreach (var entry in map.Where(pair => !dbCols.Contains(pair.db, StringComparer.OrdinalIgnoreCase)).ToList())
            issues.Add($"Column “{entry.db}” is not in the query result (query columns: {queryColumns})");
        map = map.Where(pair => dbCols.Contains(pair.db, StringComparer.OrdinalIgnoreCase)).ToList();
        if (map.Count == 0) { issues.Add("No mapped column is in the query result"); return null; }

        var apiRows = recs.Select(r =>
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dc, ap) in map) d[dc] = ApiDbVerifier.TryGetPath(r, ap, out var v, deep: false) ? v : null;
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
        return $"DB: {dbRows.Count} row(s) vs {apiRows.Count} API record(s), {map.Count} column(s), {matched} fully match" + (expectedMsg != null ? " | " + expectedMsg : "");
    }
}

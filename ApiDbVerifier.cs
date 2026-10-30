using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>Calls an API, reads one value from the JSON response, reads one value from the database and compares them.</summary>
public static class ApiDbVerifier
{
    public static async Task<(bool ok, string msg)> RunAsync(TestCase tc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tc.ApiJsonPath)) return (false, "ApiJsonPath is empty (e.g. data.customer.balance)");
        var (dbCs, dbErr) = AppConfig.GetDb(); if (dbErr != null) return (false, dbErr);
        if (string.IsNullOrWhiteSpace(tc.DbQuery)) return (false, "DbQuery is empty");

        // 1. API call
        var (status, body) = await Runner.SendApi(tc, ct);
        int expected = tc.ExpectedStatusCode > 0 ? tc.ExpectedStatusCode : 200;
        if (status != expected) return (false, $"API returned HTTP {status} (expected {expected})");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return (false, "API response is not valid JSON"); }
        using (doc)
        {
            if (!TryGetPath(doc.RootElement, tc.ApiJsonPath, out var apiVal))
                return (false, $"Path “{tc.ApiJsonPath}” not found in API response ({Describe(doc.RootElement)})");

            // 2. Database query. {api:path} placeholders in the query become SQL parameters (safe from injection).
            var args = new List<string?>();
            string sql = Regex.Replace(tc.DbQuery, @"'\{api:([^}]+)\}'|\{api:([^}]+)\}", m =>
            {
                if (!TryGetPath(doc.RootElement, (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value), out var v)) throw new InvalidOperationException($"Query placeholder “{m.Value}” not found in API response");
                args.Add(v);
                return Db.Marker(args.Count - 1);
            });

            string? dbVal;
            await using (var con = CreateConnection(dbCs))
            {
                await con.OpenAsync(ct);
                await using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
                for (int i = 0; i < args.Count; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = Db.ParamName(i);
                    p.Value = (object?)args[i] ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return (false, "Database query returned no rows");
                int col = 0;
                if (!string.IsNullOrWhiteSpace(tc.DbColumn))
                {
                    try { col = rd.GetOrdinal(tc.DbColumn.Trim()); }
                    catch (IndexOutOfRangeException) { return (false, $"Column “{tc.DbColumn}” not in query result"); }
                }
                dbVal = rd.IsDBNull(col) ? null : DbToString(rd.GetValue(col));
            }

            // 3. Compare
            var (match, how) = Compare(apiVal, dbVal, tc.CompareMode, tc.Tolerance);
            return (match, $"API {tc.ApiJsonPath} = “{apiVal}” | DB = “{dbVal ?? "NULL"}” → {(match ? "match" : "MISMATCH")} ({how})");
        }
    }

    internal static DbConnection CreateConnection(string cs) => Db.Create(cs);

    internal static string DbToString(object v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",                 // SQL bit: "true"/"false" like JSON, not "True"/"False"
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    static (bool, string) Compare(string api, string? db, string mode, string tol)
    {
        db ??= "";
        switch (mode.Trim().ToLowerInvariant())
        {
            case "numeric":
            {
                if (!decimal.TryParse(api, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) ||
                    !decimal.TryParse(db, NumberStyles.Any, CultureInfo.InvariantCulture, out var b)) return (false, "numeric: value is not a number");
                decimal t = decimal.TryParse(tol, NumberStyles.Any, CultureInfo.InvariantCulture, out var tt) ? Math.Abs(tt) : 0;
                return (Math.Abs(a - b) <= t, $"numeric, tolerance {t}");
            }
            case "date":
            {
                if (!DateTime.TryParse(api, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ||
                    !DateTime.TryParse(db, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)) return (false, "date: value is not a date");
                return (a.Date == b.Date, "date only");
            }
            case "textignorecase": case "text (ignore case)":
                return (string.Equals(api.Trim(), db.Trim(), StringComparison.OrdinalIgnoreCase), "text, ignore case");
            default:
                return (api.Trim() == db.Trim(), "text, exact");
        }
    }

    /// <summary>
    /// Reads a value from JSON with a path. Property names are case-insensitive. Supported forms:
    ///   data.items[0].name   index            data.items[-1].name   last item
    ///   data.items.name      array without index = first item that has the field
    ///   data.items[].name    first item (also [*])
    ///   items[id=2].name     first item whose id is 2
    ///   name                 a bare field name that is not at the top is searched anywhere in the response (deep = true)
    ///   ..name               always search anywhere
    /// </summary>
    public static bool TryGetPath(JsonElement root, string path, out string value, bool deep = true)
    {
        value = "";
        if (!TryGetElement(root, path, out var cur, deep)) return false;
        value = Scalar(cur);
        return true;
    }

    static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText()
    };

    readonly record struct Tok(bool Bracket, string Text);
    static readonly Regex TokenRx = new(@"\[([^\]]*)\]|([^.\[\]]+)", RegexOptions.Compiled);

    public static bool TryGetElement(JsonElement root, string path, out JsonElement cur, bool deep = true)
    {
        cur = root;
        var p = (path ?? "").Trim().TrimStart('$');
        bool forceDeep = p.StartsWith("..");
        p = p.TrimStart('.');
        var toks = TokenRx.Matches(p)
            .Select(m => m.Groups[2].Success ? new Tok(false, m.Groups[2].Value.Trim()) : new Tok(true, m.Groups[1].Value.Trim()))
            .ToList();
        if (toks.Count == 0) return true;
        if (!forceDeep && Walk(root, toks, 0, out cur)) return true;
        if ((deep || forceDeep) && !toks[0].Bracket) return DeepFind(root, toks, out cur);
        cur = root;
        return false;
    }

    static bool TryProp(JsonElement o, string key, out JsonElement v)
    {
        foreach (var p in o.EnumerateObject())
            if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        v = default;
        return false;
    }

    /// <summary>Follows the path from cur. An array met at a field name is searched item by item, so "items.name" works without an index.</summary>
    static bool Walk(JsonElement cur, List<Tok> t, int i, out JsonElement res)
    {
        res = cur;
        if (i == t.Count) return true;
        bool bracket = t[i].Bracket; string key = t[i].Text;
        if (bracket)
        {
            if (cur.ValueKind == JsonValueKind.Array)
            {
                if (key.Length == 0 || key == "*")
                {
                    foreach (var e in cur.EnumerateArray()) if (Walk(e, t, i + 1, out res)) return true;
                    return false;
                }
                if (int.TryParse(key, out var idx))
                {
                    int n = cur.GetArrayLength(); if (idx < 0) idx += n;
                    return idx >= 0 && idx < n && Walk(cur[idx], t, i + 1, out res);
                }
                int eq = key.IndexOf('=');
                if (eq > 0)
                {
                    string name = key[..eq].Trim(), val = key[(eq + 1)..].Trim().Trim('\'', '"');
                    foreach (var e in cur.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Object && TryProp(e, name, out var pv) && Scalar(pv).Equals(val, StringComparison.OrdinalIgnoreCase))
                            return Walk(e, t, i + 1, out res);
                }
                return false;
            }
            // a single object where a list was expected: [], [*] and [0] mean "this object"
            return (key.Length == 0 || key == "*" || key == "0") && Walk(cur, t, i + 1, out res);
        }
        if (cur.ValueKind == JsonValueKind.Array)
        {
            if (int.TryParse(key, out var idx2)) return idx2 >= 0 && idx2 < cur.GetArrayLength() && Walk(cur[idx2], t, i + 1, out res);
            foreach (var e in cur.EnumerateArray()) if (Walk(e, t, i, out res)) return true;
            return false;
        }
        if (cur.ValueKind == JsonValueKind.Object && TryProp(cur, key, out var v)) return Walk(v, t, i + 1, out res);
        return false;
    }

    /// <summary>Finds the first field with the name of the first path part anywhere in the JSON, then follows the rest of the path from there.</summary>
    static bool DeepFind(JsonElement node, List<Tok> t, out JsonElement res)
    {
        res = node;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (TryProp(node, t[0].Text, out var v) && Walk(v, t, 1, out res)) return true;
            foreach (var p in node.EnumerateObject()) if (DeepFind(p.Value, t, out res)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in node.EnumerateArray()) if (DeepFind(e, t, out res)) return true;
        }
        return false;
    }

    /// <summary>Short hint for "not found" messages: the field names at the top of the response.</summary>
    public static string Describe(JsonElement root)
    {
        var el = root; string prefix = "";
        if (el.ValueKind == JsonValueKind.Array)
        {
            prefix = $"response is a list of {el.GetArrayLength()}; ";
            if (el.GetArrayLength() == 0) return prefix.TrimEnd(' ', ';');
            el = el[0];
        }
        if (el.ValueKind != JsonValueKind.Object) return prefix.TrimEnd(' ', ';');
        var names = el.EnumerateObject().Select(p => p.Name).Take(12).ToList();
        return $"{prefix}top-level fields: {string.Join(", ", names)}";
    }
}

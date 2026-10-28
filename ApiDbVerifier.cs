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
        if (string.IsNullOrWhiteSpace(tc.DbConnectionString)) return (false, "DbConnectionString is empty");
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
                return (false, $"Path “{tc.ApiJsonPath}” not found in API response");

            // 2. Database query. {api:path} placeholders in the query become SQL parameters (safe from injection).
            var args = new List<string?>();
            string sql = Regex.Replace(tc.DbQuery, @"\{api:([^}]+)\}", m =>
            {
                if (!TryGetPath(doc.RootElement, m.Groups[1].Value, out var v)) throw new InvalidOperationException($"Query placeholder “{m.Value}” not found in API response");
                args.Add(v);
                return Db.Marker(tc.DbProvider, args.Count - 1);
            });

            string? dbVal;
            await using (var con = CreateConnection(tc.DbProvider, tc.DbConnectionString))
            {
                await con.OpenAsync(ct);
                await using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = Math.Max(5, tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 60);
                for (int i = 0; i < args.Count; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = Db.ParamName(tc.DbProvider, i);
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

    internal static DbConnection CreateConnection(string provider, string cs) => Db.Create(provider, cs);

    internal static string DbToString(object v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
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

    /// <summary>Reads a value with a dot path like data.items[0].name (property names are case-insensitive).</summary>
    public static bool TryGetPath(JsonElement root, string path, out string value)
    {
        value = "";
        if (!TryGetElement(root, path, out var cur)) return false;
        value = cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => cur.GetRawText()
        };
        return true;
    }

    public static bool TryGetElement(JsonElement root, string path, out JsonElement cur)
    {
        cur = root;
        foreach (Match m in Regex.Matches(path.Trim().TrimStart('$'), @"[^.\[\]]+"))
        {
            var key = m.Value.Trim();
            if (cur.ValueKind == JsonValueKind.Array && int.TryParse(key, out var idx))
            {
                if (idx < 0 || idx >= cur.GetArrayLength()) return false;
                cur = cur[idx];
            }
            else if (cur.ValueKind == JsonValueKind.Object)
            {
                bool found = false;
                foreach (var p in cur.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) { cur = p.Value; found = true; break; }
                if (!found) return false;
            }
            else return false;
        }
        return true;
    }
}

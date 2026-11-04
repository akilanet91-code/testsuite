using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text;

namespace TestFlow;

/// <summary>
/// Stub service, "AS400" format: writes a fixed-record file from a SPEC workbook (sheet "Mapping", the same one ExcelVerify uses).
/// Data comes from (in this order) the database SELECT (SourceColumn = DB column), the spec's Default / ExpectedValue, the spec's Generator, or a value made from DataType.
///
/// Spec columns:  TargetColumn, SourceColumn, DataType (text|int|decimal|date|bool), Start, Length            (same as the verifier)
/// Optional:      Decimals, Format (date format), Align (Left|Right), Pad (fill char), Storage (Display|Zoned|Packed), Default, Generator
/// </summary>
public static class As400StubWriter
{
    public static readonly string[] Encodings = { "ASCII", "UTF-8", "EBCDIC-037", "EBCDIC-500", "EBCDIC-1140" };
    public static readonly string[] Terminators = { "CRLF", "LF", "None (fixed records)" };

    static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
    static string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v.Trim() : "";

    sealed class Field
    {
        public string Target = "", Source = "", Type = "text", Format = "", Align = "", Pad = "", Storage = "", Default = "", Generator = "";
        public int Start, Length, Decimals = -1; public bool Required;
    }

    public static Encoding GetEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return (name ?? "").ToUpperInvariant() switch
        {
            "UTF-8" => new UTF8Encoding(false),
            "EBCDIC-037" => Encoding.GetEncoding(37),
            "EBCDIC-500" => Encoding.GetEncoding(500),
            "EBCDIC-1140" => Encoding.GetEncoding(1140),
            _ => Encoding.Latin1
        };
    }

    public static async Task<string> RunAsync(StubConfig c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.FileName)) throw new InvalidOperationException("Stub: file name is empty");
        if (string.IsNullOrWhiteSpace(c.SpecFile)) throw new InvalidOperationException("Stub (AS400): choose the spec file (sheet “Mapping” with TargetColumn, SourceColumn, DataType, Start, Length)");
        string specPath = Abs(RunContext.Resolve(c.SpecFile));
        if (!File.Exists(specPath)) throw new InvalidOperationException($"Stub (AS400): spec file not found: {specPath}");

        var fields = LoadSpec(specPath, string.IsNullOrWhiteSpace(c.SpecSheet) ? "Mapping" : c.SpecSheet.Trim());
        int recLen = Math.Max(c.RecordLength, fields.Max(f => f.Start + f.Length - 1));
        var enc = GetEncoding(c.Encoding);
        var rnd = int.TryParse(c.Seed, out var seed) ? new Random(seed) : new Random();

        // ----- data rows: DB select (mapped by SourceColumn) or generated -----
        List<Dictionary<string, object?>>? dbRows = null;
        if (!string.IsNullOrWhiteSpace(c.DbQuery)) dbRows = await ReadDb(RunContext.Resolve(c.DbQuery, sql: true), c.MaxRows, fields, ct);
        int count = dbRows?.Count ?? Math.Max(0, c.Rows);
        if (c.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(c.DelaySeconds), ct);

        var records = new List<byte[]>(count);
        var sums = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        for (int r = 1; r <= count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var rec = Blank(recLen, enc);
            foreach (var f in fields)
            {
                object? v = Value(f, dbRows?[r - 1], r, rnd);
                try { Put(rec, f, v, enc); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"Stub (AS400): row {r}, column “{f.Target}”: {ex.Message}"); }
                if (f.Type is "int" or "decimal" && TryDecimal(v, out var d)) sums[f.Target] = sums.GetValueOrDefault(f.Target) + d;
            }
            records.Add(rec);
        }

        // ----- file = [header] + records + [trailer] -----
        var now = DateTime.Now;
        var all = new List<byte[]>();
        if (!string.IsNullOrWhiteSpace(c.HeaderRecord)) all.Add(Special(c.HeaderRecord, recLen, now, count, sums, enc));
        all.AddRange(records);
        if (!string.IsNullOrWhiteSpace(c.TrailerRecord)) all.Add(Special(c.TrailerRecord, recLen, now, count, sums, enc));

        string dir = Abs(RunContext.Resolve(c.Folder)); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, StubFileService.ExpandName(RunContext.Resolve(c.FileName)));
        byte[] eol = (c.RecordTerminator ?? "") switch
        {
            "LF" => enc.GetBytes("\n"),
            var t when t.StartsWith("None") => Array.Empty<byte>(),
            _ => enc.GetBytes("\r\n")
        };
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            foreach (var rec in all) { fs.Write(rec); fs.Write(eol); }
        return path;
    }

    // ---------- spec ----------
    static List<Field> LoadSpec(string path, string sheet)
    {
        var rows = ExcelIO.ReadRows(path, sheet);
        var list = new List<Field>(); int next = 1;
        foreach (var r in rows)
        {
            var f = new Field { Target = G(r, "TargetColumn") };
            if (f.Target == "") continue;
            f.Source = G(r, "SourceColumn"); f.Format = G(r, "Format"); f.Align = G(r, "Align").ToLowerInvariant();
            f.Pad = G(r, "Pad"); f.Storage = G(r, "Storage").ToLowerInvariant(); f.Default = G(r, "Default"); f.Generator = G(r, "Generator");
            f.Required = G(r, "Required").ToLowerInvariant() is "yes" or "y" or "true" or "1";
            f.Type = G(r, "DataType").ToLowerInvariant() switch
            {
                "int" or "integer" or "long" => "int", "decimal" or "number" or "numeric" => "decimal",
                "date" or "datetime" => "date", "bool" or "boolean" => "bool", _ => "text"
            };
            if (!int.TryParse(G(r, "Length"), out f.Length) || f.Length <= 0) throw new InvalidOperationException($"Stub (AS400): spec column “{f.Target}” has no numeric Length");
            f.Start = int.TryParse(G(r, "Start"), out var s) && s > 0 ? s : next;   // no Start = directly after the previous column
            if (int.TryParse(G(r, "Decimals"), out var dc)) f.Decimals = dc;
            next = f.Start + f.Length;
            list.Add(f);
        }
        if (list.Count == 0) throw new InvalidOperationException($"Stub (AS400): sheet “{sheet}” of the spec has no TargetColumn rows");
        return list;
    }

    // ---------- data ----------
    static async Task<List<Dictionary<string, object?>>> ReadDb(string sql, int max, List<Field> fields, CancellationToken ct)
    {
        var (cs, err) = AppConfig.GetDb();
        if (err != null) throw new InvalidOperationException("Stub (AS400): " + err);
        var rows = new List<Dictionary<string, object?>>();
        await using DbConnection conn = Db.Create(cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 120;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToList();
        // a mapped column that the SELECT does not return is a mistake worth reporting (columns without SourceColumn may come from defaults)
        var missing = fields.Where(f => f.Source != "" && !cols.Contains(f.Source, StringComparer.OrdinalIgnoreCase)).Select(f => f.Source).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Stub (AS400): the SELECT does not return mapped column(s) {string.Join(", ", missing)}. Returned: {string.Join(", ", cols)}");
        while (await rd.ReadAsync(ct))
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cols.Count; i++) d[cols[i]] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            rows.Add(d);
            if (max > 0 && rows.Count >= max) break;
        }
        return rows;
    }

    static object? Value(Field f, Dictionary<string, object?>? db, int row, Random rnd)
    {
        if (db != null)
        {
            string key = f.Source != "" ? f.Source : f.Target;
            if (db.TryGetValue(key, out var v)) return v;   // DB NULL stays null -> written as blanks / zero
        }
        if (f.Default != "") return f.Default;
        if (f.Generator != "") return StubFileService.Generate(f.Generator, row, rnd);
        string kind = f.Type switch
        {
            "int" => "int:0-9999", "decimal" => $"decimal:0-9999:{Math.Max(0, f.Decimals < 0 ? 2 : f.Decimals)}",
            "date" => "date:2024-01-01..2024-12-31:yyyy-MM-dd", "bool" => "bool", _ => "text:" + Math.Min(f.Length, 10)
        };
        return StubFileService.Generate(kind, row, rnd);
    }

    // ---------- bytes ----------
    static byte[] Blank(int len, Encoding enc)
    {
        var sp = enc.GetBytes(" "); var b = new byte[len * sp.Length];
        for (int i = 0; i < len; i++) Array.Copy(sp, 0, b, i * sp.Length, sp.Length);
        return b;
    }

    static bool TryDecimal(object? v, out decimal d)
    {
        d = 0;
        if (v == null) return true;
        if (v is bool bo) { d = bo ? 1 : 0; return true; }
        if (v is IConvertible && v is not string && v is not DateTime) { try { d = Convert.ToDecimal(v, CultureInfo.InvariantCulture); return true; } catch { return false; } }
        var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "";
        return s == "" || decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
    }

    static void Put(byte[] rec, Field f, object? v, Encoding enc)
    {
        int cw = enc.GetBytes(" ").Length, off = (f.Start - 1) * cw;
        byte[] bytes;
        if (f.Storage == "packed") bytes = Packed(v, f);
        else if (f.Storage == "zoned") bytes = Zoned(v, f, enc);
        else bytes = enc.GetBytes(Display(v, f));
        int max = f.Length * cw;
        if (bytes.Length > max) bytes = bytes[..max];
        if (off + bytes.Length > rec.Length) throw new InvalidOperationException("column goes past the record length");
        Array.Copy(bytes, 0, rec, off, bytes.Length);
    }

    static string Display(object? v, Field f)
    {
        string s; bool numeric = f.Type is "int" or "decimal";
        if (v == null) s = "";
        else if (f.Type == "date")
        {
            string fmt = f.Format != "" ? f.Format : f.Length >= 10 ? "yyyy-MM-dd" : "yyyyMMdd";
            DateTime dt;
            if (v is DateTime d0) dt = d0;
            else if (!DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                  && !DateTime.TryParseExact(Convert.ToString(v, CultureInfo.InvariantCulture), new[] { "yyyyMMdd", "yyMMdd", "ddMMyyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                throw new InvalidOperationException($"“{v}” is not a date");
            s = dt.ToString(fmt, CultureInfo.InvariantCulture);
        }
        else if (numeric)
        {
            if (!TryDecimal(v, out var d)) throw new InvalidOperationException($"“{v}” is not a number");
            int dec = f.Type == "int" ? 0 : f.Decimals < 0 ? 2 : f.Decimals;
            s = Math.Round(d, dec, MidpointRounding.AwayFromZero).ToString("F" + dec, CultureInfo.InvariantCulture);
        }
        else if (v is bool b) s = b ? "Y" : "N";
        else s = Convert.ToString(v, CultureInfo.InvariantCulture)?.TrimEnd() ?? "";

        char pad = f.Pad.Length > 0 ? f.Pad[0] : ' ';
        bool right = f.Align == "right" || (f.Align == "" && numeric);
        if (numeric && s.Length > f.Length) throw new InvalidOperationException($"value {s} does not fit in {f.Length} characters");
        if (s.Length > f.Length) s = s[..f.Length];
        if (right && numeric && pad == '0' && s.StartsWith('-')) return "-" + s[1..].PadLeft(f.Length - 1, '0');
        return right ? s.PadLeft(f.Length, pad) : s.PadRight(f.Length, pad);
    }

    static (string digits, bool neg) Scaled(object? v, Field f)
    {
        if (!TryDecimal(v, out var d)) throw new InvalidOperationException($"“{v}” is not a number");
        int dec = f.Type == "int" ? 0 : f.Decimals < 0 ? 0 : f.Decimals;   // zoned / packed: implied decimal point, so Decimals = digits after the point
        var scaled = Math.Round(Math.Abs(d) * (decimal)Math.Pow(10, dec), 0, MidpointRounding.AwayFromZero);
        return (scaled.ToString("F0", CultureInfo.InvariantCulture), d < 0);
    }

    /// <summary>Zoned decimal: digits only (implied decimal point), zero filled; a negative sign is an overpunch on the last digit.</summary>
    static byte[] Zoned(object? v, Field f, Encoding enc)
    {
        var (digits, neg) = Scaled(v, f);
        if (digits.Length > f.Length) throw new InvalidOperationException($"value does not fit in {f.Length} zoned digits");
        digits = digits.PadLeft(f.Length, '0');
        var b = enc.GetBytes(digits);
        if (neg)
        {
            int d = digits[^1] - '0';
            if (enc.CodePage is 37 or 500 or 1140) b[^1] = (byte)(0xD0 | d);       // EBCDIC: D0-D9 = negative
            else b[^1] = (byte)"}JKLMNOPQR"[d];                                   // ASCII overpunch
        }
        return b;
    }

    /// <summary>Packed decimal (COMP-3): Length bytes = 2*Length-1 digits + sign nibble (C positive, D negative).</summary>
    static byte[] Packed(object? v, Field f)
    {
        var (digits, neg) = Scaled(v, f);
        int n = f.Length * 2 - 1;
        if (digits.Length > n) throw new InvalidOperationException($"value does not fit in {n} packed digits");
        digits = digits.PadLeft(n, '0') + (neg ? 'D' : 'C');
        var b = new byte[f.Length];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(Convert.ToInt32(digits.Substring(i * 2, 1), 16) << 4 | Convert.ToInt32(digits.Substring(i * 2 + 1, 1), 16));
        return b;
    }

    // ---------- header / trailer ----------
    /// <summary>Template from position 1, e.g.  HDR{date:yyyyMMdd}  or  TRL{count:6}{sum:Amount:12}  ({pad:5} = 5 spaces). Padded with spaces to the record length.</summary>
    static byte[] Special(string template, int recLen, DateTime now, int count, Dictionary<string, decimal> sums, Encoding enc)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(template, @"\{([a-zA-Z]+)(?::([^{}]*))?\}", m =>
        {
            string kind = m.Groups[1].Value.ToLowerInvariant(), arg = m.Groups[2].Value;
            switch (kind)
            {
                case "date": case "time": return now.ToString(arg == "" ? "yyyyMMdd" : arg, CultureInfo.InvariantCulture);
                case "count": return count.ToString(CultureInfo.InvariantCulture).PadLeft(int.TryParse(arg, out var w) ? w : 6, '0');
                case "pad": return new string(' ', int.TryParse(arg, out var p) ? p : 1);
                case "sum":
                {
                    var seg = arg.Split(':'); decimal t = sums.GetValueOrDefault(seg[0].Trim());
                    return Math.Round(t, 0).ToString("F0", CultureInfo.InvariantCulture).PadLeft(seg.Length > 1 && int.TryParse(seg[1], out var sw) ? sw : 12, '0');
                }
                default: return m.Value;
            }
        });
        var rec = Blank(recLen, enc); var b = enc.GetBytes(text);
        Array.Copy(b, rec, Math.Min(b.Length, rec.Length));
        return rec;
    }
}

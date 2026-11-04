using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace TestFlow;

/// <summary>
/// Stub service settings of one flow node (Advanced tab). When Enabled, running the node does NOT run its real action:
/// it writes a file instead, so the nodes after it (file check, CSV vs DB ...) have something to test.
/// </summary>
public class StubConfig
{
    public bool Enabled { get; set; }
    public string Folder { get; set; } = @"Samples\Output";
    public string FileName { get; set; } = "stub_{yyyyMMdd_HHmmss}.csv";
    public string Format { get; set; } = "CSV";          // CSV | Delimited | FixedWidth | Excel
    public string Delimiter { get; set; } = "";           // Delimited only: comma / pipe / tab / semicolon / tilde / any text
    public bool HasHeader { get; set; } = true;
    public int Rows { get; set; } = 10;
    public int DelaySeconds { get; set; } = 0;            // simulates a slow job
    public string Seed { get; set; } = "";                // blank = random every run; a number = same data every run
    public string Columns { get; set; } = "Id=seq:1\nName=name\nAmount=decimal:1-500:2\nCreated=date:2024-01-01..2024-12-31:yyyy-MM-dd\nStatus=choice:NEW|PAID|CLOSED";
    // AS400 (spec + DB) format
    public string SpecFile { get; set; } = "";            // workbook with sheet "Mapping": TargetColumn, SourceColumn, DataType, Start, Length (+ optional Decimals, Format, Align, Pad, Storage, Default, Generator)
    public string SpecSheet { get; set; } = "Mapping";
    public string DbQuery { get; set; } = "";             // optional SELECT; SourceColumn of the spec = column of this result. Blank = generate values
    public int MaxRows { get; set; } = 0;                 // DB rows to write, 0 = all
    public string Encoding { get; set; } = "ASCII";       // ASCII | UTF-8 | EBCDIC-037 | EBCDIC-500 | EBCDIC-1140
    public string RecordTerminator { get; set; } = "CRLF"; // CRLF | LF | None (fixed records)
    public int RecordLength { get; set; } = 0;            // 0 = end of the last spec column
    public string HeaderRecord { get; set; } = "";        // e.g. HDR{date:yyyyMMdd}
    public string TrailerRecord { get; set; } = "";       // e.g. TRL{count:6}
    public string SaveAs { get; set; } = "stubFile";      // saves the full path as {var:stubFile} for later nodes
}

public static class StubFileService
{
    public static readonly string[] Formats = { "CSV", "Delimited", "FixedWidth", "Excel", "AS400 (spec + DB)" };

    static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

    sealed record Col(string Name, string Gen, int Width);

    /// <summary>Generates the file and returns its full path. Throws InvalidOperationException with a readable message.</summary>
    public static async Task<string> RunAsync(StubConfig c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.FileName)) throw new InvalidOperationException("Stub: file name is empty");
        if (c.Format.StartsWith("AS400")) return await As400StubWriter.RunAsync(c, ct);
        var cols = ParseColumns(c.Columns, c.Format == "FixedWidth");
        if (cols.Count == 0) throw new InvalidOperationException("Stub: define at least one column (NAME=generator, one per line)");
        int rows = Math.Max(0, c.Rows);
        if (c.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(c.DelaySeconds), ct);

        var rnd = int.TryParse(c.Seed, out var seed) ? new Random(seed) : new Random();
        string dir = Abs(RunContext.Resolve(c.Folder));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ExpandName(RunContext.Resolve(c.FileName)));

        // build the data once; every format writes the same rows
        var data = new List<string[]>(rows);
        for (int r = 1; r <= rows; r++)
        {
            ct.ThrowIfCancellationRequested();
            data.Add(cols.Select(col => Generate(col.Gen, r, rnd)).ToArray());
        }

        switch (c.Format)
        {
            case "Excel": WriteExcel(path, cols, data, c.HasHeader); break;
            case "FixedWidth": WriteFixed(path, cols, data, c.HasHeader); break;
            default:
                string d = c.Format == "CSV" ? "," : DelimiterText(c.Delimiter);
                WriteDelimited(path, cols, data, d, c.HasHeader); break;
        }
        return path;
    }

    // ---------- writers ----------
    static void WriteDelimited(string path, List<Col> cols, List<string[]> data, string d, bool header)
    {
        static string Q(string v, string dl) => v.Contains(dl) || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        if (header) sb.AppendLine(string.Join(d, cols.Select(x => Q(x.Name, d))));
        foreach (var row in data) sb.AppendLine(string.Join(d, row.Select(v => Q(v, d))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void WriteFixed(string path, List<Col> cols, List<string[]> data, bool header)
    {
        string Fit(string v, int w, bool left) { if (v.Length > w) v = v[..w]; return left ? v.PadLeft(w) : v.PadRight(w); }
        static bool Numeric(string g) => g.StartsWith("seq") || g.StartsWith("int") || g.StartsWith("decimal");
        var sb = new StringBuilder();
        if (header) sb.AppendLine(string.Concat(cols.Select(x => Fit(x.Name, x.Width, false))));
        foreach (var row in data) sb.AppendLine(string.Concat(cols.Select((x, i) => Fit(row[i], x.Width, Numeric(x.Gen)))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void WriteExcel(string path, List<Col> cols, List<string[]> data, bool header)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        int r = 1;
        if (header) { for (int i = 0; i < cols.Count; i++) ws.Cell(r, i + 1).Value = cols[i].Name; r++; }
        foreach (var row in data)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                var v = row[i];
                if (cols[i].Gen.StartsWith("int") || cols[i].Gen.StartsWith("seq")) { if (long.TryParse(v, out var l)) { ws.Cell(r, i + 1).Value = l; continue; } }
                else if (cols[i].Gen.StartsWith("decimal")) { if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var m)) { ws.Cell(r, i + 1).Value = m; continue; } }
                ws.Cell(r, i + 1).Value = v;
            }
            r++;
        }
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    // ---------- parsing ----------
    static string DelimiterText(string d) => (d ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "comma" => ",", "tab" => "\t", "pipe" => "|", "semicolon" => ";", "space" => " ",
        "colon" => ":", "tilde" => "~", "caret" => "^", _ => d
    };

    /// <summary>One column per line:  NAME=generator   (fixed width: NAME=generator@length)</summary>
    static List<Col> ParseColumns(string text, bool fixedWidth)
    {
        var res = new List<Col>();
        foreach (var raw in (text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            string name = eq < 0 ? line : line[..eq].Trim(), gen = eq < 0 ? "text" : line[(eq + 1)..].Trim();
            int width = 12;
            int at = gen.LastIndexOf('@');
            if (at > 0 && int.TryParse(gen[(at + 1)..], out var w)) { width = Math.Max(1, w); gen = gen[..at].Trim(); }
            else if (fixedWidth) throw new InvalidOperationException($"Stub: column “{name}” needs a width for fixed-width files, e.g. {name}={gen}@10");
            res.Add(new Col(name, gen, width));
        }
        return res;
    }

    internal static string ExpandName(string name)
    {
        var now = DateTime.Now;
        return System.Text.RegularExpressions.Regex.Replace(name, @"\{([^{}:]+)\}", m =>
        {
            try { return now.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture); }
            catch (FormatException) { return m.Value; }
        });
    }

    // ---------- value generators ----------
    static readonly string[] First = { "Alice", "Bob", "Chen", "Dilini", "Emma", "Farid", "Grace", "Hiro", "Ivy", "Kamal", "Liam", "Mei", "Nuwan", "Omar", "Priya" };
    static readonly string[] Last = { "Perera", "Tan", "Smith", "Lee", "Khan", "Silva", "Wong", "Brown", "Singh", "Jones" };

    /// <summary>seq:start | int:min-max | decimal:min-max:places | date:from..to:format | choice:a|b|c | const:x | text[:len] | name | email | bool | guid</summary>
    internal static string Generate(string gen, int row, Random rnd)
    {
        var parts = gen.Split(':', 2);
        string kind = parts[0].Trim().ToLowerInvariant(), arg = parts.Length > 1 ? parts[1] : "";
        var inv = CultureInfo.InvariantCulture;
        try
        {
            switch (kind)
            {
                case "seq": return (long.TryParse(arg, out var s) ? s : 1) + row - 1 + "";
                case "int": { var (a, b) = Range(arg, 0, 1000); return rnd.NextInt64(a, b + 1).ToString(inv); }
                case "decimal":
                {
                    var seg = arg.Split(':'); var (a, b) = Range(seg[0], 0, 1000);
                    int places = seg.Length > 1 && int.TryParse(seg[1], out var p) ? p : 2;
                    return Math.Round(a + rnd.NextDouble() * (b - a), places).ToString("F" + places, inv);
                }
                case "date":
                {
                    var seg = arg.Split(':', 2); string fmt = seg.Length > 1 ? seg[1] : "yyyy-MM-dd";
                    var rg = seg[0].Split("..");
                    DateTime from = rg.Length > 0 && DateTime.TryParse(rg[0], inv, DateTimeStyles.None, out var f) ? f : new DateTime(2024, 1, 1);
                    DateTime to = rg.Length > 1 && DateTime.TryParse(rg[1], inv, DateTimeStyles.None, out var t) ? t : from.AddYears(1);
                    return from.AddDays(rnd.NextDouble() * Math.Max(0, (to - from).TotalDays)).ToString(fmt, inv);
                }
                case "choice": { var o = arg.Split('|'); return o[rnd.Next(o.Length)].Trim(); }
                case "const": return arg;
                case "name": return First[rnd.Next(First.Length)] + " " + Last[rnd.Next(Last.Length)];
                case "email": return (First[rnd.Next(First.Length)] + "." + Last[rnd.Next(Last.Length)] + row).ToLowerInvariant() + "@example.com";
                case "bool": return rnd.Next(2) == 0 ? "true" : "false";
                case "guid": { var b = new byte[16]; rnd.NextBytes(b); return new Guid(b).ToString(); }
                case "text": { int n = int.TryParse(arg, out var l) ? l : 8; return new string(Enumerable.Range(0, n).Select(_ => (char)('A' + rnd.Next(26))).ToArray()); }
                default: throw new InvalidOperationException($"Stub: unknown generator “{gen}”. Use seq, int, decimal, date, choice, const, text, name, email, bool or guid");
            }
        }
        catch (FormatException) { throw new InvalidOperationException($"Stub: bad generator arguments “{gen}”"); }
    }

    static (long, long) Range(string s, long dmin, long dmax)
    {
        var seg = s.Split('-', 2);   // negative numbers are not supported in ranges
        long a = seg.Length > 0 && long.TryParse(seg[0], out var x) ? x : dmin;
        long b = seg.Length > 1 && long.TryParse(seg[1], out var y) ? y : dmax;
        return a <= b ? (a, b) : (b, a);
    }
}

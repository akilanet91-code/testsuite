using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>
/// Reads an interface specification (FSD) sheet with up to three tables - Header, Detail, Trailer - like
///   S/N | Field Name | Descriptions | Data Type | Format | Length | eMgr Length | Start Posn | Occur | Default Value | Remarks | DB Column
/// Tables are found by their header row (needs "Field Name" and "Length"); a row with only "Header" / "Trailer" (/ "Detail") above a table
/// names it, an unnamed table is the Detail. A "Total Length" row ends a table. Columns are found by NAME, so their order does not matter,
/// and "eMgr Length" is never read. "DB Column" (the column added for the DB mapping) says which SELECT column fills a detail field;
/// in a Trailer it can also be COUNT or SUM(FieldName).
/// </summary>
internal static class FsdSpec
{
    static string N(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
    static readonly HashSet<string> DbNames = new() { "dbcolumn", "dbcol", "dbcolumnname", "dbfield", "dbmapping", "dbcolumnmapping", "sourcecolumn", "tablecolumn" };

    sealed class Cols { public int Sn = -1, Name = -1, Desc = -1, Type = -1, Fmt = -1, Len = -1, Start = -1, Def = -1, Rem = -1, Db = -1; }

    static Cols? HeaderRow(string[] row)
    {
        var c = new Cols();
        for (int i = 0; i < row.Length; i++)
        {
            var n = N(row[i]); if (n.Length == 0) continue;
            if (n == "sn" && c.Sn < 0) c.Sn = i;
            else if (n == "fieldname" && c.Name < 0) c.Name = i;
            else if (n is "descriptions" or "description" && c.Desc < 0) c.Desc = i;
            else if (n == "datatype" && c.Type < 0) c.Type = i;
            else if (n == "format" && c.Fmt < 0) c.Fmt = i;
            else if (n == "length" && c.Len < 0) c.Len = i;
            else if (n is "startposn" or "startposition" or "startpos" or "start" && c.Start < 0) c.Start = i;
            else if (n is "defaultvalue" or "default" && c.Def < 0) c.Def = i;
            else if (n == "remarks" && c.Rem < 0) c.Rem = i;
            else if (DbNames.Contains(n) && c.Db < 0) c.Db = i;
        }
        return c.Name >= 0 && c.Len >= 0 ? c : null;
    }

    /// <summary>Returns null when the sheet has no such table (then the old "Mapping" layout is used).</summary>
    public static As400StubWriter.Layout? Parse(List<string[]> grid, bool explicitPoint)
    {
        As400StubWriter.Layout? lay = null; Cols? cols = null; string section = "", pending = ""; int next = 1;
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            var hdr = HeaderRow(row);
            if (hdr != null) { cols = hdr; section = pending.Length > 0 ? pending : "detail"; pending = ""; next = 1; lay ??= new As400StubWriter.Layout { Fsd = true }; continue; }

            var filled = row.Where(x => x.Length > 0).ToList();
            if (filled.Count == 1)
            {
                var l = filled[0].Trim().ToLowerInvariant();
                if (l is "header" or "trailer" or "detail" or "body") { pending = l == "body" ? "detail" : l; cols = null; continue; }
            }
            if (cols == null || lay == null) continue;
            if (row.Take(4).Any(x => Regex.IsMatch(x, @"^\s*total\s*length\s*$", RegexOptions.IgnoreCase))) { cols = null; continue; }

            string Cell(int idx) => idx >= 0 && idx < row.Length ? row[idx].Trim() : "";
            string name = Cell(cols.Name), desc = Cell(cols.Desc), fmt = Cell(cols.Fmt), lenS = Cell(cols.Len);
            if (fmt.Length == 0 && lenS.Length == 0) continue;                       // blank or note line inside the table
            if (name.Length == 0) name = Regex.Replace(desc.ToUpperInvariant(), "[^A-Z0-9]", "");   // e.g. a field whose name cell was left empty
            if (name.Length == 0) continue;

            char kind = '\0'; int fmtLen = 0, dec = 0;
            var m = Regex.Match(fmt, @"^\s*([A-Za-z9])\s*\(\s*(\d+)\s*(?:,\s*(\d+))?\s*\)");
            if (m.Success) { kind = char.ToUpperInvariant(m.Groups[1].Value[0]); fmtLen = int.Parse(m.Groups[2].Value); if (m.Groups[3].Success) dec = int.Parse(m.Groups[3].Value); }
            int length = int.TryParse(Regex.Replace(lenS, @"\D", ""), out var lv) && lv > 0 ? lv : fmtLen;
            if (length <= 0) throw new InvalidOperationException($"Stub (AS400): spec row {i + 1}, field “{name}”: no usable Length or Format (e.g. X(20), 9(10), 9(18,3))");
            var startS = Regex.Replace(Cell(cols.Start), @"\D", "");
            int start = int.TryParse(startS, out var sv) && sv > 0 ? sv : next;

            var f = new As400StubWriter.Field { Target = name, Desc = desc, Start = start, Length = length, Type = "text" };
            if (kind == '9') { f.Type = dec > 0 ? "decimal" : "int"; f.Decimals = dec; }
            var df = DateFormat(Cell(cols.Rem), desc, name, length, kind);
            if (df != null) { f.Type = "date"; f.Format = df; }
            else if (f.Type == "int") f.Pad = "0";                                   // "ALL FILES PAD WITH LEADING '0' FOR NUMERIC FIELDS"
            else if (f.Type == "decimal") { if (explicitPoint) f.Pad = "0"; else f.Storage = "zoned"; }   // zoned = digits only, implied decimal point

            var def = Cell(cols.Def).Trim().Trim('"', '“', '”', '\'');
            if (Cell(cols.Def).Length > 0)
            {
                f.HasDefault = true;
                var dl = def.ToLowerInvariant();
                f.Default = dl is "space" or "spaces" or "blank" or "blanks" ? "" : dl is "zero" or "zeros" ? "0" : def;
            }

            var db = Cell(cols.Db);
            if (db.Length > 0)
            {
                if (Regex.IsMatch(db, @"^\s*(count|sum\s*\(.+\))\s*$", RegexOptions.IgnoreCase)) f.Expr = db.Trim();
                else f.Source = db;
            }

            (section switch { "header" => lay.Header, "trailer" => lay.Trailer, _ => lay.Detail }).Add(f);
            next = start + length;
        }
        return lay == null || lay.Header.Count + lay.Detail.Count + lay.Trailer.Count == 0 ? null : lay;
    }

    /// <summary>Date format from the Remarks ("Date format "YYYYMMDDHHMMSS""), else from a date-like field name and its length.</summary>
    static string? DateFormat(string remarks, string desc, string name, int length, char kind)
    {
        var m = Regex.Match(remarks + " " + desc, @"date\s*format\s*[:=]?\s*[""“”']?\s*([YyMmDdHhSs]{6,}(?:[/\-: ][YyMmDdHhSs]+)*)", RegexOptions.IgnoreCase);
        if (m.Success) return ToNetFormat(m.Groups[1].Value);
        if (kind == '9' && Regex.IsMatch(name, @"(^|_)(DATE|DT|DATETIME|TIMESTAMP)$", RegexOptions.IgnoreCase))
            return length switch { 8 => "yyyyMMdd", 12 => "yyyyMMddHHmm", 14 => "yyyyMMddHHmmss", _ => null };
        return null;
    }

    /// <summary>YYYYMMDDHHMMSS -> yyyyMMddHHmmss (MM after HH means minutes).</summary>
    static string ToNetFormat(string spec)
    {
        var sb = new System.Text.StringBuilder(); bool seenH = false;
        for (int i = 0; i < spec.Length;)
        {
            char ch = char.ToUpperInvariant(spec[i]); int j = i;
            while (j < spec.Length && char.ToUpperInvariant(spec[j]) == ch) j++;
            int run = j - i;
            switch (ch)
            {
                case 'Y': sb.Append('y', run); break;
                case 'D': sb.Append('d', run); break;
                case 'H': seenH = true; sb.Append('H', run); break;
                case 'M': sb.Append(seenH ? 'm' : 'M', run); break;
                case 'S': sb.Append('s', run); break;
                default: sb.Append(spec, i, run); break;
            }
            i = j;
        }
        return sb.ToString();
    }
}

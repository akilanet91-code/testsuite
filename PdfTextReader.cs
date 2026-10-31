using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>How the text of a PDF page is turned into lines.</summary>
public enum PdfLineMode
{
    /// <summary>Words on the same line are joined by one space, or by a TAB when there is a wide gap (table columns).
    /// Use this for delimited / table-style PDFs: the delimiter auto-detect then finds the TAB.</summary>
    Columns,
    /// <summary>Every piece of text is placed at its character position (distance / character width), like a monospaced text file.
    /// Use this for fixed-width layouts (Start + Length) and header/trailer positions.</summary>
    Layout
}

/// <summary>Reads the text layer of a PDF with NO external library (own parser + System.IO.Compression) and returns it as text lines,
/// so the PDF can be verified like any other generated text file.
/// Supported: classic and compressed (xref stream / object stream) PDFs, FlateDecode, text operators (Tj TJ ' " Td TD Tm T* cm q Q),
/// ToUnicode maps, simple fonts and Type0 (Identity) fonts.
/// Not supported: encrypted PDFs, scanned PDFs (no text, no OCR), text inside form XObjects, LZW / ASCII85 / image filters, rotated text.</summary>
public static class PdfTextReader
{
    public static bool IsPdf(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    static byte[] ReadAll(string path)
    {
        // FileShare.ReadWrite: the generating program may still hold the file open
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>All non-empty lines of all pages, top to bottom.</summary>
    public static List<string> ReadLines(string path, PdfLineMode mode = PdfLineMode.Columns)
    {
        try
        {
            var pdf = new PdfFile(ReadAll(path));
            var lines = new List<string>();
            foreach (var (page, res) in pdf.Pages())
                lines.AddRange(PageLines(pdf, page, res, mode));
            return lines;
        }
        catch (NotSupportedException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("Could not read PDF text: " + ex.Message, ex); }
    }

    /// <summary>Number of pages, for messages (0 when the file cannot be read).</summary>
    public static int PageCount(string path)
    {
        try { return new PdfFile(ReadAll(path)).Pages().Count; } catch { return 0; }
    }

    // ───────────────────────── text → lines ─────────────────────────

    static List<string> PageLines(PdfFile pdf, Dictionary<string, object> page, object? res, PdfLineMode mode)
    {
        var content = new MemoryStream();
        foreach (var s in pdf.ContentStreams(page)) { content.Write(s); content.WriteByte((byte)'\n'); }
        var chunks = new ContentInterpreter(pdf, res).Run(content.ToArray())
                         .Where(c => c.Text.Trim().Length > 0).ToList();
        if (chunks.Count == 0) return new List<string>();

        // typical character width of the page (median), used to turn distances into spaces
        var cw = chunks.Where(c => c.X1 - c.X0 > 0).Select(c => (c.X1 - c.X0) / c.Text.Length).OrderBy(x => x).ToList();
        double charW = cw.Count > 0 ? cw[cw.Count / 2] : chunks.Average(c => c.Size) * 0.5;
        if (charW <= 0.01) charW = 5.0;
        double left = chunks.Min(c => c.X0);

        // group chunks into visual lines: same baseline within half the font size
        var groups = new List<(double Y, List<Chunk> Items)>();
        foreach (var c in chunks.OrderByDescending(c => c.Y))
        {
            double tol = Math.Max(1.0, c.Size * 0.5);
            int g = groups.FindIndex(x => Math.Abs(x.Y - c.Y) <= tol);
            if (g >= 0) groups[g].Items.Add(c); else groups.Add((c.Y, new List<Chunk> { c }));
        }

        var lines = new List<string>();
        foreach (var (_, items) in groups.OrderByDescending(g => g.Y))
        {
            var sb = new StringBuilder();
            Chunk? prev = null;
            foreach (var c in items.OrderBy(c => c.X0))
            {
                double gap = prev == null ? 0 : c.X0 - prev.X1;
                bool spaceAround = sb.Length > 0 && (sb[^1] == ' ' || c.Text[0] == ' ');
                if (mode == PdfLineMode.Layout)
                {
                    int col = (int)Math.Round((c.X0 - left) / charW);
                    if (sb.Length > 0 && col <= sb.Length)
                        col = (spaceAround || gap < 0.25 * charW) ? sb.Length : sb.Length + 1;   // never glue two words by accident
                    if (col > sb.Length) sb.Append(' ', col - sb.Length);
                    sb.Append(c.Text);
                }
                else
                {
                    if (prev != null && gap > 1.8 * charW)
                    {
                        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                        sb.Append('\t').Append(c.Text.TrimStart());
                    }
                    else
                    {
                        if (prev != null && gap >= 0.25 * charW && !spaceAround) sb.Append(' ');   // small gaps are kerning: no space
                        sb.Append(c.Text);
                    }
                }
                prev = c;
            }
            var line = sb.ToString().TrimEnd();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    sealed record Chunk(string Text, double X0, double X1, double Y, double Size);

    // ───────────────────────── PDF objects ─────────────────────────

    sealed record PName(string V);
    sealed record PRef(int Num);
    sealed record PStr(byte[] B);
    sealed record PKw(string V);

    static readonly object End = new();

    /// <summary>Tokenizer / object parser for PDF syntax (file objects and content streams).</summary>
    sealed class Lexer
    {
        readonly byte[] d;
        public int Pos;
        public Lexer(byte[] data, int pos = 0) { d = data; Pos = pos; }

        public static bool IsWs(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
        static bool IsDelim(byte b) => b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';
        static int Hex(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;

        void SkipWs()
        {
            while (Pos < d.Length)
            {
                var b = d[Pos];
                if (IsWs(b)) Pos++;
                else if (b == '%') { while (Pos < d.Length && d[Pos] != 10 && d[Pos] != 13) Pos++; }
                else break;
            }
        }

        /// <summary>One raw token. "[" "]" "&lt;&lt;" "&gt;&gt;" come back as PKw; End at the end of the data.</summary>
        public object Next()
        {
            SkipWs();
            if (Pos >= d.Length) return End;
            byte b = d[Pos];
            switch ((char)b)
            {
                case '/':
                {
                    Pos++; var sb = new StringBuilder();
                    while (Pos < d.Length && !IsWs(d[Pos]) && !IsDelim(d[Pos]))
                    {
                        if (d[Pos] == '#' && Pos + 2 < d.Length && Hex(d[Pos + 1]) >= 0 && Hex(d[Pos + 2]) >= 0)
                        { sb.Append((char)(Hex(d[Pos + 1]) * 16 + Hex(d[Pos + 2]))); Pos += 3; }
                        else sb.Append((char)d[Pos++]);
                    }
                    return new PName(sb.ToString());
                }
                case '(': return LiteralString();
                case '<':
                    if (Pos + 1 < d.Length && d[Pos + 1] == '<') { Pos += 2; return new PKw("<<"); }
                    return HexString();
                case '>':
                    if (Pos + 1 < d.Length && d[Pos + 1] == '>') { Pos += 2; return new PKw(">>"); }
                    Pos++; return new PKw(">");
                case '[': Pos++; return new PKw("[");
                case ']': Pos++; return new PKw("]");
                case '{': case '}': Pos++; return new PKw(((char)b).ToString());
                case ')': Pos++; return Next();   // stray
            }
            int start = Pos;
            while (Pos < d.Length && !IsWs(d[Pos]) && !IsDelim(d[Pos])) Pos++;
            var tok = Encoding.Latin1.GetString(d, start, Pos - start);
            char f = tok[0];
            if ((char.IsDigit(f) || f == '.' || f == '+' || f == '-') &&
                double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return num;
            return new PKw(tok);
        }

        PStr LiteralString()
        {
            Pos++; int depth = 1; var r = new List<byte>();
            while (Pos < d.Length)
            {
                char c = (char)d[Pos++];
                if (c == '\\')
                {
                    if (Pos >= d.Length) break;
                    char e = (char)d[Pos++];
                    switch (e)
                    {
                        case 'n': r.Add(10); break;
                        case 'r': r.Add(13); break;
                        case 't': r.Add(9); break;
                        case 'b': r.Add(8); break;
                        case 'f': r.Add(12); break;
                        case '\r': if (Pos < d.Length && d[Pos] == '\n') Pos++; break;
                        case '\n': break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int i = 0; i < 2 && Pos < d.Length && d[Pos] >= '0' && d[Pos] <= '7'; i++) v = v * 8 + (d[Pos++] - '0');
                                r.Add((byte)v);
                            }
                            else r.Add((byte)e);
                            break;
                    }
                }
                else if (c == '(') { depth++; r.Add((byte)c); }
                else if (c == ')') { if (--depth == 0) break; r.Add((byte)c); }
                else r.Add((byte)c);
            }
            return new PStr(r.ToArray());
        }

        PStr HexString()
        {
            Pos++; var r = new List<byte>(); int hi = -1;
            while (Pos < d.Length && d[Pos] != '>')
            {
                int h = Hex(d[Pos++]); if (h < 0) continue;
                if (hi < 0) hi = h; else { r.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) r.Add((byte)(hi * 16));
            if (Pos < d.Length) Pos++;
            return new PStr(r.ToArray());
        }

        static bool IsInt(double n) => n >= 0 && n < int.MaxValue && n == Math.Floor(n);

        /// <summary>A full object: number, name, string, array, dictionary, and (refs = true) indirect references "5 0 R".</summary>
        public object? Parse(bool refs)
        {
            var t = Next();
            if (t is PKw k)
            {
                switch (k.V)
                {
                    case "[":
                    {
                        var l = new List<object?>();
                        while (true)
                        {
                            var v = Parse(refs);
                            if (ReferenceEquals(v, End) || (v is PKw e && e.V == "]")) break;
                            l.Add(v);
                        }
                        return l;
                    }
                    case "<<":
                    {
                        var dd = new Dictionary<string, object?>();
                        while (true)
                        {
                            var key = Next();
                            if (ReferenceEquals(key, End) || (key is PKw kk && kk.V == ">>")) break;
                            if (key is not PName kn) continue;
                            var v = Parse(refs);
                            if (ReferenceEquals(v, End) || (v is PKw kv && kv.V == ">>")) break;
                            dd[kn.V] = v;
                        }
                        return dd;
                    }
                    case "true": return true;
                    case "false": return false;
                    case "null": return null;
                }
                return k;
            }
            if (refs && t is double n && IsInt(n))
            {
                int save = Pos;
                if (Next() is double g && IsInt(g) && Next() is PKw r && r.V == "R") return new PRef((int)n);
                Pos = save;
            }
            return t;
        }

        /// <summary>After the BI keyword: skips the binary image data up to and including EI.</summary>
        public void SkipInlineImage()
        {
            for (int i = Pos; i + 1 < d.Length; i++)
                if (d[i] == 'E' && d[i + 1] == 'I' && (i == 0 || IsWs(d[i - 1])) && (i + 2 >= d.Length || IsWs(d[i + 2]))) { Pos = i + 2; return; }
            Pos = d.Length;
        }
    }

    // ───────────────────────── PDF file structure ─────────────────────────

    sealed class FontInfo
    {
        public Dictionary<int, string>? Map;     // code -> unicode text (ToUnicode)
        public int CodeLen = 1;                  // bytes per character code
        public int First;                        // simple fonts: FirstChar
        public double[]? Widths;                 // simple fonts: Widths
        public Dictionary<int, double>? W2;      // Type0: W
        public double DefaultW = 500;            // glyph width (1/1000 em) when unknown (Courier = 600)
    }

    sealed class PdfFile
    {
        readonly byte[] data;
        public readonly Dictionary<int, object?> Objs = new();
        readonly Dictionary<int, byte[]> raw = new();
        readonly Dictionary<int, FontInfo> fonts = new();

        public PdfFile(byte[] bytes)
        {
            data = bytes;
            if (data.Length < 8 || Encoding.Latin1.GetString(data, 0, Math.Min(1024, data.Length)).IndexOf("%PDF-", StringComparison.Ordinal) < 0)
                throw new InvalidDataException("not a PDF file");
            Scan();
        }

        void Scan()
        {
            var text = Encoding.Latin1.GetString(data);
            var rx = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj(?![a-zA-Z])");
            int pos = 0;
            while (true)
            {
                var m = rx.Match(text, pos);
                if (!m.Success) break;
                int num = int.Parse(m.Groups[1].Value);
                var lx = new Lexer(data, m.Index + m.Length);
                var val = lx.Parse(true);
                Objs[num] = val;
                pos = Math.Max(lx.Pos, m.Index + m.Length);

                if (val is Dictionary<string, object?> dict)
                {
                    int q = pos; while (q < data.Length && Lexer.IsWs(data[q])) q++;
                    if (string.CompareOrdinal(text, q, "stream", 0, 6) == 0)
                    {
                        q += 6;
                        if (q < data.Length && data[q] == '\r') q++;
                        if (q < data.Length && data[q] == '\n') q++;
                        int end = -1;
                        if (dict.TryGetValue("Length", out var lv) && lv is double ld && ld >= 0 && q + (long)ld <= data.Length)
                        {
                            int e = q + (int)ld; while (e < data.Length && Lexer.IsWs(data[e])) e++;
                            if (string.CompareOrdinal(text, e, "endstream", 0, 9) == 0) end = q + (int)ld;
                        }
                        int next;
                        if (end < 0)
                        {
                            int es = text.IndexOf("endstream", q, StringComparison.Ordinal);
                            if (es < 0) es = data.Length;
                            end = es;
                            if (end > q && data[end - 1] == '\n') end--;
                            if (end > q && data[end - 1] == '\r') end--;
                            next = es + 9;
                        }
                        else next = end + 9;
                        raw[num] = data.AsSpan(q, Math.Max(0, end - q)).ToArray();
                        pos = Math.Min(next, data.Length);
                    }
                }
            }

            // encryption check (trailer dictionaries and xref-stream dictionaries)
            foreach (Match t in Regex.Matches(text, @"trailer\s*<<"))
                if (new Lexer(data, t.Index + 7).Parse(false) is Dictionary<string, object?> td && td.ContainsKey("Encrypt"))
                    throw new NotSupportedException("encrypted PDF is not supported");
            foreach (var o in Objs.Values)
                if (o is Dictionary<string, object?> xd && TypeOf(xd) == "XRef" && xd.ContainsKey("Encrypt"))
                    throw new NotSupportedException("encrypted PDF is not supported");

            // objects stored inside object streams (PDF 1.5+)
            foreach (var kv in Objs.Where(k => k.Value is Dictionary<string, object?> od && TypeOf(od) == "ObjStm").ToList())
            {
                var dec = Decode(kv.Key); if (dec == null) continue;
                var od = (Dictionary<string, object?>)kv.Value!;
                int n = (int)Number(od, "N"), first = (int)Number(od, "First");
                var hl = new Lexer(dec); var pairs = new List<(int Num, int Off)>();
                for (int i = 0; i < n; i++)
                    if (hl.Next() is double a && hl.Next() is double b) pairs.Add(((int)a, (int)b));
                foreach (var (onum, off) in pairs)
                    if (first + off < dec.Length) Objs.TryAdd(onum, new Lexer(dec, first + off).Parse(true));
            }
        }

        public static string? TypeOf(Dictionary<string, object?> d) => d.TryGetValue("Type", out var t) && t is PName n ? n.V : null;

        public object? Get(object? o)
        {
            for (int i = 0; i < 20 && o is PRef r; i++) o = Objs.TryGetValue(r.Num, out var v) ? v : null;
            return o;
        }
        public Dictionary<string, object?>? Dict(object? o) => Get(o) as Dictionary<string, object?>;
        public List<object?>? Arr(object? o) => Get(o) as List<object?>;
        double Number(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && Get(v) is double n ? n : 0;

        // ── streams ──
        public byte[]? Decode(int num)
        {
            if (!raw.TryGetValue(num, out var b) || Objs[num] is not Dictionary<string, object?> dict) return null;
            var filters = new List<string>();
            var f = dict.TryGetValue("Filter", out var fv) ? Get(fv) : null;
            if (f is PName fn) filters.Add(fn.V);
            else if (f is List<object?> fl) foreach (var x in fl) if (Get(x) is PName xn) filters.Add(xn.V);
            foreach (var flt in filters)
            {
                switch (flt)
                {
                    case "FlateDecode": case "Fl": b = Inflate(b); break;
                    case "ASCIIHexDecode": case "AHx": b = HexDecode(b); break;
                    default: return null;   // LZW, ASCII85, image filters: not supported
                }
            }
            return b;
        }

        static byte[] Inflate(byte[] input)
        {
            var output = new MemoryStream();
            try
            {
                using var zs = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
                zs.CopyTo(output);
            }
            catch (InvalidDataException)
            {
                output = new MemoryStream();
                try
                {
                    using var ds = new DeflateStream(new MemoryStream(input, 2, Math.Max(0, input.Length - 2)), CompressionMode.Decompress);
                    ds.CopyTo(output);
                }
                catch (InvalidDataException) { /* keep what was decoded */ }
            }
            return output.ToArray();
        }

        static byte[] HexDecode(byte[] input)
        {
            var r = new List<byte>(); int hi = -1;
            foreach (var c in input)
            {
                if (c == '>') break;
                int h = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (h < 0) continue;
                if (hi < 0) hi = h; else { r.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) r.Add((byte)(hi * 16));
            return r.ToArray();
        }

        // ── pages ──
        public List<(Dictionary<string, object?> Page, object? Res)> Pages()
        {
            var result = new List<(Dictionary<string, object?>, object?)>();
            var cat = Objs.Values.OfType<Dictionary<string, object?>>().FirstOrDefault(d => TypeOf(d) == "Catalog");
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(Dictionary<string, object?>? node, object? res)
            {
                if (node == null || !visited.Add(node)) return;
                if (node.TryGetValue("Resources", out var r)) res = r;
                if (node.ContainsKey("Kids") || TypeOf(node) == "Pages")
                {
                    if (Arr(node.GetValueOrDefault("Kids")) is { } kids) foreach (var k in kids) Walk(Dict(k), res);
                }
                else result.Add((node, res));
            }
            if (cat != null) Walk(Dict(cat.GetValueOrDefault("Pages")), null);
            if (result.Count == 0)   // damaged page tree: all page objects in file order
                foreach (var kv in Objs.OrderBy(k => k.Key))
                    if (kv.Value is Dictionary<string, object?> pd && TypeOf(pd) == "Page")
                        result.Add((pd, pd.GetValueOrDefault("Resources")));
            return result;
        }

        public List<byte[]> ContentStreams(Dictionary<string, object?> page)
        {
            var refs = new List<int>();
            void Add(object? o)
            {
                if (o is PRef r)
                {
                    if (Objs.GetValueOrDefault(r.Num) is List<object?> l) foreach (var x in l) Add(x);
                    else refs.Add(r.Num);
                }
                else if (o is List<object?> l2) foreach (var x in l2) Add(x);
            }
            Add(page.GetValueOrDefault("Contents"));
            var res = new List<byte[]>();
            foreach (var n in refs) { var b = Decode(n); if (b != null) res.Add(b); }
            return res;
        }

        // ── fonts ──
        public FontInfo LoadFont(object? v)
        {
            int key = v is PRef r ? r.Num : -1;
            if (key >= 0 && fonts.TryGetValue(key, out var cached)) return cached;
            var fi = BuildFont(Dict(v));
            if (key >= 0) fonts[key] = fi;
            return fi;
        }

        FontInfo BuildFont(Dictionary<string, object?>? d)
        {
            var fi = new FontInfo();
            if (d == null) return fi;
            string sub = Get(d.GetValueOrDefault("Subtype")) is PName sn ? sn.V : "";
            string baseFont = Get(d.GetValueOrDefault("BaseFont")) is PName bn ? bn.V : "";
            if (baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase)) fi.DefaultW = 600;

            if (sub == "Type0")
            {
                fi.CodeLen = 2; fi.DefaultW = 1000;
                var dd = Dict(Arr(d.GetValueOrDefault("DescendantFonts"))?.FirstOrDefault());
                if (dd != null)
                {
                    if (Get(dd.GetValueOrDefault("DW")) is double dw) fi.DefaultW = dw;
                    if (Arr(dd.GetValueOrDefault("W")) is { } w)
                    {
                        fi.W2 = new Dictionary<int, double>();
                        for (int i = 0; i < w.Count;)
                        {
                            if (Get(w[i]) is not double c0) { i++; continue; }
                            if (i + 1 < w.Count && Get(w[i + 1]) is List<object?> list)
                            {
                                for (int j = 0; j < list.Count; j++) if (Get(list[j]) is double wj) fi.W2[(int)c0 + j] = wj;
                                i += 2;
                            }
                            else if (i + 2 < w.Count && Get(w[i + 1]) is double c1 && Get(w[i + 2]) is double wr)
                            {
                                for (int c = (int)c0; c <= (int)c1 && c - (int)c0 < 70000; c++) fi.W2[c] = wr;
                                i += 3;
                            }
                            else i++;
                        }
                    }
                }
            }
            else
            {
                if (Get(d.GetValueOrDefault("FirstChar")) is double fc) fi.First = (int)fc;
                if (Arr(d.GetValueOrDefault("Widths")) is { } ws)
                    fi.Widths = ws.Select(x => Get(x) is double wv ? wv : fi.DefaultW).ToArray();
            }

            if (d.GetValueOrDefault("ToUnicode") is PRef tr && Decode(tr.Num) is { } cm)
                ParseCMap(Encoding.Latin1.GetString(cm), fi);
            return fi;
        }

        static string HexToText(string hex)
        {
            if (hex.Length % 2 == 1) hex += "0";
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return Encoding.BigEndianUnicode.GetString(b);
        }

        static void ParseCMap(string cmap, FontInfo fi)
        {
            var map = new Dictionary<int, string>();
            var cs = Regex.Match(cmap, @"begincodespacerange\s*<([0-9A-Fa-f]+)>");
            if (cs.Success) fi.CodeLen = Math.Max(1, cs.Groups[1].Value.Length / 2);

            foreach (Match blk in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(blk.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>"))
                    map[Convert.ToInt32(m.Groups[1].Value, 16)] = HexToText(m.Groups[2].Value);

            foreach (Match blk in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(blk.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(?:<([0-9A-Fa-f]*)>|\[(.*?)\])", RegexOptions.Singleline))
                {
                    int lo = Convert.ToInt32(m.Groups[1].Value, 16), hi = Convert.ToInt32(m.Groups[2].Value, 16);
                    if (hi < lo || hi - lo > 70000) continue;
                    if (m.Groups[3].Success)
                    {
                        var start = HexToText(m.Groups[3].Value);
                        for (int c = lo; c <= hi; c++)
                            map[c] = start.Length == 0 ? "" : start[..^1] + (char)(start[^1] + (c - lo));
                    }
                    else
                    {
                        int c = lo;
                        foreach (Match h in Regex.Matches(m.Groups[4].Value, @"<([0-9A-Fa-f]*)>"))
                            if (c <= hi) map[c++] = HexToText(h.Groups[1].Value);
                    }
                }
            if (map.Count > 0) fi.Map = map;
        }
    }

    // ───────────────────────── content stream interpreter ─────────────────────────

    sealed class ContentInterpreter
    {
        readonly PdfFile pdf;
        readonly Dictionary<string, object?>? fontDict;
        readonly List<Chunk> chunks = new();
        double[] ctm = { 1, 0, 0, 1, 0, 0 }, tm = { 1, 0, 0, 1, 0, 0 }, tlm = { 1, 0, 0, 1, 0, 0 };
        readonly Stack<double[]> stack = new();
        FontInfo font = new();
        double fs = 10, tc, tw, th = 1, tl;

        public ContentInterpreter(PdfFile pdf, object? resources)
        {
            this.pdf = pdf;
            var res = pdf.Dict(resources);
            fontDict = res == null ? null : pdf.Dict(res.GetValueOrDefault("Font"));
        }

        // row-vector convention: result = a, then b
        static double[] Mul(double[] a, double[] b) => new[]
        {
            a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
            a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
            a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5]
        };
        static double[] Move(double x, double y) => new[] { 1, 0, 0, 1, x, y };

        static double N(List<object?> a, int i) => i >= 0 && i < a.Count && a[i] is double d ? d : 0;

        void NextLine(double tx, double ty) { tlm = Mul(Move(tx, ty), tlm); tm = (double[])tlm.Clone(); }

        public List<Chunk> Run(byte[] content)
        {
            var lx = new Lexer(content);
            var ops = new List<object?>();
            while (true)
            {
                var t = lx.Parse(false);
                if (ReferenceEquals(t, End)) break;
                if (t is not PKw k) { ops.Add(t); if (ops.Count > 64) ops.RemoveAt(0); continue; }

                switch (k.V)
                {
                    case "q": stack.Push((double[])ctm.Clone()); break;
                    case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                    case "cm": if (ops.Count >= 6) ctm = Mul(new[] { N(ops, 0), N(ops, 1), N(ops, 2), N(ops, 3), N(ops, 4), N(ops, 5) }, ctm); break;
                    case "BT": tm = new double[] { 1, 0, 0, 1, 0, 0 }; tlm = (double[])tm.Clone(); break;
                    case "Tf":
                        if (ops.Count >= 2 && ops[^2] is PName fn)
                        {
                            font = fontDict != null && fontDict.TryGetValue(fn.V, out var fv) ? pdf.LoadFont(fv) : new FontInfo();
                            fs = N(ops, ops.Count - 1);
                        }
                        break;
                    case "Td": NextLine(N(ops, 0), N(ops, 1)); break;
                    case "TD": tl = -N(ops, 1); NextLine(N(ops, 0), N(ops, 1)); break;
                    case "Tm": tm = new[] { N(ops, 0), N(ops, 1), N(ops, 2), N(ops, 3), N(ops, 4), N(ops, 5) }; tlm = (double[])tm.Clone(); break;
                    case "T*": NextLine(0, -tl); break;
                    case "TL": tl = N(ops, 0); break;
                    case "Tc": tc = N(ops, 0); break;
                    case "Tw": tw = N(ops, 0); break;
                    case "Tz": th = N(ops, 0) / 100.0; break;
                    case "Tj": if (ops.Count >= 1 && ops[^1] is PStr s1) Show(s1.B); break;
                    case "'": NextLine(0, -tl); if (ops.Count >= 1 && ops[^1] is PStr s2) Show(s2.B); break;
                    case "\"":
                        if (ops.Count >= 3 && ops[^1] is PStr s3) { tw = N(ops, ops.Count - 3); tc = N(ops, ops.Count - 2); NextLine(0, -tl); Show(s3.B); }
                        break;
                    case "TJ":
                        if (ops.Count >= 1 && ops[^1] is List<object?> arr)
                            foreach (var it in arr)
                            {
                                if (it is PStr ps) Show(ps.B);
                                else if (it is double adj) tm = Mul(Move(-adj / 1000.0 * fs * th, 0), tm);
                            }
                        break;
                    case "BI": lx.SkipInlineImage(); break;
                }
                ops.Clear();
            }
            return chunks;
        }

        void Show(byte[] s)
        {
            var sb = new StringBuilder(); double adv = 0;
            var m0 = Mul(tm, ctm);
            for (int i = 0; i + font.CodeLen <= s.Length; i += font.CodeLen)
            {
                int code = 0;
                for (int j = 0; j < font.CodeLen; j++) code = code * 256 + s[i + j];

                if (font.Map != null && font.Map.TryGetValue(code, out var txt)) sb.Append(txt);
                else if (font.CodeLen == 1) sb.Append((char)code);   // no ToUnicode: treat as Latin-1 / WinAnsi

                double w = font.W2 != null ? font.W2.GetValueOrDefault(code, font.DefaultW)
                         : font.Widths != null && code - font.First >= 0 && code - font.First < font.Widths.Length ? font.Widths[code - font.First]
                         : font.DefaultW;
                adv += (w / 1000.0 * fs + tc + (font.CodeLen == 1 && code == 32 ? tw : 0)) * th;
            }
            tm = Mul(Move(adv, 0), tm);
            var m1 = Mul(tm, ctm);
            double size = Math.Max(0.1, fs * Math.Sqrt(m0[2] * m0[2] + m0[3] * m0[3]));
            if (sb.Length > 0) chunks.Add(new Chunk(sb.ToString(), m0[4], m1[4], m0[5], size));
        }
    }
}

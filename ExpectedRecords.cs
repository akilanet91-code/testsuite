namespace TestFlow;

/// <summary>Checks rows (CSV records or DB rows) against ExpectedValues typed in the Excel row.
/// One record = lines COLUMN=value. Several records: separate them with a blank line or a line of ---.
/// Each record must match a different row; when the record contains the key column, that value picks the row.</summary>
internal static class ExpectedRecords
{
    /// <returns>A success message, or null when something differed (the differences are added to issues).</returns>
    public static string? Check(string text, List<Dictionary<string, string>> rows, List<string> cols, TestCase tc, string key, List<string> issues)
    {
        var blocks = new List<List<(string col, string value)>>(); var cur = new List<string>();
        void Flush()
        {
            if (cur.Count > 0) { var e = DbRecordCheck.ParseExpected(string.Join("\n", cur), null); if (e.Count > 0) blocks.Add(e); cur.Clear(); }
        }
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.All(ch => ch == '-')) Flush(); else cur.Add(t);
        }
        Flush();
        if (blocks.Count == 0) return null;
        if (rows.Count == 0) { issues.Add("ExpectedValues: no record to check"); return null; }

        key = (key ?? "").Trim(); var used = new HashSet<int>(); int ok = 0, before = issues.Count;
        for (int b = 0; b < blocks.Count; b++)
        {
            var exp = blocks[b];
            var kv = key.Length > 0 ? exp.FirstOrDefault(x => x.col.Equals(key, StringComparison.OrdinalIgnoreCase)) : default;
            bool hasKey = kv.col != null;
            string label = blocks.Count > 1 ? $"Expected record {b + 1}" + (hasKey ? $" ({key}={kv.value})" : "") : "ExpectedValues";
            int found = -1; List<string>? best = null;
            for (int i = 0; i < rows.Count && found < 0; i++)
            {
                if (used.Contains(i)) continue;
                if (hasKey && DbExcelComparer.Norm(rows[i].GetValueOrDefault(key, "")) != DbExcelComparer.Norm(kv.value)) continue;
                var diffs = DbRecordCheck.CompareRow(rows[i], exp, cols, tc);
                if (diffs.Count == 0) found = i;
                else if (best == null || diffs.Count < best.Count) best = diffs;
            }
            if (found >= 0) { used.Add(found); ok++; }
            else if (best != null) issues.Add($"{label}: {best.Count} value(s) differ: " + string.Join("; ", best.Take(10)) + (best.Count > 10 ? " …" : ""));
            else issues.Add($"{label}: no unused record" + (hasKey ? $" with {key}={kv.value}" : ""));
        }
        return issues.Count == before ? (blocks.Count == 1 ? $"ExpectedValues: all {blocks[0].Count} value(s) match" : $"ExpectedValues: {ok} expected record(s) match ({rows.Count} record(s))") : null;
    }
}

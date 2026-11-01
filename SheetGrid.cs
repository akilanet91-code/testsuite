using System.IO;

namespace TestFlow;

/// <summary>Turns a sheet (rows of cell text) into header->value records. Handles report-style sheets: title lines above the table
/// (Report ID, Report Name, Record Count...), blank lines, merged header cells. The header row is HeaderRow (1-based) or, when 0,
/// found automatically: the first row that has most of the filled cells of the sheet's widest row (title lines have only a few).
/// A header owns its own column and the empty columns up to the next header (merged cells), so the value is taken from there.</summary>
internal static class SheetGrid
{
    public static (List<string> cols, List<Dictionary<string, string>> rows) ToRows(List<string[]> grid, int headerRow = 0)
    {
        int h;
        if (headerRow > 0)
        {
            h = headerRow - 1;
            if (h >= grid.Count) throw new InvalidDataException($"HeaderRow {headerRow} is beyond the last row of the sheet ({grid.Count})");
        }
        else
        {
            int scan = Math.Min(grid.Count, 30);
            var counts = Enumerable.Range(0, scan).Select(i => grid[i].Count(t => t.Length > 0)).ToList();
            int max = counts.Count == 0 ? 0 : counts.Max();
            if (max == 0) throw new InvalidDataException("Sheet is empty");
            int need = (int)Math.Ceiling(max * 0.7);
            h = counts.FindIndex(c => c > 0 && c >= need);
        }

        var head = grid[h];
        var at = Enumerable.Range(0, head.Length).Where(c => head[c].Length > 0).ToList();
        if (at.Count == 0) throw new InvalidDataException($"Row {h + 1} has no header text");
        var heads = at.Select((c, k) => (name: head[c], from: c, to: k + 1 < at.Count ? at[k + 1] - 1 : c)).ToList();

        var rows = new List<Dictionary<string, string>>();
        for (int r = h + 1; r < grid.Count; r++)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, from, to) in heads)
            {
                string v = "";
                for (int c = from; c <= to && c < grid[r].Length; c++) if (grid[r][c].Length > 0) { v = grid[r][c]; break; }
                d[name] = v;
            }
            if (d.Values.Any(v => v.Length > 0)) rows.Add(d);
        }
        return (heads.Select(x => x.name).ToList(), rows);
    }
}

using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace TestFlow;

/// <summary>Reads the old Excel 97-2003 format (.xls, BIFF8). ClosedXML cannot open it, so ExcelDataReader (MIT licence,
/// pure managed code, no native files, no internet needed at run time) is used for it.
/// Returns the same shape as the .xlsx readers: header names + one header->text dictionary per data row.</summary>
public static class XlsReader
{
    /// <summary>True for .xls files (by extension, or by the OLE2 file signature when the extension is wrong).</summary>
    public static bool IsXls(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".xls", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sig = new byte[8]; int n = fs.Read(sig, 0, 8);
            return n == 8 && sig[0] == 0xD0 && sig[1] == 0xCF && sig[2] == 0x11 && sig[3] == 0xE0 && sig[4] == 0xA1 && sig[5] == 0xB1 && sig[6] == 0x1A && sig[7] == 0xE1;
        }
        catch { return false; }
    }

    public static (List<string> cols, List<Dictionary<string, string>> rows) Read(string path, string? sheet, int headerRow = 0)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // .xls text uses legacy code pages
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateBinaryReader(fs);

        // go to the named sheet (else stay on the first one)
        if (!string.IsNullOrWhiteSpace(sheet))
        {
            var names = new List<string>();
            do names.Add(reader.Name); while (reader.NextResult());
            int idx = names.FindIndex(n => n.Equals(sheet.Trim(), StringComparison.OrdinalIgnoreCase));
            fs.Position = 0;
            using var again = ExcelReaderFactory.CreateBinaryReader(fs);
            for (int i = 0; i < Math.Max(0, idx); i++) again.NextResult();
            return SheetGrid.ToRows(Grid(again), headerRow);
        }
        return SheetGrid.ToRows(Grid(reader), headerRow);
    }

    /// <summary>The whole sheet as rows of cell text (no header detection). A blank name or "Mapping" that does not exist = first sheet.</summary>
    public static (List<string[]> grid, string sheet) ReadGrid(string path, string? sheet)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var names = new List<string>();
        using (var fs0 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r0 = ExcelReaderFactory.CreateBinaryReader(fs0)) { do names.Add(r0.Name); while (r0.NextResult()); }
        int idx = string.IsNullOrWhiteSpace(sheet) ? 0 : names.FindIndex(n => n.Equals(sheet.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            if (sheet!.Trim().Equals("Mapping", StringComparison.OrdinalIgnoreCase)) idx = 0;
            else throw new InvalidDataException($"Sheet “{sheet}” not found. Sheets: {string.Join(", ", names)}");
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateBinaryReader(fs);
        for (int i = 0; i < idx; i++) reader.NextResult();
        return (Grid(reader), names[idx]);
    }

    static List<string[]> Grid(IExcelDataReader reader)
    {
        var grid = new List<string[]>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (int c = 0; c < cells.Length; c++) cells[c] = Text(reader.GetValue(c));
            grid.Add(cells);
        }
        return grid;
    }

    static string Text(object? v) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double d => d.ToString("G15", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => (v.ToString() ?? "").Trim()
    };
}

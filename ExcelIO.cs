using System.IO;
using System.Reflection;
using ClosedXML.Excel;

namespace TestFlow;

public static class ExcelIO
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "Status", "Message", "Duration" };

    public static List<TestCase> Load(string path)
    {
        var list = new List<TestCase>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var header = ws.FirstRowUsed() ?? throw new InvalidDataException("The sheet is empty.");
        var map = new Dictionary<int, PropertyInfo>();
        foreach (var cell in header.CellsUsed())
        {
            var name = cell.GetString().Trim();
            if (Skip.Contains(name)) continue;
            var p = typeof(TestCase).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite) map[cell.Address.ColumnNumber] = p;
        }
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var tc = new TestCase();
            foreach (var (col, p) in map)
            {
                var s = row.Cell(col).GetString().Trim();
                if (s.Length == 0) continue;
                if (p.PropertyType == typeof(int)) p.SetValue(tc, int.TryParse(s, out var n) ? n : 0);
                else if (p.PropertyType == typeof(string)) p.SetValue(tc, s);
            }
            if (string.IsNullOrWhiteSpace(tc.Name)) continue;
            if (tc.Id == 0) tc.Id = list.Count + 1;
            list.Add(tc);
        }
        return list;
    }

    public static void SaveTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("TestCases");
        string[] h = { "Id","Name","Description","Type","CtrlMJobName","CtrlMArguments","TimeoutSeconds","ExpectedExitCode","ApiUrl","HttpMethod","RequestBody","RequestHeaders","ExpectedStatusCode","ExpectedResponseContains","ExePath","ExeArguments","FileCheckFolder","FileNamePattern","FileWaitSeconds" };
        for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Row(1).Style.Font.Bold = true;
        void R(int r, params (string col, object v)[] vals) { foreach (var (c, v) in vals) ws.Cell(r, Array.IndexOf(h, c) + 1).Value = XLCellValue.FromObject(v); }
        R(2, ("Id",1),("Name","Run ETL Job"),("Description","Nightly ETL batch"),("Type","CtrlMJob"),("CtrlMJobName","ETL_DAILY_JOB"),("CtrlMArguments","-env PROD"),("TimeoutSeconds",120),("ExpectedExitCode",0));
        R(3, ("Id",2),("Name","Health API"),("Description","Check service is up"),("Type","ApiCall"),("ApiUrl","https://example.com/health"),("HttpMethod","GET"),("ExpectedStatusCode",200),("TimeoutSeconds",30));
        R(4, ("Id",3),("Name","Run Report EXE"),("Description","Runs ReportGen.exe"),("Type","ExeRunner"),("ExePath",@"C:\Tools\ReportGen.exe"),("ExeArguments","-date today"),("TimeoutSeconds",60),("ExpectedExitCode",0));
        R(5, ("Id",4),("Name","Generate & Verify PDF"),("Description","Run then check file"),("Type","ExeWithFileCheck"),("ExePath",@"C:\Tools\PDFExport.exe"),("ExeArguments",@"-out C:\Output"),("FileCheckFolder",@"C:\Output"),("FileNamePattern","report_*.pdf"),("FileWaitSeconds",30),("TimeoutSeconds",90));
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}

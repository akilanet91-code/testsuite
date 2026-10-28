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

    /// <summary>Reads any sheet as rows of header->value (formatted text). Works while the file is open in Excel.</summary>
    public static List<Dictionary<string, string>> ReadRows(string path, string? sheet = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = sheet != null && wb.Worksheets.Contains(sheet) ? wb.Worksheet(sheet) : wb.Worksheets.First();
        var header = ws.FirstRowUsed() ?? throw new InvalidDataException("Sheet is empty.");
        var cols = header.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString().Trim());
        var rows = new List<Dictionary<string, string>>();
        foreach (var r in ws.RowsUsed().Skip(1))
        {
            var d = new Dictionary<string, string>();
            foreach (var (col, name) in cols) d[name] = r.Cell(col).GetFormattedString().Trim();
            if (d.Values.Any(v => v.Length > 0)) rows.Add(d);
        }
        return rows;
    }

    public static void SaveTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("TestCases");
        string[] h = { "Id","Name","Description","Type","CtrlMJobName","CtrlMArguments","TimeoutSeconds","ExpectedExitCode","ApiUrl","HttpMethod","RequestBody","RequestHeaders","ExpectedStatusCode","ExpectedResponseContains","ExePath","ExeArguments","FileCheckFolder","FileNamePattern","FileWaitSeconds","SpecFile","Delimiter","HasHeader","HeaderPrefix","TrailerPrefix","HeaderDateStart","HeaderDateLength","HeaderDateFormat","ExpectedHeaderDate","TrailerCountStart","TrailerCountLength","ApiJsonPath","DbProvider","DbConnectionString","DbQuery","DbColumn","CompareMode","Tolerance","ExpectedFile","ExpectedSheet","KeyColumn","IgnoreColumns","ApiRecordsPath","ColumnMap","FieldLayout","SkipHeaderLines","SkipTrailerLines","ResponseCodePath","ExpectedResponseCode","ResponseMessagePath","ExpectedResponseMessage","MessageMatch","ResponseCatalog","ExpectNoDbRows" };
        for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Row(1).Style.Font.Bold = true;
        void R(int r, params (string col, object v)[] vals) { foreach (var (c, v) in vals) ws.Cell(r, Array.IndexOf(h, c) + 1).Value = XLCellValue.FromObject(v); }
        R(2, ("Id",1),("Name","Run ETL Job"),("Description","Nightly ETL batch"),("Type","CtrlMJob"),("CtrlMJobName","ETL_DAILY_JOB"),("CtrlMArguments","-env PROD"),("TimeoutSeconds",120),("ExpectedExitCode",0));
        R(3, ("Id",2),("Name","Health API"),("Description","Check service is up"),("Type","ApiCall"),("ApiUrl","https://example.com/health"),("HttpMethod","GET"),("ExpectedStatusCode",200),("TimeoutSeconds",30));
        R(4, ("Id",3),("Name","Run Report EXE"),("Description","Runs ReportGen.exe"),("Type","ExeRunner"),("ExePath",@"C:\Tools\ReportGen.exe"),("ExeArguments","-date today"),("TimeoutSeconds",60),("ExpectedExitCode",0));
        R(5, ("Id",4),("Name","Generate & Verify PDF"),("Description","Run then check file"),("Type","ExeWithFileCheck"),("ExePath",@"C:\Tools\PDFExport.exe"),("ExeArguments",@"-out C:\Output"),("FileCheckFolder",@"C:\Output"),("FileNamePattern","report_*.pdf"),("FileWaitSeconds",30),("TimeoutSeconds",90));
        R(6, ("Id",5),("Name","Verify generated Excel"),("Description","Check output against TestSuiteV2 spec"),("Type","ExcelVerify"),("FileCheckFolder",@"Samples"),("FileNamePattern","Generated_Sample.xlsx"),("SpecFile",@"Samples\TestSuiteV2_Spec.xlsx"),("TimeoutSeconds",60));
        R(7, ("Id",6),("Name","Verify header date + trailer count"),("Description","Check header has today's date at pos 5 len 8 and trailer count matches data rows"),
            ("Type","HeaderTrailerVerify"),("FileCheckFolder",@"Samples"),("FileNamePattern","Generated_HeaderTrailer.txt"),("FileWaitSeconds",5),
            ("HeaderPrefix","HDR"),("HeaderDateStart",5),("HeaderDateLength",8),("HeaderDateFormat","yyyyMMdd"),("ExpectedHeaderDate",""),
            ("TrailerPrefix","TRL"),("TrailerCountStart",5),("TrailerCountLength",6),("TimeoutSeconds",30));
        R(8, ("Id",7),("Name","API balance vs DB"),("Description","Balance from API must equal the database"),("Type","ApiDbVerify"),
            ("ApiUrl","https://example.com/api/accounts/1001"),("HttpMethod","GET"),("ExpectedStatusCode",200),("ApiJsonPath","data.balance"),
            ("DbProvider","SqlServer"),("DbConnectionString","Server=.;Database=Bank;Integrated Security=true;TrustServerCertificate=true"),
            ("DbQuery","SELECT Balance FROM Accounts WHERE AccountId = {api:data.accountId}"),("DbColumn","Balance"),("CompareMode","Numeric"),("Tolerance","0.01"),("TimeoutSeconds",30));
        R(9, ("Id",8),("Name","DB accounts vs Excel"),("Description","Every column from the query must match the Excel file"),("Type","DbExcelCompare"),
            ("DbProvider","SqlServer"),("DbConnectionString","Server=.;Database=Bank;Integrated Security=true;TrustServerCertificate=true"),
            ("DbQuery","SELECT * FROM Accounts ORDER BY AccountId"),("ExpectedFile",@"Samples\Expected_Accounts.xlsx"),("KeyColumn","AccountId"),
            ("CompareMode","Numeric"),("Tolerance","0.01"),("TimeoutSeconds",60));
        R(10, ("Id",9),("Name","API customers vs DB"),("Description","API field names differ from DB column names"),("Type","ApiDbCompare"),
            ("ApiUrl","https://example.com/api/customers"),("HttpMethod","GET"),("ExpectedStatusCode",200),("ApiRecordsPath","data.items"),
            ("DbProvider","SqlServer"),("DbConnectionString","Server=.;Database=Crm;Integrated Security=true;TrustServerCertificate=true"),
            ("DbQuery","SELECT CUST_ID, CUST_NM, BAL_AMT FROM CUSTOMER"),("ColumnMap","CUST_ID=id\nCUST_NM=customer.name\nBAL_AMT=balance"),
            ("KeyColumn","CUST_ID"),("CompareMode","Numeric"),("Tolerance","0.01"),("TimeoutSeconds",60));
        R(11, ("Id",10),("Name","Input file vs DB"),("Description","DB must match the fixed-position file after the app ran"),("Type","FixedFileDbCompare"),
            ("FileCheckFolder",@"C:\Input"),("FileNamePattern","accounts_*.txt"),("FileWaitSeconds",10),("SkipHeaderLines",1),("SkipTrailerLines",1),
            ("FieldLayout","ACCT_ID=1,6\nACCT_NAME=7,20\nBALANCE=27,10,dec:2\nOPEN_DT=37,8,date:yyyyMMdd\nSTATUS=45,1"),
            ("DbProvider","SqlServer"),("DbConnectionString","Server=.;Database=Bank;Integrated Security=true;TrustServerCertificate=true"),
            ("DbQuery","SELECT ACCT_ID, ACCT_NAME, BALANCE, OPEN_DT, STATUS FROM ACCOUNT ORDER BY ACCT_ID"),("KeyColumn","ACCT_ID"),
            ("CompareMode","Numeric"),("Tolerance","0.01"),("TimeoutSeconds",60));
        R(12, ("Id",11),("Name","Account not found (error case)"),("Description","Status, code and message come from predefined data; nothing may be saved"),("Type","ApiDbCompare"),
            ("ApiUrl","https://example.com/api/accounts/999"),("HttpMethod","GET"),("ExpectedStatusCode",404),
            ("ResponseCodePath","responseCode"),("ExpectedResponseCode","14"),("ResponseMessagePath","responseMessage"),("ResponseCatalog",@"Samples\ResponseCatalog.xlsx"),
            ("DbProvider","SqlServer"),("DbConnectionString","Server=.;Database=Bank;Integrated Security=true;TrustServerCertificate=true"),
            ("DbQuery","SELECT * FROM ACCOUNT WHERE ACCT_ID = 999"),("ExpectNoDbRows","Yes"),("TimeoutSeconds",30));
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}

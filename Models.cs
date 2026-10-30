using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace TestFlow;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One test scenario. Property names match the Excel column headers.</summary>
public class TestCase : Observable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "CtrlMJob";
    public string CtrlMJobName { get; set; } = "";
    public string CtrlMArguments { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public int ExpectedExitCode { get; set; }
    public string ApiUrl { get; set; } = "";
    public string HttpMethod { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string RequestHeaders { get; set; } = "";
    public int ExpectedStatusCode { get; set; }
    public string ExpectedResponseContains { get; set; } = "";
    /// <summary>Save values from the API response for later steps, one NAME=json.path per line, e.g. refNo=data.referenceNo. Use later as {var:refNo}.</summary>
    public string SaveAs { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ExeArguments { get; set; } = "";
    public string FileCheckFolder { get; set; } = "";
    public string FileNamePattern { get; set; } = "";
    public int FileWaitSeconds { get; set; }
    public string SpecFile { get; set; } = "";
    public string Delimiter { get; set; } = "";   // blank = auto-detect; any text, or tab/pipe/comma/semicolon/space/colon/tilde/caret
    public string HasHeader { get; set; } = "";   // blank/Yes = first line is a header; No = use spec column order

    // ── ApiDbVerify fields (API value vs database value) ──────────────────────
    /// <summary>Path to the value in the JSON response, e.g. data.customer.balance or items[0].id</summary>
    public string ApiJsonPath { get; set; } = "";
    /// <summary>Query returning the value to compare (first row used). May contain {api:path} placeholders, sent as SQL parameters.</summary>
    public string DbQuery { get; set; } = "";
    /// <summary>Result column to compare. Blank = first column.</summary>
    public string DbColumn { get; set; } = "";
    /// <summary>Text | TextIgnoreCase | Numeric | Date</summary>
    public string CompareMode { get; set; } = "";
    /// <summary>Allowed difference for Numeric compare.</summary>
    public string Tolerance { get; set; } = "";

    // ── FixedFileDbCompare fields (fixed-position file vs database) ──────────
    /// <summary>One field per line: DbColumn=start,length[,dec:N][,date:yyyyMMdd]  (start is 1-based). dec:N = N implied decimals.</summary>
    public string FieldLayout { get; set; } = "";
    /// <summary>Lines at the top of the file that are not records (header).</summary>
    public int SkipHeaderLines { get; set; }
    /// <summary>Lines at the end of the file that are not records (trailer).</summary>
    public int SkipTrailerLines { get; set; }

    // ── Record exists in database (node DbRecordCheck, or pre-check of ApiDbCompare) ──
    /// <summary>Query that finds the record, e.g. SELECT 1 FROM Customer WHERE CustNo = 'C001'. SELECT COUNT(*) also works.</summary>
    public string DbCheckQuery { get; set; } = "";
    /// <summary>Exists (default) | NotExists</summary>
    public string DbCheckExpect { get; set; } = "";
    /// <summary>Before (default) | After the API call (After allows {api:path} in the query and in the values).</summary>
    public string DbCheckWhen { get; set; } = "";
    /// <summary>Expected column values of the record, one COLUMN=value per line. Special values: {notnull} {null} {today} {any} {api:path}</summary>
    public string ExpectedValues { get; set; } = "";

    // ── Predefined response data (code / message) ────────────────────────────
    /// <summary>Where the business response code is in the JSON body, e.g. responseCode or header.code</summary>
    public string ResponseCodePath { get; set; } = "";
    /// <summary>The code the response must contain, e.g. 00</summary>
    public string ExpectedResponseCode { get; set; } = "";
    /// <summary>Where the response message is in the JSON body. Blank = the whole body is the message.</summary>
    public string ResponseMessagePath { get; set; } = "";
    /// <summary>The predefined message the response must have. Blank + ResponseCatalog = looked up by code.</summary>
    public string ExpectedResponseMessage { get; set; } = "";
    /// <summary>Equals (default) | EqualsIgnoreCase | Contains</summary>
    public string MessageMatch { get; set; } = "";
    /// <summary>Excel file with the predefined messages: columns Code, Message.</summary>
    public string ResponseCatalog { get; set; } = "";
    /// <summary>Yes = the DbQuery must return NO rows (error case: nothing may be saved).</summary>
    public string ExpectNoDbRows { get; set; } = "";

    // ── ApiDbCompare fields (many API fields vs many DB columns; names may differ) ──
    /// <summary>Where the list of records is in the response, e.g. data.items. Blank = the response itself (a list, or one object).</summary>
    public string ApiRecordsPath { get; set; } = "";
    /// <summary>One pair per line: DbColumn=api.path  (e.g. CUST_NM=customer.name). Blank = columns and fields have the same names.</summary>
    public string ColumnMap { get; set; } = "";

    // ── DbExcelCompare fields (all DB columns vs an Excel file) ──────────────
    /// <summary>Excel file holding the expected data (first row = column headers matching the query's column names).</summary>
    public string ExpectedFile { get; set; } = "";
    public string ExpectedSheet { get; set; } = "";
    /// <summary>Column that identifies a row (e.g. Id). Blank = compare row 1 with row 1, row 2 with row 2, ...</summary>
    public string KeyColumn { get; set; } = "";
    /// <summary>Comma separated columns to skip, e.g. UpdatedAt,CreatedBy</summary>
    public string IgnoreColumns { get; set; } = "";

    // ── HeaderTrailerVerify fields ─────────────────────────────────────────────
    /// <summary>Expected prefix on the first (header) line, e.g. "HDR". Leave blank to skip prefix check.</summary>
    public string HeaderPrefix { get; set; } = "";
    /// <summary>Expected prefix on the last (trailer) line, e.g. "TRL". Leave blank to skip prefix check.</summary>
    public string TrailerPrefix { get; set; } = "";
    /// <summary>1-based start position of the date value in the header line.</summary>
    public int HeaderDateStart { get; set; }
    /// <summary>Number of characters occupied by the date value in the header line.</summary>
    public int HeaderDateLength { get; set; }
    /// <summary>DateTime.ParseExact format for the header date, e.g. "yyyyMMdd". Leave blank to auto-detect.</summary>
    public string HeaderDateFormat { get; set; } = "";
    /// <summary>The date value the header must contain. Leave blank to use today's date.</summary>
    public string ExpectedHeaderDate { get; set; } = "";
    /// <summary>1-based start position of the record-count value in the trailer line.</summary>
    public int TrailerCountStart { get; set; }
    /// <summary>Number of characters occupied by the record-count value in the trailer line.</summary>
    public int TrailerCountLength { get; set; }

    string _status = "Pending", _message = ""; long _duration;
    public static event Action? StatusChanged;
    public string Status { get => _status; set { _status = value; Raise(); Raise(nameof(Details)); StatusChanged?.Invoke(); } }
    public long Duration { get => _duration; set { _duration = value; Raise(); Raise(nameof(DurationText)); } }
    public string Message { get => _message; set { _message = value; Raise(); Raise(nameof(Details)); } }
    [JsonIgnore] public string DurationText => $"{Duration}ms";

    [JsonIgnore] public string Icon => Type.Trim().ToLowerInvariant() switch
    {
        "ctrlmjob" => "⚙", "exerunner" => "▶", "exewithfilecheck" => "📄",
        "apicall" or "api" or "restapi" => "🌐", "filecheck" => "📄",
        "excelverify" or "fileverify" => "✔",
        "headertrailerverify" => "🗂", "apidbverify" => "⇄", "dbexcelcompare" => "🗃", "dbrecordcheck" => "🔎", "apidbcompare" => "🔀", "fixedfiledbcompare" => "🧾", _ => "•"
    };

    [JsonIgnore] public string Details
    {
        get
        {
            var sb = new StringBuilder();
            void A(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{k}: {v}"); }
            A("Type", Type); A("Control-M job", CtrlMJobName); A("Arguments", CtrlMArguments);
            A("Exe", ExePath); A("Exe args", ExeArguments);
            A("URL", ApiUrl); A("Method", HttpMethod); A("Body", RequestBody); A("Headers", RequestHeaders);
            A("Expected status", ExpectedStatusCode > 0 ? ExpectedStatusCode.ToString() : "");
            A("Expected contains", ExpectedResponseContains); A("Save as", SaveAs);
            A("Folder", FileCheckFolder); A("Pattern", FileNamePattern); A("Spec file", SpecFile); A("Delimiter", Delimiter); A("Has header", HasHeader);
            A("API path", ApiJsonPath); A("DB query", DbQuery); A("DB column", DbColumn); A("Compare", CompareMode); A("Tolerance", Tolerance);
            A("DB check query", DbCheckQuery); A("DB check when", DbCheckWhen); A("Expected values", ExpectedValues); A("DB check expects", DbCheckExpect); A("Response code path", ResponseCodePath); A("Expected code", ExpectedResponseCode); A("Response message path", ResponseMessagePath); A("Expected message", ExpectedResponseMessage); A("Message match", MessageMatch); A("Response catalog", ResponseCatalog); A("Expect no DB rows", ExpectNoDbRows);
            A("Field layout", FieldLayout); A("Skip header lines", SkipHeaderLines > 0 ? SkipHeaderLines.ToString() : ""); A("Skip trailer lines", SkipTrailerLines > 0 ? SkipTrailerLines.ToString() : ""); A("Records path", ApiRecordsPath); A("Column map", ColumnMap); A("Excel file", ExpectedFile); A("Sheet", ExpectedSheet); A("Key column", KeyColumn); A("Ignore columns", IgnoreColumns);
            A("Header prefix", HeaderPrefix); A("Trailer prefix", TrailerPrefix);
            if (HeaderDateStart > 0) A("Header date position", $"start {HeaderDateStart}, length {HeaderDateLength}" + (HeaderDateFormat != "" ? $" ({HeaderDateFormat})" : ""));
            A("Expected header date", ExpectedHeaderDate);
            if (TrailerCountStart > 0) A("Trailer count position", $"start {TrailerCountStart}, length {TrailerCountLength}");
            A("Timeout (s)", TimeoutSeconds > 0 ? TimeoutSeconds.ToString() : "");
            A("Expected exit code", ExpectedExitCode.ToString());
            if (!string.IsNullOrEmpty(Message)) sb.AppendLine("\nResult: " + Message);
            return sb.ToString();
        }
    }
}

public class FlowNode
{
    public int Id { get; set; }
    public string Kind { get; set; } = "CtrlMJob";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [JsonIgnore] public string LastRun { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, string> Props { get; set; } = new();
    // Advanced settings
    public int Retry { get; set; } = 0;
    public int RetryDelayMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 120000;
    public string RunCondition { get; set; } = "";   // "" | contains "passed" | contains "failed" (refers to previous node)
    public string OnFailure { get; set; } = "Stop flow"; // "Stop flow" | "Continue"
    [JsonIgnore] public string Status { get; set; } = "";
    // Each node keeps its OWN Excel test cases
    public string ExcelFile { get; set; } = "";
    public ObservableCollection<TestCase> Cases { get; set; } = new();
}
public class FlowEdge { public int From { get; set; } public int To { get; set; } }
public class FlowDoc { public List<FlowNode> Nodes { get; set; } = new(); public List<FlowEdge> Edges { get; set; } = new(); }

public static class Kinds
{
    public static readonly Dictionary<string, (string Title, string Icon, string[] Fields)> All = new()
    {
        ["CtrlMJob"]  = ("Control-M job", "⚙", new[] { "CtrlMJobName", "CtrlMArguments", "ExpectedExitCode" }),
        ["ExeRunner"] = ("Run EXE", "▶", new[] { "ExePath", "ExeArguments", "ExpectedExitCode", "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
        ["ApiCall"]   = ("REST request", "🌐", new[] { "ApiUrl", "HttpMethod", "RequestBody", "RequestHeaders", "ExpectedStatusCode", "ExpectedResponseContains", "SaveAs" }),
        ["FileCheck"] = ("Check file", "📄", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
        ["ExcelVerify"] = ("Verify file vs spec (xlsx/csv/txt)", "✔", new[] { "FileCheckFolder", "FileNamePattern", "SpecFile", "Delimiter", "HasHeader" }),
        ["HeaderTrailerVerify"] = ("Verify header date + trailer count", "🗂", new[]
        {
            "FileCheckFolder", "FileNamePattern", "FileWaitSeconds",
            "HeaderPrefix", "HeaderDateStart", "HeaderDateLength", "HeaderDateFormat", "ExpectedHeaderDate",
            "TrailerPrefix", "TrailerCountStart", "TrailerCountLength"
        }),
        ["ApiDbVerify"] = ("Compare API value vs database", "⇄", new[]
        {
            "ApiUrl", "HttpMethod", "RequestHeaders", "RequestBody", "ExpectedStatusCode", "SaveAs", "ApiJsonPath",
            "DbQuery", "DbColumn", "CompareMode", "Tolerance"
        }),
        ["DbExcelCompare"] = ("Compare database rows vs Excel", "🗃", new[]
        {
            "DbQuery", "ExpectedFile", "ExpectedSheet", "KeyColumn", "IgnoreColumns", "CompareMode", "Tolerance"
        }),
        ["DbRecordCheck"] = ("Check record in database (exists, values)", "🔎", new[] { "DbCheckQuery", "DbCheckExpect", "ExpectedValues", "CompareMode", "Tolerance" }),
        ["ApiDbCompare"] = ("Check API response: code, message, database", "🔀", new[]
        {
            "DbCheckQuery", "DbCheckExpect", "DbCheckWhen", "ExpectedValues",
            "ApiUrl", "HttpMethod", "RequestHeaders", "RequestBody", "ExpectedStatusCode", "SaveAs",
            "ResponseCodePath", "ExpectedResponseCode", "ResponseMessagePath", "ExpectedResponseMessage", "MessageMatch", "ResponseCatalog", "ExpectedResponseContains",
            "ApiRecordsPath", "DbQuery", "ExpectNoDbRows", "ColumnMap", "KeyColumn", "CompareMode", "Tolerance"
        }),
        ["FixedFileDbCompare"] = ("Compare fixed-width file vs database", "🧾", new[]
        {
            "FileCheckFolder", "FileNamePattern", "FileWaitSeconds", "SkipHeaderLines", "SkipTrailerLines", "FieldLayout",
            "DbQuery", "KeyColumn", "CompareMode", "Tolerance"
        }),
        ["TestSuite"] = ("Test suite (Excel)", "📋", Array.Empty<string>()),
    };
    public static string DefaultDesc(string kind) => kind switch
    {
        "CtrlMJob" => "Run a Control-M job and check its exit code",
        "ExeRunner" => "Launch an executable and check the result",
        "ApiCall" => "Send request and validate response",
        "FileCheck" => "Wait for a file to appear in a folder",
        "ExcelVerify" => "Validate a generated file against the spec",
        "HeaderTrailerVerify" => "Check header date and trailer record count",
        "ApiDbVerify" => "Call an API and cross-check a response value with the database",
        "DbExcelCompare" => "Compare every column returned by a query with an Excel file",
        "DbRecordCheck" => "Check that a record is in the database (or is not) and holds the expected values from Excel",
        "ApiDbCompare" => "Check status, response code and message against predefined data, and compare the response with the database",
        "FixedFileDbCompare" => "Check that the DB was updated exactly as the fixed-position file says",
        "TestSuite" => "Run the test cases imported from Excel",
        _ => ""
    };

    public static string Label(string f) => f switch
    {
        "CtrlMJobName" => "Job name", "CtrlMArguments" => "Arguments",
        "ExePath" => "Executable (.exe)", "ExeArguments" => "Arguments",
        "ExpectedExitCode" => "Expected exit code",
        "ApiUrl" => "URL", "HttpMethod" => "Method", "RequestBody" => "Body", "RequestHeaders" => "Headers",
        "ExpectedStatusCode" => "Expected status code", "ExpectedResponseContains" => "Response contains",
        "SaveAs" => "Save from response: NAME=json.path (one per line). Use later as {var:NAME}",
        "FileCheckFolder" => "Folder", "FileNamePattern" => "File name pattern", "FileWaitSeconds" => "Wait (seconds)",
        "SpecFile" => "Spec file (.xlsx)", "Delimiter" => "Delimiter (blank = auto-detect)", "HasHeader" => "Has header (Yes / No)",
        "ExpectedFile" => "Excel file with expected data (.xlsx)", "ExpectedSheet" => "Sheet name (blank = first sheet)",
        "KeyColumn" => "Key column to match rows (blank = by row order)", "IgnoreColumns" => "Columns to ignore (comma separated)",
        "FieldLayout" => "Field layout: DbColumn=start,length (one per line)",
        "SkipHeaderLines" => "Header lines to skip", "SkipTrailerLines" => "Trailer lines to skip",
        "ResponseCodePath" => "Response code path (e.g. responseCode)", "ExpectedResponseCode" => "Expected response code (e.g. 00)",
        "ResponseMessagePath" => "Response message path (blank = whole body)", "ExpectedResponseMessage" => "Expected message (predefined text)",
        "MessageMatch" => "Message match (Equals / EqualsIgnoreCase / Contains)", "ResponseCatalog" => "Message catalog (.xlsx with Code, Message)",
        "ExpectNoDbRows" => "Expect NO database rows (Yes / No)",
        "DbCheckQuery" => "Record query (e.g. SELECT 1 FROM T WHERE Id = 'C001')", "DbCheckExpect" => "Record must be: Exists / NotExists", "DbCheckWhen" => "Run the DB check: Before / After the API call",
        "ExpectedValues" => "Expected values: COLUMN=value (one per line)",
        "ApiRecordsPath" => "Records path in response (blank = whole response)",
        "ColumnMap" => "Column map: DbColumn=api.path (one per line)",
        "ApiJsonPath" => "Value in response (e.g. data.balance or items[0].id)",
        "DbQuery" => "SQL query (use {api:data.id} to pass an API value)", "DbColumn" => "Result column (blank = first)",
        "CompareMode" => "Compare as (Text / TextIgnoreCase / Numeric / Date)", "Tolerance" => "Numeric tolerance (e.g. 0.01)",
        "HeaderPrefix"       => "Header line prefix (e.g. HDR)",
        "TrailerPrefix"      => "Trailer line prefix (e.g. TRL)",
        "HeaderDateStart"    => "Header date — start position (1-based)",
        "HeaderDateLength"   => "Header date — length (chars)",
        "HeaderDateFormat"   => "Header date format (blank = auto, e.g. yyyyMMdd)",
        "ExpectedHeaderDate" => "Expected header date (blank = today)",
        "TrailerCountStart"  => "Trailer count — start position (1-based)",
        "TrailerCountLength" => "Trailer count — length (chars)",
        _ => f
    };

    /// <summary>Groups a node's fields into titled sections for the settings panel.</summary>
    public static (string, string[])[] Layout(string kind) => kind switch
    {
        "CtrlMJob" => new[] { ("JOB", new[] { "CtrlMJobName", "CtrlMArguments" }), ("EXPECTED RESULT", new[] { "ExpectedExitCode" }) },
        "ExeRunner" => new[] { ("COMMAND LINE", new[] { "ExePath", "ExeArguments" }), ("EXPECTED RESULT", new[] { "ExpectedExitCode" }),
                               ("OUTPUT FILE CHECK (optional)", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }) },
        "ApiCall" => new[] { ("REQUEST", new[] { "ApiUrl", "HttpMethod", "RequestHeaders", "RequestBody" }),
                             ("EXPECTED RESPONSE", new[] { "ExpectedStatusCode", "ExpectedResponseContains" }),
                             ("SAVE FOR LATER STEPS", new[] { "SaveAs" }) },
        "FileCheck" => new[] { ("FILE", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }) },
        "ExcelVerify" => new[] { ("GENERATED FILE", new[] { "FileCheckFolder", "FileNamePattern", "Delimiter", "HasHeader" }), ("SPECIFICATION", new[] { "SpecFile" }) },
        "HeaderTrailerVerify" => new[]
        {
            ("GENERATED FILE",   new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
            ("HEADER RECORD",    new[] { "HeaderPrefix", "HeaderDateStart", "HeaderDateLength", "HeaderDateFormat", "ExpectedHeaderDate" }),
            ("TRAILER RECORD",   new[] { "TrailerPrefix", "TrailerCountStart", "TrailerCountLength" }),
        },
        "FixedFileDbCompare" => new[]
        {
            ("INPUT FILE (FIXED POSITION)", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds", "SkipHeaderLines", "SkipTrailerLines" }),
            ("FIELD POSITIONS", new[] { "FieldLayout", "KeyColumn" }),
            ("DATABASE (AFTER THE APP RAN)", new[] { "DbQuery" }),
            ("COMPARE", new[] { "CompareMode", "Tolerance" }),
        },
        "DbRecordCheck" => new[]
        {
            ("RECORD IN DATABASE", new[] { "DbCheckQuery", "DbCheckExpect" }),
            ("EXPECTED VALUES (FROM EXCEL)", new[] { "ExpectedValues", "CompareMode", "Tolerance" }),
        },
        "ApiDbCompare" => new[]
        {
            ("DATABASE CHECK (RECORD + EXPECTED VALUES)", new[] { "DbCheckQuery", "DbCheckExpect", "DbCheckWhen", "ExpectedValues" }),
            ("API REQUEST", new[] { "ApiUrl", "HttpMethod", "RequestHeaders", "RequestBody", "ExpectedStatusCode", "SaveAs" }),
            ("EXPECTED RESPONSE (PREDEFINED DATA)", new[] { "ResponseCodePath", "ExpectedResponseCode", "ResponseMessagePath", "ExpectedResponseMessage", "MessageMatch", "ResponseCatalog", "ExpectedResponseContains" }),
            ("RECORDS IN RESPONSE", new[] { "ApiRecordsPath" }),
            ("DATABASE (OPTIONAL)", new[] { "DbQuery", "ExpectNoDbRows" }),
            ("COLUMN MAPPING", new[] { "ColumnMap", "KeyColumn" }),
            ("COMPARE", new[] { "CompareMode", "Tolerance" }),
        },
        "DbExcelCompare" => new[]
        {
            ("DATABASE", new[] { "DbQuery" }),
            ("EXCEL (EXPECTED DATA)", new[] { "ExpectedFile", "ExpectedSheet", "KeyColumn" }),
            ("COMPARE", new[] { "IgnoreColumns", "CompareMode", "Tolerance" }),
        },
        "ApiDbVerify" => new[]
        {
            ("API REQUEST", new[] { "ApiUrl", "HttpMethod", "RequestHeaders", "RequestBody", "ExpectedStatusCode", "SaveAs" }),
            ("VALUE FROM API", new[] { "ApiJsonPath" }),
            ("DATABASE", new[] { "DbQuery", "DbColumn" }),
            ("COMPARE", new[] { "CompareMode", "Tolerance" }),
        },
        _ => Array.Empty<(string, string[])>()
    };

    public static string Default(string f) => f switch
    {
        "ExpectedExitCode" => "0", "ExpectedStatusCode" => "200", "HttpMethod" => "GET", "FileWaitSeconds" => "30", "SkipHeaderLines" => "0", "SkipTrailerLines" => "0", "HasHeader" => "Yes",
        "CompareMode" => "Text", "DbCheckExpect" => "Exists", "DbCheckWhen" => "Before", "Tolerance" => "0", _ => ""
    };
}

public static class AppState
{
    /// <summary>Default list used when the Test Runner is not bound to a node.</summary>
    public static ObservableCollection<TestCase> Cases { get; } = new();
    public static event Action? CasesImported;
    public static void NotifyImported() => CasesImported?.Invoke();

    /// <summary>Asks for an Excel file and loads it into <paramref name="target"/> (replacing only that list). Returns the file name or null.</summary>
    public static string? ImportInto(ObservableCollection<TestCase> target)
    {
        var dlg = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Import test cases" };
        if (dlg.ShowDialog() != true) return null;
        try
        {
            var list = ExcelIO.Load(dlg.FileName);
            target.Clear();
            foreach (var c in list) target.Add(c);
            return System.IO.Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex) { MessageBox.Show("Import failed: " + ex.Message, "TestFlow"); return null; }
    }
}

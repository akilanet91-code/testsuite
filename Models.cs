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
    public string ExePath { get; set; } = "";
    public string ExeArguments { get; set; } = "";
    public string FileCheckFolder { get; set; } = "";
    public string FileNamePattern { get; set; } = "";
    public int FileWaitSeconds { get; set; }
    public string SpecFile { get; set; } = "";
    public string Delimiter { get; set; } = "";   // blank = auto-detect; any text, or tab/pipe/comma/semicolon/space/colon/tilde/caret
    public string HasHeader { get; set; } = "";   // blank/Yes = first line is a header; No = use spec column order

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

    [JsonIgnore]
    public string Icon => Type.Trim().ToLowerInvariant() switch
    {
        "ctrlmjob" => "⚙",
        "exerunner" => "▶",
        "exewithfilecheck" => "📄",
        "apicall" or "api" or "restapi" => "🌐",
        "filecheck" => "📄",
        "excelverify" or "fileverify" => "✔",
        "headertrailerverify" => "🗂",
        _ => "•"
    };

    [JsonIgnore]
    public string Details
    {
        get
        {
            var sb = new StringBuilder();
            void A(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{k}: {v}"); }
            A("Type", Type); A("Control-M job", CtrlMJobName); A("Arguments", CtrlMArguments);
            A("Exe", ExePath); A("Exe args", ExeArguments);
            A("URL", ApiUrl); A("Method", HttpMethod); A("Body", RequestBody); A("Headers", RequestHeaders);
            A("Expected status", ExpectedStatusCode > 0 ? ExpectedStatusCode.ToString() : "");
            A("Expected contains", ExpectedResponseContains);
            A("Folder", FileCheckFolder); A("Pattern", FileNamePattern); A("Spec file", SpecFile); A("Delimiter", Delimiter); A("Has header", HasHeader);
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
        ["CtrlMJob"] = ("Control-M job", "⚙", new[] { "CtrlMJobName", "CtrlMArguments", "ExpectedExitCode" }),
        ["ExeRunner"] = ("Run EXE", "▶", new[] { "ExePath", "ExeArguments", "ExpectedExitCode", "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
        ["ApiCall"] = ("REST request", "🌐", new[] { "ApiUrl", "HttpMethod", "RequestBody", "RequestHeaders", "ExpectedStatusCode", "ExpectedResponseContains" }),
        ["FileCheck"] = ("Check file", "📄", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
        ["ExcelVerify"] = ("Verify file vs spec (xlsx/csv/txt)", "✔", new[] { "FileCheckFolder", "FileNamePattern", "SpecFile", "Delimiter", "HasHeader" }),
        ["HeaderTrailerVerify"] = ("Verify header date + trailer count", "🗂", new[]
        {
            "FileCheckFolder", "FileNamePattern", "FileWaitSeconds",
            "HeaderPrefix", "HeaderDateStart", "HeaderDateLength", "HeaderDateFormat", "ExpectedHeaderDate",
            "TrailerPrefix", "TrailerCountStart", "TrailerCountLength"
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
        "TestSuite" => "Run the test cases imported from Excel",
        _ => ""
    };

    public static string Label(string f) => f switch
    {
        "CtrlMJobName" => "Job name",
        "CtrlMArguments" => "Arguments",
        "ExePath" => "Executable (.exe)",
        "ExeArguments" => "Arguments",
        "ExpectedExitCode" => "Expected exit code",
        "ApiUrl" => "URL",
        "HttpMethod" => "Method",
        "RequestBody" => "Body",
        "RequestHeaders" => "Headers",
        "ExpectedStatusCode" => "Expected status code",
        "ExpectedResponseContains" => "Response contains",
        "FileCheckFolder" => "Folder",
        "FileNamePattern" => "File name pattern",
        "FileWaitSeconds" => "Wait (seconds)",
        "SpecFile" => "Spec file (.xlsx)",
        "Delimiter" => "Delimiter (blank = auto-detect)",
        "HasHeader" => "Has header (Yes / No)",
        "HeaderPrefix" => "Header line prefix (e.g. HDR)",
        "TrailerPrefix" => "Trailer line prefix (e.g. TRL)",
        "HeaderDateStart" => "Header date — start position (1-based)",
        "HeaderDateLength" => "Header date — length (chars)",
        "HeaderDateFormat" => "Header date format (blank = auto, e.g. yyyyMMdd)",
        "ExpectedHeaderDate" => "Expected header date (blank = today)",
        "TrailerCountStart" => "Trailer count — start position (1-based)",
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
                             ("EXPECTED RESPONSE", new[] { "ExpectedStatusCode", "ExpectedResponseContains" }) },
        "FileCheck" => new[] { ("FILE", new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }) },
        "ExcelVerify" => new[] { ("GENERATED FILE", new[] { "FileCheckFolder", "FileNamePattern", "Delimiter", "HasHeader" }), ("SPECIFICATION", new[] { "SpecFile" }) },
        "HeaderTrailerVerify" => new[]
        {
            ("GENERATED FILE",   new[] { "FileCheckFolder", "FileNamePattern", "FileWaitSeconds" }),
            ("HEADER RECORD",    new[] { "HeaderPrefix", "HeaderDateStart", "HeaderDateLength", "HeaderDateFormat", "ExpectedHeaderDate" }),
            ("TRAILER RECORD",   new[] { "TrailerPrefix", "TrailerCountStart", "TrailerCountLength" }),
        },
        _ => Array.Empty<(string, string[])>()
    };

    public static string Default(string f) => f switch
    {
        "ExpectedExitCode" => "0",
        "ExpectedStatusCode" => "200",
        "HttpMethod" => "GET",
        "FileWaitSeconds" => "30",
        "HasHeader" => "Yes",
        _ => ""
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

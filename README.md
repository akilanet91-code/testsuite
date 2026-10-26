# TestFlow (WPF, .NET 8)

Requirements: Windows, .NET 8 SDK (or Visual Studio 2022 17.8+).

    dotnet run

## Windows
- **Test Runner** (main window): Import Excel, Run All / Run Selected / Stop, status counts, detail panel, Export Report (CSV), Template (blank Excel with sample rows), Open Flow.
- **Flow canvas**: add nodes from the palette, drag, connect (right dot -> left dot). Click a node to open Properties / Advanced settings. Advanced has retry count, delay, timeout, run condition, on-failure and Import Excel.
  The "Run imported test cases" node runs every imported scenario inside the flow.

## Excel columns (each node has its own imported file)
Id, Name, Description, Type (CtrlMJob | ExeRunner | ExeWithFileCheck | ApiCall | FileCheck), CtrlMJobName, CtrlMArguments,
TimeoutSeconds, ExpectedExitCode, ApiUrl, HttpMethod, RequestBody, RequestHeaders, ExpectedStatusCode,
ExpectedResponseContains, ExePath, ExeArguments, FileCheckFolder, FileNamePattern, FileWaitSeconds

## Control-M
Runner.CtrlMCommand (Runner.cs) is the CLI that is started as: <CtrlMCommand> "<CtrlMJobName> <CtrlMArguments>".
Point it at your site's command or wrapper script.

## Verify generated Excel against a spec (ML)
Node / Type `ExcelVerify`: finds the newest file matching FileNamePattern in FileCheckFolder and checks it against the
spec workbook in SpecFile (sheet "Mapping": TargetColumn, SourceColumn, DataType, Required, Pattern, ExpectedValue).
Rules are checked in plain C#; ML.NET (char n-gram similarity) is used only to suggest headers that do not match exactly,
and those are reported as "please confirm". Relative paths resolve from the exe folder.
Try it: import Samples\TestCases_ExcelVerify.xlsx and run it - Samples\Generated_Sample.xlsx has deliberate errors.
Chain it after a "Run EXE" node so the exe creates the file first.

### Generated text files (any delimiter)
ExcelVerify checks Excel, fixed-width text and ANY delimited text. Test-case columns:
- Delimiter: blank = auto-detect (, tab | ; ~ ^ : # space); or tab / pipe / comma / semicolon / space / colon / tilde / caret,
  or any literal text, including several characters such as ~|~ . (Excel trims cells, so write a space delimiter as "space".)
- HasHeader: blank or Yes = first line is the header; No = columns are taken from the spec Mapping sheet, in row order.
Lines with a different field count than the header are reported as "Format" issues (usually a wrong delimiter).
Fixed-width: put numeric Start (1-based) and Length columns in the spec Mapping sheet.
Limit: a quoted value cannot span several lines.

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

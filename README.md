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

## Compare an API value with the database
Node / Type `ApiDbVerify` (palette: "Compare API value vs database"). It calls the API, reads one value from the JSON response,
runs a SQL query and checks that both values are equal.
- `ApiJsonPath`: where the value is in the response, e.g. `data.balance` or `items[0].id`.
- `DbProvider`: the ADO.NET provider: `SqlServer` (default), `Oracle`, `PostgreSql`, `MySql` or `Sqlite`. `DbConnectionString`: connection string for that provider.
- `DbQuery`: SQL returning the value (first row used). Use `{api:path}` to pass a value from the response into the query,
  e.g. `SELECT Balance FROM Accounts WHERE AccountId = {api:data.accountId}`. It is sent as a SQL parameter, not pasted into the text.
- `DbColumn`: column to compare (blank = first column).
- `CompareMode`: `Text` (default), `TextIgnoreCase`, `Numeric` (with `Tolerance`), or `Date` (date part only).
Use a read-only database account. The connection string is saved in the flow file in plain text.

## Compare all database columns with an Excel file
Node / Type `DbExcelCompare` ("Compare database rows vs Excel"). It runs `DbQuery` (any number of rows and columns) and compares EVERY
column of every row with the Excel file in `ExpectedFile`. Excel headers must match the query's column names (case does not matter).
- `KeyColumn`: column that identifies a row (e.g. AccountId). Rows are paired by it. Blank = compare by row order.
- `IgnoreColumns`: comma separated columns to skip (e.g. UpdatedAt,CreatedBy).
- Numbers are compared as numbers (`Tolerance` = allowed difference), dates as dates, other values as text (`CompareMode` Text / TextIgnoreCase forces text).
- Reported issues: wrong value (DB vs Excel), row only in DB, row only in Excel, column missing in Excel.
Try it: import `Samples\TestCases_DbExcelCompare.xlsx` (the query uses inline values, so it works on any SQL Server; row 3 has a deliberate error).

## Compare API response fields with database columns (names can differ)
Node / Type `ApiDbCompare` ("Compare API response vs database (many fields)").
- `ApiRecordsPath`: where the records are in the response, e.g. `data.items`. Blank = the response itself (a list, or a single object).
- `DbQuery`: SQL returning the rows to check. `{api:path}` passes a value from the response root as a SQL parameter (handy for a single-record API).
- `ColumnMap`: one `DbColumn=api.path` pair per line, e.g. `CUST_NM=customer.name`. Blank = every DB column is compared with the API field of the same name.
- `KeyColumn`: DB column that identifies a row (must be in `ColumnMap`), so rows are paired by key. Blank = by row order.
- Numbers compare as numbers (`Tolerance`), dates as dates, others as text (`CompareMode` Text / TextIgnoreCase forces text).
- Reported: wrong values, rows only in DB or only in the API, and API fields that are not found.
Try it: import `Samples\TestCases_ApiDbCompare.xlsx`.

## Console app processed a fixed-position file and updated the DB: check the DB against the file
Node / Type `FixedFileDbCompare` ("Compare fixed-width file vs database"). Chain it after a "Run EXE" node that starts your console app.
It reads the newest file matching `FileNamePattern` in `FileCheckFolder`, cuts each record by position and compares every field with the
database rows returned by `DbQuery` (run after the app finished).
- `SkipHeaderLines` / `SkipTrailerLines`: lines at the start / end of the file that are not records.
- `FieldLayout`: one field per line: `DbColumn=start,length` (start is 1-based), e.g. `ACCT_NAME=7,20`.
  Options: `dec:N` = the value has N implied decimals (`0000010050` with `dec:2` = 100.50); `date:yyyyMMdd` = convert the text to a date.
- `KeyColumn`: DB column (must be in `FieldLayout`) that identifies a record, so rows are paired by key. Blank = by order.
- Numbers compare as numbers (`Tolerance`), dates as dates, others as text. Spaces around a value are trimmed.
- Reported: wrong values (file vs DB), records only in the file, rows only in the DB, lines shorter than the layout.
Try it: import `Samples\TestCases_FixedFileDb.xlsx` (uses `Samples\Accounts_Input.txt`).

## Database providers (ADO.NET, no ODBC)
All database nodes use native ADO.NET providers, chosen with `DbProvider`:

| DbProvider | NuGet package | Connection string example |
|---|---|---|
| SqlServer (default) | Microsoft.Data.SqlClient | `Server=.;Database=Bank;Integrated Security=true;TrustServerCertificate=true` |
| Oracle | Oracle.ManagedDataAccess.Core | `User Id=app;Password=pw;Data Source=dbhost:1521/ORCLPDB1` |
| PostgreSql | Npgsql | `Host=dbhost;Database=bank;Username=app;Password=pw` |
| MySql | MySqlConnector | `Server=dbhost;Database=bank;User=app;Password=pw` |
| Sqlite | Microsoft.Data.Sqlite | `Data Source=C:\data\bank.db` |

Parameters from `{api:path}` use `@p0`, `@p1`... (Oracle: `:p0`, `:p1`...) automatically. Remove the packages you do not need from `TestFlow.csproj`
only together with the matching line in `DbProviders.cs`.

## Check API responses against predefined data (code, message) and the database
Node / Type `ApiDbCompare` ("Check API response: code, message, database"). One test case = one scenario (success, validation error, not found...), so list every response your API can give.
Each check only runs when its setting is filled in:
- **HTTP status**: `ExpectedStatusCode`. Optional `ExpectedResponseContains` (text anywhere in the body).
- **Response code**: `ResponseCodePath` (where it is in the JSON, e.g. `header.responseCode`) and `ExpectedResponseCode` (e.g. `00`).
- **Response message**: `ResponseMessagePath` (blank = the whole body) and the predefined text in `ExpectedResponseMessage`. `MessageMatch` = `Equals` (default), `EqualsIgnoreCase` or `Contains`.
- **Message catalog**: instead of typing the message in every case, point `ResponseCatalog` to an Excel file with the columns `Code`, `Message`.
  If `ExpectedResponseMessage` is blank, the message is looked up by `ExpectedResponseCode` (or by the code found in the response).
- **Database** (optional, only when `DbQuery` is filled): success case = compare response fields with columns (`ApiRecordsPath`, `ColumnMap`, `KeyColumn`, see above).
  Error case = set `ExpectNoDbRows` to `Yes`: the query must return no rows (nothing may be saved).
All results of a case are shown together, e.g. `HTTP 200 | code 00 | message "Success" (catalog) | DB: 3 row(s) vs 3 API record(s), 6 column(s), 3 fully match`.
Try it: import `Samples\TestCases_ApiResponses.xlsx` (uses `Samples\ResponseCatalog.xlsx`).

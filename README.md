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
- The database is NOT set in the Excel file or the node: it comes from `appsettings.json` (SQL Server connection string, see "Database configuration" below).
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

## Database (SQL Server only)
All database checks use SQL Server through `Microsoft.Data.SqlClient`. The connection string is in `appsettings.json`. Values from `{api:path}` in a query are sent as SQL parameters (`@p0`, `@p1`, ...) automatically.

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

## Troubleshooting: "Microsoft.Data.SqlClient is not supported on this platform"
This happens when the app finds only the reference stub of the SQL client and not the real Windows assembly.
1. Run the app from the full build folder (`bin\Debug\net8.0-windows\win-x64\` or the publish folder), not from a copy of the .exe alone. The `runtimes` folder next to it must stay.
2. Rebuild after deleting the `bin` and `obj` folders (`dotnet clean`, then `dotnet run`). The project now sets `RuntimeIdentifier` = `win-x64`, so the build copies the real assembly.
3. If you publish a single file, use `dotnet publish -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.
4. Run it on Windows. The WPF app (`net8.0-windows`) does not run on Linux or macOS.

## JSON paths (ApiJsonPath, ResponseCodePath, ResponseMessagePath, ApiRecordsPath, ColumnMap, {api:...})
Property names are not case-sensitive. Example response:
```json
{ "status": "ok", "data": { "items": [ { "id": 1, "name": "A", "tags": ["x","y"] }, { "id": 2, "name": "B" } ], "total": 2 } }
```
| Path | Result | Meaning |
|---|---|---|
| `data.items[0].name` | A | index |
| `data.items[1].name` | B | index |
| `data.items[-1].name` | B | last item |
| `data.items.name` | A | array without index: first item that has the field |
| `data.items[].name` or `[*]` | A | first item |
| `items[id=2].name` | B | first item whose `id` is 2 |
| `data.items.tags[1]` | y | array inside an array item |
| `name` | A | a bare field name that is not at the top is searched anywhere in the response |
| `items.name` | A | the first part is searched anywhere, then the rest is followed |
| `..id` | 1 | always search anywhere |
| `$.data.total` | 2 | a leading `$.` is allowed |
A list at the top of the response works the same way: `[0].title`, `title`, `[id=8].title`.
Exceptions: `ColumnMap` paths are relative to each record and are NOT searched anywhere (a missing field is reported as missing).
When a path is not found, the message lists the top-level field names to help you fix it.

## Database configuration (appsettings.json)
The connection string is not a column in the Excel test cases. All database checks read it from `appsettings.json`, which is copied next to the exe:
```json
{
  "Database": {
    "ConnectionString": "Data Source=localhost;Initial Catalog=Bank;Integrated Security=True;TrustServerCertificate=True"
  }
}
```
- The file is read on every run, so you can edit it (in the build folder) without restarting the app.
- The environment variable `TESTFLOW_DB_CONNECTION` overrides the file, which keeps the password out of it.
- Old Excel files that still have `DbProvider` or `DbConnectionString` columns still import: those columns are ignored.
- If no connection string is found, the test fails with a message that names the file to edit.

## Notes on database compare
- `{api:path}` in `DbQuery` may be written with or without quotes: `WHERE ID = {api:id}` and `WHERE ID = '{api:id}'` both work (the value is always sent as a SQL parameter).
- SQL `bit` columns are read as `true` / `false`, so they match a JSON boolean (`true`) and a `1` / `0`.
- `datetimeoffset` is read as `yyyy-MM-dd HH:mm:ss`; `binary` columns as hex text.

## Check that a record is already in the database
Two ways, both read the SQL Server connection string from `appsettings.json`:
- **Node / Type `DbRecordCheck`** ("Check record exists in database"): `DbCheckQuery` finds the record, `DbCheckExpect` is `Exists` (default) or `NotExists`.
  Put it before an API or console-app node to check that the data you need (or a duplicate you want to avoid) is in the database.
- **Pre-check inside an `ApiDbCompare` case**: fill `DbCheckQuery` / `DbCheckExpect` in the same Excel row. The check runs BEFORE the API call.
  If it fails, the case fails with "DB pre-check failed, API not called". Typical use: record C001 already exists (`Exists`), then the API must answer "already exists" (`ExpectedResponseCode` / `ExpectedResponseMessage`).
`DbCheckQuery` examples: `SELECT 1 FROM CUSTOMER WHERE CUST_NO = 'C001'` (a row means the record exists) or `SELECT COUNT(*) FROM CUSTOMER WHERE CUST_NO = 'C001'` (the number is used, 0 means not found).
Try it: import `Samples\TestCases_RecordExists.xlsx`.

## Validate that a record was inserted, with the values defined in Excel
`DbRecordCheck` (and the `DbCheck...` columns of an `ApiDbCompare` case) can also compare the record's columns with the expected values written in the Excel row:
- `DbCheckQuery`: finds the inserted record and returns its columns, e.g. `SELECT * FROM CUSTOMER WHERE CUST_NO = 'C001'`.
- `DbCheckExpect`: `Exists` (default) or `NotExists`.
- `ExpectedValues`: one `COLUMN=value` per line (Alt+Enter in the cell), e.g.
```
CUST_NAME=Ann Lee
BAL_AMT=1500.50
STATUS_CD=A
CREATED_AT={today}
REMARK={null}
ROW_ID={notnull}
```
  Special values: `{notnull}` (must have a value), `{null}` (must be NULL or empty), `{today}` (must be today's date), `{any}` (only the column must exist), `{api:path}` (value from the API response).
  Other values are compared like the other checks: numbers as numbers (`Tolerance`), dates as dates, text exactly (spaces around are ignored).
- If the query returns several rows, one of them must match every expected value.
- `DbCheckWhen` (ApiDbCompare only): `Before` (default, record check before the API call) or `After` (after the API call, so the query and the values can use `{api:path}`, e.g. the id returned by the API).
- Only the columns listed in `ExpectedValues` are checked. A listed column that the query does not return is reported with the names the query did return.
Try it: import `Samples\TestCases_InsertValidation.xlsx`.

OFFLINE BUILD
Put the NuGet packages (.nupkg files) this app needs in this folder. NuGet.config (next to TestFlow.csproj) lists it as a
package source, so dotnet restore finds them here. Restore first looks in the global cache
(%USERPROFILE%\.nuget\packages); sources are not tried in a fixed order. For a strictly offline build delete the nuget.org
line in NuGet.config, or run:  dotnet restore --source LocalPackages

Needed for Excel 97-2003 (.xls) support:
  ExcelDataReader 3.7.0   (MIT licence)   https://www.nuget.org/packages/ExcelDataReader/3.7.0  -> "Download package"
  If restore asks for it: System.Text.Encoding.CodePages (MIT licence, the version restore names)

Other packages the app already uses (ClosedXML, Microsoft.ML, Microsoft.Data.SqlClient and their dependencies) can be
copied here the same way. Easy way, on a machine with internet, once:
  dotnet restore
  then copy the *.nupkg files from  %USERPROFILE%\.nuget\packages\<name>\<version>\  into this folder.

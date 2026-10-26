using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace TestFlow;

/// <summary>Executes a test case for real: Control-M CLI, EXE, REST call, file check.</summary>
public static class Runner
{
    /// <summary>Command used for Control-M jobs. It is called as: CtrlMCommand "JobName Arguments".
    /// Change this to your site's CLI (for example a wrapper script around ctm / ctmorder / ctmpsm).</summary>
    public static string CtrlMCommand = "ctm";

    static readonly HttpClient Http = new();

    public static async Task RunAsync(TestCase tc, CancellationToken stop)
    {
        tc.Status = "Running"; tc.Message = "";
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            cts.CancelAfter(TimeSpan.FromSeconds(tc.TimeoutSeconds > 0 ? tc.TimeoutSeconds : 300));
            var (ok, msg) = await ExecuteAsync(tc, cts.Token);
            tc.Status = ok ? "Passed" : "Failed"; tc.Message = msg;
        }
        catch (OperationCanceledException) { tc.Status = "Failed"; tc.Message = stop.IsCancellationRequested ? "Stopped by user" : "Timed out"; }
        catch (Exception ex) { tc.Status = "Failed"; tc.Message = ex.Message; }
        tc.Duration = sw.ElapsedMilliseconds;
    }

    static async Task<(bool ok, string msg)> ExecuteAsync(TestCase tc, CancellationToken ct)
    {
        switch (tc.Type.Trim().ToLowerInvariant())
        {
            case "ctrlmjob":
                {
                    var (code, o) = await RunProcess(CtrlMCommand, $"{tc.CtrlMJobName} {tc.CtrlMArguments}".Trim(), ct);
                    return (code == tc.ExpectedExitCode, $"Exit code {code} (expected {tc.ExpectedExitCode}). {Short(o)}");
                }
            case "exerunner":
            case "exewithfilecheck":
                {
                    var (code, o) = await RunProcess(tc.ExePath, tc.ExeArguments, ct);
                    if (code != tc.ExpectedExitCode) return (false, $"Exit code {code} (expected {tc.ExpectedExitCode}). {Short(o)}");
                    if (!string.IsNullOrWhiteSpace(tc.FileCheckFolder))
                    {
                        var (fok, fmsg) = await CheckFile(tc, ct);
                        return (fok, $"Exit code {code}. {fmsg}");
                    }
                    return (true, $"Exit code {code}. {Short(o)}");
                }
            case "apicall": case "api": case "restapi": return await CallApi(tc, ct);
            case "filecheck": return await CheckFile(tc, ct);
            case "excelverify":
            case "fileverify":
                {
                    static string Abs(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
                    var folder = Abs(tc.FileCheckFolder); var spec = Abs(tc.SpecFile);
                    var pat = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*" : tc.FileNamePattern;
                    if (!File.Exists(spec)) return (false, "Spec file not found: " + spec);
                    var file = Directory.Exists(folder)
                        ? Directory.EnumerateFiles(folder, pat).OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault() : null;
                    if (file == null) return (false, $"Generated file not found: {pat} in {folder}");
                    var issues = await Task.Run(() => SpecVerifier.Verify(spec, file, "Mapping", tc.Delimiter, !(tc.HasHeader.Trim().ToLowerInvariant() is "no" or "n" or "false" or "0")), ct);
                    return (issues.Count == 0,
                        issues.Count == 0 ? $"{Path.GetFileName(file)} matches the spec"
                        : $"{issues.Count} issue(s): " + string.Join("; ", issues.Take(10).Select(i => $"{i.Where}: {i.Message}")));
                }
            case "headertrailerverify":
                {
                    static string Abs2(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
                    var folder2 = Abs2(tc.FileCheckFolder);
                    var pat2 = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*" : tc.FileNamePattern;

                    // Optionally wait for the file to appear
                    string? file2 = null;
                    var deadline2 = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
                    while (true)
                    {
                        if (Directory.Exists(folder2))
                            file2 = Directory.EnumerateFiles(folder2, pat2)
                                             .OrderByDescending(f => File.GetLastWriteTime(f))
                                             .FirstOrDefault();
                        if (file2 != null) break;
                        if (DateTime.UtcNow >= deadline2)
                            return (false, $"Generated file not found: {pat2} in {folder2}");
                        await Task.Delay(500, ct);
                    }

                    var opts = HeaderTrailerOptions.FromTestCase(tc);
                    var issues = await Task.Run(() => HeaderTrailerVerifier.Verify(file2, opts), ct);
                    return (issues.Count == 0,
                        issues.Count == 0
                            ? $"{Path.GetFileName(file2)}: header date ✓  trailer count ✓"
                            : $"{issues.Count} issue(s): " + string.Join("; ", issues.Take(10).Select(i => $"{i.Where}: {i.Message}")));
                }
            default: return (false, "Unknown test type: " + tc.Type);
        }
    }

    static string Short(string s) { s = s.Trim(); return s.Length > 300 ? s[..300] + "…" : s; }

    static async Task<(int code, string output)> RunProcess(string file, string args, CancellationToken ct)
    {
        if (Path.IsPathRooted(file) && !File.Exists(file)) throw new FileNotFoundException("Exe not found: " + file);
        // Run from the exe's own folder so relative output paths behave as they do when double-clicked.
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.IsPathRooted(file) ? (Path.GetDirectoryName(file) ?? "") : ""
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, (await so) + (await se));
    }

    static async Task<(bool, string)> CheckFile(TestCase tc, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, tc.FileWaitSeconds));
        var pattern = string.IsNullOrWhiteSpace(tc.FileNamePattern) ? "*" : tc.FileNamePattern;
        while (true)
        {
            if (Directory.Exists(tc.FileCheckFolder))
            {
                var f = Directory.EnumerateFiles(tc.FileCheckFolder, pattern).FirstOrDefault();
                if (f != null) return (true, "File found: " + f);
            }
            if (DateTime.UtcNow >= deadline) return (false, $"File not found: {pattern} in {tc.FileCheckFolder}");
            await Task.Delay(500, ct);
        }
    }

    static async Task<(bool, string)> CallApi(TestCase tc, CancellationToken ct)
    {
        var method = new HttpMethod(string.IsNullOrWhiteSpace(tc.HttpMethod) ? "GET" : tc.HttpMethod.Trim().ToUpperInvariant());
        using var req = new HttpRequestMessage(method, tc.ApiUrl);
        if (!string.IsNullOrWhiteSpace(tc.RequestBody)) req.Content = new StringContent(tc.RequestBody, Encoding.UTF8, "application/json");
        foreach (var line in tc.RequestHeaders.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf(':'); if (i < 1) continue;
            var k = line[..i].Trim(); var v = line[(i + 1)..].Trim();
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && req.Content != null) req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(v);
            else req.Headers.TryAddWithoutValidation(k, v);
        }
        using var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        int expected = tc.ExpectedStatusCode > 0 ? tc.ExpectedStatusCode : 200;
        bool okStatus = (int)res.StatusCode == expected;
        bool okBody = string.IsNullOrEmpty(tc.ExpectedResponseContains) || body.Contains(tc.ExpectedResponseContains);
        return (okStatus && okBody, $"HTTP {(int)res.StatusCode} (expected {expected})" + (okBody ? "" : $"; body missing “{tc.ExpectedResponseContains}”"));
    }
}

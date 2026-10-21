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
            {
                var (code, o) = await RunProcess(tc.ExePath, tc.ExeArguments, ct);
                return (code == tc.ExpectedExitCode, $"Exit code {code} (expected {tc.ExpectedExitCode}). {Short(o)}");
            }
            case "exewithfilecheck":
            {
                var (code, o) = await RunProcess(tc.ExePath, tc.ExeArguments, ct);
                if (code != tc.ExpectedExitCode) return (false, $"Exit code {code} (expected {tc.ExpectedExitCode}). {Short(o)}");
                return await CheckFile(tc, ct);
            }
            case "apicall": case "api": case "restapi": return await CallApi(tc, ct);
            case "filecheck": return await CheckFile(tc, ct);
            default: return (false, "Unknown test type: " + tc.Type);
        }
    }

    static string Short(string s) { s = s.Trim(); return s.Length > 300 ? s[..300] + "…" : s; }

    static async Task<(int code, string output)> RunProcess(string file, string args, CancellationToken ct, string? workDir = null)
    {
        if (Path.IsPathRooted(file) && !File.Exists(file))
            throw new FileNotFoundException("Exe not found: " + file);

        // same folder you were in when it worked in cmd; default = exe folder
        var dir = !string.IsNullOrWhiteSpace(workDir) ? workDir
                : Path.IsPathRooted(file) ? (Path.GetDirectoryName(file) ?? "") : "";

        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{file}\" {args}\"")   // exactly like typing it in cmd
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        p.StandardInput.Close();                       // exe gets end-of-input instead of an invalid handle
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }

        return (p.ExitCode, $"[cmd /c \"{file}\" {args} | cwd={dir}] " + (await so) + (await se));
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

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>
/// Values saved by one test case and used by the next ones (request chaining).
///   SaveAs column :  refNo=data.referenceNo        (one NAME=json.path per line, read from the API response)
///   Use later     :  {var:refNo}  in ApiUrl, RequestBody, RequestHeaders, DbQuery, DbCheckQuery, ExpectedValues, ExpectedResponseContains ...
/// The store lives for one run (Run All / Run Selected / Run Flow) and is cleared when the next run starts.
/// </summary>
public static class RunContext
{
    static readonly Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();
    static readonly Regex Rx = new(@"\{var:(\w+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fields that must never be rewritten, and fields that are SQL text (values are quote-escaped there).
    static readonly HashSet<string> NoResolve = new(StringComparer.OrdinalIgnoreCase) { "Name", "Description", "Type", "Status", "Message", "SaveAs" };
    static readonly HashSet<string> SqlFields = new(StringComparer.OrdinalIgnoreCase) { "DbQuery", "DbCheckQuery" };

    public static void Clear() { lock (Gate) Vars.Clear(); }
    public static IReadOnlyDictionary<string, string> Snapshot() { lock (Gate) return new Dictionary<string, string>(Vars); }

    public static string Resolve(string? text, bool sql = false)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Rx.Replace(text, m =>
        {
            string v;
            lock (Gate)
            {
                if (!Vars.TryGetValue(m.Groups[1].Value, out var found))
                    throw new InvalidOperationException(
                        $"Variable “{m.Groups[1].Value}” has no value. The step that saves it (SaveAs) did not run or failed. Saved so far: {(Vars.Count == 0 ? "nothing" : string.Join(", ", Vars.Keys))}");
                v = found;
            }
            return sql ? v.Replace("'", "''") : v;
        });
    }

    /// <summary>Returns a copy of the test case with every {var:x} replaced. The original keeps its placeholders, so it can be re-run.</summary>
    public static TestCase Resolved(TestCase tc)
    {
        var copy = new TestCase();
        foreach (var p in typeof(TestCase).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            var val = p.GetValue(tc);
            if (p.PropertyType == typeof(string) && !NoResolve.Contains(p.Name))
                val = Resolve((string?)val, SqlFields.Contains(p.Name));
            if (p.Name is "Status" or "Message") continue;   // these raise UI events; the copy is never shown
            p.SetValue(copy, val);
        }
        return copy;
    }

    /// <summary>Reads each NAME=path line of SaveAs from the JSON body and stores the value.</summary>
    public static void Capture(string? saveAs, string body)
    {
        if (string.IsNullOrWhiteSpace(saveAs)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException("SaveAs: the API response is not valid JSON"); }
        using (doc)
        {
            foreach (var line in saveAs.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim(); int eq = t.IndexOf('=');
                if (eq < 1) continue;
                string name = t[..eq].Trim(), path = t[(eq + 1)..].Trim();
                if (!ApiDbVerifier.TryGetPath(doc.RootElement, path, out var v) || string.IsNullOrEmpty(v))
                    throw new InvalidOperationException($"SaveAs “{name}”: path “{path}” not found or empty in the response ({ApiDbVerifier.Describe(doc.RootElement)})");
                lock (Gate) Vars[name] = v;
                var shown = v.Length > 80 ? v.Substring(0, 80) + "..." : v;
                Runner.LogSink?.Invoke("INFO", "Saved {var:" + name + "} = " + shown);
            }
        }
    }
}

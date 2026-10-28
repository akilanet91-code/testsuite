using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TestFlow;

/// <summary>
/// Verifies a flat file that has a HEADER record and a TRAILER record surrounding the data rows.
///
/// Header check  – extract a date string from a fixed character position in line 1 and compare it
///                 to today's date (or an expected date you supply), with a configurable format.
///
/// Trailer check – extract a record-count number from a fixed character position in the last line
///                 and assert it equals the actual number of data rows (all lines except header and trailer).
///
/// All positions are 1-based (same as the existing fixed-width spec convention in this project).
/// </summary>
public static class HeaderTrailerVerifier
{
    /// <summary>
    /// Verifies the header date and/or trailer count in <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute path of the generated file.</param>
    /// <param name="options">All configuration for this check.</param>
    /// <returns>List of <see cref="Issue"/> objects (empty = pass).</returns>
    public static List<Issue> Verify(string filePath, HeaderTrailerOptions options)
    {
        var issues = new List<Issue>();

        if (!File.Exists(filePath))
        {
            issues.Add(new Issue("File", $"File not found: {filePath}"));
            return issues;
        }

        var lines = ReadNonEmpty(filePath);
        if (lines.Count < 2)
        {
            issues.Add(new Issue("File", $"File has {lines.Count} non-empty line(s); expected at least a header and a trailer."));
            return issues;
        }

        var headerLine  = lines[0];
        var trailerLine = lines[^1];
        // Data rows = everything except header (first) and trailer (last)
        int dataRowCount = lines.Count - 2;

        // ── Header prefix check ───────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(options.HeaderPrefix))
        {
            if (!headerLine.StartsWith(options.HeaderPrefix, StringComparison.OrdinalIgnoreCase))
                issues.Add(new Issue("Header", $"Expected header to start with '{options.HeaderPrefix}' but found '{Snip(headerLine)}'"));
        }

        // ── Trailer prefix check ──────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(options.TrailerPrefix))
        {
            if (!trailerLine.StartsWith(options.TrailerPrefix, StringComparison.OrdinalIgnoreCase))
                issues.Add(new Issue("Trailer", $"Expected trailer to start with '{options.TrailerPrefix}' but found '{Snip(trailerLine)}'"));
        }

        // ── Header date check ─────────────────────────────────────────────
        if (options.HeaderDateStart > 0 && options.HeaderDateLength > 0)
        {
            var raw = Extract(headerLine, options.HeaderDateStart, options.HeaderDateLength);
            if (raw == null)
            {
                issues.Add(new Issue("Header", $"Cannot extract date: line is shorter than position {options.HeaderDateStart + options.HeaderDateLength - 1}. Line length = {headerLine.Length}."));
            }
            else
            {
                var fmt = string.IsNullOrWhiteSpace(options.HeaderDateFormat) ? InferFormat(raw) : options.HeaderDateFormat.Trim();
                if (!DateTime.TryParseExact(raw, fmt, null, System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    issues.Add(new Issue("Header/Date", $"'{raw}' does not parse as date format '{fmt}'"));
                }
                else
                {
                    // Compare against expected date
                    var expected = options.ExpectedDate ?? DateTime.Today;
                    if (parsed.Date != expected.Date)
                        issues.Add(new Issue("Header/Date",
                            $"Date in header is {parsed:yyyy-MM-dd} but expected {expected:yyyy-MM-dd}"));
                }
            }
        }

        // ── Trailer record-count check ────────────────────────────────────
        if (options.TrailerCountStart > 0 && options.TrailerCountLength > 0)
        {
            var raw = Extract(trailerLine, options.TrailerCountStart, options.TrailerCountLength);
            if (raw == null)
            {
                issues.Add(new Issue("Trailer", $"Cannot extract count: line is shorter than position {options.TrailerCountStart + options.TrailerCountLength - 1}. Line length = {trailerLine.Length}."));
            }
            else if (!long.TryParse(raw.Trim(), out var count))
            {
                issues.Add(new Issue("Trailer/Count", $"'{raw}' is not a number"));
            }
            else if (count != dataRowCount)
            {
                issues.Add(new Issue("Trailer/Count",
                    $"Trailer says {count} record(s) but file has {dataRowCount} data row(s)"));
            }
        }

        return issues;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Reads all non-empty lines, preserving leading/trailing whitespace per line for position accuracy.</summary>
    static List<string> ReadNonEmpty(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? l;
        while ((l = sr.ReadLine()) != null)
            if (l.Trim().Length > 0) lines.Add(l);
        return lines;
    }

    /// <summary>Extracts a substring by 1-based start position and length. Returns null if the line is too short.</summary>
    static string? Extract(string line, int start1, int length)
    {
        int s = start1 - 1;
        if (s < 0 || s >= line.Length) return null;
        int avail = line.Length - s;
        if (avail < length) return null;
        return line.Substring(s, length);
    }

    static string Snip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Guesses a format string from an 8-char or 10-char date literal so users don't have to specify one for common cases.</summary>
    static string InferFormat(string s)
    {
        s = s.Trim();
        if (s.Length == 8 && s.All(char.IsDigit))              return "yyyyMMdd";
        if (s.Length == 10 && s[4] == '-' && s[7] == '-')      return "yyyy-MM-dd";
        if (s.Length == 10 && s[2] == '/' && s[5] == '/')      return "dd/MM/yyyy";
        if (s.Length == 10 && s[4] == '/' && s[7] == '/')      return "yyyy/MM/dd";
        if (s.Length == 6  && s.All(char.IsDigit))              return "yyyyMM";
        return "yyyyMMdd"; // safe fallback
    }
}

/// <summary>All configuration for a <see cref="HeaderTrailerVerifier"/> check, parsed from the test-case Excel columns.</summary>
public class HeaderTrailerOptions
{
    // ── Header ────────────────────────────────────────────────────────────
    /// <summary>Optional prefix that line 1 must start with (e.g. "HDR"). Leave blank to skip this check.</summary>
    public string? HeaderPrefix { get; set; }

    /// <summary>1-based character position where the date value starts in the header line.</summary>
    public int HeaderDateStart  { get; set; }

    /// <summary>Number of characters to read for the date value.</summary>
    public int HeaderDateLength { get; set; }

    /// <summary>
    /// Format string for <see cref="DateTime.ParseExact"/>. Leave blank to auto-detect from length/shape:
    /// 8-digit  → yyyyMMdd
    /// yyyy-MM-dd → yyyy-MM-dd
    /// dd/MM/yyyy → dd/MM/yyyy
    /// yyyyMM     → yyyyMM
    /// </summary>
    public string? HeaderDateFormat { get; set; }

    /// <summary>
    /// The date the header must contain. Defaults to <see cref="DateTime.Today"/> when null.
    /// Set this to a fixed date when the check is part of a regression suite for a historical batch run.
    /// </summary>
    public DateTime? ExpectedDate { get; set; }

    // ── Trailer ───────────────────────────────────────────────────────────
    /// <summary>Optional prefix that the last line must start with (e.g. "TRL"). Leave blank to skip this check.</summary>
    public string? TrailerPrefix { get; set; }

    /// <summary>1-based character position where the record count starts in the trailer line.</summary>
    public int TrailerCountStart  { get; set; }

    /// <summary>Number of characters to read for the record count.</summary>
    public int TrailerCountLength { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Builds an options object from <see cref="TestCase"/> properties.</summary>
    public static HeaderTrailerOptions FromTestCase(TestCase tc)
    {
        var o = new HeaderTrailerOptions
        {
            HeaderPrefix       = tc.HeaderPrefix,
            TrailerPrefix      = tc.TrailerPrefix,
            HeaderDateStart    = tc.HeaderDateStart,
            HeaderDateLength   = tc.HeaderDateLength,
            HeaderDateFormat   = tc.HeaderDateFormat,
            TrailerCountStart  = tc.TrailerCountStart,
            TrailerCountLength = tc.TrailerCountLength,
        };

        // Parse optional fixed expected date (e.g. "2026-10-25" or "20261025")
        if (!string.IsNullOrWhiteSpace(tc.ExpectedHeaderDate))
        {
            var fmt = string.IsNullOrWhiteSpace(tc.HeaderDateFormat) ? null : tc.HeaderDateFormat.Trim();
            if (fmt != null && DateTime.TryParseExact(tc.ExpectedHeaderDate.Trim(), fmt, null, System.Globalization.DateTimeStyles.None, out var d1))
                o.ExpectedDate = d1;
            else if (DateTime.TryParse(tc.ExpectedHeaderDate.Trim(), out var d2))
                o.ExpectedDate = d2;
        }

        return o;
    }
}

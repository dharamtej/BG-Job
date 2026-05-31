// DataAccess/Util/JobEnricher.cs
// Derives end-user-filterable fields from a job's title + description:
//   • skills   — matched against the canonical md.skills vocabulary so the UI
//                skill filter (which lists md.skills) actually joins to results.
//   • experience (min/max years) — parsed from "5+ years", "3-5 years", etc.
//   • education — highest qualification mentioned (Doctorate → High School).
//
// Called from NormalizeJobsJobHandler, which already loads reference data and
// walks every freshly-fetched row. Department names that handlers put in skills
// are dropped automatically: they aren't in md.skills, so they never match.
using System.Text;
using System.Text.RegularExpressions;

namespace CareerPanda.DataAccess.Util;

public static class JobEnricher
{
    // ── Skills ────────────────────────────────────────────────────────────────

    /// <summary>Pre-built lookup over the md.skills vocabulary. Build once per run.</summary>
    public sealed class SkillIndex
    {
        // Whole-word / phrase skills ([a-z0-9 ] only): normalized phrase → canonical name.
        public required Dictionary<string, string> Phrases { get; init; }
        // Longest phrase length (in words) we need to scan for.
        public required int MaxWords { get; init; }
        // Punctuation skills (C#, C++, CI/CD, Node.js, ASP.NET, .NET): regex → canonical name.
        public required List<(Regex Rx, string Canonical)> Symbols { get; init; }
        // lowercased name → canonical, used to canonicalize handler-supplied tags.
        public required Dictionary<string, string> ByLower { get; init; }
    }

    private static readonly Regex WordSplit = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private const int MaxScanChars = 12000;
    private const int MaxSkills    = 30;

    public static SkillIndex BuildSkillIndex(IEnumerable<string?> skillNames)
    {
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new List<(Regex, string)>();
        var byLower = new Dictionary<string, string>(StringComparer.Ordinal);
        int maxWords = 1;

        foreach (var raw in skillNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name  = raw.Trim();
            var lower = name.ToLowerInvariant();
            byLower.TryAdd(lower, name);

            bool wordy = lower.All(c => char.IsLetterOrDigit(c) || c == ' ');
            if (wordy)
            {
                var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                // Single tokens shorter than 2 chars ("c", "r") cause too many false
                // positives as plain words — the punctuation forms (C#, C++) are handled below.
                if (words.Length == 1 && words[0].Length < 2) continue;
                phrases.TryAdd(string.Join(' ', words), name);
                if (words.Length > maxWords) maxWords = words.Length;
            }
            else
            {
                // Boundary = not flanked by another skill-significant char, so "asp.net"
                // and ".net" stay distinct and "c++" matches without bleeding into "c".
                var rx = new Regex($@"(?<![a-z0-9+#.]){Regex.Escape(lower)}(?![a-z0-9+#.])",
                                   RegexOptions.Compiled | RegexOptions.IgnoreCase);
                symbols.Add((rx, name));
            }
        }

        return new SkillIndex
        {
            Phrases  = phrases,
            MaxWords = Math.Min(maxWords, 4),
            Symbols  = symbols,
            ByLower  = byLower
        };
    }

    /// <summary>
    /// Canonical skills for a job: handler tags that exist in md.skills, unioned with
    /// skills detected in the title + description. Returns null when nothing matches.
    /// </summary>
    public static string[]? ExtractSkills(string? title, string? description, string[]? existing, SkillIndex idx)
    {
        // canonical(lower) → canonical, preserves taxonomy casing and de-dupes.
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        if (existing != null)
        {
            foreach (var tag in existing)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (idx.ByLower.TryGetValue(tag.Trim().ToLowerInvariant(), out var canon))
                    found.TryAdd(canon.ToLowerInvariant(), canon);
            }
        }

        var text = (title ?? "") + "\n" + (description ?? "");
        if (text.Length == 0) return found.Count > 0 ? found.Values.ToArray() : null;
        if (text.Length > MaxScanChars) text = text[..MaxScanChars];
        var lower = text.ToLowerInvariant();

        foreach (var (rx, canon) in idx.Symbols)
            if (rx.IsMatch(lower)) found.TryAdd(canon.ToLowerInvariant(), canon);

        var words = WordSplit.Split(lower);
        var clean = new List<string>(words.Length);
        foreach (var w in words) if (w.Length > 0) clean.Add(w);

        for (int i = 0; i < clean.Count; i++)
        {
            var sb = new StringBuilder(clean[i]);
            if (idx.Phrases.TryGetValue(sb.ToString(), out var c1)) found.TryAdd(c1.ToLowerInvariant(), c1);
            for (int n = 1; n < idx.MaxWords && i + n < clean.Count; n++)
            {
                sb.Append(' ').Append(clean[i + n]);
                if (idx.Phrases.TryGetValue(sb.ToString(), out var cn)) found.TryAdd(cn.ToLowerInvariant(), cn);
            }
        }

        if (found.Count == 0) return null;
        return found.Values.Take(MaxSkills).ToArray();
    }

    // ── Experience (years) ──────────────────────────────────────────────────────

    private static readonly Regex ExpRange = new(@"(\d{1,2})\s*(?:-|–|—|to)\s*(\d{1,2})\s*\+?\s*years?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExpMin   = new(@"(?:minimum|at\s*least|min\.?|atleast)\s*(?:of\s*)?(\d{1,2})\s*\+?\s*years?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExpPlus  = new(@"(\d{1,2})\s*\+\s*years?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExpAny   = new(@"(\d{1,2})\s*\+?\s*years?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>(min, max) years of experience parsed from text. Either may be null.</summary>
    public static (int? min, int? max) ExtractExperience(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var t = text.Length > MaxScanChars ? text[..MaxScanChars] : text;

        var m = ExpRange.Match(t);
        if (m.Success)
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            if (a > b) (a, b) = (b, a);
            if (b <= 50) return (a, b);
        }
        foreach (var rx in new[] { ExpMin, ExpPlus, ExpAny })
        {
            m = rx.Match(t);
            if (m.Success)
            {
                int a = int.Parse(m.Groups[1].Value);
                if (a <= 50) return (a, null);
            }
        }
        return (null, null);
    }

    // ── Education ───────────────────────────────────────────────────────────────

    /// <summary>Highest education level mentioned, or null. Stored in education_qualification.</summary>
    public static string? ExtractEducation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        if (Has(t, "ph.d", "ph. d", "phd", "doctorate", "doctoral"))                                  return "Doctorate";
        if (Has(t, "master", "m.s.", "m.sc", "msc ", "m.tech", "mtech", "mba", "m.b.a",
                   "postgraduate", "post graduate", "post-graduate"))                                  return "Master's";
        if (Has(t, "bachelor", "b.s.", "b.sc", "bsc ", "b.tech", "btech", "b.e.", "b.e ",
                   "undergraduate", "4-year degree", "four year degree", "4 year degree"))             return "Bachelor's";
        if (Has(t, "associate degree", "associate's degree", "a.s. degree"))                           return "Associate";
        if (Has(t, "high school", "secondary school", "ged ", "ged.", "diploma"))                      return "High School/Diploma";
        return null;
    }

    private static bool Has(string text, params string[] needles)
    {
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}

using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ObgServices.Models;

namespace ObgLambda.Excel;

internal static class ExcelParsingHelpers
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string? GetString(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        // ClosedXML may return formatted values; GetString() is generally safe
        var s = cell.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            // If cell is numeric/time, GetString can be empty depending on formatting. Try raw value.
            if (cell.IsEmpty())
                return null;

            s = cell.Value.ToString();
        }

        s = NormalizeSpaces(s);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static string NormalizeSpaces(string s)
        => string.Join(' ', s.Replace('\n', ' ').Replace('\r', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static bool ParseBool(IXLCell cell)
    {
        var s = GetString(cell);
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim().ToLowerInvariant();

        return s is "yes" or "y" or "true" or "1";
    }

    public static int? ParseInt(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        if (cell.TryGetValue<double>(out var d))
            return (int)Math.Round(d);

        var s = GetString(cell);
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Trim();

        if (int.TryParse(s, NumberStyles.Integer, Invariant, out var i))
            return i;

        // Take first integer in string
        var m = System.Text.RegularExpressions.Regex.Match(s, @"-?\d+");
        if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, Invariant, out i))
            return i;

        return null;
    }

    public static int? ParseMinutes(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        if (cell.TryGetValue<double>(out var d))
            return (int)Math.Round(d);

        var s = GetString(cell);
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Trim().ToLowerInvariant();

        // Support "1h 30m"
        var h = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*h");
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*m");
        if (h.Success || m.Success)
        {
            var hours = h.Success ? int.Parse(h.Groups[1].Value, Invariant) : 0;
            var mins = m.Success ? int.Parse(m.Groups[1].Value, Invariant) : 0;
            return hours * 60 + mins;
        }

        // "37 min" => 37
        var num = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        if (num.Success && int.TryParse(num.Value, NumberStyles.Integer, Invariant, out var minutes))
            return minutes;

        return null;
    }

    public static TimeSpan? ParseTime(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        // DateTime
        if (cell.TryGetValue<DateTime>(out var dt))
            return dt.TimeOfDay;

        // TimeSpan
        if (cell.TryGetValue<TimeSpan>(out var ts))
            return ts;

        // Excel numeric time (fraction of day)
        if (cell.TryGetValue<double>(out var d))
        {
            // Typical times are < 1 (fraction of day). If a user put hours as 8, we handle below.
            if (d >= 0 && d < 1)
                return TimeSpan.FromDays(d);

            // If it's 8 or 17 etc, treat as hours
            if (d >= 0 && d <= 24)
                return TimeSpan.FromHours(d);
        }

        var s = GetString(cell);
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Trim();

        // Try common time formats
        if (TimeSpan.TryParse(s, Invariant, out var t))
            return t;

        // "8:00 AM"
        if (DateTime.TryParse(s, Invariant, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.TimeOfDay;

        // "8" => 08:00
        if (int.TryParse(s, NumberStyles.Integer, Invariant, out var hour) && hour >= 0 && hour <= 24)
            return TimeSpan.FromHours(hour);

        return null;
    }

    public static (Skill skill, SkillLevel level)? ParseSkillAndLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = NormalizeSpaces(raw).ToLowerInvariant();
        // expected "exterior - senior"
        var parts = s.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var skillPart = parts[0].Trim();
        var levelPart = parts[1].Trim();

        Skill skill = skillPart switch
        {
            "interior" => Skill.Interior,
            "exterior" => Skill.Exterior,
            "floral" => Skill.Floral,
            _ => Skill.Exterior
        };

        SkillLevel level = levelPart switch
        {
            "junior" => SkillLevel.Junior,
            "medior" => SkillLevel.Medior,
            "senior" => SkillLevel.Senior,
            _ => SkillLevel.None
        };

        return (skill, level);
    }

    public static StartFinishPoint ParseStartFinishPoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return StartFinishPoint.Unknown;

        var s = NormalizeSpaces(raw).ToLowerInvariant();
        return s switch
        {
            "home" => StartFinishPoint.Home,
            "office" => StartFinishPoint.Office,
            "either works" => StartFinishPoint.EitherWorks,
            _ => StartFinishPoint.Unknown
        };
    }

    public static BestAccessMode ParseBestAccessMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return BestAccessMode.Unknown;

        var s = NormalizeSpaces(raw).ToLowerInvariant();
        return s switch
        {
            "car/van" => BestAccessMode.CarVan,
            "drive to a hub and then walk" => BestAccessMode.HubThenWalk,
            "either works" => BestAccessMode.EitherWorks,
            _ => BestAccessMode.Unknown
        };
    }

    public static PermitDifficulty ParsePermitDifficulty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return PermitDifficulty.Unknown;

        var s = NormalizeSpaces(raw).ToLowerInvariant();
        return s switch
        {
            "easy" => PermitDifficulty.Easy,
            "medium" => PermitDifficulty.Medium,
            "hard" => PermitDifficulty.Hard,
            _ => PermitDifficulty.Unknown
        };
    }

    public static string Slugify(string input)
    {
        input = NormalizeSpaces(input).ToLowerInvariant();

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '/')
                sb.Append('-');
            // ignore other punctuation
        }

        var slug = sb.ToString();
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "item" : slug;
    }

    public static List<string> ParseTechList(string? raw, Dictionary<string, string> nameToId, Action<string>? warn = null)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var s = NormalizeSpaces(raw).Trim();
        if (s.Equals("no preference", StringComparison.OrdinalIgnoreCase))
            return result;

        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            // Try exact name match (case-insensitive)
            var key = part.Trim();
            var match = nameToId.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                warn?.Invoke($"Не знайдено техніка з іменем '{key}' у списку техніків.");
                continue;
            }

            result.Add(nameToId[match]);
        }

        return result;
    }

    public static (byte visitsPerInterval, int intervalDays, bool lockAfterFirst) ParseVisitFrequency(string? raw, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0, 7, false);

        var s = NormalizeSpaces(raw).ToLowerInvariant();

        bool lockAfterFirst = s.Contains("(weekly)", StringComparison.OrdinalIgnoreCase);

        // Extract "Nx ..."
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*x");
        byte visits = 0;
        if (m.Success && byte.TryParse(m.Groups[1].Value, NumberStyles.Integer, Invariant, out var v))
            visits = v;

        int intervalDays = 7;

        // "in 14 days" / "in 10 days"
        var mdays = System.Text.RegularExpressions.Regex.Match(s, @"in\s+(\d+)\s+days");
        if (mdays.Success && int.TryParse(mdays.Groups[1].Value, NumberStyles.Integer, Invariant, out var days))
            intervalDays = days;
        else if (s.Contains("a week"))
            intervalDays = 7;

        if (visits == 0)
        {
            warn?.Invoke($"Не вдалося розпізнати VisitsPerInterval з Visit frequency='{raw}'.");
        }

        return (visits, intervalDays, lockAfterFirst);
    }

    public static Dictionary<DayOfWeek, (int fromCol, int toCol)> GetDayTimeColumnPairs(IXLWorksheet ws, int headerRowDay = 2, int headerRowSub = 3)
    {
        var result = new Dictionary<DayOfWeek, (int fromCol, int toCol)>();

        var dayNames = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["mon"] = DayOfWeek.Monday,
            ["tue"] = DayOfWeek.Tuesday,
            ["wed"] = DayOfWeek.Wednesday,
            ["thu"] = DayOfWeek.Thursday,
            ["fri"] = DayOfWeek.Friday,
            ["sat"] = DayOfWeek.Saturday,
            ["sun"] = DayOfWeek.Sunday
        };

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;

        for (int col = 1; col <= lastCol; col++)
        {
            var main = GetString(ws.Cell(headerRowDay, col));
            if (string.IsNullOrWhiteSpace(main)) continue;

            var key = main.Trim().ToLowerInvariant();
            if (!dayNames.TryGetValue(key, out var day))
                continue;

            // Expect from/to at headerRowSub: col, col+1
            var subFrom = GetString(ws.Cell(headerRowSub, col));
            var subTo = GetString(ws.Cell(headerRowSub, col + 1));
            if (subFrom != null && subFrom.Equals("from", StringComparison.OrdinalIgnoreCase) &&
                subTo != null && subTo.Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                result[day] = (col, col + 1);
            }
        }

        return result;
    }
}

namespace Cobalt.Core.Text;

/// <summary>
/// Compact, human "age" strings for list columns: <c>45m</c>, <c>6h</c>, <c>3d</c>,
/// <c>5w</c>. UI-free and deterministic — callers pass "now" so it can be tested and
/// so a whole render uses one consistent clock.
/// </summary>
public static class AgeFormat
{
    /// <summary>
    /// Coarsest single unit that fits: minutes under an hour, hours under a day,
    /// days under a fortnight, then whole weeks. Negative spans clamp to zero.
    /// </summary>
    public static string Short(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }
        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h";
        }
        if (age.TotalDays < 14)
        {
            return $"{(int)age.TotalDays}d";
        }
        return $"{(int)(age.TotalDays / 7)}w";
    }

    /// <summary>
    /// Age of <paramref name="creation"/> as of <paramref name="now"/>; a dash when
    /// the creation date is unknown (older list responses may omit it).
    /// </summary>
    public static string Since(DateTimeOffset? creation, DateTimeOffset now) =>
        creation is null ? "-" : Short(now - creation.Value);
}

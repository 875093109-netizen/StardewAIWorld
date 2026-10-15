namespace AIWorld.Core.Behaviors;

/// <summary>
/// Shared game-time value object (PM D-P4-3 U3): `{season}-{day}-{year}@{HHMM}`.
/// Extends the Phase 3 gameDate format (`spring-9-1`) with time-of-day, so persistence
/// and FollowSession timestamps speak one format. Phase 3 knowledge.updatedAt stays
/// gameDate-only (contract §2.3 uses gameDate); FollowSession uses this full stamp.
/// </summary>
public readonly struct GameTimeStamp : IEquatable<GameTimeStamp>
{
    public string Season { get; }
    public int Day { get; }
    public int Year { get; }
    public int TimeOfDay { get; } // 0600..2600 game clock

    public GameTimeStamp(string season, int day, int year, int timeOfDay)
    {
        Season = season;
        Day = day;
        Year = year;
        TimeOfDay = timeOfDay;
    }

    public string GameDate => $"{Season}-{Day}-{Year}";

    public override string ToString() => $"{GameDate}@{TimeOfDay:D4}";

    public bool Equals(GameTimeStamp other) =>
        Season == other.Season && Day == other.Day && Year == other.Year && TimeOfDay == other.TimeOfDay;

    public override bool Equals(object? obj) => obj is GameTimeStamp other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Season, Day, Year, TimeOfDay);

    /// <summary>Parse "{season}-{day}-{year}@{HHMM}". Returns null on malformed input.</summary>
    public static GameTimeStamp? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var at = value.LastIndexOf('@');
        string datePart = at >= 0 ? value[..at] : value;
        string timePart = at >= 0 ? value[(at + 1)..] : "0600";
        var parts = datePart.Split('-');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[1], out var day) || !int.TryParse(parts[2], out var year)) return null;
        if (!int.TryParse(timePart, out var time)) return null;
        return new GameTimeStamp(parts[0], day, year, time);
    }

    /// <summary>Create from the Phase 3 gameDate string ("spring-9-1") + a game clock.</summary>
    public static GameTimeStamp? FromGameDate(string? gameDate, int timeOfDay)
    {
        if (string.IsNullOrWhiteSpace(gameDate)) return null;
        var parts = gameDate.Split('-');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[1], out var day) || !int.TryParse(parts[2], out var year)) return null;
        return new GameTimeStamp(parts[0], day, year, timeOfDay);
    }
}

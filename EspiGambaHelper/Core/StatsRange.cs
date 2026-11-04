namespace EspiGambaHelper.Core;

public enum RangePreset
{
    Last12Hours,
    Last24Hours,
    Last7Days,
    Last30Days,
    AllTime,
    Custom,
}

/// <summary>The timeframe shown in the Stats tab: a rolling window that follows the clock, everything, or fixed bounds.</summary>
public sealed class StatsRange
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(12);

    public RangePreset Preset { get; private set; } = RangePreset.Last12Hours;
    public DateTimeOffset CustomFrom { get; private set; }
    public DateTimeOffset CustomTo { get; private set; }

    public static TimeSpan? Window(RangePreset preset) => preset switch
    {
        RangePreset.Last12Hours => TimeSpan.FromHours(12),
        RangePreset.Last24Hours => TimeSpan.FromHours(24),
        RangePreset.Last7Days => TimeSpan.FromDays(7),
        RangePreset.Last30Days => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>Query bounds. Rolling windows have no upper bound so a game stamped seconds ahead of this clock still counts.</summary>
    public (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset now) => Preset switch
    {
        RangePreset.AllTime => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
        RangePreset.Custom => (CustomFrom, CustomTo),
        _ => (now - Window(Preset)!.Value, DateTimeOffset.MaxValue),
    };

    public void Use(RangePreset preset, DateTimeOffset now)
    {
        if (preset == RangePreset.Custom) Freeze(now);
        Preset = preset;
    }

    public void SetFrom(DateTimeOffset from, DateTimeOffset now)
    {
        Freeze(now);
        CustomFrom = from;
        if (CustomTo < from) CustomTo = from;
    }

    public void SetTo(DateTimeOffset to, DateTimeOffset now)
    {
        Freeze(now);
        CustomTo = to;
        if (CustomFrom > to) CustomFrom = to;
    }

    /// <summary>Editing a bound turns the current window into fixed values; the selectors always start from what is shown.</summary>
    private void Freeze(DateTimeOffset now)
    {
        if (Preset == RangePreset.Custom) return;
        var (from, to) = Resolve(now);
        CustomFrom = from == DateTimeOffset.MinValue ? now - DefaultWindow : from;
        CustomTo = to == DateTimeOffset.MaxValue ? now : to;
        Preset = RangePreset.Custom;
    }
}

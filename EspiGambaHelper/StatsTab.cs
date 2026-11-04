using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

/// <summary>Combined profit of blackjack, scratch and wheel games over a chosen timeframe, from the local stats database.</summary>
internal sealed class StatsTab(Plugin plugin)
{
    private static readonly Vector4 Positive = new(0.45f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Negative = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.65f, 0.35f, 1f);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly (RangePreset Preset, string Label)[] Presets =
    [
        (RangePreset.Last12Hours, "12 hours"),
        (RangePreset.Last24Hours, "24 hours"),
        (RangePreset.Last7Days, "7 days"),
        (RangePreset.Last30Days, "30 days"),
        (RangePreset.AllTime, "All"),
        (RangePreset.Custom, "Custom"),
    ];

    private readonly StatsRange range = new();
    private readonly DateTimePicker fromPicker = new("from");
    private readonly DateTimePicker toPicker = new("to");
    private StatsSummary? summary;
    private List<GameRecord> recent = [];
    private long cachedVersion = -1;
    private string? cachedCharacter;
    private (RangePreset, DateTimeOffset, DateTimeOffset) cachedRange;
    private DateTime cachedAt;
    private string? queryError;

    public void Draw()
    {
        if (plugin.Stats is not { } stats)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Warning);
            ImGui.TextWrapped(plugin.StatsError ?? "The stats database is not available.");
            ImGui.PopStyleColor();
            return;
        }
        DrawRecording(stats);
        ImGui.Separator();
        var now = DateTimeOffset.UtcNow;
        DrawRange(now);
        ImGui.Separator();
        Refresh(stats, now);
        if (queryError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Warning);
            ImGui.TextWrapped(queryError);
            ImGui.PopStyleColor();
        }
        if (summary == null) return;
        if (stats.Character is { } character)
            ImGui.TextDisabled($"Scratch and wheel games hosted by {character}; every blackjack round.");
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Warning);
            ImGui.TextWrapped("Log in to a character to see the scratch and wheel games it hosted. Blackjack rounds are shown for everyone.");
            ImGui.PopStyleColor();
        }
        DrawSummary(summary);
        DrawRecent();
    }

    private void DrawRecording(StatsRecorder stats)
    {
        var live = stats.LiveEnabled;
        if (ImGui.Checkbox("Record games live from SimpleBlackjack, SimpleScratch and SimpleWheel", ref live))
            plugin.SetLiveStats(live);
        if (stats.LastLiveStatus is { } liveStatus) ImGui.TextWrapped(liveStatus);
        ImGui.BeginDisabled(stats.Importing);
        if (ImGui.Button(stats.Importing ? "Importing..." : "Import from plugins")) stats.StartImport();
        ImGui.EndDisabled();
        ImGui.TextWrapped("Import adds the games archived in SimpleBlackjack, SimpleScratch and SimpleWheel; games already recorded are skipped.");
        if (stats.LastImportStatus is { } importStatus) ImGui.TextWrapped(importStatus);
    }

    private void DrawRange(DateTimeOffset now)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Timeframe:");
        foreach (var (preset, label) in Presets)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(label, range.Preset == preset)) range.Use(preset, now);
        }
        if (range.Preset == RangePreset.AllTime)
        {
            ImGui.TextDisabled("Every recorded game.");
            return;
        }
        var (from, to) = range.Resolve(now);
        var fromLocal = from.ToLocalTime().DateTime;
        var toLocal = (to == DateTimeOffset.MaxValue ? now : to).ToLocalTime().DateTime;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("From");
        ImGui.SameLine();
        if (fromPicker.Draw(ref fromLocal)) range.SetFrom(new DateTimeOffset(DateTime.SpecifyKind(fromLocal, DateTimeKind.Local)), now);
        ImGui.SameLine();
        ImGui.TextUnformatted("to");
        ImGui.SameLine();
        if (toPicker.Draw(ref toLocal)) range.SetTo(new DateTimeOffset(DateTime.SpecifyKind(toLocal, DateTimeKind.Local)), now);
        if (range.Preset != RangePreset.Custom)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(follows the clock; edit either to fix the range)");
        }
    }

    private void Refresh(StatsRecorder stats, DateTimeOffset now)
    {
        var version = stats.Version;
        var key = (range.Preset, range.CustomFrom, range.CustomTo);
        // Rolling windows move every frame, so they re-query on a timer; everything else only when something changed.
        if (summary != null && version == cachedVersion && key == cachedRange && stats.Character == cachedCharacter
            && DateTime.UtcNow - cachedAt < RefreshInterval) return;
        var (from, to) = range.Resolve(now);
        try
        {
            summary = stats.Summarize(from, to);
            recent = stats.Recent(from, to);
            queryError = null;
        }
        catch (Exception ex)
        {
            queryError = "Could not read the stats database. See /xllog.";
            Plugin.Log.Error(ex, "Stats query failed");
        }
        cachedVersion = version;
        cachedCharacter = stats.Character;
        cachedRange = key;
        cachedAt = DateTime.UtcNow;
    }

    private static void DrawSummary(StatsSummary summary)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Total profit:");
        ImGui.SameLine();
        ImGui.TextColored(summary.Profit < 0 ? Negative : Positive, $"{GilLabel.Format(summary.Profit)} gil");
        ImGui.SameLine();
        ImGui.TextDisabled($"from {summary.Games} games");
        if (!ImGui.BeginTable("Totals", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Game", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Games", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Collected", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Paid out", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableHeadersRow();
        foreach (var source in GameSources.All)
        {
            var totals = summary.For(source);
            TotalsRow(source.DisplayName(), totals.Games, totals.Collected, totals.PaidOut);
        }
        TotalsRow("Total", summary.Games, summary.Collected, summary.PaidOut);
        ImGui.EndTable();
    }

    private static void TotalsRow(string label, int games, long collected, long paidOut)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        RightAligned(games.ToString(CultureInfo.InvariantCulture));
        ImGui.TableNextColumn();
        RightAligned(GilLabel.Format(collected));
        ImGui.TableNextColumn();
        RightAligned(GilLabel.Format(paidOut));
        ImGui.TableNextColumn();
        RightAligned(GilLabel.Format(collected - paidOut), collected - paidOut < 0 ? Negative : Positive);
    }

    private void DrawRecent()
    {
        if (!ImGui.CollapsingHeader($"Recent games ({recent.Count}{(recent.Count >= 200 ? ", newest 200" : "")})###RecentGames")) return;
        if (recent.Count == 0)
        {
            ImGui.TextDisabled("No games in this timeframe.");
            return;
        }
        var height = Math.Min(recent.Count + 1, 12) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2;
        if (!ImGui.BeginTable("Recent", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, height))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Game", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Collected", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Paid out", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableHeadersRow();
        foreach (var game in recent)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(game.PlayedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(game.Source.DisplayName() + (game.Live ? " (live)" : ""));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(game.Live && game.Source == GameSource.Scratch
                    ? $"Hosted by {(game.Host.Length == 0 ? "?" : game.Host)}. Card sales are unknown until the SimpleScratch archive is imported."
                    : $"Hosted by {(game.Host.Length == 0 ? "?" : game.Host)}.");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(game.Player);
            ImGui.TableNextColumn();
            RightAligned(GilLabel.Format(game.Collected));
            ImGui.TableNextColumn();
            RightAligned(GilLabel.Format(game.PaidOut));
            ImGui.TableNextColumn();
            RightAligned(GilLabel.Format(game.Profit), game.Profit < 0 ? Negative : Positive);
        }
        ImGui.EndTable();
    }

    private static void RightAligned(string text, Vector4? color = null)
    {
        var slack = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (slack > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slack);
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
    }
}

/// <summary>A button showing a local date and time that opens a calendar with hour and minute fields.</summary>
internal sealed class DateTimePicker(string id)
{
    private const float DayCell = 30f;
    private static readonly string[] Weekdays = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    private DateTime view;

    /// <summary>Returns true when <paramref name="value"/> changed. Seconds are always zero.</summary>
    public bool Draw(ref DateTime value)
    {
        var changed = false;
        ImGui.PushID(id);
        if (ImGui.Button(value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), new Vector2(150, 0)))
        {
            view = new DateTime(value.Year, value.Month, 1);
            ImGui.OpenPopup("picker");
        }
        if (ImGui.BeginPopup("picker"))
        {
            // Month arrows sit at the calendar's edges with the month name centered between them.
            var rowWidth = DayCell * 7;
            var arrowWidth = ImGui.GetFrameHeight();
            if (ImGui.ArrowButton("previous", ImGuiDir.Left)) view = view.AddMonths(-1);
            ImGui.SameLine();
            var title = view.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            var titleSpace = rowWidth - arrowWidth * 2 - ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (titleSpace - ImGui.CalcTextSize(title).X) / 2));
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(title);
            ImGui.SameLine(rowWidth - arrowWidth);
            if (ImGui.ArrowButton("next", ImGuiDir.Right)) view = view.AddMonths(1);
            if (ImGui.BeginTable("days", 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadOuterX))
            {
                foreach (var weekday in Weekdays) ImGui.TableSetupColumn(weekday, ImGuiTableColumnFlags.WidthFixed, DayCell);
                ImGui.TableHeadersRow();
                var daysInMonth = DateTime.DaysInMonth(view.Year, view.Month);
                var day = 1 - ((int)view.DayOfWeek + 6) % 7;
                while (day <= daysInMonth)
                {
                    ImGui.TableNextRow();
                    for (var column = 0; column < 7; column++, day++)
                    {
                        ImGui.TableNextColumn();
                        if (day < 1 || day > daysInMonth) continue;
                        var selected = value.Year == view.Year && value.Month == view.Month && value.Day == day;
                        if (!ImGui.Selectable(day.ToString(CultureInfo.InvariantCulture).PadLeft(2), selected, ImGuiSelectableFlags.None, new Vector2(DayCell, 0))) continue;
                        value = new DateTime(view.Year, view.Month, day, value.Hour, value.Minute, 0);
                        changed = true;
                    }
                }
                ImGui.EndTable();
            }
            var hour = value.Hour;
            var minute = value.Minute;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Time");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("##hour", ref hour, 1, 1))
            {
                value = value.Date.AddHours(Math.Clamp(hour, 0, 23)).AddMinutes(value.Minute);
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(":");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("##minute", ref minute, 1, 5))
            {
                value = value.Date.AddHours(value.Hour).AddMinutes(Math.Clamp(minute, 0, 59));
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Now"))
            {
                var now = DateTime.Now;
                value = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
                view = new DateTime(value.Year, value.Month, 1);
                changed = true;
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }
}

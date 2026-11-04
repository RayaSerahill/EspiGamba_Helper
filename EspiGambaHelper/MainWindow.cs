using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly StatsTab statsTab;
    private string vipName = "";
    private string vipWorld = "";
    private string? vipError;

    public MainWindow(Plugin plugin) : base("EspiGamba Helper###EspiGambaHelper")
    {
        this.plugin = plugin;
        statsTab = new StatsTab(plugin);
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 280), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("EspiGambaTabs")) return;
        if (ImGui.BeginTabItem("Scratch"))
        {
            DrawScratch();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("VIP"))
        {
            DrawVips();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Stats"))
        {
            statsTab.Draw();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawScratch()
    {
        var enabled = plugin.Session.Enabled;
        if (ImGui.Checkbox("Enable", ref enabled)) plugin.SetEnabled(enabled);
        ImGui.TextWrapped("One scratch game per party or alliance member. Disabled and enable again to start a new round of scratchies.");
        ImGui.Separator();

        ImGui.BeginDisabled(plugin.Session.Enabled);
        var maximum = plugin.Configuration.DiceMaximum;
        ImGui.SetNextItemWidth(130);
        if (ImGui.InputInt("Dice maximum", ref maximum, 0, 0))
        {
            plugin.Configuration.DiceMaximum = maximum;
            plugin.Save();
        }
        if (plugin.Configuration.DiceMaximum is < 2 or > 999)
            ImGui.TextWrapped("Enter a dice maximum from 2 to 999.");
        var bonus = plugin.Configuration.VipBonus;
        ImGui.SetNextItemWidth(130);
        if (ImGui.InputInt("VIP bonus", ref bonus, 0, 0))
        {
            plugin.Configuration.VipBonus = bonus;
            plugin.Save();
        }
        ImGui.TextWrapped("Cards = roll result. VIP cards = roll result + bonus.");
        if (Select("Preset", plugin.Configuration.PresetName, plugin.Presets, false, out var preset))
        {
            plugin.Configuration.PresetName = preset;
            plugin.Save();
        }
        if (Select("Theme", plugin.Configuration.ThemeName, plugin.Themes, true, out var theme))
        {
            plugin.Configuration.ThemeName = theme;
            plugin.Save();
        }
        if (ImGui.Button("Refresh presets and themes")) plugin.RefreshChoices();
        ImGui.EndDisabled();

        var autoTells = plugin.Configuration.SendTellsAutomatically;
        if (ImGui.Checkbox("Send tells automatically on game creation", ref autoTells))
            plugin.SetAutomaticTells(autoTells);
        if (autoTells) ImGui.TextUnformatted($"Queued tells: {plugin.QueuedTellCount}");
        if (plugin.LastTellStatus is { } tellStatus) ImGui.TextWrapped(tellStatus);

        ImGui.Separator();
        ImGui.TextUnformatted($"Created: {plugin.Session.CreatedCount}   Pending: {plugin.Session.AttemptedCount - plugin.Session.CreatedCount - plugin.Session.FailedCount}   Failed: {plugin.Session.FailedCount}");
        if (plugin.Session.LastStatus is { } status) ImGui.TextWrapped(status);
        if (plugin.LastRollStatus is { } rollStatus) ImGui.TextWrapped(rollStatus);
        if (plugin.Error is { } error)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.65f, 0.35f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    private void DrawVips()
    {
        ImGui.TextWrapped("Add VIPs using their full character name and home world.");
        ImGui.SetNextItemWidth(270);
        ImGui.InputText("Name", ref vipName, 64);
        ImGui.SetNextItemWidth(270);
        ImGui.InputText("World", ref vipWorld, 64);
        if (ImGui.Button("Add VIP") && VipList.TryAdd(plugin.Configuration.Vips, vipName, vipWorld, out vipError))
        {
            plugin.Save();
            vipName = vipWorld = "";
        }
        if (vipError != null) ImGui.TextWrapped(vipError);
        ImGui.Separator();
        ImGui.TextUnformatted($"VIPs: {plugin.Configuration.Vips.Count}");
        if (plugin.Configuration.Vips.Count == 0)
        {
            ImGui.TextDisabled("No VIPs added yet.");
            return;
        }
        if (!ImGui.BeginTable("VIPs", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableHeadersRow();
        var deleteIndex = -1;
        for (var i = 0; i < plugin.Configuration.Vips.Count; i++)
        {
            var vip = plugin.Configuration.Vips[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(vip.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(vip.World);
            ImGui.TableNextColumn();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) deleteIndex = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Delete {vip.Name} @ {vip.World}");
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (deleteIndex >= 0)
        {
            plugin.Configuration.Vips.RemoveAt(deleteIndex);
            plugin.Save();
            vipError = null;
        }
    }

    private static bool Select(string label, string current, string[] choices, bool allowDefault, out string selected)
    {
        selected = current;
        var changed = false;
        ImGui.SetNextItemWidth(270);
        if (!ImGui.BeginCombo(label, current.Length == 0 ? (allowDefault ? "SimpleScratch default" : "Select a preset") : current)) return false;
        if (allowDefault && ImGui.Selectable("SimpleScratch default", current.Length == 0)) { selected = ""; changed = true; }
        foreach (var choice in choices)
        {
            if (ImGui.Selectable(choice, choice == current)) { selected = choice; changed = true; }
            if (choice == current) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return changed;
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace AltMate;

public sealed partial class MainWindow : Window
{
    private enum MainSection
    {
        Home,
        Housing,
        CharacterLink,
        Animations,
        Gil,
        Settings,
        Submarines,
        CustomDeliveries,
    }

    private enum HousingSection
    {
        Lottery = 0,
        OpenPlots = 1,
        Characters = 2,
        Demolition = 3,
    }

    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly Plugin plugin;
    private string scanMessage = string.Empty;
    private int sizeFilterIndex;
    private string worldFilter = "ALL";
    private string mapPreviewMessage = string.Empty;
    private MainSection selectedSection = MainSection.Home;
    private HousingSection selectedHousingSection = HousingSection.Lottery;
    private bool compactMode;
    private bool requestCompactMode;
    private bool requestExpandedMode;
    private MainSection? requestedExpandedSection;
    private bool clearForcedSize;
    private AnimationEmote[] animationEmotes = [];
    private AnimationEmote[] gameEmotes = [];
    private bool animationListLoaded;
    private bool gameEmoteListLoaded;
    private ulong animationTargetContentId;
    private string animationFilter = string.Empty;
    private string gameEmoteFilter = string.Empty;
    private DateTime treasureMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private Vector2 expandedWindowSize = new(940, 520);
    private ImGuiWindowFlags expandedWindowFlags;
    private static readonly string[] SizeFilters = { "ALL", "S", "S-M", "M", "M-L", "L" };

    private static string[] FpsLimitLabels() => Loc.IsEnglish
        ? ["Unlimited", "60 FPS", "30 FPS"]
        : ["無制限", "60 FPS", "30 FPS"];

    private static int FpsLimitToIndex(int limit) => limit switch
    {
        <= 0 => 0,
        <= 30 => 2,
        _ => 1,
    };

    private static int FpsIndexToLimit(int index) => index switch
    {
        2 => 30,
        1 => 60,
        _ => 0,
    };

    public MainWindow(Plugin plugin) : base(
        $"AltMate v{Plugin.PluginInterface.Manifest.AssemblyVersion.ToString(3)} - {Loc.L("複数キャラクター支援", "Multi-Character Support")}###AltMate")
    {
        this.plugin = plugin;
        if (Enum.IsDefined(typeof(MainSection), plugin.Configuration.LastMainSection))
            selectedSection = (MainSection)plugin.Configuration.LastMainSection;
        if (Enum.IsDefined(typeof(HousingSection), plugin.Configuration.LastHousingSection))
            selectedHousingSection = (HousingSection)plugin.Configuration.LastHousingSection;
        Size = new Vector2(940, 520);
        BgAlpha = plugin.Configuration.WindowBackgroundOpacity;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 360) };
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            IconOffset = new Vector2(0, -2),
            Priority = 1,
            ShowTooltip = () => ImGui.SetTooltip(Loc.T("Minimize")),
            Click = _ => EnterCompactMode(),
        });
        if (plugin.Configuration.WindowCompactMode)
            ApplyCompactMode();
    }

    public override void Draw()
    {
        if (requestCompactMode)
        {
            requestCompactMode = false;
            requestExpandedMode = false;
            if (!compactMode)
                EnterCompactMode();
        }
        else if (requestExpandedMode)
        {
            requestExpandedMode = false;
            if (compactMode)
                ExitCompactMode();
            if (requestedExpandedSection is { } section)
            {
                requestedExpandedSection = null;
                SelectSection(section);
            }
        }
        if (clearForcedSize)
        {
            // A non-null Size/Position is still submitted by Dalamud even with
            // ImGuiCond.None (which means unconditional in ImGui). Remove the
            // values themselves after their one restoration frame.
            Size = null;
            Position = null;
            clearForcedSize = false;
        }
        // Keep the window alpha in sync with the persisted setting. Shared
        // configuration can be refreshed by another client between frames.
        BgAlpha = compactMode
            ? plugin.Configuration.CompactWindowBackgroundOpacity
            : plugin.Configuration.WindowBackgroundOpacity;
        if (compactMode)
        {
            DrawCompactMenu();
            return;
        }

        var menuWidth = (plugin.Configuration.CompactMainMenu ? 96 : 184) * ImGuiHelpers.GlobalScale;

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * ImGuiHelpers.GlobalScale);
        var backgroundOpacity = plugin.Configuration.WindowBackgroundOpacity;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.085f, 0.11f, backgroundOpacity));
        if (ImGui.BeginChild("altmate-menu", new Vector2(menuWidth, 0), true))
            DrawMainMenu();
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.065f, 0.085f, backgroundOpacity * 0.82f));
        if (ImGui.BeginChild("altmate-content", new Vector2(0, 0), true))
            DrawSelectedSection();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    internal void OpenHousingLottery()
    {
        SelectSection(MainSection.Housing);
        SelectHousingSection(HousingSection.Lottery);
        IsOpen = true;
    }

    internal void OpenPreviousState()
    {
        IsOpen = true;
        RequestFocus = true;
    }

    internal void OpenCompact()
    {
        requestCompactMode = true;
        requestExpandedMode = false;
        IsOpen = true;
        RequestFocus = true;
    }

    internal void OpenExpanded()
    {
        requestExpandedMode = true;
        requestCompactMode = false;
        IsOpen = true;
        RequestFocus = true;
    }

    internal void OpenAnimations() => OpenSection(MainSection.Animations);
    internal void OpenHousing() => OpenSection(MainSection.Housing);
    internal void OpenGil() => OpenSection(MainSection.Gil);
    internal void OpenSubmarines() => OpenSection(MainSection.Submarines);
    internal void OpenCustomDeliveries() => OpenSection(MainSection.CustomDeliveries);

    private void OpenSection(MainSection section)
    {
        if (compactMode)
        {
            requestedExpandedSection = section;
            requestExpandedMode = true;
        }
        else
        {
            SelectSection(section);
        }
        IsOpen = true;
        RequestFocus = true;
    }

    internal void OpenSettings()
    {
        if (compactMode)
        {
            requestedExpandedSection = MainSection.Settings;
            requestExpandedMode = true;
        }
        else
        {
            SelectSection(MainSection.Settings);
        }
        IsOpen = true;
        RequestFocus = true;
    }

    private void DrawSelectedSection()
    {
        switch (selectedSection)
        {
            case MainSection.Home:
                DrawHome();
                break;
            case MainSection.Housing:
                DrawHousing();
                break;
            case MainSection.CharacterLink:
                DrawCharacterLink();
                break;
            case MainSection.Animations:
                DrawAnimations();
                break;
            case MainSection.Gil:
                DrawGil();
                break;
            case MainSection.Submarines:
                DrawSubmarines();
                break;
            case MainSection.CustomDeliveries:
                DrawCustomDeliveries();
                break;
            case MainSection.Settings:
                DrawSettings();
                break;
        }
    }

    private void SelectSection(MainSection section)
    {
        selectedSection = section;
        plugin.Configuration.LastMainSection = (int)section;
        plugin.Configuration.Save();
    }

    private void SelectHousingSection(HousingSection section)
    {
        selectedHousingSection = section;
        plugin.Configuration.LastHousingSection = (int)section;
        plugin.Configuration.Save();
    }

    private void DrawMainMenu()
    {
        var narrow = plugin.Configuration.CompactMainMenu;
        ImGui.Spacing();
        var iconSize = (narrow ? 58 : 128) * ImGuiHelpers.GlobalScale;
        var icon = Plugin.TextureProvider.GetFromFile(plugin.IconPath).GetWrapOrDefault();
        if (icon is not null)
        {
            var availableWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - iconSize) / 2));
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        }
        ImGui.Spacing();
        if (!narrow)
        {
            ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), "AltMate");
            ImGui.TextDisabled("MULTI CHARACTER TOOL");
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMenuButton(Loc.T("Home"), MainSection.Home);
        DrawMenuButton(Loc.T("Link"), MainSection.CharacterLink,
            detail: narrow ? null : GetCharacterLinkMenuDetail());
        DrawMenuButton(narrow ? Loc.L("アニメ", "Emotes") : Loc.T("Animation"), MainSection.Animations);
        DrawMenuButton(narrow ? Loc.L("住宅", "Housing") : Loc.T("Housing"), MainSection.Housing,
            GetHousingAttentionCount());
        DrawMenuButton(Loc.T("Gil"), MainSection.Gil);
        DrawMenuButton(narrow ? Loc.L("お得意", "Delivery") : Loc.L("お得意様", "Custom Deliveries"),
            MainSection.CustomDeliveries);
        DrawMenuButton(narrow ? Loc.L("潜水艦", "Subs") : Loc.L("潜水艦管理", "Submersibles"),
            MainSection.Submarines);
        var bottomY = ImGui.GetWindowHeight() - 62 * ImGuiHelpers.GlobalScale;
        if (ImGui.GetCursorPosY() < bottomY)
            ImGui.SetCursorPosY(bottomY);
        DrawMenuButton(narrow ? Loc.L("設定", "Settings") : Loc.T("Settings"), MainSection.Settings);
    }

    private void EnterCompactMode()
    {
        expandedWindowSize = ImGui.GetWindowSize();
        var expandedPosition = ImGui.GetWindowPos();
        plugin.Configuration.HasExpandedWindowPlacement = true;
        plugin.Configuration.ExpandedWindowX = expandedPosition.X;
        plugin.Configuration.ExpandedWindowY = expandedPosition.Y;
        plugin.Configuration.ExpandedWindowWidth = expandedWindowSize.X;
        plugin.Configuration.ExpandedWindowHeight = expandedWindowSize.Y;
        expandedWindowFlags = Flags;
        ApplyCompactMode();
        plugin.Configuration.WindowCompactMode = true;
        plugin.Configuration.Save();
    }

    private void ApplyCompactMode()
    {
        compactMode = true;
        BgAlpha = plugin.Configuration.CompactWindowBackgroundOpacity;
        Flags = expandedWindowFlags | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 112),
            MaximumSize = new Vector2(720, 128),
        };
        Size = new Vector2(650, 118);
        SizeCondition = ImGuiCond.Always;
        if (plugin.Configuration.HasCompactWindowPosition)
        {
            Position = new Vector2(plugin.Configuration.CompactWindowX, plugin.Configuration.CompactWindowY);
            PositionCondition = ImGuiCond.Always;
        }
        clearForcedSize = true;
    }

    private void ExitCompactMode()
    {
        var compactPosition = ImGui.GetWindowPos();
        plugin.Configuration.HasCompactWindowPosition = true;
        plugin.Configuration.CompactWindowX = compactPosition.X;
        plugin.Configuration.CompactWindowY = compactPosition.Y;
        compactMode = false;
        Flags = expandedWindowFlags;
        BgAlpha = plugin.Configuration.WindowBackgroundOpacity;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 360) };
        var restoredSize = plugin.Configuration.HasExpandedWindowPlacement
            ? new Vector2(MathF.Max(760, plugin.Configuration.ExpandedWindowWidth),
                MathF.Max(360, plugin.Configuration.ExpandedWindowHeight))
            : new Vector2(MathF.Max(760, expandedWindowSize.X), MathF.Max(360, expandedWindowSize.Y));
        var restoredPosition = plugin.Configuration.HasExpandedWindowPlacement
            ? new Vector2(plugin.Configuration.ExpandedWindowX, plugin.Configuration.ExpandedWindowY)
            : ImGui.GetWindowPos();
        // Apply the restored size directly once. Keeping SizeCondition=Always
        // causes Dalamud to overwrite later user resizing during subsequent frames.
        ImGui.SetWindowSize(restoredSize, ImGuiCond.Always);
        ImGui.SetWindowPos(restoredPosition, ImGuiCond.Always);
        Size = null;
        Position = null;
        clearForcedSize = false;
        plugin.Configuration.WindowCompactMode = false;
        plugin.Configuration.Save();
    }

    private void DrawCompactMenu()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * scale);
        var backgroundOpacity = plugin.Configuration.CompactWindowBackgroundOpacity;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.07f, 0.095f, backgroundOpacity));
        ImGui.BeginChild("compact-header", new Vector2(0, 52 * scale), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorPos(new Vector2(10 * scale, 8 * scale));
        var iconSize = 36 * scale;
        var icon = Plugin.TextureProvider.GetFromFile(plugin.IconPath).GetWrapOrDefault();
        if (icon is not null)
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        else
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        ImGui.SameLine();
        ImGui.SetCursorPosY(9 * scale);
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.45f, 0.84f, 1f, 1f), "AltMate");
        ImGui.TextColored(plugin.Configuration.LinkEnabled
                ? new Vector4(0.45f, 0.9f, 0.55f, 1f)
                : new Vector4(0.68f, 0.7f, 0.76f, 1f),
            plugin.Configuration.LinkEnabled ? Loc.L("● 連携中", "● Linked") : Loc.L("● 待機中", "● Standby"));
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.SetCursorPosY(12 * scale);
        var linkEnabled = plugin.Configuration.LinkEnabled;
        ImGui.PushStyleColor(ImGuiCol.Button, linkEnabled
            ? new Vector4(0.72f, 0.14f, 0.12f, 0.95f)
            : new Vector4(0.12f, 0.52f, 0.28f, 0.95f));
        if (ImGui.Button(linkEnabled
                ? Loc.L("連携停止##compact-link", "Stop link##compact-link")
                : Loc.L("連携開始##compact-link", "Start link##compact-link"), new Vector2(92 * scale, 30 * scale)))
        {
            plugin.Configuration.LinkEnabled = !linkEnabled;
            plugin.Configuration.Save();
            if (!linkEnabled && plugin.CharacterLink.RuntimeStopped)
                plugin.CharacterLink.Resume();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.SetCursorPosY(12 * scale);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.08f, 0.08f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.12f, 0.12f, 1f));
        if (ImGui.Button($"{Loc.T("EmergencyStop")}##compact-stop", new Vector2(92 * scale, 30 * scale)))
        {
            plugin.CharacterLink.EmergencyStop();
            plugin.CustomDeliveries.Automation.Stop();
        }
        ImGui.PopStyleColor(2);
        ImGui.SameLine();
        ImGui.SetCursorPosY(12 * scale);
        if (ImGui.Button("⛶##compact-expand", new Vector2(34 * scale, 30 * scale)))
            requestExpandedMode = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Maximize"));
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.09f, 0.12f, backgroundOpacity));
        ImGui.BeginChild("compact-navigation", new Vector2(0, 44 * scale), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorPos(new Vector2(7 * scale, 7 * scale));
        var items = new (string Label, MainSection Section)[]
        {
            (Loc.L("ホーム", "Home"), MainSection.Home),
            (Loc.L("連携", "Link"), MainSection.CharacterLink),
            (Loc.L("アニメ", "Emotes"), MainSection.Animations),
            (Loc.L("住宅", "Housing"), MainSection.Housing),
            (Loc.L("ギル", "Gil"), MainSection.Gil),
            (Loc.L("お得意", "Delivery"), MainSection.CustomDeliveries),
            (Loc.L("潜水艦", "Subs"), MainSection.Submarines),
            (Loc.L("設定", "Settings"), MainSection.Settings),
        }.Where(item => !plugin.Configuration.HiddenCompactMenuSections.Contains((int)item.Section)).ToArray();
        if (items.Length == 0)
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
            return;
        }
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (ImGui.GetContentRegionAvail().X - gap * (items.Length - 1)) / items.Length;
        foreach (var (label, section) in items)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, section == selectedSection
                ? new Vector4(0.12f, 0.42f, 0.62f, 0.95f)
                : new Vector4(0.12f, 0.14f, 0.19f, 0.96f));
            var buttonPosition = ImGui.GetCursorScreenPos();
            if (ImGui.Button($"{label}##compact-{section}", new Vector2(buttonWidth, 29 * scale)))
                OpenSection(section);
            DrawCompactAttentionBadge(section, buttonPosition, buttonWidth);
            ImGui.PopStyleColor();
            if (section != items[^1].Section)
                ImGui.SameLine();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawCompactAttentionBadge(MainSection section, Vector2 buttonPosition, float buttonWidth)
    {
        var (show, isUrgent, tooltip) = GetCompactSectionAttention(section);
        if (!show)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var radius = 7 * scale;
        var center = new Vector2(buttonPosition.X + buttonWidth - 7 * scale,
            buttonPosition.Y + 7 * scale);
        var draw = ImGui.GetWindowDrawList();
        var badgeColor = isUrgent
            ? new Vector4(0.94f, 0.18f, 0.14f, 1f)
            : new Vector4(1f, 0.7f, 0.12f, 1f);
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(badgeColor));
        draw.AddCircle(center, radius, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.62f, 1f)), 20,
            1.5f * scale);

        if (ImGui.IsMouseHoveringRect(center - new Vector2(radius), center + new Vector2(radius)))
            ImGui.SetTooltip(tooltip);
    }

    private void DrawMenuButton(string label, MainSection section, int badge = 0, string? detail = null)
    {
        var selected = selectedSection == section;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.42f, 0.62f, 0.92f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.5f, 0.72f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.12f, 0.16f, 0.65f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.2f, 0.26f, 0.9f));
        }

        var text = badge > 0 ? $"{label}    {badge}" : label;
        var buttonHeight = detail is null ? 46 : 54;
        var buttonPosition = ImGui.GetCursorScreenPos();
        if (ImGui.Button($"{text}{(detail is null ? string.Empty : "\n ")}##main-{section}",
                new Vector2(-1, buttonHeight * ImGuiHelpers.GlobalScale)))
            SelectSection(section);
        if (detail is not null)
        {
            var detailSize = ImGui.CalcTextSize(detail);
            var buttonWidth = ImGui.GetItemRectSize().X;
            var detailPosition = new Vector2(
                buttonPosition.X + MathF.Max(0, (buttonWidth - detailSize.X) / 2),
                buttonPosition.Y + 31 * ImGuiHelpers.GlobalScale);
            ImGui.GetWindowDrawList().AddText(detailPosition,
                ImGui.GetColorU32(new Vector4(0.42f, 0.82f, 1f, 1f)), detail);
        }
        ImGui.PopStyleColor(2);
    }

    private string? GetCharacterLinkMenuDetail()
    {
        var peers = plugin.CharacterLink.Peers;
        if (peers.Length == 0)
            return null;

        var firstName = peers[0].CharacterName;
        return peers.Length == 1
            ? $"[{firstName}]"
            : Loc.L($"[{firstName} ほか{peers.Length - 1}人]", $"[{firstName} +{peers.Length - 1}]");
    }

    private void DrawHome()
    {
        DrawPageTitle(Loc.T("Home"), Loc.T("DashboardDescription"));
        var cycle = plugin.GetCurrentCycle();
        var enabled = plugin.Configuration.Characters.Values.Where(x => x.EnabledForDisplay).ToList();
        var attention = GetHousingAttentionCount();
        var entries = enabled.Count(cycle.HasEntry);
        var housingStatus = cycle.Phase == LotteryPhase.Entry
            ? $"{Loc.T("EntryComplete")} {entries}{Loc.T("People")} / {Loc.T("NotEntered")} {enabled.Count - entries}{Loc.T("People")}"
            : $"{Loc.T("Checked")} {enabled.Count(x => cycle.HasEntry(x) && x.ResultChecked)}{Loc.T("People")} / {Loc.T("Unchecked")} {attention}{Loc.T("People")}";
        DrawHomeCard(Loc.T("HousingSummary"), housingStatus, GetPhaseDeadline(cycle),
            attention > 0 ? new Vector4(1f, 0.45f, 0.3f, 1f) : new Vector4(0.35f, 0.9f, 0.5f, 1f),
            MainSection.Housing);

        var demolitionWarnings = GetHousingDisplayEntries()
            .Where(x => x.LastEntry?.LastEnteredAt is { } entered &&
                        entered.AddDays(HousingDemolitionTracker.DemolitionPeriodDays) - DateTime.Now <= TimeSpan.FromDays(10))
            .OrderBy(x => x.LastEntry!.LastEnteredAt)
            .ToList();
        if (demolitionWarnings.Count > 0)
        {
            var nearest = demolitionWarnings[0];
            var remaining = nearest.LastEntry!.LastEnteredAt!.Value
                .AddDays(HousingDemolitionTracker.DemolitionPeriodDays) - DateTime.Now;
            var remainingText = remaining <= TimeSpan.Zero
                ? Loc.L("期限超過", "Overdue")
                : Loc.L($"残り{Math.Max(0, remaining.Days)}日{Math.Max(0, remaining.Hours)}時間",
                    $"{Math.Max(0, remaining.Days)}d {Math.Max(0, remaining.Hours)}h remaining");
            DrawHomeCard(Loc.L("住宅保持期限の警告", "Estate Demolition Warning"),
                Loc.L($"期限接近 {demolitionWarnings.Count}件", $"{demolitionWarnings.Count} estate(s) near deadline"),
                $"{FormatHouseAddress(nearest.Estate)} / {remainingText}",
                remaining <= TimeSpan.FromDays(5)
                    ? new Vector4(1f, 0.3f, 0.25f, 1f)
                    : new Vector4(1f, 0.72f, 0.2f, 1f),
                MainSection.Housing, "demolition-warning");
        }

        var peers = plugin.CharacterLink.Peers;
        var linkTitle = plugin.CharacterLink.RuntimeStopped ? Loc.T("EmergencyStopped") :
            Loc.Status(GetDisplayedStatus(plugin.CharacterLink.LastAction, x => x.LastAction));
        var leaderName = plugin.Configuration.Characters.TryGetValue(plugin.Configuration.LinkLeaderContentId, out var leader)
            ? leader.CharacterName : "—";
        DrawHomeCard(Loc.T("LinkStatus"), linkTitle,
            $"{Loc.T("Leader")} {leaderName} / {Loc.T("Connected")} {peers.Length + (Plugin.PlayerState.IsLoaded ? 1 : 0)}",
            plugin.CharacterLink.RuntimeStopped ? new Vector4(1f, 0.35f, 0.3f, 1f) : new Vector4(0.42f, 0.82f, 1f, 1f),
            MainSection.CharacterLink);

        var availableGil = plugin.Configuration.CharacterGil.Values.Aggregate(0UL, (total, character) =>
            total + character.Gil + character.Retainers.Values.Aggregate(0UL,
                (retainerTotal, retainer) => retainerTotal + retainer.Gil)) +
            plugin.Configuration.FreeCompanyGil.Values.Aggregate(0UL, (total, fc) => total + fc.Gil);
        var depositedGil = plugin.Configuration.Characters.Values
            .Where(x => cycle.HasEntry(x) && !x.ResultChecked)
            .Aggregate(0UL, (total, record) => total + record.BidGilDeposited);
        DrawHomeCard(Loc.T("Gil"), $"{Loc.T("TotalAssets")} {availableGil + depositedGil:N0} G",
            $"{Loc.T("Available")} {availableGil:N0} G / {Loc.T("LotteryDeposit")} {depositedGil:N0} G",
            new Vector4(0.95f, 0.78f, 0.25f, 1f), MainSection.Gil);

        var submarines = plugin.Configuration.FreeCompanyGil.Values
            .SelectMany(fc => fc.Submarines.Values)
            .ToArray();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var voyaging = submarines.Count(submarine => submarine.ReturnTimeUnix > nowUnix);
        var returned = submarines.Length - voyaging;
        var nextReturnUnix = submarines
            .Where(submarine => submarine.ReturnTimeUnix > nowUnix)
            .Select(submarine => (long)submarine.ReturnTimeUnix)
            .DefaultIfEmpty(0)
            .Min();
        var submarineStatus = submarines.Length == 0
            ? Loc.L("潜水艦情報なし", "No submersible data")
            : Loc.L($"航海中 {voyaging}隻 / 帰還済み {returned}隻",
                $"Voyaging {voyaging} / Returned {returned}");
        var submarineDetail = nextReturnUnix == 0
            ? Loc.L("帰還予定なし", "No scheduled returns")
            : Loc.L($"次の帰還 {DateTimeOffset.FromUnixTimeSeconds(nextReturnUnix).ToLocalTime():MM/dd HH:mm}",
                $"Next return {DateTimeOffset.FromUnixTimeSeconds(nextReturnUnix).ToLocalTime():MM/dd HH:mm}");
        DrawHomeCard(Loc.L("潜水艦管理", "Submersibles"), submarineStatus, submarineDetail,
            returned > 0 ? new Vector4(0.35f, 0.9f, 0.5f, 1f) : new Vector4(0.42f, 0.82f, 1f, 1f),
            MainSection.Submarines);

        var deliveryRecords = plugin.Configuration.CustomDeliveryCharacters.Values.ToArray();
        var incompleteCharacters = deliveryRecords.Count(record => record.RemainingWeeklyAllowances > 0);
        var deliveryStatus = deliveryRecords.Length == 0
            ? Loc.L("お得意様情報なし", "No custom delivery data")
            : Loc.L($"未消化 {incompleteCharacters}人 / 確認済み {deliveryRecords.Length}人",
                $"Incomplete {incompleteCharacters} / Checked {deliveryRecords.Length}");
        var currentDeliveryRecord = Plugin.PlayerState.IsLoaded &&
            plugin.Configuration.CustomDeliveryCharacters.TryGetValue(Plugin.PlayerState.ContentId, out var active)
                ? Loc.L($"このキャラクター：残り {active.RemainingWeeklyAllowances}回",
                    $"This character: {active.RemainingWeeklyAllowances} deliveries remaining")
                : Loc.L("ログイン中キャラクターの状況を取得", "Read the current character's delivery status");
        DrawHomeCard(Loc.L("お得意様取引", "Custom Deliveries"), deliveryStatus, currentDeliveryRecord,
            incompleteCharacters > 0 ? new Vector4(0.95f, 0.78f, 0.25f, 1f) :
                new Vector4(0.35f, 0.9f, 0.5f, 1f), MainSection.CustomDeliveries);
    }

    private void DrawHomeCard(string title, string mainText, string detail, Vector4 accent,
        MainSection destination, string idSuffix = "")
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.09f, 0.105f, 0.14f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.13f, 0.16f, 0.21f, 0.96f));
        var position = ImGui.GetCursorScreenPos();
        var height = 88 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button($"##home-card-{destination}-{idSuffix}", new Vector2(-1, height)))
            SelectSection(destination);
        ImGui.PopStyleColor(2);

        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        draw.AddRectFilled(position, position + new Vector2(4 * scale, height), ImGui.GetColorU32(accent), 3 * scale);
        draw.AddText(position + new Vector2(16, 10) * scale, ImGui.GetColorU32(new Vector4(0.72f, 0.82f, 0.92f, 1f)), title);
        draw.AddText(position + new Vector2(16, 34) * scale, ImGui.GetColorU32(accent), mainText);
        draw.AddText(position + new Vector2(16, 59) * scale, ImGui.GetColorU32(new Vector4(0.58f, 0.62f, 0.69f, 1f)), detail);
        ImGui.Spacing();
    }

    private void DrawHousing()
    {
        DrawPageTitle(Loc.T("Housing"), Loc.T("HousingDescription"));
        if (DrawSubMenuButton(Loc.L("抽選状態", "Lottery Status"), selectedHousingSection == HousingSection.Lottery))
            SelectHousingSection(HousingSection.Lottery);
        ImGui.SameLine();
        if (DrawSubMenuButton(Loc.L("保持期限", "Demolition Timer"), selectedHousingSection == HousingSection.Demolition))
            SelectHousingSection(HousingSection.Demolition);
        ImGui.SameLine();
        if (DrawSubMenuButton($"{Loc.L("空き土地", "Open Plots")} ({plugin.Configuration.OpenPlots.Count})", selectedHousingSection == HousingSection.OpenPlots))
            SelectHousingSection(HousingSection.OpenPlots);
        ImGui.SameLine();
        if (DrawSubMenuButton(Loc.L("表示キャラクター", "Displayed Characters"), selectedHousingSection == HousingSection.Characters))
            SelectHousingSection(HousingSection.Characters);
        ImGui.Separator();
        ImGui.Spacing();

        switch (selectedHousingSection)
        {
            case HousingSection.Lottery:
                DrawLotteryStatusTab();
                break;
            case HousingSection.Demolition:
                DrawHousingDemolitionTab();
                break;
            case HousingSection.OpenPlots:
                DrawOpenPlotsTab();
                break;
            case HousingSection.Characters:
                DrawDisplaySettingsTab(false);
                break;
        }
    }

    private void DrawHousingDemolitionTab()
    {
        ImGui.TextDisabled(Loc.L(
            $"各キャラクターで所有住宅の中に入り、一度だけ個人宅またはFC宅として登録してください。以後の入室日時を自動記録し、{HousingDemolitionTracker.DemolitionPeriodDays}日後までの残り時間を表示します。",
            $"Enter each character's estate and register it once as a personal or FC estate. Future visits are recorded automatically for the {HousingDemolitionTracker.DemolitionPeriodDays}-day period."));
        ImGui.TextDisabled(Loc.L(
            "ゲーム内の自動撤去が停止・延長された場合、実際の期限とは異なることがあります。",
            "The actual deadline may differ while automatic demolition is suspended or extended."));
        ImGui.Spacing();

        DrawEstateRegistrationControls();
        ImGui.Spacing();

        var selectedContentIds = plugin.Configuration.Characters.Values
            .Where(x => x.EnabledForDemolitionDisplay)
            .Select(x => x.ContentId)
            .ToHashSet();
        var estates = GetHousingDisplayEntries();

        if (selectedContentIds.Count == 0)
        {
            ImGui.TextDisabled(Loc.L(
                "表示するキャラクターが選択されていません。「表示キャラクター」で保持期限を選択してください。",
                "No characters are selected. Enable Demolition Timer under Displayed Characters."));
            return;
        }
        if (estates.Count == 0)
        {
            ImGui.TextDisabled(Loc.L(
                "選択したキャラクターの所有住宅はまだ登録されていません。対象キャラクターでハウス内に入り、上のボタンから登録してください。",
                "No estates are registered for the selected characters. Enter the estate with that character and use the buttons above."));
            return;
        }

        DrawHousingEstateBlock(Loc.L("個人宅", "Personal Estates"), OwnedEstateKind.Personal,
            estates.Where(x => x.Estate.EstateKind == OwnedEstateKind.Personal).ToList());
        ImGui.Spacing();
        DrawHousingEstateBlock(Loc.L("FC宅", "Free Company Estates"), OwnedEstateKind.FreeCompany,
            estates.Where(x => x.Estate.EstateKind == OwnedEstateKind.FreeCompany).ToList());
    }

    private void DrawEstateRegistrationControls()
    {
        var estate = plugin.HousingDemolition.CurrentIndoorEstate;
        if (estate.IsValid)
        {
            var world = Plugin.GetWorldName(estate.WorldId);
            var district = Plugin.GetDistrictName(estate.TerritoryTypeId);
            var address = Loc.IsEnglish
                ? $"{world} / {district} / Ward {estate.Ward}, Plot {estate.Plot}"
                : $"{world} / {district} 第{estate.Ward}区 {estate.Plot}番地";
            ImGui.TextWrapped(Loc.L($"現在のハウス：{address}", $"Current estate: {address}"));
        }
        else
        {
            ImGui.TextDisabled(Loc.L(
                "登録するキャラクターで対象ハウスの中に入ってください。",
                "Enter the estate with the character you want to register."));
        }

        ImGui.BeginDisabled(!estate.IsValid);
        if (ImGui.Button(Loc.L("現在地を個人宅として登録", "Register current estate as personal")))
            plugin.HousingDemolition.RegisterCurrentEstate(OwnedEstateKind.Personal);
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("現在地をFC宅として登録", "Register current estate as FC")))
            plugin.HousingDemolition.RegisterCurrentEstate(OwnedEstateKind.FreeCompany);
        ImGui.EndDisabled();

        var contentId = Plugin.PlayerState.ContentId;
        var hasPersonal = plugin.Configuration.HousingDemolition.TryGetValue(
            HousingDemolitionRecord.Key(contentId, OwnedEstateKind.Personal), out var personal) && personal.IsOwned;
        var hasFreeCompany = plugin.Configuration.HousingDemolition.TryGetValue(
            HousingDemolitionRecord.Key(contentId, OwnedEstateKind.FreeCompany), out var freeCompany) && freeCompany.IsOwned;
        ImGui.BeginDisabled(!hasPersonal);
        if (ImGui.SmallButton(Loc.L("このキャラの個人宅登録を解除", "Unregister this character's personal estate")))
            plugin.HousingDemolition.UnregisterCurrentEstate(OwnedEstateKind.Personal);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!hasFreeCompany);
        if (ImGui.SmallButton(Loc.L("このキャラのFC宅登録を解除", "Unregister this character's FC estate")))
            plugin.HousingDemolition.UnregisterCurrentEstate(OwnedEstateKind.FreeCompany);
        ImGui.EndDisabled();
    }

    private static void DrawHousingEstateBlock(string title, OwnedEstateKind kind,
        System.Collections.Generic.List<HousingDisplayEntry> estates)
    {
        ImGui.TextColored(kind == OwnedEstateKind.Personal
                ? new Vector4(0.42f, 0.82f, 1f, 1f)
                : new Vector4(0.72f, 0.58f, 1f, 1f), title);
        if (estates.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("表示対象なし", "No estates to display"));
            return;
        }
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable($"housing-demolition-{kind}", 4, flags))
            return;
        ImGui.TableSetupColumn(Loc.L("ハウス住所", "Estate Address"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.L("最終入室キャラ", "Last Character"));
        ImGui.TableSetupColumn(Loc.L("最終入室日", "Last entry"));
        ImGui.TableSetupColumn(Loc.L("残り時間", "Remaining"));
        ImGui.TableHeadersRow();
        foreach (var estate in estates)
        {
            var record = estate.Estate;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(FormatHouseAddress(record));
            if (ImGui.BeginPopupContextItem($"##housing-estate-menu-{kind}-{AddressKey(record)}",
                    ImGuiPopupFlags.MouseButtonRight))
            {
                ImGui.TextUnformatted(FormatHouseAddress(record));
                ImGui.Separator();
                var canTravel = Plugin.IsLifestreamAvailable();
                ImGui.BeginDisabled(!canTravel);
                if (ImGui.MenuItem(Loc.L("Lifestreamでこの住所へ移動", "Travel to this address with Lifestream")))
                    Plugin.TravelToHousingEstate(record);
                ImGui.EndDisabled();
                if (!canTravel && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.L("Lifestreamが読み込まれていません。", "Lifestream is not loaded."));
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(estate.LastEntry?.CharacterName ?? "—");
            ImGui.TableNextColumn();
            if (estate.LastEntry?.LastEnteredAt is null)
                ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), Loc.L("未入室", "Not recorded"));
            else
                ImGui.TextUnformatted(estate.LastEntry.LastEnteredAt.Value.ToString("yyyy/MM/dd HH:mm"));
            ImGui.TableNextColumn();
            if (estate.LastEntry?.LastEnteredAt is null)
                ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), Loc.L("入室して開始", "Enter to start"));
            else
            {
                var remaining = estate.LastEntry.LastEnteredAt.Value
                    .AddDays(HousingDemolitionTracker.DemolitionPeriodDays) - DateTime.Now;
                var text = remaining <= TimeSpan.Zero
                    ? Loc.L("期限超過", "Overdue")
                    : Loc.L($"{remaining.Days}日 {remaining.Hours}時間", $"{remaining.Days}d {remaining.Hours}h");
                var color = remaining <= TimeSpan.FromDays(5)
                    ? new Vector4(1f, 0.35f, 0.3f, 1f)
                    : remaining <= TimeSpan.FromDays(10)
                        ? new Vector4(1f, 0.72f, 0.2f, 1f)
                        : new Vector4(0.45f, 0.9f, 0.55f, 1f);
                ImGui.TextColored(color, text);
            }
        }
        ImGui.EndTable();
    }

    private System.Collections.Generic.List<HousingDisplayEntry> GetHousingDisplayEntries()
    {
        var selectedContentIds = plugin.Configuration.Characters.Values
            .Where(x => x.EnabledForDemolitionDisplay).Select(x => x.ContentId).ToHashSet();
        return plugin.Configuration.HousingDemolition.Values
            .Where(x => x.IsOwned && IsValidStoredAddress(x) &&
                        x.EstateKind is OwnedEstateKind.Personal or OwnedEstateKind.FreeCompany)
            .GroupBy(x => x.EstateKind == OwnedEstateKind.FreeCompany
                ? $"fc:{AddressKey(x)}" : $"personal:{x.ContentId}:{AddressKey(x)}")
            .Where(group => group.Any(x => selectedContentIds.Contains(x.ContentId)))
            .Select(group => new HousingDisplayEntry(
                group.OrderByDescending(x => x.LastOwnershipCheckedAt).First(),
                group.Where(x => x.LastEnteredAt.HasValue)
                    .OrderByDescending(x => x.LastEnteredAt).FirstOrDefault()))
            .OrderBy(x => x.Estate.EstateKind)
            .ThenBy(x => FormatHouseAddress(x.Estate), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsValidStoredAddress(HousingDemolitionRecord record) =>
        record.HouseWorldId is not (0 or ushort.MaxValue) &&
        record.HouseTerritoryTypeId is 339 or 340 or 341 or 641 or 979 &&
        record.HouseWard is >= 1 and <= 30 && record.HousePlot is >= 1 and <= 60;

    private sealed record HousingDisplayEntry(
        HousingDemolitionRecord Estate, HousingDemolitionRecord? LastEntry);

    private static string AddressKey(HousingDemolitionRecord record) =>
        $"{record.HouseWorldId}:{record.HouseTerritoryTypeId}:{record.HouseWard}:{record.HousePlot}";

    private static string FormatHouseAddress(HousingDemolitionRecord record)
    {
        var world = Plugin.GetWorldName(record.HouseWorldId);
        var district = Plugin.GetDistrictName(record.HouseTerritoryTypeId);
        return Loc.IsEnglish
            ? $"{world} / {district} / Ward {record.HouseWard}, Plot {record.HousePlot}"
            : $"{world} / {district} 第{record.HouseWard}区 {record.HousePlot}番地";
    }

    private static bool DrawSubMenuButton(string label, bool selected)
    {
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.42f, 0.62f, 0.85f));
        else
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.13f, 0.17f, 0.7f));
        var clicked = ImGui.Button(label, new Vector2(145 * ImGuiHelpers.GlobalScale, 32 * ImGuiHelpers.GlobalScale));
        ImGui.PopStyleColor();
        return clicked;
    }

    private static void DrawPageTitle(string title, string description)
    {
        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), title);
        ImGui.TextDisabled(description);
        ImGui.Spacing();
    }

    private void DrawGil()
    {
        DrawPageTitle(Loc.T("Gil"), Loc.T("GilDescription"));
        ImGui.TextDisabled(Loc.IsEnglish
            ? "Character gil updates while logged in. Retainer and FC values show the latest data loaded by the game."
            : "本人のギルはログイン中に更新します。リテイナーとFCチェストは、ゲーム内で確認した時点の最新額です。");

        var characters = plugin.Configuration.CharacterGil.Values.ToArray();
        var characterTotal = characters.Aggregate(0UL, (total, character) =>
            total + character.Gil + character.Retainers.Values.Aggregate(0UL,
                (retainerTotal, retainer) => retainerTotal + retainer.Gil));
        var fcTotal = plugin.Configuration.FreeCompanyGil.Values.Aggregate(0UL,
            (total, fc) => total + fc.Gil);
        var cycle = plugin.GetCurrentCycle();
        var depositedRecords = plugin.Configuration.Characters.Values
            .Where(x => cycle.HasEntry(x) && !x.ResultChecked)
            .ToArray();
        var depositedTotal = depositedRecords
            .Aggregate(0UL, (total, record) => total + record.BidGilDeposited);
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.25f, 1f),
            $"{Loc.T("TotalAssets")}　{characterTotal + fcTotal + depositedTotal:N0} G");
        ImGui.SameLine();
        ImGui.TextDisabled($"（{Loc.T("Available")} {characterTotal + fcTotal:N0} G / {Loc.T("LotteryDeposit")} {depositedTotal:N0} G）");
        var unknownDeposits = depositedRecords.Count(x => x.BidGilDeposited == 0);
        if (unknownDeposits > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), Loc.L($"金額未取得 {unknownDeposits}件", $"Amount unavailable: {unknownDeposits}"));
        }
        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), Loc.L("キャラクター・リテイナー", "Characters & Retainers"));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("gil-characters", 4, flags))
        {
            ImGui.TableSetupColumn(Loc.L("キャラクター／リテイナー", "Character / Retainer"),
                ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.7f, 0);
            ImGui.TableSetupColumn(Loc.L("ワールド", "World"), ImGuiTableColumnFlags.WidthStretch, 1f, 1);
            ImGui.TableSetupColumn(Loc.L("所持ギル", "Gil"),
                ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                130 * ImGuiHelpers.GlobalScale, 2);
            ImGui.TableSetupColumn(Loc.L("最終確認", "Last Updated"),
                ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort,
                145 * ImGuiHelpers.GlobalScale, 3);
            ImGui.TableHeadersRow();
            var sortedCharacters = SortGilCharacters(characters);
            foreach (var character in sortedCharacters)
            {
                DrawGilRow(character.CharacterName, character.WorldName, character.Gil, character.UpdatedAt, false);
                var retainersWithGil = character.Retainers.Values
                    .Where(x => x.Gil > 0)
                    .OrderBy(x => x.Name)
                    .ToArray();
                foreach (var retainer in retainersWithGil)
                    DrawGilRow($"　└ {retainer.Name}", Loc.L("リテイナー", "Retainer"), retainer.Gil, retainer.UpdatedAt, true);
                if (plugin.Configuration.Characters.TryGetValue(character.ContentId, out var lottery) &&
                    cycle.HasEntry(lottery) && !lottery.ResultChecked && lottery.BidGilDeposited > 0)
                    DrawGilRow(Loc.L("　└ ハウジング抽選預かり中", "　└ Housing lottery deposit"),
                        lottery.PlotAddress ?? Loc.L("応募した土地", "Entered plot"), lottery.BidGilDeposited,
                        lottery.LastCheckedAt, true, new Vector4(0.35f, 0.8f, 1f, 1f));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), Loc.L("FCチェスト", "Free Company Chests"));
        ImGui.TextDisabled(Loc.IsEnglish
            ? "Each Free Company is displayed and counted once, even when multiple characters belong to it."
            : "同じFCに複数キャラクターが所属していても、FCごとに1件だけ表示・集計します。");
        if (ImGui.BeginTable("gil-free-companies", 4, flags))
        {
            ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn(Loc.L("ワールド／確認キャラ", "World / Checked By"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn(Loc.L("チェスト内ギル", "Chest Gil"), ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("最終確認", "Last Updated"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var fc in plugin.Configuration.FreeCompanyGil.Values.OrderBy(x => x.Name))
                DrawGilRow(DisplayFcName(fc.Name), $"{fc.WorldName} / {fc.LastCheckedByName}", fc.Gil, fc.UpdatedAt, false);
            ImGui.EndTable();
        }

    }

    private void DrawSubmarines()
    {
        DrawPageTitle(Loc.L("潜水艦管理", "Submersible Management"), Loc.L(
            "FCごとの潜水艦の発着状況と帰還予定を管理します。",
            "Track submersible voyages and return schedules for each Free Company."));
        ImGui.TextDisabled(Loc.L(
            "カンパニーワークショップで潜水艦管理を開くと、名称と帰還時刻を自動更新します。",
            "Open Submersible Management in the company workshop to update names and return times."));
        ImGui.Separator();
        if (ImGui.BeginTabBar("submarine-tabs"))
        {
            if (ImGui.BeginTabItem(Loc.L("運航状況", "Voyage Status")))
            {
                DrawSubmarineStatus();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Loc.L("財宝収益", "Treasure Revenue")))
            {
                DrawSubmarineTreasureRevenue();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawSubmarineStatus()
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        var submarines = plugin.Configuration.FreeCompanyGil.Values
            .SelectMany(fc => fc.Submarines.Values.Select(submarine => (fc, submarine)))
            .OrderBy(x => x.fc.Name).ThenBy(x => x.submarine.Name).ToArray();
        if (submarines.Length == 0)
        {
            ImGui.TextDisabled(Loc.L("潜水艦情報はまだ確認されていません。", "No submersible data has been observed yet."));
        }
        else if (ImGui.BeginTable("gil-submarines", 5, flags))
        {
            ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthStretch, 1.45f);
            ImGui.TableSetupColumn(Loc.L("潜水艦", "Submersible"), ImGuiTableColumnFlags.WidthStretch, 1.25f);
            ImGui.TableSetupColumn(Loc.L("状態", "Status"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("帰還時刻", "Returns At"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("残り時間", "Remaining"), ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var (fc, submarine) in submarines)
                DrawSubmarineRow(fc, submarine);
            ImGui.EndTable();
        }
    }

    private void DrawSubmarineTreasureRevenue()
    {
        ImGui.TextDisabled(Loc.L(
            "帰還報告を開いた時点から、沈没船の財宝8種類のNPC換金額を記録します。",
            "Records NPC sale value for the eight salvaged treasure items when a voyage report is opened."));
        if (ImGui.SmallButton("◀##treasure-month-prev"))
            treasureMonth = treasureMonth.AddMonths(-1);
        ImGui.SameLine();
        ImGui.TextUnformatted(treasureMonth.ToString(Loc.IsEnglish ? "MMMM yyyy" : "yyyy年 M月",
            Loc.IsEnglish ? EnglishCulture : JapaneseCulture));
        ImGui.SameLine();
        if (ImGui.SmallButton("▶##treasure-month-next") &&
            treasureMonth < new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1))
            treasureMonth = treasureMonth.AddMonths(1);

        var monthStart = new DateTimeOffset(treasureMonth, TimeZoneInfo.Local.GetUtcOffset(treasureMonth));
        var monthEnd = monthStart.AddMonths(1);
        var companies = plugin.Configuration.FreeCompanyGil.Values
            .Where(x => x.TreasureVoyages.Count > 0)
            .OrderBy(x => x.Name).ToArray();
        if (companies.Length == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Loc.L("財宝の帰還記録はまだありません。", "No treasure voyage has been recorded yet."));
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("submarine-treasure-revenue", 5, flags))
            return;
        ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Loc.L("トータル", "Total"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("選択月", "Selected Month"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("1航行平均", "Per Voyage"), ImGuiTableColumnFlags.WidthFixed, 135 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("換金効率", "Efficiency"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var fc in companies)
        {
            var voyages = fc.TreasureVoyages;
            var total = voyages.Aggregate(0UL, (sum, voyage) => sum + voyage.TreasureGil);
            var monthly = voyages.Where(voyage =>
            {
                var returned = DateTimeOffset.FromUnixTimeSeconds(voyage.ReturnedAtUnix);
                return returned >= monthStart.ToUniversalTime() && returned < monthEnd.ToUniversalTime();
            }).Aggregate(0UL, (sum, voyage) => sum + voyage.TreasureGil);
            var average = voyages.Count == 0 ? 0UL : total / (ulong)voyages.Count;
            var totalDays = voyages.Sum(voyage => voyage.DepartedAtUnix > 0 && voyage.ReturnedAtUnix > voyage.DepartedAtUnix
                ? (voyage.ReturnedAtUnix - voyage.DepartedAtUnix) / 86400d : 0d);
            var efficiency = totalDays > 0 ? (ulong)Math.Round(total / totalDays) : 0UL;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{DisplayFcName(fc.Name)}（{fc.WorldName}）");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.L($"記録航行数：{voyages.Count}", $"Recorded voyages: {voyages.Count}"));
            DrawTreasureAmount(total);
            DrawTreasureAmount(monthly);
            DrawTreasureAmount(average);
            ImGui.TableNextColumn();
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.25f, 1f), $"{efficiency:N0} G/日");
        }
        ImGui.EndTable();
    }

    private static void DrawTreasureAmount(ulong amount)
    {
        ImGui.TableNextColumn();
        var text = $"{amount:N0} G";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.25f, 1f), text);
    }

    private static void DrawSubmarineRow(FreeCompanyGilRecord fc, SubmarineRecord submarine)
    {
        var now = DateTimeOffset.UtcNow;
        var returnAt = submarine.ReturnTimeUnix == 0
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeSeconds(submarine.ReturnTimeUnix).ToLocalTime();
        var underway = returnAt.HasValue && returnAt.Value > now.ToLocalTime();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{DisplayFcName(fc.Name)}（{fc.WorldName}）");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(submarine.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.L("右クリックで航路を表示", "Right-click to show route"));
        if (ImGui.BeginPopupContextItem($"submarine-route-{fc.FreeCompanyId}-{submarine.Name}"))
        {
            ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), submarine.Name);
            ImGui.TextDisabled($"{DisplayFcName(fc.Name)}（{fc.WorldName}）");
            ImGui.Separator();
            ImGui.TextUnformatted(Loc.L("航行航路", "Voyage Route"));
            var routeNames = GetSubmarineRouteNames(submarine.RoutePointIds);
            if (routeNames.Length == 0)
                ImGui.TextDisabled(Loc.L("航路情報なし", "No route information"));
            else
                for (var index = 0; index < routeNames.Length; index++)
                    ImGui.BulletText($"{index + 1}. {routeNames[index]}");
            ImGui.EndPopup();
        }
        ImGui.TableNextColumn();
        ImGui.TextColored(underway ? new Vector4(0.35f, 0.8f, 1f, 1f) : new Vector4(0.3f, 0.9f, 0.45f, 1f),
            underway ? Loc.L("航海中", "Voyaging") : Loc.L("帰還済", "Returned"));
        ImGui.TableNextColumn(); ImGui.TextDisabled(returnAt?.ToString("MM/dd (ddd) HH:mm") ?? "—");
        ImGui.TableNextColumn();
        if (!underway)
            ImGui.TextDisabled("—");
        else
        {
            var remaining = returnAt!.Value - now.ToLocalTime();
            ImGui.TextUnformatted(remaining.TotalDays >= 1
                ? $"{(int)remaining.TotalDays}日 {remaining.Hours:D2}:{remaining.Minutes:D2}"
                : $"{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}");
        }
    }

    private static string[] GetSubmarineRouteNames(byte[] pointIds)
    {
        if (pointIds.Length == 0)
            return [];
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.SubmarineExploration>();
        return pointIds.Select(id =>
        {
            var row = sheet.GetRowOrDefault(id);
            if (row == null)
                return $"#{id}";
            var destination = row.Value.Destination.ToString();
            return string.IsNullOrWhiteSpace(destination) ? $"#{id}" : destination;
        }).ToArray();
    }

    private static string DisplayFcName(string name)
    {
        if (!name.StartsWith("FC ", StringComparison.Ordinal) || name.Length <= 3)
            return name;
        return name.AsSpan(3).ToString().All(Uri.IsHexDigit) ? Loc.L("不明なFC", "Unknown FC") : name;
    }

    private static void DrawGilRow(string name, string world, uint gil, DateTime updatedAt, bool muted,
        Vector4? amountColor = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (muted) ImGui.TextDisabled(name); else ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(world);
        ImGui.TableNextColumn();
        var amountText = $"{gil:N0} G";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
            Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(amountText).X));
        ImGui.TextColored(amountColor ?? new Vector4(0.95f, 0.78f, 0.25f, 1f), amountText);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(updatedAt == default ? "—" : FormatDate(updatedAt));
    }

    private static System.Collections.Generic.IEnumerable<CharacterGilRecord> SortGilCharacters(
        CharacterGilRecord[] characters)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount == 0)
            return characters.OrderBy(x => x.CharacterName).ThenBy(x => x.WorldName);

        var spec = specs.Specs[0];
        var descending = spec.SortDirection == ImGuiSortDirection.Descending;
        return spec.ColumnUserID switch
        {
            1 => descending
                ? characters.OrderByDescending(x => x.WorldName).ThenBy(x => x.CharacterName)
                : characters.OrderBy(x => x.WorldName).ThenBy(x => x.CharacterName),
            2 => descending
                ? characters.OrderByDescending(x => x.Gil).ThenBy(x => x.CharacterName)
                : characters.OrderBy(x => x.Gil).ThenBy(x => x.CharacterName),
            _ => descending
                ? characters.OrderByDescending(x => x.CharacterName).ThenBy(x => x.WorldName)
                : characters.OrderBy(x => x.CharacterName).ThenBy(x => x.WorldName),
        };
    }

    private void DrawSettings()
    {
        DrawPageTitle(Loc.T("Settings"), Loc.T("SettingsDescription"));
        ImGui.TextUnformatted(Loc.T("Language"));
        var languageIndex = plugin.Configuration.Language == "en" ? 1 : 0;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##language", ref languageIndex,
                new[] { Loc.T("Japanese"), Loc.T("English") }, 2))
        {
            plugin.Configuration.Language = languageIndex == 1 ? "en" : "ja";
            plugin.SaveSharedSettings();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.T("RestartNotRequired"));
        ImGui.Spacing();
        var showChatMessages = plugin.Configuration.ShowChatMessages;
        if (ImGui.Checkbox(Loc.L("チャット欄にAltMateの通知を表示する", "Show AltMate notifications in chat"),
                ref showChatMessages))
        {
            plugin.Configuration.ShowChatMessages = showChatMessages;
            plugin.SaveSharedSettings();
        }
        ImGui.TextDisabled(Loc.L(
            "無効時もAltMateのログファイルへの記録と画面内ステータスは継続します。",
            "Disabling this does not affect AltMate log files or in-window status messages."));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.L("通常表示の背景透明度", "Expanded view background opacity"));
        var opacityPercent = (int)MathF.Round(plugin.Configuration.WindowBackgroundOpacity * 100f);
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("##window-background-opacity", ref opacityPercent, 0, 100, "%d%%",
                ImGuiSliderFlags.AlwaysClamp))
        {
            var opacity = opacityPercent / 100f;
            plugin.Configuration.WindowBackgroundOpacity = opacity;
            BgAlpha = opacity;
            plugin.SaveSharedSettings();
        }
        ImGui.TextDisabled(Loc.L(
            "最大化した通常画面に適用されます。",
            "Applied to the expanded window."));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.L("最小化表示の背景透明度", "Compact view background opacity"));
        var compactOpacityPercent = (int)MathF.Round(plugin.Configuration.CompactWindowBackgroundOpacity * 100f);
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("##compact-window-background-opacity", ref compactOpacityPercent, 0, 100, "%d%%",
                ImGuiSliderFlags.AlwaysClamp))
        {
            var compactOpacity = compactOpacityPercent / 100f;
            plugin.Configuration.CompactWindowBackgroundOpacity = compactOpacity;
            if (compactMode)
                BgAlpha = compactOpacity;
            plugin.SaveSharedSettings();
        }
        ImGui.TextDisabled(Loc.L(
            "最小化したコントロールバーに適用されます。",
            "Applied to the compact control bar."));
        ImGui.Spacing();
        var compactMainMenu = plugin.Configuration.CompactMainMenu;
        if (ImGui.Checkbox(Loc.L("通常表示の左メニューを小さくする", "Use compact sidebar in expanded view"),
                ref compactMainMenu))
        {
            plugin.Configuration.CompactMainMenu = compactMainMenu;
            plugin.SaveSharedSettings();
        }
        ImGui.TextDisabled(Loc.L(
            "機能はそのままに、ロゴとメニュー幅を縮小します。",
            "Reduces the logo and sidebar width without hiding any features."));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.L("最小化表示のメニュー項目", "Compact view menu items"));
        ImGui.TextDisabled(Loc.L(
            "最小化バーの下段に表示する項目を選択します。",
            "Choose the items shown in the lower row of the compact bar."));
        var compactMenuItems = new (string Label, MainSection Section)[]
        {
            (Loc.L("ホーム", "Home"), MainSection.Home),
            (Loc.L("連携", "Link"), MainSection.CharacterLink),
            (Loc.L("アニメ", "Emotes"), MainSection.Animations),
            (Loc.L("住宅", "Housing"), MainSection.Housing),
            (Loc.L("ギル", "Gil"), MainSection.Gil),
            (Loc.L("お得意", "Delivery"), MainSection.CustomDeliveries),
            (Loc.L("潜水艦", "Subs"), MainSection.Submarines),
            (Loc.L("設定", "Settings"), MainSection.Settings),
        };
        for (var index = 0; index < compactMenuItems.Length; index++)
        {
            var item = compactMenuItems[index];
            var visible = !plugin.Configuration.HiddenCompactMenuSections.Contains((int)item.Section);
            if (ImGui.Checkbox($"{item.Label}##compact-menu-visible-{item.Section}", ref visible))
            {
                if (visible)
                    plugin.Configuration.HiddenCompactMenuSections.Remove((int)item.Section);
                else
                    plugin.Configuration.HiddenCompactMenuSections.Add((int)item.Section);
                plugin.SaveSharedSettings();
            }
            if (index % 4 != 3 && index != compactMenuItems.Length - 1)
                ImGui.SameLine();
        }
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Command"));
        ImGui.TextDisabled("/altmate");
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("DataStorage"));
        ImGui.TextWrapped(Loc.T("DataStorageDescription"));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Privacy"));
        ImGui.TextWrapped(Loc.T("PrivacyDescription"));
    }

    private void DrawAnimations()
    {
        DrawPageTitle(Loc.T("Animation"), Loc.L(
            "ゲーム内エモートとPenumbraで差し替えたアニメーションを再生します。",
            "Play in-game emotes and Penumbra-replaced animations."));
        if (ImGui.BeginTabBar("animation-tabs"))
        {
            if (ImGui.BeginTabItem(Loc.L("ゲーム内エモート", "In-game Emotes")))
            {
                DrawGameEmotes();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Penumbra"))
            {
                DrawPenumbraAnimations();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawGameEmotes()
    {
        ImGui.TextDisabled(Loc.L(
            "全ゲーム内エモートを表示します。未習得エモートはグループポーズ中のみ再生できます。",
            "Shows all in-game emotes. Locked emotes can only be played while in Group Pose."));
        if (!gameEmoteListLoaded)
        {
            gameEmotes = plugin.Animations.LoadGameEmotes().ToArray();
            gameEmoteListLoaded = true;
        }
        if (ImGui.Button($"{Loc.T("RefreshList")}##game-emotes"))
            gameEmotes = plugin.Animations.LoadGameEmotes().ToArray();
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Animations.Status);
        ImGui.Spacing();

        DrawAnimationTargetSelector("game");
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##game-emote-filter", Loc.L("エモート名で絞り込み", "Filter by emote name"), ref gameEmoteFilter, 100);
        ImGui.Separator();

        var filtered = gameEmotes.Where(x => string.IsNullOrWhiteSpace(gameEmoteFilter) ||
            x.Name.Contains(gameEmoteFilter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (filtered.Length == 0)
        {
            ImGui.TextDisabled(Loc.L("表示できるエモートがありません。", "No emotes to display."));
            return;
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("game-emotes", 3, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Emote"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 68 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var emote in filtered)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(emote.IsUnlocked
                ? emote.Name
                : Loc.L($"{emote.Name}（未習得・GPoseのみ）", $"{emote.Name} (Locked, GPose only)"));
            ImGui.TableNextColumn(); ImGui.TextDisabled(emote.Id.ToString());
            ImGui.TableNextColumn();
            var canPlay = emote.IsUnlocked || plugin.Animations.IsInGroupPose;
            if (!canPlay)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{Loc.T("Play")}##play-game-emote-{emote.Id}"))
                plugin.CharacterLink.PlayEmote(emote.Id, animationTargetContentId);
            if (!canPlay)
                ImGui.EndDisabled();
        }
        ImGui.EndTable();
    }

    private void DrawPenumbraAnimations()
    {
        ImGui.TextDisabled(Plugin.CurrentConfiguration?.Language == "en"
            ? "The list reflects the active Penumbra collection, options, and winning mod priority on this client."
            : "このクライアントで実際に有効なPenumbraコレクション・オプション・Mod優先度を表示します。");

        if (!plugin.Animations.IsPenumbraLoaded)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), Loc.T("PenumbraMissing"));
            return;
        }

        if (!animationListLoaded)
        {
            animationEmotes = plugin.Animations.LoadActiveEmotes().ToArray();
            animationListLoaded = true;
        }

        if (ImGui.Button(Loc.T("RefreshList")))
        {
            animationEmotes = plugin.Animations.LoadActiveEmotes().ToArray();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Animations.Status);
        ImGui.Spacing();

        DrawAnimationTargetSelector("penumbra");

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##animation-filter", Loc.T("FilterEmote"), ref animationFilter, 100);
        ImGui.Separator();

        if (animationEmotes.Length == 0)
        {
            ImGui.TextDisabled(Loc.T("AnimationEmpty"));
            return;
        }

        var filtered = animationEmotes.Where(x => string.IsNullOrWhiteSpace(animationFilter) ||
                     x.Name.Contains(animationFilter, StringComparison.CurrentCultureIgnoreCase) ||
                     x.ModName.Contains(animationFilter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("active-animation-emotes", 4, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Emote"), ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn(Loc.T("SourceMod"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 68 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var emote in filtered)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(emote.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(emote.ModName);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(emote.Id.ToString());
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Loc.T("Play")}##play-emote-{emote.Id}"))
                plugin.CharacterLink.PlayEmote(emote.Id, animationTargetContentId);
        }
        ImGui.EndTable();
    }

    private void DrawAnimationTargetSelector(string id)
    {
        var targets = GetAnimationTargets();
        if (animationTargetContentId == 0 || targets.All(x => x.ContentId != animationTargetContentId))
            animationTargetContentId = Plugin.PlayerState.ContentId;
        var targetPreview = targets.FirstOrDefault(x => x.ContentId == animationTargetContentId).Label ?? Loc.T("Character");
        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo($"{Loc.T("PlayCharacter")}##animation-target-{id}", targetPreview))
            return;
        foreach (var target in targets)
        {
            var selected = target.ContentId == animationTargetContentId;
            if (ImGui.Selectable($"{target.Label}##animation-target-{id}-{target.ContentId}", selected))
                animationTargetContentId = target.ContentId;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private (ulong ContentId, string Label)[] GetAnimationTargets()
    {
        var localId = Plugin.PlayerState.ContentId;
        var localName = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : "このキャラクター";
        var localRole = localId == plugin.Configuration.LinkLeaderContentId ? Loc.T("Leader") : Loc.T("Follower");
        return new[] { (ContentId: localId, Label: $"{localName}（この画面・{localRole}）") }
            .Concat(plugin.CharacterLink.Peers.Select(x =>
                (ContentId: x.ContentId, Label: $"{x.CharacterName}（{(x.ContentId == plugin.Configuration.LinkLeaderContentId ? Loc.T("Leader") : Loc.T("Follower"))}）")))
            .GroupBy(x => x.ContentId).Select(x => x.First()).ToArray();
    }

    private void DrawCharacterLink()
    {
        DrawPageTitle(Loc.T("Link"), Loc.T("LinkDescription"));
        ImGui.TextDisabled($"{Loc.T("LoadedVersion")}：{Plugin.PluginInterface.Manifest.AssemblyVersion}");

        if (plugin.CharacterLink.RuntimeStopped)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.52f, 0.28f, 0.95f));
            if (ImGui.Button(Loc.T("ResumeLink"), new Vector2(180 * ImGuiHelpers.GlobalScale, 38 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.Resume();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), Loc.T("EmergencyStopped"));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.14f, 0.12f, 0.95f));
            if (ImGui.Button(Loc.T("StopAll"), new Vector2(180 * ImGuiHelpers.GlobalScale, 38 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.EmergencyStop();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f),
                Loc.Status(GetDisplayedStatus(plugin.CharacterLink.LastAction, x => x.LastAction)));
        }

        ImGui.Spacing();
        var linkEnabled = plugin.Configuration.LinkEnabled;
        ImGui.PushStyleColor(ImGuiCol.Button, linkEnabled
            ? new Vector4(0.72f, 0.14f, 0.12f, 0.95f)
            : new Vector4(0.12f, 0.52f, 0.28f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, linkEnabled
            ? new Vector4(0.86f, 0.2f, 0.16f, 1f)
            : new Vector4(0.16f, 0.66f, 0.36f, 1f));
        if (ImGui.Button(linkEnabled
                ? Loc.L("連携操作を停止", "Stop linked controls")
                : Loc.L("連携操作を開始", "Start linked controls"),
                new Vector2(220 * ImGuiHelpers.GlobalScale, 42 * ImGuiHelpers.GlobalScale)))
        {
            plugin.Configuration.LinkEnabled = !linkEnabled;
            plugin.Configuration.Save();
            if (!linkEnabled && plugin.CharacterLink.RuntimeStopped)
                plugin.CharacterLink.Resume();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.PopStyleColor(2);
        ImGui.SameLine();
        ImGui.TextColored(linkEnabled
                ? new Vector4(0.45f, 0.9f, 0.55f, 1f)
                : new Vector4(0.72f, 0.72f, 0.76f, 1f),
            linkEnabled ? Loc.L("連携中", "Linked") : Loc.L("停止中", "Stopped"));

        var connectedLeader = plugin.CharacterLink.Peers.FirstOrDefault(x =>
            x.ContentId == plugin.Configuration.LinkLeaderContentId);
        var currentLeader = connectedLeader is not null
            ? $"{connectedLeader.CharacterName} @ {connectedLeader.WorldName}"
            : plugin.Configuration.Characters.TryGetValue(
                plugin.Configuration.LinkLeaderContentId, out var leaderRecord)
                ? $"{leaderRecord.CharacterName} @ {leaderRecord.WorldName}"
                : Loc.T("SelectCharacter");
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(Loc.T("Leader"), currentLeader))
        {
            foreach (var record in OrderedCharacters())
            {
                var selected = record.ContentId == plugin.Configuration.LinkLeaderContentId;
                if (ImGui.Selectable($"{record.CharacterName} @ {record.WorldName}##leader-{record.ContentId}", selected))
                {
                    plugin.Configuration.LinkLeaderContentId = record.ContentId;
                    plugin.Configuration.Save();
                    plugin.CharacterLink.SettingsChanged();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled(plugin.CharacterLink.IsLeader
            ? Loc.T("ThisIsLeader")
            : Loc.T("ThisIsFollower"));

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), Loc.L("フォロワーFPS制限", "Follower FPS limit"));
        ImGui.TextDisabled(Loc.L(
            "リーダー側から、接続中のフォロワークライアントだけを制限します。リーダー自身は変更しません。",
            "The leader limits connected follower clients only. The leader itself is not changed."));
        var roleFps = plugin.Configuration.RoleBasedFpsEnabled;
        if (plugin.CharacterLink.IsLeader &&
            ImGui.Checkbox(Loc.L("フォロワーFPS制限を有効にする", "Enable follower FPS limit"), ref roleFps))
        {
            plugin.Configuration.RoleBasedFpsEnabled = roleFps;
            plugin.SaveSharedSettings();
            plugin.RoleBasedFps.ApplyNow();
        }
        else if (!plugin.CharacterLink.IsLeader)
        {
            ImGui.TextDisabled(roleFps
                ? Loc.L("リーダー側で有効になっています。", "Enabled by the leader.")
                : Loc.L("リーダー側で無効になっています。", "Disabled by the leader."));
        }
        if (roleFps && plugin.CharacterLink.IsLeader)
        {
            var followerFpsIndex = FpsLimitToIndex(plugin.Configuration.FollowerFpsLimit);
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo(Loc.L("フォロワー上限", "Follower limit"), ref followerFpsIndex, FpsLimitLabels(), 3))
            {
                plugin.Configuration.FollowerFpsLimit = FpsIndexToLimit(followerFpsIndex);
                plugin.SaveSharedSettings();
                plugin.RoleBasedFps.ApplyNow();
            }
        }
        ImGui.TextDisabled($"{Loc.T("Status")}：{Loc.Status(plugin.RoleBasedFps.Status)}");

        if (!plugin.CharacterLink.IsLeader)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.42f, 0.62f, 0.92f));
            if (ImGui.Button(Loc.T("MoveToLeader"), new Vector2(180 * ImGuiHelpers.GlobalScale, 32 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.MoveToLeader();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.Status(plugin.CharacterLink.WorldLinkStatus));
            ImGui.TextDisabled(Loc.T("DifferentWorldHelp"));
            ImGui.Spacing();
            if (ImGui.Button(Loc.IsEnglish ? "Test follow" : "追従テスト"))
                plugin.CharacterLink.TestFollow();
            ImGui.SameLine();
            if (ImGui.Button(Loc.IsEnglish ? "Test pillion" : "相乗りテスト"))
                plugin.CharacterLink.TestRidePillion();
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.IsEnglish ? "Runs one test on this follower." : "フォロワー側で1回だけ動作を試します。");
            ImGui.TextDisabled($"{(Loc.IsEnglish ? "Diagnostic" : "診断")}：{plugin.CharacterLink.DiagnosticMessage}");
        }

        ImGui.Separator();
        var autoFollow = plugin.Configuration.AutoFollowEnabled;
        if (ImGui.Checkbox(Loc.T("AutoFollow"), ref autoFollow))
        {
            plugin.Configuration.AutoFollowEnabled = autoFollow;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var autoRide = plugin.Configuration.AutoRidePillionEnabled;
        if (ImGui.Checkbox(Loc.T("AutoRide"), ref autoRide))
        {
            plugin.Configuration.AutoRidePillionEnabled = autoRide;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var mountFallback = plugin.Configuration.MountRouletteFallbackEnabled;
        if (ImGui.Checkbox(Loc.L("相乗りできない場合はマウントルーレットを使用", "Use Mount Roulette when pillion is unavailable"), ref mountFallback))
        {
            plugin.Configuration.MountRouletteFallbackEnabled = mountFallback;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var pauseCombat = plugin.Configuration.PauseLinkInCombat;
        if (ImGui.Checkbox(Loc.T("PauseCombat"), ref pauseCombat))
        {
            plugin.Configuration.PauseLinkInCombat = pauseCombat;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var followDistance = plugin.Configuration.FollowStartDistance;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("FollowDistance"), ref followDistance, 1f, 15f, "%.1f m"))
        {
            plugin.Configuration.FollowStartDistance = followDistance;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var vnavRecovery = plugin.Configuration.VnavmeshStuckRecoveryEnabled;
        if (ImGui.Checkbox(Loc.L("追従が詰まった時にvnavmeshで復帰", "Use vnavmesh when follow gets stuck"), ref vnavRecovery))
        {
            plugin.Configuration.VnavmeshStuckRecoveryEnabled = vnavRecovery;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.CharacterLink.IsVnavmeshLoaded
            ? Loc.L("vnavmesh：接続済み", "vnavmesh: Connected")
            : Loc.L("vnavmesh：未接続（通常追従のみ）", "vnavmesh: Not connected (direct follow only)"));
        var syncInteraction = plugin.Configuration.SyncLeaderInteractionEnabled;
        if (ImGui.Checkbox(Loc.L("リーダーが操作したNPC・オブジェクトをフォロワーも操作",
                "Followers interact with the NPC/object used by the leader"), ref syncInteraction))
        {
            plugin.Configuration.SyncLeaderInteractionEnabled = syncInteraction;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled(Loc.L("TextAdvanceを両方で有効にすると、受注・会話送り・報告も続けて処理できます。",
            "With TextAdvance enabled on both clients, quest acceptance, dialogue and completion can continue automatically."));

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), Loc.T("CombatLink"));
        ImGui.TextDisabled(Loc.T("CombatLinkHelp"));
        var combatLink = plugin.Configuration.CombatLinkEnabled;
        if (ImGui.Checkbox(Loc.T("LinkCombatStart"), ref combatLink))
        {
            plugin.Configuration.CombatLinkEnabled = combatLink;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var useBmr = plugin.Configuration.UseBossModReborn;
        if (ImGui.Checkbox(Loc.IsEnglish ? "BossMod Reborn (movement and targeting)" : "BossMod Reborn（移動・ターゲット）", ref useBmr))
        {
            plugin.Configuration.UseBossModReborn = useBmr;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var useRsr = plugin.Configuration.UseRotationSolverReborn;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Rotation Solver Reborn (combat rotation)" : "Rotation Solver Reborn（攻撃ローテーション）", ref useRsr))
        {
            plugin.Configuration.UseRotationSolverReborn = useRsr;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var stopDelay = plugin.Configuration.CombatStopDelaySeconds;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("StopAfterCombat"), ref stopDelay, 0f, 15f,
                Plugin.CurrentConfiguration?.Language == "en" ? "%.1f sec" : "%.1f 秒"))
        {
            plugin.Configuration.CombatStopDelaySeconds = stopDelay;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled($"{Loc.T("Status")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.CombatStatus, x => x.CombatStatus))}");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"{Loc.T("OccultLink")}##occult-link"))
        {
            ImGui.Indent(12 * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled(Loc.T("OccultHelp"));
            var occultSync = plugin.Configuration.OccultAethernetSyncEnabled;
            if (ImGui.Checkbox(Loc.IsEnglish ? "Sync leader's aetheryte travel" : "リーダーのエーテライト移動にフォロワーを連動", ref occultSync))
            {
                plugin.Configuration.OccultAethernetSyncEnabled = occultSync;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            ImGui.TextDisabled(plugin.CharacterLink.IsLifestreamLoaded
                ? Loc.L("Lifestream：接続済み", "Lifestream: Connected")
                : Loc.L("Lifestream：未接続（両クライアントで有効にしてください）", "Lifestream: Not connected (enable it on both clients)"));
            ImGui.TextDisabled($"{Loc.T("Status")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.OccultTravelStatus, x => x.OccultTravelStatus))}");

            ImGui.Spacing();
            var syncReturn = plugin.Configuration.SyncReturnEnabled;
            if (ImGui.Checkbox(Loc.IsEnglish ? "Sync leader's Demi-Return" : "リーダーのデミデジョンにフォロワーを連動", ref syncReturn))
            {
                plugin.Configuration.SyncReturnEnabled = syncReturn;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            var autoTreasure = plugin.Configuration.AutoOpenNearbyTreasureEnabled;
            if (ImGui.Checkbox(Loc.IsEnglish ? "Open nearby treasure automatically (within 2m)" : "近くの宝箱を自動で開ける（2m以内）", ref autoTreasure))
            {
                plugin.Configuration.AutoOpenNearbyTreasureEnabled = autoTreasure;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            ImGui.TextDisabled($"{(Loc.IsEnglish ? "Treasure" : "宝箱")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.TreasureStatus, x => x.TreasureStatus))}");
            ImGui.Unindent(12 * ImGuiHelpers.GlobalScale);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("AreaContentSync"));
        ImGui.TextDisabled(Loc.T("AreaContentHelp"));
        var syncRegularTeleport = plugin.Configuration.SyncRegularTeleportEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Sync regular teleport" : "リーダーの通常テレポにフォロワーを連動", ref syncRegularTeleport))
        {
            plugin.Configuration.SyncRegularTeleportEnabled = syncRegularTeleport;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncCityAethernet = plugin.Configuration.SyncCityAethernetEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Sync city aethernet travel" : "都市内エーテライト移動を同期", ref syncCityAethernet))
        {
            plugin.Configuration.SyncCityAethernetEnabled = syncCityAethernet;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncResidentialAethernet = plugin.Configuration.SyncResidentialAethernetEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Sync residential aethernet travel" : "住宅街のエーテライト移動を同期", ref syncResidentialAethernet))
        {
            plugin.Configuration.SyncResidentialAethernetEnabled = syncResidentialAethernet;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncZoneBoundary = plugin.Configuration.SyncZoneBoundaryEnabled;
        if (ImGui.Checkbox(Loc.L("エリア境界の徒歩移動を同期", "Follow through zone boundaries"), ref syncZoneBoundary))
        {
            plugin.Configuration.SyncZoneBoundaryEnabled = syncZoneBoundary;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncFcEstate = plugin.Configuration.SyncFreeCompanyEstateEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Sync FC estate teleport (Lifestream address travel)" : "FCハウステレポを同期（Lifestream住所移動）", ref syncFcEstate))
        {
            plugin.Configuration.SyncFreeCompanyEstateEnabled = syncFcEstate;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled(plugin.CharacterLink.IsLifestreamLoaded
            ? $"{Loc.L("移動同期", "Travel sync")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.GeneralTravelStatus, x => x.GeneralTravelStatus))}"
            : Loc.L("移動同期：Lifestream未接続（両クライアントで有効にしてください）", "Travel sync: Lifestream not connected (enable it on both clients)"));
        ImGui.TextDisabled($"{(Loc.IsEnglish ? "FC estate" : "FCハウス")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.HousingTravelStatus, x => x.HousingTravelStatus))}");
        ImGui.Spacing();
        var syncDuty = plugin.Configuration.SyncDutyCommenceEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Follower accepts duty commencement" : "フォロワーもコンテンツ突入を承認", ref syncDuty))
        {
            plugin.Configuration.SyncDutyCommenceEnabled = syncDuty;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var acceptPartyInvite = plugin.Configuration.AutoAcceptPartyInviteEnabled;
        if (ImGui.Checkbox(Loc.L("フォロワーがPT招待を自動承認", "Follower automatically accepts party invitations"), ref acceptPartyInvite))
        {
            plugin.Configuration.AutoAcceptPartyInviteEnabled = acceptPartyInvite;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncTeleport = plugin.Configuration.SyncTeleportInvitationEnabled;
        if (ImGui.Checkbox(Loc.IsEnglish ? "Automatically accept teleport invitations on follower" : "フォロワーに届いたテレポ勧誘を自動承認", ref syncTeleport))
        {
            plugin.Configuration.SyncTeleportInvitationEnabled = syncTeleport;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled(Loc.IsEnglish ? "Only followers currently connected to the leader accept these prompts." : "リーダーへ接続中のフォロワーだけが、PT招待・コンテンツ突入・テレポ勧誘を承認します。");
        ImGui.TextDisabled($"{Loc.T("Status")}：{Loc.Status(GetDisplayedStatus(plugin.CharacterLink.AreaSyncStatus, x => x.AreaSyncStatus))}");

        ImGui.Separator();
        ImGui.TextUnformatted($"{Loc.T("ConnectedClients")}：{plugin.CharacterLink.Peers.Length}");
        var peers = plugin.CharacterLink.Peers;
        if (peers.Length == 0)
        {
            ImGui.TextDisabled(Loc.T("WaitingOtherClient"));
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("linked-characters", 5, flags))
            return;
        ImGui.TableSetupColumn(Loc.T("Character"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Loc.T("Role"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.T("Job"), ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.T("Status"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var peer in peers)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{peer.CharacterName}\n{peer.WorldName}");
            ImGui.TableNextColumn();
            ImGui.TextColored(peer.ContentId == plugin.Configuration.LinkLeaderContentId
                    ? new Vector4(0.35f, 0.82f, 1f, 1f)
                    : new Vector4(0.65f, 0.75f, 0.85f, 1f),
                peer.ContentId == plugin.Configuration.LinkLeaderContentId ? Loc.T("Leader") : Loc.T("Follower"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(peer.JobName);
            ImGui.TableNextColumn();
            var state = !string.IsNullOrWhiteSpace(peer.LastAction) ? Loc.Status(peer.LastAction) :
                Plugin.CurrentConfiguration?.Language == "en"
                    ? peer.InCombat ? "In combat" : peer.RidingPillion ? "Riding pillion" : peer.Mounted ? "Mounted" : "Idle"
                    : peer.InCombat ? "戦闘中" : peer.RidingPillion ? "相乗り中" : peer.Mounted ? "マウント中" : "待機中";
            ImGui.TextUnformatted(state);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(peer.MaxHp > 0 ? $"{peer.CurrentHp:N0}/{peer.MaxHp:N0}" : "—");
        }
        ImGui.EndTable();
    }

    private string GetDisplayedStatus(string localStatus, Func<LinkedCharacterState, string> peerStatus)
    {
        if (!plugin.CharacterLink.IsLeader)
            return localStatus;
        var supporter = plugin.CharacterLink.Peers
            .Where(x => x.ContentId != plugin.Configuration.LinkLeaderContentId)
            .OrderBy(x => x.CharacterName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        if (supporter is null)
            return localStatus;
        var status = peerStatus(supporter);
        return string.IsNullOrWhiteSpace(status) ? localStatus : status;
    }

    private int GetHousingAttentionCount()
    {
        var cycle = plugin.GetCurrentCycle();
        return plugin.Configuration.Characters.Values.Count(record =>
            record.EnabledForDisplay &&
            (cycle.Phase == LotteryPhase.Entry
                ? !cycle.HasEntry(record)
                : cycle.HasEntry(record) && !record.ResultChecked));
    }

    private (bool Show, bool IsUrgent, string Tooltip) GetCompactSectionAttention(MainSection section)
    {
        if (section == MainSection.CustomDeliveries)
        {
            var incomplete = plugin.Configuration.CustomDeliveryCharacters.Values.Count(x =>
                x.RemainingWeeklyAllowances > 0);
            return incomplete > 0
                ? (true, false, Loc.L($"お得意様の未消化：{incomplete}人",
                    $"Custom deliveries remaining: {incomplete}"))
                : (false, false, string.Empty);
        }

        if (section == MainSection.Submarines)
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var returned = plugin.Configuration.FreeCompanyGil.Values
                .SelectMany(fc => fc.Submarines.Values)
                .Count(submarine => submarine.ReturnTimeUnix > 0 && submarine.ReturnTimeUnix <= nowUnix);
            return returned > 0
                ? (true, false, Loc.L($"帰還時刻を過ぎた潜水艦：{returned}隻",
                    $"Submersibles ready: {returned}"))
                : (false, false, string.Empty);
        }

        if (section != MainSection.Housing)
            return (false, false, string.Empty);

        var cycle = plugin.GetCurrentCycle();
        var lotteryAttention = GetHousingAttentionCount();
        var demolitionWarnings = GetHousingDisplayEntries().Count(x =>
            x.LastEntry?.LastEnteredAt is { } entered &&
            entered.AddDays(HousingDemolitionTracker.DemolitionPeriodDays) - DateTime.Now <= TimeSpan.FromDays(10));
        if (lotteryAttention == 0 && demolitionWarnings == 0)
            return (false, false, string.Empty);
        var details = new System.Collections.Generic.List<string>();
        if (lotteryAttention > 0)
            details.Add(cycle.Phase == LotteryPhase.Entry
                ? Loc.L($"抽選期間中の未応募：{lotteryAttention}人",
                    $"Lottery entries missing: {lotteryAttention}")
                : Loc.L($"結果発表期間中の未確認：{lotteryAttention}人",
                    $"Lottery results unchecked: {lotteryAttention}"));
        if (demolitionWarnings > 0)
            details.Add(Loc.L($"住宅の保持期限が接近：{demolitionWarnings}件",
                $"Estate deadlines approaching: {demolitionWarnings}"));
        var urgent = demolitionWarnings > 0 ||
                     cycle.Phase != LotteryPhase.Entry && lotteryAttention > 0;
        return (true, urgent, string.Join("\n", details));
    }

    private void DrawLotteryStatusTab()
    {
        var cycle = plugin.GetCurrentCycle();
        var phaseText = cycle.Phase == LotteryPhase.Entry
            ? Loc.L("応募期間", "Entry Period")
            : Loc.L("結果発表期間", "Results Period");
        var phaseColor = cycle.Phase == LotteryPhase.Entry
            ? new Vector4(0.35f, 0.8f, 1f, 1f)
            : new Vector4(1f, 0.72f, 0.2f, 1f);

        ImGui.TextColored(phaseColor, $"{Loc.L("現在", "Current")}：{phaseText}");
        ImGui.SameLine();
        ImGui.TextDisabled($"（{GetPhaseDeadline(cycle)}）");
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("現在のキャラを再確認", "Refresh Current Character")))
            plugin.CheckCurrentCharacter(true);
        ImGui.TextDisabled(Loc.L("「表示キャラクター」で選択したキャラクターだけ表示しています。", "Only characters selected under Displayed Characters are shown."));

        ImGui.Separator();
        var displayedCharacters = OrderedCharacters().Where(x => x.EnabledForDisplay).ToList();
        if (displayedCharacters.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("表示するキャラクターが選択されていません。「表示キャラクター」で選択してください。", "No characters are selected. Select them under Displayed Characters."));
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("lottery-characters", 4, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Character"), ImGuiTableColumnFlags.WidthStretch, 1.35f);
        ImGui.TableSetupColumn(Loc.T("Status"), ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("応募先", "Entered Plot"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.L("最終確認", "Last Updated"), ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var record in displayedCharacters)
        {
            var hasEntry = cycle.HasEntry(record);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Selectable($"{record.CharacterName}\n{record.WorldName}##lottery-{record.ContentId}",
                false, ImGuiSelectableFlags.SpanAllColumns);
            if (ImGui.BeginPopupContextItem($"##lottery-menu-{record.ContentId}",
                    ImGuiPopupFlags.MouseButtonRight))
            {
                ImGui.TextUnformatted(record.PlotAddress ?? Loc.L("応募した土地", "Entered plot"));
                ImGui.Separator();
                var canTravel = hasEntry && Plugin.IsLifestreamAvailable();
                if (!canTravel)
                    ImGui.BeginDisabled();
                if (ImGui.MenuItem(Loc.L("応募先へLifestreamで移動", "Travel to entered plot with Lifestream")))
                {
                    mapPreviewMessage = Plugin.TravelToLotteryPlot(record)
                        ? Loc.L("応募先への移動を開始しました。", "Started travelling to the entered plot.")
                        : Loc.L("応募先の住所を取得できませんでした。", "Could not read the entered plot address.");
                }
                if (!canTravel)
                    ImGui.EndDisabled();
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn();
            var (statusText, statusColor) = GetStatus(cycle, record, hasEntry);
            ImGui.TextColored(statusColor, statusText);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(hasEntry ? record.PlotAddress ?? Loc.L("応募した土地", "Entered plot") : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(record.LastCheckedAt == default
                ? "—"
                : FormatDate(record.LastCheckedAt));
        }

        ImGui.EndTable();
    }

    private void DrawDisplaySettingsTab(bool showPageTitle = true)
    {
        if (showPageTitle)
            DrawPageTitle(Loc.L("表示キャラクター", "Displayed Characters"), Loc.L("一覧表示とログイン時の通知に使用するキャラクターを選択します。", "Choose characters used in lists and login notifications."));
        ImGui.TextUnformatted(Loc.L("表示するキャラクター", "Characters to display"));
        ImGui.TextDisabled(Loc.L("抽選状態と保持期限で、表示するキャラクターをそれぞれ選択できます。", "Choose displayed characters separately for Lottery Status and Demolition Timer."));
        ImGui.Spacing();

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("display-characters", 3, flags))
        {
            ImGui.TableSetupColumn(Loc.T("Character"));
            ImGui.TableSetupColumn(Loc.L("抽選状態", "Lottery Status"), ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("保持期限", "Demolition Timer"), ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var record in OrderedCharacters())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{record.CharacterName} @ {record.WorldName}");
                ImGui.TableNextColumn();
                var lottery = record.EnabledForDisplay;
                if (ImGui.Checkbox($"##lottery-display-{record.ContentId}", ref lottery))
                {
                    record.EnabledForDisplay = lottery;
                    record.LastCheckedAt = DateTime.Now;
                    plugin.Configuration.Save();
                }
                ImGui.TableNextColumn();
                var demolition = record.EnabledForDemolitionDisplay;
                if (ImGui.Checkbox($"##demolition-display-{record.ContentId}", ref demolition))
                {
                    record.EnabledForDemolitionDisplay = demolition;
                    record.LastCheckedAt = DateTime.Now;
                    plugin.Configuration.Save();
                }
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("キャラクター一覧の取得元", "Character Data Source"));
        ImGui.TextWrapped(plugin.GetCharacterDataDirectory());
        if (ImGui.Button(Loc.L("キャラクターフォルダを再読み込み", "Rescan Character Folders")))
        {
            var added = plugin.ScanCharacterFolders();
            scanMessage = added > 0
                ? Loc.L($"{added}キャラクター追加しました。", $"Added {added} character(s).")
                : Loc.L("追加対象はありませんでした。", "No new characters were found.");
        }
        if (!string.IsNullOrEmpty(scanMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(scanMessage);
        }
    }

    private void DrawOpenPlotsTab()
    {
        ImGui.TextUnformatted(Loc.L("エーテライトの区画一覧で手動確認した空き土地を保存しています。", "Stores open plots inspected from the residential aetheryte ward list."));
        ImGui.TextDisabled(Loc.L("同じ区を再確認すると、その区の保存内容を最新状態で置き換えます。", "Inspecting the same ward again replaces its saved results."));

        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        ImGui.Combo(Loc.L("サイズ", "Size"), ref sizeFilterIndex, SizeFilters, SizeFilters.Length);
        ImGui.SameLine();

        var worlds = plugin.Configuration.OpenPlots.Select(x => x.WorldName)
            .Distinct().OrderBy(x => x).Prepend("ALL").ToArray();
        if (!worlds.Contains(worldFilter))
            worldFilter = "ALL";
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(Loc.L("ワールド", "World"), worldFilter))
        {
            foreach (var world in worlds)
            {
                var selected = worldFilter == world;
                if (ImGui.Selectable(world, selected))
                    worldFilter = world;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (!string.IsNullOrEmpty(mapPreviewMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(mapPreviewMessage);
        }
        ImGui.Separator();

        var filteredPlots = plugin.Configuration.OpenPlots
            .Where(x => worldFilter == "ALL" || x.WorldName == worldFilter)
            .Where(x => MatchesSizeFilter(x.Size))
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.DistrictName)
            .ThenBy(x => x.WardNumber)
            .ThenBy(x => x.PlotNumber)
            .ToList();

        if (filteredPlots.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("条件に一致する空き土地はありません。", "No open plots match the selected filters."));
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("open-plots", 7, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.L("ワールド", "World"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.L("住宅街", "District"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Loc.L("区・番地", "Ward / Plot"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("サイズ", "Size"), ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("価格", "Price"), ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("応募", "Entry"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("確認日時", "Checked At"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var plot in filteredPlots)
        {
            var bidCount = plugin.GetBidCount(plot);
            ImGui.TableNextRow();
            if (bidCount > 0)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(new Vector4(0.12f, 0.42f, 0.22f, 0.72f)));
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{plot.WorldName}##plot-{plot.WorldId}-{plot.TerritoryTypeId}-{plot.WardNumber}-{plot.PlotNumber}",
                    false, ImGuiSelectableFlags.SpanAllColumns))
                OpenPlotMap(plot);
            if (ImGui.BeginPopupContextItem(
                    $"##plot-menu-{plot.WorldId}-{plot.TerritoryTypeId}-{plot.WardNumber}-{plot.PlotNumber}",
                    ImGuiPopupFlags.MouseButtonRight))
            {
                ImGui.TextUnformatted(FormatPlotAddress(plot));
                ImGui.Separator();
                if (ImGui.MenuItem(Loc.L("地図で位置を表示", "Show on Map")))
                    OpenPlotMap(plot);
                var lifestreamAvailable = Plugin.IsLifestreamAvailable();
                if (!lifestreamAvailable)
                    ImGui.BeginDisabled();
                if (ImGui.MenuItem(Loc.L("Lifestreamで自動移動", "Travel with Lifestream")))
                    TravelToPlot(plot);
                if (!lifestreamAvailable)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(Loc.L("Lifestreamが読み込まれていません。", "Lifestream is not loaded."));
                }
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot.DistrictName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Loc.L($"{plot.WardNumber}区 {plot.PlotNumber}番地", $"W{plot.WardNumber} P{plot.PlotNumber}"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot.Size);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{plot.Price:N0} G");
            ImGui.TableNextColumn();
            if (bidCount > 0)
                ImGui.TextColored(new Vector4(0.35f, 1f, 0.55f, 1f), Loc.L($"応募中 ×{bidCount}", $"Entered ×{bidCount}"));
            else
                ImGui.TextDisabled("—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatDate(plot.CheckedAt));
        }

        ImGui.EndTable();
    }

    private bool MatchesSizeFilter(string size) => SizeFilters[sizeFilterIndex] switch
    {
        "S" => size == "S",
        "S-M" => size is "S" or "M",
        "M" => size == "M",
        "M-L" => size is "M" or "L",
        "L" => size == "L",
        _ => true,
    };

    private void OpenPlotMap(OpenPlotRecord plot)
    {
        mapPreviewMessage = Plugin.PreviewOpenPlot(plot)
            ? Loc.L($"{FormatPlotAddress(plot)}を表示", $"Showing {FormatPlotAddress(plot)}")
            : Loc.L("地図を開けませんでした。", "Unable to open the map.");
    }

    private void TravelToPlot(OpenPlotRecord plot)
    {
        mapPreviewMessage = Plugin.TravelToOpenPlot(plot)
            ? Loc.L($"{plot.WorldName} {FormatPlotAddress(plot)}へ移動開始", $"Travelling to {plot.WorldName} {FormatPlotAddress(plot)}")
            : Loc.L("Lifestreamで移動を開始できませんでした。", "Lifestream could not start travel.");
    }

    private IOrderedEnumerable<CharacterLotteryRecord> OrderedCharacters() =>
        plugin.Configuration.Characters.Values
            .OrderByDescending(x => x.ContentId == Plugin.PlayerState.ContentId)
            .ThenBy(x => x.CharacterName);

    private static (string Text, Vector4 Color) GetStatus(
        LotteryCycle cycle, CharacterLotteryRecord record, bool hasEntry)
    {
        if (cycle.Phase == LotteryPhase.Entry)
            return hasEntry
                ? (Loc.L("参加", "Entered"), new Vector4(0.25f, 0.9f, 0.45f, 1f))
                : (Loc.L("未参加", "Not Entered"), new Vector4(1f, 0.35f, 0.35f, 1f));

        if (!hasEntry)
            return (Loc.L("—（未参加）", "— (Not Entered)"), new Vector4(0.6f, 0.6f, 0.6f, 1f));

        return record.ResultChecked
            ? (Loc.L("確認済", "Checked"), new Vector4(0.25f, 0.9f, 0.45f, 1f))
            : (Loc.L("未確認", "Unchecked"), new Vector4(1f, 0.35f, 0.35f, 1f));
    }

    private static string GetPhaseDeadline(LotteryCycle cycle) => cycle.Phase == LotteryPhase.Entry
        ? $"{Loc.L("応募締切", "Entry Deadline")} {FormatDate(cycle.EntryEndsAt)}"
        : $"{Loc.L("発表終了", "Results End")} {FormatDate(cycle.ResultsEndAt)}";

    private static string FormatDate(DateTime value) =>
        value.ToString(Loc.IsEnglish ? "MM/dd (ddd) HH:mm" : "MM/dd（ddd） HH:mm",
            Loc.IsEnglish ? EnglishCulture : JapaneseCulture);

    private static string FormatPlotAddress(OpenPlotRecord plot) => Loc.L(
        $"{plot.DistrictName} {plot.WardNumber}区 {plot.PlotNumber}番地",
        $"{plot.DistrictName} Ward {plot.WardNumber}, Plot {plot.PlotNumber}");
}

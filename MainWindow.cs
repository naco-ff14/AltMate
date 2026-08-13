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

public sealed class MainWindow : Window
{
    private enum MainSection
    {
        Home,
        Housing,
        CharacterLink,
        Animations,
        Gil,
        Settings,
    }

    private enum HousingSection
    {
        Lottery,
        OpenPlots,
        Characters,
    }

    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
    private readonly Plugin plugin;
    private string scanMessage = string.Empty;
    private int sizeFilterIndex;
    private string worldFilter = "ALL";
    private string mapPreviewMessage = string.Empty;
    private MainSection selectedSection = MainSection.Home;
    private HousingSection selectedHousingSection = HousingSection.Lottery;
    private bool compactMode;
    private bool clearForcedSize;
    private AnimationEmote[] animationEmotes = [];
    private bool animationListLoaded;
    private ulong animationTargetContentId;
    private string animationFilter = string.Empty;
    private Vector2 expandedWindowSize = new(940, 520);
    private ImGuiWindowFlags expandedWindowFlags;
    private float? expandedBackgroundAlpha;
    private static readonly string[] SizeFilters = { "ALL", "S", "S-M", "M", "M-L", "L" };

    public MainWindow(Plugin plugin) : base(
        $"AltMate v{Plugin.PluginInterface.Manifest.AssemblyVersion.ToString(3)} - 複数キャラクター支援###AltMate")
    {
        this.plugin = plugin;
        Size = new Vector2(940, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 360) };
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            IconOffset = new Vector2(0, -2),
            Priority = 1,
            ShowTooltip = () => ImGui.SetTooltip("最小化"),
            Click = _ => EnterCompactMode(),
        });
    }

    public override void Draw()
    {
        if (clearForcedSize)
        {
            SizeCondition = ImGuiCond.None;
            clearForcedSize = false;
        }
        if (compactMode)
        {
            DrawCompactMenu();
            return;
        }

        var menuWidth = 184 * ImGuiHelpers.GlobalScale;

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.085f, 0.11f, 0.96f));
        if (ImGui.BeginChild("altmate-menu", new Vector2(menuWidth, 0), true))
            DrawMainMenu();
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.065f, 0.085f, 0.72f));
        if (ImGui.BeginChild("altmate-content", new Vector2(0, 0), true))
            DrawSelectedSection();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    internal void OpenHousingLottery()
    {
        selectedSection = MainSection.Housing;
        selectedHousingSection = HousingSection.Lottery;
        IsOpen = true;
    }

    internal void OpenSettings()
    {
        if (compactMode)
            ExitCompactMode();
        selectedSection = MainSection.Settings;
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
            case MainSection.Settings:
                DrawSettings();
                break;
        }
    }

    private void DrawMainMenu()
    {
        ImGui.Spacing();
        var iconSize = 128 * ImGuiHelpers.GlobalScale;
        var icon = Plugin.TextureProvider.GetFromFile(plugin.IconPath).GetWrapOrDefault();
        if (icon is not null)
        {
            var availableWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - iconSize) / 2));
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        }
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), "AltMate");
        ImGui.TextDisabled("MULTI CHARACTER TOOL");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMenuButton(Loc.T("Home"), MainSection.Home);
        DrawMenuButton(Loc.T("Link"), MainSection.CharacterLink, detail: GetCharacterLinkMenuDetail());
        DrawMenuButton(Loc.T("Animation"), MainSection.Animations);
        DrawMenuButton(Loc.T("Housing"), MainSection.Housing, GetHousingAttentionCount());
        DrawMenuButton(Loc.T("Gil"), MainSection.Gil);
        var bottomY = ImGui.GetWindowHeight() - 62 * ImGuiHelpers.GlobalScale;
        if (ImGui.GetCursorPosY() < bottomY)
            ImGui.SetCursorPosY(bottomY);
        DrawMenuButton(Loc.T("Settings"), MainSection.Settings);
    }

    private void EnterCompactMode()
    {
        expandedWindowSize = ImGui.GetWindowSize();
        expandedWindowFlags = Flags;
        expandedBackgroundAlpha = BgAlpha;
        compactMode = true;
        BgAlpha = 0.58f;
        Flags = expandedWindowFlags | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(330, 70),
            MaximumSize = new Vector2(420, 82),
        };
        Size = new Vector2(350, 74);
        SizeCondition = ImGuiCond.Always;
        clearForcedSize = true;
    }

    private void ExitCompactMode()
    {
        compactMode = false;
        Flags = expandedWindowFlags;
        BgAlpha = expandedBackgroundAlpha;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 360) };
        Size = new Vector2(MathF.Max(760, expandedWindowSize.X), MathF.Max(360, expandedWindowSize.Y));
        SizeCondition = ImGuiCond.Always;
        clearForcedSize = true;
    }

    private void DrawCompactMenu()
    {
        var iconSize = 28 * ImGuiHelpers.GlobalScale;
        var icon = Plugin.TextureProvider.GetFromFile(plugin.IconPath).GetWrapOrDefault();
        if (icon is not null)
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        else
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.08f, 0.08f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.12f, 0.12f, 1f));
        if (ImGui.SmallButton("緊急停止##compact-stop"))
            plugin.CharacterLink.EmergencyStop();
        ImGui.PopStyleColor(2);
        ImGui.SameLine();
        if (ImGui.SmallButton("⛶##compact-expand"))
            ExitCompactMode();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("最大化");

        ImGui.SetCursorPosY(42 * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled("状態：");
        ImGui.SameLine(0, 2 * ImGuiHelpers.GlobalScale);
        var statusColor = plugin.CharacterLink.RuntimeStopped
            ? new Vector4(1f, 0.32f, 0.28f, 1f)
            : new Vector4(0.42f, 0.82f, 1f, 1f);
        ImGui.TextColored(statusColor, plugin.CharacterLink.LastAction);
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
            selectedSection = section;
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
            : $"[{firstName} ほか{peers.Length - 1}人]";
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

        var peers = plugin.CharacterLink.Peers;
        var linkTitle = plugin.CharacterLink.RuntimeStopped ? "緊急停止中" : plugin.CharacterLink.LastAction;
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
    }

    private void DrawHomeCard(string title, string mainText, string detail, Vector4 accent, MainSection destination)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.09f, 0.105f, 0.14f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.13f, 0.16f, 0.21f, 0.96f));
        var position = ImGui.GetCursorScreenPos();
        var height = 88 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button($"##home-card-{destination}", new Vector2(-1, height)))
            selectedSection = destination;
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
        if (DrawSubMenuButton("抽選状態", selectedHousingSection == HousingSection.Lottery))
            selectedHousingSection = HousingSection.Lottery;
        ImGui.SameLine();
        if (DrawSubMenuButton($"空き土地 ({plugin.Configuration.OpenPlots.Count})", selectedHousingSection == HousingSection.OpenPlots))
            selectedHousingSection = HousingSection.OpenPlots;
        ImGui.SameLine();
        if (DrawSubMenuButton("表示キャラクター", selectedHousingSection == HousingSection.Characters))
            selectedHousingSection = HousingSection.Characters;
        ImGui.Separator();
        ImGui.Spacing();

        switch (selectedHousingSection)
        {
            case HousingSection.Lottery:
                DrawLotteryStatusTab();
                break;
            case HousingSection.OpenPlots:
                DrawOpenPlotsTab();
                break;
            case HousingSection.Characters:
                DrawDisplaySettingsTab(false);
                break;
        }
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

    private static void DrawComingSoon(string title, string description)
    {
        DrawPageTitle(title, description);
        ImGui.Spacing();
        ImGui.TextDisabled("COMING SOON");
    }

    private void DrawGil()
    {
        DrawPageTitle(Loc.T("Gil"), Loc.T("GilDescription"));
        ImGui.TextDisabled("本人はログイン中に更新。リテイナーとFCはゲーム側へ情報が読み込まれた時点の最新値です。");

        var characters = plugin.Configuration.CharacterGil.Values
            .OrderBy(x => x.CharacterName).ThenBy(x => x.WorldName).ToArray();
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
            $"総資産　{characterTotal + fcTotal + depositedTotal:N0} G");
        ImGui.SameLine();
        ImGui.TextDisabled($"（使用可能 {characterTotal + fcTotal:N0} G / 抽選預かり中 {depositedTotal:N0} G）");
        var unknownDeposits = depositedRecords.Count(x => x.BidGilDeposited == 0);
        if (unknownDeposits > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"金額未取得 {unknownDeposits}件");
        }
        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), "キャラクター・リテイナー");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("gil-characters", 4, flags))
        {
            ImGui.TableSetupColumn("キャラクター／リテイナー", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("ワールド", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("所持ギル", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var character in characters)
            {
                DrawGilRow(character.CharacterName, character.WorldName, character.Gil, character.UpdatedAt, false);
                foreach (var retainer in character.Retainers.Values.OrderBy(x => x.Name))
                    DrawGilRow($"　└ {retainer.Name}", "リテイナー", retainer.Gil, retainer.UpdatedAt, true);
                if (plugin.Configuration.Characters.TryGetValue(character.ContentId, out var lottery) &&
                    cycle.HasEntry(lottery) && !lottery.ResultChecked && lottery.BidGilDeposited > 0)
                    DrawGilRow("　└ ハウジング抽選預かり中",
                        lottery.PlotAddress ?? "応募した土地", lottery.BidGilDeposited,
                        lottery.LastCheckedAt, true, new Vector4(0.35f, 0.8f, 1f, 1f));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.42f, 0.82f, 1f, 1f), "FCチェスト");
        ImGui.TextDisabled("同じFC所属のキャラクターが複数いても、FC ID単位で1件だけ表示・集計します。");
        if (ImGui.BeginTable("gil-free-companies", 4, flags))
        {
            ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("ワールド／確認キャラ", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("チェスト内ギル", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var fc in plugin.Configuration.FreeCompanyGil.Values.OrderBy(x => x.Name))
                DrawGilRow(fc.Name, $"{fc.WorldName} / {fc.LastCheckedByName}", fc.Gil, fc.UpdatedAt, false);
            ImGui.EndTable();
        }
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
        ImGui.TextColored(amountColor ?? new Vector4(0.95f, 0.78f, 0.25f, 1f), $"{gil:N0} G");
        ImGui.TableNextColumn();
        ImGui.TextDisabled(updatedAt == default ? "—" : updatedAt.ToString("MM/dd（ddd） HH:mm", JapaneseCulture));
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
        ImGui.TextUnformatted(Loc.T("Command"));
        ImGui.TextDisabled(Loc.T("LegacyCommand"));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("DataStorage"));
        ImGui.TextWrapped(Loc.T("DataStorageDescription"));
        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.T("Privacy"));
        ImGui.TextWrapped(Loc.T("PrivacyDescription"));
    }

    private void DrawAnimations()
    {
        DrawPageTitle(Loc.T("Animation"), Loc.T("AnimationDescription"));
        ImGui.TextDisabled("この画面のキャラクターで現在有効なMod・オプションと、競合時に実際に適用される優先度を反映します。");
        ImGui.TextDisabled("サポーター側のPenumbra設定が異なる場合は、サポーター側のAltMate画面から一覧を確認してください。");

        if (!plugin.Animations.IsPenumbraLoaded)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Penumbraが読み込まれていません。");
            return;
        }

        if (!animationListLoaded)
        {
            animationEmotes = plugin.Animations.LoadActiveEmotes().ToArray();
            animationListLoaded = true;
        }

        if (ImGui.Button("一覧を更新"))
        {
            animationEmotes = plugin.Animations.LoadActiveEmotes().ToArray();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Animations.Status);
        ImGui.Spacing();

        var targets = GetAnimationTargets();
        if (animationTargetContentId == 0 || targets.All(x => x.ContentId != animationTargetContentId))
            animationTargetContentId = Plugin.PlayerState.ContentId;
        var targetPreview = targets.FirstOrDefault(x => x.ContentId == animationTargetContentId).Label ?? "このキャラクター";
        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("再生するキャラクター", targetPreview))
        {
            foreach (var target in targets)
            {
                var selected = target.ContentId == animationTargetContentId;
                if (ImGui.Selectable($"{target.Label}##animation-target-{target.ContentId}", selected))
                    animationTargetContentId = target.ContentId;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##animation-filter", "エモート名で絞り込み", ref animationFilter, 100);
        ImGui.Separator();

        if (animationEmotes.Length == 0)
        {
            ImGui.TextDisabled("「一覧を更新」を押すと、Penumbraで現在有効なエモートを表示します。");
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
        ImGui.TableSetupColumn("エモート", ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn("適用元Mod", ImGuiTableColumnFlags.WidthStretch, 2f);
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
            if (ImGui.SmallButton($"再生##play-emote-{emote.Id}"))
                plugin.CharacterLink.PlayEmote(emote.Id, animationTargetContentId);
        }
        ImGui.EndTable();
    }

    private (ulong ContentId, string Label)[] GetAnimationTargets()
    {
        var localId = Plugin.PlayerState.ContentId;
        var localName = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : "このキャラクター";
        var localRole = localId == plugin.Configuration.LinkLeaderContentId ? "リーダー" : "サポーター";
        return new[] { (ContentId: localId, Label: $"{localName}（この画面・{localRole}）") }
            .Concat(plugin.CharacterLink.Peers.Select(x =>
                (ContentId: x.ContentId, Label: $"{x.CharacterName}（{(x.ContentId == plugin.Configuration.LinkLeaderContentId ? "リーダー" : "サポーター")}）")))
            .GroupBy(x => x.ContentId).Select(x => x.First()).ToArray();
    }

    private void DrawCharacterLink()
    {
        DrawPageTitle(Loc.T("Link"), Loc.T("LinkDescription"));
        ImGui.TextDisabled($"読込バージョン：{Plugin.PluginInterface.Manifest.AssemblyVersion}");

        if (plugin.CharacterLink.RuntimeStopped)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.52f, 0.28f, 0.95f));
            if (ImGui.Button("連携操作を再開", new Vector2(180 * ImGuiHelpers.GlobalScale, 38 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.Resume();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "緊急停止中");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.14f, 0.12f, 0.95f));
            if (ImGui.Button("すべて緊急停止", new Vector2(180 * ImGuiHelpers.GlobalScale, 38 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.EmergencyStop();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.CharacterLink.LastAction);
        }

        ImGui.Spacing();
        var linkEnabled = plugin.Configuration.LinkEnabled;
        if (ImGui.Checkbox("連携操作を有効にする", ref linkEnabled))
        {
            plugin.Configuration.LinkEnabled = linkEnabled;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }

        var connectedLeader = plugin.CharacterLink.Peers.FirstOrDefault(x =>
            x.ContentId == plugin.Configuration.LinkLeaderContentId);
        var currentLeader = connectedLeader is not null
            ? $"{connectedLeader.CharacterName} @ {connectedLeader.WorldName}"
            : plugin.Configuration.Characters.TryGetValue(
                plugin.Configuration.LinkLeaderContentId, out var leaderRecord)
                ? $"{leaderRecord.CharacterName} @ {leaderRecord.WorldName}"
                : "選択してください";
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("リーダー", currentLeader))
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
            ? "このキャラクターはリーダーです。"
            : "このキャラクターはフォロワーとして動作します。");

        if (!plugin.CharacterLink.IsLeader)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.42f, 0.62f, 0.92f));
            if (ImGui.Button("リーダーの元へ移動", new Vector2(180 * ImGuiHelpers.GlobalScale, 32 * ImGuiHelpers.GlobalScale)))
                plugin.CharacterLink.MoveToLeader();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.CharacterLink.WorldLinkStatus);
            ImGui.TextDisabled("別ワールド中の自動連携は停止します。このボタンを押したときだけLifestreamで合流します。");
            ImGui.Spacing();
            if (ImGui.Button("追従テスト"))
                plugin.CharacterLink.TestFollow();
            ImGui.SameLine();
            if (ImGui.Button("相乗りテスト"))
                plugin.CharacterLink.TestRidePillion();
            ImGui.SameLine();
            ImGui.TextDisabled("フォロワー側で押すとゲームコマンドを1回実行します。");
            ImGui.TextDisabled($"診断：{plugin.CharacterLink.DiagnosticMessage}");
        }

        ImGui.Separator();
        var autoFollow = plugin.Configuration.AutoFollowEnabled;
        if (ImGui.Checkbox("フォロワーがリーダーを自動追従", ref autoFollow))
        {
            plugin.Configuration.AutoFollowEnabled = autoFollow;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var autoRide = plugin.Configuration.AutoRidePillionEnabled;
        if (ImGui.Checkbox("リーダーのマウントへ自動で相乗り", ref autoRide))
        {
            plugin.Configuration.AutoRidePillionEnabled = autoRide;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var pauseCombat = plugin.Configuration.PauseLinkInCombat;
        if (ImGui.Checkbox("どちらかが戦闘中なら自動操作を一時停止", ref pauseCombat))
        {
            plugin.Configuration.PauseLinkInCombat = pauseCombat;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var followDistance = plugin.Configuration.FollowStartDistance;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("追従を再開する距離", ref followDistance, 3f, 15f, "%.1f m"))
        {
            plugin.Configuration.FollowStartDistance = followDistance;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), "戦闘連携");
        ImGui.TextDisabled("リーダーが戦闘を開始すると、フォロワー側の戦闘支援を自動的に開始します。");
        var combatLink = plugin.Configuration.CombatLinkEnabled;
        if (ImGui.Checkbox("リーダーの戦闘開始にフォロワーを連動", ref combatLink))
        {
            plugin.Configuration.CombatLinkEnabled = combatLink;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var useBmr = plugin.Configuration.UseBossModReborn;
        if (ImGui.Checkbox("BossMod Reborn（移動・ターゲット）", ref useBmr))
        {
            plugin.Configuration.UseBossModReborn = useBmr;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var useRsr = plugin.Configuration.UseRotationSolverReborn;
        if (ImGui.Checkbox("Rotation Solver Reborn（攻撃ローテーション）", ref useRsr))
        {
            plugin.Configuration.UseRotationSolverReborn = useRsr;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var stopDelay = plugin.Configuration.CombatStopDelaySeconds;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("リーダーの戦闘終了後に停止", ref stopDelay, 0f, 15f, "%.1f 秒"))
        {
            plugin.Configuration.CombatStopDelaySeconds = stopDelay;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled($"状態：{plugin.CharacterLink.CombatStatus}");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("クレセントアイル連携##occult-link"))
        {
            ImGui.Indent(12 * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled("リーダーのエリア内移動やデミデジョンをフォロワーへ同期します。");
            var occultSync = plugin.Configuration.OccultAethernetSyncEnabled;
            if (ImGui.Checkbox("リーダーのエーテライト移動にフォロワーを連動", ref occultSync))
            {
                plugin.Configuration.OccultAethernetSyncEnabled = occultSync;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            ImGui.TextDisabled(plugin.CharacterLink.IsLifestreamLoaded
                ? "Lifestream：接続済み"
                : "Lifestream：未接続（両クライアントで有効にしてください）");
            ImGui.TextDisabled($"状態：{plugin.CharacterLink.OccultTravelStatus}");

            ImGui.Spacing();
            var syncReturn = plugin.Configuration.SyncReturnEnabled;
            if (ImGui.Checkbox("リーダーのデミデジョンにフォロワーを連動", ref syncReturn))
            {
                plugin.Configuration.SyncReturnEnabled = syncReturn;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            var autoTreasure = plugin.Configuration.AutoOpenNearbyTreasureEnabled;
            if (ImGui.Checkbox("近くの宝箱を自動で開ける（2m以内）", ref autoTreasure))
            {
                plugin.Configuration.AutoOpenNearbyTreasureEnabled = autoTreasure;
                plugin.Configuration.Save();
                plugin.CharacterLink.SettingsChanged();
            }
            ImGui.TextDisabled($"宝箱：{plugin.CharacterLink.TreasureStatus}");
            ImGui.Unindent(12 * ImGuiHelpers.GlobalScale);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("エリア移動・コンテンツ同期");
        ImGui.TextDisabled("通常テレポ、都市内、住宅街の移動をLifestream経由でフォロワーへ同期します。");
        var syncRegularTeleport = plugin.Configuration.SyncRegularTeleportEnabled;
        if (ImGui.Checkbox("リーダーの通常テレポにフォロワーを連動", ref syncRegularTeleport))
        {
            plugin.Configuration.SyncRegularTeleportEnabled = syncRegularTeleport;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncCityAethernet = plugin.Configuration.SyncCityAethernetEnabled;
        if (ImGui.Checkbox("都市内エーテライト移動を同期", ref syncCityAethernet))
        {
            plugin.Configuration.SyncCityAethernetEnabled = syncCityAethernet;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncResidentialAethernet = plugin.Configuration.SyncResidentialAethernetEnabled;
        if (ImGui.Checkbox("住宅街のエーテライト移動を同期", ref syncResidentialAethernet))
        {
            plugin.Configuration.SyncResidentialAethernetEnabled = syncResidentialAethernet;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncFcEstate = plugin.Configuration.SyncFreeCompanyEstateEnabled;
        if (ImGui.Checkbox("FCハウステレポを同期（Lifestream住所移動）", ref syncFcEstate))
        {
            plugin.Configuration.SyncFreeCompanyEstateEnabled = syncFcEstate;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled(plugin.CharacterLink.IsLifestreamLoaded
            ? $"移動同期：{plugin.CharacterLink.GeneralTravelStatus}"
            : "移動同期：Lifestream未接続（両クライアントで有効にしてください）");
        ImGui.TextDisabled($"FCハウス：{plugin.CharacterLink.HousingTravelStatus}");
        ImGui.Spacing();
        var syncDuty = plugin.Configuration.SyncDutyCommenceEnabled;
        if (ImGui.Checkbox("フォロワーもコンテンツ突入を承認", ref syncDuty))
        {
            plugin.Configuration.SyncDutyCommenceEnabled = syncDuty;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        var syncTeleport = plugin.Configuration.SyncTeleportInvitationEnabled;
        if (ImGui.Checkbox("フォロワーに届いたテレポ勧誘を自動承認", ref syncTeleport))
        {
            plugin.Configuration.SyncTeleportInvitationEnabled = syncTeleport;
            plugin.Configuration.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.TextDisabled("CF突入・テレポ勧誘は、リーダーへ接続中のフォロワーだけが承認します。");
        ImGui.TextDisabled($"状態：{plugin.CharacterLink.AreaSyncStatus}");

        ImGui.Separator();
        ImGui.TextUnformatted($"接続中の別クライアント　{plugin.CharacterLink.Peers.Length}台");
        var peers = plugin.CharacterLink.Peers;
        if (peers.Length == 0)
        {
            ImGui.TextDisabled("別のFF14クライアントでAltMateが起動するのを待っています。");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("linked-characters", 5, flags))
            return;
        ImGui.TableSetupColumn("キャラクター", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("役割", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ジョブ", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthStretch, 1f);
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
                peer.ContentId == plugin.Configuration.LinkLeaderContentId ? "リーダー" : "フォロワー");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(peer.JobName);
            ImGui.TableNextColumn();
            var state = peer.InCombat ? "戦闘中" : peer.RidingPillion ? "相乗り中" : peer.Mounted ? "マウント中" : "待機中";
            ImGui.TextUnformatted(state);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(peer.MaxHp > 0 ? $"{peer.CurrentHp:N0}/{peer.MaxHp:N0}" : "—");
        }
        ImGui.EndTable();
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

    private void DrawLotteryStatusTab()
    {
        var cycle = plugin.GetCurrentCycle();
        var phaseText = cycle.Phase == LotteryPhase.Entry ? "応募期間" : "結果発表期間";
        var phaseColor = cycle.Phase == LotteryPhase.Entry
            ? new Vector4(0.35f, 0.8f, 1f, 1f)
            : new Vector4(1f, 0.72f, 0.2f, 1f);

        ImGui.TextColored(phaseColor, $"現在：{phaseText}");
        ImGui.SameLine();
        ImGui.TextDisabled($"（{GetPhaseDeadline(cycle)}）");
        ImGui.SameLine();
        if (ImGui.Button("現在のキャラを再確認"))
            plugin.CheckCurrentCharacter(true);
        ImGui.TextDisabled("「表示キャラクター」で選択したキャラクターだけ表示しています。");

        ImGui.Separator();
        var displayedCharacters = OrderedCharacters().Where(x => x.EnabledForDisplay).ToList();
        if (displayedCharacters.Count == 0)
        {
            ImGui.TextDisabled("表示するキャラクターが選択されていません。「表示キャラクター」で選択してください。");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("lottery-characters", 4, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("キャラクター", ImGuiTableColumnFlags.WidthStretch, 1.35f);
        ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("応募先", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var record in displayedCharacters)
        {
            var hasEntry = cycle.HasEntry(record);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{record.CharacterName}\n{record.WorldName}");
            ImGui.TableNextColumn();
            var (statusText, statusColor) = GetStatus(cycle, record, hasEntry);
            ImGui.TextColored(statusColor, statusText);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(hasEntry ? record.PlotAddress ?? "応募した土地" : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(record.LastCheckedAt == default
                ? "—"
                : record.LastCheckedAt.ToString("MM/dd（ddd） HH:mm", JapaneseCulture));
        }

        ImGui.EndTable();
    }

    private void DrawDisplaySettingsTab(bool showPageTitle = true)
    {
        if (showPageTitle)
            DrawPageTitle("表示キャラクター", "一覧表示とログイン時の通知に使用するキャラクターを選択します。");
        ImGui.TextUnformatted("表示するキャラクター");
        ImGui.TextDisabled("チェックしたキャラクターだけ、一覧表示とログイン時の自動表示の対象になります。");
        ImGui.Spacing();

        foreach (var record in OrderedCharacters())
        {
            var enabled = record.EnabledForDisplay;
            if (ImGui.Checkbox($"{record.CharacterName} @ {record.WorldName}##display-{record.ContentId}", ref enabled))
            {
                record.EnabledForDisplay = enabled;
                record.LastCheckedAt = DateTime.Now;
                plugin.Configuration.Save();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("キャラクター一覧の取得元");
        ImGui.TextWrapped(plugin.GetCharacterDataDirectory());
        if (ImGui.Button("キャラクターフォルダを再読み込み"))
        {
            var added = plugin.ScanCharacterFolders();
            scanMessage = added > 0 ? $"{added}キャラクター追加しました。" : "追加対象はありませんでした。";
        }
        if (!string.IsNullOrEmpty(scanMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(scanMessage);
        }
    }

    private void DrawOpenPlotsTab()
    {
        ImGui.TextUnformatted("エーテライトの区画一覧で手動確認した空き土地を保存しています。");
        ImGui.TextDisabled("同じ区を再確認すると、その区の保存内容を最新状態で置き換えます。");

        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("サイズ", ref sizeFilterIndex, SizeFilters, SizeFilters.Length);
        ImGui.SameLine();

        var worlds = plugin.Configuration.OpenPlots.Select(x => x.WorldName)
            .Distinct().OrderBy(x => x).Prepend("ALL").ToArray();
        if (!worlds.Contains(worldFilter))
            worldFilter = "ALL";
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("ワールド", worldFilter))
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
            ImGui.TextDisabled("条件に一致する空き土地はありません。");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("open-plots", 7, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("ワールド", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("住宅街", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("区・番地", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("サイズ", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("価格", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("応募", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("確認日時", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
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
                ImGui.TextUnformatted($"{plot.DistrictName} {plot.WardNumber}区 {plot.PlotNumber}番地");
                ImGui.Separator();
                if (ImGui.MenuItem("地図で位置を表示"))
                    OpenPlotMap(plot);
                var lifestreamAvailable = Plugin.IsLifestreamAvailable();
                if (!lifestreamAvailable)
                    ImGui.BeginDisabled();
                if (ImGui.MenuItem("Lifestreamで自動移動"))
                    TravelToPlot(plot);
                if (!lifestreamAvailable)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Lifestreamが読み込まれていません。");
                }
                ImGui.EndPopup();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot.DistrictName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{plot.WardNumber}区 {plot.PlotNumber}番地");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot.Size);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{plot.Price:N0} G");
            ImGui.TableNextColumn();
            if (bidCount > 0)
                ImGui.TextColored(new Vector4(0.35f, 1f, 0.55f, 1f), $"応募中 ×{bidCount}");
            else
                ImGui.TextDisabled("—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot.CheckedAt.ToString("MM/dd（ddd） HH:mm", JapaneseCulture));
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
            ? $"{plot.DistrictName} {plot.WardNumber}区 {plot.PlotNumber}番地を表示"
            : "地図を開けませんでした。";
    }

    private void TravelToPlot(OpenPlotRecord plot)
    {
        mapPreviewMessage = Plugin.TravelToOpenPlot(plot)
            ? $"{plot.WorldName} {plot.DistrictName} {plot.WardNumber}区 {plot.PlotNumber}番地へ移動開始"
            : "Lifestreamで移動を開始できませんでした。";
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
                ? ("参加", new Vector4(0.25f, 0.9f, 0.45f, 1f))
                : ("未参加", new Vector4(1f, 0.35f, 0.35f, 1f));

        if (!hasEntry)
            return ("—（未参加）", new Vector4(0.6f, 0.6f, 0.6f, 1f));

        return record.ResultChecked
            ? ("確認済", new Vector4(0.25f, 0.9f, 0.45f, 1f))
            : ("未確認", new Vector4(1f, 0.35f, 0.35f, 1f));
    }

    private static string GetPhaseDeadline(LotteryCycle cycle) => cycle.Phase == LotteryPhase.Entry
        ? $"応募締切 {FormatDate(cycle.EntryEndsAt)}"
        : $"発表終了 {FormatDate(cycle.ResultsEndAt)}";

    private static string FormatDate(DateTime value) =>
        value.ToString("MM/dd（ddd） HH:mm", JapaneseCulture);
}

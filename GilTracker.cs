using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Text;

namespace AltMate;

public sealed unsafe class GilTracker : IDisposable
{
    private readonly Plugin plugin;
    private DateTime lastCheckUtc;
    private bool fcGilWasLoaded;

    public GilTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now - lastCheckUtc < TimeSpan.FromSeconds(2))
            return;
        lastCheckUtc = now;
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return;
            var contentId = Plugin.PlayerState.ContentId;
            if (!plugin.Configuration.CharacterGil.TryGetValue(contentId, out var character))
            {
                character = new CharacterGilRecord { ContentId = contentId };
                plugin.Configuration.CharacterGil[contentId] = character;
            }
            var changed = false;
            var gil = inventory->GetGil();
            if (character.Gil != gil || now - character.UpdatedAt.ToUniversalTime() > TimeSpan.FromMinutes(1))
            {
                character.CharacterName = Plugin.PlayerState.CharacterName;
                character.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
                character.Gil = gil;
                character.UpdatedAt = DateTime.Now;
                changed = true;
            }

            var retainers = RetainerManager.Instance();
            if (retainers != null && retainers->IsReady)
            {
                for (uint i = 0; i < retainers->GetRetainerCount(); i++)
                {
                    var retainer = retainers->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0 || !retainer->Available)
                        continue;
                    var name = ReadUtf8((byte*)retainer + 0x08, 32);
                    if (!character.Retainers.TryGetValue(retainer->RetainerId, out var record) ||
                        record.Gil != retainer->Gil || record.Name != name)
                    {
                        character.Retainers[retainer->RetainerId] = new RetainerGilRecord
                        {
                            RetainerId = retainer->RetainerId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"リテイナー {i + 1}" : name,
                            Gil = retainer->Gil,
                            UpdatedAt = DateTime.Now,
                        };
                        changed = true;
                    }
                }
            }

            var fcContainer = inventory->GetInventoryContainer(InventoryType.FreeCompanyGil);
            if (fcContainer != null && fcContainer->IsLoaded)
            {
                var agentModule = AgentModule.Instance();
                var agent = agentModule == null ? null :
                    (AgentFreeCompany*)agentModule->GetAgentByInternalId(AgentId.FreeCompany);
                var info = agent == null ? null : agent->InfoProxyFreeCompany;
                if (info != null && info->Id != 0)
                {
                    var fcGil = inventory->GetFreeCompanyGil();
                    var fcName = info->NameString;
                    if (!plugin.Configuration.FreeCompanyGil.TryGetValue(info->Id, out var fc) ||
                        fc.Gil != fcGil || (!string.IsNullOrWhiteSpace(fcName) && fc.Name != fcName) || !fcGilWasLoaded)
                    {
                        fc ??= new FreeCompanyGilRecord { FreeCompanyId = info->Id };
                        if (!string.IsNullOrWhiteSpace(fcName))
                            fc.Name = fcName;
                        else if (IsGeneratedFcName(fc.Name))
                            fc.Name = "不明なFC";
                        fc.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
                        fc.Gil = fcGil;
                        fc.UpdatedAt = DateTime.Now;
                        fc.LastCheckedByContentId = contentId;
                        fc.LastCheckedByName = Plugin.PlayerState.CharacterName;
                        plugin.Configuration.FreeCompanyGil[info->Id] = fc;
                        changed = true;
                    }

                }
            }
            fcGilWasLoaded = fcContainer != null && fcContainer->IsLoaded;

            var workshopAgentModule = AgentModule.Instance();
            var workshopAgent = workshopAgentModule == null ? null :
                (AgentFreeCompany*)workshopAgentModule->GetAgentByInternalId(AgentId.FreeCompany);
            var workshopInfo = workshopAgent == null ? null : workshopAgent->InfoProxyFreeCompany;
            if (workshopInfo != null && workshopInfo->Id != 0 &&
                HousingManager.Instance()->WorkshopTerritory != null)
            {
                if (!plugin.Configuration.FreeCompanyGil.TryGetValue(workshopInfo->Id, out var workshopFc))
                {
                    workshopFc = new FreeCompanyGilRecord
                    {
                        FreeCompanyId = workshopInfo->Id,
                        Name = string.IsNullOrWhiteSpace(workshopInfo->NameString) ? "不明なFC" : workshopInfo->NameString,
                        WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString(),
                        LastCheckedByContentId = contentId,
                        LastCheckedByName = Plugin.PlayerState.CharacterName,
                    };
                    plugin.Configuration.FreeCompanyGil[workshopInfo->Id] = workshopFc;
                }
                else if (!string.IsNullOrWhiteSpace(workshopInfo->NameString) && workshopFc.Name != workshopInfo->NameString)
                {
                    workshopFc.Name = workshopInfo->NameString;
                    changed = true;
                }
                if (UpdateSubmarines(workshopFc, now))
                    changed = true;
            }

            if (changed)
                plugin.Configuration.Save();
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "ギル情報を更新できませんでした。");
        }
    }

    private static bool IsGeneratedFcName(string name)
    {
        if (!name.StartsWith("FC ", StringComparison.Ordinal) || name.Length <= 3)
            return false;
        foreach (var character in name.AsSpan(3))
            if (!Uri.IsHexDigit(character))
                return false;
        return true;
    }

    private static bool UpdateSubmarines(FreeCompanyGilRecord fc, DateTime nowUtc)
    {
        var housing = HousingManager.Instance();
        if (housing->WorkshopTerritory == null)
            return false;

        var observed = new System.Collections.Generic.Dictionary<string, SubmarineRecord>();
        var vessels = housing->WorkshopTerritory->Submersible;
        for (var index = 0; index < Math.Min(4, vessels.DataPointers.Length); index++)
        {
            var vessel = vessels.DataPointers[index].Value;
            if (vessel == null)
                continue;
            var name = ReadUtf8(vessel->Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            observed[name] = new SubmarineRecord { Name = name, ReturnTimeUnix = vessel->ReturnTime };
        }
        if (observed.Count == 0)
            return false;

        var changed = observed.Count != fc.Submarines.Count;
        if (!changed)
            foreach (var pair in observed)
                if (!fc.Submarines.TryGetValue(pair.Key, out var current) ||
                    current.ReturnTimeUnix != pair.Value.ReturnTimeUnix)
                {
                    changed = true;
                    break;
                }
        if (!changed)
            return false;

        fc.Submarines = observed;
        fc.SubmarinesUpdatedAt = nowUtc.ToLocalTime();
        return true;
    }

    private static string ReadUtf8(byte* pointer, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && pointer[length] != 0)
            length++;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(pointer, length);
    }

    private static string ReadUtf8(Span<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var length = terminator < 0 ? bytes.Length : terminator;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..length]);
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}

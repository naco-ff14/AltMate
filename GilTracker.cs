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
                    var fcName = ReadUtf8((byte*)info + 0x7C, 22);
                    if (!plugin.Configuration.FreeCompanyGil.TryGetValue(info->Id, out var fc) ||
                        fc.Gil != fcGil || fc.Name != fcName || !fcGilWasLoaded)
                    {
                        plugin.Configuration.FreeCompanyGil[info->Id] = new FreeCompanyGilRecord
                        {
                            FreeCompanyId = info->Id,
                            Name = string.IsNullOrWhiteSpace(fcName) ? $"FC {info->Id:X}" : fcName,
                            WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString(),
                            Gil = fcGil,
                            UpdatedAt = DateTime.Now,
                            LastCheckedByContentId = contentId,
                            LastCheckedByName = Plugin.PlayerState.CharacterName,
                        };
                        changed = true;
                    }
                }
            }
            fcGilWasLoaded = fcContainer != null && fcContainer->IsLoaded;

            if (changed)
                plugin.Configuration.Save();
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "ギル情報を更新できませんでした。");
        }
    }

    private static string ReadUtf8(byte* pointer, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && pointer[length] != 0)
            length++;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(pointer, length);
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}

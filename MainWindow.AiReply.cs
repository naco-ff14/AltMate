using Dalamud.Bindings.ImGui;
using System;
using System.IO;
using System.Text.Json;

namespace AltMate;

public sealed partial class MainWindow
{
    private string aiReplyKey = "";
    private ulong aiSettingsCharacter;
    private string aiSettingsError = "";

    private sealed record AiReplyPreferences(bool Gemini, bool Tell, bool Party, bool WithoutName, string Nickname);

    private static string AiSettingsPath(ulong character) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AltMate", $"ai-reply-{character}.json");

    private void LoadAiSettings(ulong character, AiChatReply reply)
    {
        aiSettingsCharacter = character;
        aiReplyKey = "";
        aiSettingsError = "";
        AiReplyPreferences? saved = null;
        try
        {
            var path = AiSettingsPath(character);
            if (File.Exists(path)) saved = JsonSerializer.Deserialize<AiReplyPreferences>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            aiSettingsError = Loc.L("AI会話の設定を読み込めませんでした。", "Could not load AI chat settings.");
        }
        reply.UseGemini = saved?.Gemini ?? false;
        reply.Tell = saved?.Tell ?? false;
        reply.Party = saved?.Party ?? false;
        reply.PartyRespondWithoutName = saved?.WithoutName ?? false;
        reply.PartyNickname = saved?.Nickname ?? "";
    }

    private void SaveAiSettings(ulong character, AiChatReply reply)
    {
        try
        {
            var path = AiSettingsPath(character);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AiReplyPreferences(
                reply.UseGemini, reply.Tell, reply.Party, reply.PartyRespondWithoutName, reply.PartyNickname)));
            File.Move(temporary, path, overwrite: true);
            aiSettingsError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            aiSettingsError = Loc.L("AI会話の設定を保存できませんでした。", "Could not save AI chat settings.");
        }
    }

    private void DrawAiReplySettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("AIチャット自動返信", "AI chat auto-reply"));
        var reply = plugin.AiReply;
        var character = Plugin.PlayerState.ContentId;
        if (!Plugin.PlayerState.IsLoaded || character == 0)
        {
            ImGui.TextUnformatted(Loc.L("キャラクターでログインしてください。", "Log in to a character."));
            return;
        }
        if (aiSettingsCharacter != character) LoadAiSettings(character, reply);
        var changed = false;
        ImGui.TextWrapped(Loc.L(
            "返信対象の本文を選択したAPIに送ります。料金や無料枠はAPI側の設定に従います。返信には [AI] が付きます。",
            "Eligible message text is sent to the selected API. Pricing and free limits depend on your API account. Replies include [AI]."));
        if (!reply.Enabled)
        {
            if (ImGui.Checkbox(Loc.L("Geminiを使う（オフでOpenAI）", "Use Gemini (off for OpenAI)"), ref reply.UseGemini))
            {
                aiReplyKey = "";
                changed = true;
            }
            ImGui.InputTextWithHint("##ai-reply-key", reply.UseGemini ? "Gemini API key" : "OpenAI API key",
                ref aiReplyKey, 512, ImGuiInputTextFlags.Password);
        }
        else
            ImGui.TextUnformatted(reply.UseGemini ? "Gemini" : "OpenAI");
        changed |= ImGui.Checkbox("Tell##ai-reply", ref reply.Tell);
        changed |= ImGui.Checkbox(Loc.L("パーティ", "Party"), ref reply.Party);
        if (reply.Party)
        {
            ImGui.InputTextWithHint("##ai-party-nickname", Loc.L("呼び名（任意）", "Nickname (optional)"),
                ref reply.PartyNickname, 64);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            changed |= ImGui.Checkbox(Loc.L("名前なしの発言にも返す", "Also reply to undirected messages"),
                ref reply.PartyRespondWithoutName);
            ImGui.TextWrapped(Loc.L("「ルリ、次どこ行く？」のように呼びかけます。名前なしの返信担当はパーティに1人だけ設定してください。",
                "Address a character naturally, e.g. “Luli, where next?” Enable undirected replies on only one party member."));
        }
        if (changed) SaveAiSettings(character, reply);
        if (aiSettingsError.Length > 0) ImGui.TextWrapped(aiSettingsError);
        if (reply.Enabled)
        {
            if (ImGui.Button(Loc.L("自動返信を停止", "Stop auto-replies"))) reply.Disable();
        }
        else if (ImGui.Button(Loc.L("このキャラで自動返信を開始", "Start auto-replies for this character")) &&
            reply.Enable(aiReplyKey)) aiReplyKey = "";
        ImGui.TextWrapped(reply.Status);
    }
}

using Dalamud.Bindings.ImGui;

namespace AltMate;

public sealed partial class MainWindow
{
    private string aiReplyKey = "";

    private void DrawAiReplySettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("AIチャット自動返信", "AI chat auto-reply"));
        var reply = plugin.AiReply;
        ImGui.TextWrapped(Loc.L(
            "返信対象の本文をOpenAI APIに送ります（従量課金）。返信には [AI] が付きます。",
            "Eligible message text is sent to the OpenAI API and billed by usage. Replies include [AI]."));
        if (!reply.Enabled)
            ImGui.InputTextWithHint("##ai-reply-key", "OpenAI API key", ref aiReplyKey, 512, ImGuiInputTextFlags.Password);
        ImGui.Checkbox("Tell##ai-reply", ref reply.Tell);
        ImGui.Checkbox(Loc.L("パーティ", "Party"), ref reply.Party);
        if (reply.Party)
        {
            ImGui.InputTextWithHint("##ai-party-nickname", Loc.L("呼び名（任意）", "Nickname (optional)"),
                ref reply.PartyNickname, 64);
            ImGui.Checkbox(Loc.L("名前なしの発言にも返す", "Also reply to undirected messages"),
                ref reply.PartyRespondWithoutName);
            ImGui.TextWrapped(Loc.L("「ルリ、次どこ行く？」のように呼びかけます。名前なしの返信担当はパーティに1人だけ設定してください。",
                "Address a character naturally, e.g. “Luli, where next?” Enable undirected replies on only one party member."));
        }
        if (reply.Enabled)
        {
            if (ImGui.Button(Loc.L("自動返信を停止", "Stop auto-replies"))) reply.Disable();
        }
        else if (ImGui.Button(Loc.L("このキャラで自動返信を開始", "Start auto-replies for this character")) &&
            reply.Enable(aiReplyKey)) aiReplyKey = "";
        ImGui.TextWrapped(reply.Status);
    }
}

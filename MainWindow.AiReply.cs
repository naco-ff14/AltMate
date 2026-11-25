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
            "返信対象の本文を選択したAPIに送ります。料金や無料枠はAPI側の設定に従います。返信には [AI] が付きます。",
            "Eligible message text is sent to the selected API. Pricing and free limits depend on your API account. Replies include [AI]."));
        if (!reply.Enabled)
        {
            if (ImGui.Checkbox(Loc.L("Geminiを使う（オフでOpenAI）", "Use Gemini (off for OpenAI)"), ref reply.UseGemini))
                aiReplyKey = "";
            ImGui.InputTextWithHint("##ai-reply-key", reply.UseGemini ? "Gemini API key" : "OpenAI API key",
                ref aiReplyKey, 512, ImGuiInputTextFlags.Password);
        }
        else
            ImGui.TextUnformatted(reply.UseGemini ? "Gemini" : "OpenAI");
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

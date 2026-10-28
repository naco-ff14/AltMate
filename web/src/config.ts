export function configError(url: string, key: string): string | null {
  if (!url || !key)
    return "Supabase の接続設定がありません。管理者が環境変数を設定して再ビルドしてください。";
  try {
    const parsed = new URL(url);
    if (
      parsed.protocol !== "https:" ||
      !parsed.hostname.endsWith(".supabase.co") ||
      parsed.pathname !== "/" ||
      parsed.search ||
      parsed.hash ||
      parsed.username ||
      parsed.password
    ) {
      return "Supabase のプロジェクト URL を確認してください（https://…supabase.co）。";
    }
  } catch {
    return "Supabase のプロジェクト URL が正しくありません。";
  }
  if (key.startsWith("sb_publishable_")) return null;
  try {
    const payload = JSON.parse(
      atob(key.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")),
    );
    if (payload.role === "anon") return null;
  } catch {
    /* Reject anything except a public publishable/anon key. */
  }
  return "公開用の publishable / anon キーを指定してください。secret / service_role キーは使用できません。";
}

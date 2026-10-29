import { useState } from "react";
import type { FormEvent } from "react";
import { configurationError, supabase } from "./supabase";

export default function PasswordRecovery({
  authenticated,
  onDone,
}: {
  authenticated: boolean;
  onDone: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [sent, setSent] = useState(false);
  const [saved, setSaved] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!supabase || busy) return;
    const form = event.currentTarget;
    const values = new FormData(form);
    setError("");
    if (authenticated && values.get("password") !== values.get("confirm")) {
      setError("2つのパスワードが一致しません。");
      return;
    }
    setBusy(true);
    try {
      if (authenticated) {
        const { error: updateError } = await supabase.auth.updateUser({
          password: String(values.get("password")),
        });
        if (updateError) {
          setError(
            "変更できませんでした。パスワードの条件を確認してください。リンクの有効期限が切れた場合は、再設定メールを送り直してください。",
          );
        } else {
          form.reset();
          setSaved(true);
          // The SDK clears the local session. Never keep password values in storage or logs.
          await supabase.auth.signOut({ scope: "local" });
        }
      } else {
        const { error: sendError } = await supabase.auth.resetPasswordForEmail(
          String(values.get("email")).trim(),
          { redirectTo: `${window.location.origin}/?reset=password` },
        );
        if (sendError)
          setError(
            "メールを送信できませんでした。少し時間をおいて再度お試しください。",
          );
        else {
          form.reset();
          setSent(true);
        }
      }
    } catch {
      setError("接続できませんでした。時間をおいて再度お試しください。");
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="login-panel recovery-page">
      <div className="login-card">
        <p className="eyebrow">ALTMATE / ACCOUNT RECOVERY</p>
        <h2>
          {saved
            ? "パスワードを変更しました"
            : authenticated
              ? "新しいパスワードを設定"
              : "パスワードの再設定"}
        </h2>
        {saved ? (
          <>
            <p className="muted">
              次回から新しいパスワードでログインできます。
            </p>
            <button className="primary login-button" onClick={onDone}>
              ログイン画面へ
            </button>
          </>
        ) : (
          <>
            <p className="muted">
              {authenticated
                ? "新しいパスワードを2回入力してください。"
                : "登録済みのメールアドレスに再設定用リンクを送ります。期限切れの場合も、ここから送り直せます。"}
            </p>
            {configurationError ? (
              <div className="notice error" role="alert">
                {configurationError}
              </div>
            ) : (
              <form onSubmit={submit}>
                {authenticated ? (
                  <>
                    <label htmlFor="new-password">新しいパスワード</label>
                    <input
                      id="new-password"
                      name="password"
                      type="password"
                      minLength={8}
                      required
                      autoComplete="new-password"
                      disabled={busy}
                    />
                    <label htmlFor="confirm-password">
                      新しいパスワード（確認）
                    </label>
                    <input
                      id="confirm-password"
                      name="confirm"
                      type="password"
                      minLength={8}
                      required
                      autoComplete="new-password"
                      disabled={busy}
                    />
                    <p className="login-note">
                      8文字以上。Supabaseで設定された追加条件がある場合は、その条件も満たしてください。
                    </p>
                  </>
                ) : (
                  <>
                    <label htmlFor="recovery-email">
                      登録済みメールアドレス
                    </label>
                    <input
                      id="recovery-email"
                      name="email"
                      type="email"
                      autoComplete="email"
                      maxLength={254}
                      required
                      disabled={busy}
                    />
                  </>
                )}
                {error && (
                  <div className="notice error" role="alert">
                    {error}
                  </div>
                )}
                {sent && (
                  <div className="notice" role="status">
                    登録されたアドレスであれば、再設定メールが届きます。迷惑メールフォルダーも確認してください。
                  </div>
                )}
                <button className="primary login-button" disabled={busy}>
                  {busy
                    ? "処理中…"
                    : authenticated
                      ? "パスワードを変更"
                      : "再設定メールを送信"}
                </button>
              </form>
            )}
            <button className="login-button" disabled={busy} onClick={onDone}>
              戻る
            </button>
          </>
        )}
      </div>
    </main>
  );
}

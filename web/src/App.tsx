import { useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import type { Session } from "@supabase/supabase-js";
import { configurationError, recoveryRequested, supabase } from "./supabase";
import PasswordRecovery from "./PasswordRecovery";
import SyncDevices, { normalizePairingCode } from "./SyncDevices";
import { dateLabel, loadSnapshot } from "./data";
import type { Snapshot } from "./data";
import Operations from "./DashboardViews";
const sections = [
  ["overview", "◈", "概要"],
  ["housing", "⌂", "ハウジング"],
  ["gil", "◎", "ギル・FC"],
  ["submarines", "≋", "潜水艦"],
  ["delivery", "◇", "お得意様取引"],
  ["devices", "⚙", "設定"],
] as const;
type Section = (typeof sections)[number][0];
function Brand() {
  return (
    <div className="brand">
      <span className="brand-mark">
        A<span>✦</span>
      </span>
      <div>
        AltMate<small>WEB DASHBOARD</small>
      </div>
    </div>
  );
}
function Notice({
  children,
  error = false,
}: {
  children: ReactNode;
  error?: boolean;
}) {
  return (
    <div
      className={`notice ${error ? "error" : ""}`}
      role={error ? "alert" : "status"}
    >
      {children}
    </div>
  );
}

function Login({ onRecovery, onLink }: { onRecovery: () => void; onLink: (code: string) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [code, setCode] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!supabase || busy) return;
    setBusy(true);
    setError("");
    const form = event.currentTarget;
    const data = new FormData(form);
    const normalized = normalizePairingCode(code);
    if (normalized && !/^[A-F0-9]{12}$/.test(normalized)) {
      setError("プラグインに表示された12文字のコードを入力してください。"); setBusy(false); return;
    }
    onLink(normalized);
    try {
      const result = await supabase.auth.signInWithPassword({
        email: String(data.get("email")).trim(),
        password: String(data.get("password")),
      });
      if (result.error)
        setError(
          "ログインできませんでした。メールアドレスとパスワード、または接続状況を確認してください。",
        );
      else form.reset();
    } catch {
      setError("接続できませんでした。時間をおいて再度お試しください。");
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="login-shell">
      <section className="login-intro">
        <Brand />
        <div>
          <p className="eyebrow">YOUR ADVENTURE, AT A GLANCE</p>
          <h1>
            冒険の記録を、
            <br />
            いつでも手元に。
          </h1>
          <p>
            キャラクターの資産からハウジング、潜水艦まで。
            <br />
            あなただけの情報を、ひとつの場所で。
          </p>
          <div className="constellation" aria-hidden="true">
            ◇<span>✦</span>◇
          </div>
        </div>
        <small>AltMate · Private workspace</small>
      </section>
      <main className="login-panel">
        <div className="login-card">
          <p className="eyebrow">WELCOME BACK</p>
          <h2>マイダッシュボード</h2>
          <p className="muted">登録済みのアカウントでログインしてください。</p>
          {configurationError ? (
            <Notice error>{configurationError}</Notice>
          ) : (
            <form onSubmit={submit}>
              <label htmlFor="email">メールアドレス</label>
              <input
                id="email"
                name="email"
                type="email"
                autoComplete="username"
                required
                maxLength={254}
                placeholder="you@example.com"
                disabled={busy}
              />
              <label htmlFor="password">パスワード</label>
              <input
                id="password"
                name="password"
                type="password"
                autoComplete="current-password"
                required
                disabled={busy}
              />
              <label htmlFor="login-code">プラグイン連携コード（初回のみ・任意）</label>
              <input id="login-code" value={code} maxLength={20} autoComplete="off" spellCheck={false}
                placeholder="ABCDEF-123456" disabled={busy} onChange={e => setCode(e.target.value)} />
              {code.trim() && <p className="muted">自分のプラグインで発行したコードを入力してください。ログインと同時に、このアカウントへの同期を承認します。</p>}
              {error && <Notice error>{error}</Notice>}
              <button className="primary login-button" disabled={busy}>
                {busy ? "ログイン中…" : code.trim() ? "ログインして連携 →" : "ログイン →"}
              </button>
            </form>
          )}
          <p className="login-note">
            招待・作成済みのアカウント専用です。
            <br />
            <button type="button" onClick={onRecovery}>
              パスワードを忘れた方
            </button>
          </p>
          <div className="privacy-note">
            ◇ ログインした本人のデータだけを表示します
          </div>
        </div>
      </main>
    </div>
  );
}

function Dashboard({ session, linkCode, onCodeUsed }: { session: Session; linkCode: string; onCodeUsed: () => void }) {
  const [section, setSection] = useState<Section>(linkCode || window.location.hash === "#link" ? "devices" : "overview");
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [checkedAt, setCheckedAt] = useState("");
  const [signingOut, setSigningOut] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    loadSnapshot(supabase!, session.user.id, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) {
          setSnapshot(data);
          setCheckedAt(new Date().toISOString());
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted)
          setError(
            err instanceof Error
              ? err.message
              : "データを取得できませんでした。",
          );
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [session.user.id, refresh]);
  const entities = snapshot?.entities ?? [];
  const latest = entities
    .filter(
      (e) => !["sync_metadata", "character_profile"].includes(e.entity_type),
    )
    .map((e) => e.observed_at)
    .filter((d) => Number.isFinite(Date.parse(d)))
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  async function logout() {
    setSigningOut(true);
    setError("");
    try {
      const { error: result } = await supabase!.auth.signOut({
        scope: "local",
      });
      if (result) setError("ログアウトできませんでした。再度お試しください。");
    } catch {
      setError("ログアウトできませんでした。再度お試しください。");
    } finally {
      setSigningOut(false);
    }
  }
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <Brand />
        <p className="nav-label">WORKSPACE</p>
        <nav aria-label="メインメニュー">
          {sections.map(([id, icon, label]) => (
            <button
              key={id}
              className={section === id ? "nav-item active" : "nav-item"}
              aria-current={section === id ? "page" : undefined}
              onClick={() => setSection(id)}
            >
              <span aria-hidden="true">{icon}</span>
              {label}
              {section === id && <i />}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <span className="private-badge">● PRIVATE</span>
          <p title={session.user.email}>{session.user.email}</p>
          <button onClick={logout} disabled={signingOut}>
            {signingOut ? "ログアウト中…" : "ログアウト ↗"}
          </button>
        </div>
      </aside>
      <main className="workspace">
        <header className="topbar">
          <span>
            マイワークスペース <span className="divider">/</span>{" "}
            {sections.find((s) => s[0] === section)?.[2]}
          </span>
          <span className="private-tag">◇ 本人のみ</span>
        </header>
        <div className="page">
          <div className="page-heading">
            <div>
              <p className="eyebrow">ALTMATE / MY WORKSPACE</p>
              <h1>
                {section === "overview"
                  ? "冒険のダッシュボード"
                  : sections.find((s) => s[0] === section)?.[2]}
              </h1>
              <p className="muted">
                {latest
                  ? `最新の観測 ${dateLabel(latest)}`
                  : "あなたの冒険の記録が、ここに集まります。"}
              </p>
            </div>
            <button
              className="refresh"
              disabled={loading}
              onClick={() => setRefresh((value) => value + 1)}
            >
              {loading ? "取得中…" : "↻ 表示を更新"}
            </button>
          </div>
          {error && (
            <Notice error>
              {error}
              {snapshot && " 前回取得した記録を表示しています。"}
            </Notice>
          )}
          {loading && <Notice>記録を取得しています…</Notice>}
          {!loading && !error && snapshot && !latest && (
            <Notice>
              初回同期を待っています。「設定」の「プラグイン連携」で登録してください。
            </Notice>
          )}
          {section === "devices" ? (
              <section className="card">
                <div className="card-heading">
                  <h2>設定</h2>
                </div>
                <SyncDevices
                  devices={snapshot?.devices ?? []}
                  initialCode={linkCode}
                  onCodeUsed={onCodeUsed}
                  onRefresh={() => setRefresh((value) => value + 1)}
                />
              </section>
            ) : snapshot && (
              <Operations
                section={section}
                userId={session.user.id}
                entities={entities}
                navigate={(view) => setSection(view)}
              />
            )}
          {section !== "devices" && !snapshot && !loading && error && (
            <div className="op-empty">
              <h2>記録を表示できません</h2>「表示を更新」から再試行できます。
            </div>
          )}
          <footer>
            AltMate{" "}
            <span>
              表示確認 {checkedAt ? dateLabel(checkedAt) : "未取得"} ·
              日時は端末のタイムゾーン
            </span>
          </footer>
        </div>
      </main>
    </div>
  );
}
export default function App() {
  const [linkCode, setLinkCode] = useState("");
  const [session, setSession] = useState<Session | null>(null);
  const [ready, setReady] = useState(!supabase);
  const [authError, setAuthError] = useState(false);
  const [recovery, setRecovery] = useState(recoveryRequested);
  function openRecovery() {
    window.history.replaceState(null, "", "/?reset=password");
    setRecovery(true);
  }
  function closeRecovery() {
    window.history.replaceState(null, "", "/");
    setRecovery(false);
  }
  useEffect(() => {
    if (!supabase) return;
    let active = true;
    // Subscription is registered first so a login/logout cannot be overwritten by an older session read.
    let authChanged = false;
    const {
      data: { subscription },
    } = supabase.auth.onAuthStateChange((event, next) => {
      authChanged = true;
      if (active) {
        if (event === "PASSWORD_RECOVERY") openRecovery();
        setSession(next);
        setReady(true);
        setAuthError(false);
      }
    });
    supabase.auth
      .getSession()
      .then(({ data, error }) => {
        if (active && !authChanged) {
          setSession(error ? null : data.session);
          setAuthError(Boolean(error));
          setReady(true);
        }
      })
      .catch(() => {
        if (active && !authChanged) {
          setAuthError(true);
          setReady(true);
        }
      });
    return () => {
      active = false;
      subscription.unsubscribe();
    };
  }, []);
  if (!ready)
    return (
      <main className="startup" role="status">
        AltMate · ログイン状態を確認しています…
      </main>
    );
  if (recovery)
    return (
      <PasswordRecovery
        authenticated={Boolean(session)}
        onDone={closeRecovery}
      />
    );
  return (
    <>
      {authError && (
        <Notice error>
          ログイン状態を確認できませんでした。再度ログインしてください。
        </Notice>
      )}
      {session ? (
        <Dashboard key={session.user.id} session={session} linkCode={linkCode} onCodeUsed={() => setLinkCode("")} />
      ) : (
        <Login onRecovery={openRecovery} onLink={setLinkCode} />
      )}
    </>
  );
}

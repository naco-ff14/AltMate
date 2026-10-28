import { useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import type { Session } from "@supabase/supabase-js";
import { configurationError, supabase } from "./supabase";
import {
  dateLabel,
  isStale,
  loadSnapshot,
  numberField,
  stringField,
  totalGil,
  unixDateLabel,
} from "./data";
import type { Entity, Snapshot } from "./data";

const sections = [
  ["overview", "◈", "概要"],
  ["characters", "♙", "キャラクター"],
  ["housing", "⌂", "ハウジング"],
  ["gil", "◎", "ギル・FC"],
  ["submarines", "≋", "潜水艦"],
  ["delivery", "◇", "お得意様取引"],
  ["devices", "▣", "同期端末"],
] as const;
type Section = (typeof sections)[number][0];
const amount = (n: number | null, suffix = "") =>
  n === null ? "未記録" : `${n.toLocaleString("ja-JP")}${suffix}`;

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

function Login() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!supabase || busy) return;
    setBusy(true);
    setError("");
    const form = event.currentTarget;
    const data = new FormData(form);
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
              {error && <Notice error>{error}</Notice>}
              <button className="primary login-button" disabled={busy}>
                {busy ? "ログイン中…" : "ログイン →"}
              </button>
            </form>
          )}
          <p className="login-note">
            招待・作成済みのアカウント専用です。
            <br />
            パスワードが分からない場合は管理者にお問い合わせください。
          </p>
          <div className="privacy-note">
            ◇ ログインした本人のデータだけを表示します
          </div>
        </div>
      </main>
    </div>
  );
}

function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <span aria-hidden="true">◇</span>
      <h3>{title}</h3>
      <p>
        {children ??
          "まだ同期された記録がありません。同期が届くと、ここに表示されます。"}
      </p>
    </div>
  );
}
function Card({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
}) {
  return (
    <section className="card">
      <div className="card-heading">
        <h2>{title}</h2>
        {subtitle && <span>{subtitle}</span>}
      </div>
      {children}
    </section>
  );
}
function Stamp({ entity }: { entity: Entity }) {
  return (
    <small className={isStale(entity.observed_at) ? "stale" : "muted"}>
      観測: {dateLabel(entity.observed_at)}
      {isStale(entity.observed_at) && " · 24時間以上前 / 日時不明"}
    </small>
  );
}
function Rows({
  rows,
  kind,
}: {
  rows: Entity[];
  kind: "character" | "housing" | "fc" | "submarine" | "lottery" | "delivery";
}) {
  if (!rows.length) return <Empty title="まだ記録がありません" />;
  return (
    <div className="records">
      {rows.map((e) => {
        const p = e.payload;
        return (
          <article className="record" key={`${e.entity_type}:${e.entity_key}`}>
            <div className="record-title">
              <h3>
                {stringField(
                  p,
                  kind === "fc" || kind === "submarine"
                    ? "Name"
                    : "CharacterName",
                )}
              </h3>
              <span>{stringField(p, "WorldName", "")}</span>
            </div>
            {kind === "character" && (
              <p className="record-value">
                {amount(numberField(p, "Gil"), " G")}
              </p>
            )}
            {kind === "housing" && (
              <>
                <p>
                  {p.IsOwned === true
                    ? "所有確認あり"
                    : p.IsOwned === false
                      ? "所有確認なし"
                      : "所有状況不明"}{" "}
                  ·{" "}
                  {p.EstateKind === 0
                    ? "個人宅"
                    : p.EstateKind === 1
                      ? "FCハウス"
                      : "種別未記録"}
                </p>
                <p>
                  区: {amount(numberField(p, "HouseWard"))} / 番地:{" "}
                  {amount(numberField(p, "HousePlot"))}
                </p>
                <p>最終入室: {dateLabel(p.LastEnteredAt)}</p>
                <small className="muted">
                  撤去期限はこの記録だけでは確定できません。ゲーム内の表示を確認してください。
                </small>
              </>
            )}
            {kind === "fc" && (
              <>
                <p>
                  FCギル:{" "}
                  {p.GilConfirmed === true
                    ? amount(numberField(p, "Gil"), " G")
                    : "未確認"}
                </p>
                <p>
                  カンパニークレジット:{" "}
                  {p.CompanyCreditsConfirmed === true
                    ? amount(numberField(p, "CompanyCredits"))
                    : "未確認"}
                </p>
                <small className="muted">
                  クレジット観測: {dateLabel(p.CompanyCreditsUpdatedAt)}
                </small>
              </>
            )}
            {kind === "submarine" && (
              <>
                <p>FC: {stringField(p, "FreeCompanyName")}</p>
                <p>
                  帰港予定: {unixDateLabel(numberField(p, "ReturnTimeUnix"))}
                </p>
              </>
            )}
            {kind === "lottery" && (
              <>
                <p>{stringField(p, "PlotAddress")}</p>
                <p>応募期間終了: {dateLabel(p.EntryPhaseEndsAt)}</p>
                <p>結果: {p.ResultChecked === true ? "確認済み" : "未確認"}</p>
              </>
            )}
            {kind === "delivery" && (
              <>
                <p>
                  今週の残り納品回数:{" "}
                  {amount(numberField(p, "RemainingWeeklyAllowances"))}
                </p>
                <p>
                  クラフタースクリップ · 紫貨{" "}
                  {amount(numberField(p, "PurpleCrafterScrip"))} / 橙貨{" "}
                  {amount(numberField(p, "OrangeCrafterScrip"))}
                </p>
                <p>
                  ギャザラースクリップ · 紫貨{" "}
                  {amount(numberField(p, "PurpleGathererScrip"))} / 橙貨{" "}
                  {amount(numberField(p, "OrangeGathererScrip"))}
                </p>
              </>
            )}
            <Stamp entity={e} />
          </article>
        );
      })}
    </div>
  );
}

function Dashboard({ session }: { session: Session }) {
  const [section, setSection] = useState<Section>("overview");
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [query, setQuery] = useState("");
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
  const filtered = entities.filter(
    (e) =>
      Object.values(e.payload).some(
        (v) =>
          typeof v === "string" &&
          v.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()),
      ) || !query.trim(),
  );
  const of = (type: string) => filtered.filter((e) => e.entity_type === type);
  const characters = of("character_gil");
  const housing = of("housing_demolition");
  const submarines = of("submarine");
  const fc = of("free_company");
  const latest = entities
    .map((e) => e.observed_at)
    .filter((d) => Number.isFinite(Date.parse(d)))
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  async function logout() {
    setSigningOut(true);
    setError("");
    try {
      const { error: logoutError } = await supabase!.auth.signOut({
        scope: "local",
      });
      if (logoutError)
        setError("ログアウトできませんでした。再度お試しください。");
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
              onClick={() => {
                setSection(id);
                setQuery("");
              }}
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
              onClick={() => setRefresh((r) => r + 1)}
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
          {!loading && !error && snapshot && !entities.length && (
            <Notice>
              初回同期を待っています。現在の SyncAgent
              はローカルプレビュー段階です。クラウドへの自動送信はまだ利用できません。
            </Notice>
          )}
          {section !== "devices" && (
            <div className="filter-row">
              <label htmlFor="search">記録を検索</label>
              <input
                id="search"
                type="search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="キャラクター名・ワールド・FC名"
              />
              <span>{filtered.length} 件の記録</span>
            </div>
          )}
          {snapshot && (
            <>
              {section === "overview" && (
                <>
                  <div className="stats">
                    <div className="stat">
                      <span>♙ キャラクター</span>
                      <strong>
                        {characters.length}
                        <small>人のギル記録</small>
                      </strong>
                    </div>
                    <div className="stat">
                      <span>◎ キャラクター所持金</span>
                      <strong>
                        {amount(totalGil(filtered))}
                        <small>
                          {totalGil(filtered) !== null
                            ? "GIL · FC・リテイナーを除く"
                            : "同期後に表示"}
                        </small>
                      </strong>
                    </div>
                    <div className="stat">
                      <span>⌂ ハウジング</span>
                      <strong>
                        {housing.length}
                        <small>件の記録</small>
                      </strong>
                    </div>
                    <div className="stat">
                      <span>≋ 潜水艦</span>
                      <strong>
                        {submarines.length}
                        <small>隻の記録</small>
                      </strong>
                    </div>
                  </div>
                  <div className="overview-grid">
                    <Card title="キャラクターの資産" subtitle="CHARACTERS">
                      <Rows rows={characters} kind="character" />
                    </Card>
                    <Card title="ハウジング" subtitle="ESTATES">
                      <Rows rows={housing} kind="housing" />
                    </Card>
                    <Card title="潜水艦" subtitle="SUBMERSIBLES">
                      <Rows rows={submarines} kind="submarine" />
                    </Card>
                    <Card title="フリーカンパニー" subtitle="FREE COMPANIES">
                      <Rows rows={fc} kind="fc" />
                    </Card>
                  </div>
                </>
              )}
              {section === "characters" && (
                <Card title="キャラクター">
                  <Rows rows={characters} kind="character" />
                </Card>
              )}
              {section === "housing" && (
                <div className="overview-grid">
                  <Card title="所有ハウジング">
                    <Rows rows={housing} kind="housing" />
                  </Card>
                  <Card title="土地抽選の記録">
                    <Rows rows={of("housing_lottery")} kind="lottery" />
                  </Card>
                </div>
              )}
              {section === "gil" && (
                <div className="overview-grid">
                  <Card title="キャラクター所持金">
                    <Rows rows={characters} kind="character" />
                  </Card>
                  <Card title="FC資産・クレジット">
                    <Rows rows={fc} kind="fc" />
                  </Card>
                </div>
              )}
              {section === "submarines" && (
                <Card title="潜水艦の帰港予定">
                  <Rows rows={submarines} kind="submarine" />
                </Card>
              )}
              {section === "delivery" && (
                <Card title="お得意様取引・スクリップ">
                  <Rows rows={of("custom_delivery")} kind="delivery" />
                </Card>
              )}
              {section === "devices" && (
                <Card title="登録済みの同期端末">
                  {!snapshot.devices.length ? (
                    <Empty title="同期端末は未登録です">
                      端末のペアリング機能は準備中です。プレビューの JSON
                      をアップロードする必要はありません。
                    </Empty>
                  ) : (
                    <div className="records">
                      {snapshot.devices.map((d) => (
                        <article className="record" key={d.id}>
                          <div className="record-title">
                            <h3>{d.name}</h3>
                            <span>{d.revoked_at ? "無効" : "登録済み"}</span>
                          </div>
                          <p>最終通信: {dateLabel(d.last_seen_at)}</p>
                        </article>
                      ))}
                    </div>
                  )}
                </Card>
              )}
            </>
          )}
          {!snapshot && !loading && error && (
            <Empty title="記録を表示できません">
              「表示を更新」から再試行できます。
            </Empty>
          )}
          <footer>
            AltMate{" "}
            <span>
              読み取り専用 · 表示確認{" "}
              {checkedAt ? dateLabel(checkedAt) : "未取得"} ·
              日時は端末のタイムゾーン
            </span>
          </footer>
        </div>
      </main>
    </div>
  );
}

export default function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [ready, setReady] = useState(!supabase);
  const [authError, setAuthError] = useState(false);
  useEffect(() => {
    if (!supabase) return;
    let active = true;
    // Subscription is registered first so a login/logout cannot be overwritten by an older session read.
    let authChanged = false;
    const {
      data: { subscription },
    } = supabase.auth.onAuthStateChange((_event, next) => {
      authChanged = true;
      if (active) {
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
  return (
    <>
      {authError && (
        <Notice error>
          ログイン状態を確認できませんでした。再度ログインしてください。
        </Notice>
      )}
      {session ? (
        <Dashboard key={session.user.id} session={session} />
      ) : (
        <Login />
      )}
    </>
  );
}

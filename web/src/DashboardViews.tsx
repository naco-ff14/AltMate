import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import type { Entity } from "./data";
import {
  dateLabel,
  mergeCompanies,
  numberField as num,
  stringField as str,
  unixDateLabel,
} from "./data";
import {
  activeEntities,
  assetSummary,
  characters,
  deliveryStatus,
  district,
  hasEntry,
  houseDeadline,
  estateRows,
  lotteryCycle,
  lotteryStatus,
  matches,
  personalGil,
  remaining,
  retainerGil,
  submarineStatus,
  time,
  wallet,
  weeklyReset,
  DAY,
} from "./operations";
export type View = "overview" | "housing" | "gil" | "submarines" | "delivery";
const money = (value: number | null) =>
  value === null ? "未記録" : `${value.toLocaleString("ja-JP")} G`;
const count = (value: number | null) =>
  value === null ? "—" : value.toLocaleString("ja-JP");
function Panel({
  title,
  aside,
  children,
}: {
  title: string;
  aside?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="op-panel">
      <header>
        <h2>{title}</h2>
        {aside}
      </header>
      {children}
    </section>
  );
}
function Empty({ children }: { children: ReactNode }) {
  return <div className="op-empty">{children}</div>;
}
function Table({
  columns,
  children,
  empty,
}: {
  columns: string[];
  children: ReactNode;
  empty?: boolean;
}) {
  return empty ? (
    <Empty>表示する記録がありません。</Empty>
  ) : (
    <div className="table-scroll">
      <p className="table-hint">表は左右にスクロールできます →</p>
      <table>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column}>{column}</th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}
function Badge({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: "good" | "warn" | "bad" | "neutral";
}) {
  return <span className={`op-badge ${tone}`}>{children}</span>;
}
function Person({ name, world }: { name: string; world: string }) {
  return (
    <>
      <strong>{name}</strong>
      <small className="cell-note">{world}</small>
    </>
  );
}
function Search({
  query,
  setQuery,
  children,
}: {
  query: string;
  setQuery: (s: string) => void;
  children?: ReactNode;
}) {
  return (
    <div className="op-tools">
      <label>
        <span>記録を検索</span>
        <input
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="名前・ワールド・住所"
        />
      </label>
      {children}
    </div>
  );
}
function AssetCards({ assets }: { assets: ReturnType<typeof assetSummary> }) {
  return (
    <div className="asset-strip">
      {[
        ["個人資産", assets.personal],
        ["FC資金", assets.fc],
        ["抽選預かり金", assets.deposit],
        ["総資産", assets.total],
      ].map(([label, value]) => (
        <div key={String(label)} className={label === "総資産" ? "total" : ""}>
          <span>{label}</span>
          <strong>{money(value as number | null)}</strong>
        </div>
      ))}
    </div>
  );
}
export default function Operations({
  section,
  entities: raw,
  navigate,
}: {
  section: View;
  entities: Entity[];
  navigate: (view: View) => void;
}) {
  const [now, setNow] = useState(Date.now());
  const [query, setQuery] = useState("");
  const [tab, setTab] = useState("lottery");
  const [filter, setFilter] = useState("all");
  const [sort, setSort] = useState("desc");
  useEffect(() => {
    const tick = () => setNow(Date.now());
    const timer = window.setInterval(tick, 30000);
    window.addEventListener("focus", tick);
    document.addEventListener("visibilitychange", tick);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener("focus", tick);
      document.removeEventListener("visibilitychange", tick);
    };
  }, []);
  useEffect(() => {
    setQuery("");
    setFilter("all");
    setSort("desc");
    setTab("lottery");
  }, [section]);
  const entities = activeEntities(raw);
  const roster = characters(entities);
  const cycle = lotteryCycle(entities, now);
  const assets = assetSummary(entities, now);
  const houses = estateRows(entities);
  const ships = entities.filter((e) => e.entity_type === "submarine");
  const pendingShips = ships.filter((e) =>
    ["帰港予定経過", "未出航・待機"].includes(submarineStatus(e, now)),
  );
  const unverified = roster.filter((c) =>
    ["今期未確認", "結果未確認", "周期未確認"].includes(
      lotteryStatus(c, cycle),
    ),
  ).length;
  const nearHouses = houses.filter(
    (e) => houseDeadline(e) !== null && houseDeadline(e)! - now <= 10 * DAY,
  ).length;
  const incomplete = roster.filter(
    (c) => deliveryStatus(c, now) === "未消化",
  ).length;
  const unknownDeliveries = roster.filter((c) =>
    ["今週未確認", "記録なし"].includes(deliveryStatus(c, now)),
  ).length;
  const missing = !assets.complete ? (
    <p className="op-note warning">
      リテイナー・空き土地・未確認キャラの情報は、追加データ対応版からの同期後にそろいます。未同期の残高は合計に含めていません。
    </p>
  ) : null;
  if (section === "overview")
    return (
      <div className="operations">
        <div className="overview-brief">
          <button className="asset-hero" onClick={() => navigate("gil")}>
            <span>総資産</span>
            <strong>{money(assets.total)}</strong>
            <small>
              個人 {money(assets.personal)} ／ FC {money(assets.fc)} ／
              抽選預かり {money(assets.deposit)}
            </small>
            <span className="hero-link">内訳を見る →</span>
          </button>
          <div className="attention-links">
            <button onClick={() => navigate("housing")}>
              <span>ハウジング</span>
              <strong>確認待ち {unverified}人</strong>
              <small>保持期限10日以内 {nearHouses}件</small>
            </button>
            <button onClick={() => navigate("submarines")}>
              <span>潜水艦</span>
              <strong>未出航・帰港予定経過 {pendingShips.length}隻</strong>
              <small>FCごとの状況を見る →</small>
            </button>
            <button onClick={() => navigate("delivery")}>
              <span>お得意様</span>
              <strong>未消化 {incomplete}人</strong>
              <small>今週未確認・記録なし {unknownDeliveries}人</small>
            </button>
          </div>
        </div>
        {assets.partial && (
          <p className="op-note">
            総資産は同期済みの記録の合計です。未記録・未確認の残高があるため、全資産の確定額ではありません。
          </p>
        )}
        {missing}
        <p className="op-note">
          必要な項目を選ぶと、詳細の一覧へ移動します。残り時間はゲームが動いていなくても更新されます。
        </p>
      </div>
    );

  if (section === "gil") {
    const rows = roster
      .filter((c) => matches([c.name, c.world], query))
      .sort((a, b) =>
        sort === "name"
          ? a.name.localeCompare(b.name, "ja")
          : sort === "wallet_desc"
            ? (wallet(b) ?? -1) - (wallet(a) ?? -1)
            : sort === "wallet_asc"
              ? (wallet(a) ?? Infinity) - (wallet(b) ?? Infinity)
              : sort === "asc"
                ? (personalGil(a, assets.complete) ?? Infinity) -
                  (personalGil(b, assets.complete) ?? Infinity)
                : (personalGil(b, assets.complete) ?? -1) -
                  (personalGil(a, assets.complete) ?? -1),
      );
    const companies = mergeCompanies(entities)
      .filter((e) => matches(Object.values(e.payload), query))
      .sort(
        (a, b) => (num(b.payload, "Gil") ?? -1) - (num(a.payload, "Gil") ?? -1),
      );
    return (
      <div className="operations">
        <AssetCards assets={assets} />
        <p className="op-note">
          個人資産＝キャラ所持金＋記録済みリテイナー。総資産＝個人資産＋FC資金＋今期の未確認の抽選預かり金。
        </p>
        {missing}
        <Search query={query} setQuery={setQuery}>
          <label>
            <span>並び順</span>
            <select value={sort} onChange={(e) => setSort(e.target.value)}>
              <option value="desc">個人資産の多い順</option>
              <option value="asc">個人資産の少ない順</option>
              <option value="wallet_desc">所持金の多い順</option>
              <option value="wallet_asc">所持金の少ない順</option>
              <option value="name">キャラクター名順</option>
            </select>
          </label>
        </Search>
        <Panel
          title="キャラクター別の資産"
          aside={<small>{rows.length}人</small>}
        >
          <Table
            columns={[
              "キャラクター",
              "所持金",
              "リテイナー",
              "個人資産",
              "抽選預かり",
              "観測日時",
            ]}
            empty={!rows.length}
          >
            {rows.map((c) => (
              <tr key={c.key}>
                <td>
                  <Person name={c.name} world={c.world} />
                  {c.retainers.length > 0 && (
                    <details>
                      <summary>リテイナー {c.retainers.length}人</summary>
                      {c.retainers.map((e) => (
                        <small key={e.entity_key} className="cell-note">
                          {str(e.payload, "Name")}：
                          {money(num(e.payload, "Gil"))}
                        </small>
                      ))}
                    </details>
                  )}
                </td>
                <td className="numeric">{money(wallet(c))}</td>
                <td className="numeric">
                  {money(retainerGil(c, assets.complete))}
                </td>
                <td className="numeric emphasis">
                  {money(personalGil(c, assets.complete))}
                </td>
                <td className="numeric">
                  {hasEntry(c.lottery, cycle) &&
                  c.lottery?.payload.ResultChecked !== true
                    ? money(num(c.lottery!.payload, "BidGilDeposited"))
                    : "—"}
                </td>
                <td>{dateLabel(c.wallet?.observed_at)}</td>
              </tr>
            ))}
          </Table>
        </Panel>
        <Panel title="FC別の資金・クレジット">
          <Table
            columns={[
              "フリーカンパニー",
              "FC資金",
              "カンパニークレジット",
              "資金の観測日時",
            ]}
            empty={!companies.length}
          >
            {companies.map((e) => (
              <tr key={e.entity_key}>
                <td>
                  <Person
                    name={str(e.payload, "Name")}
                    world={str(e.payload, "WorldName")}
                  />
                </td>
                <td className="numeric">
                  {e.payload.GilConfirmed === true
                    ? money(num(e.payload, "Gil"))
                    : "未確認"}
                </td>
                <td className="numeric">
                  {e.payload.CompanyCreditsConfirmed === true
                    ? count(num(e.payload, "CompanyCredits"))
                    : "未確認"}
                </td>
                <td>{dateLabel(e.payload.GilUpdatedAt ?? e.observed_at)}</td>
              </tr>
            ))}
          </Table>
        </Panel>
      </div>
    );
  }
  if (section === "housing") {
    const tabs = [
      ["lottery", "土地抽選"],
      ["estates", "ハウス保持期限"],
      ["plots", "空き土地"],
    ];
    const statusOrder = [
      "結果未確認",
      "今期未確認",
      "周期未確認",
      "応募記録なし",
      "応募済み",
      "結果確認済み",
    ];
    const rows = roster
      .filter((c) =>
        matches([c.name, c.world, c.lottery?.payload.PlotAddress], query),
      )
      .filter((c) => filter === "all" || lotteryStatus(c, cycle) === filter)
      .sort(
        (a, b) =>
          statusOrder.indexOf(lotteryStatus(a, cycle)) -
          statusOrder.indexOf(lotteryStatus(b, cycle)),
      );
    const estates = houses
      .filter((e) => matches(Object.values(e.payload), query))
      .sort(
        (a, b) =>
          (houseDeadline(a) ?? Infinity) - (houseDeadline(b) ?? Infinity),
      );
    const plots = entities
      .filter(
        (e) =>
          e.entity_type === "open_plot" &&
          matches(Object.values(e.payload), query),
      )
      .sort(
        (a, b) =>
          str(a.payload, "WorldName").localeCompare(
            str(b.payload, "WorldName"),
            "ja",
          ) ||
          (num(a.payload, "WardNumber") ?? 0) -
            (num(b.payload, "WardNumber") ?? 0) ||
          (num(a.payload, "PlotNumber") ?? 0) -
            (num(b.payload, "PlotNumber") ?? 0),
      );
    return (
      <div className="operations">
        <div className="op-tabs" role="tablist" aria-label="ハウジングの表示">
          {tabs.map(([id, label]) => (
            <button
              key={id}
              role="tab"
              aria-selected={tab === id}
              onClick={() => {
                setTab(id);
                setQuery("");
                setFilter("all");
              }}
            >
              {label}
            </button>
          ))}
        </div>
        {cycle && (
          <div className="cycle-bar">
            <Badge tone={cycle.phase === "entry" ? "good" : "warn"}>
              {cycle.phase === "entry" ? "応募期間" : "結果発表期間"}
            </Badge>
            <span>
              {cycle.phase === "entry" ? "応募締切" : "結果確認期限"}{" "}
              {dateLabel(
                new Date(
                  cycle.phase === "entry" ? cycle.entryEnd : cycle.end,
                ).toISOString(),
              )}
            </span>
            <strong>
              {remaining(
                cycle.phase === "entry" ? cycle.entryEnd : cycle.end,
                now,
              )}
            </strong>
            {cycle.inferred && <small>応募記録から算出</small>}
          </div>
        )}
        <Search query={query} setQuery={setQuery}>
          {tab === "lottery" && (
            <label>
              <span>抽選状態</span>
              <select
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
              >
                <option value="all">すべて</option>
                {statusOrder.map((status) => (
                  <option key={status}>{status}</option>
                ))}
              </select>
            </label>
          )}
        </Search>
        {tab === "lottery" && (
          <>
            <div className="count-line">
              {statusOrder
                .filter((status) => status !== "周期未確認" || !cycle)
                .map((status) => (
                  <span key={status}>
                    {status}{" "}
                    <b>
                      {
                        roster.filter((c) => lotteryStatus(c, cycle) === status)
                          .length
                      }
                    </b>
                  </span>
                ))}
            </div>
            <Panel title="キャラクター別の抽選状況">
              <Table
                columns={[
                  "キャラクター",
                  "今期の状態",
                  "応募先",
                  "応募期間終了",
                  "預かり金",
                  "最終記録",
                ]}
                empty={!rows.length}
              >
                {rows.map((c) => {
                  const status = lotteryStatus(c, cycle);
                  return (
                    <tr key={c.key}>
                      <td>
                        <Person name={c.name} world={c.world} />
                      </td>
                      <td>
                        <Badge
                          tone={
                            ["結果未確認", "今期未確認", "周期未確認"].includes(
                              status,
                            )
                              ? "warn"
                              : status === "応募済み" ||
                                  status === "結果確認済み"
                                ? "good"
                                : "neutral"
                          }
                        >
                          {status}
                        </Badge>
                      </td>
                      <td>
                        {hasEntry(c.lottery, cycle)
                          ? str(c.lottery!.payload, "PlotAddress", "住所未記録")
                          : "—"}
                      </td>
                      <td>{dateLabel(c.lottery?.payload.EntryPhaseEndsAt)}</td>
                      <td className="numeric">
                        {hasEntry(c.lottery, cycle) &&
                        c.lottery?.payload.ResultChecked !== true
                          ? money(num(c.lottery!.payload, "BidGilDeposited"))
                          : "—"}
                      </td>
                      <td>{dateLabel(c.lottery?.observed_at)}</td>
                    </tr>
                  );
                })}
              </Table>
            </Panel>
            <p className="op-note">
              「応募記録なし」は保存記録上の状態です。ゲーム内の応募状況と異なる場合は、ゲーム側で確認して再同期してください。
            </p>
          </>
        )}
        {tab === "estates" && (
          <>
            <Panel
              title="所有ハウスの保持期限"
              aside={<small>期限が近い順</small>}
            >
              <Table
                columns={[
                  "所有キャラクター",
                  "種別・住所",
                  "推定保持期限",
                  "残り時間",
                  "最終入室",
                  "記録日時",
                ]}
                empty={!estates.length}
              >
                {estates.map((e) => {
                  const deadline = houseDeadline(e);
                  return (
                    <tr key={e.entity_key}>
                      <td>
                        <Person
                          name={str(e.payload, "CharacterName")}
                          world={str(e.payload, "WorldName")}
                        />
                        {str(e.payload, "CoOwners", "") && (
                          <small className="cell-note">
                            記録者: {str(e.payload, "CoOwners")}
                          </small>
                        )}
                      </td>
                      <td>
                        <strong>
                          {e.payload.EstateKind === 0 ? "個人宅" : "FCハウス"}
                        </strong>
                        <small className="cell-note">
                          {district(num(e.payload, "HouseTerritoryTypeId"))}{" "}
                          {count(num(e.payload, "HouseWard"))}区{" "}
                          {count(num(e.payload, "HousePlot"))}番地
                        </small>
                        <small className="cell-note">
                          ハウスのワールドID{" "}
                          {count(num(e.payload, "HouseWorldId"))}
                        </small>
                      </td>
                      <td>
                        {deadline === null
                          ? "最終入室未記録"
                          : dateLabel(new Date(deadline).toISOString())}
                      </td>
                      <td>
                        <Badge
                          tone={
                            deadline === null
                              ? "neutral"
                              : deadline <= now
                                ? "bad"
                                : deadline - now <= 10 * DAY
                                  ? "warn"
                                  : "good"
                          }
                        >
                          {remaining(deadline, now)}
                        </Badge>
                      </td>
                      <td>{dateLabel(e.payload.LastEnteredAt)}</td>
                      <td>{dateLabel(e.observed_at)}</td>
                    </tr>
                  );
                })}
              </Table>
            </Panel>
            <p className="op-note">
              保持期限は記録された最終入室＋45日による推定です。自動撤去の停止期間や未同期の入室は反映されません。ゲーム内表示を優先してください。
            </p>
          </>
        )}
        {tab === "plots" && (
          <>
            <Panel
              title="観測済みの空き土地"
              aside={<small>{plots.length}件</small>}
            >
              <Table
                columns={[
                  "ワールド",
                  "地区・区・番地",
                  "サイズ",
                  "価格",
                  "記録の期間",
                  "観測日時",
                ]}
                empty={!plots.length}
              >
                {plots.map((e) => (
                  <tr key={e.entity_key}>
                    <td>{str(e.payload, "WorldName")}</td>
                    <td>
                      <strong>{str(e.payload, "DistrictName")}</strong>
                      <small className="cell-note">
                        {count(num(e.payload, "WardNumber"))}区{" "}
                        {count(num(e.payload, "PlotNumber"))}番地
                      </small>
                    </td>
                    <td>{str(e.payload, "Size")}</td>
                    <td className="numeric">
                      {money(num(e.payload, "Price"))}
                    </td>
                    <td>
                      <Badge
                        tone={
                          cycle &&
                          time(e.payload.CycleStartsAt) !== null &&
                          Math.abs(
                            time(e.payload.CycleStartsAt)! - cycle.start,
                          ) < 300000
                            ? "good"
                            : "warn"
                        }
                      >
                        {cycle &&
                        time(e.payload.CycleStartsAt) !== null &&
                        Math.abs(time(e.payload.CycleStartsAt)! - cycle.start) <
                          300000
                          ? "今期の記録"
                          : "前期・周期未確認"}
                      </Badge>
                    </td>
                    <td>{dateLabel(e.observed_at)}</td>
                  </tr>
                ))}
              </Table>
            </Panel>
            <p className="op-note">
              観測時点の空き情報です。成約などの変更は次のゲーム側の確認まで分かりません。
            </p>
            {missing}
          </>
        )}
      </div>
    );
  }
  if (section === "submarines") {
    const groups = new Map<
      string,
      { name: string; world: string; ships: Entity[]; known: boolean }
    >();
    for (const fc of mergeCompanies(entities))
      groups.set(fc.entity_key, {
        name: str(fc.payload, "Name"),
        world: str(fc.payload, "WorldName"),
        ships: [],
        known: entities.some(
          (e) =>
            e.entity_type === "submarine_fleet" &&
            e.entity_key === fc.entity_key,
        ),
      });
    for (const fleet of entities.filter(
      (e) => e.entity_type === "submarine_fleet",
    ))
      groups.set(fleet.entity_key, {
        name: str(fleet.payload, "Name"),
        world: str(fleet.payload, "WorldName"),
        ships: [],
        known: true,
      });
    for (const e of ships) {
      let key = str(e.payload, "FreeCompanyKey", "");
      if (!key)
        key =
          [...groups.entries()].find(
            ([, g]) =>
              g.name === str(e.payload, "FreeCompanyName") &&
              g.world === str(e.payload, "WorldName"),
          )?.[0] ??
          `${str(e.payload, "FreeCompanyName")}@${str(e.payload, "WorldName")}`;
      if (!groups.has(key))
        groups.set(key, {
          name: str(e.payload, "FreeCompanyName"),
          world: str(e.payload, "WorldName"),
          ships: [],
          known: true,
        });
      groups.get(key)!.ships.push(e);
    }
    const rows = [...groups.entries()]
      .filter(([, g]) =>
        matches(
          [g.name, g.world, ...g.ships.map((e) => str(e.payload, "Name"))],
          query,
        ),
      )
      .filter(
        ([, g]) =>
          filter === "all" ||
          g.ships.some((e) =>
            filter === "pending"
              ? ["未出航・待機", "帰港予定経過"].includes(
                  submarineStatus(e, now),
                )
              : submarineStatus(e, now) === "航海中",
          ),
      );
    return (
      <div className="operations">
        <Search query={query} setQuery={setQuery}>
          <label>
            <span>運航状態</span>
            <select value={filter} onChange={(e) => setFilter(e.target.value)}>
              <option value="all">すべてのFC</option>
              <option value="pending">未出航・帰港予定経過あり</option>
              <option value="voyaging">航海中あり</option>
            </select>
          </label>
        </Search>
        <div className="count-line">
          <span>
            未出航・待機{" "}
            <b>
              {
                ships.filter((e) => submarineStatus(e, now) === "未出航・待機")
                  .length
              }
            </b>
            隻
          </span>
          <span>
            帰港予定経過{" "}
            <b>
              {
                ships.filter((e) => submarineStatus(e, now) === "帰港予定経過")
                  .length
              }
            </b>
            隻
          </span>
          <span>
            航海中{" "}
            <b>
              {ships.filter((e) => submarineStatus(e, now) === "航海中").length}
            </b>
            隻
          </span>
        </div>
        {rows.length ? (
          rows.map(([key, g]) => (
            <Panel
              key={key}
              title={g.name}
              aside={
                <small>
                  {g.world} ·{" "}
                  {g.ships.length
                    ? `${g.ships.length}隻`
                    : g.known
                      ? "登録0隻"
                      : "情報未同期"}
                </small>
              }
            >
              <Table
                columns={[
                  "潜水艦",
                  "運航状態",
                  "帰港予定",
                  "残り時間",
                  "観測日時",
                ]}
                empty={!g.ships.length}
              >
                {g.ships
                  .sort(
                    (a, b) =>
                      (num(a.payload, "ReturnTimeUnix") ?? Infinity) -
                      (num(b.payload, "ReturnTimeUnix") ?? Infinity),
                  )
                  .map((e) => {
                    const status = submarineStatus(e, now);
                    const unix = num(e.payload, "ReturnTimeUnix");
                    return (
                      <tr key={e.entity_key}>
                        <td>
                          <strong>{str(e.payload, "Name")}</strong>
                        </td>
                        <td>
                          <Badge
                            tone={
                              status === "航海中"
                                ? "good"
                                : status === "未確認"
                                  ? "neutral"
                                  : "warn"
                            }
                          >
                            {status}
                          </Badge>
                        </td>
                        <td>{unix ? unixDateLabel(unix) : "—"}</td>
                        <td>
                          {status === "航海中"
                            ? remaining(unix! * 1000, now)
                            : "—"}
                        </td>
                        <td>{dateLabel(e.observed_at)}</td>
                      </tr>
                    );
                  })}
              </Table>
            </Panel>
          ))
        ) : (
          <Empty>該当するFC・潜水艦の記録がありません。</Empty>
        )}
        <p className="op-note">
          「帰港予定経過」は保存された予定時刻による判定です。回収・再出航を済ませていても、次の同期までは更新されません。
        </p>
      </div>
    );
  }
  const rank = { 未消化: 0, 今週未確認: 1, 記録なし: 2, 消化済み: 3 };
  const rows = roster
    .filter((c) => matches([c.name, c.world], query))
    .filter((c) => filter === "all" || deliveryStatus(c, now) === filter)
    .sort(
      (a, b) =>
        rank[deliveryStatus(a, now)] - rank[deliveryStatus(b, now)] ||
        (num(b.delivery?.payload ?? {}, "RemainingWeeklyAllowances") ?? -1) -
          (num(a.delivery?.payload ?? {}, "RemainingWeeklyAllowances") ?? -1),
    );
  return (
    <div className="operations">
      <div className="cycle-bar">
        <Badge>今週</Badge>
        <span>
          {dateLabel(new Date(weeklyReset(now)).toISOString())} 以降の記録
        </span>
        <strong>
          次のリセット {remaining(weeklyReset(now) + 7 * DAY, now)}
        </strong>
      </div>
      <Search query={query} setQuery={setQuery}>
        <label>
          <span>消化状況</span>
          <select value={filter} onChange={(e) => setFilter(e.target.value)}>
            <option value="all">すべて</option>
            {Object.keys(rank).map((status) => (
              <option key={status}>{status}</option>
            ))}
          </select>
        </label>
      </Search>
      <div className="count-line">
        {Object.keys(rank).map((status) => (
          <span key={status}>
            {status}{" "}
            <b>
              {roster.filter((c) => deliveryStatus(c, now) === status).length}
            </b>
            人
          </span>
        ))}
      </div>
      <Panel title="キャラクター別のお得意様取引">
        <Table
          columns={[
            "キャラクター",
            "今週の状態",
            "残り納品",
            "クラフタースクリップ",
            "ギャザラースクリップ",
            "観測日時",
          ]}
          empty={!rows.length}
        >
          {rows.map((c) => {
            const status = deliveryStatus(c, now);
            const p = c.delivery?.payload ?? {};
            return (
              <tr key={c.key}>
                <td>
                  <Person name={c.name} world={c.world} />
                </td>
                <td>
                  <Badge
                    tone={
                      status === "消化済み"
                        ? "good"
                        : status === "記録なし"
                          ? "neutral"
                          : "warn"
                    }
                  >
                    {status}
                  </Badge>
                </td>
                <td className="numeric">
                  {["今週未確認", "記録なし"].includes(status)
                    ? "未確認"
                    : `${count(num(p, "RemainingWeeklyAllowances"))}回`}
                </td>
                {["Crafter", "Gatherer"].map((kind) => (
                  <td className="numeric" key={kind}>
                    <span className="scrip-line purple-scrip">
                      紫貨 {count(num(p, `Purple${kind}Scrip`))}
                    </span>
                    <span className="scrip-line orange-scrip">
                      橙貨 {count(num(p, `Orange${kind}Scrip`))}
                    </span>
                  </td>
                ))}
                <td>{dateLabel(c.delivery?.observed_at)}</td>
              </tr>
            );
          })}
        </Table>
      </Panel>
      <p className="op-note">
        貨幣は最終観測時点の所持数です。週が変わった記録を「消化済み」のまま扱わず、「今週未確認」と表示します。
      </p>
    </div>
  );
}

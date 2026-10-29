import { useEffect, useState } from "react";
import { supabase } from "./supabase";
import { dateLabel } from "./data";
import type { Device } from "./data";

export default function SyncDevices({
  devices,
  onRefresh,
}: {
  devices: Device[];
  onRefresh: () => void;
}) {
  const [name, setName] = useState("Windows PC");
  const [pairing, setPairing] = useState<{
    code: string;
    expires_at: string;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!pairing) return;
    const timer = window.setInterval(() => {
      if (Date.now() >= Date.parse(pairing.expires_at)) {
        setPairing(null);
        setCopied(false);
      }
    }, 1000);
    return () => window.clearInterval(timer);
  }, [pairing]);
  async function create() {
    if (busy) return;
    setBusy(true);
    setError("");
    setPairing(null);
    setCopied(false);
    try {
      const { data, error: rpcError } = await supabase!.rpc(
        "create_sync_pairing",
        { p_name: name.trim() },
      );
      if (
        rpcError ||
        !data ||
        typeof data.code !== "string" ||
        !/^[a-f0-9]{64}$/.test(data.code) ||
        !Number.isFinite(Date.parse(data.expires_at))
      ) {
        setError(
          "連携コードを発行できませんでした。時間をおいて再試行してください。初回設定中の場合は管理者にお問い合わせください。",
        );
      } else setPairing({ code: data.code, expires_at: data.expires_at });
    } catch {
      setError("接続できませんでした。再試行してください。");
    } finally {
      setBusy(false);
    }
  }
  async function revoke(device: Device) {
    if (
      busy ||
      !window.confirm(
        `「${device.name}」の同期を無効にしますか？ 再び利用するにはPCを連携し直す必要があります。`,
      )
    )
      return;
    setBusy(true);
    setError("");
    try {
      const { error: rpcError } = await supabase!.rpc("revoke_sync_device", {
        p_device_id: device.id,
      });
      if (rpcError)
        setError("端末を無効にできませんでした。再試行してください。");
      else onRefresh();
    } catch {
      setError("接続できませんでした。再試行してください。");
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="records">
      <section className="record pairing-panel">
        <h3>PCとの連携</h3>
        <p>
          連携コードをPC側の AltMate Sync Agent
          に入力して登録します。コードは10分間有効で、1回だけ利用できます。
        </p>
        <label htmlFor="device-name">端末名</label>
        <input
          id="device-name"
          value={name}
          maxLength={80}
          onChange={(e) => setName(e.target.value)}
          disabled={busy}
        />
        <button onClick={create} disabled={busy || !name.trim()}>
          {busy ? "処理中…" : "連携コードを発行"}
        </button>
        {error && (
          <div className="notice error" role="alert">
            {error}
          </div>
        )}
        {pairing && (
          <div className="pairing-result">
            <label htmlFor="pairing-code">PC登録用コード</label>
            <input
              id="pairing-code"
              readOnly
              value={pairing.code}
              autoComplete="off"
              spellCheck={false}
            />
            <p>有効期限: {dateLabel(pairing.expires_at)}</p>
            <p>
              このコードは他の人に共有しないでください。PC側にパスワードや管理用キーを入力する必要はありません。
            </p>
            <button
              onClick={async () => {
                try {
                  await navigator.clipboard.writeText(pairing.code);
                  setCopied(true);
                } catch {
                  setError(
                    "コピーできませんでした。コードを選択してコピーしてください。",
                  );
                }
              }}
            >
              {copied ? "コピーしました" : "コードをコピー"}
            </button>
            <button onClick={() => setPairing(null)}>コードを隠す</button>
          </div>
        )}
      </section>
      {devices.length === 0 ? (
        <div className="empty">
          <h3>同期端末は未登録です</h3>
          <p>コードを使ってPCを連携すると、ここに表示されます。</p>
        </div>
      ) : (
        devices.map((device) => (
          <article className="record" key={device.id}>
            <div className="record-title">
              <h3>{device.name}</h3>
              <span>{device.revoked_at ? "無効" : "登録済み"}</span>
            </div>
            <p>最終通信: {dateLabel(device.last_seen_at)}</p>
            {!device.revoked_at && (
              <button disabled={busy} onClick={() => revoke(device)}>
                この端末を無効にする
              </button>
            )}
          </article>
        ))
      )}
    </div>
  );
}

import { useEffect, useRef, useState } from "react";
import { supabase } from "./supabase";
import { dateLabel } from "./data";
import type { Device } from "./data";

export const normalizePairingCode = (code: string) => code.replace(/[\s-]/g, "").toUpperCase();
export default function SyncDevices({ devices, onRefresh, initialCode = "", onCodeUsed }: {
  devices: Device[]; onRefresh: () => void; initialCode?: string; onCodeUsed?: () => void;
}) {
  const [name, setName] = useState("Windows PC");
  const [code, setCode] = useState(initialCode);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const attempted = useRef(false);
  useEffect(() => {
    if (initialCode && !attempted.current) {
      attempted.current = true;
      void approve(initialCode);
      onCodeUsed?.();
    }
  }, [initialCode]);
  async function approve(value = code) {
    if (busy) return;
    const normalized = normalizePairingCode(value);
    if (!/^[A-F0-9]{12}$/.test(normalized)) {
      setError("プラグインに表示された12文字の連携コードを入力してください。"); return;
    }
    setBusy(true); setError(""); setMessage("");
    try {
      const { data, error: rpcError } = await supabase!.rpc("approve_plugin_pairing", {
        p_code: normalized, p_name: name.trim(),
      });
      if (rpcError || data?.approved !== true) {
        setError(data?.error === "rate_limited"
          ? "試行回数の上限です。10分ほど待ってから再試行してください。"
          : data?.error === "device_limit" ? "連携数の上限です。不要な連携を解除してください。"
          : "連携できませんでした。コードの有効期限・入力内容とサーバーの更新を確認してください。");
      } else {
        setCode(""); setMessage("連携を承認しました。プラグインが確認すると同期を開始します。"); onRefresh();
      }
    } catch { setError("接続できませんでした。再試行してください。"); }
    finally { setBusy(false); }
  }
  async function revoke(device: Device) {
    if (busy || !window.confirm(`「${device.name}」の連携を解除しますか？ このPCのプラグインからの同期が停止します。`)) return;
    setBusy(true); setError("");
    try {
      const { error: rpcError } = await supabase!.rpc("revoke_sync_device", { p_device_id: device.id });
      if (rpcError) setError("連携を解除できませんでした。再試行してください。"); else onRefresh();
    } catch { setError("接続できませんでした。再試行してください。"); }
    finally { setBusy(false); }
  }
  return <div className="records">
    <section className="record pairing-panel">
      <h2>プラグイン連携</h2>
      <p>AltMateの設定で「Webと連携する」を押し、表示されたコードを登録してください。別アプリの起動は不要です。</p>
      <p>自分のプラグインで発行したコードだけを登録してください。承認すると、このアカウントへ記録を送信できるようになります。</p>
      <form onSubmit={event => { event.preventDefault(); void approve(); }}>
        <label htmlFor="device-name">PCの表示名</label>
        <input id="device-name" value={name} maxLength={80} required disabled={busy} onChange={e => setName(e.target.value)} />
        <label htmlFor="plugin-code">プラグインの連携コード</label>
        <input id="plugin-code" value={code} maxLength={20} required autoComplete="off" spellCheck={false} placeholder="ABCDEF-123456" disabled={busy} onChange={e => setCode(e.target.value)} />
        <button disabled={busy || !name.trim() || !/^[A-F0-9]{12}$/.test(normalizePairingCode(code))}>{busy ? "承認中…" : "このアカウントに連携する"}</button>
      </form>
      {error && <div className="notice error" role="alert">{error}</div>}
      {message && <div className="notice" role="status">{message}</div>}
    </section>
    <section className="record"><h3>連携済みのPC</h3>
      <p>既存の連携はそのまま使えます。PCの買い替え・紛失時はここで解除できます。</p>
      {devices.length === 0 ? <p>連携済みのPCはありません。承認後はプラグインの確認を待ち、「表示を更新」を押してください。</p> : devices.map(device => <article className="record" key={device.id}>
        <div className="record-title"><h3>{device.name}</h3><span>{device.revoked_at ? "解除済み" : "連携済み"}</span></div>
        <p>最終通信: {dateLabel(device.last_seen_at)}</p>
        {!device.revoked_at && <button disabled={busy} onClick={() => void revoke(device)}>このPCの連携を解除</button>}
      </article>)}
    </section>
  </div>;
}

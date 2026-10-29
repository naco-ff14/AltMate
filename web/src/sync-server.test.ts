import {
  beforeAll,
  afterAll,
  beforeEach,
  afterEach,
  describe,
  expect,
  it,
} from "vitest";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { PGlite } from "@electric-sql/pglite";
import { pgcrypto } from "@electric-sql/pglite/contrib/pgcrypto";
import {
  handleSync,
  validateEnvelope,
} from "../../supabase/functions/_shared/sync";

const a = "00000000-0000-4000-8000-000000000001";
const b = "00000000-0000-4000-8000-000000000002";
const db = new PGlite({ extensions: { pgcrypto } });
beforeAll(async () => {
  await db.exec(`create role anon; create role authenticated; create role service_role bypassrls;
    create schema auth; create table auth.users(id uuid primary key);
    create function auth.uid() returns uuid language sql stable as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
    grant usage on schema auth to public;`);
  for (const file of [
    "202610280001_web_dashboard.sql",
    "202610290001_authenticated_sync.sql",
    "202610290002_dashboard_views.sql",
    "202610290003_plugin_pairing.sql",
  ])
    await db.exec(
      readFileSync(
        new URL(`../../supabase/migrations/${file}`, import.meta.url),
        "utf8",
      ),
    );
}, 30000);
afterAll(async () => {
  await db.close();
});
beforeEach(async () => {
  await db.exec(`begin; insert into auth.users values ('${a}'),('${b}');`);
});
afterEach(async () => {
  await db.exec("rollback; reset role;");
});
async function as(role: string, owner = "") {
  await db.exec(`reset role; set role ${role};`);
  await db.query("select set_config('request.jwt.claim.sub', $1, false)", [
    owner,
  ]);
}
async function scalar<T>(sql: string, args: unknown[] = []): Promise<T> {
  const { rows } = await db.query<{ result: T }>(sql, args);
  return rows[0].result;
}
async function failure(action: () => Promise<unknown>, pattern: RegExp) {
  await db.exec("savepoint expected_failure");
  let caught: unknown;
  try {
    await action();
  } catch (error) {
    caught = error;
  }
  await db.exec(
    "rollback to savepoint expected_failure; release savepoint expected_failure",
  );
  expect(String(caught)).toMatch(pattern);
}
async function pair(owner = a) {
  await as("authenticated", owner);
  const result = await scalar<{ code: string }>(
    "select public.create_sync_pairing('Test PC') as result",
  );
  await as("service_role");
  const device = await scalar<{
    DeviceId: string;
    Token: string;
    HmacKey: string;
  }>("select public.claim_sync_pairing($1) as result", [result.code]);
  return { ...device, code: result.code };
}
function envelope(
  device: string,
  gil = 10,
  time = new Date(Date.now() - 60000).toISOString(),
) {
  return {
    SchemaVersion: 1,
    DeviceId: device,
    Revision: 1,
    EventId: randomUUID(),
    CapturedAt: new Date().toISOString(),
    Changes: [
      {
        EntityType: "character_gil",
        EntityKey: "a".repeat(64),
        ObservedAt: time,
        Data: { CharacterName: "Test", WorldName: "World", Gil: gil },
      },
    ],
  };
}
async function ingest(token: string, body: unknown) {
  await as("service_role");
  return scalar<{ applied: number; duplicate: boolean }>(
    "select public.ingest_sync($1,$2::jsonb) as result",
    [token, JSON.stringify(body)],
  );
}
async function allowNext(device: string) {
  await db.exec("reset role");
  await db.query(
    "update public.devices set last_upload_at = null where id = $1",
    [device],
  );
}

describe("PostgreSQL migrations: pairing, ownership and transactional ingestion", () => {
  it("accepts v2 snapshots, keeps metadata out of history, and adds fields without rolling back equal-time money", async () => {
    const device = await pair();
    const first = envelope(device.DeviceId, 100);
    await ingest(device.Token, first);
    await allowNext(device.DeviceId);
    const enriched = {
      ...first,
      EventId: randomUUID(),
      Changes: [
        {
          ...first.Changes[0],
          Data: {
            ...first.Changes[0].Data,
            Gil: 999,
            CharacterKey: "c".repeat(64),
          },
        },
      ],
    };
    expect((await ingest(device.Token, enriched)).applied).toBe(1);
    await db.exec("reset role");
    expect(
      await scalar(
        "select (payload->>'Gil')::int as result from public.entities where entity_type='character_gil'",
      ),
    ).toBe(100);
    expect(
      await scalar(
        "select (payload->>'Gil')::int as result from public.entity_history where entity_type='character_gil' order by id desc limit 1",
      ),
    ).toBe(100);
    await allowNext(device.DeviceId);
    const snapshot = {
      ...envelope(device.DeviceId),
      Changes: [
        {
          EntityType: "sync_metadata",
          EntityKey: "b".repeat(64),
          ObservedAt: new Date().toISOString(),
          Data: { DataVersion: 2, WalletKeys: ["a".repeat(64)] },
        },
      ],
    };
    expect((await ingest(device.Token, snapshot)).applied).toBe(1);
    await db.exec("reset role");
    expect(
      await scalar(
        "select count(*)::int as result from public.entity_history where entity_type='sync_metadata'",
      ),
    ).toBe(0);
    expect(validateEnvelope(snapshot)).toBe(true);
    expect(
      validateEnvelope({
        ...snapshot,
        Changes: [
          { ...snapshot.Changes[0], Data: { WalletKeys: ["raw-content-id"] } },
        ],
      }),
    ).toBe(false);
    await allowNext(device.DeviceId);
    const lottery = {
      ...envelope(device.DeviceId),
      Changes: [
        {
          EntityType: "housing_lottery",
          EntityKey: "e".repeat(64),
          ObservedAt: first.Changes[0].ObservedAt,
          Data: {
            CharacterName: "A",
            WorldName: "W",
            EntryPhaseEndsAt: null as string | null,
            ResultChecked: false,
          },
        },
      ],
    };
    await ingest(device.Token, lottery);
    await allowNext(device.DeviceId);
    const deadline = new Date(Date.now() + 3 * 86400000).toISOString();
    lottery.EventId = randomUUID();
    lottery.Changes[0].Data.EntryPhaseEndsAt = deadline;
    expect((await ingest(device.Token, lottery)).applied).toBe(1);
    await db.exec("reset role");
    expect(
      await scalar(
        "select payload->>'EntryPhaseEndsAt' as result from public.entities where entity_type='housing_lottery'",
      ),
    ).toBe(deadline);
  });
  it("accepts a valid device request through the endpoint and actual database RPC", async () => {
    const device = await pair();
    const request = new Request("https://example.test/sync", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${device.Token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(envelope(device.DeviceId)),
    });
    const response = await handleSync(request, async (name, args) => {
      expect(name).toBe("ingest_sync");
      const result = await ingest(args.p_token as string, args.p_envelope);
      return { data: result, error: null };
    });
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ applied: 1, duplicate: false });
    await as("authenticated", a);
    expect(
      await scalar("select count(*)::int as result from public.entities"),
    ).toBe(1);
  });
  it("isolates two owners and denies anonymous reads, client writes, token hashes and private secrets", async () => {
    const first = await pair(a);
    const second = await pair(b);
    await ingest(first.Token, envelope(first.DeviceId, 10));
    await ingest(second.Token, envelope(second.DeviceId, 99));
    await as("authenticated", a);
    expect(
      await scalar("select count(*)::int as result from public.entities"),
    ).toBe(1);
    expect(
      await scalar(
        "select (payload->>'Gil')::int as result from public.entities",
      ),
    ).toBe(10);
    expect(
      await scalar(
        "select count(*)::int as result from public.entities where user_id = $1",
        [b],
      ),
    ).toBe(0);
    expect(
      await scalar("select count(*)::int as result from public.devices"),
    ).toBe(1);
    await failure(
      () => db.exec("select token_hash from public.devices"),
      /permission denied/,
    );
    await failure(
      () => db.exec("select * from altmate_private.sync_keys"),
      /permission denied/,
    );
    await failure(
      () => db.exec("delete from public.entities"),
      /permission denied/,
    );
    await failure(
      () =>
        db.query("select public.ingest_sync($1, '{}'::jsonb)", [first.Token]),
      /permission denied/,
    );
    await failure(
      () => db.query("select public.claim_sync_pairing($1)", [first.code]),
      /permission denied/,
    );
    await as("anon");
    await failure(
      () => db.exec("select * from public.entities"),
      /permission denied/,
    );
    await failure(
      () => db.exec("select public.create_sync_pairing('no')"),
      /permission denied/,
    );
  });
  it("uses one-time, expiring, rate-limited pairing codes and owner-scoped HMAC keys", async () => {
    const first = await pair(a);
    await failure(
      () => db.query("select public.claim_sync_pairing($1)", [first.code]),
      /invalid_pairing/,
    );
    const second = await pair(a);
    const other = await pair(b);
    expect(second.HmacKey).toBe(first.HmacKey);
    expect(other.HmacKey).not.toBe(first.HmacKey);
    expect(second.Token).not.toBe(first.Token);
    await as("authenticated", a);
    const expiring = await scalar<{ code: string }>(
      "select public.create_sync_pairing('Expiring') as result",
    );
    await failure(
      () => db.exec("select public.create_sync_pairing('Fourth')"),
      /pairing_rate_limited/,
    );
    await db.exec(
      "reset role; update altmate_private.sync_pairings set expires_at = now() - interval '1 second' where consumed_at is null",
    );
    await as("service_role");
    await failure(
      () => db.query("select public.claim_sync_pairing($1)", [expiring.code]),
      /invalid_pairing/,
    );
  });
  it("acknowledges exact retries, rejects UUID reuse and rate limits distinct uploads", async () => {
    const device = await pair();
    const data = envelope(device.DeviceId);
    expect(await ingest(device.Token, data)).toEqual({
      applied: 1,
      duplicate: false,
    });
    expect(await ingest(device.Token, data)).toEqual({
      applied: 0,
      duplicate: true,
    });
    await failure(
      () => ingest(device.Token, { ...data, Revision: 2 }),
      /event_conflict/,
    );
    await failure(
      () => ingest(device.Token, envelope(device.DeviceId)),
      /sync_rate_limited/,
    );
    await db.exec("reset role");
    expect(
      await scalar("select count(*)::int as result from public.entity_history"),
    ).toBe(1);
    expect(
      await scalar("select count(*)::int as result from public.sync_events"),
    ).toBe(1);
  });
  it("rejects old observations and mismatched device IDs, and revocation only affects the owner", async () => {
    const device = await pair(a);
    const now = new Date(Date.now() - 60000).toISOString();
    await ingest(device.Token, envelope(device.DeviceId, 100, now));
    await allowNext(device.DeviceId);
    expect(
      (
        await ingest(
          device.Token,
          envelope(device.DeviceId, 0, "2020-01-01T00:00:00Z"),
        )
      ).applied,
    ).toBe(0);
    await allowNext(device.DeviceId);
    await failure(
      () => ingest(device.Token, envelope(randomUUID())),
      /invalid_envelope/,
    );
    await as("authenticated", b);
    await db.query("select public.revoke_sync_device($1)", [device.DeviceId]);
    await as("authenticated", a);
    await db.query("select public.revoke_sync_device($1)", [device.DeviceId]);
    await failure(
      () => ingest(device.Token, envelope(device.DeviceId)),
      /invalid_device/,
    );
  });
  it("rolls back every entity and the event if a later change fails validation", async () => {
    const device = await pair();
    const data = envelope(device.DeviceId);
    data.Changes.push({
      ...data.Changes[0],
      EntityKey: "b".repeat(64),
      ObservedAt: "2099-01-01T00:00:00Z",
    });
    await failure(() => ingest(device.Token, data), /invalid_observation/);
    await db.exec("reset role");
    expect(
      await scalar("select count(*)::int as result from public.entities"),
    ).toBe(0);
    expect(
      await scalar("select count(*)::int as result from public.sync_events"),
    ).toBe(0);
  });
});

describe("plugin-initiated pairing", () => {
  const secret = "d".repeat(64);
  const begin = () => scalar<{Code:string;ExpiresAt:string}>("select public.begin_plugin_pairing($1) as result",[secret]);
  const poll = () => scalar<{Pending?:boolean;DeviceId?:string;Token?:string;HmacKey?:string}>("select public.poll_plugin_pairing($1) as result",[secret]);
  const approve = (code:string) => scalar<{approved?:boolean;error?:string}>("select public.approve_plugin_pairing($1,'My PC') as result",[code]);
  it("requires browser authentication, keeps polling private, approves once and retries credentials safely",async()=>{
    await as("service_role"); const issued=await begin();
    expect(issued.Code).toMatch(/^[A-F0-9]{12}$/);
    expect(await begin()).toEqual(issued);
    expect(await poll()).toEqual({Pending:true});
    await as("anon"); await failure(()=>approve(issued.Code),/permission denied/);
    await as("authenticated",a);
    await failure(()=>poll(),/permission denied/);
    expect(await approve(issued.Code)).toEqual({approved:true});
    await as("authenticated",b); expect((await approve(issued.Code)).error).toBe("invalid_code");
    await as("service_role"); const paired=await poll();
    expect(paired.DeviceId).toBeTruthy(); expect(paired.Token).toMatch(/^[a-f0-9]{64}$/);
    expect(await poll()).toEqual(paired);
    expect(await scalar("select count(*)::int as result from public.devices")).toBe(1);
    await as("authenticated",b);
    expect(await scalar("select count(*)::int as result from public.devices")).toBe(0);
    await as("authenticated",a);
    await db.query("select public.revoke_sync_device($1)",[paired.DeviceId]);
    await as("service_role"); await failure(()=>poll(),/invalid_pairing/);
  });
  it("does not exchange the short display code for credentials or poll another secret",async()=>{
    await as("service_role"); const issued=await begin();
    await failure(()=>scalar("select public.poll_plugin_pairing($1) as result",[issued.Code]),/invalid_pairing/);
    await failure(()=>scalar("select public.poll_plugin_pairing($1) as result",["e".repeat(64)]),/invalid_pairing/);
  });
  it("expires approval and credential retrieval, and rate limits failed approvals",async()=>{
    await as("service_role"); const issued=await begin();
    await db.exec("reset role; update altmate_private.plugin_pairings set expires_at=now()-interval '1 second'");
    await as("authenticated",a); expect((await approve(issued.Code)).error).toBe("invalid_code");
    for(let i=1;i<20;i++) expect((await approve("000000000000")).error).toBe("invalid_code");
    expect((await approve(issued.Code)).error).toBe("rate_limited");
    await as("service_role"); await failure(()=>poll(),/invalid_pairing/);
  });
});

describe("device endpoint validation", () => {
  const request = (body: unknown, token = "a".repeat(64)) =>
    new Request("https://example.test/sync", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
    });
  it("routes plugin pairing actions using the private bearer secret only",async()=>{
    for(const action of ["begin_plugin_pairing","poll_plugin_pairing"]){
      const response=await handleSync(request({Action:action}),async(name,args)=>{
        expect(name).toBe(action); expect(args).toEqual({p_secret:"a".repeat(64)});
        return {data:{Pending:true},error:null};
      });
      expect(response.status).toBe(200);
    }
    expect((await handleSync(request({Action:"poll_plugin_pairing",UserId:a}),async()=>{throw Error("unexpected");})).status).toBe(400);
  });
  it("rejects private fields, raw keys, unknown types, malformed times and unbounded bodies", async () => {
    const valid = envelope(randomUUID());
    expect(validateEnvelope(valid)).toBe(true);
    for (const change of [
      {
        ...valid.Changes[0],
        Data: { ...valid.Changes[0].Data, ContentId: "123" },
      },
      { ...valid.Changes[0], EntityKey: "123" },
      { ...valid.Changes[0], EntityType: "__proto__" },
      { ...valid.Changes[0], ObservedAt: "2026-10-29" },
      { ...valid.Changes[0], Data: { LocalLinkKey: "secret" } },
    ])
      expect(validateEnvelope({ ...valid, Changes: [change] })).toBe(false);
    const rpc = async () => {
      throw new Error("must not reach server");
    };
    expect((await handleSync(request(valid, "wrong"), rpc)).status).toBe(401);
    expect(
      (await handleSync(request({ ...valid, UserId: a }), rpc)).status,
    ).toBe(400);
    expect(
      (await handleSync(request({ huge: "x".repeat(1048576) }), rpc)).status,
    ).toBe(413);
  });
  it("maps server rejection without leaking details and uses the token for server-side identity", async () => {
    const valid = envelope(randomUUID());
    expect(
      (
        await handleSync(request(valid), async (_name, args) => {
          expect(args.p_token).toBe("a".repeat(64));
          return { data: null, error: { message: "invalid_device" } };
        })
      ).status,
    ).toBe(401);
    const failure = await handleSync(request(valid), async () => ({
      data: null,
      error: { message: "secret internal detail" },
    }));
    expect(failure.status).toBe(503);
    expect(await failure.text()).not.toContain("secret");
    const limited = await handleSync(request(valid), async () => ({
      data: null,
      error: { message: "sync_rate_limited" },
    }));
    expect(limited.status).toBe(429);
    expect(limited.headers.get("Retry-After")).toBe("300");
  });
});

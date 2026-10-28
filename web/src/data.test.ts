import { describe, expect, it, vi } from "vitest";
import type { SupabaseClient } from "@supabase/supabase-js";
import {
  dateLabel,
  isStale,
  loadSnapshot,
  numberField,
  totalGil,
} from "./data";
import type { Entity } from "./data";
import { configError } from "./config";

const entity = (
  payload: Record<string, unknown>,
  type = "character_gil",
): Entity => ({
  entity_key: "key",
  entity_type: type,
  payload,
  observed_at: "2026-10-29T00:00:00Z",
});
describe("safe display of financial observations", () => {
  it("distinguishes absent values from confirmed zero and excludes FC balances", () => {
    expect(totalGil([])).toBeNull();
    expect(totalGil([entity({})])).toBeNull();
    expect(totalGil([entity({ Gil: 0 })])).toBe(0);
    expect(
      totalGil([entity({ Gil: 400 }), entity({ Gil: 999 }, "free_company")]),
    ).toBe(400);
    for (const Gil of ["100", -1, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1])
      expect(numberField({ Gil }, "Gil")).toBeNull();
  });
  it("flags stale/invalid observations and rejects sentinel dates", () => {
    const now = Date.parse("2026-10-29T00:00:00Z");
    expect(isStale("2026-10-27T23:59:59Z", now)).toBe(true);
    expect(isStale("2026-10-28T23:59:59Z", now)).toBe(false);
    expect(isStale("invalid", now)).toBe(true);
    expect(dateLabel("0001-01-01T00:00:00")).toBe("未記録");
    expect(dateLabel("invalid")).toBe("未記録");
  });
});
describe("public environment configuration", () => {
  const url = "https://project.supabase.co";
  const jwt = (role: string) =>
    `header.${btoa(JSON.stringify({ role }))}.signature`;
  it("accepts only a public key and a hosted HTTPS Supabase origin", () => {
    expect(configError(url, "sb_publishable_test")).toBeNull();
    expect(configError(url, jwt("anon"))).toBeNull();
    for (const key of [
      "sb_secret_private",
      jwt("service_role"),
      jwt("authenticated"),
      "invalid",
    ])
      expect(configError(url, key)).not.toBeNull();
    for (const value of [
      "",
      "http://project.supabase.co",
      "https://evil.example",
      `${url}/path`,
      `${url}?token=x`,
      "https://user:password@project.supabase.co",
    ])
      expect(configError(value, "sb_publishable_test")).not.toBeNull();
  });
});
describe("private snapshot queries", () => {
  it("paginates past the server row limit and requests only safe columns for the current user", async () => {
    const calls: {
      table: string;
      columns?: string;
      owner?: string;
      range?: number[];
    }[] = [];
    const client = {
      from(table: string) {
        const call: (typeof calls)[number] = { table };
        calls.push(call);
        const builder = {
          select(columns: string) {
            call.columns = columns;
            return builder;
          },
          eq(column: string, owner: string) {
            expect(column).toBe("user_id");
            call.owner = owner;
            return builder;
          },
          order() {
            return builder;
          },
          range(from: number, to: number) {
            call.range = [from, to];
            return builder;
          },
          abortSignal() {
            return Promise.resolve({
              data:
                table === "devices"
                  ? []
                  : Array.from(
                      { length: call.range?.[0] === 0 ? 500 : 1 },
                      () => entity({ Gil: 1 }),
                    ),
              error: null,
            });
          },
        };
        return builder;
      },
    } as unknown as SupabaseClient;
    const result = await loadSnapshot(
      client,
      "owner-a",
      new AbortController().signal,
    );
    expect(result.entities).toHaveLength(501);
    expect(calls.map((c) => c.owner)).toEqual([
      "owner-a",
      "owner-a",
      "owner-a",
    ]);
    expect(calls[1].range).toEqual([500, 999]);
    expect(calls[2].columns).toBe("id,name,last_seen_at,revoked_at");
    expect(JSON.stringify(calls)).not.toContain("token_hash");
  });
  it("reports permission/network failures rather than showing an empty success", async () => {
    const builder = {
      select: vi.fn(() => builder),
      eq: vi.fn(() => builder),
      order: vi.fn(() => builder),
      range: vi.fn(() => builder),
      abortSignal: vi.fn(async () => ({
        error: { message: "private internal detail" },
        data: null,
      })),
    };
    const client = { from: () => builder } as unknown as SupabaseClient;
    await expect(
      loadSnapshot(client, "owner", new AbortController().signal),
    ).rejects.toThrow("同期データを取得できませんでした");
  });
});

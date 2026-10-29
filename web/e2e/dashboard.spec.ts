import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";

const owner = "00000000-0000-4000-8000-000000000001";
const base64 = (value: object) =>
  Buffer.from(JSON.stringify(value)).toString("base64url");
const token = `${base64({ alg: "HS256", typ: "JWT" })}.${base64({ sub: owner, role: "authenticated", exp: Math.floor(Date.now() / 1000) + 3600 })}.test`;
const auth = {
  access_token: token,
  refresh_token: "test-refresh-token",
  token_type: "bearer",
  expires_in: 3600,
  user: {
    id: owner,
    aud: "authenticated",
    role: "authenticated",
    email: "test@example.com",
    app_metadata: {},
    user_metadata: {},
    created_at: "2026-10-29T00:00:00Z",
  },
};
const record = (type: string, payload: object, key = type) => ({
  entity_type: type,
  entity_key: key,
  payload,
  observed_at: "2026-10-29T00:00:00Z",
});

async function mockApi(page: Page, records: object[] = []) {
  await page.route("https://altmate-test.supabase.co/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/auth/v1/token") return route.fulfill({ json: auth });
    if (url.pathname === "/auth/v1/logout")
      return route.fulfill({ status: 204 });
    if (url.pathname === "/auth/v1/user")
      return route.fulfill({ json: auth.user });
    if (url.pathname.startsWith("/rest/v1/")) {
      expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
      expect(url.searchParams.get("user_id")).toBe(`eq.${owner}`);
      expect(url.searchParams.get("select")).not.toContain("token_hash");
      return route.fulfill({
        json: url.pathname.endsWith("/entities") ? records : [],
      });
    }
    return route.abort();
  });
}
async function login(page: Page) {
  await page.goto("/");
  await page.getByLabel("メールアドレス").fill("test@example.com");
  await page.getByLabel("パスワード", { exact: true }).fill("test-password");
  await page.getByRole("button", { name: "ログイン →", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "冒険のダッシュボード" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "↻ 表示を更新" }),
  ).toBeEnabled();
}

test("request recovery email without exposing account existence", async ({
  page,
}) => {
  await mockApi(page);
  await page.route("**/auth/v1/recover?**", async (route) => {
    expect(route.request().postDataJSON().email).toBe("test@example.com");
    expect(new URL(route.request().url()).searchParams.get("redirect_to")).toBe(
      "http://127.0.0.1:4175/?reset=password",
    );
    await route.fulfill({ json: {} });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "パスワードを忘れた方" }).click();
  await page.getByLabel("登録済みメールアドレス").fill("test@example.com");
  await page.getByRole("button", { name: "再設定メールを送信" }).click();
  await expect(page.getByRole("status")).toContainText(
    "登録されたアドレスであれば",
  );
});

test("verified recovery link permits password change and signs out", async ({
  page,
}) => {
  await mockApi(page);
  let changed = false;
  await page.route("**/auth/v1/user", async (route) => {
    if (route.request().method() === "PUT") {
      expect(route.request().postDataJSON().password).toBe("new-test-password");
      expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
      changed = true;
    }
    await route.fulfill({ json: auth.user });
  });
  await page.goto(
    `/#access_token=${token}&refresh_token=test-refresh-token&expires_in=3600&token_type=bearer&type=recovery`,
  );
  await expect(
    page.getByRole("heading", { name: "新しいパスワードを設定" }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "冒険のダッシュボード" }),
  ).toHaveCount(0);
  await page
    .getByLabel("新しいパスワード", { exact: true })
    .fill("new-test-password");
  await page
    .getByLabel("新しいパスワード（確認）", { exact: true })
    .fill("mismatched-password");
  await page
    .getByRole("button", { name: "パスワードを変更", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("一致しません");
  expect(changed).toBe(false);
  await page
    .getByLabel("新しいパスワード（確認）", { exact: true })
    .fill("new-test-password");
  await page
    .getByRole("button", { name: "パスワードを変更", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "パスワードを変更しました" }),
  ).toBeVisible();
  expect(changed).toBe(true);
  expect(new URL(page.url()).hash).toBe("");
  await page.getByRole("button", { name: "ログイン画面へ" }).click();
  await expect(
    page.getByRole("heading", { name: "マイダッシュボード" }),
  ).toBeVisible();
});

test("expired recovery link offers a new email instead of a password update", async ({
  page,
}) => {
  await mockApi(page);
  await page.goto(
    "/?reset=password#error=access_denied&error_code=otp_expired&error_description=Expired",
  );
  await expect(page.getByLabel("登録済みメールアドレス")).toBeVisible();
  await expect(
    page.getByLabel("新しいパスワード", { exact: true }),
  ).toHaveCount(0);
});

test("private empty dashboard, navigation, reload and logout", async ({
  page,
}, info) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockApi(page);
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "マイダッシュボード" }),
  ).toBeVisible();
  await page.screenshot({
    path: `test-results/login-${info.project.name}.png`,
    fullPage: true,
  });
  await login(page);
  await expect(
    page.getByText("初回同期を待っています。", { exact: false }),
  ).toBeVisible();
  await page.screenshot({
    path: `test-results/dashboard-${info.project.name}.png`,
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "冒険のダッシュボード" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "同期端末" }).click();
  await expect(
    page.getByRole("heading", { name: "同期端末は未登録です" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "ログアウト ↗" }).click();
  await expect(
    page.getByRole("heading", { name: "マイダッシュボード" }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "冒険のダッシュボード" }),
  ).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("renders observations, preserves unknown credit balances, filters and handles refresh errors", async ({
  page,
}, info) => {
  await mockApi(page, [
    record("character_gil", {
      CharacterName: "Test Adventurer",
      WorldName: "Test World",
      Gil: 0,
    }),
    record("free_company", {
      Name: "Test FC",
      WorldName: "Test World",
      CompanyCredits: 9999,
      CompanyCreditsConfirmed: false,
    }),
    record("housing_demolition", {
      CharacterName: "Test Adventurer",
      IsOwned: true,
      EstateKind: 0,
      HouseWard: 4,
      HousePlot: 12,
      LastEnteredAt: "2026-10-28T00:00:00Z",
    }),
    record("submarine", {
      Name: "Test Submarine",
      FreeCompanyName: "Test FC",
      ReturnTimeUnix: 1793318400,
    }),
  ]);
  await login(page);
  await expect(page.getByText("0 G", { exact: true })).toBeVisible();
  await expect(
    page.getByText("カンパニークレジット: 未確認", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Test Submarine", { exact: true })).toBeVisible();
  await page.getByLabel("記録を検索").fill("no-match");
  await expect(page.getByText("Test Adventurer", { exact: true })).toHaveCount(
    0,
  );
  await page.getByLabel("記録を検索").fill("");
  await expect(page.getByText("Test Adventurer", { exact: true })).toHaveCount(
    2,
  );
  await page.screenshot({
    path: `test-results/observations-${info.project.name}.png`,
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.route("**/rest/v1/entities?**", (route) =>
    route.fulfill({ status: 403, json: { message: "Forbidden" } }),
  );
  await page.getByRole("button", { name: "↻ 表示を更新" }).click();
  await expect(page.getByRole("alert")).toContainText(
    "同期データを取得できませんでした",
  );
  await expect(page.getByRole("alert")).toContainText("前回取得した記録");
});

test("failed login remains private with a generic error", async ({ page }) => {
  await mockApi(page);
  await page.route("**/auth/v1/token?**", (route) =>
    route.fulfill({
      status: 400,
      json: {
        error_code: "invalid_credentials",
        msg: "Invalid login credentials",
      },
    }),
  );
  await page.goto("/");
  await page.getByLabel("メールアドレス").fill("test@example.com");
  await page.getByLabel("パスワード", { exact: true }).fill("wrong-password");
  await page.getByRole("button", { name: "ログイン →" }).click();
  await expect(page.getByRole("alert")).toContainText(
    "ログインできませんでした",
  );
  await expect(
    page.getByRole("heading", { name: "冒険のダッシュボード" }),
  ).toHaveCount(0);
});

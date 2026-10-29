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

test("pairing code and device revocation are available only in the authenticated workspace", async ({
  page,
}) => {
  await mockApi(page);
  await page.route("**/rest/v1/rpc/create_sync_pairing", async (route) => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
    expect(route.request().postDataJSON()).toEqual({ p_name: "Windows PC" });
    await route.fulfill({
      json: {
        code: "a".repeat(64),
        expires_at: new Date(Date.now() + 600000).toISOString(),
      },
    });
  });
  await login(page);
  await page.getByRole("button", { name: "同期端末" }).click();
  await page.getByRole("button", { name: "連携コードを発行" }).click();
  await expect(page.getByLabel("PC登録用コード")).toHaveValue("a".repeat(64));
  await page.getByRole("button", { name: "コードを隠す" }).click();
  await expect(page.getByLabel("PC登録用コード")).toHaveCount(0);
  await page.route("**/rest/v1/devices?**", (route) =>
    route.fulfill({
      json: [
        { id: owner, name: "Test PC", revoked_at: null, last_seen_at: null },
      ],
    }),
  );
  await page.getByRole("button", { name: "↻ 表示を更新" }).click();
  let revoked = false;
  await page.route("**/rest/v1/rpc/revoke_sync_device", async (route) => {
    expect(route.request().postDataJSON()).toEqual({ p_device_id: owner });
    revoked = true;
    await route.fulfill({ status: 204 });
  });
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "この端末を無効にする" }).click();
  await expect.poll(() => revoked).toBe(true);
});

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
  await page.getByRole("button", { name: "ギル・FC", exact: true }).click();
  await expect(page.getByRole("row", { name: /Test FC/ })).toContainText(
    "未確認",
  );
  await expect(
    page.getByRole("row", { name: /Test Adventurer/ }),
  ).toContainText("0 G");
  await page.getByLabel("記録を検索").fill("no-match");
  await expect(page.getByText("Test Adventurer", { exact: true })).toHaveCount(
    0,
  );
  await page.getByLabel("記録を検索").fill("");
  await expect(page.getByText("Test Adventurer", { exact: true })).toHaveCount(
    1,
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
  await page.getByRole("button", { name: "潜水艦", exact: true }).click();
  await expect(page.getByText("Test Submarine", { exact: true })).toBeVisible();
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

test("operations show total assets, sortable personal balances, housing, FC readiness and weekly unknowns", async ({
  page,
}, info) => {
  const now = Date.now();
  const day = 86400000;
  const a = "1".repeat(64),
    b = "2".repeat(64),
    c = "3".repeat(64),
    r = "4".repeat(64),
    fc = "5".repeat(64),
    plot = "6".repeat(64),
    s1 = "7".repeat(64),
    s2 = "8".repeat(64);
  const sample = (type: string, key: string, payload: object, stamp = now) => ({
    ...record(type, payload, key),
    observed_at: new Date(stamp).toISOString(),
  });
  const person = (name: string) => ({
    CharacterName: name,
    WorldName: "Test World",
  });
  await mockApi(page, [
    sample("sync_metadata", "metadata", {
      DataVersion: 2,
      CycleAnchorUtc: new Date(now - 6 * day).toISOString(),
      CharacterKeys: [a, b, c],
      WalletKeys: [a, b],
      RetainerKeys: [r],
      CompanyKeys: [fc],
      OpenPlotKeys: [plot],
    }),
    sample("character_profile", a, person("Alpha")),
    sample("character_profile", b, person("Beta")),
    sample("character_profile", c, person("Gamma")),
    sample("character_gil", a, { ...person("Alpha"), Gil: 100 }),
    sample("character_gil", b, { ...person("Beta"), Gil: 400 }),
    sample("retainer_gil", r, {
      ...person("Alpha"),
      Name: "Retainer",
      CharacterKey: a,
      Gil: 200,
    }),
    sample("free_company", fc, { Name: "Fleet FC", WorldName: "Test World" }),
    sample("free_company_gil", fc, { Gil: 600, GilConfirmed: true }),
    sample("housing_lottery", "lot1", {
      ...person("Alpha"),
      CharacterKey: a,
      PlotAddress: "ミスト 2区 3番地",
      EntryPhaseEndsAt: new Date(now - day).toISOString(),
      ResultChecked: false,
      BidGilDeposited: 50,
    }),
    sample("housing_lottery", "lot2", {
      ...person("Beta"),
      CharacterKey: b,
      EntryPhaseEndsAt: null,
    }),
    sample("housing_demolition", "house", {
      ...person("Alpha"),
      IsOwned: true,
      EstateKind: 0,
      HouseTerritoryTypeId: 339,
      HouseWard: 2,
      HousePlot: 3,
      LastEnteredAt: new Date(now - 44 * day).toISOString(),
    }),
    sample("open_plot", plot, {
      WorldName: "Test World",
      DistrictName: "シロガネ",
      WardNumber: 4,
      PlotNumber: 5,
      Size: "M",
      Price: 20000000,
      CycleStartsAt: new Date(now - 6 * day).toISOString(),
    }),
    sample("submarine_fleet", fc, {
      Name: "Fleet FC",
      WorldName: "Test World",
      SubmarineKeys: [s1, s2],
    }),
    sample("submarine", s1, {
      Name: "Waiting Sub",
      FreeCompanyKey: fc,
      FreeCompanyName: "Fleet FC",
      WorldName: "Test World",
      ReturnTimeUnix: 0,
    }),
    sample("submarine", s2, {
      Name: "Returned Sub",
      FreeCompanyKey: fc,
      FreeCompanyName: "Fleet FC",
      WorldName: "Test World",
      ReturnTimeUnix: Math.floor(now / 1000) - 60,
    }),
    sample("custom_delivery", "delivery1", {
      ...person("Alpha"),
      CharacterKey: a,
      RemainingWeeklyAllowances: 3,
      PurpleCrafterScrip: 123,
      OrangeCrafterScrip: 456,
      PurpleGathererScrip: 789,
      OrangeGathererScrip: 20,
    }),
    sample(
      "custom_delivery",
      "delivery2",
      { ...person("Beta"), CharacterKey: b, RemainingWeeklyAllowances: 0 },
      now - 8 * day,
    ),
  ]);
  await login(page);
  await expect(page.getByText("1,350 G", { exact: true })).toBeVisible();
  await expect(page.getByText("Alpha", { exact: true })).toHaveCount(0);
  await page.screenshot({
    path: `test-results/overview-v2-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "ギル・FC", exact: true }).click();
  const balances = page
    .locator("section.op-panel")
    .filter({
      has: page.getByRole("heading", { name: "キャラクター別の資産" }),
    });
  await expect(balances.getByRole("row").nth(1)).toContainText("Beta");
  await page.getByLabel("並び順").selectOption("asc");
  await expect(balances.getByRole("row").nth(1)).toContainText("Alpha");
  await expect(page.getByRole("row", { name: /Alpha/ })).toContainText("300 G");
  await page.screenshot({
    path: `test-results/gil-v2-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "ハウジング", exact: true }).click();
  await expect(page.getByRole("row", { name: /Alpha/ })).toContainText(
    "結果未確認",
  );
  await expect(page.getByRole("row", { name: /Beta/ })).toContainText(
    "応募記録なし",
  );
  await expect(page.getByRole("row", { name: /Gamma/ })).toContainText(
    "今期未確認",
  );
  await page.getByRole("tab", { name: "ハウス保持期限" }).click();
  await expect(page.getByRole("row", { name: /Alpha/ })).toContainText(
    "個人宅",
  );
  await page.getByRole("tab", { name: "空き土地" }).click();
  await expect(page.getByRole("row", { name: /シロガネ/ })).toContainText(
    "4区 5番地",
  );
  await page.screenshot({
    path: `test-results/plots-v2-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "潜水艦", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Fleet FC" })).toBeVisible();
  await expect(page.getByRole("row", { name: /Waiting Sub/ })).toContainText(
    "未出航・待機",
  );
  await expect(page.getByRole("row", { name: /Returned Sub/ })).toContainText(
    "帰港予定経過",
  );
  await page.screenshot({
    path: `test-results/submarines-v2-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "お得意様取引", exact: true }).click();
  await expect(page.getByRole("row", { name: /Alpha/ })).toContainText(
    "未消化",
  );
  await expect(page.getByRole("row", { name: /Alpha/ })).toContainText("123");
  await expect(page.getByRole("row", { name: /Beta/ })).toContainText(
    "今週未確認",
  );
  await expect(page.getByRole("row", { name: /Gamma/ })).toContainText(
    "記録なし",
  );
  await page.getByLabel("消化状況").selectOption("未消化");
  await expect(page.getByRole("row", { name: /Beta/ })).toHaveCount(0);
  await page.screenshot({
    path: `test-results/delivery-v2-${info.project.name}.png`,
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test("return countdown progresses without requesting another cloud snapshot", async ({
  page,
}) => {
  await page.clock.install();
  const now = Date.now();
  let reads = 0;
  await mockApi(page, [
    {
      ...record("submarine", {
        Name: "Timed Sub",
        FreeCompanyName: "Timed FC",
        WorldName: "World",
        ReturnTimeUnix: Math.floor(now / 1000) + 30,
      }),
      observed_at: new Date(now).toISOString(),
    },
  ]);
  page.on("request", (request) => {
    if (request.url().includes("/rest/v1/entities?")) reads++;
  });
  await login(page);
  await page.getByRole("button", { name: "潜水艦", exact: true }).click();
  await expect(page.getByRole("row", { name: /Timed Sub/ })).toContainText(
    "航海中",
  );
  const before = reads;
  await page.clock.fastForward(60000);
  await expect(page.getByRole("row", { name: /Timed Sub/ })).toContainText(
    "帰港予定経過",
  );
  expect(reads).toBe(before);
});

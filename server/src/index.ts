// Marathon Sleaze Detector: the shared ratings server (a Cloudflare Worker with a D1 database).
//
//   App API (JSON)
//     GET    /auth/start?port=N        sign in with Steam; the token goes back to the app at 127.0.0.1:N
//     GET    /v1/reputation?keys=a,b   totals for runners (anyone)
//     GET    /v1/changes?since=T       totals changed since unix time T (anyone; used after the daily snapshot)
//     GET    /v1/me                    who the token belongs to
//     PUT    /v1/reports/:key          rate a runner (signed in)
//     DELETE /v1/reports/:key          take a rating back (signed in)
//     POST   /v1/signout
//   Admin: /admin (a page; sign in with Steam, admins only) and /admin/api/*.
//   Daily: totals (never who rated whom, never private comments) are published to a GitHub repository.

import { loginUrl, persona, verify } from "./steam";
import { ReportRow, total, Totals, Weights } from "./scoring";
import { ADMIN_PAGE } from "./admin";

export interface Env {
  DB: D1Database;
  WRITE_LIMIT?: RateLimit;
  READ_LIMIT?: RateLimit;
  // Settings (wrangler.toml [vars]).
  WEIGHT_TRUSTED: string;
  WEIGHT_ADMIN: string;
  SEVERE_BONUS: string;
  ROOK_WEIGHT: string;
  MAX_PER_HOUR: string;
  MAX_PER_DAY: string;
  BAD_FLAGS: string;          // rating categories, comma separated (see wrangler.toml)
  GOOD_FLAGS: string;
  SEVERE_FLAGS: string;
  SNAPSHOT_REPO?: string;     // "owner/repo" the daily totals go to
  SNAPSHOT_PATH?: string;     // file in that repository
  // Secrets (wrangler secret put ...).
  ADMIN_STEAM_ID?: string;    // the first admin; can never be demoted or banned
  STEAM_API_KEY?: string;     // optional: Steam display names in the admin panel
  GITHUB_TOKEN?: string;      // write access to SNAPSHOT_REPO only
  CF_ACCOUNT_ID?: string;     // optional, with CF_API_TOKEN: daily usage against the plan's limits in the admin panel
  CF_API_TOKEN?: string;      // a token with only "Account Analytics: Read"
}

interface RateLimit { limit(o: { key: string }): Promise<{ success: boolean }> }

const KEY = /^[A-Z0-9*]{1,40}(#[0-9]{4})?$/;
const APP_SESSION_DAYS = 180, ADMIN_SESSION_HOURS = 12;
const now = () => Math.floor(Date.now() / 1000);

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    try {
      return await route(req, env);
    } catch (e) {
      console.error(e);
      return json({ error: "server error" }, 500);
    }
  },

  async scheduled(_c: ScheduledController, env: Env): Promise<void> {
    await publishSnapshot(env);
    await env.DB.prepare("DELETE FROM logins WHERE expires_at < ?").bind(now()).run();
    await env.DB.prepare("DELETE FROM sessions WHERE expires_at < ?").bind(now()).run();
  },
};

async function route(req: Request, env: Env): Promise<Response> {
  const url = new URL(req.url);
  const p = url.pathname;
  const ip = req.headers.get("CF-Connecting-IP") ?? "?";
  const write = req.method !== "GET";
  // Per-address limits first: a flood stops here, before it costs any database work.
  const limiter = write ? env.WRITE_LIMIT : env.READ_LIMIT;
  if (limiter && !(await limiter.limit({ key: ip })).success) return json({ error: "too many requests" }, 429);

  if (p === "/auth/start" && req.method === "GET") return authStart(url, env, "app");
  if (p === "/auth/callback" && req.method === "GET") return authCallback(url, env);
  if (p === "/v1/reputation" && req.method === "GET") return reputation(url, env);
  if (p === "/v1/changes" && req.method === "GET") return changes(url, env);
  if (p === "/v1/me" && req.method === "GET") return me(req, env);
  if (p === "/v1/signout" && req.method === "POST") return signout(req, env);
  const rep = /^\/v1\/reports\/([^/]+)$/.exec(p);
  if (rep && req.method === "PUT") return putReport(req, env, decodeURIComponent(rep[1]));
  if (rep && req.method === "DELETE") return deleteReport(req, env, decodeURIComponent(rep[1]));
  if (p === "/admin" || p === "/admin/") return new Response(ADMIN_PAGE, { headers: { "Content-Type": "text/html; charset=utf-8" } });
  if (p === "/admin/login") return authStart(url, env, "admin");
  if (p.startsWith("/admin/api/")) return adminApi(req, env, url);
  if (p === "/") return new Response("Marathon Sleaze Detector ratings server. Source: https://github.com/Reginald-Gillespie/MarathonSleazeDetector\n");
  return json({ error: "not found" }, 404);
}

// ---------- helpers ----------

function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json" } });
}

function randomToken(): string {
  const b = new Uint8Array(32);
  crypto.getRandomValues(b);
  return btoa(String.fromCharCode(...b)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function sha256(s: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map(x => x.toString(16).padStart(2, "0")).join("");
}

/** The owner account (ADMIN_STEAM_ID): always admin. Whitespace around the stored value is ignored. */
const isOwner = (env: Env, steamId: string) => !!env.ADMIN_STEAM_ID && env.ADMIN_STEAM_ID.trim() === steamId;

const list = (s: string | undefined) => (s ?? "").split(",").map(x => x.trim()).filter(Boolean);
const allFlags = (env: Env) => [...list(env.BAD_FLAGS), ...list(env.GOOD_FLAGS)];

function weights(env: Env): Weights {
  return {
    user: 1,
    trusted: Number(env.WEIGHT_TRUSTED) || 3,
    admin: Number(env.WEIGHT_ADMIN) || 5,
    severeBonus: Number(env.SEVERE_BONUS) || 0.5,
    severeFlags: list(env.SEVERE_FLAGS),
    rookOnRook: Number(env.ROOK_WEIGHT) || 2,
  };
}

interface User { id: number; steam_id: string; persona: string | null; role: string; banned: number }

/** The signed-in user from "Authorization: Bearer <token>" (app) or the admin cookie. */
async function sessionUser(req: Request, env: Env, kind: "app" | "admin"): Promise<User | null> {
  let token: string | undefined;
  if (kind === "app") token = req.headers.get("Authorization")?.replace(/^Bearer\s+/i, "");
  else token = /(?:^|;\s*)sd_admin=([^;]+)/.exec(req.headers.get("Cookie") ?? "")?.[1];
  if (!token) return null;
  const row = await env.DB.prepare(
    `SELECT u.id, u.steam_id, u.persona, u.role, u.banned FROM sessions s JOIN users u ON u.id = s.user_id
     WHERE s.token_hash = ? AND s.kind = ? AND s.expires_at > ?`).bind(await sha256(token), kind, now()).first<User>();
  return row ?? null;
}

// ---------- sign-in ----------

async function authStart(url: URL, env: Env, kind: "app" | "admin"): Promise<Response> {
  let ret = "admin";
  if (kind === "app") {
    // The app listens on a local port for the answer; nothing else may receive a token.
    const port = Number(url.searchParams.get("port"));
    if (!Number.isInteger(port) || port < 1024 || port > 65535) return json({ error: "bad port" }, 400);
    ret = `http://127.0.0.1:${port}/signed-in`;
  }
  const state = randomToken();
  await env.DB.prepare("INSERT INTO logins (state, ret, expires_at) VALUES (?, ?, ?)").bind(state, ret, now() + 600).run();
  const returnTo = `${url.origin}/auth/callback?state=${state}`;
  return Response.redirect(loginUrl(returnTo, url.origin), 302);
}

async function authCallback(url: URL, env: Env): Promise<Response> {
  const state = url.searchParams.get("state") ?? "";
  const login = await env.DB.prepare("DELETE FROM logins WHERE state = ? AND expires_at > ? RETURNING ret")
    .bind(state, now()).first<{ ret: string }>();
  if (!login) return page("Sign-in expired", "Start the sign-in again from the app.");
  const steamId = await verify(url, `${url.origin}/auth/callback?state=${state}`);
  if (!steamId) return page("Sign-in failed", "Steam did not confirm the sign-in. Try again.");

  const name = await persona(steamId, env.STEAM_API_KEY);
  const t = now();
  const user = await env.DB.prepare(
    `INSERT INTO users (steam_id, persona, created_at, last_seen) VALUES (?, ?, ?, ?)
     ON CONFLICT(steam_id) DO UPDATE SET last_seen = excluded.last_seen, persona = COALESCE(excluded.persona, users.persona)
     RETURNING id, steam_id, persona, role, banned`).bind(steamId, name, t, t).first<User>();
  if (!user) return page("Sign-in failed", "Try again.");
  if (isOwner(env, steamId) && user.role !== "admin") {
    await env.DB.prepare("UPDATE users SET role = 'admin', banned = 0 WHERE id = ?").bind(user.id).run();
    user.role = "admin";
  }

  const token = randomToken();
  if (login.ret === "admin") {
    if (user.role !== "admin") return page("Not an admin", `Signed in as Steam account ${steamId}, which is not an admin.`);
    await env.DB.prepare("INSERT INTO sessions (token_hash, user_id, kind, created_at, expires_at) VALUES (?, ?, 'admin', ?, ?)")
      .bind(await sha256(token), user.id, t, t + ADMIN_SESSION_HOURS * 3600).run();
    return new Response(null, {
      status: 302,
      headers: {
        Location: "/admin",
        "Set-Cookie": `sd_admin=${token}; Path=/admin; HttpOnly; Secure; SameSite=Strict; Max-Age=${ADMIN_SESSION_HOURS * 3600}`,
      },
    });
  }
  await env.DB.prepare("INSERT INTO sessions (token_hash, user_id, kind, created_at, expires_at) VALUES (?, ?, 'app', ?, ?)")
    .bind(await sha256(token), user.id, t, t + APP_SESSION_DAYS * 86400).run();
  return Response.redirect(`${login.ret}?token=${encodeURIComponent(token)}&steamId=${steamId}`, 302);
}

function page(title: string, text: string): Response {
  const esc = (s: string) => s.replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]!));
  return new Response(
    `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>${esc(title)}</title>
     <body style="font:16px system-ui;margin:40px auto;max-width:520px;padding:0 16px"><h1 style="font-size:20px">${esc(title)}</h1><p>${esc(text)}</p>`,
    { headers: { "Content-Type": "text/html; charset=utf-8" } });
}

async function me(req: Request, env: Env): Promise<Response> {
  const u = await sessionUser(req, env, "app");
  if (!u) return json({ error: "not signed in" }, 401);
  // The app asks this once at every start: it marks the account as active (stored at most once a day per account).
  const t = now();
  await env.DB.prepare("UPDATE users SET last_seen = ? WHERE id = ? AND last_seen < ?").bind(t, u.id, t - 86400).run();
  return json({ steamId: u.steam_id, role: u.role, banned: !!u.banned, weight: roleWeight(env, u) });
}

async function signout(req: Request, env: Env): Promise<Response> {
  const token = req.headers.get("Authorization")?.replace(/^Bearer\s+/i, "");
  if (token) await env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?").bind(await sha256(token)).run();
  return json({ ok: true });
}

// ---------- reading ----------

const SCORE_COLUMNS = `target_key AS key, target_name AS name, good, bad, toxic, backstab,
  rook_on_rook AS rookOnRook, dc_early AS dcEarly, w_good AS weightedGood, w_bad AS weightedBad,
  w_rook_bad AS weightedRookBad, w_rook_good AS weightedRookGood, updated_at AS updatedAt`;

async function reputation(url: URL, env: Env): Promise<Response> {
  const keys = [...new Set((url.searchParams.get("keys") ?? "").split(",").map(k => k.trim()).filter(k => KEY.test(k)))].slice(0, 100);
  if (keys.length === 0) return json([]);
  const { results } = await env.DB.prepare(`SELECT ${SCORE_COLUMNS} FROM scores WHERE target_key IN (${keys.map(() => "?").join(",")})`)
    .bind(...keys).all();
  return json(results);
}

async function changes(url: URL, env: Env): Promise<Response> {
  const since = Math.max(0, Number(url.searchParams.get("since")) || 0);
  const { results } = await env.DB.prepare(`SELECT ${SCORE_COLUMNS} FROM scores WHERE updated_at > ? ORDER BY updated_at LIMIT 5000`)
    .bind(since).all();
  return json({ now: now(), scores: results });
}

// ---------- writing ----------

interface ReportBody { targetName?: string; verdict?: string; flags?: string[]; comment?: string; context?: string; runId?: string }

async function putReport(req: Request, env: Env, key: string): Promise<Response> {
  const u = await sessionUser(req, env, "app");
  if (!u) return json({ error: "not signed in" }, 401);
  if (u.banned) return json({ error: "banned" }, 403);
  if (!KEY.test(key)) return json({ error: "bad runner key" }, 400);
  const raw = await req.text();
  if (raw.length > 4096) return json({ error: "too large" }, 413);
  let b: ReportBody;
  try { b = JSON.parse(raw); } catch { return json({ error: "bad json" }, 400); }
  const verdict = b.verdict === "good" ? "good" : b.verdict === "bad" ? "bad" : null;
  if (!verdict) return json({ error: "bad verdict" }, 400);
  const known = allFlags(env);
  const flags = [...new Set((b.flags ?? []).filter(f => known.includes(f)))];
  const name = String(b.targetName ?? key).slice(0, 48);
  const comment = String(b.comment ?? "").slice(0, 500);
  const context = String(b.context ?? "").slice(0, 200);
  const runId = String(b.runId ?? "").slice(0, 40);

  // Per-account write limits (an hour and a day), so a signed-in account cannot flood the database.
  const t = now();
  const lim = await env.DB.prepare(
    `UPDATE users SET
       rl_hour_count = CASE WHEN rl_hour_start > ? - 3600 THEN rl_hour_count + 1 ELSE 1 END,
       rl_hour_start = CASE WHEN rl_hour_start > ? - 3600 THEN rl_hour_start ELSE ? END,
       rl_day_count  = CASE WHEN rl_day_start > ? - 86400 THEN rl_day_count + 1 ELSE 1 END,
       rl_day_start  = CASE WHEN rl_day_start > ? - 86400 THEN rl_day_start ELSE ? END,
       last_seen = ?
     WHERE id = ? RETURNING rl_hour_count, rl_day_count`).bind(t, t, t, t, t, t, t, u.id).first<{ rl_hour_count: number; rl_day_count: number }>();
  if (!lim || lim.rl_hour_count > (Number(env.MAX_PER_HOUR) || 30) || lim.rl_day_count > (Number(env.MAX_PER_DAY) || 200))
    return json({ error: "rating limit reached, try later" }, 429);

  await env.DB.prepare(
    `INSERT INTO reports (rater_id, target_key, target_name, verdict, flags, comment, context, run_id, created_at, updated_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(rater_id, target_key) DO UPDATE SET target_name = excluded.target_name, verdict = excluded.verdict,
       flags = excluded.flags, comment = excluded.comment, context = excluded.context, run_id = excluded.run_id,
       updated_at = excluded.updated_at, comment_public = CASE WHEN reports.comment = excluded.comment THEN reports.comment_public ELSE 0 END`)
    .bind(u.id, key, name, verdict, JSON.stringify(flags), comment, context, runId, t, t).run();
  await recompute(env, [key]);
  return json(await afterWrite(env, u, key));
}

async function deleteReport(req: Request, env: Env, key: string): Promise<Response> {
  const u = await sessionUser(req, env, "app");
  if (!u) return json({ error: "not signed in" }, 401);
  if (!KEY.test(key)) return json({ error: "bad runner key" }, 400);
  await env.DB.prepare("DELETE FROM reports WHERE rater_id = ? AND target_key = ?").bind(u.id, key).run();
  await recompute(env, [key]);
  return json(await afterWrite(env, u, key));
}

/**
 * The answer to a rating or a take-back: the runner's new totals ("score", null when nobody rates them any more) and
 * the part of them that is the caller's own rating ("mine", in the same shape), so the app can show the rating at once
 * and later add a changed rating of its own on top of everyone else's.
 */
async function afterWrite(env: Env, u: User, key: string) {
  const score = await env.DB.prepare(`SELECT ${SCORE_COLUMNS} FROM scores WHERE target_key = ?`).bind(key).first();
  const row = await env.DB.prepare("SELECT verdict, flags FROM reports WHERE rater_id = ? AND target_key = ?")
    .bind(u.id, key).first<{ verdict: string; flags: string }>();
  let mine = total([], weights(env));
  if (row) {
    const o = await env.DB.prepare("SELECT scope, weight FROM overrides WHERE user_id = ?").bind(u.id).all<{ scope: string; weight: number }>();
    mine = total([{ verdict: row.verdict, flags: JSON.parse(row.flags) as string[], role: u.role, banned: !!u.banned,
                    overrides: new Map(o.results.map(r => [r.scope, r.weight])) }], weights(env));
  }
  return { ok: true, score: score ?? null, mine: asScore(key, mine), weight: roleWeight(env, u) };
}

/** Totals in the shape the reputation lookup returns. */
function asScore(key: string, t: Totals) {
  return { key, good: t.good, bad: t.bad, toxic: t.toxic, backstab: t.backstab, rookOnRook: t.rook_on_rook, dcEarly: t.dc_early,
           weightedGood: t.w_good, weightedBad: t.w_bad, weightedRookBad: t.w_rook_bad, weightedRookGood: t.w_rook_good };
}

/** How many people one rating from this account counts as (before any per-category override). */
function roleWeight(env: Env, u: User): number {
  const w = weights(env);
  return u.banned ? 0 : u.role === "admin" ? w.admin : u.role === "trusted" ? w.trusted : w.user;
}

/** Recomputes the totals of these runners from their ratings (after a rating, a ban, an override or a role change). */
async function recompute(env: Env, keys: string[]): Promise<void> {
  const w = weights(env);
  for (let i = 0; i < keys.length; i += 50) {
    const chunk = keys.slice(i, i + 50);
    const { results } = await env.DB.prepare(
      `SELECT r.target_key, r.target_name, r.verdict, r.flags, r.rater_id, u.role, u.banned, r.updated_at
       FROM reports r JOIN users u ON u.id = r.rater_id WHERE r.target_key IN (${chunk.map(() => "?").join(",")})`)
      .bind(...chunk).all<{ target_key: string; target_name: string; verdict: string; flags: string; rater_id: number; role: string; banned: number; updated_at: number }>();
    const raters = [...new Set(results.map(r => r.rater_id))];
    const overrides = new Map<number, Map<string, number>>();
    if (raters.length > 0) {
      const o = await env.DB.prepare(`SELECT user_id, scope, weight FROM overrides WHERE user_id IN (${raters.map(() => "?").join(",")})`)
        .bind(...raters).all<{ user_id: number; scope: string; weight: number }>();
      for (const r of o.results) {
        if (!overrides.has(r.user_id)) overrides.set(r.user_id, new Map());
        overrides.get(r.user_id)!.set(r.scope, r.weight);
      }
    }
    const t = now();
    const stmts: D1PreparedStatement[] = [];
    for (const key of chunk) {
      const rows = results.filter(r => r.target_key === key);
      if (rows.length === 0) { stmts.push(env.DB.prepare("DELETE FROM scores WHERE target_key = ?").bind(key)); continue; }
      const reports: ReportRow[] = rows.map(r => ({
        verdict: r.verdict, flags: JSON.parse(r.flags) as string[], role: r.role, banned: !!r.banned,
        overrides: overrides.get(r.rater_id) ?? new Map(),
      }));
      const s = total(reports, w);
      const name = rows.reduce((a, b) => (b.updated_at > a.updated_at ? b : a)).target_name;
      stmts.push(env.DB.prepare(
        `INSERT INTO scores (target_key, target_name, good, bad, toxic, backstab, rook_on_rook, dc_early, w_good, w_bad, w_rook_bad, w_rook_good, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(target_key) DO UPDATE SET target_name = excluded.target_name, good = excluded.good, bad = excluded.bad,
           toxic = excluded.toxic, backstab = excluded.backstab, rook_on_rook = excluded.rook_on_rook,
           dc_early = excluded.dc_early, w_good = excluded.w_good, w_bad = excluded.w_bad, w_rook_bad = excluded.w_rook_bad, w_rook_good = excluded.w_rook_good,
           updated_at = excluded.updated_at`)
        .bind(key, name, s.good, s.bad, s.toxic, s.backstab, s.rook_on_rook, s.dc_early, s.w_good, s.w_bad, s.w_rook_bad, s.w_rook_good, t));
    }
    if (stmts.length > 0) await env.DB.batch(stmts);
  }
}

async function keysRatedBy(env: Env, userId: number): Promise<string[]> {
  const { results } = await env.DB.prepare("SELECT target_key FROM reports WHERE rater_id = ?").bind(userId).all<{ target_key: string }>();
  return results.map(r => r.target_key);
}

// ---------- admin ----------

async function adminApi(req: Request, env: Env, url: URL): Promise<Response> {
  const admin = await sessionUser(req, env, "admin");
  if (!admin || admin.role !== "admin" || admin.banned) return json({ error: "admins only" }, 401);
  // Changes need a custom header: a plain form on another site cannot send it, so no cross-site requests.
  if (req.method !== "GET" && req.headers.get("X-Admin") !== "1") return json({ error: "missing header" }, 400);
  const p = url.pathname.replace(/^\/admin\/api/, "");
  const log = (action: string, detail: unknown) =>
    env.DB.prepare("INSERT INTO audit (admin_id, action, detail, at) VALUES (?, ?, ?, ?)").bind(admin.id, action, JSON.stringify(detail), now()).run();

  if (p === "/me") return json({ id: admin.id, steamId: admin.steam_id, persona: admin.persona, flags: allFlags(env),
                                badFlags: list(env.BAD_FLAGS), goodFlags: list(env.GOOD_FLAGS) });

  if (p === "/users" && req.method === "GET") {
    const q = (url.searchParams.get("q") ?? "").trim();
    const { results } = await env.DB.prepare(
      `SELECT u.id, u.steam_id AS steamId, u.persona, u.role, u.banned, u.ban_reason AS banReason, u.created_at AS createdAt,
              u.last_seen AS lastSeen, (SELECT COUNT(*) FROM reports r WHERE r.rater_id = u.id) AS ratings
       FROM users u WHERE (? = '' OR u.steam_id = ? OR u.persona LIKE ? OR u.role = ?) ORDER BY u.last_seen DESC LIMIT 100`)
      .bind(q, q, `%${q}%`, q).all();
    return json(results);
  }

  const userM = /^\/users\/(\d+)$/.exec(p);
  if (userM && req.method === "GET") {
    const id = Number(userM[1]);
    const user = await env.DB.prepare("SELECT id, steam_id AS steamId, persona, role, banned, ban_reason AS banReason FROM users WHERE id = ?").bind(id).first();
    if (!user) return json({ error: "no such user" }, 404);
    const overrides = (await env.DB.prepare("SELECT scope, weight FROM overrides WHERE user_id = ?").bind(id).all()).results;
    const ratings = (await env.DB.prepare(
      `SELECT target_key AS targetKey, target_name AS targetName, verdict, flags, comment, comment_public AS commentPublic, updated_at AS updatedAt
       FROM reports WHERE rater_id = ? ORDER BY updated_at DESC LIMIT 300`).bind(id).all()).results;
    return json({ user, overrides, ratings });
  }

  const actM = /^\/users\/(\d+)\/(ban|role|override)$/.exec(p);
  if (actM && req.method === "POST") {
    const id = Number(actM[1]);
    const target = await env.DB.prepare("SELECT id, steam_id, role FROM users WHERE id = ?").bind(id).first<{ id: number; steam_id: string; role: string }>();
    if (!target) return json({ error: "no such user" }, 404);
    const protectedAdmin = isOwner(env, target.steam_id);
    const body = await req.json() as { banned?: boolean; reason?: string; role?: string; scope?: string; weight?: number | null };
    if (actM[2] === "ban") {
      if (protectedAdmin) return json({ error: "the owner account cannot be banned" }, 400);
      await env.DB.prepare("UPDATE users SET banned = ?, ban_reason = ? WHERE id = ?").bind(body.banned ? 1 : 0, body.banned ? String(body.reason ?? "").slice(0, 200) : null, id).run();
      if (body.banned) await env.DB.prepare("DELETE FROM sessions WHERE user_id = ?").bind(id).run();
      await log(body.banned ? "ban" : "unban", { userId: id, steamId: target.steam_id, reason: body.reason ?? "" });
    } else if (actM[2] === "role") {
      const role = body.role;
      if (role !== "user" && role !== "trusted" && role !== "admin") return json({ error: "bad role" }, 400);
      if (protectedAdmin && role !== "admin") return json({ error: "the owner account stays an admin" }, 400);
      await env.DB.prepare("UPDATE users SET role = ? WHERE id = ?").bind(role, id).run();
      if (role !== "admin") await env.DB.prepare("DELETE FROM sessions WHERE user_id = ? AND kind = 'admin'").bind(id).run();
      await log("role", { userId: id, steamId: target.steam_id, role });
    } else {
      const scope = String(body.scope ?? "");
      if (scope !== "*" && scope !== "good" && scope !== "bad" && !allFlags(env).includes(scope)) return json({ error: "bad scope" }, 400);
      if (body.weight === null || body.weight === undefined) {
        await env.DB.prepare("DELETE FROM overrides WHERE user_id = ? AND scope = ?").bind(id, scope).run();
      } else {
        const weight = Math.max(0, Math.min(100, Number(body.weight)));
        await env.DB.prepare("INSERT INTO overrides (user_id, scope, weight) VALUES (?, ?, ?) ON CONFLICT(user_id, scope) DO UPDATE SET weight = excluded.weight")
          .bind(id, scope, weight).run();
      }
      await log("override", { userId: id, steamId: target.steam_id, scope, weight: body.weight ?? null });
    }
    await recompute(env, await keysRatedBy(env, id));
    return json({ ok: true });
  }

  if (p === "/runner" && req.method === "GET") {
    const key = (url.searchParams.get("key") ?? "").trim().toUpperCase();
    const score = await env.DB.prepare(`SELECT ${SCORE_COLUMNS} FROM scores WHERE target_key = ?`).bind(key).first();
    const ratings = (await env.DB.prepare(
      `SELECT r.rater_id AS raterId, u.steam_id AS steamId, u.persona, u.role, u.banned, r.target_name AS targetName, r.verdict,
              r.flags, r.comment, r.comment_public AS commentPublic, r.context, r.updated_at AS updatedAt
       FROM reports r JOIN users u ON u.id = r.rater_id WHERE r.target_key = ? ORDER BY r.updated_at DESC LIMIT 300`).bind(key).all()).results;
    return json({ key, score, ratings });
  }

  if (p === "/runners" && req.method === "GET") {
    const q = (url.searchParams.get("q") ?? "").trim().toUpperCase();
    const { results } = await env.DB.prepare(
      `SELECT ${SCORE_COLUMNS} FROM scores WHERE (? = '' OR target_key LIKE ? OR UPPER(target_name) LIKE ?) ORDER BY updated_at DESC LIMIT 100`)
      .bind(q, `%${q}%`, `%${q}%`).all();
    return json(results);
  }

  // Edit one rating (verdict and flags) or delete it.
  if (p === "/rating" && req.method === "POST") {
    const body = await req.json() as { raterId?: number; targetKey?: string; verdict?: string; flags?: string[]; delete?: boolean };
    const raterId = Number(body.raterId), key = String(body.targetKey ?? "");
    const before = await env.DB.prepare("SELECT verdict, flags FROM reports WHERE rater_id = ? AND target_key = ?")
      .bind(raterId, key).first<{ verdict: string; flags: string }>();
    if (!before) return json({ error: "no such rating" }, 404);
    if (body.delete) {
      await env.DB.prepare("DELETE FROM reports WHERE rater_id = ? AND target_key = ?").bind(raterId, key).run();
      await log("delete rating", { raterId, targetKey: key, was: before });
    } else {
      const verdict = body.verdict === "good" ? "good" : body.verdict === "bad" ? "bad" : null;
      if (!verdict) return json({ error: "bad verdict" }, 400);
      const allowed = list(verdict === "bad" ? env.BAD_FLAGS : env.GOOD_FLAGS);
      const flags = [...new Set((body.flags ?? []).filter(f => allowed.includes(f)))];
      await env.DB.prepare("UPDATE reports SET verdict = ?, flags = ? WHERE rater_id = ? AND target_key = ?")
        .bind(verdict, JSON.stringify(flags), raterId, key).run();
      await log("edit rating", { raterId, targetKey: key, was: before, now: { verdict, flags } });
    }
    await recompute(env, [key]);
    return json({ ok: true });
  }

  if (p === "/comment" && req.method === "POST") {
    const body = await req.json() as { raterId?: number; targetKey?: string; public?: boolean };
    await env.DB.prepare("UPDATE reports SET comment_public = ? WHERE rater_id = ? AND target_key = ?")
      .bind(body.public ? 1 : 0, Number(body.raterId), String(body.targetKey)).run();
    await log(body.public ? "comment public" : "comment private", { raterId: body.raterId, targetKey: body.targetKey });
    return json({ ok: true });
  }

  if (p === "/recompute" && req.method === "POST") {
    const { results } = await env.DB.prepare("SELECT DISTINCT target_key FROM reports").all<{ target_key: string }>();
    await recompute(env, results.map(r => r.target_key));
    await log("recompute", { runners: results.length });
    return json({ ok: true, runners: results.length });
  }

  if (p === "/stats" && req.method === "GET") return json(await stats(env));
  if (p === "/audit" && req.method === "GET") {
    const { results } = await env.DB.prepare(
      `SELECT a.at, a.action, a.detail, u.steam_id AS adminSteamId, u.persona AS adminPersona
       FROM audit a JOIN users u ON u.id = a.admin_id ORDER BY a.id DESC LIMIT 200`).all();
    return json(results);
  }

  if (p === "/signout" && req.method === "POST") {
    const token = /(?:^|;\s*)sd_admin=([^;]+)/.exec(req.headers.get("Cookie") ?? "")?.[1];
    if (token) await env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?").bind(await sha256(token)).run();
    return new Response(JSON.stringify({ ok: true }), {
      headers: { "Content-Type": "application/json", "Set-Cookie": "sd_admin=; Path=/admin; HttpOnly; Secure; SameSite=Strict; Max-Age=0" },
    });
  }
  return json({ error: "not found" }, 404);
}

// ---------- usage ----------

/**
 * Counts for the admin panel's Usage tab: no names or IDs, only how many. Accounts are people who signed in with
 * Steam; "active" means the app was started (or a rating made) while signed in. With CF_ACCOUNT_ID and CF_API_TOKEN
 * set, also the last 14 days of requests and database rows, from Cloudflare's own analytics.
 */
async function stats(env: Env) {
  const t = now(), day = 86400;
  const accounts = await env.DB.prepare(
    `SELECT COUNT(*) AS total,
            SUM(last_seen > ?) AS active14, SUM(last_seen > ?) AS active1, SUM(created_at > ?) AS new14,
            SUM(banned) AS banned
     FROM users`).bind(t - 14 * day, t - day, t - 14 * day).first();
  const ratings = await env.DB.prepare(
    `SELECT (SELECT COUNT(*) FROM reports) AS ratings, (SELECT COUNT(*) FROM scores) AS runners,
            (SELECT COUNT(*) FROM reports WHERE updated_at > ?) AS ratings14`).bind(t - 14 * day).first();
  return { accounts, ratings, usage: await usage(env) };
}

async function usage(env: Env): Promise<{ days?: { date: string; requests: number; rowsRead: number; rowsWritten: number }[]; error?: string }> {
  if (!env.CF_ACCOUNT_ID || !env.CF_API_TOKEN) return { error: "not set up" };
  const end = new Date(), start = new Date(end.getTime() - 13 * 86400 * 1000);
  const d = (x: Date) => x.toISOString().slice(0, 10);
  const query = `query ($account: String!, $start: Date!, $end: Date!, $script: String!) {
    viewer { accounts(filter: { accountTag: $account }) {
      workers: workersInvocationsAdaptive(limit: 1000, filter: { date_geq: $start, date_leq: $end, scriptName: $script }) {
        sum { requests } dimensions { date } }
      d1: d1AnalyticsAdaptiveGroups(limit: 1000, filter: { date_geq: $start, date_leq: $end }) {
        sum { rowsRead rowsWritten } dimensions { date } }
    } } }`;
  try {
    const res = await fetch("https://api.cloudflare.com/client/v4/graphql", {
      method: "POST",
      headers: { Authorization: `Bearer ${env.CF_API_TOKEN}`, "Content-Type": "application/json" },
      body: JSON.stringify({ query, variables: { account: env.CF_ACCOUNT_ID, start: d(start), end: d(end), script: "sleaze-detector" } }),
    });
    const body = await res.json() as {
      errors?: { message: string }[] | null;
      data?: { viewer: { accounts: { workers: { sum: { requests: number }; dimensions: { date: string } }[];
                                      d1: { sum: { rowsRead: number; rowsWritten: number }; dimensions: { date: string } }[] }[] } };
    };
    if (body.errors?.length) return { error: body.errors.map(e => e.message).join("; ") };
    const acc = body.data?.viewer.accounts[0];
    const days = new Map<string, { date: string; requests: number; rowsRead: number; rowsWritten: number }>();
    for (let i = 0; i < 14; i++) {
      const k = d(new Date(start.getTime() + i * 86400 * 1000));
      days.set(k, { date: k, requests: 0, rowsRead: 0, rowsWritten: 0 });
    }
    for (const w of acc?.workers ?? []) { const x = days.get(w.dimensions.date); if (x) x.requests += w.sum.requests; }
    for (const g of acc?.d1 ?? []) { const x = days.get(g.dimensions.date); if (x) { x.rowsRead += g.sum.rowsRead; x.rowsWritten += g.sum.rowsWritten; } }
    return { days: [...days.values()].reverse() };
  } catch (e) {
    return { error: String(e) };
  }
}

// ---------- daily snapshot ----------

/**
 * Publishes every runner's totals to a GitHub repository (only totals: never who rated whom, never comments). The app
 * starts from this file and asks the server only for what changed since, so it keeps working, a day out of date at
 * worst, if the server is down or out of its daily quota.
 */
async function publishSnapshot(env: Env): Promise<void> {
  if (!env.GITHUB_TOKEN || !env.SNAPSHOT_REPO) return;
  const path = env.SNAPSHOT_PATH || "scores.json";
  const { results } = await env.DB.prepare(`SELECT ${SCORE_COLUMNS} FROM scores ORDER BY target_key`).all();
  const content = JSON.stringify({ generatedAt: now(), scores: results });
  const api = `https://api.github.com/repos/${env.SNAPSHOT_REPO}/contents/${path}`;
  const headers = {
    Authorization: `Bearer ${env.GITHUB_TOKEN}`,
    Accept: "application/vnd.github+json",
    "User-Agent": "sleaze-detector-ratings",
  };
  const existing = await fetch(api, { headers });
  const sha = existing.ok ? ((await existing.json()) as { sha?: string }).sha : undefined;
  const bytes = new TextEncoder().encode(content);
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  const res = await fetch(api, {
    method: "PUT",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify({ message: `Daily scores ${new Date().toISOString().slice(0, 10)}`, content: btoa(bin), sha }),
  });
  if (!res.ok) console.error("snapshot upload failed", res.status, await res.text());
}

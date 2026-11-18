// The admin panel: one page, served by the Worker at /admin, talking to /admin/api/*.

export const ADMIN_PAGE = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Sleaze Detector admin</title>
<style>
:root{--bg:#f6f6f4;--panel:#fff;--fg:#1d1d1b;--mut:#6b6b66;--line:#e2e2dc;--acc:#2f6fde;--bad:#c8372d;--good:#2f8f4e}
@media (prefers-color-scheme:dark){:root{--bg:#16181c;--panel:#1e2126;--fg:#e8e8e8;--mut:#8a8f98;--line:#2c3037;--acc:#5b8def;--bad:#e5645a;--good:#56b97a}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif}
header{display:flex;gap:16px;align-items:center;padding:12px 16px;border-bottom:1px solid var(--line);background:var(--panel);flex-wrap:wrap}
header h1{font-size:16px;margin:0}nav button{background:none;border:0;color:var(--mut);font:inherit;padding:6px 10px;cursor:pointer;border-radius:6px}
nav button.on{color:var(--fg);background:var(--bg)}main{padding:16px;max-width:1100px}
input,select{font:inherit;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--panel);color:var(--fg)}
button.act{font:inherit;padding:6px 12px;border:1px solid var(--line);border-radius:6px;background:var(--panel);color:var(--fg);cursor:pointer}
button.act.danger{color:var(--bad)}table{border-collapse:collapse;width:100%;margin-top:12px}
td,th{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--mut);font-weight:500}
tr.click{cursor:pointer}tr.click:hover{background:var(--panel)}.mut{color:var(--mut)}.bad{color:var(--bad)}.good{color:var(--good)}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px;margin-top:12px}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.tag{font-size:12px;padding:1px 6px;border-radius:4px;background:var(--bg)}
#msg{margin-left:auto;color:var(--mut)}
</style></head><body>
<header><h1>Sleaze Detector admin</h1>
<nav><button data-t="stats">Usage</button><button data-t="users" class="on">Raters</button><button data-t="runners">Runners</button><button data-t="audit">Audit log</button></nav>
<span id="msg"></span><button class="act" id="recompute" title="Recalculate every runner's totals from their ratings">Recompute scores</button><button class="act" id="out">Sign out</button></header>
<main id="main"></main>
<script>
const $ = (s, e = document) => e.querySelector(s);
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const when = t => t ? new Date(t * 1000).toLocaleString() : "";
let FLAGS = [], BAD_FLAGS = [], GOOD_FLAGS = [];
async function api(path, body) {
  const r = await fetch("/admin/api" + path, body === undefined ? {} :
    { method: "POST", headers: { "Content-Type": "application/json", "X-Admin": "1" }, body: JSON.stringify(body) });
  if (r.status === 401) { location.href = "/admin/login"; throw new Error("sign in"); }
  const d = await r.json(); if (!r.ok) { alert(d.error || r.status); throw new Error(d.error); } return d;
}
const flash = t => { $("#msg").textContent = t; setTimeout(() => $("#msg").textContent = "", 2500); };
const who = u => esc(u.persona || "") + ' <span class="mut">' + esc(u.steamId) + '</span>';
const verdict = v => '<span class="' + (v === "bad" ? "bad" : "good") + '">' + v + '</span>';
const flagList = f => (typeof f === "string" ? JSON.parse(f) : f).map(x => '<span class="tag">' + esc(x) + '</span>').join(" ");
const editButton = (raterId, key, v, flags) =>
  '<button class="act" data-edit="' + raterId + '" data-key="' + esc(key) + '" data-v="' + v + '" data-f="' + esc(typeof flags === "string" ? flags : JSON.stringify(flags)) + '">Edit</button>';

/** Inline editor for one rating, opened in place of its row; calls done() after a change. */
function wireEdits(done) {
  document.querySelectorAll("[data-edit]").forEach(b => b.onclick = e => {
    e.stopPropagation();
    const row = b.closest("tr"), raterId = Number(b.dataset.edit), key = b.dataset.key;
    let v = b.dataset.v;
    const chosen = new Set(JSON.parse(b.dataset.f));
    const cell = document.createElement("td");
    cell.colSpan = row.children.length;
    const draw = () => {
      const opts = v === "bad" ? BAD_FLAGS : GOOD_FLAGS;
      cell.innerHTML = '<div class="row">Verdict <select class="ev"><option value="good">good</option><option value="bad">bad</option></select>' +
        opts.map(f => '<label><input type="checkbox" value="' + esc(f) + '"' + (chosen.has(f) ? " checked" : "") + '> ' + esc(f) + '</label>').join(" ") +
        '<button class="act es">Save</button><button class="act ec">Cancel</button><button class="act danger ed">Delete rating</button></div>';
      cell.querySelector(".ev").value = v;
      cell.querySelector(".ev").onchange = ev => { v = ev.target.value; chosen.clear(); draw(); };
      cell.querySelectorAll("input[type=checkbox]").forEach(c => c.onchange = () => c.checked ? chosen.add(c.value) : chosen.delete(c.value));
      cell.querySelector(".es").onclick = async () => { await api("/rating", { raterId, targetKey: key, verdict: v, flags: [...chosen] }); flash("Rating saved"); done(); };
      cell.querySelector(".ec").onclick = () => done();
      cell.querySelector(".ed").onclick = async () => { if (confirm("Delete this rating?")) { await api("/rating", { raterId, targetKey: key, delete: true }); flash("Rating deleted"); done(); } };
    };
    row.innerHTML = "";
    row.appendChild(cell);
    draw();
  });
}

async function users(q = "") {
  const list = await api("/users?q=" + encodeURIComponent(q));
  $("#main").innerHTML = '<div class="row"><input id="q" placeholder="Steam ID, name, or role (admin / trusted)" size="40" value="' + esc(q) + '"><button class="act" id="go">Search</button></div>' +
    '<table><tr><th>Rater</th><th>Role</th><th>Ratings</th><th>Last seen</th></tr>' +
    list.map(u => '<tr class="click" data-id="' + u.id + '"><td>' + who(u) + (u.banned ? ' <span class="bad">banned</span>' : '') +
      '</td><td>' + u.role + '</td><td>' + u.ratings + '</td><td class="mut">' + when(u.lastSeen) + '</td></tr>').join("") + '</table>';
  $("#go").onclick = () => users($("#q").value); $("#q").onkeydown = e => e.key === "Enter" && users($("#q").value);
  document.querySelectorAll("tr[data-id]").forEach(tr => tr.onclick = () => user(tr.dataset.id));
}

async function user(id) {
  const { user: u, overrides, ratings } = await api("/users/" + id);
  $("#main").innerHTML = '<button class="act" id="back">Back</button><div class="card"><div class="row"><b>' + who(u) + '</b>' +
    ' <a href="https://steamcommunity.com/profiles/' + esc(u.steamId) + '" target="_blank" rel="noopener">Steam profile</a></div>' +
    '<div class="row" style="margin-top:10px">Role <select id="role"><option>user</option><option>trusted</option><option>admin</option></select>' +
    '<button class="act" id="setrole">Set role</button></div>' +
    '<div class="row" style="margin-top:10px">' + (u.banned ? '<span class="bad">Banned: ' + esc(u.banReason) + '</span> <button class="act" id="unban">Unban</button>'
      : '<input id="reason" placeholder="Reason" size="30"><button class="act danger" id="ban">Ban (ratings stop counting)</button>') + '</div>' +
    '<div style="margin-top:14px"><b>Weight overrides</b> <span class="mut">(their ratings of this kind count with this weight; 0 = no weight)</span>' +
    '<table>' + overrides.map(o => '<tr><td>' + esc(o.scope) + '</td><td>' + o.weight + '</td><td><button class="act" data-del="' + esc(o.scope) + '">Remove</button></td></tr>').join("") + '</table>' +
    '<div class="row" style="margin-top:8px"><select id="scope"><option value="*">everything</option><option value="bad">bad ratings</option><option value="good">good ratings</option>' +
    FLAGS.map(f => '<option>' + f + '</option>').join("") + '</select><input id="weight" type="number" min="0" step="0.5" value="0" style="width:80px"><button class="act" id="addo">Set</button></div></div></div>' +
    '<div class="card"><b>Their ratings</b><table><tr><th>Runner</th><th>Verdict</th><th>Flags</th><th>Comment</th><th>When</th><th></th></tr>' +
    ratings.map(r => '<tr><td>' + esc(r.targetName) + ' <span class="mut">' + esc(r.targetKey) + '</span></td><td>' + verdict(r.verdict) + '</td><td>' + flagList(r.flags) +
      '</td><td>' + esc(r.comment) + (r.commentPublic ? ' <span class="tag">public</span>' : '') + '</td><td class="mut">' + when(r.updatedAt) + '</td><td>' +
      editButton(id, r.targetKey, r.verdict, r.flags) + '</td></tr>').join("") + '</table></div>';
  $("#role").value = u.role;
  $("#back").onclick = () => users();
  $("#setrole").onclick = async () => { await api("/users/" + id + "/role", { role: $("#role").value }); flash("Role set"); user(id); };
  if ($("#ban")) $("#ban").onclick = async () => { if (confirm("Ban this rater?")) { await api("/users/" + id + "/ban", { banned: true, reason: $("#reason").value }); flash("Banned"); user(id); } };
  if ($("#unban")) $("#unban").onclick = async () => { await api("/users/" + id + "/ban", { banned: false }); flash("Unbanned"); user(id); };
  $("#addo").onclick = async () => { await api("/users/" + id + "/override", { scope: $("#scope").value, weight: Number($("#weight").value) }); flash("Override set"); user(id); };
  document.querySelectorAll("[data-del]").forEach(b => b.onclick = async () => { await api("/users/" + id + "/override", { scope: b.dataset.del, weight: null }); user(id); });
  wireEdits(() => user(id));
}

async function runners(q = "") {
  const list = await api("/runners?q=" + encodeURIComponent(q));
  $("#main").innerHTML = '<div class="row"><input id="q" placeholder="Runner name" size="40" value="' + esc(q) + '"><button class="act" id="go">Search</button></div>' +
    '<table><tr><th>Runner</th><th>Good</th><th>Bad</th><th>Weighted good / bad</th><th>Updated</th></tr>' +
    list.map(s => '<tr class="click" data-k="' + esc(s.key) + '"><td>' + esc(s.name) + ' <span class="mut">' + esc(s.key) + '</span></td><td>' + s.good + '</td><td>' + s.bad +
      '</td><td>' + s.weightedGood + ' / ' + s.weightedBad + '</td><td class="mut">' + when(s.updatedAt) + '</td></tr>').join("") + '</table>';
  $("#go").onclick = () => runners($("#q").value); $("#q").onkeydown = e => e.key === "Enter" && runners($("#q").value);
  document.querySelectorAll("tr[data-k]").forEach(tr => tr.onclick = () => runner(tr.dataset.k));
}

async function runner(key) {
  const { score, ratings } = await api("/runner?key=" + encodeURIComponent(key));
  $("#main").innerHTML = '<button class="act" id="back">Back</button><div class="card"><b>' + esc(score ? score.name : key) + '</b> <span class="mut">' + esc(key) + '</span>' +
    (score ? '<div class="mut">' + score.good + ' good, ' + score.bad + ' bad; weighted ' + score.weightedGood + ' / ' + score.weightedBad + '</div>' : '') + '</div>' +
    '<div class="card"><table><tr><th>Rater</th><th>Verdict</th><th>Flags</th><th>Comment</th><th></th></tr>' +
    ratings.map(r => '<tr><td class="click" data-u="' + r.raterId + '">' + who(r) + (r.banned ? ' <span class="bad">banned</span>' : '') + ' <span class="tag">' + r.role + '</span></td><td>' + verdict(r.verdict) +
      '</td><td>' + flagList(r.flags) + '</td><td>' + esc(r.comment) + '</td><td>' + (r.comment ? '<button class="act" data-c="' + r.raterId + '" data-p="' + (r.commentPublic ? 0 : 1) + '">' +
      (r.commentPublic ? 'Make private' : 'Make public') + '</button> ' : '') + editButton(r.raterId, key, r.verdict, r.flags) + '</td></tr>').join("") + '</table></div>';
  $("#back").onclick = () => runners();
  document.querySelectorAll("[data-u]").forEach(td => td.onclick = () => user(td.dataset.u));
  document.querySelectorAll("[data-c]").forEach(b => b.onclick = async () => { await api("/comment", { raterId: Number(b.dataset.c), targetKey: key, public: b.dataset.p === "1" }); runner(key); });
  wireEdits(() => runner(key));
}

// Free plan limits per day (Workers requests, D1 rows read and written). Update them after changing plans.
const LIMITS = { requests: 100000, rowsRead: 5000000, rowsWritten: 100000 };

async function stats() {
  const { accounts: a, ratings: r, usage: u } = await api("/stats");
  const n = x => Number(x || 0).toLocaleString();
  const pct = (x, lim) => {
    const p = x / lim * 100;
    return '<span class="' + (p >= 80 ? "bad" : p >= 50 ? "" : "mut") + '">' + (p < 0.1 && x > 0 ? "<0.1" : p.toFixed(p < 10 ? 1 : 0)) + '%</span>';
  };
  const box = (label, value, note) => '<div class="card" style="flex:1;min-width:150px;margin-top:0"><div class="mut">' + label +
    '</div><div style="font-size:24px;font-weight:600">' + value + '</div>' + (note ? '<div class="mut" style="font-size:12px">' + note + '</div>' : '') + '</div>';
  let html = '<p class="mut">Counts only. Accounts are people who signed in with Steam; people using the app without signing in are not counted. ' +
    'Active means the app was started (or a rating made) while signed in.</p>' +
    '<div class="row" style="align-items:stretch">' +
    box("Accounts", n(a.total), n(a.banned) + " banned") + box("Active, last 14 days", n(a.active14)) + box("Active, last day", n(a.active1)) +
    box("New, last 14 days", n(a.new14)) + '</div>' +
    '<div class="row" style="align-items:stretch;margin-top:8px">' + box("Ratings", n(r.ratings), n(r.ratings14) + " made or changed in the last 14 days") +
    box("Runners rated", n(r.runners)) + '</div>';
  if (u.days) {
    const peak = k => Math.max(...u.days.map(d => d[k]));
    html += '<div class="card"><b>Daily use against the free plan</b> <span class="mut">(limits per day: ' + n(LIMITS.requests) + ' requests, ' +
      n(LIMITS.rowsWritten) + ' rows written, ' + n(LIMITS.rowsRead) + ' rows read; over a limit the server stops answering until 00:00 UTC). ' +
      'Busiest day: ' + pct(peak("requests"), LIMITS.requests) + ' of requests, ' + pct(peak("rowsWritten"), LIMITS.rowsWritten) + ' of writes.</span>' +
      '<table><tr><th>Day (UTC)</th><th>Requests</th><th>Rows written</th><th>Rows read</th></tr>' +
      u.days.map(d => '<tr><td>' + d.date + '</td><td>' + n(d.requests) + ' ' + pct(d.requests, LIMITS.requests) + '</td><td>' + n(d.rowsWritten) + ' ' +
        pct(d.rowsWritten, LIMITS.rowsWritten) + '</td><td>' + n(d.rowsRead) + ' ' + pct(d.rowsRead, LIMITS.rowsRead) + '</td></tr>').join("") + '</table></div>';
  } else {
    html += '<div class="card"><b>Daily use against the free plan</b><div class="mut" style="margin-top:6px">' +
      (u.error === "not set up"
        ? 'Not set up. Create a Cloudflare API token with only "Account Analytics: Read", then in server/: ' +
          '<code>npx wrangler secret put CF_API_TOKEN</code> and <code>npx wrangler secret put CF_ACCOUNT_ID</code> (the account ID on the Workers overview page).'
        : 'Cloudflare analytics did not answer: ' + esc(u.error)) + '</div></div>';
  }
  $("#main").innerHTML = html;
}

async function audit() {
  const list = await api("/audit");
  $("#main").innerHTML = '<table><tr><th>When</th><th>Admin</th><th>Action</th><th>Detail</th></tr>' +
    list.map(a => '<tr><td class="mut">' + when(a.at) + '</td><td>' + esc(a.adminPersona || a.adminSteamId) + '</td><td>' + esc(a.action) + '</td><td class="mut">' + esc(a.detail) + '</td></tr>').join("") + '</table>';
}

document.querySelectorAll("nav button").forEach(b => b.onclick = () => {
  document.querySelectorAll("nav button").forEach(x => x.classList.toggle("on", x === b));
  ({ stats, users, runners, audit })[b.dataset.t]();
});
$("#recompute").onclick = async () => { const r = await api("/recompute", {}); flash("Recomputed " + r.runners + " runners"); };
$("#out").onclick = async () => { await api("/signout", {}); location.href = "/admin/login"; };
api("/me").then(me => { FLAGS = me.flags; BAD_FLAGS = me.badFlags; GOOD_FLAGS = me.goodFlags; users(); });
</script></body></html>`;

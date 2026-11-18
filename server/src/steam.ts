// Sign in through Steam (OpenID 2.0). Steam only ever tells us the account's SteamID64; no password or other
// account data passes through this server.

const STEAM_OPENID = "https://steamcommunity.com/openid/login";
const CLAIMED_ID = /^https:\/\/steamcommunity\.com\/openid\/id\/(\d{17})$/;

/** Where to send the browser to sign in. Steam sends it back to `returnTo` with a signed answer. */
export function loginUrl(returnTo: string, realm: string): string {
  const p = new URLSearchParams({
    "openid.ns": "http://specs.openid.net/auth/2.0",
    "openid.mode": "checkid_setup",
    "openid.return_to": returnTo,
    "openid.realm": realm,
    "openid.identity": "http://specs.openid.net/auth/2.0/identifier_select",
    "openid.claimed_id": "http://specs.openid.net/auth/2.0/identifier_select",
  });
  return `${STEAM_OPENID}?${p}`;
}

/**
 * Checks Steam's answer by asking Steam itself (check_authentication) and returns the SteamID64, or null.
 * `expectedReturnTo` must be the exact address we gave Steam, so an answer meant for another site is refused.
 */
export async function verify(callback: URL, expectedReturnTo: string): Promise<string | null> {
  const q = callback.searchParams;
  if (q.get("openid.mode") !== "id_res") return null;
  if (q.get("openid.op_endpoint") !== STEAM_OPENID) return null;
  if (q.get("openid.return_to") !== expectedReturnTo) return null;
  const claimed = q.get("openid.claimed_id") ?? "";
  const m = CLAIMED_ID.exec(claimed);
  if (!m || q.get("openid.identity") !== claimed) return null;

  const body = new URLSearchParams();
  for (const [k, v] of q) if (k.startsWith("openid.")) body.set(k, v);
  body.set("openid.mode", "check_authentication");
  const res = await fetch(STEAM_OPENID, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body,
  });
  if (!res.ok) return null;
  const text = await res.text();
  return /(^|\n)is_valid:true(\n|$)/.test(text) ? m[1] : null;
}

/** The account's display name, if a Steam Web API key is configured (optional: only used in the admin panel). */
export async function persona(steamId: string, apiKey: string | undefined): Promise<string | null> {
  if (!apiKey) return null;
  try {
    const res = await fetch(
      `https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=${encodeURIComponent(apiKey)}&steamids=${steamId}`);
    if (!res.ok) return null;
    const data = await res.json() as { response?: { players?: { personaname?: string }[] } };
    return data.response?.players?.[0]?.personaname ?? null;
  } catch {
    return null;
  }
}

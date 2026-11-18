# Ratings server

The shared ratings for Marathon Sleaze Detector: a Cloudflare Worker (`src/`) with a D1 (SQLite) database
(`migrations/`). It runs on Cloudflare's free plan.

## What it stores

- **Raters:** the Steam account ID (SteamID64) of everyone who signed in, their role (user / trusted / admin) and
  whether they are banned. Signing in goes through Steam's own page; Steam only tells the server the account ID.
- **Ratings:** one per rater per runner (rating again replaces it): good or bad, flags, an optional comment.
  Comments are private unless an admin makes one public.
- **Totals per runner**, recomputed whenever a rating or an admin setting that affects it changes.

Only the totals are ever published (`GET /v1/reputation`, and a daily snapshot to GitHub). Who rated whom and private
comments never leave the server.

## How ratings count

A rating counts as one person, a trusted rater's as `WEIGHT_TRUSTED`, an admin's as `WEIGHT_ADMIN` (5: the app
pins the meter fully red or green at 5, so one admin rating does that on its own). An admin can ban a rater (their
ratings stop counting) or cap the weight of some of their ratings, for example "their Toxic ratings count 0". Banning or
changing a rater recomputes every runner they rated.

## Limits

- Per IP address: 20 writes and 120 reads a minute (Workers rate limiting), checked before any database work.
- Per account: `MAX_PER_HOUR` / `MAX_PER_DAY` ratings. Rating someone again replaces the old rating.
- If the free daily quota runs out, the app falls back to the daily snapshot (a day old at worst).

## Deploy your own

```
npm install
npx wrangler login
npx wrangler d1 create sleaze-detector          # put the database_id it prints into wrangler.toml
npx wrangler d1 migrations apply sleaze-detector --remote
npx wrangler secret put ADMIN_STEAM_ID          # your SteamID64: always admin
npx wrangler secret put STEAM_API_KEY           # optional, https://steamcommunity.com/dev/apikey
npx wrangler deploy
```

Admin panel: `https://<your worker>/admin`, sign in with Steam. Admins can make other admins there.

Daily snapshot: set `SNAPSHOT_REPO` in `wrangler.toml` and `npx wrangler secret put GITHUB_TOKEN` (a fine-grained
token with Contents read / write on that one repository).

Local testing: `npx wrangler d1 migrations apply sleaze-detector --local`, then `npx wrangler dev`.

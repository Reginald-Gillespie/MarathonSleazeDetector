-- Marathon Sleaze Detector: shared ratings.
-- Everything here is private to the server. Only the per-runner totals in `scores` are ever published.

-- Raters: Steam accounts that signed in from the app.
CREATE TABLE users (
  id            INTEGER PRIMARY KEY,
  steam_id      TEXT NOT NULL UNIQUE,          -- SteamID64
  persona       TEXT,                          -- Steam display name at last sign-in (if the API key is set)
  role          TEXT NOT NULL DEFAULT 'user',  -- 'user' | 'trusted' | 'admin'
  banned        INTEGER NOT NULL DEFAULT 0,    -- 1: ratings carry no weight, cannot rate
  ban_reason    TEXT,
  created_at    INTEGER NOT NULL,              -- unix seconds
  last_seen     INTEGER NOT NULL,
  -- Write limits (fixed windows).
  rl_hour_start INTEGER NOT NULL DEFAULT 0,
  rl_hour_count INTEGER NOT NULL DEFAULT 0,
  rl_day_start  INTEGER NOT NULL DEFAULT 0,
  rl_day_count  INTEGER NOT NULL DEFAULT 0
);

-- Sign-in sessions. Only a hash of the token is stored.
CREATE TABLE sessions (
  token_hash TEXT PRIMARY KEY,
  user_id    INTEGER NOT NULL REFERENCES users(id),
  kind       TEXT NOT NULL,                    -- 'app' | 'admin'
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE INDEX sessions_user ON sessions(user_id);

-- Pending Steam sign-ins (a random state ties Steam's answer to the request that started it).
CREATE TABLE logins (
  state      TEXT PRIMARY KEY,
  ret        TEXT NOT NULL,                    -- where to send the token: the app's loopback address, or 'admin'
  expires_at INTEGER NOT NULL
);

-- One rating per rater per runner; rating again replaces it.
CREATE TABLE reports (
  rater_id       INTEGER NOT NULL REFERENCES users(id),
  target_key     TEXT NOT NULL,                -- the app's runner key ("QUARTZWOLF#0816", look-alike letters folded)
  target_name    TEXT NOT NULL,                -- the name as shown
  verdict        TEXT NOT NULL,                -- 'good' | 'bad'
  flags          TEXT NOT NULL DEFAULT '[]',   -- JSON array of flag ids
  comment        TEXT NOT NULL DEFAULT '',
  comment_public INTEGER NOT NULL DEFAULT 0,   -- set by an admin
  context        TEXT NOT NULL DEFAULT '',
  run_id         TEXT NOT NULL DEFAULT '',
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL,
  PRIMARY KEY (rater_id, target_key)
);
CREATE INDEX reports_target ON reports(target_key);

-- Admin weight overrides for one rater: scope is a flag id ("toxic"), 'good', 'bad' or '*' (everything).
-- A report counts with the smallest override that applies to it; weight 0 = carries no weight.
CREATE TABLE overrides (
  user_id INTEGER NOT NULL REFERENCES users(id),
  scope   TEXT NOT NULL,
  weight  REAL NOT NULL,
  PRIMARY KEY (user_id, scope)
);

-- Totals per runner, recomputed from `reports` whenever a rating or an admin setting that affects it changes.
CREATE TABLE scores (
  target_key   TEXT PRIMARY KEY,
  target_name  TEXT NOT NULL,
  good         INTEGER NOT NULL,
  bad          INTEGER NOT NULL,
  toxic        INTEGER NOT NULL,
  backstab     INTEGER NOT NULL,
  cheating     INTEGER NOT NULL,
  rook_on_rook INTEGER NOT NULL,
  dc_early     INTEGER NOT NULL,
  w_good       REAL NOT NULL,
  w_bad        REAL NOT NULL,
  w_rook_bad   REAL NOT NULL,
  updated_at   INTEGER NOT NULL
);
CREATE INDEX scores_updated ON scores(updated_at);

-- Everything an admin did.
CREATE TABLE audit (
  id       INTEGER PRIMARY KEY,
  admin_id INTEGER NOT NULL REFERENCES users(id),
  action   TEXT NOT NULL,
  detail   TEXT NOT NULL,
  at       INTEGER NOT NULL
);

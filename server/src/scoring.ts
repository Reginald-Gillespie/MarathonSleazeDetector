// How ratings add up to a runner's score. Mirrors ScoringRules.ReportWeights in the app
// (src/SleazeDetector/Reputation/Scoring.cs); the app turns these totals into the meter.

export interface Weights {
  user: number;      // a normal rater
  trusted: number;   // a rater an admin marked trusted
  admin: number;     // an admin: by default as many people as it takes to pin the meter (the app's FullAt)
  severeBonus: number;   // extra for a bad rating with a severe flag
  severeFlags: string[];
  rookOnRook: number;    // a Rook on Rook or Nice to Rooks rating, times the rater's weight, for viewers playing Rook
}

export interface ReportRow {
  verdict: string;
  flags: string[];
  role: string;
  banned: boolean;
  overrides: Map<string, number>;   // scope -> weight, for this rater
}

export interface Totals {
  good: number; bad: number;
  toxic: number; backstab: number; rook_on_rook: number; dc_early: number;
  w_good: number; w_bad: number; w_rook_bad: number; w_rook_good: number;
}

/** The rater's weight for this one report: role weight, unless banned or an override says less. */
export function raterWeight(r: ReportRow, w: Weights): number {
  if (r.banned) return 0;
  let weight = r.role === "admin" ? w.admin : r.role === "trusted" ? w.trusted : w.user;
  // An override caps the weight of the reports it applies to ("their Toxic ratings carry no weight").
  const scopes = ["*", r.verdict, ...r.flags];
  for (const s of scopes) {
    const o = r.overrides.get(s);
    if (o !== undefined) weight = Math.min(weight, o);
  }
  return Math.max(0, weight);
}

/** Adds up a runner's reports. */
export function total(reports: ReportRow[], w: Weights): Totals {
  const t: Totals = { good: 0, bad: 0, toxic: 0, backstab: 0, rook_on_rook: 0, dc_early: 0, w_good: 0, w_bad: 0, w_rook_bad: 0, w_rook_good: 0 };
  for (const r of reports) {
    const weight = raterWeight(r, w);
    if (weight <= 0) continue;   // banned or zeroed: not counted at all, not even in the plain counts
    const has = (f: string) => r.flags.includes(f);
    const bad = r.verdict !== "good";
    if (bad) {
      t.bad++;
      if (has("toxic")) t.toxic++;
      if (has("backstab")) t.backstab++;
      if (has("rook_on_rook")) t.rook_on_rook++;
      if (has("dc_early")) t.dc_early++;
    } else t.good++;
    // The Rook flag of the verdict (Rook on Rook / Nice to Rooks) goes to the Rook channel, used only for viewers
    // playing Rook. A rating with only that flag has no general weight.
    const rookFlag = bad ? "rook_on_rook" : "nice_to_rook";
    const rook = has(rookFlag);
    let general = rook && !r.flags.some(f => f !== rookFlag) ? 0 : weight;
    if (bad && general > 0 && r.flags.some(f => w.severeFlags.includes(f))) general += w.severeBonus;
    if (bad) { t.w_bad += general; if (rook) t.w_rook_bad += weight * w.rookOnRook; }
    else { t.w_good += general; if (rook) t.w_rook_good += weight * w.rookOnRook; }
  }
  return t;
}

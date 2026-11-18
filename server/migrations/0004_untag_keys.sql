-- Runner keys no longer include the "#1234" tag: only prox chat shows it, while pings and the kill feed show the
-- name alone. Ratings move to the name; target_name keeps the tag as it was typed or read.

-- Where one account rated the same name more than once (with and without a tag, or with different tags), keep the
-- newest rating, and on a tie the one whose key sorts first.
DELETE FROM reports WHERE EXISTS (
  SELECT 1 FROM reports o
  WHERE o.rater_id = reports.rater_id
    AND (CASE WHEN instr(o.target_key, '#') > 0 THEN substr(o.target_key, 1, instr(o.target_key, '#') - 1) ELSE o.target_key END)
      = (CASE WHEN instr(reports.target_key, '#') > 0 THEN substr(reports.target_key, 1, instr(reports.target_key, '#') - 1) ELSE reports.target_key END)
    AND (o.updated_at > reports.updated_at OR (o.updated_at = reports.updated_at AND o.target_key < reports.target_key)));

UPDATE reports SET target_key = substr(target_key, 1, instr(target_key, '#') - 1) WHERE instr(target_key, '#') > 0;

-- Totals of the moved runners are made again by the daily job, or at once with "Recompute scores" on the admin page.
DELETE FROM scores WHERE instr(target_key, '#') > 0;

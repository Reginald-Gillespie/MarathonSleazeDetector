"""
Real name crops (from the app's --harvest) and their labels.

    python real.py propose data/names            -> data/names/labels.tsv (adds tracks not in it yet, never overwrites)
    python real.py sheets data/names out_dir     -> contact sheets of unchecked tracks to label by eye
    python real.py html data/names out.html      -> a page to check / correct the labels in a browser
    python real.py build data/names out_dir      -> out_dir/real_train.npz and real_val.npz

labels.tsv columns: clip folder, track id, label, status. Label '-' = not a name (scenery, other HUD text). Status:
'auto' (the app's own voted reading, unchecked), 'checked' (looked at), 'user' (confirmed by the user).
"""
import base64
import hashlib
import io
import os
import sys
from collections import defaultdict

import cv2
import numpy as np

from common import H_IN, W_MAX, to_input

VAL_CLIPS = ('21.41.37.21',)        # always held out (red names on orange scenery)


def read_tsv(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8') as f:
        return [l.rstrip('\n').split('\t') for l in f if l.strip()]


# Copies of other clips (same frames under another name): left out so nothing is counted twice.
SKIP_CLIPS = ('CryoTwoTeams',)


def clips(root):
    return sorted(d for d in os.listdir(root)
                  if os.path.exists(os.path.join(root, d, 'tracks.tsv')) and d not in SKIP_CLIPS)


def load_labels(root):
    out = {}
    for r in read_tsv(os.path.join(root, 'labels.tsv')):
        out[(r[0], int(r[1]))] = (r[2], r[3] if len(r) > 3 else 'auto')
    return out


def save_labels(root, labels):
    with open(os.path.join(root, 'labels.tsv'), 'w', encoding='utf-8') as f:
        for (c, t), (lab, st) in sorted(labels.items()):
            f.write(f'{c}\t{t}\t{lab}\t{st}\n')


def track_reads(root, clip):
    by = defaultdict(list)
    for r in read_tsv(os.path.join(root, clip, 'reads.tsv')):
        by[int(r[1])].append(r)
    return by


def full_rows(rows):
    """Reads of a track showing the whole name: box within ~a character of the widest box (not still drawing in)."""
    wmax = max(int(r[6]) for r in rows)
    h = float(rows[0][8])
    return [r for r in rows if int(r[6]) >= wmax - 10.3 / 1200 * h * 0.8]


def propose(root):
    labels = load_labels(root)
    added = 0
    for c in clips(root):
        reads = track_reads(root, c)
        for r in read_tsv(os.path.join(root, c, 'tracks.tsv')):
            t = int(r[0])
            if (c, t) in labels:
                continue
            name = r[4] if len(r) > 4 else ''
            # Tracks the app never took for a runner and saw only once or twice are scenery: '-' for now.
            if not name and len(reads.get(t, [])) < 3:
                labels[(c, t)] = ('-', 'auto')
            else:
                labels[(c, t)] = (name or '?', 'auto')
            added += 1
    save_labels(root, labels)
    print(f'{added} tracks added, {len(labels)} total')


def middle_crop(root, clip, rows):
    rows = full_rows(rows)
    r = rows[len(rows) // 2]
    return cv2.cvtColor(cv2.imread(os.path.join(root, clip, r[0])), cv2.COLOR_BGR2RGB), r


def sheets(root, out, per=24, include_dash=False):
    os.makedirs(out, exist_ok=True)
    labels = load_labels(root)
    items = []
    for c in clips(root):
        reads = track_reads(root, c)
        for t, rows in sorted(reads.items()):
            lab, st = labels.get((c, t), ('?', 'auto'))
            if st != 'auto' or (lab == '-' and not include_dash):
                continue
            items.append((c, t, lab, rows))
    index = []
    for s in range(0, len(items), per):
        tiles = []
        for k, (c, t, lab, rows) in enumerate(items[s:s + per]):
            im, _ = middle_crop(root, c, rows)
            im = cv2.resize(im, None, fx=3, fy=3, interpolation=cv2.INTER_CUBIC)
            tile = np.full((im.shape[0] + 4, 900, 3), 30, np.uint8)
            tile[2:2 + im.shape[0], 60:60 + min(im.shape[1], 840)] = im[:, :840]
            cv2.putText(tile, str(k), (4, tile.shape[0] // 2 + 8), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
            tiles.append(tile)
            index.append(f'{s // per}\t{k}\t{c}\t{t}\t{lab}\t{len(rows)}')
        cv2.imwrite(os.path.join(out, f'sheet_{s // per:03d}.png'), cv2.cvtColor(np.vstack(tiles), cv2.COLOR_RGB2BGR))
    with open(os.path.join(out, 'index.tsv'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(index) + '\n')
    print(f'{len(items)} tracks on {(len(items) + per - 1) // per} sheets')


def clip_title(c):
    """The clip's file name as it was (folder names have spaces and the like replaced)."""
    return c.replace('_-_', ' - ').replace('_', ' ') + '.mp4'


def html(root, out):
    """A page to look over the labels: three crops per name with its clip and time, the label in an editable box, and a button that saves the
    corrected labels.tsv (to put back in data/names)."""
    labels = load_labels(root)
    parts = []
    for c in clips(root):
        reads = track_reads(root, c)
        for t, rows in sorted(reads.items()):
            lab, st = labels.get((c, t), ('?', 'auto'))
            # Only the names that are used for training: looked at, and read as a name.
            if st == 'auto' or lab in ('-', '?'):
                continue
            full = full_rows(rows)
            # Three frames of the name (start, middle, end of its time on screen): over different scenery a hard
            # letter is usually clear in at least one.
            imgs = []
            for k in sorted({0, len(full) // 2, len(full) - 1}):
                im = cv2.imread(os.path.join(root, c, full[k][0]))
                im = cv2.resize(im, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
                ok, enc = cv2.imencode('.jpg', im, [cv2.IMWRITE_JPEG_QUALITY, 92])
                imgs.append(f'<img src="data:image/jpeg;base64,{base64.b64encode(enc.tobytes()).decode()}">')
            secs = float(full[len(full) // 2][9])
            where = f'{clip_title(c)} at {int(secs // 60)}:{int(secs % 60):02d}'
            parts.append(f'<div class="row" data-k="{c}\t{t}" data-st="{st}"><div class="where">{where}</div>'
                         f'<div class="imgs">{"".join(imgs)}</div>'
                         f'<input value="{lab}" spellcheck="false"><label><input type="checkbox" class="ok"'
                         f'{" checked" if st == "user" else ""}> correct</label><span class="st">{st}</span></div>')
    all_rows = '\n'.join(f'{c}\t{t}\t{lab}\t{st}' for (c, t), (lab, st) in sorted(labels.items()))
    page = HTML.replace('%ROWS%', '\n'.join(parts)).replace('%ALL%', base64.b64encode(all_rows.encode()).decode())
    with open(out, 'w', encoding='utf-8') as f:
        f.write(page)
    print(f'{len(parts)} names on the page')


HTML = """<!doctype html><meta charset="utf-8"><title>Name labels</title>
<style>
:root{--bg:#16181c;--fg:#e8e8e8;--mut:#8a8f98;--line:#2a2e35}
body{background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif;margin:0;padding:16px}
.top{position:sticky;top:0;background:var(--bg);padding:8px 0 12px;border-bottom:1px solid var(--line);z-index:1}
.row{display:flex;gap:12px;align-items:center;padding:6px 0;border-bottom:1px solid var(--line);flex-wrap:wrap}
.row img{image-rendering:auto;max-width:100%}
.where{flex-basis:100%;color:var(--mut);font-size:12px}
.imgs{display:flex;gap:6px;flex-wrap:wrap}
.row input[type=text],.row input:not([type]){font:16px ui-monospace,monospace;background:#0e0f12;color:var(--fg);
  border:1px solid var(--line);padding:4px 6px;width:22ch}
.st{color:var(--mut);font-size:12px}
button{font:14px system-ui;padding:6px 12px}
</style>
<div class="top">Fix any wrong name (type <b>-</b> if it is not a name), tick <b>correct</b> on the ones you checked,
then <button id="save">Save labels.tsv</button> and put the file in data\\names.</div>
%ROWS%
<script>
const all = new TextDecoder().decode(Uint8Array.from(atob('%ALL%'), c => c.charCodeAt(0)));
document.getElementById('save').onclick = () => {
  const m = new Map(all.split('\\n').filter(Boolean).map(l => { const p = l.split('\\t'); return [p[0] + '\\t' + p[1], p]; }));
  document.querySelectorAll('.row').forEach(r => {
    const p = m.get(r.dataset.k); if (!p) return;
    const v = r.querySelector('input:not([type=checkbox])').value.trim().toUpperCase();
    const ok = r.querySelector('.ok').checked;
    if (v !== p[2] || ok) { p[2] = v; p[3] = 'user'; }
  });
  const text = [...m.values()].map(p => p.join('\\t')).join('\\n') + '\\n';
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], {type: 'text/tab-separated-values'}));
  a.download = 'labels.tsv'; a.click();
};
</script>"""


def build(root, out):
    os.makedirs(out, exist_ok=True)
    labels = load_labels(root)
    sets = {'train': ([], []), 'val': ([], [])}
    for c in clips(root):
        reads = track_reads(root, c)
        # Trimmed copies of a recording go to the same side as the recording itself.
        key = c.replace('_-_Trim2', '').replace('_-_Trim', '')
        val = any(v in c for v in VAL_CLIPS) or int(hashlib.md5(key.encode()).hexdigest(), 16) % 5 == 0
        for t, rows in reads.items():
            lab, st = labels.get((c, t), ('?', 'auto'))
            if lab in ('?', '') or st == 'auto':
                continue
            # Not a name (scenery, icons, other HUD text): the model is taught to read nothing there.
            text = '' if lab == '-' else lab
            for r in full_rows(rows):
                im = cv2.cvtColor(cv2.imread(os.path.join(root, c, r[0])), cv2.COLOR_BGR2RGB)
                xs, ys = sets['val' if val else 'train']
                xs.append(to_input(im)); ys.append(text)
    for k, (xs, ys) in sets.items():
        x = np.zeros((len(xs), H_IN, W_MAX, 3), np.uint8)
        w = np.zeros(len(xs), np.int16)
        for i, im in enumerate(xs):
            x[i, :, :im.shape[1]] = im
            w[i] = im.shape[1]
        np.savez(os.path.join(out, f'real_{k}.npz'), x=x, w=w, y=np.array(ys))
        print(k, len(ys), 'crops', len(set(ys)), 'names')


def corpus(path, out):
    """The read-test corpus (image, x, y, w, h, zone, text per line) as model inputs, for testing."""
    from common import crop_box
    base = os.path.dirname(os.path.abspath(path))
    xs, ys, frames = [], [], {}
    for r in read_tsv(path):
        f = r[0] if os.path.isabs(r[0]) else os.path.join(base, r[0])
        if f not in frames:
            frames[f] = cv2.cvtColor(cv2.imread(f), cv2.COLOR_BGR2RGB)
        im = frames[f]
        x, y, w, h = map(int, r[1:5])
        xs.append(to_input(crop_box(im, x, y, w, h, im.shape[0])))
        ys.append(r[6])
    x = np.zeros((len(xs), H_IN, W_MAX, 3), np.uint8)
    w = np.zeros(len(xs), np.int16)
    for i, im in enumerate(xs):
        x[i, :, :im.shape[1]] = im
        w[i] = im.shape[1]
    np.savez(out, x=x, w=w, y=np.array(ys))
    print(len(ys), 'crops')


if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'propose':
        propose(sys.argv[2])
    elif cmd == 'sheets':
        sheets(sys.argv[2], sys.argv[3])
    elif cmd == 'html':
        html(sys.argv[2], sys.argv[3])
    elif cmd == 'corpus':
        corpus(sys.argv[2], sys.argv[3])
    elif cmd == 'build':
        build(sys.argv[2], sys.argv[3])

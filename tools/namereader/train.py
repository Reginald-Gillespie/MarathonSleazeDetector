"""
Trains the name reader: a small CRNN (convolutions, then a bidirectional LSTM) with CTC over the synthetic shards from
gen.py, plus the labelled real crops (real.npz from real.py) mixed in, and checks it on held-out real crops.

    python train.py synth_dir out_dir [--real real.npz] [--val real_val.npz] [--epochs 20] [--init model.pt]

Writes out_dir/model.pt (best on the real validation set, or the last epoch without one), out_dir/namenet.onnx and
out_dir/log.txt.
"""
import argparse
import glob
import os
import time

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from common import CHARSET, H_IN, W_MAX

NC = len(CHARSET) + 1


class CRNN(nn.Module):
    def __init__(self):
        super().__init__()

        def blk(i, o):
            return [nn.Conv2d(i, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True)]

        self.cnn = nn.Sequential(
            *blk(3, 32), nn.MaxPool2d(2, 2),                        # 16 x 160
            *blk(32, 64), nn.MaxPool2d(2, 2),                       # 8 x 80: ~3 steps per character
            *blk(64, 128), *blk(128, 128), nn.MaxPool2d((2, 1)),    # 4 x 80
            *blk(128, 192), *blk(192, 192), nn.MaxPool2d((2, 1)),   # 2 x 80
            nn.Conv2d(192, 256, (2, 1)), nn.BatchNorm2d(256), nn.ReLU(inplace=True))
        self.rnn = nn.LSTM(256, 128, num_layers=2, bidirectional=True, batch_first=True, dropout=0.1)
        self.fc = nn.Linear(256, NC)

    def forward(self, x):
        """x: [B, 3, 32, 320] float, RGB scaled to -1..1. Returns logits [B, T, NC]."""
        f = self.cnn(x).squeeze(2).permute(0, 2, 1)
        f, _ = self.rnn(f)
        return self.fc(f)


class Exported(nn.Module):
    """What the app runs: uint8-range RGB in, per-step class probabilities out."""
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, x):
        return F.softmax(self.m(x / 127.5 - 1.0), dim=-1)


PAGE = """<!doctype html><meta charset="utf-8"><meta http-equiv="refresh" content="3"><title>Name reader training</title>
<style>
:root{--bg:#f6f6f4;--fg:#1d1d1b;--mut:#6b6b66;--bar:#2f6fde;--track:#dcdcd6}
@media (prefers-color-scheme:dark){:root{--bg:#16181c;--fg:#e8e8e8;--mut:#8a8f98;--bar:#5b8def;--track:#2a2e35}}
body{background:var(--bg);color:var(--fg);font:15px system-ui,sans-serif;margin:0;padding:24px 16px;max-width:720px}
h1{font-size:18px;margin:0 0 4px}.mut{color:var(--mut)}
.row{margin:18px 0}.lab{display:flex;justify-content:space-between;margin-bottom:6px}
.track{height:14px;background:var(--track);border-radius:7px;overflow:hidden}
.fill{height:100%;background:var(--bar)}
table{border-collapse:collapse;margin-top:18px}td,th{padding:4px 14px 4px 0;text-align:left;font-variant-numeric:tabular-nums}
</style>
<h1>Name reader training</h1><div class="mut">%STATUS% &middot; updated %TIME% (refreshes every 3 s)</div>
<div class="row"><div class="lab"><span>This pass (%EP% of %EPS%)</span><span>%STEP% / %STEPS% steps &middot; %EPLEFT% left</span></div>
<div class="track"><div class="fill" style="width:%EPPCT%%"></div></div></div>
<div class="row"><div class="lab"><span>Whole run</span><span>%PCT%% &middot; about %LEFT% left</span></div>
<div class="track"><div class="fill" style="width:%PCT%%"></div></div></div>
<div class="mut">Training loss now: %LOSS% (lower is better)</div>
<table><tr><th>Pass</th><th>Loss</th><th>Synthetic names read exactly</th><th>Real names read exactly</th><th>Time</th></tr>%ROWS%</table>
"""


def fmt_time(sec):
    sec = max(0, int(sec))
    return f'{sec // 3600}h {sec % 3600 // 60:02d}m' if sec >= 3600 else f'{sec // 60}m {sec % 60:02d}s'


class Progress:
    """A page in the output folder that shows how far training is (open it in a browser; it refreshes itself)."""

    def __init__(self, out, epochs, steps):
        self.path = os.path.join(out, 'progress.html')
        self.epochs, self.steps, self.rows, self.t0 = epochs, steps, [], time.time()
        self.ep_t0 = self.t0

    def epoch_start(self):
        self.ep_t0 = time.time()

    def update(self, ep, step, loss, status='Training'):
        done = ep * self.steps + step
        total = self.epochs * self.steps
        el = time.time() - self.t0
        ep_el = time.time() - self.ep_t0
        rate = done / el if el > 0 and done > 0 else 0
        ep_rate = step / ep_el if ep_el > 0 and step > 0 else rate
        vals = {'%STATUS%': status, '%TIME%': time.strftime('%H:%M:%S'), '%EP%': str(min(ep + 1, self.epochs)),
                '%EPS%': str(self.epochs), '%STEP%': str(step), '%STEPS%': str(self.steps),
                '%EPPCT%': f'{100 * step / max(1, self.steps):.1f}', '%PCT%': f'{100 * done / max(1, total):.1f}',
                '%EPLEFT%': fmt_time((self.steps - step) / ep_rate) if ep_rate else '?',
                '%LEFT%': fmt_time((total - done) / rate) if rate else '?',
                '%LOSS%': f'{loss:.4f}' if loss is not None else '-', '%ROWS%': ''.join(self.rows)}
        page = PAGE
        for k, v in vals.items():
            page = page.replace(k, v)
        tmp = self.path + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            f.write(page)
        os.replace(tmp, self.path)

    def epoch_done(self, ep, loss, sacc, racc, secs):
        r = f'{racc * 100:.2f}%' if racc is not None else '-'
        self.rows.append(f'<tr><td>{ep + 1}</td><td>{loss:.4f}</td><td>{sacc * 100:.2f}%</td><td>{r}</td><td>{fmt_time(secs)}</td></tr>')


def load_shards(paths, extra=()):
    """Shards (and extra (x, w, y) sets) into one array, filled in place: no second copy of ~10 GB in memory."""
    metas = []
    for p in paths:
        d = np.load(p, mmap_mode='r')
        metas.append(len(d['w']))
    n = sum(metas) + sum(len(e[1]) for e in extra)
    x = np.zeros((n, H_IN, W_MAX, 3), np.uint8)
    w = np.zeros(n, np.int16)
    ys = []
    i = 0
    for p in paths:
        d = np.load(p)
        xi = d['x']
        k = len(xi)
        x[i:i + k, :, :xi.shape[2]] = xi[:, :, :W_MAX]
        w[i:i + k] = d['w']
        ys += list(d['y'])
        i += k
    for ex, ew, ey in extra:
        k = len(ex)
        x[i:i + k, :, :ex.shape[2]] = ex
        w[i:i + k] = ew
        ys += list(ey)
        i += k
    return x, w, ys


def targets(texts):
    enc = [[CHARSET.index(c) + 1 for c in t if c in CHARSET] for t in texts]
    lens = torch.tensor([len(e) for e in enc], dtype=torch.long)
    flat = torch.tensor([i for e in enc for i in e], dtype=torch.long)
    return flat, lens


def greedy(logits):
    """Best path decode: [B, T, C] -> list of strings."""
    best = logits.argmax(-1).cpu().numpy()
    out = []
    for row in best:
        s, prev = [], 0
        for k in row:
            if k != prev and k != 0:
                s.append(CHARSET[k - 1])
            prev = k
        out.append(''.join(s).strip())
    return out


def augment(x):
    """x: uint8 batch on the GPU [B, H, W, 3] -> float [B, 3, H, W] in -1..1, with photometric jitter."""
    x = x.permute(0, 3, 1, 2).float()
    B = x.shape[0]
    dev = x.device
    gain = torch.empty(B, 3, 1, 1, device=dev).uniform_(0.85, 1.15)
    bias = torch.empty(B, 1, 1, 1, device=dev).uniform_(-15, 15)
    mask = (x.sum(1, keepdim=True) > 0).float()          # keep the zero padding zero
    x = (x * gain + bias) * mask
    x = x + torch.randn_like(x) * torch.empty(B, 1, 1, 1, device=dev).uniform_(0, 4) * mask
    return x.clamp(0, 255) / 127.5 - 1.0


def evaluate(model, x, w, y, dev, bs=512):
    model.eval()
    preds = []
    with torch.no_grad():
        for i in range(0, len(x), bs):
            wb = min(W_MAX, (int(w[i:i + bs].max()) + 3) // 4 * 4 + 16)
            xb = torch.from_numpy(x[i:i + bs, :, :wb]).to(dev).permute(0, 3, 1, 2).float() / 127.5 - 1.0
            preds += greedy(model(xb))
    ok = sum(p == t for p, t in zip(preds, y))
    return ok / max(1, len(y)), preds


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('synth')
    ap.add_argument('out')
    ap.add_argument('--real', default=None)
    ap.add_argument('--real-weight', type=int, default=4)
    ap.add_argument('--val', default=None)
    ap.add_argument('--epochs', type=int, default=20)
    ap.add_argument('--bs', type=int, default=192)
    ap.add_argument('--lr', type=float, default=2e-3)
    ap.add_argument('--init', default=None)
    ap.add_argument('--last', action='store_true', help='keep the last epoch (when the validation set is not held out)')
    ap.add_argument('--skip-shards', type=int, default=0, help='leave out the oldest synthetic shards (memory)')
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    log = open(os.path.join(args.out, 'log.txt'), 'a')

    def say(s):
        print(s, flush=True)
        log.write(s + '\n'); log.flush()

    dev = 'cuda'
    torch.backends.cudnn.benchmark = True
    shards = sorted(glob.glob(os.path.join(args.synth, 'shard_*.npz')))[args.skip_shards:]
    extra = []
    if args.real:
        rx, rw, ry = load_shards(args.real.split(','))
        extra = [(rx, rw, ry)] * args.real_weight
        say(f'real training crops: {len(ry)} (x{args.real_weight})')
    x, w, y = load_shards(shards[:-1], extra)
    sx, sw, sy = load_shards(shards[-1:])           # synthetic held out
    sx, sw, sy = sx[:3000], sw[:3000], sy[:3000]
    vx = vw = vy = None
    if args.val:
        vx, vw, vy = load_shards([args.val])
    say(f'train {len(y)}  synth-val {len(sy)}  real-val {0 if vy is None else len(vy)}')

    model = CRNN().to(dev)
    if args.init:
        model.load_state_dict(torch.load(args.init, map_location=dev))
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    steps = args.epochs * (len(y) // args.bs)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=args.lr, total_steps=steps, pct_start=0.1)
    ctc = nn.CTCLoss(blank=0, zero_infinity=True)
    scaler = torch.amp.GradScaler()
    xt = torch.from_numpy(x)            # stays in (pinned) host memory; batches go to the GPU
    best = -1
    prog = Progress(args.out, args.epochs, len(y) // args.bs)
    say(f'progress page: {prog.path}')
    prog.update(0, 0, None, 'Starting')
    for ep in range(args.epochs):
        model.train()
        perm = torch.randperm(len(y))
        t0, tot, nb = time.time(), 0.0, 0
        prog.epoch_start()
        last_loss = None
        for i in range(0, len(y) - args.bs + 1, args.bs):
            idx = perm[i:i + args.bs]
            # Only as wide as this batch's widest line plus a margin: the app runs the model on each line at its own
            # width (faster than padding every line to W_MAX), so the model must not depend on a long blank tail.
            wb = min(W_MAX, (int(w[idx.numpy()].max()) + 3) // 4 * 4 + 16)
            xb = augment(xt[idx][:, :, :wb].to(dev, non_blocking=True))
            flat, lens = targets([y[j] for j in idx.tolist()])
            with torch.autocast('cuda', dtype=torch.float16):     # (bf16 LSTMs are several times slower here)
                logits = model(xb)
            lp = F.log_softmax(logits.float(), -1).permute(1, 0, 2)
            il = torch.full((lp.shape[1],), lp.shape[0], dtype=torch.long)
            loss = ctc(lp, flat, il, lens)
            opt.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.unscale_(opt)
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            scaler.step(opt)
            scaler.update()
            sched.step()
            if nb % 50 == 0:
                last_loss = loss.item()
                tot += last_loss
                prog.update(ep, nb, last_loss)
            nb += 1
        sacc, _ = evaluate(model, sx, sw, sy, dev)
        msg = f'epoch {ep + 1}/{args.epochs} loss {tot / max(1, (nb + 49) // 50):.4f} synth-val {sacc:.4f}'
        score = sacc
        if vx is not None:
            racc, preds = evaluate(model, vx, vw, vy, dev)
            msg += f' real-val {racc:.4f}'
            score = racc + sacc * 1e-3
        say(msg + f' ({time.time() - t0:.0f}s)')
        prog.epoch_done(ep, tot / max(1, (nb + 49) // 50), sacc, score if vx is not None else None, time.time() - t0)
        prog.update(ep + 1, 0, last_loss, 'Training' if ep + 1 < args.epochs else 'Exporting')
        if score >= best or args.last:
            best = score
            torch.save(model.state_dict(), os.path.join(args.out, 'model.pt'))

    model.load_state_dict(torch.load(os.path.join(args.out, 'model.pt'), map_location=dev))
    if vx is not None:
        racc, preds = evaluate(model, vx, vw, vy, dev)
        with open(os.path.join(args.out, 'val_misses.txt'), 'w', encoding='utf-8') as f:
            for i, (p, t) in enumerate(zip(preds, vy)):
                if p != t:
                    f.write(f'{i}\t{p}\t{t}\n')
        say(f'best real-val {racc:.4f}')
    export(model.cpu().eval(), os.path.join(args.out, 'namenet.onnx'))
    say('exported')
    prog.update(args.epochs, 0, None, 'Done')


def export(model, path):
    ex = Exported(model).eval()
    dummy = torch.zeros(1, 3, H_IN, W_MAX)
    torch.onnx.export(ex, dummy, path, input_names=['x'], output_names=['p'], opset_version=17,
                      dynamic_axes={'x': {0: 'b', 3: 'w'}, 'p': {0: 'b', 1: 't'}}, dynamo=False)


if __name__ == '__main__':
    main()

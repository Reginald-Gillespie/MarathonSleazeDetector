"""
Synthetic training lines for the name reader.

Random names are drawn in the game's own HUD font (PP Fraktion Mono Bold, cached by the app from the install), in the
HUD's name colours with a drop shadow, over real scenery cut from recordings. Most are then pushed through a real
H.264 encode / decode at recording bitrates (the 4:2:0 colour loss and block artefacts that make names hard to read in
clips); the rest stay clean, as the live game shows them. Each line is cut out the way the app cuts it (colour box
plus a margin) and stored at the model's input height.

    python gen.py out_dir shards [--clean-frac 0.25] [--seed N]

Writes out_dir/shard_XXX.npz: x uint8 [n, 32, 320, 3] (left aligned, zero padded), w int16 [n] (used width),
y str [n] (text).
"""
import argparse
import glob
import os
import random
import subprocess
import sys

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from common import H_IN, W_MAX, crop_box, to_input

FONT = os.path.join(os.environ['APPDATA'], 'MarathonSleazeDetector', 'cache', 'hudfont.otf')
SS = 4                      # supersampling of the glyph rendering
CAP_EM = 0.682              # cap height / em of the font
ADV_EM = 0.600              # advance / em (monospaced)
LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
DIGITS = '0123456789'

_fonts = {}


def font(px):
    if px not in _fonts:
        _fonts[px] = ImageFont.truetype(FONT, px)
    return _fonts[px]


def rand_word(lo, hi):
    return ''.join(random.choice(LETTERS) for _ in range(random.randint(lo, hi)))


def rand_name(prox):
    r = random.random()
    if r < 0.08:
        s = 'UESC ' + rand_word(4, 9) + (' ' + rand_word(4, 8) if random.random() < 0.4 else '')
    elif r < 0.45:
        s = rand_word(3, 12)
        if random.random() < 0.4:
            s += ''.join(random.choice(DIGITS) for _ in range(random.randint(1, 4)))
        if random.random() < 0.15:
            i = random.randint(1, len(s) - 1)
            s = s[:i] + random.choice('_ ') + s[i:]
    else:
        pool = LETTERS * 6 + DIGITS * 3 + '_' * 2 + '-.' + ' '
        n = random.randint(3, 15)
        s = ''.join(random.choice(pool) for _ in range(n))
        s = s.strip()
        while '  ' in s:
            s = s.replace('  ', ' ')
        if len(s) < 3:
            s = rand_word(3, 8)
    if random.random() < 0.04:          # censored part of a name
        k = random.randint(1, min(6, len(s)))
        s = '*' * k + s[k:]
    if random.random() < 0.08:          # long names run to 30+ characters
        s = (s + random.choice(['', '_', ' ']) + rand_word(6, 16))[:32]
    if prox and random.random() < 0.85:
        return s[:15] + '#' + ''.join(random.choice(DIGITS) for _ in range(4))
    r = random.random()
    if r < 0.15:                        # a pinged name: the ping's red '+' comes first
        s = '+ ' + s[:28]
    elif r < 0.18:                      # two names joined by a '+' (a squad ping between them)
        s = s[:14] + '+' + rand_word(4, 12)
    return s[:32]


def ink_colour():
    """(colour, is read). The app reads enemy names, drawn in red; the reader is taught to read nothing in any other
    HUD text colour - squad lime, white, grey, yellow - which the colour box can take in when a screen effect tints
    everything (an orange flash makes lime and white text look reddish)."""
    r = random.random()
    if r < 0.80:    # enemy red (pinkish in the HUD)
        return np.array([random.uniform(225, 255), random.uniform(25, 95), random.uniform(55, 125)]), True
    if r < 0.90:    # squad lime
        return np.array([random.uniform(170, 215), random.uniform(225, 255), random.uniform(20, 70)]), False
    if r < 0.97:    # white / grey HUD text
        v = random.uniform(170, 255)
        return np.array([v, v, v * random.uniform(0.95, 1.05)]), False
    return np.array([random.uniform(220, 255), random.uniform(190, 230), random.uniform(40, 110)]), False


class Backgrounds:
    def __init__(self, folder):
        self.files = sorted(glob.glob(os.path.join(folder, '*.jpg')))
        self.cache = {}

    def patch(self, w, h):
        f = random.choice(self.files)
        if f not in self.cache:
            if len(self.cache) > 200:
                self.cache.pop(next(iter(self.cache)))
            self.cache[f] = cv2.cvtColor(cv2.imread(f), cv2.COLOR_BGR2RGB)
        im = self.cache[f]
        # Sometimes rescaled (scenery at other resolutions / distances) or flipped.
        if random.random() < 0.3:
            s = random.uniform(0.5, 2.0)
            im = cv2.resize(im, None, fx=s, fy=s, interpolation=cv2.INTER_LINEAR)
        if im.shape[0] < h + 1 or im.shape[1] < w + 1:
            im = cv2.resize(im, (max(w + 1, im.shape[1]), max(h + 1, im.shape[0])))
        y = random.randint(0, im.shape[0] - h)
        x = random.randint(0, im.shape[1] - w)
        p = im[y:y + h, x:x + w].astype(np.float32)
        if random.random() < 0.5:
            p = p[:, ::-1]
        # Brightness / contrast / tint.
        p = p * random.uniform(0.6, 1.35) + random.uniform(-25, 25)
        p = p * np.array([random.uniform(0.85, 1.15) for _ in range(3)], np.float32)
        return np.clip(p, 0, 255)


def coverage(text, pitch, cap, ox, oy, w, h, speaker=None):
    """Glyph coverage (0..1) of the text on a w x h canvas, first cell at (ox, baseline oy), sub-pixel accurate."""
    em = cap / CAP_EM
    f = font(max(4, int(round(em * SS))))
    img = Image.new('L', (w * SS, h * SS), 0)
    d = ImageDraw.Draw(img)
    for i, c in enumerate(text):
        if c == ' ':
            continue
        # Each character sits on the monospaced grid; the font's own advance is pitch only up to tracking.
        x = (ox + i * pitch + (pitch - ADV_EM * em) / 2) * SS
        d.text((x, oy * SS), c, font=f, fill=255, anchor='ls')
    if speaker is not None:
        # Prox chat's speaker icon after the tag: a small box and cone, then one or two sound arcs.
        x0, s_ = speaker * SS, cap * SS * 1.2
        top, bot = oy * SS - s_ * 0.85, oy * SS - s_ * 0.05
        mid = (top + bot) / 2
        d.rectangle([x0, mid - s_ * 0.17, x0 + s_ * 0.22, mid + s_ * 0.17], fill=255)
        d.polygon([(x0 + s_ * 0.15, mid - s_ * 0.17), (x0 + s_ * 0.5, top), (x0 + s_ * 0.5, bot), (x0 + s_ * 0.15, mid + s_ * 0.17)], fill=255)
        lw = max(1, int(s_ * 0.1))
        for k in range(random.randint(1, 2)):
            r = s_ * (0.22 + 0.18 * k)
            d.arc([x0 + s_ * 0.5 - r, mid - r, x0 + s_ * 0.5 + r, mid + r], -50, 50, fill=255, width=lw)
    a = np.asarray(img, np.float32).reshape(h, SS, w, SS).mean(axis=(1, 3)) / 255.0
    return a


def shift_blur(m, dx, dy, sigma):
    M = np.float32([[1, 0, dx], [0, 1, dy]])
    s = cv2.warpAffine(m, M, (m.shape[1], m.shape[0]), flags=cv2.INTER_LINEAR)
    if sigma > 0.05:
        s = cv2.GaussianBlur(s, (0, 0), sigma)
    return s


def make_line(bgs, frame_h, prox, text=None):
    """One name drawn over scenery, with context around it. Returns (tile RGB float, label, crop box in the tile)."""
    text = text or rand_name(prox)
    scale = frame_h / 1200.0
    pitch = (9.3 if prox else 10.3) * scale * random.uniform(0.975, 1.025)
    cap = pitch * random.uniform(1.07, 1.16)
    cap_ref = 13.0 * scale
    n = len(text)
    pad_x, pad_y = int(cap_ref * 3), int(cap_ref * 1.2)
    speaker = prox and '#' in text and random.random() < 0.7
    w = int(n * pitch + 2 * pad_x + (pitch * 2.5 if speaker else 0))
    h = int(cap * 1.6 + 2 * pad_y)
    ox = pad_x + random.random()
    oy = pad_y + cap * 1.05 + random.random()
    cov = coverage(text, pitch, cap, ox, oy, w, h,
                   speaker=ox + n * pitch + pitch * random.uniform(0.5, 1.1) if speaker else None)
    bg = bgs.patch(w, h)
    # Drop shadow: a dark, slightly blurred copy down and to the right.
    sh = shift_blur(cov, random.uniform(0.3, 1.6) * scale, random.uniform(0.3, 1.6) * scale, random.uniform(0.0, 1.0) * scale)
    sh_col = np.array([random.uniform(20, 90), random.uniform(5, 45), random.uniform(40, 120)], np.float32)
    sh_a = random.uniform(0.3, 0.9)
    out = bg * (1 - sh_a * sh[..., None]) + sh_col * (sh_a * sh[..., None])
    ink, is_name = ink_colour()
    ink = ink.astype(np.float32)
    if random.random() < 0.03:      # no text at all: scenery the colour box picked up
        is_name, a_scale = False, 0.0
    else:
        a_scale = 1.0
    a = cov * random.uniform(0.85, 1.0) * a_scale
    if random.random() < 0.3:   # a faint glow around the letters
        g = cv2.GaussianBlur(cov, (0, 0), random.uniform(0.8, 2.0) * scale) * random.uniform(0.1, 0.35)
        out = out * (1 - g[..., None]) + ink * g[..., None]
    out = out * (1 - a[..., None]) + ink * a[..., None]
    if random.random() < 0.08:      # a screen effect washing everything in one colour (orange flash, red damage)
        wash = np.array(random.choice([(230, 110, 30), (220, 60, 40), (240, 150, 60)]), np.float32)
        k = random.uniform(0.25, 0.55)
        out = out * (1 - k) + wash * k
    # Other HUD bits next to the name (icons, white words): never in the name colour, so outside the colour box.
    for side in (-1, 1):
        if random.random() < 0.3:
            c = random.choice('>#+0OI<' + LETTERS)
            dc = coverage(c, pitch, cap, 0, oy, int(pitch * 2), h)
            col = np.array(random.choice([(235, 235, 235), (240, 170, 60), (120, 170, 255), (200, 200, 200)]), np.float32)
            x0 = int(ox - pitch * random.uniform(1.6, 2.6)) if side < 0 else int(ox + (n + (2.2 if speaker else 0)) * pitch + pitch * random.uniform(0.6, 1.6))
            x0 = max(0, min(w - dc.shape[1], x0))
            reg = out[:, x0:x0 + dc.shape[1]]
            reg[:] = reg * (1 - dc[..., None]) + col * dc[..., None]
    # The app's box: where the name colour is, i.e. the ink (coverage over ~0.35), give or take a pixel.
    ys, xs = np.nonzero(cov > random.uniform(0.25, 0.45))
    if len(xs) == 0:
        return None
    box = [xs.min() + random.randint(-1, 1), ys.min() + random.randint(-1, 1),
           xs.max() + 1 + random.randint(-1, 1), ys.max() + 1 + random.randint(-1, 1)]
    return out, (text if is_name else ''), box


def encode_roundtrip(frames, w, h, rng):
    """Encodes frames (list of HxWx3 uint8 RGB) with H.264 4:2:0 at a recording bitrate and decodes them back."""
    mbps = rng.uniform(6, 30) * (w * h) / (1920 * 1200)
    enc = rng.choice(['libx264', 'h264_nvenc', 'hevc_nvenc'])
    tmp = os.path.join(os.environ.get('TEMP', '.'), f'nr_{os.getpid()}_{rng.randint(0, 1 << 30)}.mp4')
    args = ['ffmpeg', '-v', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{w}x{h}', '-r', '30', '-i', '-',
            '-c:v', enc, '-b:v', f'{mbps:.1f}M', '-maxrate', f'{mbps * 1.5:.1f}M', '-bufsize', f'{mbps:.1f}M',
            '-pix_fmt', 'yuv420p', '-g', '60', tmp]
    if enc == 'libx264':
        args[args.index('-pix_fmt', 12):args.index('-pix_fmt', 12)] = ['-preset', 'veryfast']
    p = subprocess.run(args, input=b''.join(f.tobytes() for f in frames), capture_output=True)
    if p.returncode != 0:
        if enc != 'libx264':
            return encode_roundtrip(frames, w, h, random.Random(0))   # no NVENC: fall back
        raise RuntimeError(p.stderr.decode())
    d = subprocess.run(['ffmpeg', '-v', 'error', '-i', tmp, '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'], capture_output=True)
    os.remove(tmp)
    raw = np.frombuffer(d.stdout, np.uint8)
    return raw.reshape(-1, h, w, 3)


def video_batch(bgs, rng, out):
    """One clip of tiled lines: text static, scenery drifting, as in a recording. Appends samples to out."""
    frame_h = rng.choice([1050, 1080, 1200, 1200, 1200, 1440, 1600, 1800])
    frame_w = int(frame_h * 1.6) & ~1
    frame_h &= ~1
    # Rows of lines, each row as high as its tallest line.
    placed = []
    y = rng.randint(0, 6)
    while True:
        row, x, rh = [], rng.randint(0, 30), 0
        while True:
            t = make_line(bgs, frame_h, rng.random() < 0.35)
            if t is None:
                continue
            tw = t[0].shape[1]
            if x + tw > frame_w:
                break
            row.append((t[0], t[1], t[2], x, y))
            rh = max(rh, t[0].shape[0])
            x += tw + rng.randint(0, 20)
        if y + rh > frame_h or not row:
            break
        placed += [p for p in row]
        y += rh
    nframes = 6
    drift = [(rng.uniform(-2, 2), rng.uniform(-1, 1)) for _ in placed]
    frames = []
    for k in range(nframes):
        fr = np.zeros((frame_h, frame_w, 3), np.float32)
        fr[:] = 40
        for (tile, text, box, x, y), (vx, vy) in zip(placed, drift):
            th, tw = tile.shape[:2]
            M = np.float32([[1, 0, vx * k], [0, 1, vy * k]])
            # Scenery moves, the text does not: move the whole tile, then put the original text pixels back.
            moved = cv2.warpAffine(tile, M, (tw, th), borderMode=cv2.BORDER_REFLECT)
            fr[y:y + th, x:x + tw] = moved if k else tile
        frames.append(np.clip(fr, 0, 255).astype(np.uint8))
    # (The whole tile drifts a pixel or two, text included: close enough to a still HUD over a moving scene.)
    dec = encode_roundtrip(frames, frame_w, frame_h, rng)
    for k in (1, 3, 5):
        f = dec[k]
        for (tile, text, box, x, y), (vx, vy) in zip(placed, drift):
            dx, dy = vx * k, vy * k
            b = [box[0] + dx, box[1] + dy, box[2] + dx, box[3] + dy]
            b = [int(round(v)) for v in b]
            img = crop_box(f, x + b[0], y + b[1], b[2] - b[0], b[3] - b[1], frame_h, rng)
            out.append((to_input(img, rng), text))


def clean_batch(bgs, rng, out, n=64):
    for _ in range(n):
        frame_h = rng.choice([1080, 1200, 1440, 1600, 1800, 1800, 2160])
        t = make_line(bgs, frame_h, rng.random() < 0.35)
        if t is None:
            continue
        tile, text, box = t
        img = np.clip(tile + np.random.normal(0, rng.uniform(0, 3), tile.shape), 0, 255).astype(np.uint8)
        img = crop_box(img, box[0], box[1], box[2] - box[0], box[3] - box[1], frame_h, rng)
        out.append((to_input(img, rng), text))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('shards', type=int)
    ap.add_argument('--start', type=int, default=0)
    ap.add_argument('--per-shard', type=int, default=8000)
    ap.add_argument('--clean-frac', type=float, default=0.25)
    ap.add_argument('--bg', default=None)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    bgs = Backgrounds(args.bg)
    for s in range(args.start, args.start + args.shards):
        path = os.path.join(args.out, f'shard_{s:03d}.npz')
        if os.path.exists(path):
            continue
        rng = random.Random(1000 + s)
        random.seed(2000 + s)
        np.random.seed(3000 + s)
        out = []
        while len(out) < args.per_shard:
            if rng.random() < args.clean_frac:
                clean_batch(bgs, rng, out)
            else:
                video_batch(bgs, rng, out)
        out = out[:args.per_shard]
        x = np.zeros((len(out), H_IN, W_MAX, 3), np.uint8)
        w = np.zeros(len(out), np.int16)
        for i, (im, _) in enumerate(out):
            x[i, :, :im.shape[1]] = im
            w[i] = im.shape[1]
        np.savez(path, x=x, w=w, y=np.array([t for _, t in out]))
        print(path, flush=True)


if __name__ == '__main__':
    main()

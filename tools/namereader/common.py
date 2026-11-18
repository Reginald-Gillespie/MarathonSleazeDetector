"""Shared by the generator, the dataset tools and training: how a name box is cut out and scaled for the model.
The app (Ocr/NameNet.cs) does exactly the same: box plus a margin, then scaled to H_IN pixels high."""
import cv2
import numpy as np

H_IN = 32           # model input height
W_MAX = 448         # widest input (the longest names, ~33 characters, are about 400)
CHARSET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_#-.*+ '   # CTC classes 1..len; 0 is blank
# TODO: so Marathon names can have a lot more than this... I'm hoping that chars like this are either read consistently enough as something else that it still looks up properly, otherwise and regardless I'll probably train it on more chars later since this worked so well.

def margins(frame_h):
    cap_ref = 13.0 / 1200.0 * frame_h
    return int(round(cap_ref * 1.2)), int(round(cap_ref * 0.6))


def crop_box(frame, x, y, w, h, frame_h, rng=None):
    """The name box (x, y, w, h) plus the app's margin, from a frame (or a tile) as RGB uint8."""
    mx, my = margins(frame_h)
    if rng is not None:   # the app's colour box is never exact
        mx += rng.randint(-1, 1)
        my += rng.randint(-1, 1)
    x0, y0 = max(0, x - mx), max(0, y - my)
    x1, y1 = min(frame.shape[1], x + w + mx), min(frame.shape[0], y + h + my)
    return np.ascontiguousarray(frame[y0:y1, x0:x1])


def to_input(img, rng=None):
    """Scales a crop to the model's input height (width in proportion, at most W_MAX)."""
    h, w = img.shape[:2]
    nw = max(8, min(W_MAX, int(round(w * H_IN / h))))
    # Without augmentation: pixel-area averaging when shrinking, bilinear when enlarging (what NameNet.cs does).
    interp = (cv2.INTER_AREA if h > H_IN else cv2.INTER_LINEAR) if rng is None else         rng.choice([cv2.INTER_AREA, cv2.INTER_LINEAR, cv2.INTER_CUBIC])
    if rng is not None and rng.random() < 0.25:
        # Played full screen on a bigger display (Movies & TV at 2726 x 1704): upscaled before the app sees it.
        s = rng.uniform(1.2, 1.6)
        img = cv2.resize(img, (int(w * s), int(h * s)), interpolation=cv2.INTER_CUBIC)
    return cv2.resize(img, (nw, H_IN), interpolation=interp)


def encode(text):
    return [CHARSET.index(c) + 1 for c in text]

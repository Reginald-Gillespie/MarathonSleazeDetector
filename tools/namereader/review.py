"""
Contact sheets for labelling by eye: each track's middle full crop, enlarged, with the model's reading (and the app's)
printed beside it and a number to refer to it.

    python review.py pred.tsv data/names out_dir [--min-crops 3] [--per 16]

Writes out_dir/sheet_NNN.png and out_dir/index.tsv (sheet, number, clip, track, model reading, app reading).
"""
import argparse
import os

import cv2
import numpy as np

import real


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('pred')
    ap.add_argument('root')
    ap.add_argument('out')
    ap.add_argument('--min-crops', type=int, default=3)
    ap.add_argument('--per', type=int, default=16)
    ap.add_argument('--skip-labelled', action='store_true')
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    labels = real.load_labels(args.root)
    items = []
    for r in real.read_tsv(args.pred):
        c, t, app, model, share, n = r[0], int(r[1]), r[2], r[3], float(r[4]), int(r[5])
        if n < args.min_crops or len(model.replace('*', '').replace('-', '').strip()) < 3:
            continue
        if args.skip_labelled and labels.get((c, t), ('', 'auto'))[1] != 'auto':
            continue
        items.append((c, t, model, app, share))
    reads = {}
    index = []
    for s in range(0, len(items), args.per):
        tiles = []
        for k, (c, t, model, app, share) in enumerate(items[s:s + args.per]):
            if c not in reads:
                reads = {c: real.track_reads(args.root, c)}
            im, _ = real.middle_crop(args.root, c, reads[c][t])
            im = cv2.resize(im, None, fx=3, fy=3, interpolation=cv2.INTER_CUBIC)[:, :900]
            tile = np.full((im.shape[0] + 34, 960, 3), 25, np.uint8)
            tile[2:2 + im.shape[0], 50:50 + im.shape[1]] = im
            cv2.putText(tile, str(k), (6, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2)
            cv2.putText(tile, f'model: {model}   ({share:.2f})    app: {app}', (50, im.shape[0] + 26),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (120, 255, 120), 1)
            tiles.append(tile)
            index.append(f'{s // args.per}\t{k}\t{c}\t{t}\t{model}\t{app}')
        cv2.imwrite(os.path.join(args.out, f'sheet_{s // args.per:03d}.png'), cv2.cvtColor(np.vstack(tiles), cv2.COLOR_RGB2BGR))
    with open(os.path.join(args.out, 'index.tsv'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(index) + '\n')
    print(len(items), 'tracks on', (len(items) + args.per - 1) // args.per, 'sheets')


if __name__ == '__main__':
    main()

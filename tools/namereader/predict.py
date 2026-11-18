"""
Reads every harvested track with a trained model and proposes labels.

    python predict.py model.pt data/names out.tsv

out.tsv: clip, track, app's name, model's majority reading over the track's full crops, share of crops agreeing,
number of crops. Used to label: where the model agrees with itself across a track (and with the app) the label is
taken; the rest is looked at by eye (real.py sheets).
"""
import os
import sys
from collections import Counter

import cv2
import numpy as np
import torch

import real
import train
from common import H_IN, W_MAX, to_input


def main():
    model_path, root, out = sys.argv[1:4]
    m = train.CRNN()
    m.load_state_dict(torch.load(model_path, map_location='cpu'))
    m = m.cuda().eval()
    rows_out = []
    for c in real.clips(root):
        names = {int(r[0]): (r[4] if len(r) > 4 else '') for r in real.read_tsv(os.path.join(root, c, 'tracks.tsv'))}
        for t, rows in sorted(real.track_reads(root, c).items()):
            rows = real.full_rows(rows)
            x = np.zeros((len(rows), H_IN, W_MAX, 3), np.uint8)
            for i, r in enumerate(rows):
                im = to_input(cv2.cvtColor(cv2.imread(os.path.join(root, c, r[0])), cv2.COLOR_BGR2RGB))
                x[i, :, :im.shape[1]] = im
            with torch.no_grad():
                xb = torch.from_numpy(x).cuda().permute(0, 3, 1, 2).float() / 127.5 - 1.0
                preds = train.greedy(m(xb))
            top, k = Counter(preds).most_common(1)[0]
            rows_out.append(f'{c}\t{t}\t{names.get(t, "")}\t{top}\t{k / len(preds):.2f}\t{len(preds)}')
    with open(out, 'w', encoding='utf-8') as f:
        f.write('\n'.join(rows_out) + '\n')
    print(len(rows_out), 'tracks')


if __name__ == '__main__':
    main()

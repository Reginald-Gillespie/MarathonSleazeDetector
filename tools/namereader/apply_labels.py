"""
Writes labels decided on review sheets into data/names/labels.tsv.

    python apply_labels.py review_dir data/names decisions.txt [status]

decisions.txt, one line per sheet:   <sheet>: ok | <n>=<LABEL> ; <n>=- ; ...
Every track on a listed sheet takes the model's reading unless overridden; '-' marks a track that is not a name.
Status written: 'checked' (default).
"""
import sys

import real


def main():
    rev, root, dec = sys.argv[1:4]
    status = sys.argv[4] if len(sys.argv) > 4 else 'checked'
    index = {}
    for r in real.read_tsv(f'{rev}/index.tsv'):
        index[(int(r[0]), int(r[1]))] = (r[2], int(r[3]), r[4])
    labels = real.load_labels(root)
    n = 0
    for line in open(dec, encoding='utf-8'):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        sheet, rest = line.split(':', 1)
        sheet = int(sheet)
        over = {}
        for part in rest.split(';'):
            part = part.strip()
            if '=' in part:
                k, v = part.split('=', 1)
                over[int(k)] = v.strip()
        for (s, k), (c, t, model) in index.items():
            if s != sheet:
                continue
            labels[(c, t)] = (over.get(k, model), status)
            n += 1
    real.save_labels(root, labels)
    print(n, 'labels written')


if __name__ == '__main__':
    main()

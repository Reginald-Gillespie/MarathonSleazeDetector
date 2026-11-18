# Name reader training

The app reads kill feed and prox chat names with a small neural network (`src/SleazeDetector/Assets/namenet.onnx`,
run by `Ocr/NameNet.cs` through ONNX Runtime on the CPU). This folder holds everything to rebuild it.

## Setup

Python 3.13 with a CUDA GPU:

```
python -m venv venv
venv\Scripts\python -m pip install torch --index-url https://download.pytorch.org/whl/cu126
venv\Scripts\python -m pip install numpy pillow opencv-python-headless onnx onnxscript
```

`ffmpeg` must be on the PATH. The HUD font is read from the app's cache
(`%AppData%\MarathonSleazeDetector\cache\hudfont.otf`), which the app writes on its first start.

## 1. Real names from recordings

```
SleazeDetector --harvest data\names <clip.mp4 | folder> ...
```

For each clip it writes `data\names\<clip>\` with one small PNG per read of a kill feed or prox name (the colour box plus
a margin, exactly what the app reads), `reads.tsv` and `tracks.tsv`. A clip is a few hundred KB of crops; the
recordings themselves can be deleted afterwards. Clips already harvested are skipped.

## 2. Labels

`data\names\labels.tsv`: clip, track, label, status. `-` = not a name (the model learns to read nothing there), `?` =
leave out. Status `checked` (looked at), `user` (confirmed by you), `auto` (unchecked, not used).

```
python predict.py model.pt data\names pred.tsv          # the model's reading of every track
python review.py pred.tsv data\names sheets\ --skip-labelled
python apply_labels.py sheets\ data\names decisions.txt  # "<sheet>: <n>=<LABEL>; <n>=-" per sheet
python real.py html data\names labels.html               # a page to check / correct labels in a browser
```

The label is the text as it shows, including the red `+` of a ping (`+ RUNNER`) and two names joined by one
(`RUNNER+OTHER`); the app cleans those up. A prox label leaves out the speaker icon.

## 3. Synthetic names

```
python gen.py synth\ 24 --bg backgrounds\
```

Random names in the HUD font, in the name colours with a drop shadow, over scenery from recordings (`backgrounds\`:
JPEGs of the left part of the screen, e.g. `ffmpeg -i clip.mp4 -vf "fps=1/45,crop=iw*0.42:ih*0.66:0:ih*0.22"`).
Three quarters go through a real H.264 / HEVC encode at recording bitrates (what makes clips hard to read), the rest
stay clean like the live game. 8000 lines per shard.

## 4. Training

```
python real.py build data\names real\                    # real_train.npz / real_val.npz (split by clip)
python train.py synth\ out\ --real real\real_train.npz --val real\real_val.npz --epochs 12
copy out\namenet.onnx ..\..\src\SleazeDetector\Assets\
```

`model.pt` here is the shipped model's weights: start new training from it with `--init model.pt` (a few passes
with the new labels mixed in), rather than from scratch. While training runs, `out\progress.html` shows its progress
(open it in a browser; it refreshes itself).

The model reads red (enemy) text only. Lime squad names, white and yellow HUD text are trained as "nothing to
read", because a screen effect (an orange flash) can make them look reddish to the app's colour box.

`SleazeDetector --read-test corpus.txt report.txt net [model.onnx]` measures the app's own reading with a model.

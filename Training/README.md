# Training

Training config for the Slither-style snake (Unity ML-Agents).

The Python environment (`venv`), the built game (`Build`) and the training
results (`results`) live outside this repository, in:

```
C:\Users\YH801\Unity\RL-Train_Scripts\1.snake-rl-scripts\
```

## Versions

- Unity 6000.5.7f1 (Universal 2D), ML-Agents package 4.1.0
- Python 3.10, mlagents 1.1.0, torch 2.8.0+cu128
  (torch 2.9+ needs `onnxscript` for ONNX export and does not work with mlagents 1.1.0)

## Setup (first time only)

```powershell
cd C:\Users\YH801\Unity\RL-Train_Scripts\1.snake-rl-scripts
py install 3.10
py -3.10 -m venv venv
.\venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install torch==2.8.0 --index-url https://download.pytorch.org/whl/cu128
pip install mlagents
```

## Train (headless build, fastest)

1. Unity: File > Build Profiles > Windows > Build, output to
   `C:\Users\YH801\Unity\RL-Train_Scripts\1.snake-rl-scripts\Build\`
   (rebuild after every code / scene change)
2. Run:

```powershell
cd C:\Users\YH801\Unity\RL-Train_Scripts\1.snake-rl-scripts
.\venv\Scripts\Activate.ps1
mlagents-learn "..\..\1. snake-RL\Training\slither_ppo.yaml" --run-id=slither_v1 --env="Build\1. snake-RL.exe" --no-graphics --num-envs=4
# add --resume to continue, --force to overwrite
```

## Train in the Editor (for quick checks)

```powershell
mlagents-learn "..\..\1. snake-RL\Training\slither_ppo.yaml" --run-id=slither_test
```

Then press Play in Unity.

## Watch progress

```powershell
tensorboard --logdir results
```

Open http://localhost:6006 and check `Environment/Cumulative Reward` and `Slither/Length`.

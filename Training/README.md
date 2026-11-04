# Training（学習）

Unity ML-Agents でヘビを学習させるための設定とコマンドです。セットアップの手順は、リポジトリ直下の README の「別のPCでのセットアップ」を見てください。

## このフォルダ

| ファイル・フォルダ | 中身 |
|---|---|
| `slither_ppo.yaml` | 1種類のヘビを学習する PPO の設定（`v1-single`・`v2-multi`） |
| `slither_personalities.yaml` | 穏やか・攻撃的の2つの性格を同時に学習する設定（`v4-personality` だけにある） |
| `venv/` | Python の環境 |
| `Build/` | 学習用に書き出したゲーム（exe） |
| `results/` | 学習の結果（モデルと TensorBoard のログ） |

`venv/`・`Build/`・`results/` は各 PC で作るもので、Git には入れていません。ブランチを切り替えても共通のまま残ります。

## 動作環境

- Unity 6000.5.7f1（Universal 2D）、ML-Agents パッケージ 4.1.0
- Python 3.10、mlagents 1.1.0、PyTorch 2.8.0 + CUDA 12.8
  （PyTorch 2.9 以降は ONNX の書き出しに `onnxscript` が要り、mlagents 1.1.0 では動かない）

## 学習する

Unity で **Slither → Build Training Exe**（Ctrl + Alt + B）を押して `Build\` に書き出してから、このフォルダで実行します。スクリプトやシーンを変えたら、毎回書き出し直します。

```powershell
.\venv\Scripts\Activate.ps1

# v1-single・v2-multi
mlagents-learn slither_ppo.yaml --run-id=slither_v1 --env="Build\1. snake-RL.exe" --no-graphics --num-envs=4

# v4-personality
mlagents-learn slither_personalities.yaml --run-id=slither_v4 --env="Build\1. snake-RL.exe" --no-graphics --num-envs=4

# 続きから：--resume ／ 最初からやり直す：--force
```

エディタで動きを見ながら学習するときは、`--env` 以降を付けずに実行して、表示が出たら Unity で ▶ を押します。

## 進み具合を見る

```powershell
tensorboard --logdir results
```

http://localhost:6006 を開いて、次の値を見ます。

| 値 | 意味 |
|---|---|
| `Environment/Cumulative Reward` | 1回（死ぬまで）でもらった報酬の合計 |
| `Environment/Episode Length` | 1回で生き残ったステップ数 |
| `Slither/Length` | 死んだときの体の長さ |
| `Slither/Calm/Length`・`Slither/Aggressive/Length` | 性格ごとの体の長さ（v4） |
| `Slither/Calm/Kills`・`Slither/Aggressive/Kills` | 性格ごとに相手を倒した数（1万ステップごとの合計、v4） |

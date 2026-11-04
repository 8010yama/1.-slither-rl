"""
Unity を起動せずにできる、プロジェクトの確認（GitHub Actions で push のたびに実行する）

1. Unity の .meta ファイル：アセットとその .meta が、必ずペアで Git に入っているか
2. 学習済みモデル（.onnx）：観測の数と行動の形が、シーンの Behavior Parameters と合っているか
   （コードを変えて観測の数が変わったのに、古いモデルのまま、などを見つける）
3. シーンが使っているモデル：参照しているモデルのファイルが、リポジトリにあるか
4. 学習の設定（Training/*.yaml）：ML-Agents が読めるか（書き間違えた項目名などを見つける）、
   behaviors に書いた名前がシーンにあるか

使い方（リポジトリの一番上で）:
    python ci/check_project.py
"""

import re
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCENE = ROOT / "Assets" / "Scenes" / "SampleScene.unity"

errors: list[str] = []


def ok(message: str) -> None:
    print(f"  ✓ {message}")


def note(message: str) -> None:
    print(f"  - {message}")


def fail(message: str) -> None:
    print(f"  ✗ {message}")
    errors.append(message)


def tracked_files() -> list[str]:
    out = subprocess.run(["git", "ls-files", "Assets"], cwd=ROOT, capture_output=True, text=True, check=True)
    return [line for line in out.stdout.splitlines() if line]


# ---------------- 1. .meta ファイル ----------------

def check_meta_files() -> None:
    print("1. Unity の .meta ファイル")
    files = set(tracked_files())
    # Git はフォルダを持たないので、ファイルのパスからフォルダを作る（Assets 自体には .meta がない）
    folders = {str(Path(f).parent).replace("\\", "/") for f in files}
    folders = {d for d in folders if d != "Assets"}
    assets = {f for f in files if not f.endswith(".meta")} | folders
    metas = {f[: -len(".meta")] for f in files if f.endswith(".meta")}

    missing_meta = sorted(assets - metas)
    orphan_meta = sorted(metas - assets)
    for f in missing_meta:
        fail(f"{f} の .meta がありません（Unity で開くと GUID が変わり、参照が外れます）")
    for f in orphan_meta:
        fail(f"{f}.meta だけがあり、本体がありません")
    if not missing_meta and not orphan_meta:
        ok(f"アセット {len(assets)} 個すべてに .meta があります")


# ---------------- シーンの読み取り ----------------

def parse_behaviors(scene_text: str) -> list[dict]:
    """シーンの Behavior Parameters（ML-Agents）を読み取る"""
    behaviors = []
    for block in scene_text.split("--- !u!"):
        if "m_BehaviorName:" not in block or "VectorObservationSize:" not in block:
            continue

        def field(name: str) -> str:
            m = re.search(rf"^\s*{name}:\s*(.*)$", block, re.MULTILINE)
            return m.group(1).strip() if m else ""

        branch_hex = field("BranchSizes")
        branches = list(struct.unpack(f"<{len(branch_hex) // 8}i", bytes.fromhex(branch_hex))) if branch_hex else []
        behaviors.append({
            "name": field("m_BehaviorName"),
            "observations": int(field("VectorObservationSize")) * int(field("NumStackedVectorObservations") or 1),
            "continuous": int(field("m_NumContinuousActions") or 0),
            "branches": branches,
        })
    return behaviors


def onnx_guids() -> dict[str, Path]:
    """リポジトリにある .onnx の GUID → ファイル"""
    guids = {}
    for meta in (ROOT / "Assets").rglob("*.onnx.meta"):
        m = re.search(r"^guid:\s*([0-9a-f]{32})", meta.read_text(encoding="utf-8"), re.MULTILINE)
        if m:
            guids[m.group(1)] = meta.with_suffix("")
    return guids


def model_spec(path: Path) -> dict:
    """ML-Agents が書き出した .onnx から、観測の数と行動の形を読む"""
    import onnx

    graph = onnx.load(str(path)).graph

    def width(values, name):
        for v in values:
            if v.name == name:
                return v.type.tensor_type.shape.dim[1].dim_value
        return 0

    return {
        "observations": width(graph.input, "obs_0"),
        "continuous": width(graph.output, "continuous_actions"),
        "branch_count": width(graph.output, "discrete_actions"),
        "branch_total": width(graph.input, "action_masks"),  # 選択肢の数の合計
    }


# ---------------- 2・3. モデル ----------------

def check_models(behaviors: list[dict]) -> None:
    print("2. 学習済みモデルとシーンの設定")
    models = sorted((ROOT / "Assets").rglob("*.onnx"))
    if not behaviors:
        fail("シーンに Behavior Parameters が見つかりません")
        return
    if not models:
        ok("モデルはありません（確認することなし）")
        return

    specs = {(b["observations"], b["continuous"], len(b["branches"]), sum(b["branches"])) for b in behaviors}
    if len(specs) != 1:
        fail(f"シーンの中で、ヘビごとに観測・行動の設定が違います: {sorted(specs)}")
        return
    obs, cont, branch_count, branch_total = specs.pop()
    ok(f"シーンの設定：観測 {obs}、連続値の行動 {cont}、選択肢の行動 {branch_count} 個（選択肢は合計 {branch_total}）")

    for path in models:
        rel = path.relative_to(ROOT).as_posix()
        spec = model_spec(path)
        problems = []
        if spec["observations"] != obs:
            problems.append(f"観測 {spec['observations']}（シーンは {obs}）")
        if spec["continuous"] != cont:
            problems.append(f"連続値の行動 {spec['continuous']}（シーンは {cont}）")
        if spec["branch_count"] != branch_count or spec["branch_total"] != branch_total:
            problems.append(f"選択肢の行動 {spec['branch_count']} 個・合計 {spec['branch_total']}"
                            f"（シーンは {branch_count} 個・合計 {branch_total}）")
        if problems:
            fail(f"{rel} がシーンと合いません：" + "、".join(problems) + "。学習し直すか、モデルを消してください")
        else:
            ok(f"{rel}")

    print("3. シーンが使っているモデル")
    guids = onnx_guids()
    # Behavior Parameters の m_Model と、性格を切り替えるときの calmModel・angryModel など、
    # 名前に Model が付く項目から参照しているモデル
    refs = re.findall(r"^\s*(\w*[Mm]odel\w*):\s*\{[^}]*guid:\s*([0-9a-f]{32})",
                      SCENE.read_text(encoding="utf-8"), re.MULTILINE)
    used = {}
    for field_name, guid in refs:
        used.setdefault(guid, set()).add(field_name)
    for guid, fields in sorted(used.items()):
        where = "、".join(sorted(fields))
        if guid in guids:
            ok(f"{guids[guid].relative_to(ROOT).as_posix()}（{where}）")
        else:
            fail(f"シーンの {where} が GUID {guid} のモデルを使っていますが、リポジトリにありません")
    if not used:
        ok("シーンはモデルを使っていません（学習用の設定）")


# ---------------- 4. 学習の設定 ----------------

def check_training_configs(behaviors: list[dict]) -> None:
    print("4. 学習の設定（Training/*.yaml）")
    import yaml
    from mlagents.plugins.trainer_type import register_trainer_plugins
    from mlagents.trainers.settings import RunOptions

    register_trainer_plugins()  # ppo などの名前を ML-Agents に登録する
    scene_names = {b["name"] for b in behaviors}

    for path in sorted((ROOT / "Training").glob("*.yaml")):
        rel = path.relative_to(ROOT).as_posix()
        try:
            options = RunOptions.from_dict(yaml.safe_load(path.read_text(encoding="utf-8")))
        except Exception as e:
            fail(f"{rel} を ML-Agents が読めません：{type(e).__name__}: {e}")
            continue
        unknown = sorted(set(options.behaviors) - scene_names)
        if unknown and len(unknown) == len(options.behaviors):
            # どの名前もシーンにない＝ほかのブランチ（v1・v2 など）用の設定。このブランチでは使わない
            note(f"{rel}（{', '.join(options.behaviors)}）はこのブランチのシーンでは使わない設定です")
        elif unknown:
            fail(f"{rel} の behaviors {unknown} がシーンにありません（シーンの名前は {sorted(scene_names)}）。"
                 "書き間違いかもしれません")
        elif options.behaviors:
            ok(f"{rel}（{', '.join(options.behaviors)}）")
        else:
            ok(f"{rel}（すべてのヘビに default_settings を使う）")


def main() -> int:
    check_meta_files()
    behaviors = parse_behaviors(SCENE.read_text(encoding="utf-8"))
    check_models(behaviors)
    check_training_configs(behaviors)

    print()
    if errors:
        print(f"問題が {len(errors)} 件あります。")
        return 1
    print("問題はありませんでした。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

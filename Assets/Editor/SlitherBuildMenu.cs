using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 学習用 exe をワンクリックで書き出すメニュー。
///   メニュー: Slither → Build Training Exe   （ショートカット: Ctrl + Alt + B）
/// ・開いているシーンを保存してから、Training/Build/ に Windows 用 exe を書き出す
/// ・保存先や設定を毎回選ばなくていい
/// ・学習中（exe が動いている）だと上書きできないので、その場合はエラーを出して止まる
/// </summary>
public static class SlitherBuildMenu
{
    const string BuildFolder = "Training/Build";

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string ExePath => Path.Combine(ProjectRoot, BuildFolder, PlayerSettings.productName + ".exe");

    [MenuItem("Slither/Build Training Exe %&b", priority = 0)]
    public static void BuildTrainingExe()
    {
        // 1. 開いているシーンを保存（保存し忘れ防止）
        if (!EditorSceneManager.SaveOpenScenes())
        {
            EditorUtility.DisplayDialog("Slither", "シーンを保存できませんでした。", "OK");
            return;
        }

        // 2. ビルドに入れるシーン（Build Profiles の Scene List で有効なもの）
        var scenes = System.Array.FindAll(EditorBuildSettings.scenes, s => s.enabled);
        if (scenes.Length == 0)
        {
            EditorUtility.DisplayDialog("Slither", "Scene List にシーンがありません。\nFile → Build Profiles → Scene List で追加してください。", "OK");
            return;
        }

        // 3. 学習中の exe が動いていたら上書きできない
        string exeName = Path.GetFileNameWithoutExtension(ExePath);
        if (Process.GetProcessesByName(exeName).Length > 0)
        {
            EditorUtility.DisplayDialog("Slither",
                $"{exeName}.exe が動いています（学習中？）。\n学習を止めてから、もう一度ビルドしてください。", "OK");
            return;
        }

        // 4. ビルド
        var options = new BuildPlayerOptions
        {
            scenes = System.Array.ConvertAll(scenes, s => s.path),
            locationPathName = ExePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            UnityEngine.Debug.Log($"[Slither] Build succeeded: {ExePath} ({summary.totalTime.TotalSeconds:F0}s)");
            EditorUtility.DisplayDialog("Slither", $"ビルド完了 ({summary.totalTime.TotalSeconds:F0} 秒)\n{ExePath}", "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("Slither", $"ビルド失敗: {summary.result}\nConsole を確認してください。", "OK");
        }
    }

    [MenuItem("Slither/Open Training Folder", priority = 20)]
    public static void OpenTrainingFolder()
    {
        EditorUtility.RevealInFinder(Path.Combine(ProjectRoot, "Training", "slither_ppo.yaml"));
    }
}

using UnityEngine;

/// <summary>
/// 観戦用のスコアボード。画面左上に、ヘビごとの「今の長さ・最長・死んだ回数・倒した数」を出す。
/// 空の GameObject に付けるだけで動く（学習中は表示しない）。
/// 名前は Hierarchy のヘビの名前がそのまま出るので、「v1」「v2」などにしておくと分かりやすい。
/// </summary>
public class SlitherScoreboard : MonoBehaviour
{
    [SerializeField] SlitherArena arena;
    [SerializeField] int fontSize = 22;

    GUIStyle style;
    Texture2D swatch;

    void Start()
    {
        if (arena == null) arena = FindAnyObjectByType<SlitherArena>();
        swatch = Texture2D.whiteTexture;
    }

    void OnGUI()
    {
        if (arena == null || Unity.MLAgents.Academy.Instance.IsCommunicatorOn) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = fontSize };
            style.normal.textColor = Color.white;
        }

        float y = 10f, line = fontSize + 8f;
        GUI.Label(new Rect(40, y, 900, line), "name            len   best  deaths  kills", style);
        y += line;
        foreach (var s in arena.snakes)
        {
            var prev = GUI.color;
            GUI.color = s.DisplayColor;
            GUI.DrawTexture(new Rect(12, y + 6, fontSize - 4, fontSize - 4), swatch);
            GUI.color = prev;
            string row = $"{s.name,-14} {s.Length,5} {s.BestLength,6} {s.Deaths,7} {s.Kills,6}";
            GUI.Label(new Rect(40, y, 900, line), row, style);
            y += line;
        }
    }
}

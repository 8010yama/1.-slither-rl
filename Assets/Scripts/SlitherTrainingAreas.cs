using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// 学習を速くするために、フィールド（Arena＋その子のヘビ）を複製して並べる。
/// 空の GameObject に付けて、Base Area に Arena をドラッグする。
/// 既定では Python と接続して学習しているときだけ複製する（手で遊ぶ・録画するときは1つだけ）。
/// 他のスクリプトより先に動かすため、実行順を早めにしている。
/// </summary>
[DefaultExecutionOrder(-1000)]
public class SlitherTrainingAreas : MonoBehaviour
{
    [Tooltip("複製する元の Arena（ヘビを子にしておく）")]
    [SerializeField] GameObject baseArea;
    [Tooltip("フィールドの合計数（元の1つを含む）")]
    [SerializeField] int areaCount = 16;
    [Tooltip("フィールド同士の間隔。半径30なら70あれば重ならない")]
    [SerializeField] float spacing = 70f;
    [Tooltip("ON: 学習中だけ複製する / OFF: いつでも複製する")]
    [SerializeField] bool onlyWhenTraining = true;

    void Awake()
    {
        if (baseArea == null || areaCount <= 1) return;
        if (onlyWhenTraining && !Academy.Instance.IsCommunicatorOn) return;

        int columns = Mathf.CeilToInt(Mathf.Sqrt(areaCount));
        Vector3 origin = baseArea.transform.position;

        for (int i = 1; i < areaCount; i++)
        {
            var offset = new Vector3((i % columns) * spacing, -(i / columns) * spacing, 0f);
            var clone = Instantiate(baseArea, origin + offset, baseArea.transform.rotation, baseArea.transform.parent);
            clone.name = $"{baseArea.name} ({i})";
        }
    }
}

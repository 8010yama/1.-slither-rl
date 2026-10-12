using UnityEngine;

/// <summary>
/// ヘビの頭を追いかけるカメラ。Main Camera に付ける。
/// 体が長くなるほど少しずつズームアウトする。
/// </summary>
[RequireComponent(typeof(Camera))]
public class SlitherCamera : MonoBehaviour
{
    [SerializeField] SlitherAgent target;
    [Tooltip("追従のなめらかさ（大きいほどピッタリ付いてくる）")]
    [SerializeField] float followSharpness = 8f;

    [Header("Zoom")]
    [SerializeField] float baseSize = 8f;
    [Tooltip("体の節1つあたりのズームアウト量")]
    [SerializeField] float sizePerSegment = 0.02f;
    [SerializeField] float maxSize = 20f;

    Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f); // フィールドの外側
        if (target == null) target = FindAnyObjectByType<SlitherAgent>();
    }

    void LateUpdate()
    {
        if (target == null || !target.HasBody) return;

        float k = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);

        Vector3 goal = target.HeadWorld;
        goal.z = transform.position.z;
        transform.position = Vector3.Lerp(transform.position, goal, k);

        float size = Mathf.Min(maxSize, baseSize + target.Length * sizePerSegment);
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, size, k * 0.5f);
    }
}

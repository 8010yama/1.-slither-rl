using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// スリザリオ風の円形フィールド。エサの管理と、フィールド上のヘビの一覧を持つ。
/// この GameObject の位置がフィールドの中心になる。
/// 座標はすべて「フィールド中心からの相対位置 (local)」で扱う。
/// ヘビ（SlitherAgent）はこの Arena の子に置く。snakesPerArena を増やすと、
/// 最初のヘビを複製して同じフィールドに何匹も出す（ヘビ同士がぶつかると死ぬ生存競争になる）。
/// </summary>
[DefaultExecutionOrder(-500)] // ヘビより先に準備する
public class SlitherArena : MonoBehaviour
{
    [Header("Field")]
    public float radius = 30f;

    [Header("Snakes")]
    [Tooltip("このフィールドに出すヘビの数。子にあるヘビが足りなければ、子のヘビを順番に複製して増やす")]
    public int snakesPerArena = 1;

    [Header("Food")]
    [Tooltip("フィールドに常に置いておくエサの数（食べられたら補充）")]
    public int foodCount = 200;
    [Tooltip("ヘビが死んだときに落とすエサも含めた上限")]
    public int maxFood = 600;
    public float foodDiameter = 0.35f;

    [Header("Colors")]
    public Color backgroundColor = new Color(0.08f, 0.09f, 0.12f);
    public Color borderColor = new Color(0.9f, 0.25f, 0.3f);

    public readonly List<Vector2> foods = new List<Vector2>();
    public readonly List<SlitherAgent> snakes = new List<SlitherAgent>();
    readonly List<SpriteRenderer> foodRenderers = new List<SpriteRenderer>();

    bool initialized;
    static Sprite circleSprite;
    static Material unlitMaterial;

    public Vector2 Center => transform.position;

    void Awake() => EnsureInitialized();

    public void EnsureInitialized()
    {
        if (initialized) return;
        initialized = true;

        // 外周（赤い縁）と床
        CreateCircle(transform, "Border", borderColor, -2, radius * 2f + 0.6f).transform.localPosition = Vector3.zero;
        CreateCircle(transform, "Floor", backgroundColor, -1, radius * 2f).transform.localPosition = Vector3.zero;

        for (int i = 0; i < foodCount; i++) AddFood(RandomPoint(0.95f));

        SpawnSnakes();
    }

    void SpawnSnakes()
    {
        var existing = GetComponentsInChildren<SlitherAgent>(true);
        if (existing.Length == 0) return;
        // 子に置いたヘビを順番に複製する（性格の違うヘビを並べておけば、性格ごとに同じ数ずつ増える）
        for (int i = existing.Length; i < snakesPerArena; i++)
        {
            var template = existing[i % existing.Length].gameObject;
            var clone = Instantiate(template, transform);
            clone.name = $"{template.name} ({i})";
        }
    }

    public void Register(SlitherAgent snake)
    {
        if (!snakes.Contains(snake)) snakes.Add(snake);
    }

    public void Unregister(SlitherAgent snake) => snakes.Remove(snake);

    public Vector2 RandomPoint(float maxFraction) => Random.insideUnitCircle * radius * maxFraction;

    public void AddFood(Vector2 local)
    {
        if (foods.Count >= maxFood) return;
        foods.Add(local);
        var r = CreateCircle(transform, "Food", Color.HSVToRGB(Random.value, 0.65f, 1f), 1, foodDiameter);
        r.transform.position = (Vector3)(Center + local);
        foodRenderers.Add(r);
    }

    /// <summary>位置 local から reach 以内のエサを食べて、食べた数を返す。</summary>
    public int EatFood(Vector2 local, float reach)
    {
        float reach2 = reach * reach;
        int eaten = 0;
        for (int i = foods.Count - 1; i >= 0; i--)
        {
            if ((foods[i] - local).sqrMagnitude > reach2) continue;
            RemoveFoodAt(i);
            eaten++;
        }
        while (foods.Count < foodCount) AddFood(RandomPoint(0.95f));
        return eaten;
    }

    void RemoveFoodAt(int i)
    {
        int last = foods.Count - 1;
        Destroy(foodRenderers[i].gameObject);
        foods[i] = foods[last];
        foodRenderers[i] = foodRenderers[last];
        foods.RemoveAt(last);
        foodRenderers.RemoveAt(last);
    }

    // ───────── 描画ユーティリティ（ヘビ側からも使う） ─────────

    public static SpriteRenderer CreateCircle(Transform parent, string objectName, Color color, int order, float diameter)
    {
        if (circleSprite == null) circleSprite = BuildCircleSprite();
        if (unlitMaterial == null)
        {
            // 2D ライトが無いシーンでも色が出るように Unlit を使う（見つからなければ既定のまま）
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader != null) unlitMaterial = new Material(shader);
        }

        var go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * diameter;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = circleSprite;
        sr.color = color;
        sr.sortingOrder = order;
        if (unlitMaterial != null) sr.sharedMaterial = unlitMaterial;
        return sr;
    }

    // 直径1ユニットの、ふちが滑らかな白い円
    static Sprite BuildCircleSprite()
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(c - d + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}

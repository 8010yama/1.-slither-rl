using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// スリザリオ風のヘビのエージェント（本家寄せ版）。
/// ・常に前進し続け、曲がる量を連続値（-1〜1）で決める。急には曲がれない
/// ・ブーストすると2倍速になる代わりに、体が少しずつ減ってエサとして落ちる
/// ・エサを食べるほど長く、太くなる
/// ・他のヘビの体に頭が当たると死ぬ（自分の体には当たらない＝本家と同じ）
/// ・フィールドの縁は既定ではぶつかっても死なず、縁に沿ってすべる（wallKills で切り替え）
/// ・死ぬと体がエサになってばらまかれ、別の場所から復活する
///
/// 行動: 連続値1つ（曲がる量）＋ 離散1つ（0=通常, 1=ブースト）
/// 手動操作: マウスの方向に向かう。左クリック or スペースでブースト
///
/// Behavior Parameters（Behavior Name / 観測数 / 行動）は、
/// このコンポーネントを追加したとき・インスペクターを触ったときに自動で設定される。
/// Arena がシーンに無ければ実行時に自動で作られる。
/// </summary>
public class SlitherAgent : Agent
{
    [SerializeField] SlitherArena arena;

    [Header("Movement")]
    [Tooltip("1ステップで進む距離（体の節の間隔も兼ねる）")]
    [SerializeField] float speed = 0.3f;
    [Tooltip("1ステップで曲がれる最大の角度")]
    [SerializeField] float turnDegrees = 12f;
    [SerializeField] float bodyDiameter = 0.8f;
    [Tooltip("長くなるほど太くなる。この長さで最大の太さになる")]
    [SerializeField] int lengthForMaxWidth = 300;
    [Tooltip("最大の太さ（bodyDiameter の何倍か）")]
    [SerializeField] float maxWidthScale = 2.2f;
    [SerializeField] int initialLength = 12;
    [Tooltip("エサ1個で伸びる節の数")]
    [SerializeField] int growthPerFood = 1;
    [Tooltip("ON にすると自分の体にぶつかっても死ぬ（本家は OFF）")]
    [SerializeField] bool selfCollision = false;
    [Tooltip("ON にすると縁に当たったら死ぬ。OFF なら縁に沿ってすべる")]
    [SerializeField] bool wallKills = false;

    [Header("Boost")]
    [Tooltip("ブースト中、何ステップごとに体が1節減るか")]
    [SerializeField] int boostStepsPerSegment = 4;
    [Tooltip("この長さ以下だとブーストできない")]
    [SerializeField] int minBoostLength = 15;

    [Header("Sensors")]
    [SerializeField] int rayCount = 16;
    [SerializeField] float rayLength = 12f;

    [Header("Episode")]
    [Tooltip("1エピソードの最大行動回数。超えたら打ち切って別の場所からやり直す（0 で無制限。録画のときは 0 がおすすめ）")]
    [SerializeField] int maxEpisodeSteps = 5000;

    [Header("Rewards")]
    [SerializeField] float foodReward = 0.5f;
    [SerializeField] float deathPenalty = -1f;
    [SerializeField] float stepPenalty = -0.001f;
    [Tooltip("この歩数エサを食べなかったらペナルティを与えて終了（ぐるぐる回り防止）")]
    [SerializeField] int maxStepsWithoutFood = 300;
    [SerializeField] float starvePenalty = -0.5f;

    [Header("Playback")]
    [Tooltip("学習中以外（推論・手動操作）で、何アカデミーステップごとに1回動くか。0.02秒 × 2 = 毎秒25回")]
    [SerializeField] int stepsPerMoveWhenWatching = 2;

    [Header("Look")]
    [SerializeField] bool randomColor = true;
    [SerializeField] Color color = new Color(0.3f, 0.9f, 0.45f);
    [SerializeField] bool showEyes = true;

    const int ExtraObservations = 5;

    // [0] が頭。フィールド中心からの相対座標
    readonly List<Vector2> path = new List<Vector2>();
    readonly List<SpriteRenderer> segmentRenderers = new List<SpriteRenderer>();
    int length;
    float heading; // ラジアン
    int stepsSinceFood;
    int stepCounter;
    int boostCounter;
    int episodeSteps;
    bool boosting;
    float lastTurnInput;

    // 目（白目2つ＋黒目2つ）
    readonly SpriteRenderer[] eyeWhites = new SpriteRenderer[2];
    readonly SpriteRenderer[] pupils = new SpriteRenderer[2];

    public IReadOnlyList<Vector2> Path => path;
    public int Length => length;
    public bool HasBody => path.Count > 0;
    public Vector2 HeadWorld => arena.Center + path[0];

    float CurrentDiameter =>
        bodyDiameter * Mathf.Lerp(1f, maxWidthScale, Mathf.Clamp01((length - initialLength) / (float)Mathf.Max(1, lengthForMaxWidth - initialLength)));
    float BodyRadius => CurrentDiameter * 0.5f;
    bool CanBoost => length > minBoostLength;

    // ───────── エディタ用: Behavior Parameters の自動設定 ─────────

#if UNITY_EDITOR
    // コンポーネントを追加したとき（または右クリック → Reset）に呼ばれる
    void Reset()
    {
        var bp = GetComponent<BehaviorParameters>();
        if (bp == null) bp = gameObject.AddComponent<BehaviorParameters>();
        bp.BehaviorName = "Slither";
        bp.BrainParameters.NumStackedVectorObservations = 1;
        SyncBehaviorParameters(bp);
    }

    // インスペクターを触るたびに観測数・行動の形を合わせる
    void OnValidate()
    {
        var bp = GetComponent<BehaviorParameters>();
        if (bp != null) SyncBehaviorParameters(bp);
    }

    void SyncBehaviorParameters(BehaviorParameters bp)
    {
        int size = rayCount * 3 + ExtraObservations;
        if (bp.BrainParameters.VectorObservationSize != size)
            bp.BrainParameters.VectorObservationSize = size;

        var spec = bp.BrainParameters.ActionSpec;
        bool ok = spec.NumContinuousActions == 1
                  && spec.BranchSizes != null && spec.BranchSizes.Length == 1 && spec.BranchSizes[0] == 2;
        if (!ok) bp.BrainParameters.ActionSpec = new ActionSpec(1, new[] { 2 });
    }
#endif

    // ───────── 初期化 ─────────

    public override void Initialize()
    {
        // 親の Arena を優先（フィールドを複数並べて並列学習するため）
        if (arena == null) arena = GetComponentInParent<SlitherArena>();
        if (arena == null) arena = FindAnyObjectByType<SlitherArena>();
        if (arena == null) arena = new GameObject("Arena").AddComponent<SlitherArena>();
        arena.EnsureInitialized();
        arena.Register(this);
        CreateEyes();
        Academy.Instance.AgentPreStep += OnAcademyPreStep;
    }

    protected override void OnDisable()
    {
        if (Academy.IsInitialized) Academy.Instance.AgentPreStep -= OnAcademyPreStep;
        if (arena != null) arena.Unregister(this);
        base.OnDisable();
    }

    void OnAcademyPreStep(int academyStepCount)
    {
        int period = Academy.Instance.IsCommunicatorOn ? 1 : Mathf.Max(1, stepsPerMoveWhenWatching);
        stepCounter++;
        if (stepCounter >= period)
        {
            stepCounter = 0;
            RequestDecision();
        }
    }

    public override void OnEpisodeBegin()
    {
        if (randomColor) color = Color.HSVToRGB(Random.value, 0.6f, 0.95f);

        // 他のヘビから少し離れた場所に復活
        Vector2 start = arena.RandomPoint(0.6f);
        for (int tries = 0; tries < 20 && TooCloseToOthers(start, 4f); tries++)
            start = arena.RandomPoint(0.6f);

        heading = Random.Range(0f, Mathf.PI * 2f);
        var dir = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
        length = initialLength;
        path.Clear();
        for (int i = 0; i < length; i++) path.Add(start - dir * speed * i);

        stepsSinceFood = 0;
        boostCounter = 0;
        episodeSteps = 0;
        boosting = false;
        Render();
    }

    // ───────── 観測（rayCount × 3 + 5） ─────────

    public override void CollectObservations(VectorSensor sensor)
    {
        if (path.Count == 0)
        {
            // 念のため: 体が無いときは 0 で埋める
            for (int i = 0; i < rayCount * 3 + ExtraObservations; i++) sensor.AddObservation(0f);
            return;
        }
        var head = path[0];

        // 頭のまわり360°にレイを飛ばし、「エサ」「ヘビの体」「縁」までの近さを見る
        for (int i = 0; i < rayCount; i++)
        {
            float a = heading + Mathf.PI * 2f * i / rayCount;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            sensor.AddObservation(Closeness(RayToFood(head, d)));
            sensor.AddObservation(Closeness(RayToBodies(head, d)));
            sensor.AddObservation(Closeness(RayToWall(head, d)));
        }

        // 長さ・ブーストできるか・中心からの距離・中心の方向（自分の向き基準）
        sensor.AddObservation(Mathf.Clamp01(length / 300f));
        sensor.AddObservation(CanBoost);
        sensor.AddObservation(head.magnitude / arena.radius);
        float toCenter = Mathf.Atan2(-head.y, -head.x) - heading;
        sensor.AddObservation(Mathf.Sin(toCenter));
        sensor.AddObservation(Mathf.Cos(toCenter));
    }

    float Closeness(float dist) => dist < rayLength ? 1f - dist / rayLength : 0f;

    float RayToFood(Vector2 o, Vector2 d)
    {
        float best = float.MaxValue;
        float r = 0.5f; // 見つけやすいように当たり判定は少し大きめ
        foreach (var f in arena.foods) best = Mathf.Min(best, RayCircle(o, d, f, r));
        return best;
    }

    float RayToBodies(Vector2 o, Vector2 d)
    {
        float best = float.MaxValue;
        foreach (var s in arena.snakes)
        {
            if (!s.HasBody) continue;
            int startIndex = 0;
            if (s == this)
            {
                if (!selfCollision) continue;
                startIndex = 8; // 首まわりは無視
            }
            var p = s.Path;
            for (int j = startIndex; j < p.Count; j += 2) // 節が重なっているので1つ飛ばしで十分
                best = Mathf.Min(best, RayCircle(o, d, p[j], s.BodyRadius));
        }
        return best;
    }

    float RayToWall(Vector2 o, Vector2 d)
    {
        // |o + t d| = R を解く（o は円の内側）
        float b = Vector2.Dot(o, d);
        float c = o.sqrMagnitude - arena.radius * arena.radius;
        return -b + Mathf.Sqrt(Mathf.Max(0f, b * b - c));
    }

    float RayCircle(Vector2 o, Vector2 d, Vector2 center, float r)
    {
        var v = center - o;
        float t = Vector2.Dot(v, d);
        if (t < 0f || t > rayLength + r) return float.MaxValue;
        float perp2 = v.sqrMagnitude - t * t;
        if (perp2 > r * r) return float.MaxValue;
        return Mathf.Max(0f, t - Mathf.Sqrt(r * r - perp2));
    }

    // ───────── 行動: 連続[0]=曲がる量(-1=右いっぱい 〜 +1=左いっぱい), 離散[0]=ブースト ─────────

    public override void OnActionReceived(ActionBuffers actions)
    {
        float turnInput = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        lastTurnInput = turnInput;
        heading += turnInput * turnDegrees * Mathf.Deg2Rad;

        boosting = actions.DiscreteActions[0] == 1 && CanBoost;
        int moves = boosting ? 2 : 1;

        int eaten = 0;
        for (int m = 0; m < moves; m++)
        {
            if (!MoveOneStep(ref eaten)) return; // 死んだ
        }

        if (eaten > 0)
        {
            AddReward(foodReward * eaten);
            stepsSinceFood = 0;
        }
        else
        {
            AddReward(stepPenalty);
            stepsSinceFood++;
        }

        // ブーストの代償: 体が減って、尻尾の位置にエサとして落ちる
        if (boosting)
        {
            boostCounter++;
            if (boostCounter >= boostStepsPerSegment)
            {
                boostCounter = 0;
                arena.AddFood(path[path.Count - 1]);
                length--;
                // 落としたエサを拾い直すだけで得をしないよう、同じ量を引いておく
                AddReward(-foodReward);
            }
        }
        while (path.Count > length) path.RemoveAt(path.Count - 1);

        if (stepsSinceFood > maxStepsWithoutFood)
        {
            AddReward(starvePenalty);
            EndEpisodeWithStats();
            return;
        }

        // 時間切れ: 失敗ではないので EpisodeInterrupted（＝「この先も続いていたはず」として学習される）
        episodeSteps++;
        if (maxEpisodeSteps > 0 && episodeSteps >= maxEpisodeSteps)
        {
            Academy.Instance.StatsRecorder.Add("Slither/Length", length);
            EpisodeInterrupted();
            return;
        }

        Render();
    }

    // 1歩進む。死んだら false
    bool MoveOneStep(ref int eaten)
    {
        var newHead = path[0] + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * speed;

        if (HitsWall(newHead))
        {
            if (wallKills)
            {
                path.Insert(0, newHead);
                Die(deathPenalty);
                return false;
            }
            // 縁の内側に押し戻して、縁に沿う向きに直す
            float limit = arena.radius - BodyRadius;
            newHead = newHead.normalized * limit;
            var move = newHead - path[0];
            if (move.sqrMagnitude > 1e-6f) heading = Mathf.Atan2(move.y, move.x);
        }

        path.Insert(0, newHead);

        if (HitsBody(newHead))
        {
            Die(deathPenalty);
            return false;
        }

        int n = arena.EatFood(newHead, BodyRadius + arena.foodDiameter * 0.5f + 0.1f);
        if (n > 0)
        {
            eaten += n;
            length += n * growthPerFood;
        }
        while (path.Count > length) path.RemoveAt(path.Count - 1);
        return true;
    }

    // マウスで手動操作（Behavior Type を Heuristic Only にしたとき）
    // マウスの方向に向かって曲がる。左クリック or スペースでブースト
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var c = actionsOut.ContinuousActions;
        var d = actionsOut.DiscreteActions;
        c[0] = 0f;
        d[0] = 0;

        var cam = Camera.main;
        if (cam == null || !HasBody) return;

        Vector2 mouse;
        bool boost;
#if ENABLE_INPUT_SYSTEM
        var ms = Mouse.current;
        if (ms == null) return;
        mouse = ms.position.ReadValue();
        var kb = Keyboard.current;
        boost = ms.leftButton.isPressed || (kb != null && kb.spaceKey.isPressed);
#else
        mouse = Input.mousePosition;
        boost = Input.GetMouseButton(0) || Input.GetKey(KeyCode.Space);
#endif
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, -cam.transform.position.z));
        Vector2 toMouse = (Vector2)world - HeadWorld;
        if (toMouse.sqrMagnitude > 0.01f)
        {
            float diff = Mathf.DeltaAngle(heading * Mathf.Rad2Deg, Mathf.Atan2(toMouse.y, toMouse.x) * Mathf.Rad2Deg);
            c[0] = Mathf.Clamp(diff / turnDegrees, -1f, 1f);
        }
        d[0] = boost ? 1 : 0;
    }

    // ───────── 当たり判定 ─────────

    bool HitsWall(Vector2 p) => p.magnitude > arena.radius - BodyRadius;

    bool HitsBody(Vector2 head)
    {
        foreach (var s in arena.snakes)
        {
            if (!s.HasBody) continue;
            int startIndex = 0;
            if (s == this)
            {
                if (!selfCollision) continue;
                startIndex = 8;
            }
            float hit = (BodyRadius + s.BodyRadius) * 0.8f;
            float hit2 = hit * hit;
            var p = s.Path;
            for (int j = startIndex; j < p.Count; j++)
                if ((p[j] - head).sqrMagnitude < hit2) return true;
        }
        return false;
    }

    bool TooCloseToOthers(Vector2 p, float margin)
    {
        foreach (var s in arena.snakes)
        {
            if (s == this || !s.HasBody) continue;
            foreach (var q in s.Path)
                if ((q - p).sqrMagnitude < margin * margin) return true;
        }
        return false;
    }

    void Die(float penalty)
    {
        AddReward(penalty);
        // 本家のように体がエサになってばらまかれる
        for (int i = 1; i < path.Count; i += 2)
            arena.AddFood(path[i] + Random.insideUnitCircle * 0.3f);
        EndEpisodeWithStats();
    }

    void EndEpisodeWithStats()
    {
        Academy.Instance.StatsRecorder.Add("Slither/Length", length);
        // EndEpisode の中で最後の観測（CollectObservations）が呼ばれるので、
        // ここでは体を消さない。OnEpisodeBegin で作り直される
        EndEpisode(); // すぐに OnEpisodeBegin が呼ばれて復活する
    }

    // ───────── 描画 ─────────

    void Render()
    {
        while (segmentRenderers.Count < path.Count)
            segmentRenderers.Add(SlitherArena.CreateCircle(transform, "Segment", color, 5, bodyDiameter));

        // ブースト中は少し明るく光らせる
        var baseColor = boosting ? Color.Lerp(color, Color.white, 0.35f) : color;
        var dark = baseColor * 0.8f;
        dark.a = 1f;
        var scale = Vector3.one * CurrentDiameter;

        for (int i = 0; i < segmentRenderers.Count; i++)
        {
            var r = segmentRenderers[i];
            bool active = i < path.Count;
            if (r.gameObject.activeSelf != active) r.gameObject.SetActive(active);
            if (!active) continue;
            r.transform.position = (Vector3)(arena.Center + path[i]);
            r.transform.localScale = scale;
            r.color = (i / 3) % 2 == 0 ? baseColor : dark; // しま模様
            r.sortingOrder = 5 + (i == 0 ? 2 : 0);
        }

        RenderEyes();
    }

    void CreateEyes()
    {
        for (int i = 0; i < 2; i++)
        {
            eyeWhites[i] = SlitherArena.CreateCircle(transform, "EyeWhite", Color.white, 8, 1f);
            pupils[i] = SlitherArena.CreateCircle(transform, "Pupil", new Color(0.05f, 0.05f, 0.08f), 9, 1f);
        }
    }

    void RenderEyes()
    {
        bool visible = showEyes && HasBody;
        for (int i = 0; i < 2; i++)
        {
            if (eyeWhites[i].gameObject.activeSelf != visible) eyeWhites[i].gameObject.SetActive(visible);
            if (pupils[i].gameObject.activeSelf != visible) pupils[i].gameObject.SetActive(visible);
        }
        if (!visible) return;

        float diameter = CurrentDiameter;
        float whiteSize = diameter * 0.42f;
        float pupilSize = diameter * 0.24f;

        var forward = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
        var side = new Vector2(-forward.y, forward.x); // 左方向

        // 黒目は「これから曲がる方向」を少し先取りして見る
        float look = heading + lastTurnInput * turnDegrees * Mathf.Deg2Rad * 3f;
        var lookDir = new Vector2(Mathf.Cos(look), Mathf.Sin(look));
        float pupilOffset = (whiteSize - pupilSize) * 0.5f * 0.9f;

        Vector2 head = arena.Center + path[0];
        for (int i = 0; i < 2; i++)
        {
            float sign = i == 0 ? 1f : -1f;
            Vector2 eyePos = head + forward * diameter * 0.18f + side * sign * diameter * 0.24f;

            eyeWhites[i].transform.position = eyePos;
            eyeWhites[i].transform.localScale = Vector3.one * whiteSize;

            pupils[i].transform.position = eyePos + lookDir * pupilOffset;
            pupils[i].transform.localScale = Vector3.one * pupilSize;
        }
    }
}

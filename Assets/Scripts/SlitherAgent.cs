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
/// 性格（Personality）:
///   Calm（穏やか）       … エサ集め優先。他のヘビに近づくと減点。倒しても得しない
///   Aggressive（攻撃的） … 相手の頭に近づくと加点、相手を自分の体にぶつけて倒すと大きく加点
///   Switcher（豹変型）   … ふだんは穏やか。相手に近づかれる or たまにランダムでキレて、
///                          しばらく攻撃的になる（体が赤くなり、目が血走る）
/// 性格ごとに Behavior Name が Slither_Calm / Slither_Aggressive / Slither_Switcher になり、
/// それぞれ別のAI（モデル）として同時に学習する。
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
    public enum Personality { Calm, Aggressive, Switcher }

    [System.Serializable]
    public class ModeRewards
    {
        [Tooltip("エサ1個の報酬")]
        public float foodReward = 0.5f;
        [Tooltip("相手が自分の体にぶつかって死んだときの報酬")]
        public float killReward = 0f;
        [Tooltip("他のヘビの体が Personal Space より近いとき、1ステップごとに加える値（マイナスで減点）")]
        public float nearOthersPenalty = 0f;
        [Tooltip("一番近い相手の頭に 1 近づくごとの報酬（追いかける動きを覚えさせる）")]
        public float chaseReward = 0f;
        [Tooltip("相手の頭が自分の体の目の前にある（相手が自分の体に向かって進んでいる）とき、1匹につき毎ステップもらえる報酬。体で相手の進路をふさぐ動きを覚えさせる")]
        public float pressureReward = 0f;
    }

    [SerializeField] SlitherArena arena;

    [Header("Personality")]
    [SerializeField] Personality personality = Personality.Calm;
    [Tooltip("穏やかモードで「近すぎる」とみなす距離")]
    [SerializeField] float personalSpace = 4f;
    [Tooltip("攻撃モードで相手を追いかける対象にする距離")]
    [SerializeField] float chaseRange = 15f;
    [Tooltip("豹変型: 相手の頭がこの距離まで近づいたらキレる")]
    [SerializeField] float provokeDistance = 5f;
    [Tooltip("豹変型: 理由なくキレる確率（1ステップあたり）")]
    [SerializeField] float randomRageChance = 0.002f;
    [Tooltip("豹変型: キレている時間（ステップ）")]
    [SerializeField] int rageDuration = 250;
    [Tooltip("豹変型: 落ち着いたあと、この時間（ステップ）はキレない。0 だと相手が近くにいる限りすぐまたキレる")]
    [SerializeField] int rageCooldown = 300;
    [Header("Switcher: model swap (play / recording only)")]
    [Tooltip("豹変型が落ち着いているときに使うモデル（例: Calm_final.onnx）。空なら Behavior Parameters の Model のまま")]
    [SerializeField] UnityEngine.Object calmModel;
    [Tooltip("豹変型がキレているときに使うモデル（例: Aggressive_final.onnx）")]
    [SerializeField] UnityEngine.Object angryModel;
    [Tooltip("体で進路をふさいでいるとみなす距離")]
    [SerializeField] float pressureRadius = 3f;
    [SerializeField] ModeRewards calmRewards = new ModeRewards { foodReward = 0.5f, killReward = 0f, nearOthersPenalty = -0.01f, chaseReward = 0f, pressureReward = 0f };
    [SerializeField] ModeRewards aggressiveRewards = new ModeRewards { foodReward = 0.2f, killReward = 10f, nearOthersPenalty = 0f, chaseReward = 0f, pressureReward = 0.01f };
    // 報酬の初期値を更新したときに、シーンに置いてあるヘビにも自動で反映するための番号
    [SerializeField, HideInInspector] int rewardPresetVersion;
    const int CurrentRewardPreset = 4;

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
    [Tooltip("OFF にすると「キレているか」の観測を外して観測数53になり、v1・v2 のモデルを使える")]
    [SerializeField] bool includeMoodObservation = true;
    [Tooltip("近くの相手3匹の「頭の位置・進む向き・大きさ」を観測に入れる（v4以降）。OFF にすると v3 以前のモデルを使える")]
    [SerializeField] bool includeOpponentObservation = true;
    [SerializeField] int rayCount = 16;
    [SerializeField] float rayLength = 12f;

    [Header("Episode")]
    [Tooltip("1エピソードの最大行動回数。超えたら打ち切って別の場所からやり直す（0 で無制限。録画のときは 0 がおすすめ）")]
    [SerializeField] int maxEpisodeSteps = 5000;

    [Header("Rewards (common)")]
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

    const int OpponentSlots = 3;
    const int OpponentFeatures = 6;
    int ExtraObservations => 5 + (includeMoodObservation ? 1 : 0) + (includeOpponentObservation ? OpponentSlots * OpponentFeatures : 0);

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
    int rageTimer;
    int rageCooldownTimer;
    float prevChaseDist = float.MaxValue;
    int appliedModelMood = -1; // -1: 未設定, 0: 穏やかモデル, 1: キレモデル

    // 目（白目2つ＋黒目2つ）
    readonly SpriteRenderer[] eyeWhites = new SpriteRenderer[2];
    readonly SpriteRenderer[] pupils = new SpriteRenderer[2];

    // 観戦用の通算カウント（スコアボードで表示）
    public int Deaths { get; private set; }
    public int Kills { get; private set; }
    public int BestLength { get; private set; }
    public Color DisplayColor => color;

    public IReadOnlyList<Vector2> Path => path;
    public float Heading => heading;
    public int Length => length;
    public bool HasBody => path.Count > 0;
    public Vector2 HeadWorld => arena.Center + path[0];

    float CurrentDiameter =>
        bodyDiameter * Mathf.Lerp(1f, maxWidthScale, Mathf.Clamp01((length - initialLength) / (float)Mathf.Max(1, lengthForMaxWidth - initialLength)));
    float BodyRadius => CurrentDiameter * 0.5f;
    bool CanBoost => length > minBoostLength;

    public Personality CurrentPersonality => personality;
    public bool IsAngry => personality == Personality.Aggressive || (personality == Personality.Switcher && rageTimer > 0);
    ModeRewards Mode => IsAngry ? aggressiveRewards : calmRewards;
    string StatPrefix => $"Slither/{personality}/";

    // ───────── エディタ用: Behavior Parameters の自動設定 ─────────

#if UNITY_EDITOR
    // コンポーネントを追加したとき（または右クリック → Reset）に呼ばれる
    void Reset()
    {
        var bp = GetComponent<BehaviorParameters>();
        if (bp == null) bp = gameObject.AddComponent<BehaviorParameters>();
        bp.BrainParameters.NumStackedVectorObservations = 1;
        ApplyRewardPreset();
        SyncBehaviorParameters(bp);
    }

    void ApplyRewardPreset()
    {
        personalSpace = 5f;
        calmRewards = new ModeRewards { foodReward = 0.5f, killReward = 0f, nearOthersPenalty = -0.01f, chaseReward = 0f, pressureReward = 0f };
        aggressiveRewards = new ModeRewards { foodReward = 0.2f, killReward = 10f, nearOthersPenalty = 0f, chaseReward = 0f, pressureReward = 0.01f };
        rewardPresetVersion = CurrentRewardPreset;
    }

    // インスペクターを触るたびに観測数・行動の形を合わせる
    void OnValidate()
    {
        bool changed = false;
        if (rewardPresetVersion < CurrentRewardPreset)
        {
            ApplyRewardPreset();
            changed = true;
        }
        var bp = GetComponent<BehaviorParameters>();
        if (bp != null) changed |= SyncBehaviorParameters(bp);
        if (changed) MarkSceneDirtyLater();
    }

    // 自動で書き換えた値がシーンに保存されるように、シーンを「変更あり」にしておく
    void MarkSceneDirtyLater()
    {
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this == null || Application.isPlaying) return;
            UnityEditor.EditorUtility.SetDirty(this);
            var bp = GetComponent<BehaviorParameters>();
            if (bp != null) UnityEditor.EditorUtility.SetDirty(bp);
            if (gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        };
    }

    bool SyncBehaviorParameters(BehaviorParameters bp)
    {
        bool changed = false;
        // 性格ごとに別のAIとして学習するので、Behavior Name を性格に合わせる
        string behaviorName = $"Slither_{personality}";
        if (bp.BehaviorName != behaviorName) { bp.BehaviorName = behaviorName; changed = true; }

        int size = rayCount * 3 + ExtraObservations;
        if (bp.BrainParameters.VectorObservationSize != size)
        {
            bp.BrainParameters.VectorObservationSize = size;
            changed = true;
        }

        var spec = bp.BrainParameters.ActionSpec;
        bool ok = spec.NumContinuousActions == 1
                  && spec.BranchSizes != null && spec.BranchSizes.Length == 1 && spec.BranchSizes[0] == 2;
        if (!ok) { bp.BrainParameters.ActionSpec = new ActionSpec(1, new[] { 2 }); changed = true; }
        return changed;
    }
#endif

    // ───────── 初期化 ─────────

    public override void Initialize()
    {
        // 親の Arena を優先（フィールドを複数並べて並列学習するため）
        // 複製されたときに元のヘビの描画用オブジェクトが付いてきていたら消しておく
        for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);

        if (arena == null) arena = GetComponentInParent<SlitherArena>();
        if (arena == null) arena = FindAnyObjectByType<SlitherArena>();
        if (arena == null) arena = new GameObject("Arena").AddComponent<SlitherArena>();
        arena.EnsureInitialized();
        arena.Register(this);
        CreateEyes();
        Academy.Instance.AgentPreStep += OnAcademyPreStep;
        ApplySwitcherModel();
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
        if (randomColor) color = Color.HSVToRGB(PersonalityHue(), 0.6f, 0.95f);

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
        rageTimer = 0;
        rageCooldownTimer = 0;
        prevChaseDist = float.MaxValue;
        ApplySwitcherModel();
        Render();
    }

    // ───────── 観測（rayCount × 3 + 6） ─────────

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

        // 今キレているか（豹変型はこれを見て穏やか／攻撃的を切り替える）
        if (includeMoodObservation) sensor.AddObservation(IsAngry);

        // 近くの相手3匹：頭の位置（自分の向き基準）・進む向き・大きさ
        if (includeOpponentObservation) AddOpponentObservations(sensor, head);
    }

    readonly List<SlitherAgent> opponentBuffer = new List<SlitherAgent>();

    void AddOpponentObservations(VectorSensor sensor, Vector2 head)
    {
        opponentBuffer.Clear();
        foreach (var s in arena.snakes)
            if (s != this && s.HasBody) opponentBuffer.Add(s);
        opponentBuffer.Sort((a, b) => (a.Path[0] - head).sqrMagnitude.CompareTo((b.Path[0] - head).sqrMagnitude));

        const float range = 20f;
        float c = Mathf.Cos(-heading), sn = Mathf.Sin(-heading);
        for (int i = 0; i < OpponentSlots; i++)
        {
            if (i >= opponentBuffer.Count)
            {
                for (int k = 0; k < OpponentFeatures; k++) sensor.AddObservation(0f);
                continue;
            }
            var o = opponentBuffer[i];
            Vector2 d = o.Path[0] - head;
            float lx = d.x * c - d.y * sn; // 自分の向きを x 軸にした座標
            float ly = d.x * sn + d.y * c;
            float rel = o.Heading - heading;
            sensor.AddObservation(1f);                                 // いるかどうか
            sensor.AddObservation(Mathf.Clamp(lx / range, -1f, 1f));   // 前後
            sensor.AddObservation(Mathf.Clamp(ly / range, -1f, 1f));   // 左右
            sensor.AddObservation(Mathf.Sin(rel));                     // 相手の進む向き
            sensor.AddObservation(Mathf.Cos(rel));
            sensor.AddObservation(Mathf.Clamp(o.Length / (float)Mathf.Max(1, length), 0f, 3f) / 3f); // 相手の大きさ
        }
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

        UpdateRage();
        ApplySwitcherModel();
        ApplyPersonalityRewards();

        if (eaten > 0)
        {
            AddReward(Mode.foodReward * eaten);
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
                AddReward(-Mode.foodReward);
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
            Academy.Instance.StatsRecorder.Add(StatPrefix + "Length", length);
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

        var killer = HitsBody(newHead);
        if (killer != null)
        {
            if (killer != this) killer.OnKilledOther();
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
        if (length > BestLength) BestLength = length;
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

    // ───────── 性格 ─────────

    float PersonalityHue()
    {
        switch (personality)
        {
            case Personality.Calm: return Random.Range(0.25f, 0.6f);        // 緑〜水色〜青
            case Personality.Aggressive:                                      // 赤〜オレンジ
                float h = Random.Range(-0.04f, 0.1f);
                return h < 0f ? h + 1f : h;
            default: return Random.Range(0.72f, 0.85f);                      // 紫
        }
    }

    // 豹変型: 相手に近づかれる or たまにランダムでキレる。一定時間で落ち着く
    void UpdateRage()
    {
        if (personality != Personality.Switcher) return;
        if (rageTimer > 0)
        {
            rageTimer--;
            if (rageTimer == 0) rageCooldownTimer = rageCooldown; // 落ち着いた。しばらくはキレない
            return;
        }
        if (rageCooldownTimer > 0)
        {
            rageCooldownTimer--;
            return;
        }
        NearestOthers(out _, out float headDist);
        if (headDist < provokeDistance || Random.value < randomRageChance)
        {
            rageTimer = rageDuration;
            prevChaseDist = float.MaxValue;
            Academy.Instance.StatsRecorder.Add(StatPrefix + "Rage", 1f, StatAggregationMethod.Sum);
        }
    }

    // 豹変型: キレたら攻撃的なモデル、落ち着いたら穏やかなモデルに頭脳ごと入れ替える（学習中は何もしない）
    void ApplySwitcherModel()
    {
        if (personality != Personality.Switcher) return;
        if (calmModel == null || angryModel == null) return;
        if (Academy.Instance.IsCommunicatorOn) return;

        int mood = IsAngry ? 1 : 0;
        if (mood == appliedModelMood) return;
        appliedModelMood = mood;

        var bp = GetComponent<BehaviorParameters>();
        // Model の型（ModelAsset）を直接書かずに済むように、プロパティ経由で入れ替える
        var prop = typeof(BehaviorParameters).GetProperty("Model");
        prop?.SetValue(bp, mood == 1 ? angryModel : calmModel);
    }

    void ApplyPersonalityRewards()
    {
        var mode = Mode;
        if (mode.nearOthersPenalty == 0f && mode.chaseReward == 0f && mode.pressureReward == 0f) return;

        if (mode.pressureReward != 0f)
        {
            int blocked = CountBlockedOpponents();
            if (blocked > 0) AddReward(mode.pressureReward * blocked);
        }
        if (mode.nearOthersPenalty == 0f && mode.chaseReward == 0f) return;

        NearestOthers(out float bodyDist, out float headDist);

        if (mode.nearOthersPenalty != 0f && bodyDist < personalSpace)
            AddReward(mode.nearOthersPenalty);

        if (mode.chaseReward != 0f)
        {
            if (headDist < chaseRange)
            {
                if (prevChaseDist < chaseRange)
                {
                    // 近づいた分だけ加点（離れたら減点）。相手が入れ替わったときの飛びは抑える
                    float delta = Mathf.Clamp(prevChaseDist - headDist, -1f, 1f);
                    AddReward(mode.chaseReward * delta);
                }
                prevChaseDist = headDist;
            }
            else prevChaseDist = float.MaxValue;
        }
    }

    // 自分の体の目の前に頭があって、しかもこちらに向かって進んでいる相手の数
    int CountBlockedOpponents()
    {
        int count = 0;
        float r2 = pressureRadius * pressureRadius;
        foreach (var s in arena.snakes)
        {
            if (s == this || !s.HasBody) continue;
            Vector2 h = s.Path[0];
            var dir = new Vector2(Mathf.Cos(s.Heading), Mathf.Sin(s.Heading));
            for (int j = 4; j < path.Count; j += 2) // 自分の頭まわりは除く
            {
                var d = path[j] - h;
                if (d.sqrMagnitude < r2 && Vector2.Dot(d, dir) > 0f) { count++; break; }
            }
        }
        return count;
    }

    // 一番近い「他のヘビの体」と「他のヘビの頭」までの距離
    void NearestOthers(out float bodyDist, out float headDist)
    {
        float body2 = float.MaxValue, head2 = float.MaxValue;
        var head = path[0];
        foreach (var s in arena.snakes)
        {
            if (s == this || !s.HasBody) continue;
            var p = s.Path;
            head2 = Mathf.Min(head2, (p[0] - head).sqrMagnitude);
            for (int j = 0; j < p.Count; j += 2)
                body2 = Mathf.Min(body2, (p[j] - head).sqrMagnitude);
        }
        bodyDist = body2 == float.MaxValue ? float.MaxValue : Mathf.Sqrt(body2);
        headDist = head2 == float.MaxValue ? float.MaxValue : Mathf.Sqrt(head2);
    }

    // 相手が自分の体にぶつかって死んだ
    public void OnKilledOther()
    {
        Kills++;
        AddReward(Mode.killReward);
        Academy.Instance.StatsRecorder.Add(StatPrefix + "Kills", 1f, StatAggregationMethod.Sum);
    }

    // ───────── 当たり判定 ─────────

    bool HitsWall(Vector2 p) => p.magnitude > arena.radius - BodyRadius;

    // ぶつかった体の持ち主を返す（ぶつかっていなければ null）
    SlitherAgent HitsBody(Vector2 head)
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
                if ((p[j] - head).sqrMagnitude < hit2) return s;
        }
        return null;
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
        Deaths++;
        AddReward(penalty);
        // 本家のように体がエサになってばらまかれる
        for (int i = 1; i < path.Count; i += 2)
            arena.AddFood(path[i] + Random.insideUnitCircle * 0.3f);
        EndEpisodeWithStats();
    }

    void EndEpisodeWithStats()
    {
        Academy.Instance.StatsRecorder.Add("Slither/Length", length);
        Academy.Instance.StatsRecorder.Add(StatPrefix + "Length", length);
        // EndEpisode の中で最後の観測（CollectObservations）が呼ばれるので、
        // ここでは体を消さない。OnEpisodeBegin で作り直される
        EndEpisode(); // すぐに OnEpisodeBegin が呼ばれて復活する
    }

    // ───────── 描画 ─────────

    void Render()
    {
        while (segmentRenderers.Count < path.Count)
            segmentRenderers.Add(SlitherArena.CreateCircle(transform, "Segment", color, 5, bodyDiameter));

        // 豹変型がキレている間は赤くなる。ブースト中は少し明るく光らせる
        var baseColor = (personality == Personality.Switcher && IsAngry) ? Color.Lerp(color, new Color(1f, 0.15f, 0.15f), 0.75f) : color;
        if (boosting) baseColor = Color.Lerp(baseColor, Color.white, 0.35f);
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
        bool angry = IsAngry;
        // キレているときは白目が赤く、黒目が小さく（にらんでいる感じ）
        float whiteSize = diameter * (angry ? 0.46f : 0.42f);
        float pupilSize = diameter * (angry ? 0.16f : 0.24f);
        var whiteColor = angry ? new Color(1f, 0.55f, 0.55f) : Color.white;

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
            eyeWhites[i].color = whiteColor;

            pupils[i].transform.position = eyePos + lookDir * pupilOffset;
            pupils[i].transform.localScale = Vector3.one * pupilSize;
        }
    }
}

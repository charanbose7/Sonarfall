using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// THE only thing you place in the scene by hand. Drop this on one empty GameObject and
/// press Play — it configures the platform (portrait, 60fps, vsync), then builds the
/// camera, vignette, managers, player (with trail), exit, particles and audio, wires
/// them together, and starts the game. No prefabs, no manual references.
/// </summary>
public class GameBootstrap : MonoBehaviour
{
    private Material _glowMat;
    private Sprite _glowSprite;

    private void Awake()
    {
        // ---- Platform / performance ----
        Application.targetFrameRate = GameConfig.TargetFrameRate;
        QualitySettings.vSyncCount = 0;                 // let targetFrameRate govern the cadence
        Screen.orientation = ScreenOrientation.Portrait; // mobile: lock portrait
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
    }

    private void Start()
    {
        // ---- EventSystem ----
        // REQUIRED for uGUI buttons to receive taps. Everything else in this game polls input
        // directly (EchoInput), which needs no EventSystem — so without this, menu/settings
        // buttons are completely dead. The project runs the new Input System only
        // (activeInputHandler = 1), so it must be InputSystemUIInputModule, not the legacy one.
        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            var module = esGO.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            // Created at runtime it has no action asset, so give it the stock UI actions.
            if (module.actionsAsset == null) module.AssignDefaultActions();
#else
            esGO.AddComponent<StandaloneInputModule>();
#endif
        }

        // The trail's own material: a LineRenderer has no sprite, so it stays untextured (white).
        // Every sprite takes its material from VisualUtils.AdditiveFor instead.
        _glowMat = new Material(Shader.Find("Sonarfall/Additive")) { name = "TrailMat" };
        _glowSprite = VisualUtils.RadialGlow();

        // ---- Camera ----
        var camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = GameConfig.BackgroundColor;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.nearClipPlane = 0.1f;
        camGO.AddComponent<AudioListener>();

        // ---- Vignette (child of camera) ----
        var vigGO = new GameObject("Vignette");
        vigGO.transform.SetParent(camGO.transform, false);
        vigGO.transform.localPosition = new Vector3(0f, 0f, 1f);
        var vigSR = vigGO.AddComponent<SpriteRenderer>();
        vigSR.sprite = VisualUtils.Vignette();
        vigSR.color = Color.white;
        vigSR.sortingOrder = 90;

        // ---- Managers ----
        var root = new GameObject("Sonarfall");
        var gm = root.AddComponent<GameManager>();
        var sonar = root.AddComponent<SonarManager>();
        var ui = root.AddComponent<UIManager>();
        var audio = root.AddComponent<ProceduralAudio>();
        var wallCtrl = root.AddComponent<WallShaderController>();
        var fx = root.AddComponent<FxManager>();

        wallCtrl.Init();
        audio.Init();
        sonar.Init(audio);
        ui.BuildUI();
        fx.Init(cam);

        // ---- Player ----
        var playerGO = new GameObject("Player");
        var rb = playerGO.AddComponent<Rigidbody2D>();
        // Kinematic: we move the dot directly at frame-rate and resolve walls with casts,
        // so there's zero physics-step input lag. No interpolation for the same reason.
        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation2D.None;
        var pcol = playerGO.AddComponent<CircleCollider2D>();
        pcol.radius = GameConfig.PlayerRadius;

        var glow = new GameObject("Glow");
        glow.transform.SetParent(playerGO.transform, false);
        var glowSR = glow.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(glowSR, _glowSprite);
        glowSR.color = GameConfig.PlayerColor;
        glowSR.sortingOrder = 50;
        glow.transform.localScale = Vector3.one * GameConfig.PlayerGlowScale;

        // Beacon halo behind the dot — a soft pulsing aura that keeps the eye on the player.
        var halo = new GameObject("Halo");
        halo.transform.SetParent(playerGO.transform, false);
        var haloSR = halo.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(haloSR, _glowSprite);
        haloSR.color = GameConfig.PlayerColor;
        haloSR.sortingOrder = 49; // behind the core glow (50)
        halo.AddComponent<PulseGlow>().Configure(
            GameConfig.PlayerColor, GameConfig.PlayerGlowScale * 1.9f, GameConfig.PlayerGlowScale * 0.3f,
            0.22f, 0.16f, 2.4f);

        // Motion trail.
        var trailGO = new GameObject("Trail");
        trailGO.transform.SetParent(playerGO.transform, false);
        var trail = trailGO.AddComponent<TrailRenderer>();
        trail.time = GameConfig.TrailTime;
        trail.startWidth = GameConfig.TrailWidth; trail.endWidth = 0f;
        trail.numCapVertices = 4;
        trail.minVertexDistance = 0.02f;
        trail.autodestruct = false;
        trail.sharedMaterial = _glowMat;
        trail.sortingOrder = 45;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(GameConfig.PlayerColor, 0f), new GradientColorKey(GameConfig.PlayerColor, 1f) },
            new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
        trail.colorGradient = grad;

        var player = playerGO.AddComponent<PlayerController>();
        player.Init(gm, sonar, cam, glow.transform, fx, trail, audio);
        sonar.SetPlayer(playerGO.transform);

        // ---- Exit glow ----
        var exitGO = new GameObject("Exit");
        var exitSR = exitGO.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(exitSR, _glowSprite);
        exitSR.color = GameConfig.ExitColor;
        exitSR.sortingOrder = 30;

        // ---- Wire up and go ----
        gm.Init(cam, player, sonar, ui, audio, wallCtrl, fx, exitSR, vigGO.transform);
        gm.StartGame();
    }
}

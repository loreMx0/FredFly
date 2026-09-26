using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace LumaflyFredCompanion
{
    public class LampBugController : MonoBehaviour
    {
        // ---------------- config ----------------
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _respawnKey;
        private ConfigEntry<bool> _logDiagnostics;

        private ConfigEntry<float> _offsetX;
        private ConfigEntry<float> _offsetY;
        private ConfigEntry<float> _followLag;

        private ConfigEntry<float> _idleRadius;
        private ConfigEntry<float> _returnRadius;
        private ConfigEntry<float> _returnSmoothTime;
        private ConfigEntry<float> _returnMaxSpeed;
        private ConfigEntry<bool> _snapOnSceneChange;
        private ConfigEntry<float> _sceneSnapDelay;
        private ConfigEntry<float> _teleportSnapDistance;
        private ConfigEntry<float> _spawnBeyondEdgeFactor;

        private ConfigEntry<string> _spriteSet;
        private ConfigEntry<string> _framesFolder;
        private ConfigEntry<string> _filePattern;
        private ConfigEntry<float> _fps;
        private ConfigEntry<float> _spriteScale;
        private ConfigEntry<float> _spriteScaleY;

        private ConfigEntry<bool> _drawTrail;
        private ConfigEntry<string> _glowColorHex;
        private ConfigEntry<float> _glowRadius;
        private ConfigEntry<string> _sortingLayer;
        private ConfigEntry<int> _sortingOrder;

        // ---------------- attack config ----------------
        private ConfigEntry<bool> _attackEnabled;
        private ConfigEntry<float> _attackInterval;
        private ConfigEntry<int> _attackDamage;
        private ConfigEntry<int> _attackJackpotDamage;
        private ConfigEntry<int> _attackJackpotChance;
        private ConfigEntry<float> _attackRange;
        private ConfigEntry<float> _attackDashSpeed;
        private ConfigEntry<float> _attackFlashSeconds;

        // ---------------- runtime ----------------
        private GameObject _root;
        private Transform _t;
        private MonoBehaviour _hero;

        private SpriteRenderer _bugSR;
        private SpriteRenderer _glowSR;
        private TrailRenderer _trail;
        private Material _trailMat;

        private readonly List<Sprite> _frameSprites = new List<Sprite>();
        private readonly List<Texture2D> _frameTextures = new List<Texture2D>();
        private Sprite _glowSprite;
        private Texture2D _glowTexture;

        private int _frameIndex;
        private float _frameTimer;

        private Vector3 _anchorPos;
        private Vector3 _velAnchor;

        private Vector3 _segStart;
        private Vector3 _segControl;
        private Vector3 _segEnd;
        private float _segTime;
        private float _segDuration;
        private bool _isDart;
        private bool _isReturning;
        private Vector3 _returnVel;

        private bool _waitingSceneSnap;
        private bool _facingRight = true;

        private int _diagCounter;
        private bool _firstTickLogged;
        private bool _loggedCandidatesOnce;

        private Color _glowColorValue = new Color(1f, 0.8f, 1f, 0.9f);

        // ---------------- attack runtime ----------------
        private float _attackTimer;
        private bool _isAttacking;
        private float _attackStartTime;
        private MonoBehaviour _attackTarget;
        private float _flashUntil;
        private Color _flashColor = Color.white;

        private const float ApproachTime = 0.18f;
        private const float MeanderMin = 1.6f;
        private const float MeanderMax = 3.8f;
        private const float DartMin = 0.14f;
        private const float DartMax = 0.34f;
        private const float MeanderRMax = 0.92f;
        private const float MeanderRMin = 0.25f;
        private const float DartRMax = 0.35f;
        private const float DartRMin = 0.13f;
        private const float AttackTimeoutSeconds = 2.5f;
        private const float AttackHitDistance = 0.9f;

        private static Type _heroType;
        private static PropertyInfo _heroInstanceProp;
        private static FieldInfo _heroInstanceField;

        private static Type _healthManagerType;
        private static FieldInfo _hpField;
        private static MethodInfo _damageMethod;
        private static string _damageMethodSignature;
        private static bool _damageResolveAttempted;
        private static int _enemyLayer = -2;
        private static MethodInfo _findByTypeMethod;   // cached reflection handle
        private static bool _findByTypeResolved;

        private MethodInfo _miLoadImageBool;
        private MethodInfo _miLoadImageNoBool;
        private MethodInfo _miTexLoadImage;

        // =====================================================================
        //  Path resolution
        // =====================================================================

        private static List<string> CandidateBaseDirs()
        {
            var list = new List<string>();

            try
            {
                string p = Paths.PluginPath;
                if (!string.IsNullOrEmpty(p)) list.Add(p);
            }
            catch { }

            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) list.Add(dir);
                }
            }
            catch { }

            try
            {
                string cwd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrEmpty(cwd)) list.Add(cwd);
            }
            catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<string>();
            foreach (var s in list)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (seen.Add(s)) unique.Add(s);
            }
            return unique;
        }

        private static string SafeCombine(string a, string b)
        {
            try
            {
                if (string.IsNullOrEmpty(a)) return b;
                return Path.Combine(a, b);
            }
            catch { return null; }
        }

        private string ResolveFramesDir()
        {
            var candidates = CandidateBaseDirs();

            if (!_loggedCandidatesOnce)
            {
                _loggedCandidatesOnce = true;
                for (int i = 0; i < candidates.Count; i++)
                    Plugin.Log?.LogInfo("[lumafly] candidate base dir [" + i + "]: " + candidates[i]);
            }

            if (!string.IsNullOrWhiteSpace(_framesFolder.Value) && Path.IsPathRooted(_framesFolder.Value))
                return _framesFolder.Value;

            string spriteSet = _spriteSet.Value ?? "Fred";

            if (!string.IsNullOrWhiteSpace(_framesFolder.Value))
            {
                foreach (var baseDir in candidates)
                {
                    string full = SafeCombine(baseDir, _framesFolder.Value);
                    if (full != null && Directory.Exists(full))
                    {
                        Plugin.Log?.LogInfo("[lumafly] resolved custom FramesFolder at: " + full);
                        return full;
                    }
                }
                return SafeCombine(candidates.Count > 0 ? candidates[0] : "", _framesFolder.Value)
                    ?? _framesFolder.Value;
            }

            foreach (var baseDir in candidates)
            {
                string full = SafeCombine(baseDir, spriteSet);
                if (full != null && Directory.Exists(full))
                {
                    Plugin.Log?.LogInfo("[lumafly] resolved SpriteSet folder at: " + full);
                    return full;
                }
            }

            Plugin.Log?.LogWarning("[lumafly] Could not find folder '" + spriteSet + "'. Tried:");
            foreach (var baseDir in candidates)
                Plugin.Log?.LogWarning("[lumafly]   " + SafeCombine(baseDir, spriteSet));

            return SafeCombine(candidates.Count > 0 ? candidates[0] : "", spriteSet)
                ?? spriteSet;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================

        private static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string s = hex.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);

            try
            {
                if (s.Length == 6)
                {
                    byte r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                    return new Color(r / 255f, g / 255f, b / 255f, 1f);
                }
                if (s.Length == 8)
                {
                    byte r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                    byte a = byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber);
                    return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] ParseHexColor failed for '" + hex + "': " + ex.Message);
            }
            return fallback;
        }

        private static MonoBehaviour FindHero()
        {
            try
            {
                if (_heroType == null)
                {
                    _heroType = AccessTools.TypeByName("HeroController")
                             ?? AccessTools.TypeByName("TeamCherry.HeroController");
                    if (_heroType != null)
                    {
                        _heroInstanceProp = _heroType.GetProperty("instance",
                            BindingFlags.Public | BindingFlags.Static);
                        if (_heroInstanceProp == null)
                            _heroInstanceField = _heroType.GetField("instance",
                                BindingFlags.Public | BindingFlags.Static);
                        Plugin.Log?.LogInfo("[lumafly] HeroController type resolved: " + _heroType.FullName);
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[lumafly] HeroController type NOT found");
                    }
                }
                if (_heroType == null) return null;
                object inst = _heroInstanceProp != null
                    ? _heroInstanceProp.GetValue(null)
                    : _heroInstanceField?.GetValue(null);
                return inst as MonoBehaviour;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] FindHero exception: " + ex.Message);
                return null;
            }
        }

        // =====================================================================
        //  Init
        // =====================================================================

        public void Init(ConfigFile cfg, ManualLogSource log)
        {
            Plugin.Log?.LogInfo("[lumafly] Init() entered");
            try
            {
                CacheLoadImageMethods();

                _enabled = cfg.Bind<bool>("General", "Enabled", true, "Show/hide companion.");
                _toggleKey = cfg.Bind<KeyCode>("General", "ToggleKey", KeyCode.F10, "Desktop only.");
                _respawnKey = cfg.Bind<KeyCode>("General", "RespawnKey", KeyCode.F11, "Desktop only.");
                _logDiagnostics = cfg.Bind<bool>("General", "LogDiagnostics", false, "Periodic state logging.");

                _offsetX = cfg.Bind<float>("Follow", "OffsetX", -0.25f, "Offset from Hornet X.");
                _offsetY = cfg.Bind<float>("Follow", "OffsetY", 0.55f, "Offset from Hornet Y.");
                _followLag = cfg.Bind<float>("Follow", "FollowLag", 0.12f, "Anchor smoothing.");

                _idleRadius = cfg.Bind<float>("Motion", "IdleRadius", 1.6f, "Drift radius.");
                _returnRadius = cfg.Bind<float>("Motion", "ReturnRadius", 3f, "Direct-return distance.");
                _returnSmoothTime = cfg.Bind<float>("Motion", "ReturnSmoothTime", 0.12f, "Return smooth time.");
                _returnMaxSpeed = cfg.Bind<float>("Motion", "ReturnMaxSpeed", 6f, "Return max speed.");
                _snapOnSceneChange = cfg.Bind<bool>("Motion", "SnapOnSceneChange", true, "Reposition on scene change.");
                _sceneSnapDelay = cfg.Bind<float>("Motion", "SceneSnapDelay", 0.05f, "Snap delay.");
                _teleportSnapDistance = cfg.Bind<float>("Motion", "TeleportSnapDistance", 12f, "Hard-snap distance.");
                _spawnBeyondEdgeFactor = cfg.Bind<float>("Motion", "SpawnBeyondEdgeFactor", 1.15f, "Edge spawn factor.");

                _spriteSet = cfg.Bind<string>("Sprite", "SpriteSet", "Fred", "Frame folder name.");
                _framesFolder = cfg.Bind<string>("Sprite", "FramesFolder", "", "Optional custom folder.");
                _filePattern = cfg.Bind<string>("Sprite", "FilePattern", "bug*.png", "Frame filename pattern.");
                _fps = cfg.Bind<float>("Sprite", "FramesPerSecond", 12f, "Animation FPS.");
                _spriteScale = cfg.Bind<float>("Sprite", "SpriteScale", 1.25f,
                    new ConfigDescription("Uniform scale.", new AcceptableValueRange<float>(0.05f, 10f), Array.Empty<object>()));
                _spriteScaleY = cfg.Bind<float>("Sprite", "SpriteScaleY", 1.25f,
                    new ConfigDescription("Vertical scale override.", new AcceptableValueRange<float>(0.05f, 10f), Array.Empty<object>()));

                _drawTrail = cfg.Bind<bool>("Visual", "DrawTrail", false, "Trailing sparkle (off by default).");
                _glowColorHex = cfg.Bind<string>("Visual", "GlowColorHex", "FFCCFFE6", "Glow tint hex.");
                _glowRadius = cfg.Bind<float>("Visual", "GlowRadius", 0.7f, "Glow radius scale.");
                _sortingLayer = cfg.Bind<string>("Visual", "SortingLayer", "Foreground", "Sprite sorting layer.");
                _sortingOrder = cfg.Bind<int>("Visual", "SortingOrder", 32760, "Sorting order.");

                // ----- attack config -----
                _attackEnabled = cfg.Bind<bool>("Attack", "Enabled", true, "If true, the bug attacks the nearest enemy every N seconds.");
                _attackInterval = cfg.Bind<float>("Attack", "Interval", 10f, "Seconds between attacks.");
                _attackDamage = cfg.Bind<int>("Attack", "Damage", 1, "Normal damage per hit.");
                _attackJackpotChance = cfg.Bind<int>("Attack", "JackpotChance", 1000, "1 in N chance to deal JackpotDamage. Set to 0 to disable.");
                _attackJackpotDamage = cfg.Bind<int>("Attack", "JackpotDamage", 999, "Damage dealt on a jackpot hit.");
                _attackRange = cfg.Bind<float>("Attack", "Range", 8f, "Maximum distance from the bug to an enemy to trigger an attack.");
                _attackDashSpeed = cfg.Bind<float>("Attack", "DashSpeed", 25f, "How fast the bug darts toward the target.");
                _attackFlashSeconds = cfg.Bind<float>("Attack", "FlashSeconds", 0.4f, "Duration of the gold flash after a hit.");

                _glowColorValue = ParseHexColor(_glowColorHex.Value, new Color(1f, 0.8f, 1f, 0.9f));

                // ----- setting changed hooks -----
                _enabled.SettingChanged += (_, __) => { if (_root != null) _root.SetActive(_enabled.Value); };
                _spriteSet.SettingChanged += (_, __) => Reload();
                _framesFolder.SettingChanged += (_, __) => { _loggedCandidatesOnce = false; Reload(); };
                _filePattern.SettingChanged += (_, __) => Reload();
                _spriteScale.SettingChanged += (_, __) => ApplyScale();
                _spriteScaleY.SettingChanged += (_, __) => ApplyScale();
                _glowColorHex.SettingChanged += (_, __) =>
                {
                    _glowColorValue = ParseHexColor(_glowColorHex.Value, new Color(1f, 0.8f, 1f, 0.9f));
                    RebuildGlowTexture();
                    SyncTrail();
                };
                _glowRadius.SettingChanged += (_, __) => ApplyGlowScale();
                _sortingLayer.SettingChanged += (_, __) => ApplySorting();
                _sortingOrder.SettingChanged += (_, __) => ApplySorting();
                _drawTrail.SettingChanged += (_, __) => SyncTrail();

                Plugin.Log?.LogInfo("[lumafly] Config bound. Calling CreateAll()");
                CreateAll();
                Plugin.Log?.LogInfo("[lumafly] CreateAll() completed");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("[lumafly] Init() failed: " + ex);
            }
        }

        private void CacheLoadImageMethods()
        {
            try
            {
                Type t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false)
                      ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine.CoreModule", false)
                      ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine", false);
                if (t != null)
                {
                    _miLoadImageBool = t.GetMethod("LoadImage",
                        BindingFlags.Static | BindingFlags.Public, null,
                        new Type[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }, null);
                    if (_miLoadImageBool == null)
                        _miLoadImageNoBool = t.GetMethod("LoadImage",
                            BindingFlags.Static | BindingFlags.Public, null,
                            new Type[] { typeof(Texture2D), typeof(byte[]) }, null);
                }
                _miTexLoadImage = typeof(Texture2D).GetMethod("LoadImage",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(byte[]) }, null);

                Plugin.Log?.LogInfo(
                    "[lumafly] ImageConversion static? " + (t != null)
                    + " bool-overload? " + (_miLoadImageBool != null)
                    + " byte-overload? " + (_miLoadImageNoBool != null)
                    + " instance? " + (_miTexLoadImage != null));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] CacheLoadImageMethods: " + ex.Message);
            }
        }

        private bool ApplyImageToTexture(Texture2D tex, byte[] data)
        {
            try
            {
                if (tex == null || data == null) return false;
                if (_miLoadImageBool != null)
                    return (bool)_miLoadImageBool.Invoke(null, new object[] { tex, data, false });
                if (_miLoadImageNoBool != null)
                    return (bool)_miLoadImageNoBool.Invoke(null, new object[] { tex, data });
                if (_miTexLoadImage != null)
                    return (bool)_miTexLoadImage.Invoke(tex, new object[] { data });
                return false;
            }
            catch { return false; }
        }

        // =====================================================================
        //  Scene setup
        // =====================================================================

        private void CreateAll()
        {
            if (_root != null) Object.Destroy(_root);

            _root = new GameObject("Lumafly");
            Object.DontDestroyOnLoad(_root);
            _t = _root.transform;

            GameObject bugGO = new GameObject("Bug");
            bugGO.transform.SetParent(_t, false);
            _bugSR = bugGO.AddComponent<SpriteRenderer>();

            GameObject glowGO = new GameObject("Glow");
            glowGO.transform.SetParent(_t, false);
            _glowSR = glowGO.AddComponent<SpriteRenderer>();

            LoadFrames();

            if (_frameSprites.Count == 0)
            {
                Texture2D mag = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                Color[] px = new Color[32 * 32];
                for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 0f, 1f, 1f);
                mag.SetPixels(px);
                mag.Apply();
                _frameTextures.Add(mag);
                _frameSprites.Add(MakeSprite(mag, "magenta"));
                Plugin.Log?.LogWarning("[lumafly] No frames loaded — using magenta placeholder");
            }

            _bugSR.sprite = _frameSprites[0];
            ApplyScale();
            ApplySorting();

            _glowTexture = BuildGlowTexture(128);
            _glowSprite = MakeSprite(_glowTexture, "glow");
            _glowSR.sprite = _glowSprite;
            _glowSR.color = Color.white;
            ApplyGlowScale();
            try
            {
                _glowSR.sortingLayerName = _sortingLayer.Value;
                _glowSR.sortingOrder = _sortingOrder.Value - 1;
            }
            catch { }

            SyncTrail();

            _segStart = _segEnd = _anchorPos = _t.position;

            SceneManager.activeSceneChanged -= OnSceneChanged;
            SceneManager.activeSceneChanged += OnSceneChanged;

            _root.SetActive(_enabled.Value);
            Plugin.Log?.LogInfo("[lumafly] Root active = " + _enabled.Value);
        }

        private static Sprite MakeSprite(Texture2D tex, string name)
        {
            Sprite s = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            s.name = name;
            return s;
        }

        private void LoadFrames()
        {
            foreach (var s in _frameSprites) if (s != null) Object.Destroy(s);
            foreach (var t in _frameTextures) if (t != null) Object.Destroy(t);
            _frameSprites.Clear();
            _frameTextures.Clear();

            string dir = ResolveFramesDir();
            Plugin.Log?.LogInfo("[lumafly] Loading frames from: " + dir);

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Plugin.Log?.LogWarning("[lumafly] Folder does not exist: " + dir);
                return;
            }

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, _filePattern.Value, SearchOption.TopDirectoryOnly)
                    .OrderBy(NaturalKey).ToList();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("[lumafly] EnumerateFiles failed: " + ex.Message);
                return;
            }

            Plugin.Log?.LogInfo("[lumafly] Found " + files.Count + " files");

            foreach (var file in files)
            {
                Texture2D tex = LoadPng(file);
                if (tex == null)
                {
                    Plugin.Log?.LogWarning("[lumafly] Failed to load " + Path.GetFileName(file));
                    continue;
                }
                _frameTextures.Add(tex);
                _frameSprites.Add(MakeSprite(tex, Path.GetFileNameWithoutExtension(file)));
                Plugin.Log?.LogInfo("[lumafly] Loaded " + Path.GetFileName(file)
                    + " (" + tex.width + "x" + tex.height + ")");
            }
        }

        private Texture2D LoadPng(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear;
                if (!ApplyImageToTexture(tex, data))
                {
                    Object.Destroy(tex);
                    return null;
                }
                return tex;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] LoadPng " + path + ": " + ex.Message);
                return null;
            }
        }

        private string NaturalKey(string p)
        {
            return Regex.Replace(Path.GetFileNameWithoutExtension(p), "\\d+",
                m => m.Value.PadLeft(6, '0'));
        }

        private Texture2D BuildGlowTexture(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            float cx = size * 0.5f, cy = size * 0.5f, rad = size * 0.5f;
            Color c = _glowColorValue;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - cx) / rad;
                    float dy = (y - cy) / rad;
                    float f = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)), 2.2f);
                    tex.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * f));
                }
            }
            tex.Apply();
            return tex;
        }

        private void RebuildGlowTexture()
        {
            if (_glowSR == null) return;
            if (_glowTexture != null) Object.Destroy(_glowTexture);
            if (_glowSprite != null) Object.Destroy(_glowSprite);
            _glowTexture = BuildGlowTexture(128);
            _glowSprite = MakeSprite(_glowTexture, "glow");
            _glowSR.sprite = _glowSprite;
        }

        // =====================================================================
        //  Apply config
        // =====================================================================

        private void ApplyScale()
        {
            if (_bugSR == null) return;
            float sx = Mathf.Clamp(_spriteScale?.Value ?? 1.25f, 0.05f, 10f);
            float sy = Mathf.Clamp(_spriteScaleY?.Value ?? sx, 0.05f, 10f);
            float dir = _facingRight ? 1f : -1f;
            _bugSR.transform.localScale = new Vector3(dir * sx, sy, 1f);
        }

        private void ApplyGlowScale()
        {
            if (_glowSR == null) return;
            float r = Mathf.Max(0.05f, _glowRadius?.Value ?? 0.7f);
            _glowSR.transform.localScale = new Vector3(r, r, 1f);
        }

        private void ApplySorting()
        {
            if (_bugSR != null)
            {
                try
                {
                    _bugSR.sortingLayerName = _sortingLayer.Value;
                    _bugSR.sortingOrder = _sortingOrder.Value;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[lumafly] Bug sorting failed: " + ex.Message);
                }
            }
            if (_glowSR != null)
            {
                try
                {
                    _glowSR.sortingLayerName = _sortingLayer.Value;
                    _glowSR.sortingOrder = _sortingOrder.Value - 1;
                }
                catch { }
            }
        }

        private void SyncTrail()
        {
            if (_root == null) return;
            if (_drawTrail == null || !_drawTrail.Value)
            {
                if (_trail != null) { Object.Destroy(_trail); _trail = null; }
                if (_trailMat != null) { Object.Destroy(_trailMat); _trailMat = null; }
                return;
            }
            if (_trail == null)
            {
                try
                {
                    Shader sh = Shader.Find("Sprites/Default")
                             ?? Shader.Find("Particles/Standard Unlit")
                             ?? Shader.Find("Unlit/Transparent")
                             ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                    if (sh == null)
                    {
                        Plugin.Log?.LogWarning("[lumafly] No shader for trail — disabling");
                        _drawTrail.Value = false;
                        return;
                    }
                    _trailMat = new Material(sh);
                    _trail = _root.AddComponent<TrailRenderer>();
                    _trail.material = _trailMat;
                    _trail.time = 0.35f;
                    _trail.minVertexDistance = 0.01f;
                    _trail.startWidth = 0.1f;
                    _trail.endWidth = 0f;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[lumafly] Trail setup failed: " + ex.Message);
                    _trail = null;
                    if (_trailMat != null) { Object.Destroy(_trailMat); _trailMat = null; }
                    return;
                }
            }
            if (_trail != null)
            {
                _trail.startColor = new Color(_glowColorValue.r, _glowColorValue.g, _glowColorValue.b, 0.65f);
                _trail.endColor = new Color(_glowColorValue.r, _glowColorValue.g, _glowColorValue.b, 0f);
                _trail.emitting = true;
            }
        }

        // =====================================================================
        //  Attack system
        // =====================================================================

        private static void ResolveDamageApi()
        {
            if (_damageResolveAttempted) return;
            _damageResolveAttempted = true;

            _healthManagerType = AccessTools.TypeByName("HealthManager");
            if (_healthManagerType == null)
            {
                Plugin.Log?.LogWarning("[lumafly] HealthManager type not found — attacks disabled");
                return;
            }

            _hpField = _healthManagerType.GetField("hp",
                BindingFlags.Public | BindingFlags.Instance);

            string[] names = { "TakeDamage", "Hit", "ApplyDamage", "Damage" };
            Type[][] sigs =
            {
                new Type[] { typeof(GameObject), typeof(int) },
                new Type[] { typeof(GameObject), typeof(int), typeof(bool) },
                new Type[] { typeof(int) },
                new Type[] { typeof(int), typeof(bool) },
            };

            foreach (var name in names)
            {
                foreach (var sig in sigs)
                {
                    var m = _healthManagerType.GetMethod(name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null, sig, null);
                    if (m != null)
                    {
                        _damageMethod = m;
                        _damageMethodSignature = name + "("
                            + string.Join(", ", sig.Select(t => t.Name).ToArray()) + ")";
                        Plugin.Log?.LogInfo("[lumafly] Damage method resolved: " + _damageMethodSignature);
                        return;
                    }
                }
            }

            Plugin.Log?.LogWarning("[lumafly] Could not find any damage method on HealthManager — attacks disabled");
        }

        // ----- FIXED: no more CS0618 warning -----
        // Prefer FindObjectsByType on Unity 2023+ (via reflection, so we don't
        // depend on the new API at compile time), fall back to FindObjectsOfType
        // on older Unity builds. Both paths return UnityEngine.Object[].
        private static Object[] FindAllByType(Type type)
        {
            if (type == null) return Array.Empty<Object>();

            if (!_findByTypeResolved)
            {
                _findByTypeResolved = true;
                try
                {
                    _findByTypeMethod = typeof(Object).GetMethod(
                        "FindObjectsByType",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(Type), typeof(FindObjectsSortMode) },
                        null);
                    if (_findByTypeMethod != null)
                        Plugin.Log?.LogInfo("[lumafly] Using FindObjectsByType (fast path).");
                    else
                        Plugin.Log?.LogInfo("[lumafly] FindObjectsByType not available — using legacy FindObjectsOfType.");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[lumafly] FindObjectsByType reflection failed: " + ex.Message);
                    _findByTypeMethod = null;
                }
            }

            // Try the modern API via reflection first.
            if (_findByTypeMethod != null)
            {
                try
                {
                    var result = _findByTypeMethod.Invoke(null,
                        new object[] { type, FindObjectsSortMode.None }) as Object[];
                    if (result != null) return result;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("[lumafly] FindObjectsByType invoke failed: " + ex.Message);
                    _findByTypeMethod = null; // stop trying
                }
            }

            // Legacy fallback — silenced warning.
#pragma warning disable CS0618
            return Object.FindObjectsOfType(type);
#pragma warning restore CS0618
        }

        private static MonoBehaviour FindNearestEnemy(Vector3 from, float range)
        {
            if (_healthManagerType == null) return null;
            if (_enemyLayer == -2) _enemyLayer = LayerMask.NameToLayer("Enemies");
            if (_enemyLayer < 0) return null;

            MonoBehaviour best = null;
            float bestDistSq = range * range;

            try
            {
                var objects = FindAllByType(_healthManagerType);
                if (objects == null || objects.Length == 0) return null;

                foreach (var obj in objects)
                {
                    var mb = obj as MonoBehaviour;
                    if (mb == null) continue;
                    if (mb.gameObject.layer != _enemyLayer) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;

                    if (_hpField != null)
                    {
                        try
                        {
                            int hp = Convert.ToInt32(_hpField.GetValue(mb));
                            if (hp <= 0) continue;
                        }
                        catch { }
                    }

                    float dsq = (mb.transform.position - from).sqrMagnitude;
                    if (dsq < bestDistSq)
                    {
                        bestDistSq = dsq;
                        best = mb;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] FindNearestEnemy failed: " + ex.Message);
            }

            return best;
        }

        private void TickAttack()
        {
            if (_attackEnabled == null || !_attackEnabled.Value) return;
            if (_isAttacking) return;

            _attackTimer += Time.deltaTime;
            if (_attackTimer < _attackInterval.Value) return;

            _attackTimer = 0f;

            ResolveDamageApi();
            if (_damageMethod == null) return;

            var target = FindNearestEnemy(_t.position, _attackRange.Value);
            if (target == null)
            {
                if (_logDiagnostics.Value)
                    Plugin.Log?.LogInfo("[lumafly] No enemy in range — skipping attack");
                return;
            }

            _isAttacking = true;
            _attackStartTime = Time.time;
            _attackTarget = target;
            Plugin.Log?.LogInfo("[lumafly] Attack begun on " + target.name);
        }

        private void UpdateAttackDash()
        {
            if (Time.time - _attackStartTime > AttackTimeoutSeconds)
            {
                EndAttack();
                return;
            }

            if (_attackTarget == null)
            {
                EndAttack();
                return;
            }

            Vector3 targetPos = _attackTarget.transform.position;
            Vector3 toTarget = targetPos - _t.position;
            float dist = toTarget.magnitude;

            if (dist < AttackHitDistance)
            {
                int dmg = RollDamage();
                DealDamage(_attackTarget, dmg);
                EndAttack();
                return;
            }

            Vector3 dir = toTarget / Mathf.Max(dist, 0.0001f);
            _t.position += dir * _attackDashSpeed.Value * Time.deltaTime;
            if (Mathf.Abs(dir.x) > 0.01f) SetFacing(dir.x >= 0f);

            _t.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }

        private int RollDamage()
        {
            int chance = Mathf.Max(0, _attackJackpotChance.Value);
            if (chance > 0 && Random.Range(0, chance) == 0)
            {
                Plugin.Log?.LogInfo("[lumafly] *** JACKPOT *** rolled 1 in " + chance);
                return _attackJackpotDamage.Value;
            }
            return _attackDamage.Value;
        }

        private void DealDamage(MonoBehaviour target, int dmg)
        {
            if (target == null || _damageMethod == null) return;

            try
            {
                var parameters = _damageMethod.GetParameters();
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.ParameterType == typeof(int)) args[i] = dmg;
                    else if (p.ParameterType == typeof(GameObject)) args[i] = _bugSR != null ? _bugSR.gameObject : gameObject;
                    else if (p.ParameterType == typeof(bool)) args[i] = false;
                    else if (p.ParameterType.IsValueType) args[i] = Activator.CreateInstance(p.ParameterType);
                    else args[i] = null;
                }

                _damageMethod.Invoke(target, args);
                Plugin.Log?.LogInfo("[lumafly] Dealt " + dmg + " to " + target.name
                    + " via " + _damageMethodSignature);

                _flashUntil = Time.time + Mathf.Max(0.05f, _attackFlashSeconds.Value);
                _flashColor = (dmg >= _attackJackpotDamage.Value)
                    ? new Color(1f, 0.85f, 0.15f, 1f)
                    : new Color(1f, 1f, 1f, 1f);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[lumafly] DealDamage failed: " + ex.Message);
            }
        }

        private void EndAttack()
        {
            _isAttacking = false;
            _attackTarget = null;
            _segStart = _t.position;
            NewSegment(true);
        }

        // =====================================================================
        //  Lifecycle
        // =====================================================================

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            if (_trailMat != null) { Object.Destroy(_trailMat); _trailMat = null; }
        }

        private void OnSceneChanged(Scene o, Scene n)
        {
            _isAttacking = false;
            _attackTarget = null;
            if (_snapOnSceneChange.Value && isActiveAndEnabled && !_waitingSceneSnap)
                StartCoroutine(SnapRoutine());
        }

        private IEnumerator SnapRoutine()
        {
            _waitingSceneSnap = true;
            if (_sceneSnapDelay.Value > 0f)
                yield return new WaitForSeconds(_sceneSnapDelay.Value);

            if (_hero == null) _hero = FindHero();
            if (_hero != null && _hero.gameObject.activeInHierarchy)
            {
                _anchorPos = _hero.transform.position
                           + new Vector3(_offsetX.Value, _offsetY.Value, -0.1f);
                _velAnchor = Vector3.zero;
                _isReturning = false;
                _returnVel = Vector3.zero;
                _t.position = _anchorPos;
                _segStart = _segEnd = _t.position;
                NewSegment(true);
            }
            _waitingSceneSnap = false;
        }

        private void Update()
        {
            _diagCounter++;
            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                Plugin.Log?.LogInfo("[lumafly] first tick — plugin is alive and ticking");
            }
            if (_logDiagnostics.Value && _diagCounter % 300 == 0)
            {
                Plugin.Log?.LogInfo(
                    "[lumafly] tick: hero=" + (_hero != null ? "found" : "null")
                    + " rootActive=" + (_root != null && _root.activeSelf)
                    + " attacking=" + _isAttacking
                    + " frames=" + _frameSprites.Count);
            }

            try
            {
                if (Input.GetKeyDown(_toggleKey.Value))
                {
                    _enabled.Value = !_enabled.Value;
                    if (_root != null) _root.SetActive(_enabled.Value);
                }
            }
            catch { }

            if (!_enabled.Value) return;

            if (_hero == null) _hero = FindHero();
            if (_hero == null) return;
            if (!_hero.gameObject.activeInHierarchy) return;

            TickAttack();

            if (_flashUntil > 0f)
            {
                if (Time.time < _flashUntil)
                {
                    if (_bugSR != null) _bugSR.color = _flashColor;
                }
                else
                {
                    _flashUntil = 0f;
                    if (_bugSR != null) _bugSR.color = Color.white;
                }
            }

            if (_isAttacking)
            {
                UpdateAttackDash();
                AnimateFrames();
                return;
            }

            Vector3 heroPos = _hero.transform.position;
            Vector3 target = heroPos + new Vector3(_offsetX.Value, _offsetY.Value, -0.1f);

            _anchorPos = Vector3.SmoothDamp(
                _anchorPos == default(Vector3) ? target : _anchorPos,
                target, ref _velAnchor, Mathf.Max(0f, _followLag.Value));

            Vector3 toAnchor = _anchorPos - heroPos;
            if (toAnchor.magnitude < 1f)
                _anchorPos += (toAnchor.sqrMagnitude > 1e-5f ? toAnchor.normalized : Vector3.left)
                            * (1f - toAnchor.magnitude) * 0.6f;

            if (Vector3.Distance(_t.position, _anchorPos) > _teleportSnapDistance.Value)
            {
                _t.position = _anchorPos;
                _segStart = _segEnd = _t.position;
                NewSegment(true);
            }

            Vector3 fromAnchor = _t.position - _anchorPos;
            if (fromAnchor.magnitude > _returnRadius.Value && !_isReturning)
            {
                _isReturning = true;
                _returnVel = Vector3.zero;
                _segTime = 0f;
                _segDuration = 0f;
                float dx = (_anchorPos - _t.position).x;
                if (Mathf.Abs(dx) > 0.01f) SetFacing(dx >= 0f);
            }

            if (_isReturning)
            {
                Vector3 dir = fromAnchor.sqrMagnitude > 1e-5f ? fromAnchor.normalized : Vector3.right;
                Vector3 tp = _anchorPos + dir * _idleRadius.Value;
                _t.position = Vector3.SmoothDamp(_t.position, tp, ref _returnVel,
                    Mathf.Max(0.01f, _returnSmoothTime.Value),
                    Mathf.Max(0.01f, _returnMaxSpeed.Value));
                if (Vector3.Distance(_t.position, tp) < 0.05f) NewSegment(true);
            }
            else
            {
                _segTime += Time.deltaTime;
                float k = Mathf.Clamp01(_segTime / Mathf.Max(0.0001f, _segDuration));
                float ks = k * k * (3f - 2f * k);
                Vector3 p1 = Vector3.Lerp(_segStart, _segControl, ks);
                Vector3 p2 = Vector3.Lerp(_segControl, _segEnd, ks);
                Vector3 pos = Vector3.Lerp(p1, p2, ks);
                pos.y += Mathf.Sin(Time.time * 8.482301f) * 0.045f;
                _t.position = pos;
                if (k >= 0.9999f) NewSegment(Random.value > 0.13f);
            }

            _t.rotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 1.9f) * 2.5f);

            AnimateFrames();
        }

        private void AnimateFrames()
        {
            if (_frameSprites.Count <= 1 || _bugSR == null) return;
            _frameTimer += Time.deltaTime;
            float step = 1f / Mathf.Max(1f, _fps.Value);
            while (_frameTimer >= step)
            {
                _frameTimer -= step;
                _frameIndex++;
                if (_frameIndex >= _frameSprites.Count) _frameIndex = 0;
            }
            _bugSR.sprite = _frameSprites[_frameIndex];
        }

        private void SetFacing(bool right)
        {
            if (_facingRight == right) return;
            _facingRight = right;
            ApplyScale();
        }

        private void NewSegment(bool meander)
        {
            _isDart = !meander;
            _isReturning = false;
            _segStart = _t.position;

            float maxR = _idleRadius.Value * (_isDart ? DartRMax : MeanderRMax);
            float minR = _idleRadius.Value * (_isDart ? DartRMin : MeanderRMin);
            Vector2 dir = Random.insideUnitCircle.normalized;
            float r = Mathf.Lerp(minR, maxR, Random.value);
            _segEnd = _anchorPos + new Vector3(dir.x * r, dir.y * r, 0f);

            Vector3 chord = _segEnd - _segStart;
            Vector3 perp = new Vector3(-chord.y, chord.x, 0f).normalized;
            _segControl = Vector3.LerpUnclamped(_segStart, _segEnd, 0.5f)
                        + perp * (_isDart ? 0.15f : 0.35f);
            _segDuration = _isDart
                ? Random.Range(DartMin, DartMax)
                : Random.Range(MeanderMin, MeanderMax);
            _segTime = 0f;

            if (_isDart)
            {
                float dx = (_segEnd - _segStart).x;
                if (Mathf.Abs(dx) > 0.01f) SetFacing(dx >= 0f);
            }
        }

        private void Reload()
        {
            LoadFrames();
            _frameIndex = 0;
            _frameTimer = 0f;
            if (_frameSprites.Count > 0)
            {
                _bugSR.sprite = _frameSprites[0];
                ApplyScale();
            }
        }
    }
}

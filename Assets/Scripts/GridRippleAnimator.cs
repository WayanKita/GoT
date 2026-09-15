using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
#if UNITY_EDITOR
using UnityEditor;
#endif

// =====================================================================================
//  GridRippleAnimator.cs
//
//  Ripples a grid of tiles by delaying each one according to its distance from an
//  origin, so a wave travels across the grid. Works off world positions, so it doesn't
//  need to know the grid's row/column layout.
//
//  Three modes:
//    Continuous - endless travelling sine wave. Tiles bob forever.
//    Pulse      - a single wave front crosses the grid once; each tile rises and settles.
//    Reveal     - each tile rises from an offset into its home position and stays there.
//
//  Setup:
//    1. Add to the grid parent, hit "Populate Tiles" in the context menu.
//    2. Pick a mode and an origin, then tune Amplitude / Wave Speed / Wavelength.
//    3. Tick Preview In Editor to watch it without entering play mode.
//
//  Home positions are captured once and serialized, so previewing can't destroy them.
// =====================================================================================

[ExecuteAlways]
[DisallowMultipleComponent]
public class GridRippleAnimator : MonoBehaviour
{
    public enum RippleMode { Continuous, Pulse, Reveal }
    public enum RippleShape { Radial, Linear }
    public enum OriginMode { GridCenter, FromTransform, WorldPoint }

    [Serializable]
    private struct Tile
    {
        public Transform target;
        public Vector3 home;
    }

    [Header("Tiles")]
    [Tooltip("Where to look for tiles. Leave empty to use this object.")]
    public Transform tileRoot;

    [Tooltip("On: every renderer in the hierarchy counts as a tile. Off: direct children only.")]
    public bool includeNestedTiles = false;

    [SerializeField, HideInInspector] private List<Tile> tiles = new List<Tile>();

    [Header("Wave")]
    public RippleMode mode = RippleMode.Continuous;
    public RippleShape shape = RippleShape.Radial;

    [Tooltip("Direction the wave travels in Linear mode. Ignored when Radial.")]
    public Vector3 waveDirection = Vector3.right;

    [Tooltip("Which way tiles are displaced. Normalized at runtime.")]
    public Vector3 displacementAxis = Vector3.up;

    [Tooltip("Peak displacement in world units.")]
    public float amplitude = 0.05f;

    [Tooltip("How fast the wave front crosses the grid, in world units per second.")]
    public float waveSpeed = 4f;

    [Tooltip("Continuous only: distance between wave crests. Small = tight ripples.")]
    [Min(0.0001f)] public float wavelength = 3f;

    [Tooltip("Pulse / Reveal only: how long one tile takes to complete its move.")]
    [Min(0.0001f)] public float tileDuration = 0.5f;

    [Tooltip("Amplitude multiplier over normalized distance from the origin (0 = origin, 1 = farthest tile). Use it to fade the ripple out toward the edges.")]
    public AnimationCurve amplitudeOverDistance = AnimationCurve.Constant(0f, 1f, 1f);

    [Tooltip("Random extra delay per tile, in seconds. A little breaks up the perfectly clean wave front.")]
    [Min(0f)] public float delayJitter = 0f;

    [Header("Origin")]
    public OriginMode originMode = OriginMode.GridCenter;
    public Transform originTransform;
    public Vector3 originPoint;

    [Header("Shape curves")]
    [Tooltip("Pulse mode: displacement over one tile's duration. Should start and end at 0.")]
    public AnimationCurve pulseShape = new AnimationCurve(
        new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));

    [Tooltip("Reveal mode: eased progress from the offset start position to home.")]
    public AnimationCurve revealShape = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Behaviour")]
    public bool playOnStart = true;

    [Tooltip("Pulse mode: send another wave every Loop Interval seconds.")]
    public bool loopPulse = false;

    [Min(0.01f)] public float loopInterval = 2f;

    public bool useUnscaledTime = false;

    [Tooltip("Runs the ripple in the scene view without entering play mode. Positions are restored when you switch it off.")]
    public bool previewInEditor = false;

    [Tooltip("Same seed reproduces the same jitter every run.")]
    public int seed = 1;

    [Header("Events")]
    public UnityEvent onRippleComplete;

    /// <summary>Fires when a Pulse or Reveal wave has finished crossing the grid.</summary>
    public event Action RippleComplete;

    // Runtime state, rebuilt on play. Not serialized.
    private float[] _distances;   // normalized 0..1 for the amplitude curve
    private float[] _delays;      // seconds before this tile starts
    private float _maxDistance;
    private float _longestDelay;
    private bool _playing;
    private float _startTime;
    private bool _completeFired;

    public bool IsPlaying => _playing;

    // ---------------------------------------------------------------- lifecycle

    private void Start()
    {
        if (Application.isPlaying && playOnStart) Play();
    }

    private void OnEnable()
    {
#if UNITY_EDITOR
        EditorApplication.update += EditorTick;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        ResetToHome();
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        Tick(useUnscaledTime ? Time.unscaledTime : Time.time);
    }

#if UNITY_EDITOR
    private void EditorTick()
    {
        if (this == null || Application.isPlaying) return;
        if (!previewInEditor)
        {
            if (_playing) { _playing = false; ResetToHome(); }
            return;
        }

        if (!_playing) Play();
        Tick((float)EditorApplication.timeSinceStartup);
        SceneView.RepaintAll();
    }
#endif

    // ---------------------------------------------------------------- public API

    /// <summary>Starts the ripple from the configured origin.</summary>
    public void Play()
    {
        if (tiles.Count == 0) PopulateTiles();
        BuildWave(ResolveOrigin());
        _startTime = Now();
        _completeFired = false;
        _playing = true;
    }

    /// <summary>Starts a ripple centred on an arbitrary world point. Handy for click-to-ripple.</summary>
    public void RippleFrom(Vector3 worldPoint)
    {
        if (tiles.Count == 0) PopulateTiles();
        BuildWave(worldPoint);
        _startTime = Now();
        _completeFired = false;
        _playing = true;
    }

    /// <summary>Stops the ripple and puts every tile back where it belongs.</summary>
    public void Stop()
    {
        _playing = false;
        ResetToHome();
    }

    /// <summary>Snaps every tile back to its captured home position.</summary>
    [ContextMenu("Reset To Home")]
    public void ResetToHome()
    {
        foreach (var t in tiles)
            if (t.target != null) t.target.position = t.home;
    }

    /// <summary>Collects the tiles and records where they currently sit as home.</summary>
    [ContextMenu("Populate Tiles")]
    public void PopulateTiles()
    {
        var root = tileRoot != null ? tileRoot : transform;
        tiles.Clear();

        if (includeNestedTiles)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                tiles.Add(new Tile { target = r.transform, home = r.transform.position });
        }
        else
        {
            foreach (Transform child in root)
                tiles.Add(new Tile { target = child, home = child.position });
        }

        MarkDirty();
    }

    /// <summary>Re-reads current positions as home. Run this after moving the grid.</summary>
    [ContextMenu("Recapture Home Positions")]
    public void RecaptureHomePositions()
    {
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t.target == null) continue;
            t.home = t.target.position;
            tiles[i] = t;
        }
        MarkDirty();
    }

    // ---------------------------------------------------------------- wave setup

    private Vector3 ResolveOrigin()
    {
        switch (originMode)
        {
            case OriginMode.FromTransform:
                return originTransform != null ? originTransform.position : transform.position;
            case OriginMode.WorldPoint:
                return originPoint;
            default:
                return GridCenter();
        }
    }

    private Vector3 GridCenter()
    {
        if (tiles.Count == 0) return transform.position;
        var sum = Vector3.zero;
        int n = 0;
        foreach (var t in tiles)
        {
            if (t.target == null) continue;
            sum += t.home;
            n++;
        }
        return n > 0 ? sum / n : transform.position;
    }

    /// <summary>Works out each tile's distance from the origin and turns that into a delay.</summary>
    private void BuildWave(Vector3 origin)
    {
        int n = tiles.Count;
        _distances = new float[n];
        _delays = new float[n];

        var raw = new float[n];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        Vector3 dir = waveDirection.sqrMagnitude > 0.0001f ? waveDirection.normalized : Vector3.right;

        for (int i = 0; i < n; i++)
        {
            if (tiles[i].target == null) { raw[i] = 0f; continue; }

            raw[i] = shape == RippleShape.Linear
                ? Vector3.Dot(tiles[i].home - origin, dir)   // signed: distance along the wave direction
                : Vector3.Distance(tiles[i].home, origin);

            min = Mathf.Min(min, raw[i]);
            max = Mathf.Max(max, raw[i]);
        }

        // Shift so the nearest tile is at 0. Matters for Linear, where tiles "behind"
        // the origin would otherwise get negative delays and start before the wave.
        float span = Mathf.Max(0.0001f, max - min);
        _maxDistance = span;

        var rng = new System.Random(seed);
        _longestDelay = 0f;
        float speed = Mathf.Max(0.0001f, waveSpeed);

        for (int i = 0; i < n; i++)
        {
            float d = raw[i] - min;
            _distances[i] = d / span;
            _delays[i] = d / speed + (float)rng.NextDouble() * delayJitter;
            _longestDelay = Mathf.Max(_longestDelay, _delays[i]);
        }
    }

    // ---------------------------------------------------------------- per-frame

    private void Tick(float now)
    {
        if (!_playing || tiles.Count == 0) return;
        if (_distances == null || _distances.Length != tiles.Count) BuildWave(ResolveOrigin());

        // A moving origin transform should drag the wave along with it.
        if (mode == RippleMode.Continuous && originMode == OriginMode.FromTransform && originTransform != null)
            BuildWave(originTransform.position);

        float elapsed = now - _startTime;
        Vector3 axis = displacementAxis.sqrMagnitude > 0.0001f ? displacementAxis.normalized : Vector3.up;
        bool anyActive = false;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.target == null) continue;

            float a = amplitude * amplitudeOverDistance.Evaluate(_distances[i]);
            float offset;

            switch (mode)
            {
                case RippleMode.Continuous:
                {
                    // Standard travelling wave: crests move outward at waveSpeed.
                    float d = _distances[i] * _maxDistance;
                    float phase = (waveSpeed * elapsed - d) / wavelength;
                    offset = a * Mathf.Sin(phase * Mathf.PI * 2f);
                    anyActive = true;
                    break;
                }

                case RippleMode.Pulse:
                {
                    float local = elapsed - _delays[i];
                    if (loopPulse) local = Mathf.Repeat(elapsed, loopInterval) - _delays[i];

                    if (local < 0f || local > tileDuration) { offset = 0f; }
                    else { offset = a * pulseShape.Evaluate(local / tileDuration); anyActive = true; }
                    break;
                }

                default: // Reveal
                {
                    float local = elapsed - _delays[i];
                    float t = Mathf.Clamp01(local / tileDuration);
                    if (local < 0f) t = 0f;
                    // Starts one amplitude below home and eases up into place.
                    offset = -amplitude * (1f - revealShape.Evaluate(t));
                    if (t < 1f) anyActive = true;
                    break;
                }
            }

            tile.target.position = tile.home + axis * offset;
        }

        if (mode == RippleMode.Continuous || loopPulse) return;

        if (!anyActive && elapsed > _longestDelay && !_completeFired)
        {
            _completeFired = true;
            _playing = false;
            ResetToHome();
            RippleComplete?.Invoke();
            onRippleComplete?.Invoke();
        }
    }

    private float Now()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) return (float)EditorApplication.timeSinceStartup;
#endif
        return useUnscaledTime ? Time.unscaledTime : Time.time;
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    private void OnValidate()
    {
        wavelength = Mathf.Max(0.0001f, wavelength);
        tileDuration = Mathf.Max(0.0001f, tileDuration);
        _distances = null; // force a rebuild with the new settings
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 origin = ResolveOrigin();
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(origin, 0.25f);

        if (shape == RippleShape.Linear && waveDirection.sqrMagnitude > 0.0001f)
            Gizmos.DrawRay(origin, waveDirection.normalized * 2f);
        else
            for (int r = 1; r <= 3; r++)
                Gizmos.DrawWireSphere(origin, wavelength * r);
    }
}

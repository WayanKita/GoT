using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
#if UNITY_EDITOR
using UnityEditor;
#endif

// =====================================================================================
//  ClusterSequenceAnimator.cs
//
//  Put this on CampusTest. It animates each cluster child in turn: everything drops in
//  from a start position (same X and Z, Y pushed to startY) to its authored position.
//  Each cluster gets a random speed between minSpeed and maxSpeed.
//
//  Setup:
//    1. Add to CampusTest, hit "Populate From Children" in the context menu.
//    2. Drag the entries in Cluster Order to set the sequence.
//    3. Optional: turn on Use Timings and fill in Cluster Start Times.
//    4. Press play, or call Play() from another script / a UI button.
//
//  Timings:
//    With Use Timings on, cluster N starts at Cluster Start Times[N] seconds after Play(),
//    regardless of whether earlier clusters have landed. Entries line up with Cluster
//    Order by index, so if you reorder clusters, reorder the times too. Times don't have
//    to be ascending. A missing entry falls back to (previous time + Delay Between Clusters).
//
//  The authored positions are captured once and serialized, so snapping to the start
//  position in the editor can't destroy them.
// =====================================================================================

[DisallowMultipleComponent]
public class ClusterSequenceAnimator : MonoBehaviour
{
    [Serializable]
    private struct Captured
    {
        public Transform cluster;   // which cluster this belongs to
        public Transform target;    // the transform being moved
        public Vector3 endPosition; // position B: where it was authored
    }

    [Header("Sequence")]
    [Tooltip("Clusters animate top to bottom. Drag to reorder.")]
    public List<Transform> clusterOrder = new List<Transform>();

    [Tooltip("On: animate every child of a cluster. Off: animate the cluster object itself, so its children come along.")]
    public bool animateChildren = true;

    [Tooltip("On: a cluster must land before the next one starts. Off: clusters overlap, launched every Delay seconds. Ignored when Use Timings is on.")]
    public bool waitForClusterToFinish = true;

    [Tooltip("Pause between clusters (seconds). Also the stagger interval when Wait For Cluster To Finish is off, and the fallback spacing for missing timing entries.")]
    [Min(0f)] public float delayBetweenClusters = 0.15f;

    [Header("Timings")]
    [Tooltip("On: each cluster starts at its entry in Cluster Start Times. Off: use the sequential settings above.")]
    public bool useTimings = false;

    [Tooltip("Start time per cluster, in seconds after Play(). Index matches Cluster Order.")]
    public List<float> clusterStartTimes = new List<float>();

    [Header("Start position (A)")]
    [Tooltip("X and Z are kept; Y is replaced by this value.")]
    public float startY = -0.08f;

    [Tooltip("On: startY is added to the authored Y instead of replacing it.")]
    public bool startYIsOffset = false;

    [Header("Speed")]
    [Tooltip("World units per second. A speed is drawn from this range for each cluster.")]
    public float minSpeed = 0.05f;
    public float maxSpeed = 0.2f;

    [Tooltip("On: every object rolls its own speed. Off: one speed per cluster.")]
    public bool randomizePerObject = false;

    [Tooltip("Shapes the movement over its duration. Linear is a straight ramp.")]
    public AnimationCurve easing = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Per-object delay")]
    [Tooltip("Each object inside a cluster waits a random amount before it starts moving (seconds). Set both to 0 for the whole cluster to launch at once.")]
    [Min(0f)] public float minObjectDelay = 0f;

    [Min(0f)] public float maxObjectDelay = 0.25f;

    [Header("Behaviour")]
    public bool playOnStart = true;

    [Tooltip("Deactivate objects until their cluster's turn, so nothing is visible at the start position.")]
    public bool hideUntilAnimated = true;

    [Tooltip("Ignores Time.timeScale. Useful if the reveal plays while the game is paused.")]
    public bool useUnscaledTime = false;

    [Tooltip("On: the same seed reproduces the same speeds every run.")]
    public bool deterministic = false;
    public int seed = 1;

    [Header("Events")]
    public UnityEvent onSequenceComplete;

    /// <summary>Fires as each cluster starts, with its position in the sequence.</summary>
    public event Action<Transform, int> ClusterStarted;

    /// <summary>Fires once the whole sequence has landed.</summary>
    public event Action SequenceComplete;

    [SerializeField, HideInInspector] private List<Captured> captured = new List<Captured>();

    private Coroutine _routine;
    private System.Random _rng;
    private int _activeClusters;

    public bool IsPlaying => _routine != null;

    // ---------------------------------------------------------------- lifecycle

    private void Awake()
    {
        if (captured.Count == 0) CaptureTargets();
    }

    private void Start()
    {
        if (playOnStart) Play();
    }

    private void OnValidate()
    {
        SyncTimings();
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Runs the whole sequence from the beginning.</summary>
    public void Play()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("ClusterSequenceAnimator.Play() only runs in play mode.", this);
            return;
        }

        if (captured.Count == 0) CaptureTargets();

        Stop();
        _rng = deterministic ? new System.Random(seed) : new System.Random(Environment.TickCount);
        SnapToStart();
        _routine = StartCoroutine(PlaySequence());
    }

    /// <summary>Halts mid-animation, leaving everything where it is.</summary>
    public void Stop()
    {
        // Stops the sequence and any clusters still in flight.
        StopAllCoroutines();
        _routine = null;
        _activeClusters = 0;
    }

    /// <summary>Re-reads the current positions as the authored end positions (B).</summary>
    [ContextMenu("Capture Target Positions")]
    public void CaptureTargets()
    {
        captured.Clear();

        foreach (var cluster in clusterOrder)
        {
            if (cluster == null) continue;

            if (!animateChildren)
            {
                captured.Add(new Captured { cluster = cluster, target = cluster, endPosition = cluster.position });
                continue;
            }

            foreach (Transform child in cluster)
                captured.Add(new Captured { cluster = cluster, target = child, endPosition = child.position });
        }

        MarkDirty();
    }

    /// <summary>Fills Cluster Order from the current children, in hierarchy order.</summary>
    [ContextMenu("Populate From Children")]
    public void PopulateFromChildren()
    {
        clusterOrder.Clear();
        foreach (Transform child in transform) clusterOrder.Add(child);
        SyncTimings();
        CaptureTargets();
    }

    /// <summary>Overwrites Cluster Start Times with an even spacing of Delay Between Clusters.</summary>
    [ContextMenu("Fill Timings From Delay")]
    public void FillTimingsFromDelay()
    {
        clusterStartTimes.Clear();
        for (int i = 0; i < clusterOrder.Count; i++)
            clusterStartTimes.Add(i * delayBetweenClusters);
        MarkDirty();
    }

    /// <summary>Moves everything to position A. Safe in the editor: targets are captured first.</summary>
    [ContextMenu("Snap To Start")]
    public void SnapToStart()
    {
        if (captured.Count == 0) CaptureTargets();

        foreach (var c in captured)
        {
            if (c.target == null) continue;
            c.target.position = StartPositionFor(c.endPosition);
            if (hideUntilAnimated && Application.isPlaying) c.target.gameObject.SetActive(false);
        }
        MarkDirty();
    }

    /// <summary>Restores everything to position B.</summary>
    [ContextMenu("Snap To End")]
    public void SnapToEnd()
    {
        foreach (var c in captured)
        {
            if (c.target == null) continue;
            c.target.position = c.endPosition;
            c.target.gameObject.SetActive(true);
        }
        MarkDirty();
    }

    /// <summary>Start time (seconds after Play) for the cluster at this index in Cluster Order.</summary>
    public float GetClusterStartTime(int index)
    {
        float time = 0f;
        for (int i = 0; i <= index; i++)
        {
            if (i < clusterStartTimes.Count) time = Mathf.Max(0f, clusterStartTimes[i]);
            else if (i > 0) time += delayBetweenClusters;
        }
        return time;
    }

    // ---------------------------------------------------------------- animation

    private IEnumerator PlaySequence()
    {
        _activeClusters = 0;

        if (useTimings) yield return PlayTimed();
        else yield return PlaySequential();

        // Overlapping or timed clusters may still be in flight.
        while (_activeClusters > 0) yield return null;

        _routine = null;
        SequenceComplete?.Invoke();
        onSequenceComplete?.Invoke();
    }

    private IEnumerator PlaySequential()
    {
        for (int i = 0; i < clusterOrder.Count; i++)
        {
            var cluster = clusterOrder[i];
            if (cluster == null) continue;

            ClusterStarted?.Invoke(cluster, i);

            if (waitForClusterToFinish) yield return RunCluster(cluster);
            else StartCoroutine(RunCluster(cluster));

            if (delayBetweenClusters > 0f) yield return Wait(delayBetweenClusters);
        }
    }

    private IEnumerator PlayTimed()
    {
        // Launch order sorted by start time; ties keep Cluster Order.
        var order = new List<int>();
        for (int i = 0; i < clusterOrder.Count; i++)
            if (clusterOrder[i] != null) order.Add(i);

        order.Sort((a, b) =>
        {
            int cmp = GetClusterStartTime(a).CompareTo(GetClusterStartTime(b));
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        float elapsed = 0f;
        foreach (int index in order)
        {
            float startTime = GetClusterStartTime(index);
            while (elapsed < startTime)
            {
                yield return null;
                elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            }

            var cluster = clusterOrder[index];
            ClusterStarted?.Invoke(cluster, index);
            StartCoroutine(RunCluster(cluster));
        }
    }

    private IEnumerator RunCluster(Transform cluster)
    {
        _activeClusters++;
        yield return AnimateCluster(cluster);
        _activeClusters--;
    }

    private IEnumerator AnimateCluster(Transform cluster)
    {
        // Gather this cluster's items and work out how long each takes at its speed.
        var items = new List<Transform>();
        var starts = new List<Vector3>();
        var ends = new List<Vector3>();
        var durations = new List<float>();
        var delays = new List<float>();

        float clusterSpeed = RandomSpeed();

        foreach (var c in captured)
        {
            if (c.cluster != cluster || c.target == null) continue;

            Vector3 start = StartPositionFor(c.endPosition);
            float distance = Vector3.Distance(start, c.endPosition);
            float speed = randomizePerObject ? RandomSpeed() : clusterSpeed;
            float delay = RandomRange(minObjectDelay, maxObjectDelay);

            items.Add(c.target);
            starts.Add(start);
            ends.Add(c.endPosition);
            durations.Add(speed > 0f ? distance / speed : 0f);
            delays.Add(delay);

            c.target.position = start;

            // An object with a delay stays hidden until its own turn, otherwise it would
            // sit visible at the start position while it waits.
            c.target.gameObject.SetActive(!hideUntilAnimated || delay <= 0f);
        }

        if (items.Count == 0) yield break;

        float elapsed = 0f;
        bool moving = true;

        while (moving)
        {
            elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            moving = false;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;

                float local = elapsed - delays[i];
                if (local < 0f) { moving = true; continue; } // still waiting its turn

                if (hideUntilAnimated && !items[i].gameObject.activeSelf)
                    items[i].gameObject.SetActive(true);

                float t = durations[i] > 0f ? Mathf.Clamp01(local / durations[i]) : 1f;
                items[i].position = Vector3.LerpUnclamped(starts[i], ends[i], easing.Evaluate(t));
                if (t < 1f) moving = true;
            }

            yield return null;
        }

        // Kill any floating-point drift.
        for (int i = 0; i < items.Count; i++)
            if (items[i] != null) items[i].position = ends[i];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Keeps Cluster Start Times the same length as Cluster Order, padding new entries.</summary>
    private void SyncTimings()
    {
        if (clusterStartTimes == null) clusterStartTimes = new List<float>();

        while (clusterStartTimes.Count < clusterOrder.Count)
        {
            int i = clusterStartTimes.Count;
            float previous = i > 0 ? clusterStartTimes[i - 1] : -delayBetweenClusters;
            clusterStartTimes.Add(Mathf.Max(0f, previous + delayBetweenClusters));
        }

        if (clusterStartTimes.Count > clusterOrder.Count)
            clusterStartTimes.RemoveRange(clusterOrder.Count, clusterStartTimes.Count - clusterOrder.Count);
    }

    private float RandomSpeed() => RandomRange(minSpeed, maxSpeed);

    private float RandomRange(float a, float b)
    {
        float lo = Mathf.Min(a, b);
        float hi = Mathf.Max(a, b);
        if (hi <= lo) return lo;
        if (_rng == null) _rng = new System.Random(Environment.TickCount);
        return lo + (float)_rng.NextDouble() * (hi - lo);
    }

    private Vector3 StartPositionFor(Vector3 end) =>
        new Vector3(end.x, startYIsOffset ? end.y + startY : startY, end.z);

    private IEnumerator Wait(float seconds) =>
        useUnscaledTime ? WaitUnscaled(seconds) : WaitScaled(seconds);

    private IEnumerator WaitScaled(float seconds)
    {
        yield return new WaitForSeconds(seconds);
    }

    private IEnumerator WaitUnscaled(float seconds)
    {
        float t = 0f;
        while (t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
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

    private void OnDrawGizmosSelected()
    {
        foreach (var c in captured)
        {
            if (c.target == null) continue;
            Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
            Gizmos.DrawLine(StartPositionFor(c.endPosition), c.endPosition);
        }
    }
}
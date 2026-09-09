using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GOTRising : MonoBehaviour
{
    [Header("Height")]
    [Tooltip("Building is moved down by Height × Multiplier")]
    public float heightMultiplier = 1.2f;

    [Tooltip("Direction buildings rise in")]
    public Vector3 riseDirection = Vector3.up;

    [Header("Timing")]
    [Tooltip("Total duration of entire sequence")]
    public float totalAnimationTime = 10f;

    [Tooltip("Duration of an individual building animation")]
    public float riseDuration = 2f;

    [Range(0f, 1f)]
    [Tooltip("0 = one by one, 1 = all at once")]
    public float overlap = 0.5f;

    [Header("Rotation")]
    [Tooltip("Number of complete rotations while rising")]
    public float rotations = 1f;

    public Vector3 rotationAxis = Vector3.up;

    [Header("Animation")]
    public AnimationCurve movementCurve =
        AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Playback")]
    public bool playOnStart = true;

    private class BuildingData
    {
        public Transform transform;
        public float riseDistance;
        public Quaternion originalRotation;
    }

    private void Start()
    {
        if (playOnStart)
            Play();
    }

    [ContextMenu("Play")]
    public void Play()
    {
        StopAllCoroutines();
        StartCoroutine(RaiseChildren());
    }

    private IEnumerator RaiseChildren()
    {
        List<BuildingData> buildings = new();

        Vector3 direction = riseDirection.normalized;

        // Move ALL buildings down immediately
        foreach (Transform child in transform)
        {
            float height = GetObjectHeight(child);
            float riseDistance = height * heightMultiplier;

            child.localPosition -= direction * riseDistance;

            buildings.Add(new BuildingData
            {
                transform = child,
                riseDistance = riseDistance,
                originalRotation = child.localRotation
            });
        }

        if (buildings.Count == 0)
            yield break;

        // Shuffle order
        for (int i = 0; i < buildings.Count; i++)
        {
            int j = Random.Range(i, buildings.Count);

            (buildings[i], buildings[j]) =
                (buildings[j], buildings[i]);
        }

        float sequentialSpacing =
            buildings.Count > 1
            ? Mathf.Max(0f,
                totalAnimationTime - riseDuration)
                / (buildings.Count - 1)
            : 0f;

        float actualSpacing =
            Mathf.Lerp(sequentialSpacing, 0f, overlap);

        foreach (BuildingData building in buildings)
        {
            StartCoroutine(
                AnimateBuilding(
                    building.transform,
                    building.riseDistance,
                    building.originalRotation));

            if (actualSpacing > 0f)
                yield return new WaitForSeconds(actualSpacing);
        }
    }

    private IEnumerator AnimateBuilding(
        Transform target,
        float riseDistance,
        Quaternion originalRotation)
    {
        Vector3 direction = riseDirection.normalized;

        Vector3 startPos = target.localPosition;
        Vector3 endPos = startPos + direction * riseDistance;

        float elapsed = 0f;

        while (elapsed < riseDuration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(elapsed / riseDuration);

            float eased =
                movementCurve.Evaluate(t);

            target.localPosition =
                Vector3.LerpUnclamped(
                    startPos,
                    endPos,
                    eased);

            float remainingSpin =
                (1f - eased) *
                rotations *
                360f;

            target.localRotation =
                originalRotation *
                Quaternion.AngleAxis(
                    remainingSpin,
                    rotationAxis.normalized);

            yield return null;
        }

        target.localPosition = endPos;
        target.localRotation = originalRotation;
    }

    private float GetObjectHeight(Transform target)
    {
        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
            return 1f;

        Bounds bounds = renderers[0].bounds;

        foreach (Renderer renderer in renderers)
        {
            bounds.Encapsulate(renderer.bounds);
        }

        // World-space height
        return bounds.size.y;
    }
}
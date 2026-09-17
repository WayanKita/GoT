using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class GroupChildrenByBuildingCode : MonoBehaviour
{
    private static readonly Regex BuildingCodeRegex =
        new Regex(@"^([A-Z]\d{2})\s+", RegexOptions.Compiled);

    private static readonly Regex GroupNameRegex =
        new Regex(@"^[A-Z]\d{2}$", RegexOptions.Compiled);

    [ContextMenu("Group Children By Building Code")]
    public void GroupChildren()
    {
        // Copy direct children first, because we'll modify the hierarchy.
        List<Transform> directChildren = new List<Transform>();

        foreach (Transform child in transform)
        {
            directChildren.Add(child);
        }

        // Find already existing group objects such as E01, E02, B03.
        Dictionary<string, Transform> existingGroups =
            new Dictionary<string, Transform>();

        foreach (Transform child in directChildren)
        {
            if (GroupNameRegex.IsMatch(child.name))
            {
                existingGroups[child.name] = child;
            }
        }

        // Find children that should be grouped.
        Dictionary<string, List<Transform>> objectsToGroup =
            new Dictionary<string, List<Transform>>();

        foreach (Transform child in directChildren)
        {
            Match match = BuildingCodeRegex.Match(child.name);

            if (!match.Success)
                continue;

            string buildingCode = match.Groups[1].Value;

            if (!objectsToGroup.ContainsKey(buildingCode))
            {
                objectsToGroup[buildingCode] = new List<Transform>();
            }

            objectsToGroup[buildingCode].Add(child);
        }

        if (objectsToGroup.Count == 0)
        {
            Debug.Log(
                $"No ungrouped building objects found under '{gameObject.name}'.",
                gameObject
            );

            return;
        }

#if UNITY_EDITOR
        Undo.SetCurrentGroupName("Group Children By Building Code");
        int undoGroup = Undo.GetCurrentGroup();
#endif

        foreach (string buildingCode in objectsToGroup.Keys.OrderBy(x => x))
        {
            Transform groupParent;

            // Reuse existing group if available.
            if (existingGroups.TryGetValue(buildingCode, out Transform existingGroup))
            {
                groupParent = existingGroup;
            }
            else
            {
                GameObject groupObject = new GameObject(buildingCode);

#if UNITY_EDITOR
                Undo.RegisterCreatedObjectUndo(
                    groupObject,
                    $"Create {buildingCode}"
                );

                Undo.SetTransformParent(
                    groupObject.transform,
                    transform,
                    $"Parent {buildingCode}"
                );
#else
                groupObject.transform.SetParent(transform);
#endif

                groupObject.transform.localPosition = Vector3.zero;
                groupObject.transform.localRotation = Quaternion.identity;
                groupObject.transform.localScale = Vector3.one;

                groupParent = groupObject.transform;
                existingGroups[buildingCode] = groupParent;
            }

            foreach (Transform child in objectsToGroup[buildingCode])
            {
                if (child == groupParent)
                    continue;

#if UNITY_EDITOR
                Undo.SetTransformParent(
                    child,
                    groupParent,
                    $"Move {child.name} to {buildingCode}"
                );
#else
                child.SetParent(groupParent, true);
#endif
            }
        }

#if UNITY_EDITOR
        Undo.CollapseUndoOperations(undoGroup);
        EditorUtility.SetDirty(gameObject);
#endif

        Debug.Log(
            $"Grouped {objectsToGroup.Count} building groups under '{gameObject.name}'.",
            gameObject
        );
    }
}
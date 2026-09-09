using UnityEngine;

public class CaveCameraController : MonoBehaviour
{
    [Header("Viewer Tracking")]

    [Tooltip(
        "When enabled, the Head Transform drives the projection. " +
        "When disabled, the Player Transform is used."
    )]
    public bool useHeadTransform = false;

    [Tooltip(
        "The Player transform used for keyboard-controlled movement. " +
        "If empty, this GameObject's transform is used."
    )]
    public Transform playerTransform;

    [Tooltip(
        "The tracked head or eye transform. " +
        "This can later be controlled through VRPN."
    )]
    public Transform headTransform;

    [Header("Left Wall")]
    public Camera leftScreen;
    public Transform leftPlane;

    [Header("Right Wall")]
    public Camera rightScreen;
    public Transform rightPlane;

    [Header("Projection Settings")]
    [Min(0.0001f)]
    public float nearClipPlane = 0.001f;

    [Min(0.01f)]
    public float farClipPlane = 100f;

    [Header("Debug")]
    public bool drawDebugLines = true;

    private void LateUpdate()
    {
        Vector3 eyePosition = GetEyePosition();

        UpdateWallCamera(
            leftScreen,
            leftPlane,
            eyePosition
        );

        UpdateWallCamera(
            rightScreen,
            rightPlane,
            eyePosition
        );
    }

    private Vector3 GetEyePosition()
    {
        if (useHeadTransform)
        {
            if (headTransform != null)
            {
                return headTransform.position;
            }

            Debug.LogWarning(
                "Use Head Transform is enabled, but no Head Transform " +
                "has been assigned. Falling back to the Player Transform.",
                this
            );
        }

        if (playerTransform != null)
        {
            return playerTransform.position;
        }

        return transform.position;
    }

    private void UpdateWallCamera(
        Camera cam,
        Transform plane,
        Vector3 eyePosition)
    {
        if (cam == null || plane == null)
            return;

        MeshFilter meshFilter = plane.GetComponent<MeshFilter>();

        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            Debug.LogWarning(
                $"CAVE wall '{plane.name}' requires a MeshFilter.",
                plane
            );

            return;
        }

        Bounds bounds = meshFilter.sharedMesh.bounds;
        Vector3 wallCenter = plane.TransformPoint(bounds.center);

        /*
         * Determine the two axes that form the plane.
         *
         * A standard Unity Plane uses local X and Z.
         * A Quad normally uses local X and Y.
         */
        Vector3 xHalfAxis = plane.TransformVector(
            Vector3.right * bounds.extents.x
        );

        Vector3 yHalfAxis = plane.TransformVector(
            Vector3.up * bounds.extents.y
        );

        Vector3 zHalfAxis = plane.TransformVector(
            Vector3.forward * bounds.extents.z
        );

        Vector3[] possibleAxes =
        {
            xHalfAxis,
            yHalfAxis,
            zHalfAxis
        };

        Vector3 firstAxis = Vector3.zero;
        Vector3 secondAxis = Vector3.zero;

        for (int i = 0; i < possibleAxes.Length; i++)
        {
            if (possibleAxes[i].sqrMagnitude < 0.0000001f)
                continue;

            if (firstAxis == Vector3.zero)
            {
                firstAxis = possibleAxes[i];
            }
            else
            {
                secondAxis = possibleAxes[i];
                break;
            }
        }

        if (firstAxis == Vector3.zero ||
            secondAxis == Vector3.zero)
        {
            Debug.LogWarning(
                $"Could not determine the surface axes of '{plane.name}'.",
                plane
            );

            return;
        }

        /*
         * The axis most closely aligned with world Y is treated
         * as the vertical wall axis.
         */
        float firstVerticalAlignment = Mathf.Abs(
            Vector3.Dot(
                firstAxis.normalized,
                Vector3.up
            )
        );

        float secondVerticalAlignment = Mathf.Abs(
            Vector3.Dot(
                secondAxis.normalized,
                Vector3.up
            )
        );

        Vector3 rawHalfWidth;
        Vector3 halfHeight;

        if (firstVerticalAlignment > secondVerticalAlignment)
        {
            halfHeight = firstAxis;
            rawHalfWidth = secondAxis;
        }
        else
        {
            halfHeight = secondAxis;
            rawHalfWidth = firstAxis;
        }

        // Ensure the vertical axis points upward.
        if (Vector3.Dot(halfHeight, Vector3.up) < 0f)
        {
            halfHeight = -halfHeight;
        }

        Vector3 wallUp = halfHeight.normalized;

        /*
         * Calculate the wall normal and force it to point
         * from the wall toward the viewer.
         */
        Vector3 wallNormal = Vector3.Cross(
            rawHalfWidth.normalized,
            wallUp
        ).normalized;

        if (Vector3.Dot(
                wallNormal,
                eyePosition - wallCenter) < 0f)
        {
            wallNormal = -wallNormal;
        }

        /*
         * Calculate screen-right from the viewer-facing wall normal
         * and the wall's upward direction.
         *
         * Do not reverse this cross-product order. Reversing it
         * mirrors the displayed image horizontally.
         */
        Vector3 wallRight = Vector3.Cross(
            wallNormal,
            wallUp
        ).normalized;

        Vector3 halfWidth =
            wallRight * rawHalfWidth.magnitude;

        halfHeight =
            wallUp * halfHeight.magnitude;

        // Wall corners as seen by the viewer.
        Vector3 bottomLeft =
            wallCenter - halfWidth - halfHeight;

        Vector3 bottomRight =
            wallCenter + halfWidth - halfHeight;

        Vector3 topLeft =
            wallCenter - halfWidth + halfHeight;

        Vector3 topRight =
            wallCenter + halfWidth + halfHeight;

        ApplyOffAxisProjection(
            cam,
            eyePosition,
            bottomLeft,
            bottomRight,
            topLeft,
            wallRight,
            wallUp,
            wallNormal
        );

        if (drawDebugLines)
        {
            // Wall perimeter.
            Debug.DrawLine(
                bottomLeft,
                bottomRight,
                Color.red
            );

            Debug.DrawLine(
                bottomLeft,
                topLeft,
                Color.green
            );

            Debug.DrawLine(
                topLeft,
                topRight,
                Color.cyan
            );

            Debug.DrawLine(
                bottomRight,
                topRight,
                Color.yellow
            );

            // White points toward calculated screen-right.
            Debug.DrawRay(
                wallCenter,
                wallRight * 0.25f,
                Color.white
            );

            // Magenta points from the wall toward the viewer.
            Debug.DrawRay(
                wallCenter,
                wallNormal * 0.25f,
                Color.magenta
            );

            // Blue connects the tracked eye to the wall center.
            Debug.DrawLine(
                eyePosition,
                wallCenter,
                Color.blue
            );
        }
    }

    private void ApplyOffAxisProjection(
        Camera cam,
        Vector3 eyePosition,
        Vector3 bottomLeft,
        Vector3 bottomRight,
        Vector3 topLeft,
        Vector3 wallRight,
        Vector3 wallUp,
        Vector3 wallNormal)
    {
        Vector3 eyeToBottomLeft =
            bottomLeft - eyePosition;

        Vector3 eyeToBottomRight =
            bottomRight - eyePosition;

        Vector3 eyeToTopLeft =
            topLeft - eyePosition;

        /*
         * wallNormal points from the wall toward the viewer.
         * The wall is therefore in the camera's negative-Z direction.
         */
        float distanceToWall = -Vector3.Dot(
            eyeToBottomLeft,
            wallNormal
        );

        if (distanceToWall <= 0.0001f)
        {
            Debug.LogWarning(
                $"Viewer is on or behind the wall used by '{cam.name}'.",
                cam
            );

            return;
        }

        float near = nearClipPlane;
        float far = farClipPlane;

        float left =
            Vector3.Dot(
                wallRight,
                eyeToBottomLeft
            ) * near / distanceToWall;

        float right =
            Vector3.Dot(
                wallRight,
                eyeToBottomRight
            ) * near / distanceToWall;

        float bottom =
            Vector3.Dot(
                wallUp,
                eyeToBottomLeft
            ) * near / distanceToWall;

        float top =
            Vector3.Dot(
                wallUp,
                eyeToTopLeft
            ) * near / distanceToWall;

        if (right <= left || top <= bottom)
        {
            Debug.LogWarning(
                $"Invalid projection bounds for '{cam.name}'. " +
                $"Left={left}, Right={right}, " +
                $"Bottom={bottom}, Top={top}",
                cam
            );

            return;
        }

        cam.nearClipPlane = near;
        cam.farClipPlane = far;
        cam.transform.position = eyePosition;

        /*
         * Unity cameras look along local negative Z.
         *
         * wallNormal points toward the viewer, so points on the wall
         * receive a negative camera-space Z coordinate.
         */
        Matrix4x4 viewRotation = Matrix4x4.identity;

        viewRotation.SetRow(
            0,
            new Vector4(
                wallRight.x,
                wallRight.y,
                wallRight.z,
                0f
            )
        );

        viewRotation.SetRow(
            1,
            new Vector4(
                wallUp.x,
                wallUp.y,
                wallUp.z,
                0f
            )
        );

        viewRotation.SetRow(
            2,
            new Vector4(
                wallNormal.x,
                wallNormal.y,
                wallNormal.z,
                0f
            )
        );

        Matrix4x4 translation =
            Matrix4x4.Translate(-eyePosition);

        cam.worldToCameraMatrix =
            viewRotation * translation;

        cam.projectionMatrix = PerspectiveOffCenter(
            left,
            right,
            bottom,
            top,
            near,
            far
        );

        cam.cullingMatrix =
            cam.projectionMatrix *
            cam.worldToCameraMatrix;
    }

    private Matrix4x4 PerspectiveOffCenter(
        float left,
        float right,
        float bottom,
        float top,
        float near,
        float far)
    {
        float x =
            2f * near / (right - left);

        float y =
            2f * near / (top - bottom);

        float a =
            (right + left) / (right - left);

        float b =
            (top + bottom) / (top - bottom);

        float c =
            -(far + near) / (far - near);

        float d =
            -(2f * far * near) / (far - near);

        Matrix4x4 matrix = new Matrix4x4();

        matrix[0, 0] = x;
        matrix[0, 1] = 0f;
        matrix[0, 2] = a;
        matrix[0, 3] = 0f;

        matrix[1, 0] = 0f;
        matrix[1, 1] = y;
        matrix[1, 2] = b;
        matrix[1, 3] = 0f;

        matrix[2, 0] = 0f;
        matrix[2, 1] = 0f;
        matrix[2, 2] = c;
        matrix[2, 3] = d;

        matrix[3, 0] = 0f;
        matrix[3, 1] = 0f;
        matrix[3, 2] = -1f;
        matrix[3, 3] = 0f;

        return matrix;
    }

    private void OnDisable()
    {
        ResetCamera(leftScreen);
        ResetCamera(rightScreen);
    }

    private void OnDestroy()
    {
        ResetCamera(leftScreen);
        ResetCamera(rightScreen);
    }

    private void ResetCamera(Camera cam)
    {
        if (cam == null)
            return;

        cam.ResetWorldToCameraMatrix();
        cam.ResetProjectionMatrix();
        cam.ResetCullingMatrix();
    }
}
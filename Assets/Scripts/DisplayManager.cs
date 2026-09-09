using UnityEngine;

public class DisplayManager : MonoBehaviour
{
    [Header("Cameras")]
    public Camera mainCamera;
    public Camera leftCamera;
    public Camera rightCamera;

    [Header("DEV Mode RenderTextures")]
    public RenderTexture leftRenderTexture;
    public RenderTexture rightRenderTexture;

    private void Start()
    {
        LogDisplays();

        if (GameSettings.DevMode)
        {
            SetupDevMode();
        }
        else
        {
            SetupFullscreenMode();
        }
    }

    private void SetupDevMode()
    {
        Debug.Log("DisplayManager: DEV MODE");

        // Show bird's-eye camera
        if (mainCamera != null)
            mainCamera.enabled = true;

        // Render cave cameras to the wall textures
        if (leftCamera != null)
        {
            leftCamera.targetTexture = leftRenderTexture;
            leftCamera.targetDisplay = 0;
        }

        if (rightCamera != null)
        {
            rightCamera.targetTexture = rightRenderTexture;
            rightCamera.targetDisplay = 0;
        }

        Screen.fullScreen = false;
    }

    private void SetupFullscreenMode()
    {
        Debug.Log("DisplayManager: FULLSCREEN CAVE MODE");

        if (Display.displays.Length < 2)
        {
            Debug.LogWarning(
                "Only one display detected. Falling back to DEV mode."
            );

            SetupDevMode();
            return;
        }

        Screen.fullScreenMode =
            FullScreenMode.FullScreenWindow;

        Screen.fullScreen = true;

        // Activate all secondary displays
        for (int i = 1; i < Display.displays.Length; i++)
        {
            Display.displays[i].Activate();
        }

        // Hide spectator view
        if (mainCamera != null)
            mainCamera.enabled = false;

        // Direct display rendering
        if (leftCamera != null)
        {
            leftCamera.targetTexture = null;
            leftCamera.targetDisplay = 0;
        }

        if (rightCamera != null)
        {
            rightCamera.targetTexture = null;
            rightCamera.targetDisplay = 1;
        }
    }

    private void LogDisplays()
    {
        Debug.Log(
            $"Detected Displays: {Display.displays.Length}"
        );

        for (int i = 0; i < Display.displays.Length; i++)
        {
            Debug.Log(
                $"Display {i}: " +
                $"{Display.displays[i].systemWidth}x" +
                $"{Display.displays[i].systemHeight}"
            );
        }
    }
}
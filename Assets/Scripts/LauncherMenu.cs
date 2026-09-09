using UnityEngine;
using UnityEngine.SceneManagement;

public static class GameSettings
{
    public static bool DevMode = true;
}

public class LauncherMenu : MonoBehaviour
{
    [Header("Main Scene")]
    public string mainSceneName = "MainScene";

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    private void OnGUI()
    {
        float panelWidth = 500f;
        float panelHeight = 220f;

        float panelX = (Screen.width - panelWidth) * 0.5f;
        float panelY = (Screen.height - panelHeight) * 0.5f;

        GUI.Box(
            new Rect(panelX, panelY, panelWidth, panelHeight),
            "Virtual CAVE Launcher"
        );

        if (GUI.Button(
            new Rect(panelX + 50, panelY + 60, 400, 50),
            "DEV MODE"))
        {
            LaunchDevMode();
        }

        if (GUI.Button(
            new Rect(panelX + 50, panelY + 130, 400, 50),
            "FULLSCREEN CAVE"))
        {
            LaunchFullscreenMode();
        }
    }

    private void LaunchDevMode()
    {
        if (string.IsNullOrEmpty(mainSceneName))
        {
            Debug.LogError("Main Scene Name is empty.");
            return;
        }

        GameSettings.DevMode = true;

        Debug.Log("Launching DEV Mode");

        SceneManager.LoadScene(mainSceneName);
    }

    private void LaunchFullscreenMode()
    {
        if (string.IsNullOrEmpty(mainSceneName))
        {
            Debug.LogError("Main Scene Name is empty.");
            return;
        }

        GameSettings.DevMode = false;

        Debug.Log("Launching FULLSCREEN CAVE Mode");

        SceneManager.LoadScene(mainSceneName);
    }
}
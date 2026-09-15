using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GameSettings
{
    public static bool DevMode = false;
}

public class LauncherMenu : MonoBehaviour
{
    private bool devMode = false;
    private string[] sceneNames;

    private void Start()
    {
        List<string> availableScenes = new List<string>();

        string launcherSceneName = SceneManager.GetActiveScene().name;
        int sceneCount = SceneManager.sceneCountInBuildSettings;

        for (int i = 0; i < sceneCount; i++)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            string sceneName = Path.GetFileNameWithoutExtension(path);

            // Hide launcher scene
            if (sceneName == launcherSceneName)
                continue;

            availableScenes.Add(sceneName);
        }

        sceneNames = availableScenes.ToArray();

        Debug.Log($"Found {sceneNames.Length} launchable scenes:");

        foreach (string scene in sceneNames)
        {
            Debug.Log($" - {scene}");
        }
    }

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
        float panelWidth = 600f;

        // Auto-size panel based on scene count
        float panelHeight = 150f + (sceneNames.Length * 50f);

        float panelX = (Screen.width - panelWidth) * 0.5f;
        float panelY = (Screen.height - panelHeight) * 0.5f;

        GUI.Box(
            new Rect(panelX, panelY, panelWidth, panelHeight),
            "Virtual CAVE Launcher"
        );

        // Dev mode toggle
        devMode = GUI.Toggle(
            new Rect(panelX + 30, panelY + 40, 200, 30),
            devMode,
            "Dev Mode"
        );

        GUI.Label(
            new Rect(panelX + 30, panelY + 80, 200, 25),
            "Select Scene:"
        );

        // One button per scene
        for (int i = 0; i < sceneNames.Length; i++)
        {
            if (GUI.Button(
                new Rect(
                    panelX + 30,
                    panelY + 115 + (i * 45),
                    panelWidth - 60,
                    40),
                sceneNames[i]))
            {
                GameSettings.DevMode = devMode;

                Debug.Log(
                    $"Launching '{sceneNames[i]}' | DevMode={GameSettings.DevMode}"
                );

                SceneManager.LoadScene(sceneNames[i]);
            }
        }

        if (sceneNames.Length == 0)
        {
            GUI.Label(
                new Rect(panelX + 30, panelY + 120, 400, 30),
                "No launchable scenes found in Build Settings."
            );
        }
    }
}
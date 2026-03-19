using UnityEngine;
using PlayEveryWare.EpicOnlineServices;

public static class EosPrototypeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        EnsureSingletons();
    }

    private static void EnsureSingletons()
    {
        if (Object.FindObjectOfType<EOSManager>() == null)
        {
            var eosGo = new GameObject("EOSManager");
            eosGo.AddComponent<EOSManager>();
            Object.DontDestroyOnLoad(eosGo);
        }

        // [Territory Mode] EosPrototypeController temporarily disabled
        // if (Object.FindObjectOfType<EosPrototypeController>() == null)
        // {
        //     var protoGo = new GameObject("EosPrototypeController");
        //     protoGo.AddComponent<EosPrototypeController>();
        //     Object.DontDestroyOnLoad(protoGo);
        // }

        // Show game mode selector instead of directly launching a mode
        if (Object.FindObjectOfType<GameModeSelector>() == null)
        {
            var selectorGo = new GameObject("GameModeSelector");
            selectorGo.AddComponent<GameModeSelector>();
            Object.DontDestroyOnLoad(selectorGo);
        }
    }
}

/// <summary>
/// Simple OnGUI menu to choose between game modes.
/// Spawns the selected controller and destroys itself.
/// </summary>
public class GameModeSelector : MonoBehaviour
{
    private void OnGUI()
    {
        float w = 400, h = 320;
        float x = (Screen.width - w) * 0.5f;
        float y = (Screen.height - h) * 0.5f;

        GUILayout.BeginArea(new Rect(x, y, w, h), GUI.skin.box);

        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
        };
        GUILayout.Space(15);
        GUILayout.Label("Select Game Mode", titleStyle);
        GUILayout.Space(20);

        var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        btnStyle.fixedHeight = 45;

        if (GUILayout.Button("Territory Capture", btnStyle))
        {
            LaunchTerritory();
        }

        GUILayout.Space(10);

        if (GUILayout.Button("Cops & Robbers", btnStyle))
        {
            LaunchCopsAndRobbers();
        }

        GUILayout.Space(10);

        if (GUILayout.Button("Island Warfare", btnStyle))
        {
            LaunchIslandWarfare();
        }

        GUILayout.Space(15);

        var descStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = true
        };
        GUILayout.Label("Territory: 2 players, capture cubes on 64x64 grid\nCops & Robbers: up to 10 players, tag game\nIsland Warfare: up to 4 players, build & battle", descStyle);

        GUILayout.EndArea();
    }

    private void LaunchTerritory()
    {
        if (Object.FindObjectOfType<TerritoryGameController>() == null)
        {
            var go = new GameObject("TerritoryGameController");
            go.AddComponent<TerritoryGameController>();
            DontDestroyOnLoad(go);
        }
        Destroy(gameObject);
    }

    private void LaunchCopsAndRobbers()
    {
        if (Object.FindObjectOfType<CopsAndRobbersController>() == null)
        {
            var go = new GameObject("CopsAndRobbersController");
            go.AddComponent<CopsAndRobbersController>();
            DontDestroyOnLoad(go);
        }
        Destroy(gameObject);
    }

    private void LaunchIslandWarfare()
    {
        if (Object.FindObjectOfType<IslandWarfareController>() == null)
        {
            var go = new GameObject("IslandWarfareController");
            go.AddComponent<IslandWarfareController>();
            DontDestroyOnLoad(go);
        }
        Destroy(gameObject);
    }
}

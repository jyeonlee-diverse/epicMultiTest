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
        if (UnityEngine.Object.FindObjectOfType<EOSManager>() == null)
        {
            var eosGo = new GameObject("EOSManager");
            eosGo.AddComponent<EOSManager>();
            UnityEngine.Object.DontDestroyOnLoad(eosGo);
        }

        // [Territory Mode] EosPrototypeController temporarily disabled
        // if (UnityEngine.Object.FindObjectOfType<EosPrototypeController>() == null)
        // {
        //     var protoGo = new GameObject("EosPrototypeController");
        //     protoGo.AddComponent<EosPrototypeController>();
        //     UnityEngine.Object.DontDestroyOnLoad(protoGo);
        // }

        if (UnityEngine.Object.FindObjectOfType<TerritoryGameController>() == null)
        {
            var territoryGo = new GameObject("TerritoryGameController");
            territoryGo.AddComponent<TerritoryGameController>();
            UnityEngine.Object.DontDestroyOnLoad(territoryGo);
        }
    }
}

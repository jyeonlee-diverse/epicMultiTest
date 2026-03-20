using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using PlayEveryWare.EpicOnlineServices;
using PlayEveryWare.EpicOnlineServices.Samples;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Sessions;

/// <summary>
/// Cops and Robbers multiplayer game on a 64x64 grid.
/// Police try to tag all thieves within 3 minutes.
/// Thieves try to survive, rescue jailed allies, or reach escape points.
/// Uses EOS P2P networking (same architecture as TerritoryGameController).
/// </summary>
public class CopsAndRobbersController : MonoBehaviour
{
    // ───── EOS Constants ─────
    private const string LobbyAttributeCode = "CODE";
    private const string LobbyAttributeSessionId = "SESSIONID";
    private const string LobbyAttributeState = "STATE";
    private const string LobbyAttributeSeed = "SEED";
    private const string LobbyAttributeRoles = "ROLES";
    private const string LobbyMemberReadyKey = "READY";
    private const string LobbyAttributeRound = "ROUND";
    private const string LobbyAttributeScoreA = "SCOREA";
    private const string LobbyAttributeScoreB = "SCOREB";
    private const string LobbyAttributeTeams = "TEAMS";
    private const string LobbyAttributeLastWinner = "LASTWINNER";
    private const string P2PSocketName = "COPSROBBERS";

    // ───── Game Constants ─────
    private const int GridSize = 64;
    private const float CellSize = 1f;
    private const int MaxPlayers = 10;
    private const float GameDuration = 180f; // 3 minutes
    private const float PoliceDelay = 5f; // police can't move for 5s
    private const float TagRadius = 1.5f;
    private const float RescueRadius = 3.0f;
    private const float EscapeRadius = 2.0f;
    private const int BlockSize = 8;

    // ───── Dungeon Generation ─────
    private const int DungeonGridW = 16;
    private const int DungeonGridH = 16;
    private const float DungeonCellSize = 4f; // each cell = 4x4 world units
    private const int DungeonRoomCount = 6;
    private const int DungeonRoomMinDist = 3; // min cells between room centers

    private enum CellType { Wall, Room, Corridor }
    private CellType[,] dungeonGrid;
    private readonly List<Vector2Int> roomPositions = new List<Vector2Int>();
    private GameObject dungeonRoot;

    // Dungeon model prefabs (loaded from Resources/DungeonKit)
    private GameObject prefabCorridor;
    private GameObject prefabCorridorCorner;
    private GameObject prefabCorridorEnd;
    private GameObject prefabCorridorIntersection;
    private GameObject prefabCorridorJunction;
    private GameObject prefabRoomSmall;
    private GameObject prefabRoomSmallVar;
    private GameObject prefabRoomLarge;
    private GameObject prefabHole;

    // ───── Map Positions ─────
    private static readonly Vector2Int JailCenter = new Vector2Int(32, 32);
    private static readonly Vector2Int[] EscapePoints = {
        new Vector2Int(4, 4), new Vector2Int(60, 4),
        new Vector2Int(4, 60), new Vector2Int(60, 60)
    };

    // ───── Colors ─────
    private static readonly Color ColorNeutral = new Color(0.35f, 0.35f, 0.35f, 1f);
    private static readonly Color ColorGridLine = new Color(0.28f, 0.28f, 0.28f, 1f);
    private static readonly Color ColorJail = new Color(0.9f, 0.8f, 0.1f, 0.6f);
    private static readonly Color ColorEscape = new Color(0.1f, 0.9f, 0.3f, 0.6f);
    private static readonly Color ColorPolice = new Color(0.2f, 0.4f, 0.95f, 1f);
    private static readonly Color ColorThief = new Color(0.95f, 0.2f, 0.2f, 1f);
    private static readonly Color ColorJailed = new Color(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Color ColorEscaped = new Color(0.2f, 0.9f, 0.3f, 1f);
    private static readonly Color ColorObstacle = new Color(0.45f, 0.42f, 0.4f, 1f);

    // ───── Shader Mode ─────
    public enum ShaderMode { Standard, Jelly, Clay }
    [Header("Shader Mode")]
    public ShaderMode shaderMode = ShaderMode.Jelly;

    // ───── EOS Managers ─────
    private EOSLobbyManager lobbyManager;
    private EOSSessionsManager sessionsManager;
    private P2PInterface p2p;
    private ulong connectionNotifyId;
    private bool isQuitting;

    // ───── Login/Lobby State ─────
    private bool isLoggedIn;
    private bool isHosting;
    private bool isSearchingLobbies;
    private bool isReady;
    private bool gameStarted;
    private bool gameEnded;
    private bool startUpdateInProgress;

    private string displayName;
    private string lobbyCode = "COPS";
    private string statusMessage = "";
    private string gameState = "WAITING";
    private string devAuthAddress = "localhost:6547";
    private string devAuthCredential = "";
    private int gameSeed;

    private float sendInterval = 0.1f;
    private float lastSendTime;

    // ───── Role System ─────
    private enum PlayerRole { None, Police, Thief }
    private enum ThiefState { Free, Jailed, Escaped }

    private PlayerRole localRole = PlayerRole.None;
    private ThiefState localThiefState = ThiefState.Free;
    private readonly Dictionary<string, PlayerRole> playerRoles = new Dictionary<string, PlayerRole>();
    private readonly Dictionary<string, ThiefState> thiefStates = new Dictionary<string, ThiefState>();

    // ───── Timer ─────
    private float gameStartTime;
    private float remainingTime;
    private float lastTimerSendTime;

    // ───── Match System (Best of 3) ─────
    private int currentRound = 0;
    private int teamAWins = 0;
    private int teamBWins = 0;
    private readonly List<string> teamAMembers = new List<string>();
    private readonly List<string> teamBMembers = new List<string>();
    private bool matchInProgress = false;
    private bool matchEnded = false;
    private bool isRoundTransition = false;
    private float roundTransitionStartTime;
    private const float RoundTransitionDuration = 5f;
    private const int WinsNeeded = 2;
    private string lastRoundWinner = "";

    // ───── Character Models ─────
    private const string PoliceFbxPath = "Characters/character-male-c";
    private const string ThiefFbxPath = "Characters/character-male-e";
    private GameObject policePrefab;
    private GameObject thiefPrefab;

    // ───── Players ─────
    private GameObject localPlayer;
    private readonly Dictionary<string, GameObject> remotePlayers = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, RemoteMotionState> remoteStates = new Dictionary<string, RemoteMotionState>();

    private struct RemoteMotionState
    {
        public Vector3 Target;
        public Vector3 Velocity;
        public float LastRecvTime;
    }

    // ───── Grid & World ─────
    private Texture2D gridTexture;
    private GameObject groundObj;
    private Material groundMaterial;

    // ───── Markers ─────
    private GameObject jailMarker;
    private readonly List<GameObject> escapeMarkers = new List<GameObject>();

    // ───── Camera ─────
    private static readonly Vector3 CameraOffsetLobby = new Vector3(0f, 30f, -20f);
    private static readonly Vector3 CameraOffsetGame = new Vector3(0f, 12f, -8f);
    private Vector3 cameraOffset = CameraOffsetLobby;

    // ───── Deferred Lobby Write (rate-limit protection) ─────
    private string pendingRolesJson;
    private string pendingTeamsJson;
    private float pendingLobbyWriteTime = -1f;

    // ───── Lobby Search ─────
    private class LobbySearchEntry
    {
        public Lobby Lobby;
        public Epic.OnlineServices.Lobby.LobbyDetails Details;
    }
    private readonly List<LobbySearchEntry> lobbyResults = new List<LobbySearchEntry>();

    // ═══════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════

    private void Awake()
    {
        Application.runInBackground = true;
        displayName = "Player" + UnityEngine.Random.Range(1000, 9999);
        policePrefab = Resources.Load<GameObject>(PoliceFbxPath);
        thiefPrefab = Resources.Load<GameObject>(ThiefFbxPath);
        if (policePrefab == null) Debug.LogError("Police FBX not found at Resources/" + PoliceFbxPath);
        if (thiefPrefab == null) Debug.LogError("Thief FBX not found at Resources/" + ThiefFbxPath);

        // Load dungeon kit models
        prefabCorridor = Resources.Load<GameObject>("DungeonKit/corridor");
        prefabCorridorCorner = Resources.Load<GameObject>("DungeonKit/corridor-corner");
        prefabCorridorEnd = Resources.Load<GameObject>("DungeonKit/corridor-end");
        prefabCorridorIntersection = Resources.Load<GameObject>("DungeonKit/corridor-intersection");
        prefabCorridorJunction = Resources.Load<GameObject>("DungeonKit/corridor-junction");
        prefabRoomSmall = Resources.Load<GameObject>("DungeonKit/room-small");
        prefabRoomSmallVar = Resources.Load<GameObject>("DungeonKit/room-small-variation");
        prefabRoomLarge = Resources.Load<GameObject>("DungeonKit/room-large");
        prefabHole = Resources.Load<GameObject>("DungeonKit/hole");
        if (prefabHole == null) Debug.LogError("hole.fbx not found at Resources/DungeonKit/hole");
    }

    private void Start()
    {
        InitGrid();
        EnsureWorld();
    }

    private void Update()
    {
        TryResolveManagers();
        UpdateLobbyState();
        UpdateReadyState();
        UpdateRoundTransition();
        UpdateDeferredLobbyWrite();
        UpdateLocalMovement();
        UpdateGameMechanics();
        UpdateRemotePlayers();
        UpdateCamera();
        UpdateTimer();
        SendLocalPositionIfNeeded();
        ReceivePackets();
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
    }

    private void OnDestroy()
    {
        if (isQuitting) return;
        if (p2p != null && connectionNotifyId != 0)
        {
            p2p.RemoveNotifyPeerConnectionRequest(connectionNotifyId);
            connectionNotifyId = 0;
        }
    }

    // ═══════════════════════════════════════════
    //  GRID & WORLD SETUP
    // ═══════════════════════════════════════════

    private void InitGrid()
    {
        gridTexture = new Texture2D(GridSize, GridSize, TextureFormat.RGBA32, false);
        gridTexture.filterMode = FilterMode.Point;
        gridTexture.wrapMode = TextureWrapMode.Clamp;
        PaintGrid();
        gridTexture.Apply();
    }

    private void PaintGrid()
    {
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                Color c = (x % BlockSize == 0 || z % BlockSize == 0) ? ColorGridLine : ColorNeutral;
                gridTexture.SetPixel(x, z, c);
            }
        }

        // Paint jail zone (8x8 centered at 32,32)
        int jailStart = JailCenter.x - BlockSize / 2;
        for (int x = jailStart; x < jailStart + BlockSize; x++)
            for (int z = jailStart; z < jailStart + BlockSize; z++)
                if (x >= 0 && x < GridSize && z >= 0 && z < GridSize)
                    gridTexture.SetPixel(x, z, ColorJail);

        // Paint escape zones (3x3 at each corner)
        foreach (var ep in EscapePoints)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int ex = ep.x + dx, ez = ep.y + dz;
                    if (ex >= 0 && ex < GridSize && ez >= 0 && ez < GridSize)
                        gridTexture.SetPixel(ex, ez, ColorEscape);
                }
        }
    }

    private void EnsureWorld()
    {
        // ── Light ──
        if (GameObject.Find("CRLight") == null)
        {
            var lightGo = new GameObject("CRLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        // ── Ground ──
        groundObj = GameObject.Find("CRGround");
        if (groundObj == null)
        {
            groundObj = new GameObject("CRGround");
            groundObj.transform.position = Vector3.zero;

            var mesh = new Mesh();
            float groundY = -0.05f; // Slightly below Y=0 to avoid Z-fighting with dungeon floor models
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, groundY, 0), new Vector3(GridSize, groundY, 0),
                new Vector3(0, groundY, GridSize), new Vector3(GridSize, groundY, GridSize)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 1), new Vector2(1, 1)
            };
            mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            mesh.normals = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

            groundObj.AddComponent<MeshFilter>().mesh = mesh;
            var meshRenderer = groundObj.AddComponent<MeshRenderer>();
            var collider = groundObj.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;

            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            groundMaterial = new Material(shader);
            groundMaterial.mainTexture = gridTexture;
            groundMaterial.EnableKeyword("_EMISSION");
            groundMaterial.SetTexture("_EmissionMap", gridTexture);
            groundMaterial.SetColor("_EmissionColor", Color.white * 0.6f);
            meshRenderer.sharedMaterial = groundMaterial;
        }

        // Jail and escape markers are placed dynamically during GenerateDungeon()

        // ── Local Player ──
        if (localPlayer == null)
        {
            localPlayer = GameObject.Find("CRLocalPlayer");
        }
        if (localPlayer == null)
        {
            // Start with thief model; will be swapped when role is assigned
            localPlayer = CreateCharacterModel(thiefPrefab, "CRLocalPlayer");
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, GridSize * 0.25f);
        }

        // ── Camera ──
        var cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = localPlayer.transform.position + cameraOffset;
            cam.transform.LookAt(localPlayer.transform.position);
        }
    }

    private void CreateJailWall(Transform parent, Vector3 pos, Vector3 scale)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "JailWall";
        wall.transform.SetParent(parent);
        wall.transform.position = pos;
        wall.transform.localScale = scale;
        var r = wall.GetComponent<Renderer>();
        if (r != null)
        {
            var mat = CreateMat(new Color(0.9f, 0.8f, 0.1f, 0.5f));
            mat.SetFloat("_Mode", 3); // Transparent
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
            r.sharedMaterial = mat;
        }
        // Jail walls are visual only, no collision
        var col = wall.GetComponent<Collider>();
        if (col != null) Destroy(col);
    }

    // ═══════════════════════════════════════════
    //  PROCEDURAL DUNGEON GENERATION
    // ═══════════════════════════════════════════

    private void GenerateDungeon()
    {
        // Destroy old dungeon
        if (dungeonRoot != null) { Destroy(dungeonRoot); dungeonRoot = null; }
        dungeonRoot = new GameObject("DungeonRoot");
        roomPositions.Clear();

        var rng = new System.Random(gameSeed != 0 ? gameSeed : 12345);
        dungeonGrid = new CellType[DungeonGridW, DungeonGridH];

        // 1) Initialize all as Wall
        for (int x = 0; x < DungeonGridW; x++)
            for (int z = 0; z < DungeonGridH; z++)
                dungeonGrid[x, z] = CellType.Wall;

        // 2) Place rooms with minimum spacing
        int attempts = 0;
        while (roomPositions.Count < DungeonRoomCount && attempts < 200)
        {
            int rx = 1 + rng.Next(DungeonGridW - 2);
            int rz = 1 + rng.Next(DungeonGridH - 2);
            var candidate = new Vector2Int(rx, rz);

            bool tooClose = false;
            foreach (var existing in roomPositions)
            {
                if (Mathf.Abs(existing.x - rx) + Mathf.Abs(existing.y - rz) < DungeonRoomMinDist)
                { tooClose = true; break; }
            }

            if (!tooClose)
            {
                roomPositions.Add(candidate);
                dungeonGrid[rx, rz] = CellType.Room;
            }
            attempts++;
        }

        // 3) Connect rooms using Minimum Spanning Tree (Prim's algorithm)
        if (roomPositions.Count >= 2)
        {
            var connected = new List<int> { 0 };
            var remaining = new List<int>();
            for (int i = 1; i < roomPositions.Count; i++) remaining.Add(i);

            while (remaining.Count > 0)
            {
                int bestFrom = -1, bestTo = -1;
                int bestDist = int.MaxValue;

                foreach (int c in connected)
                {
                    foreach (int r in remaining)
                    {
                        int dist = Mathf.Abs(roomPositions[c].x - roomPositions[r].x) +
                                   Mathf.Abs(roomPositions[c].y - roomPositions[r].y);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestFrom = c;
                            bestTo = r;
                        }
                    }
                }

                if (bestTo < 0) break;
                CarveCorridor(roomPositions[bestFrom], roomPositions[bestTo], rng);
                connected.Add(bestTo);
                remaining.Remove(bestTo);
            }

            // Add 1-2 extra corridors for loops (more interesting gameplay)
            int extras = 1 + rng.Next(2);
            for (int i = 0; i < extras && roomPositions.Count >= 2; i++)
            {
                int a = rng.Next(roomPositions.Count);
                int b = rng.Next(roomPositions.Count);
                if (a != b) CarveCorridor(roomPositions[a], roomPositions[b], rng);
            }
        }

        // 4) Place jail at the center room (or nearest room to center)
        // Update jail position to match the closest room to grid center
        int centerX = DungeonGridW / 2, centerZ = DungeonGridH / 2;
        int closestRoomIdx = 0;
        int closestDist = int.MaxValue;
        for (int i = 0; i < roomPositions.Count; i++)
        {
            int d = Mathf.Abs(roomPositions[i].x - centerX) + Mathf.Abs(roomPositions[i].y - centerZ);
            if (d < closestDist) { closestDist = d; closestRoomIdx = i; }
        }

        // 5) Place dungeon models
        PlaceDungeonModels(rng);

        // 6) Place jail and escape markers
        PlaceDungeonMarkers();

        // 7) Update ground texture to show dungeon layout
        PaintDungeonGrid();
        gridTexture.Apply();
        if (groundMaterial != null)
        {
            groundMaterial.mainTexture = gridTexture;
            groundMaterial.SetTexture("_EmissionMap", gridTexture);
        }
    }

    private void CarveCorridor(Vector2Int from, Vector2Int to, System.Random rng)
    {
        int x = from.x, z = from.y;
        int tx = to.x, tz = to.y;

        // L-shaped path: randomly choose horizontal-first or vertical-first
        bool horizontalFirst = rng.Next(2) == 0;

        if (horizontalFirst)
        {
            while (x != tx) { x += (tx > x) ? 1 : -1; if (dungeonGrid[x, z] == CellType.Wall) dungeonGrid[x, z] = CellType.Corridor; }
            while (z != tz) { z += (tz > z) ? 1 : -1; if (dungeonGrid[x, z] == CellType.Wall) dungeonGrid[x, z] = CellType.Corridor; }
        }
        else
        {
            while (z != tz) { z += (tz > z) ? 1 : -1; if (dungeonGrid[x, z] == CellType.Wall) dungeonGrid[x, z] = CellType.Corridor; }
            while (x != tx) { x += (tx > x) ? 1 : -1; if (dungeonGrid[x, z] == CellType.Wall) dungeonGrid[x, z] = CellType.Corridor; }
        }
    }

    private int GetNeighborMask(int x, int z)
    {
        // Bit mask: North(8) East(4) South(2) West(1)
        int mask = 0;
        if (z + 1 < DungeonGridH && dungeonGrid[x, z + 1] != CellType.Wall) mask |= 8; // North
        if (x + 1 < DungeonGridW && dungeonGrid[x + 1, z] != CellType.Wall) mask |= 4; // East
        if (z - 1 >= 0 && dungeonGrid[x, z - 1] != CellType.Wall) mask |= 2;            // South
        if (x - 1 >= 0 && dungeonGrid[x - 1, z] != CellType.Wall) mask |= 1;            // West
        return mask;
    }

    private void PlaceDungeonModels(System.Random rng)
    {
        for (int x = 0; x < DungeonGridW; x++)
        {
            for (int z = 0; z < DungeonGridH; z++)
            {
                if (dungeonGrid[x, z] == CellType.Wall) continue;

                float worldX = x * DungeonCellSize + DungeonCellSize * 0.5f;
                float worldZ = z * DungeonCellSize + DungeonCellSize * 0.5f;
                Vector3 pos = new Vector3(worldX, 0f, worldZ);

                int mask = GetNeighborMask(x, z);

                if (dungeonGrid[x, z] == CellType.Room)
                {
                    // Room: use room-small or variation
                    GameObject prefab = (rng.Next(2) == 0) ? prefabRoomSmall : prefabRoomSmallVar;
                    if (prefab == null) prefab = prefabRoomSmall;
                    PlaceModel(prefab, pos, 0f);
                }
                else // Corridor
                {
                    PlaceCorridorByMask(mask, pos, rng);
                }
            }
        }
    }

    private void PlaceCorridorByMask(int mask, Vector3 pos, System.Random rng)
    {
        int connections = 0;
        for (int b = 0; b < 4; b++) if ((mask & (1 << b)) != 0) connections++;

        GameObject prefab = null;
        float rotation = 0f;

        switch (connections)
        {
            case 0: // Isolated (shouldn't happen, but use end piece)
                prefab = prefabCorridorEnd;
                break;
            case 1: // Dead end
                prefab = prefabCorridorEnd;
                if ((mask & 8) != 0) rotation = 0f;        // North open
                else if ((mask & 4) != 0) rotation = 90f;  // East open
                else if ((mask & 2) != 0) rotation = 180f; // South open
                else rotation = 270f;                       // West open
                break;
            case 2: // Straight or Corner
                // Check if straight (opposite directions)
                if ((mask & 0b1010) == 0b1010) { prefab = prefabCorridor; rotation = 0f; }   // N-S
                else if ((mask & 0b0101) == 0b0101) { prefab = prefabCorridor; rotation = 90f; } // E-W
                else
                {
                    // Corner
                    prefab = prefabCorridorCorner;
                    if ((mask & 0b1100) == 0b1100) rotation = 0f;        // N+E
                    else if ((mask & 0b0110) == 0b0110) rotation = 90f;  // E+S
                    else if ((mask & 0b0011) == 0b0011) rotation = 180f; // S+W
                    else rotation = 270f;                                // W+N
                }
                break;
            case 3: // T-junction
                prefab = prefabCorridorJunction;
                if ((mask & 2) == 0) rotation = 0f;        // No South → T facing N-E-W
                else if ((mask & 1) == 0) rotation = 90f;  // No West
                else if ((mask & 8) == 0) rotation = 180f; // No North
                else rotation = 270f;                       // No East
                break;
            case 4: // Intersection
                prefab = prefabCorridorIntersection;
                break;
        }

        if (prefab != null)
            PlaceModel(prefab, pos, rotation);
    }

    private void PlaceModel(GameObject prefab, Vector3 pos, float yRotation)
    {
        if (prefab == null) return;
        var go = Instantiate(prefab, pos, Quaternion.Euler(0f, yRotation, 0f));
        go.transform.SetParent(dungeonRoot.transform);
        // Remove any colliders from the model to avoid physics interference
        foreach (var col in go.GetComponentsInChildren<Collider>())
            Destroy(col);
    }

    private void PaintDungeonGrid()
    {
        Color wallColor = new Color(0.12f, 0.1f, 0.14f, 1f);
        Color corridorColor = new Color(0.3f, 0.28f, 0.26f, 1f);
        Color roomColor = new Color(0.45f, 0.4f, 0.35f, 1f);

        for (int px = 0; px < GridSize; px++)
        {
            for (int pz = 0; pz < GridSize; pz++)
            {
                int cx = px / (int)DungeonCellSize;
                int cz = pz / (int)DungeonCellSize;
                cx = Mathf.Clamp(cx, 0, DungeonGridW - 1);
                cz = Mathf.Clamp(cz, 0, DungeonGridH - 1);

                CellType cell = dungeonGrid != null ? dungeonGrid[cx, cz] : CellType.Wall;
                Color c;
                switch (cell)
                {
                    case CellType.Room: c = roomColor; break;
                    case CellType.Corridor: c = corridorColor; break;
                    default: c = wallColor; break;
                }

                // Grid lines within cells
                int localX = px % (int)DungeonCellSize;
                int localZ = pz % (int)DungeonCellSize;
                if (localX == 0 || localZ == 0)
                    c = Color.Lerp(c, Color.black, 0.3f);

                gridTexture.SetPixel(px, pz, c);
            }
        }

        // Paint jail zone
        if (roomPositions.Count > 0)
        {
            var jailRoom = GetJailRoomPosition();
            int jx = jailRoom.x * (int)DungeonCellSize;
            int jz = jailRoom.y * (int)DungeonCellSize;
            for (int dx = 0; dx < (int)DungeonCellSize; dx++)
                for (int dz = 0; dz < (int)DungeonCellSize; dz++)
                    if (jx + dx < GridSize && jz + dz < GridSize)
                        gridTexture.SetPixel(jx + dx, jz + dz, ColorJail);
        }

        // Paint escape rooms
        var escapeRooms = GetEscapeRoomPositions();
        foreach (var ep in escapeRooms)
        {
            int ex = ep.x * (int)DungeonCellSize;
            int ez = ep.y * (int)DungeonCellSize;
            for (int dx = 0; dx < (int)DungeonCellSize; dx++)
                for (int dz = 0; dz < (int)DungeonCellSize; dz++)
                    if (ex + dx < GridSize && ez + dz < GridSize)
                        gridTexture.SetPixel(ex + dx, ez + dz, ColorEscape);
        }
    }

    private void PlaceDungeonMarkers()
    {
        // Clean old markers
        if (jailMarker != null) { Destroy(jailMarker); jailMarker = null; }
        foreach (var m in escapeMarkers) { if (m != null) Destroy(m); }
        escapeMarkers.Clear();

        // Jail marker at jail room
        var jailRoom = GetJailRoomPosition();
        Vector3 jailPos = RoomToWorldPos(jailRoom);
        jailMarker = new GameObject("JailMarker");
        float half = DungeonCellSize * 0.5f;
        CreateJailWall(jailMarker.transform, jailPos + new Vector3(0, 1.5f, -half), new Vector3(DungeonCellSize, 3f, 0.2f));
        CreateJailWall(jailMarker.transform, jailPos + new Vector3(0, 1.5f, half), new Vector3(DungeonCellSize, 3f, 0.2f));
        CreateJailWall(jailMarker.transform, jailPos + new Vector3(-half, 1.5f, 0), new Vector3(0.2f, 3f, DungeonCellSize));
        CreateJailWall(jailMarker.transform, jailPos + new Vector3(half, 1.5f, 0), new Vector3(0.2f, 3f, DungeonCellSize));

        // Escape markers at escape rooms (hole.fbx model)
        var escapeRooms = GetEscapeRoomPositions();
        foreach (var ep in escapeRooms)
        {
            Vector3 epPos = RoomToWorldPos(ep);
            GameObject marker;
            if (prefabHole != null)
            {
                marker = Instantiate(prefabHole, new Vector3(epPos.x, 0f, epPos.z), Quaternion.identity);
                marker.name = "EscapePoint";
                // Remove colliders from the model
                foreach (var col in marker.GetComponentsInChildren<Collider>())
                    Destroy(col);
            }
            else
            {
                // Fallback if hole.fbx not found
                marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "EscapePoint";
                marker.transform.position = new Vector3(epPos.x, 0.3f, epPos.z);
                marker.transform.localScale = new Vector3(DungeonCellSize * 0.8f, 0.3f, DungeonCellSize * 0.8f);
                var r = marker.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = CreateMat(ColorEscape);
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }
            escapeMarkers.Add(marker);
        }
    }

    private Vector2Int GetJailRoomPosition()
    {
        // Closest room to grid center
        int centerX = DungeonGridW / 2, centerZ = DungeonGridH / 2;
        int bestIdx = 0, bestDist = int.MaxValue;
        for (int i = 0; i < roomPositions.Count; i++)
        {
            int d = Mathf.Abs(roomPositions[i].x - centerX) + Mathf.Abs(roomPositions[i].y - centerZ);
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return roomPositions.Count > 0 ? roomPositions[bestIdx] : new Vector2Int(centerX, centerZ);
    }

    private List<Vector2Int> GetEscapeRoomPositions()
    {
        // Rooms farthest from jail = escape points (up to 2)
        var jail = GetJailRoomPosition();
        var sorted = new List<Vector2Int>(roomPositions);
        sorted.Sort((a, b) =>
        {
            int da = Mathf.Abs(a.x - jail.x) + Mathf.Abs(a.y - jail.y);
            int db = Mathf.Abs(b.x - jail.x) + Mathf.Abs(b.y - jail.y);
            return db.CompareTo(da); // Farthest first
        });
        var result = new List<Vector2Int>();
        for (int i = 0; i < Mathf.Min(2, sorted.Count); i++)
        {
            if (sorted[i] != jail) result.Add(sorted[i]);
        }
        return result;
    }

    private Vector3 RoomToWorldPos(Vector2Int room)
    {
        return new Vector3(room.x * DungeonCellSize + DungeonCellSize * 0.5f, 0f,
                           room.y * DungeonCellSize + DungeonCellSize * 0.5f);
    }

    private bool IsDungeonWalkable(Vector3 pos)
    {
        int cx = Mathf.FloorToInt(pos.x / DungeonCellSize);
        int cz = Mathf.FloorToInt(pos.z / DungeonCellSize);
        if (cx < 0 || cx >= DungeonGridW || cz < 0 || cz >= DungeonGridH) return false;
        return dungeonGrid[cx, cz] != CellType.Wall;
    }

    // ═══════════════════════════════════════════
    //  ROLE ASSIGNMENT
    // ═══════════════════════════════════════════

    private void AssignTeams()
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null) return;

        var memberIds = new List<string>();
        foreach (var m in lobby.Members)
        {
            if (m?.ProductId != null && m.ProductId.IsValid())
                memberIds.Add(m.ProductId.ToString());
        }
        memberIds.Sort(StringComparer.Ordinal);

        var rng = new System.Random(gameSeed);
        for (int i = memberIds.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var tmp = memberIds[i];
            memberIds[i] = memberIds[j];
            memberIds[j] = tmp;
        }

        // Team A = smaller group (police in odd rounds), Team B = larger group (thief in odd rounds)
        int teamACount = memberIds.Count >= 7 ? 2 : 1;

        teamAMembers.Clear();
        teamBMembers.Clear();

        for (int i = 0; i < memberIds.Count; i++)
        {
            if (i < teamACount)
                teamAMembers.Add(memberIds[i]);
            else
                teamBMembers.Add(memberIds[i]);
        }
    }

    private void AssignRolesForRound()
    {
        // Odd rounds (1,3): Team A = Police, Team B = Thief
        // Even rounds (2): Team A = Thief, Team B = Police
        bool teamAIsPolice = (currentRound % 2 == 1);

        playerRoles.Clear();
        thiefStates.Clear();

        var policeMembers = teamAIsPolice ? teamAMembers : teamBMembers;
        var thiefMembers = teamAIsPolice ? teamBMembers : teamAMembers;

        var policeIds = new List<string>(policeMembers);
        var thiefIds = new List<string>(thiefMembers);

        foreach (var id in policeIds)
            playerRoles[id] = PlayerRole.Police;
        foreach (var id in thiefIds)
        {
            playerRoles[id] = PlayerRole.Thief;
            thiefStates[id] = ThiefState.Free;
        }

        // Set local role
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && localId.IsValid())
        {
            string localStr = localId.ToString();
            localRole = playerRoles.ContainsKey(localStr) ? playerRoles[localStr] : PlayerRole.Thief;
            if (localRole == PlayerRole.Thief)
                localThiefState = ThiefState.Free;
        }

        // Host broadcasts roles via P2P (immediate) and queues lobby write
        if (isHosting)
        {
            string rolesJson = BuildRolesJson(policeIds, thiefIds);
            string teamsJson = (currentRound <= 1) ? BuildTeamsJson() : null;

            // P2P broadcast for immediate sync (no ModifyLobby here to avoid TooManyRequests)
            if (teamsJson != null)
            {
                byte[] teamBytes = Encoding.UTF8.GetBytes(teamsJson);
                byte[] teamData = new byte[1 + teamBytes.Length];
                teamData[0] = (byte)'T';
                Buffer.BlockCopy(teamBytes, 0, teamData, 1, teamBytes.Length);
                SendToAll(new ArraySegment<byte>(teamData), PacketReliability.ReliableOrdered);
            }

            byte[] jsonBytes = Encoding.UTF8.GetBytes(rolesJson);
            byte[] data = new byte[1 + jsonBytes.Length];
            data[0] = (byte)'O';
            Buffer.BlockCopy(jsonBytes, 0, data, 1, jsonBytes.Length);
            SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);

            // Deferred lobby write (roles+teams) - delayed to avoid rate limit
            pendingRolesJson = rolesJson;
            pendingTeamsJson = teamsJson;
            pendingLobbyWriteTime = Time.time + 1.5f; // write after 1.5s
        }

        SpawnPlayerAtRole();
        UpdatePlayerVisual(localPlayer, localRole, localRole == PlayerRole.Thief ? localThiefState : ThiefState.Free);
    }

    private string BuildRolesJson(List<string> police, List<string> thieves)
    {
        // Simple manual JSON to avoid dependency
        var sb = new StringBuilder();
        sb.Append("{\"police\":[");
        for (int i = 0; i < police.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(police[i]).Append("\"");
        }
        sb.Append("],\"thief\":[");
        for (int i = 0; i < thieves.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(thieves[i]).Append("\"");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void ParseRolesJson(string json)
    {
        playerRoles.Clear();
        thiefStates.Clear();

        // Simple parser for {"police":["id1","id2"],"thief":["id3",...]}
        var policeList = ExtractJsonArray(json, "police");
        var thiefList = ExtractJsonArray(json, "thief");

        foreach (var id in policeList)
            playerRoles[id] = PlayerRole.Police;
        foreach (var id in thiefList)
        {
            playerRoles[id] = PlayerRole.Thief;
            thiefStates[id] = ThiefState.Free;
        }

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && localId.IsValid())
        {
            string localStr = localId.ToString();
            localRole = playerRoles.ContainsKey(localStr) ? playerRoles[localStr] : PlayerRole.Thief;
            if (localRole == PlayerRole.Thief && !thiefStates.ContainsKey(localStr))
                localThiefState = ThiefState.Free;
        }

        SpawnPlayerAtRole();
        UpdatePlayerVisual(localPlayer, localRole, localRole == PlayerRole.Thief ? localThiefState : ThiefState.Free);

        // Update remote player models and visuals
        var remoteKeys = new List<string>(remotePlayers.Keys);
        foreach (var key in remoteKeys)
        {
            if (playerRoles.TryGetValue(key, out PlayerRole role))
            {
                // Swap model to match role
                GameObject prefab = role == PlayerRole.Police ? policePrefab : thiefPrefab;
                GameObject old = remotePlayers[key];
                Vector3 pos = old != null ? old.transform.position : Vector3.zero;
                if (old != null) Destroy(old);
                GameObject newRemote = CreateCharacterModel(prefab, "CRRemote_" + key);
                newRemote.transform.position = pos;
                remotePlayers[key] = newRemote;

                ThiefState ts = ThiefState.Free;
                if (role == PlayerRole.Thief)
                    thiefStates.TryGetValue(key, out ts);
                UpdatePlayerVisual(newRemote, role, ts);
            }
        }
    }

    private List<string> ExtractJsonArray(string json, string key)
    {
        var result = new List<string>();
        string search = "\"" + key + "\":[";
        int start = json.IndexOf(search, StringComparison.Ordinal);
        if (start < 0) return result;
        start += search.Length;
        int end = json.IndexOf(']', start);
        if (end < 0) return result;

        string content = json.Substring(start, end - start);
        // Extract quoted strings
        int i = 0;
        while (i < content.Length)
        {
            int q1 = content.IndexOf('"', i);
            if (q1 < 0) break;
            int q2 = content.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            result.Add(content.Substring(q1 + 1, q2 - q1 - 1));
            i = q2 + 1;
        }
        return result;
    }

    private string BuildTeamsJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"a\":[");
        for (int i = 0; i < teamAMembers.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(teamAMembers[i]).Append("\"");
        }
        sb.Append("],\"b\":[");
        for (int i = 0; i < teamBMembers.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(teamBMembers[i]).Append("\"");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void ParseTeamsJson(string json)
    {
        teamAMembers.Clear();
        teamBMembers.Clear();
        teamAMembers.AddRange(ExtractJsonArray(json, "a"));
        teamBMembers.AddRange(ExtractJsonArray(json, "b"));
    }

    private string GetLocalTeam()
    {
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null) return "";
        string localStr = localId.ToString();
        if (teamAMembers.Contains(localStr)) return "A";
        if (teamBMembers.Contains(localStr)) return "B";
        return "";
    }

    private void SpawnPlayerAtRole()
    {
        if (localPlayer == null) return;

        // Swap model to match role
        GameObject prefab = localRole == PlayerRole.Police ? policePrefab : thiefPrefab;
        SwapCharacterModel(ref localPlayer, prefab, "CRLocalPlayer");

        if (roomPositions.Count >= 2)
        {
            var jailRoom = GetJailRoomPosition();
            if (localRole == PlayerRole.Police)
            {
                // Police spawn at jail room (center)
                localPlayer.transform.position = RoomToWorldPos(jailRoom);
            }
            else
            {
                // Thieves spawn at a room that is NOT jail and NOT an escape point
                // (spawning at escape point causes immediate escape detection)
                var escapeRooms = GetEscapeRoomPositions();
                Vector2Int spawnRoom = roomPositions[0]; // fallback
                foreach (var room in roomPositions)
                {
                    if (room == jailRoom) continue;
                    if (escapeRooms.Contains(room)) continue;
                    spawnRoom = room;
                    break;
                }
                localPlayer.transform.position = RoomToWorldPos(spawnRoom);
            }
        }
        else
        {
            // Fallback
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, localRole == PlayerRole.Police ? 56f : 8f);
        }
    }

    private void UpdatePlayerVisual(GameObject player, PlayerRole role, ThiefState state)
    {
        if (player == null) return;

        float scale;
        if (role == PlayerRole.Police)
            scale = 1.3f;
        else
            scale = (state == ThiefState.Jailed) ? 0.8f : 1.0f;

        player.transform.localScale = new Vector3(scale, scale, scale);
    }

    // ═══════════════════════════════════════════
    //  GAME MECHANICS
    // ═══════════════════════════════════════════

    private void UpdateTimer()
    {
        if (!gameStarted || gameEnded) return;

        if (isHosting)
        {
            // Host is authoritative for time
            remainingTime = GameDuration - (Time.time - gameStartTime);
            if (remainingTime <= 0f)
            {
                remainingTime = 0f;
                EndRound();
                return;
            }

            // Broadcast timer every 1 second
            if (Time.time - lastTimerSendTime >= 1f)
            {
                lastTimerSendTime = Time.time;
                byte[] data = new byte[5];
                data[0] = (byte)'G';
                Buffer.BlockCopy(BitConverter.GetBytes(remainingTime), 0, data, 1, 4);
                SendToAll(new ArraySegment<byte>(data), PacketReliability.UnreliableUnordered);
            }
        }
        // Non-host: remainingTime is set by received 'G' packets only
    }

    private void UpdateGameMechanics()
    {
        if (!gameStarted || gameEnded || localPlayer == null) return;

        float elapsed = Time.time - gameStartTime;
        Vector3 localPos = localPlayer.transform.position;
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid()) return;
        string localStr = localId.ToString();

        // ── Police Logic ──
        if (localRole == PlayerRole.Police && elapsed >= PoliceDelay)
        {
            // Check tag against free thieves
            foreach (var kvp in remotePlayers)
            {
                if (kvp.Value == null) continue;
                if (!playerRoles.TryGetValue(kvp.Key, out PlayerRole peerRole)) continue;
                if (peerRole != PlayerRole.Thief) continue;
                if (!thiefStates.TryGetValue(kvp.Key, out ThiefState ts) || ts != ThiefState.Free) continue;

                float dist = Vector3.Distance(
                    new Vector3(localPos.x, 0, localPos.z),
                    new Vector3(kvp.Value.transform.position.x, 0, kvp.Value.transform.position.z));

                if (dist <= TagRadius)
                {
                    ArrestThief(kvp.Key);
                    BroadcastArrest(kvp.Key);
                }
            }
        }

        // ── Thief Logic ──
        // Thieves also have a grace period before escape/rescue detection activates
        if (localRole == PlayerRole.Thief && localThiefState == ThiefState.Free && elapsed >= PoliceDelay)
        {
            // Check escape (dynamic escape rooms from dungeon)
            var escapeRooms = GetEscapeRoomPositions();
            foreach (var ep in escapeRooms)
            {
                Vector3 epWorld = RoomToWorldPos(ep);
                float dist = Vector3.Distance(
                    new Vector3(localPos.x, 0, localPos.z), new Vector3(epWorld.x, 0, epWorld.z));

                if (dist <= EscapeRadius)
                {
                    localThiefState = ThiefState.Escaped;
                    thiefStates[localStr] = ThiefState.Escaped;
                    UpdatePlayerVisual(localPlayer, localRole, localThiefState);
                    BroadcastEscape();
                    CheckGameEnd();
                    return;
                }
            }

            // Check rescue (free thief near jail room → rescue all jailed)
            Vector3 jailWorld = RoomToWorldPos(GetJailRoomPosition());
            float jailDist = Vector3.Distance(
                new Vector3(localPos.x, 0, localPos.z), new Vector3(jailWorld.x, 0, jailWorld.z));

            if (jailDist <= RescueRadius)
            {
                bool anyJailed = false;
                foreach (var kvp in thiefStates)
                    if (kvp.Value == ThiefState.Jailed) { anyJailed = true; break; }

                if (anyJailed)
                {
                    RescueAllThieves();
                    BroadcastRescue();
                }
            }
        }
    }

    private void ArrestThief(string peerId)
    {
        thiefStates[peerId] = ThiefState.Jailed;

        // Move jailed player to jail
        if (remotePlayers.TryGetValue(peerId, out GameObject remote))
        {
            remote.transform.position = RoomToWorldPos(GetJailRoomPosition());
            UpdatePlayerVisual(remote, PlayerRole.Thief, ThiefState.Jailed);
        }

        // If it's local player being arrested (via received packet)
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && peerId == localId.ToString())
        {
            localThiefState = ThiefState.Jailed;
            localPlayer.transform.position = RoomToWorldPos(GetJailRoomPosition());
            UpdatePlayerVisual(localPlayer, localRole, localThiefState);
        }

        CheckGameEnd();
    }

    private void RescueAllThieves()
    {
        var localId = EOSManager.Instance.GetProductUserId();
        string localStr = localId != null ? localId.ToString() : "";

        // Rescued thieves respawn at the farthest room from jail
        var escapeRooms = GetEscapeRoomPositions();
        Vector3 rescuePos = escapeRooms.Count > 0 ? RoomToWorldPos(escapeRooms[0]) : new Vector3(GridSize * 0.5f, 0f, 8f);

        var keys = new List<string>(thiefStates.Keys);
        foreach (var key in keys)
        {
            if (thiefStates[key] == ThiefState.Jailed)
            {
                thiefStates[key] = ThiefState.Free;

                if (key == localStr)
                {
                    localThiefState = ThiefState.Free;
                    localPlayer.transform.position = rescuePos;
                    UpdatePlayerVisual(localPlayer, localRole, localThiefState);
                }
                else if (remotePlayers.TryGetValue(key, out GameObject remote))
                {
                    remote.transform.position = rescuePos;
                    UpdatePlayerVisual(remote, PlayerRole.Thief, ThiefState.Free);
                }
            }
        }
    }

    private void CheckGameEnd()
    {
        // Only the host decides when a round ends
        if (!isHosting || gameEnded) return;

        // Check if all thieves are accounted for (none free)
        bool allAccountedFor = true;
        foreach (var kvp in thiefStates)
        {
            if (kvp.Value == ThiefState.Free)
            {
                allAccountedFor = false;
                break;
            }
        }

        if (allAccountedFor && thiefStates.Count > 0)
            EndRound();
    }

    private void EndRound()
    {
        if (gameEnded) return;
        gameEnded = true;
        gameStarted = false;

        // Determine round winner
        bool policeWon = DidPoliceWin();
        bool teamAIsPolice = (currentRound % 2 == 1);

        if (policeWon)
        {
            if (teamAIsPolice) teamAWins++; else teamBWins++;
            lastRoundWinner = teamAIsPolice ? "A" : "B";
        }
        else
        {
            if (teamAIsPolice) teamBWins++; else teamAWins++;
            lastRoundWinner = teamAIsPolice ? "B" : "A";
        }

        // Check match end
        if (teamAWins >= WinsNeeded || teamBWins >= WinsNeeded)
        {
            matchEnded = true;
            if (isHosting) SetLobbyMatchState("MATCH_END");
        }
        else
        {
            isRoundTransition = true;
            roundTransitionStartTime = Time.time;
            if (isHosting) SetLobbyMatchState("ROUND_END");
        }
    }

    private void SetLobbyMatchState(string state)
    {
        // Set locally first (authoritative on host)
        gameState = state;

        var update = BuildLobbyUpdate();
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = state, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeScoreA, ValueType = AttributeType.String, AsString = teamAWins.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeScoreB, ValueType = AttributeType.String, AsString = teamBWins.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeLastWinner, ValueType = AttributeType.String, AsString = lastRoundWinner, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        lobbyManager.ModifyLobby(update, r =>
        {
            if (r != Result.Success) Debug.LogWarning("SetLobbyMatchState failed: " + r);
        });

        // Broadcast via P2P for immediate sync
        BroadcastGameState(state);
    }

    private void UpdateDeferredLobbyWrite()
    {
        if (pendingLobbyWriteTime < 0f || Time.time < pendingLobbyWriteTime) return;
        pendingLobbyWriteTime = -1f;

        if (!isHosting || !IsInLobby()) return;

        var update = BuildLobbyUpdate();
        if (!string.IsNullOrEmpty(pendingRolesJson))
        {
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeRoles, ValueType = AttributeType.String,
                AsString = pendingRolesJson, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
        }
        if (!string.IsNullOrEmpty(pendingTeamsJson))
        {
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeTeams, ValueType = AttributeType.String,
                AsString = pendingTeamsJson, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
        }
        if (update.Attributes.Count > 0)
        {
            lobbyManager.ModifyLobby(update, r =>
            {
                if (r != Result.Success) Debug.LogWarning("Deferred lobby write failed: " + r);
            });
        }
        pendingRolesJson = null;
        pendingTeamsJson = null;
    }

    private void UpdateRoundTransition()
    {
        if (!isRoundTransition || !isHosting || matchEnded) return;
        if (Time.time - roundTransitionStartTime >= RoundTransitionDuration)
            StartNextRound();
    }

    private void StartNextRound()
    {
        isRoundTransition = false;
        currentRound++;
        gameSeed = UnityEngine.Random.Range(10000, 99999);

        // Reset round state
        gameStarted = false;
        gameEnded = false;
        remainingTime = GameDuration;
        localRole = PlayerRole.None;
        localThiefState = ThiefState.Free;
        playerRoles.Clear();
        thiefStates.Clear();

        if (dungeonRoot != null) { Destroy(dungeonRoot); dungeonRoot = null; }
        roomPositions.Clear();

        // Set locally IMMEDIATELY so state machine triggers on next frame
        gameState = "STARTED";

        // Host sets lobby state to start new round
        var update = BuildLobbyUpdate();
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = "STARTED", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeRound, ValueType = AttributeType.String, AsString = currentRound.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeSeed, ValueType = AttributeType.String, AsString = gameSeed.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        lobbyManager.ModifyLobby(update, r =>
        {
            if (r == Result.Success) statusMessage = "Round " + currentRound + " starting!";
            else Debug.LogWarning("StartNextRound ModifyLobby failed: " + r);
        });

        // Broadcast via P2P for immediate sync
        BroadcastGameState("STARTED");
    }

    private bool DidPoliceWin()
    {
        // Police win if all thieves are jailed (none free, none escaped)
        foreach (var kvp in thiefStates)
            if (kvp.Value == ThiefState.Free || kvp.Value == ThiefState.Escaped)
                return false;
        return thiefStates.Count > 0;
    }

    // ═══════════════════════════════════════════
    //  PLAYER MOVEMENT
    // ═══════════════════════════════════════════

    private void UpdateLocalMovement()
    {
        if (localPlayer == null) return;

        // Don't move if game hasn't started (but allow looking around)
        if (!gameStarted) return;

        // Police delay
        if (localRole == PlayerRole.Police && (Time.time - gameStartTime) < PoliceDelay) return;

        // Jailed thieves can't move
        if (localRole == PlayerRole.Thief && localThiefState == ThiefState.Jailed) return;

        // Escaped thieves can't move
        if (localRole == PlayerRole.Thief && localThiefState == ThiefState.Escaped) return;

        float speed = localRole == PlayerRole.Police ? 13f : 12f;
        float moveX = Input.GetAxisRaw("Horizontal");
        float moveZ = Input.GetAxisRaw("Vertical");
        Vector3 dir = new Vector3(moveX, 0f, moveZ).normalized;
        Vector3 delta = dir * speed * Time.deltaTime;
        Vector3 newPos = localPlayer.transform.position + delta;

        // Face movement direction
        if (dir.sqrMagnitude > 0.01f)
            localPlayer.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        // Clamp to grid
        newPos.x = Mathf.Clamp(newPos.x, 0.5f, GridSize - 0.5f);
        newPos.z = Mathf.Clamp(newPos.z, 0.5f, GridSize - 0.5f);
        newPos.y = 0f;

        // Dungeon grid collision
        if (IsDungeonWalkable(newPos))
            localPlayer.transform.position = newPos;
        else
        {
            // Try sliding along axes
            Vector3 slideX = new Vector3(newPos.x, 0f, localPlayer.transform.position.z);
            Vector3 slideZ = new Vector3(localPlayer.transform.position.x, 0f, newPos.z);

            if (IsDungeonWalkable(slideX))
                localPlayer.transform.position = slideX;
            else if (IsDungeonWalkable(slideZ))
                localPlayer.transform.position = slideZ;
        }
    }

    private void UpdateCamera()
    {
        if (localPlayer == null) return;
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 target = localPlayer.transform.position + cameraOffset;
        cam.transform.position = Vector3.Lerp(cam.transform.position, target, Time.deltaTime * 5f);
        cam.transform.LookAt(localPlayer.transform.position);
    }

    // ═══════════════════════════════════════════
    //  REMOTE PLAYERS
    // ═══════════════════════════════════════════

    private void UpdateRemotePlayer(string peerId, Vector3 position)
    {
        if (!remotePlayers.TryGetValue(peerId, out GameObject remote))
        {
            // Choose model based on role
            PlayerRole role = PlayerRole.Thief;
            if (playerRoles.TryGetValue(peerId, out PlayerRole pr)) role = pr;
            GameObject prefab = role == PlayerRole.Police ? policePrefab : thiefPrefab;
            remote = CreateCharacterModel(prefab, "CRRemote_" + peerId);

            ThiefState ts = ThiefState.Free;
            if (role == PlayerRole.Thief) thiefStates.TryGetValue(peerId, out ts);
            UpdatePlayerVisual(remote, role, ts);

            remotePlayers.Add(peerId, remote);
        }

        if (!remoteStates.TryGetValue(peerId, out RemoteMotionState state))
        {
            state = new RemoteMotionState { Target = position, LastRecvTime = Time.time, Velocity = Vector3.zero };
        }
        else
        {
            float dt = Mathf.Max(0.0001f, Time.time - state.LastRecvTime);
            state.Velocity = (position - state.Target) / dt;
            state.LastRecvTime = Time.time;
            state.Target = position;
        }
        remoteStates[peerId] = state;
    }

    private void UpdateRemotePlayers()
    {
        const float smooth = 12f;
        const float maxPredict = 0.15f;
        const float snapDist = 2.5f;

        foreach (var kvp in remotePlayers)
        {
            var remote = kvp.Value;
            if (remote == null) continue;
            if (!remoteStates.TryGetValue(kvp.Key, out RemoteMotionState state)) continue;

            // Don't interpolate jailed players
            if (thiefStates.TryGetValue(kvp.Key, out ThiefState ts) && ts == ThiefState.Jailed)
                continue;

            float age = Mathf.Clamp(Time.time - state.LastRecvTime, 0f, maxPredict);
            Vector3 predicted = state.Target + state.Velocity * age;
            Vector3 current = remote.transform.position;

            if (Vector3.Distance(current, predicted) > snapDist)
            {
                remote.transform.position = predicted;
                continue;
            }

            float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
            Vector3 newPos = Vector3.Lerp(current, predicted, t);

            // Face movement direction
            Vector3 moveDir = newPos - current;
            moveDir.y = 0f;
            if (moveDir.sqrMagnitude > 0.0001f)
                remote.transform.rotation = Quaternion.LookRotation(moveDir, Vector3.up);

            remote.transform.position = newPos;
        }
    }

    // ═══════════════════════════════════════════
    //  P2P NETWORKING
    // ═══════════════════════════════════════════

    private void SendLocalPositionIfNeeded()
    {
        if (!isLoggedIn || !IsInLobby() || !gameStarted) return;
        if (Time.time - lastSendTime < sendInterval) return;
        lastSendTime = Time.time;

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid()) return;

        var members = lobbyManager.GetCurrentLobby().Members;
        if (members == null) return;

        foreach (var member in members)
        {
            if (member?.ProductId == null || !member.ProductId.IsValid()) continue;
            if (member.ProductId.ToString() == localId.ToString()) continue;
            SendPosition(member.ProductId, localPlayer.transform.position);
        }
    }

    private void SendPosition(ProductUserId remoteUserId, Vector3 pos)
    {
        if (p2p == null) return;
        byte[] data = new byte[13];
        data[0] = (byte)'P';
        Buffer.BlockCopy(BitConverter.GetBytes(pos.x), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.y), 0, data, 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.z), 0, data, 9, 4);

        var options = new SendPacketOptions
        {
            LocalUserId = EOSManager.Instance.GetProductUserId(),
            RemoteUserId = remoteUserId,
            SocketId = new SocketId { SocketName = P2PSocketName },
            Channel = 0,
            Reliability = PacketReliability.UnreliableUnordered,
            AllowDelayedDelivery = true,
            Data = new ArraySegment<byte>(data)
        };
        p2p.SendPacket(ref options);
    }

    private void BroadcastArrest(string targetPeerId)
    {
        byte[] idBytes = Encoding.UTF8.GetBytes(targetPeerId);
        byte[] data = new byte[1 + idBytes.Length];
        data[0] = (byte)'A';
        Buffer.BlockCopy(idBytes, 0, data, 1, idBytes.Length);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void BroadcastRescue()
    {
        byte[] data = new byte[1];
        data[0] = (byte)'R';
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void BroadcastEscape()
    {
        byte[] data = new byte[1];
        data[0] = (byte)'E';
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    /// <summary>
    /// Host broadcasts game state via P2P for immediate sync (lobby attributes can be delayed).
    /// Format: 'S' + state|round|scoreA|scoreB|seed|lastWinner
    /// </summary>
    private void BroadcastGameState(string state)
    {
        string payload = state + "|" + currentRound + "|" + teamAWins + "|" + teamBWins + "|" + gameSeed + "|" + lastRoundWinner;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(payload);
        byte[] data = new byte[1 + jsonBytes.Length];
        data[0] = (byte)'S';
        Buffer.BlockCopy(jsonBytes, 0, data, 1, jsonBytes.Length);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
    }

    // Time when P2P state was last received; suppresses stale lobby reads
    private float lastP2PStateTime = -10f;
    private const float P2PStateSuppressDuration = 3f;

    private void HandleGameStatePacket(string payload)
    {
        if (isHosting) return; // Host is authoritative, ignore P2P state from others

        // Parse: state|round|scoreA|scoreB|seed|lastWinner
        string[] parts = payload.Split('|');
        if (parts.Length < 5) return;

        string newState = parts[0];
        if (int.TryParse(parts[1], out int rnd)) currentRound = rnd;
        if (int.TryParse(parts[2], out int sa)) teamAWins = sa;
        if (int.TryParse(parts[3], out int sb)) teamBWins = sb;
        if (int.TryParse(parts[4], out int seed)) gameSeed = seed;
        if (parts.Length >= 6) lastRoundWinner = parts[5];

        // Apply state transition immediately
        gameState = newState;
        lastP2PStateTime = Time.time;
    }

    private void SendToAll(ArraySegment<byte> data, PacketReliability reliability)
    {
        if (p2p == null) return;
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid()) return;

        var members = lobbyManager.GetCurrentLobby().Members;
        if (members == null) return;

        foreach (var member in members)
        {
            if (member?.ProductId == null || !member.ProductId.IsValid()) continue;
            if (member.ProductId.ToString() == localId.ToString()) continue;

            var options = new SendPacketOptions
            {
                LocalUserId = localId,
                RemoteUserId = member.ProductId,
                SocketId = new SocketId { SocketName = P2PSocketName },
                Channel = 1,
                Reliability = reliability,
                AllowDelayedDelivery = true,
                Data = data
            };
            p2p.SendPacket(ref options);
        }
    }

    private void ReceivePackets()
    {
        if (!isLoggedIn || p2p == null) return;
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid()) return;

        int safety = 0;
        while (safety < 30)
        {
            var sizeOpts = new GetNextReceivedPacketSizeOptions
            {
                LocalUserId = localId,
                RequestedChannel = null
            };
            p2p.GetNextReceivedPacketSize(ref sizeOpts, out uint nextSize);
            if (nextSize == 0) return;

            byte[] buffer = new byte[nextSize];
            var seg = new ArraySegment<byte>(buffer);
            ProductUserId peerId = null;
            SocketId socketId = default;
            var recvOpts = new ReceivePacketOptions
            {
                LocalUserId = localId,
                MaxDataSizeBytes = nextSize,
                RequestedChannel = null
            };
            Result result = p2p.ReceivePacket(ref recvOpts, ref peerId, ref socketId, out byte channel, seg, out uint bytesWritten);
            if (result != Result.Success) return;

            if (peerId == null || !peerId.IsValid() || socketId.SocketName != P2PSocketName)
            { safety++; continue; }
            if (bytesWritten == 0) { safety++; continue; }

            byte type = buffer[0];

            if (type == (byte)'P' && bytesWritten >= 13)
            {
                float x = BitConverter.ToSingle(buffer, 1);
                float y = BitConverter.ToSingle(buffer, 5);
                float z = BitConverter.ToSingle(buffer, 9);
                UpdateRemotePlayer(peerId.ToString(), new Vector3(x, y, z));
            }
            else if (type == (byte)'A' && bytesWritten > 1)
            {
                string targetId = Encoding.UTF8.GetString(buffer, 1, (int)bytesWritten - 1);
                ArrestThief(targetId);
            }
            else if (type == (byte)'R')
            {
                RescueAllThieves();
            }
            else if (type == (byte)'E')
            {
                string escaperId = peerId.ToString();
                if (thiefStates.ContainsKey(escaperId))
                {
                    thiefStates[escaperId] = ThiefState.Escaped;
                    if (remotePlayers.TryGetValue(escaperId, out GameObject remote))
                        UpdatePlayerVisual(remote, PlayerRole.Thief, ThiefState.Escaped);
                    CheckGameEnd();
                }
            }
            else if (type == (byte)'O' && bytesWritten > 1)
            {
                string json = Encoding.UTF8.GetString(buffer, 1, (int)bytesWritten - 1);
                ParseRolesJson(json);
            }
            else if (type == (byte)'T' && bytesWritten > 1)
            {
                string json = Encoding.UTF8.GetString(buffer, 1, (int)bytesWritten - 1);
                ParseTeamsJson(json);
            }
            else if (type == (byte)'G' && bytesWritten >= 5)
            {
                remainingTime = BitConverter.ToSingle(buffer, 1);
            }
            else if (type == (byte)'S' && bytesWritten > 1)
            {
                string payload = Encoding.UTF8.GetString(buffer, 1, (int)bytesWritten - 1);
                HandleGameStatePacket(payload);
            }

            safety++;
        }
    }

    private void OnIncomingConnectionRequest(ref OnIncomingConnectionRequestInfo data)
    {
        if (p2p == null) return;
        if (!data.SocketId.HasValue || data.SocketId.Value.SocketName != P2PSocketName) return;

        var acceptOpts = new AcceptConnectionOptions
        {
            LocalUserId = EOSManager.Instance.GetProductUserId(),
            RemoteUserId = data.RemoteUserId,
            SocketId = data.SocketId.Value
        };
        p2p.AcceptConnection(ref acceptOpts);
    }

    // ═══════════════════════════════════════════
    //  EOS LOGIN / LOBBY
    // ═══════════════════════════════════════════

    private void TryResolveManagers()
    {
        if (lobbyManager == null) lobbyManager = EOSManager.Instance.GetOrCreateManager<EOSLobbyManager>();
        if (sessionsManager == null) sessionsManager = EOSManager.Instance.GetOrCreateManager<EOSSessionsManager>();
        if (p2p == null) p2p = EOSManager.Instance.GetEOSP2PInterface();

        if (p2p != null && connectionNotifyId == 0 && isLoggedIn)
        {
            var opts = new AddNotifyPeerConnectionRequestOptions
            {
                LocalUserId = EOSManager.Instance.GetProductUserId(),
                SocketId = new SocketId { SocketName = P2PSocketName }
            };
            connectionNotifyId = p2p.AddNotifyPeerConnectionRequest(ref opts, null, OnIncomingConnectionRequest);
        }
    }

    private GameObject CreateCharacterModel(GameObject prefab, string name)
    {
        GameObject go;
        if (prefab != null)
        {
            go = Instantiate(prefab);
            go.name = name;
            // Add a BoxCollider for obstacle collision checks (FBX models don't have one by default)
            if (go.GetComponent<Collider>() == null)
                go.AddComponent<BoxCollider>();
        }
        else
        {
            // Fallback to cube if FBX not loaded
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
        }
        return go;
    }

    private void SwapCharacterModel(ref GameObject current, GameObject prefab, string name)
    {
        Vector3 pos = current != null ? current.transform.position : Vector3.zero;
        Quaternion rot = current != null ? current.transform.rotation : Quaternion.identity;
        if (current != null) Destroy(current);

        current = CreateCharacterModel(prefab, name);
        current.transform.position = pos;
        current.transform.rotation = rot;
    }

    private void SetCharacterColor(GameObject player, Color c)
    {
        if (player == null) return;
        var renderers = player.GetComponentsInChildren<Renderer>();
        var mat = CreateMat(c);
        foreach (var r in renderers)
            r.sharedMaterial = mat;
    }

    private Material CreateMat(Color c)
    {
        switch (shaderMode)
        {
            case ShaderMode.Jelly: return CreateJellyMat(c);
            case ShaderMode.Clay:  return CreateClayMat(c);
            default:
                Shader s = Shader.Find("Standard");
                if (s == null) s = Shader.Find("Legacy Shaders/Diffuse");
                if (s == null) s = Shader.Find("Unlit/Color");
                var m = new Material(s);
                m.color = c;
                return m;
        }
    }

    private Material CreateJellyMat(Color c)
    {
        Shader s = Shader.Find("Custom/Jelly");
        if (s == null)
        {
            Debug.LogWarning("Custom/Jelly shader not found, falling back to Standard");
            var fallback = new Material(Shader.Find("Standard"));
            fallback.color = c;
            return fallback;
        }
        var m = new Material(s);
        m.SetColor("_JellyColor", c);
        m.SetFloat("_Alpha", Mathf.Clamp(c.a, 0.5f, 0.9f));

        Color.RGBToHSV(c, out float h, out float sat, out float val);
        Color sssCol = Color.HSVToRGB(Mathf.Repeat(h - 0.05f, 1f), sat * 0.6f, Mathf.Min(val + 0.3f, 1f));
        m.SetColor("_SSSColor", sssCol);

        Color rimCol = Color.HSVToRGB(h, sat * 0.3f, Mathf.Min(val + 0.4f, 1f));
        m.SetColor("_RimColor", rimCol);

        return m;
    }

    private Material CreateClayMat(Color c)
    {
        Shader s = Shader.Find("Custom/Clay");
        if (s == null)
        {
            Debug.LogWarning("Custom/Clay shader not found, falling back to Standard");
            var fallback = new Material(Shader.Find("Standard"));
            fallback.color = c;
            return fallback;
        }
        var m = new Material(s);
        m.SetColor("_ClayColor", c);
        return m;
    }

    private void StartLogin()
    {
        statusMessage = "Logging in...";
        TryResolveManagers();

        var authInterface = EOSManager.Instance.GetEOSAuthInterface();
        if (authInterface == null) { statusMessage = "Auth interface unavailable"; return; }

        var options = new Epic.OnlineServices.Auth.LoginOptions
        {
            Credentials = new Epic.OnlineServices.Auth.Credentials
            {
                Type = Epic.OnlineServices.Auth.LoginCredentialType.Developer,
                Id = devAuthAddress,
                Token = devAuthCredential
            }
        };

        authInterface.Login(ref options, null, (ref Epic.OnlineServices.Auth.LoginCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                statusMessage = "Auth login failed: " + info.ResultCode;
                return;
            }
            statusMessage = "Auth login success";
            EOSManager.Instance.StartConnectLoginWithEpicAccount(info.LocalUserId, OnConnectLogin);
        });
    }

    private void OnConnectLogin(Epic.OnlineServices.Connect.LoginCallbackInfo info)
    {
        TryResolveManagers();
        if (info.ResultCode == Result.Success)
        {
            isLoggedIn = true;
            statusMessage = "Logged in";
            sessionsManager.OnLoggedIn();
            return;
        }
        if (info.ResultCode == Result.InvalidUser)
        {
            EOSManager.Instance.CreateConnectUserWithContinuanceToken(info.ContinuanceToken, createInfo =>
            {
                if (createInfo.ResultCode == Result.Success)
                {
                    isLoggedIn = true;
                    statusMessage = "Account created";
                    sessionsManager.OnLoggedIn();
                }
                else statusMessage = "Create user failed: " + createInfo.ResultCode;
            });
            return;
        }
        statusMessage = "Connect login failed: " + info.ResultCode;
    }

    private void Host()
    {
        if (!isLoggedIn) { statusMessage = "Login first"; return; }
        isHosting = true;
        gameSeed = UnityEngine.Random.Range(10000, 99999);
        ResetGameState();

        var lobby = new Lobby
        {
            BucketId = "COPSROBBERS",
            MaxNumLobbyMembers = (uint)MaxPlayers,
            LobbyPermissionLevel = Epic.OnlineServices.Lobby.LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = true
        };

        lobbyManager.CreateLobby(lobby, result =>
        {
            if (result != Result.Success) { statusMessage = "Create lobby failed: " + result; return; }
            SetReady(false);
            var update = BuildLobbyUpdate();
            lobbyManager.ModifyLobby(update, r =>
            {
                statusMessage = r == Result.Success ? "Lobby created (Code: " + lobbyCode + ")" : "Lobby update fail";
            });
        });
    }

    private Lobby BuildLobbyUpdate()
    {
        var current = lobbyManager.GetCurrentLobby();
        var update = new Lobby
        {
            BucketId = current.BucketId,
            LobbyPermissionLevel = current.LobbyPermissionLevel,
            AllowInvites = current.AllowInvites
        };

        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeCode, ValueType = AttributeType.String, AsString = lobbyCode, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = gameState, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });

        if (gameSeed != 0)
            update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeSeed, ValueType = AttributeType.String, AsString = gameSeed.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });

        return update;
    }

    private void SearchLobbies()
    {
        if (!isLoggedIn) { statusMessage = "Login first"; return; }
        isSearchingLobbies = true;
        lobbyManager.SearchByAttribute(LobbyAttributeCode, lobbyCode, result =>
        {
            isSearchingLobbies = false;
            if (result != Result.Success) { statusMessage = "Search failed: " + result; return; }
            lobbyResults.Clear();
            foreach (var kvp in lobbyManager.GetSearchResults())
                lobbyResults.Add(new LobbySearchEntry { Lobby = kvp.Key, Details = kvp.Value });
            statusMessage = "Found " + lobbyResults.Count + " lobbies";
        });
    }

    private void JoinLobby(LobbySearchEntry entry)
    {
        if (entry == null) return;
        lobbyManager.JoinLobby(entry.Lobby.Id, entry.Details, false, result =>
        {
            if (result != Result.Success) { statusMessage = "Join failed: " + result; return; }
            statusMessage = "Joined lobby";
            isHosting = false;
            ResetGameState();
            SetReady(false);
        });
    }

    private bool IsInLobby()
    {
        if (lobbyManager == null) return false;
        var lobby = lobbyManager.GetCurrentLobby();
        return lobby != null && lobby.IsValid();
    }

    private void SetReady(bool ready)
    {
        if (!IsInLobby()) return;
        isReady = ready;
        lobbyManager.SetMemberAttribute(new LobbyAttribute
        {
            Key = LobbyMemberReadyKey, ValueType = AttributeType.Boolean, AsBool = ready,
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        });
    }

    private void LeaveLobby()
    {
        if (!IsInLobby()) return;
        lobbyManager.LeaveLobby(r => { statusMessage = "Left lobby"; });
        isHosting = false;
        isReady = false;
        ResetGameState();
        foreach (var kvp in remotePlayers) { if (kvp.Value != null) Destroy(kvp.Value); }
        remotePlayers.Clear();
        remoteStates.Clear();
    }

    private string GetLobbyAttribute(string key)
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby == null) return "";
        foreach (var attr in lobby.Attributes)
            if (string.Equals(attr.Key, key, StringComparison.OrdinalIgnoreCase))
                return attr.AsString ?? "";
        return "";
    }

    private void ResetGameState()
    {
        gameStarted = false;
        gameEnded = false;
        gameState = "WAITING";
        remainingTime = GameDuration;
        cameraOffset = CameraOffsetLobby;
        startUpdateInProgress = false;
        localRole = PlayerRole.None;
        localThiefState = ThiefState.Free;
        playerRoles.Clear();
        thiefStates.Clear();

        // Match system reset
        currentRound = 0;
        teamAWins = 0;
        teamBWins = 0;
        teamAMembers.Clear();
        teamBMembers.Clear();
        matchInProgress = false;
        matchEnded = false;
        isRoundTransition = false;
        lastRoundWinner = "";
        pendingLobbyWriteTime = -1f;
        pendingRolesJson = null;
        pendingTeamsJson = null;

        // Destroy obstacles
        if (dungeonRoot != null) { Destroy(dungeonRoot); dungeonRoot = null; }
        roomPositions.Clear();

        // Reset player to default thief model
        if (localPlayer != null)
        {
            SwapCharacterModel(ref localPlayer, thiefPrefab, "CRLocalPlayer");
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, GridSize * 0.25f);
            localPlayer.transform.localScale = Vector3.one;
        }
    }

    private void UpdateLobbyState()
    {
        if (!IsInLobby()) { gameState = "WAITING"; gameStarted = false; return; }

        // ── Read lobby attributes ──
        // HOST is the authority for game state → never reads STATE from lobby cache.
        //   Host sets gameState locally (UpdateReadyState, SetLobbyMatchState, StartNextRound)
        //   and writes to lobby for others.
        // NON-HOST reads lobby only when P2P hasn't delivered yet (before match starts).
        //   Once match is in progress, P2P 'S' packets are authoritative.
        if (!isHosting)
        {
            // Non-host: read from lobby only before match starts or if P2P hasn't arrived recently
            bool lobbyReadSuppressed = Time.time - lastP2PStateTime < P2PStateSuppressDuration;
            if (!lobbyReadSuppressed)
            {
                string lobbyState = GetLobbyAttribute(LobbyAttributeState);
                if (!string.IsNullOrEmpty(lobbyState))
                    gameState = lobbyState;

                string roundStr = GetLobbyAttribute(LobbyAttributeRound);
                if (!string.IsNullOrEmpty(roundStr) && int.TryParse(roundStr, out int rnd))
                    currentRound = rnd;

                string scoreAStr = GetLobbyAttribute(LobbyAttributeScoreA);
                if (!string.IsNullOrEmpty(scoreAStr) && int.TryParse(scoreAStr, out int sa))
                    teamAWins = sa;
                string scoreBStr = GetLobbyAttribute(LobbyAttributeScoreB);
                if (!string.IsNullOrEmpty(scoreBStr) && int.TryParse(scoreBStr, out int sb))
                    teamBWins = sb;

                string winner = GetLobbyAttribute(LobbyAttributeLastWinner);
                if (!string.IsNullOrEmpty(winner))
                    lastRoundWinner = winner;
            }
        }
        // HOST: gameState is set locally, never read from lobby cache

        // Read teams from lobby (fallback, only if not yet populated)
        if (teamAMembers.Count == 0)
        {
            string teamsStr = GetLobbyAttribute(LobbyAttributeTeams);
            if (!string.IsNullOrEmpty(teamsStr))
                ParseTeamsJson(teamsStr);
        }

        // Round start
        if (!gameStarted && string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = true;
            gameEnded = false;
            isRoundTransition = false;
            gameStartTime = Time.time;
            remainingTime = GameDuration;

            // Seed: prefer value from P2P 'S' packet; only use lobby as fallback
            if (gameSeed == 0)
            {
                string seedStr = GetLobbyAttribute(LobbyAttributeSeed);
                if (!string.IsNullOrEmpty(seedStr) && int.TryParse(seedStr, out int parsed))
                    gameSeed = parsed;
            }

            if (currentRound == 0) currentRound = 1;

            cameraOffset = CameraOffsetGame;

            // Destroy old dungeon before generating new one
            if (dungeonRoot != null) { Destroy(dungeonRoot); dungeonRoot = null; }
            GenerateDungeon();

            if (currentRound <= 1)
                AssignTeams();
            AssignRolesForRound();

            matchInProgress = true;
            statusMessage = "Round " + currentRound + " | Role: " + localRole + " | Seed: " + gameSeed;
            return; // Don't process ROUND_END/MATCH_END on the same frame as start
        }

        // Round end (transition to next round)
        if (!isRoundTransition && !matchEnded && gameStarted && string.Equals(gameState, "ROUND_END", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = false;
            gameEnded = true;
            isRoundTransition = true;
            roundTransitionStartTime = Time.time;
        }

        // Match end
        if (!matchEnded && gameStarted && string.Equals(gameState, "MATCH_END", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = false;
            gameEnded = true;
            matchEnded = true;
        }

        // Lobby fallback for seed (non-host only, host sets locally)
        if (!isHosting)
        {
            string seedStr2 = GetLobbyAttribute(LobbyAttributeSeed);
            if (!string.IsNullOrEmpty(seedStr2) && int.TryParse(seedStr2, out int parsed2))
                gameSeed = parsed2;
        }

        // Check for roles from lobby attribute (for late joiners / reconnect)
        if (gameStarted && playerRoles.Count == 0)
        {
            string rolesStr = GetLobbyAttribute(LobbyAttributeRoles);
            if (!string.IsNullOrEmpty(rolesStr))
                ParseRolesJson(rolesStr);
        }
    }

    private void UpdateReadyState()
    {
        if (!IsInLobby() || !isHosting) return;
        if (matchInProgress && !matchEnded) return; // Match running & not ended → rounds are auto-managed
        if (!AreAllMembersReady() || string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase) || startUpdateInProgress) return;

        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null || lobby.Members.Count < 2) return;

        startUpdateInProgress = true;
        currentRound = 1;
        teamAWins = 0;
        teamBWins = 0;
        matchEnded = false;
        matchInProgress = false;
        isRoundTransition = false;
        lastRoundWinner = "";
        teamAMembers.Clear();
        teamBMembers.Clear();
        gameEnded = false;
        gameSeed = UnityEngine.Random.Range(10000, 99999);

        // Set locally IMMEDIATELY to prevent re-entry on next frame
        // (even if ModifyLobby fails, the game state machine will proceed)
        gameState = "STARTED";

        var update = BuildLobbyUpdate();
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = "STARTED", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeRound, ValueType = AttributeType.String, AsString = "1", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeScoreA, ValueType = AttributeType.String, AsString = "0", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeScoreB, ValueType = AttributeType.String, AsString = "0", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeLastWinner, ValueType = AttributeType.String, AsString = "", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        update.Attributes.Add(new LobbyAttribute { Key = LobbyAttributeSeed, ValueType = AttributeType.String, AsString = gameSeed.ToString(), Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public });
        lobbyManager.ModifyLobby(update, r =>
        {
            startUpdateInProgress = false;
            if (r == Result.Success) statusMessage = "Match started! Round 1";
            else Debug.LogWarning("ModifyLobby for match start failed: " + r);
        });

        // Broadcast via P2P for immediate sync (lobby attributes can be delayed)
        BroadcastGameState("STARTED");
    }

    private bool AreAllMembersReady()
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null || lobby.Members.Count == 0) return false;

        foreach (var m in lobby.Members)
        {
            if (m?.ProductId == null || !m.ProductId.IsValid()) return false;
            if (!m.MemberAttributes.TryGetValue(LobbyMemberReadyKey, out LobbyAttribute ra)) return false;
            if (ra.ValueType == AttributeType.Boolean) { if (ra.AsBool != true) return false; }
            else { if (!string.Equals(ra.AsString, "true", StringComparison.OrdinalIgnoreCase)) return false; }
        }
        return true;
    }

    // ═══════════════════════════════════════════
    //  GUI
    // ═══════════════════════════════════════════

    private void OnGUI()
    {
        // ── Left Panel: EOS Controls ──
        GUILayout.BeginArea(new Rect(10, 10, 380, 600), GUI.skin.box);
        var titleStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
        GUILayout.Label("<b>Cops & Robbers</b>", titleStyle);
        GUILayout.Label("64x64 Grid · Max 10 Players · Best of 3");

        GUILayout.Space(4);
        GUILayout.Label("Display Name");
        displayName = GUILayout.TextField(displayName, 24);
        GUILayout.Label("Dev Auth Address");
        devAuthAddress = GUILayout.TextField(devAuthAddress, 32);
        GUILayout.Label("Dev Auth Credential");
        devAuthCredential = GUILayout.TextField(devAuthCredential, 32);
        GUILayout.Label("Lobby Code");
        lobbyCode = GUILayout.TextField(lobbyCode, 16);

        GUILayout.Space(4);
        if (!isLoggedIn)
        {
            if (GUILayout.Button("Login")) StartLogin();
        }
        else
        {
            GUILayout.Label("Logged in: " + EOSManager.Instance.GetProductUserId());
        }

        GUILayout.Space(4);
        if (GUILayout.Button("Host")) Host();
        if (GUILayout.Button(isSearchingLobbies ? "Searching..." : "Search Lobbies")) { if (!isSearchingLobbies) SearchLobbies(); }
        if (GUILayout.Button("Leave Lobby")) LeaveLobby();

        if (IsInLobby())
        {
            if (GUILayout.Button(isReady ? "Unready" : "Ready")) SetReady(!isReady);
        }

        GUILayout.Space(4);
        GUILayout.Label("Status: " + statusMessage);

        // Lobby search results
        if (lobbyResults.Count > 0)
        {
            GUILayout.Label("--- Lobby Results ---");
            for (int i = 0; i < lobbyResults.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(lobbyResults[i].Lobby.Id, GUILayout.Width(220));
                if (GUILayout.Button("Join", GUILayout.Width(60))) JoinLobby(lobbyResults[i]);
                GUILayout.EndHorizontal();
            }
        }

        // Lobby info
        if (IsInLobby())
        {
            var lobby = lobbyManager.GetCurrentLobby();
            GUILayout.Space(4);
            GUILayout.Label("--- Lobby ---");
            GUILayout.Label("State: " + gameState + " | Ready: " + (isReady ? "Yes" : "No"));
            GUILayout.Label("Members: " + (lobby?.Members != null ? lobby.Members.Count : 0) + "/" + MaxPlayers);

            if (matchInProgress)
            {
                string localTeam = GetLocalTeam();
                int myWins = localTeam == "A" ? teamAWins : teamBWins;
                int enemyWins = localTeam == "A" ? teamBWins : teamAWins;
                GUILayout.Label("Round: " + currentRound + " | Score: " + myWins + "-" + enemyWins);
            }

            if (localRole != PlayerRole.None)
            {
                string roleStr = localRole == PlayerRole.Police ? "POLICE" : "THIEF";
                var roleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                roleStyle.normal.textColor = localRole == PlayerRole.Police ? Color.cyan : Color.red;
                GUILayout.Label("Your Role: " + roleStr, roleStyle);
            }
        }

        GUILayout.EndArea();

        // ── Right Panel: Game HUD ──
        if (gameStarted || isRoundTransition || matchEnded)
        {
            DrawGameHUD();
        }

        // ── Center: Round Transition Banner ──
        if (isRoundTransition && !matchEnded)
        {
            DrawRoundTransitionBanner();
        }

        // ── Center: Match Result Banner ──
        if (matchEnded)
        {
            DrawMatchResultBanner();
        }
    }

    private void DrawGameHUD()
    {
        GUILayout.BeginArea(new Rect(Screen.width - 320, 10, 310, 350), GUI.skin.box);

        // Round and Score header
        var hudTitle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUILayout.Label("ROUND " + currentRound + " / 3", hudTitle);

        // Score display
        string localTeam = GetLocalTeam();
        int myWins = localTeam == "A" ? teamAWins : teamBWins;
        int enemyWins = localTeam == "A" ? teamBWins : teamAWins;
        var scoreStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        scoreStyle.normal.textColor = Color.yellow;
        GUILayout.Label("Score: " + myWins + " - " + enemyWins + "  (First to " + WinsNeeded + ")", scoreStyle);

        // Current role indicator
        bool teamAIsPolice = (currentRound % 2 == 1);
        string myRoleThisRound = "";
        if (localTeam == "A") myRoleThisRound = teamAIsPolice ? "POLICE" : "THIEF";
        else if (localTeam == "B") myRoleThisRound = teamAIsPolice ? "THIEF" : "POLICE";
        var roleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        roleStyle.normal.textColor = localRole == PlayerRole.Police ? Color.cyan : Color.red;
        GUILayout.Label("You: " + myRoleThisRound, roleStyle);

        // Timer
        GUILayout.Space(4);
        var timerStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        timerStyle.normal.textColor = remainingTime <= 30f ? Color.red : Color.white;
        int min = (int)(remainingTime / 60);
        int sec = (int)(remainingTime % 60);
        GUILayout.Label(string.Format("{0}:{1:00}", min, sec), timerStyle);

        // Police delay warning
        if (localRole == PlayerRole.Police && !gameEnded && gameStarted)
        {
            float elapsed = Time.time - gameStartTime;
            if (elapsed < PoliceDelay)
            {
                var warnStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                warnStyle.normal.textColor = Color.yellow;
                GUILayout.Label("Wait " + Mathf.CeilToInt(PoliceDelay - elapsed) + "s...", warnStyle);
            }
        }

        GUILayout.Space(8);

        // Status of all players
        int freeThieves = 0, jailedThieves = 0, escapedThieves = 0, policeCount = 0;
        foreach (var kvp in playerRoles)
        {
            if (kvp.Value == PlayerRole.Police) policeCount++;
            else if (thiefStates.TryGetValue(kvp.Key, out ThiefState ts))
            {
                switch (ts)
                {
                    case ThiefState.Free: freeThieves++; break;
                    case ThiefState.Jailed: jailedThieves++; break;
                    case ThiefState.Escaped: escapedThieves++; break;
                }
            }
        }

        var blueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        blueStyle.normal.textColor = Color.cyan;
        GUILayout.Label("Police: " + policeCount, blueStyle);

        var redStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        redStyle.normal.textColor = Color.red;
        GUILayout.Label("Thieves (Free): " + freeThieves, redStyle);

        var grayStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        grayStyle.normal.textColor = Color.gray;
        GUILayout.Label("Thieves (Jailed): " + jailedThieves, grayStyle);

        var greenStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        greenStyle.normal.textColor = Color.green;
        GUILayout.Label("Thieves (Escaped): " + escapedThieves, greenStyle);

        GUILayout.EndArea();

        // Bottom instruction
        string instruction = "";
        if (localRole == PlayerRole.Police)
            instruction = "WASD to move · Catch all thieves!";
        else if (localThiefState == ThiefState.Free)
            instruction = "WASD to move · Escape or survive! · Rescue jailed allies!";
        else if (localThiefState == ThiefState.Jailed)
            instruction = "You are JAILED! Wait for rescue...";
        else if (localThiefState == ThiefState.Escaped)
            instruction = "You ESCAPED! Safe!";

        GUI.Label(new Rect(Screen.width * 0.5f - 200, Screen.height - 40, 400, 30), instruction);
    }

    private void DrawRoundTransitionBanner()
    {
        Texture2D overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
        overlayTex.Apply();
        GUI.DrawTexture(new Rect(0, Screen.height * 0.2f, Screen.width, Screen.height * 0.5f), overlayTex);

        float centerY = Screen.height * 0.25f;

        // Round complete
        var bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 42, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        bigStyle.normal.textColor = Color.yellow;
        GUI.Label(new Rect(0, centerY, Screen.width, 55), "ROUND " + currentRound + " COMPLETE!", bigStyle);

        // Who won this round
        string localTeam = GetLocalTeam();
        bool myTeamWon = (lastRoundWinner == localTeam);
        var winStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        winStyle.normal.textColor = myTeamWon ? Color.green : Color.red;
        GUI.Label(new Rect(0, centerY + 60, Screen.width, 40), myTeamWon ? "Your team wins this round!" : "Enemy team wins this round!", winStyle);

        // Score
        int myWins = localTeam == "A" ? teamAWins : teamBWins;
        int enemyWins = localTeam == "A" ? teamBWins : teamAWins;
        var scoreStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        scoreStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(0, centerY + 110, Screen.width, 35), "Score: " + myWins + " - " + enemyWins, scoreStyle);

        // Next round info
        var nextStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
        nextStyle.normal.textColor = Color.yellow;
        float remaining = RoundTransitionDuration - (Time.time - roundTransitionStartTime);
        if (remaining < 0) remaining = 0;
        GUI.Label(new Rect(0, centerY + 155, Screen.width, 30), "Switching sides... Next round in " + Mathf.CeilToInt(remaining) + "s", nextStyle);
    }

    private void DrawMatchResultBanner()
    {
        Texture2D overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.75f));
        overlayTex.Apply();
        GUI.DrawTexture(new Rect(0, Screen.height * 0.15f, Screen.width, Screen.height * 0.6f), overlayTex);

        float centerY = Screen.height * 0.2f;

        // Match over
        var bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 52, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        bigStyle.normal.textColor = Color.yellow;
        GUI.Label(new Rect(0, centerY, Screen.width, 65), "MATCH OVER!", bigStyle);

        // Winner
        string localTeam = GetLocalTeam();
        string matchWinner = teamAWins >= WinsNeeded ? "A" : "B";
        bool myTeamWon = (matchWinner == localTeam);

        var winStyle = new GUIStyle(GUI.skin.label) { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        winStyle.normal.textColor = myTeamWon ? Color.green : Color.red;
        GUI.Label(new Rect(0, centerY + 75, Screen.width, 50), myTeamWon ? "YOUR TEAM WINS!" : "YOUR TEAM LOSES...", winStyle);

        // Final score
        int myWins = localTeam == "A" ? teamAWins : teamBWins;
        int enemyWins = localTeam == "A" ? teamBWins : teamAWins;
        var scoreStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        scoreStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(0, centerY + 135, Screen.width, 40), "Final Score: " + myWins + " - " + enemyWins, scoreStyle);

        // Round breakdown
        var detailStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
        detailStyle.normal.textColor = Color.gray;
        GUI.Label(new Rect(0, centerY + 185, Screen.width, 30), "Best of 3 complete", detailStyle);
    }
}

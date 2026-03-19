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
/// Island Warfare multiplayer game on an 80x80 water grid with 4 islands.
/// Each player occupies an island, gathers resources, builds cannons/walls,
/// and attacks other islands. Last island standing wins.
/// Uses EOS P2P networking (same architecture as CopsAndRobbersController).
/// </summary>
public class IslandWarfareController : MonoBehaviour
{
    // ───── EOS Constants ─────
    private const string LobbyAttributeCode = "CODE";
    private const string LobbyAttributeSessionId = "SESSIONID";
    private const string LobbyAttributeState = "STATE";
    private const string LobbyAttributeSeed = "SEED";
    private const string LobbyAttributeAssign = "ASSIGN";
    private const string LobbyMemberReadyKey = "READY";
    private const string P2PSocketName = "ISLANDWAR";

    // ───── Game Constants ─────
    private const int GridSize = 80;
    private const int IslandSize = 16;
    private const int MaxPlayers = 4;
    private const float GameDuration = 300f; // 5 minutes
    private const float CellSize = 1f;
    private const int BlockSize = 8;
    private const float PickupRadius = 1.5f;
    private const float ResourceRespawnInterval = 30f;
    private const int ResourcesPerIsland = 9; // 3 each: Wood, Stone, Iron
    private const int IslandMaxHP = 100;

    // ───── Building Constants ─────
    private const int CannonCostWood = 3;
    private const int CannonCostIron = 2;
    private const int WallCostWood = 2;
    private const int WallCostStone = 2;
    private const float CannonFireInterval = 8f;
    private const int CannonDamage = 5;
    private const int WallMaxHP = 30;
    private const float ProjectileSpeed = 18f;
    private const float ProjectileArcHeight = 12f;

    // ───── Island Positions (origin = bottom-left of 16x16 area) ─────
    private static readonly Vector2Int[] IslandOrigins = {
        new Vector2Int(8, 56),   // 0: NW
        new Vector2Int(56, 56),  // 1: NE
        new Vector2Int(8, 8),    // 2: SW
        new Vector2Int(56, 8)    // 3: SE
    };

    // ───── Colors ─────
    private static readonly Color ColorWater = new Color(0.15f, 0.35f, 0.65f, 1f);
    private static readonly Color ColorWaterGrid = new Color(0.12f, 0.30f, 0.58f, 1f);
    private static readonly Color ColorIsland = new Color(0.45f, 0.55f, 0.30f, 1f);
    private static readonly Color ColorIslandGrid = new Color(0.40f, 0.50f, 0.27f, 1f);
    private static readonly Color ColorWood = new Color(0.55f, 0.35f, 0.15f, 1f);
    private static readonly Color ColorStone = new Color(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Color ColorIron = new Color(0.50f, 0.50f, 0.55f, 1f);
    private static readonly Color ColorCannon = new Color(0.3f, 0.3f, 0.3f, 1f);
    private static readonly Color ColorWall = new Color(0.7f, 0.65f, 0.55f, 1f);
    private static readonly Color ColorProjectile = new Color(1f, 0.5f, 0.1f, 1f);

    private static readonly Color[] IslandPlayerColors = {
        new Color(0.2f, 0.6f, 0.95f, 1f),  // Blue
        new Color(0.95f, 0.2f, 0.2f, 1f),   // Red
        new Color(0.2f, 0.85f, 0.3f, 1f),   // Green
        new Color(0.95f, 0.8f, 0.1f, 1f)    // Yellow
    };

    // ───── Shader Mode ─────
    public enum ShaderMode { Standard, Jelly, Clay }
    [Header("Shader Mode")]
    public ShaderMode shaderMode = ShaderMode.Standard;

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
    private string lobbyCode = "ISLE";
    private string statusMessage = "";
    private string gameState = "WAITING";
    private string devAuthAddress = "localhost:6547";
    private string devAuthCredential = "";
    private int gameSeed;

    private float sendInterval = 0.1f;
    private float lastSendTime;

    // ───── Bottom Alert Message ─────
    private string alertMessage = "";
    private float alertExpireTime;

    // ───── Island Assignment ─────
    private int localIslandIndex = -1;
    private readonly Dictionary<string, int> playerIslandMap = new Dictionary<string, int>();
    private readonly int[] islandHP = new int[4];
    private readonly bool[] islandAlive = { true, true, true, true };

    // ───── Resources ─────
    private enum ResourceType { Wood, Stone, Iron }

    private struct ResourceNode
    {
        public ResourceType Type;
        public Vector3 Position;
        public int IslandIndex;
        public bool Active;
        public float RespawnTime;
    }

    private readonly List<ResourceNode> resourceNodes = new List<ResourceNode>();
    private readonly List<GameObject> resourceObjects = new List<GameObject>();
    private int localWood, localStone, localIron;

    // ───── Buildings ─────
    private enum BuildingType { Cannon, Wall }

    private class BuildingData
    {
        public int Id;
        public BuildingType Type;
        public Vector3 Position;
        public int OwnerIsland;
        public int HP;
        public float LastFireTime;
        public GameObject Visual;
    }

    private int nextBuildingId = 1;
    private readonly List<BuildingData> buildings = new List<BuildingData>();
    private BuildingType? selectedBuildType = null;

    // ───── Projectiles ─────
    private class ProjectileData
    {
        public Vector3 Start;
        public Vector3 End;
        public float Progress;
        public int TargetIsland;
        public GameObject Visual;
    }

    private readonly List<ProjectileData> projectiles = new List<ProjectileData>();

    // ───── Timer ─────
    private float gameStartTime;
    private float remainingTime;
    private float lastTimerSendTime;

    // ───── Character Models ─────
    private const string CharacterFbxPath = "Characters/character-male-e";
    private GameObject characterPrefab;

    // ───── Building Models (loaded from Resources/Buildings) ─────
    private const string CannonFbxPath = "Buildings/cannon";
    private const string CannonBallFbxPath = "Buildings/cannon-ball";
    private const string WallFbxPath = "Buildings/template-wall";
    private GameObject cannonPrefab;
    private GameObject cannonBallPrefab;
    private GameObject wallPrefab;

    // ───── Resource Models (loaded from Resources/Buildings) ─────
    private const string WoodFbxPath = "Buildings/palm-bend";
    private const string StoneFbxPath = "Buildings/rock-a";
    private const string IronFbxPath = "Buildings/resource-planks";
    private GameObject woodPrefab;
    private GameObject stonePrefab;
    private GameObject ironPrefab;

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
    private bool gridDirty;

    // ───── Camera ─────
    private static readonly Vector3 CameraOffsetLobby = new Vector3(0f, 40f, -25f);
    private static readonly Vector3 CameraOffsetGame = new Vector3(0f, 20f, -12f);
    private Vector3 cameraOffset = CameraOffsetLobby;
    private float cameraZoom = 1f;

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
        characterPrefab = Resources.Load<GameObject>(CharacterFbxPath);
        if (characterPrefab == null) Debug.LogError("Character FBX not found at Resources/" + CharacterFbxPath);

        // Load building models from Resources
        cannonPrefab = Resources.Load<GameObject>(CannonFbxPath);
        cannonBallPrefab = Resources.Load<GameObject>(CannonBallFbxPath);
        wallPrefab = Resources.Load<GameObject>(WallFbxPath);
        if (cannonPrefab == null) Debug.LogWarning("Cannon FBX not found at Resources/" + CannonFbxPath);
        if (cannonBallPrefab == null) Debug.LogWarning("Cannon-ball FBX not found at Resources/" + CannonBallFbxPath);
        if (wallPrefab == null) Debug.LogWarning("Wall FBX not found at Resources/" + WallFbxPath);

        // Load resource models
        woodPrefab = Resources.Load<GameObject>(WoodFbxPath);
        stonePrefab = Resources.Load<GameObject>(StoneFbxPath);
        ironPrefab = Resources.Load<GameObject>(IronFbxPath);
        if (woodPrefab == null) Debug.LogWarning("Wood FBX not found at Resources/" + WoodFbxPath);
        if (stonePrefab == null) Debug.LogWarning("Stone FBX not found at Resources/" + StoneFbxPath);
        if (ironPrefab == null) Debug.LogWarning("Iron FBX not found at Resources/" + IronFbxPath);
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
        UpdateLocalMovement();
        UpdateResourcePickup();
        UpdateBuildInput();
        UpdateCannons();
        UpdateProjectiles();
        UpdateRemotePlayers();
        UpdateCamera();
        UpdateTimer();
        SendLocalPositionIfNeeded();
        ReceivePackets();

        if (gridDirty)
        {
            gridTexture.Apply();
            gridDirty = false;
        }
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
        // Paint everything as water
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                Color c = (x % BlockSize == 0 || z % BlockSize == 0) ? ColorWaterGrid : ColorWater;
                gridTexture.SetPixel(x, z, c);
            }
        }

        // Paint islands
        for (int i = 0; i < IslandOrigins.Length; i++)
        {
            var origin = IslandOrigins[i];
            for (int dx = 0; dx < IslandSize; dx++)
            {
                for (int dz = 0; dz < IslandSize; dz++)
                {
                    int x = origin.x + dx;
                    int z = origin.y + dz;
                    if (x >= 0 && x < GridSize && z >= 0 && z < GridSize)
                    {
                        Color c = (dx % BlockSize == 0 || dz % BlockSize == 0) ? ColorIslandGrid : ColorIsland;
                        gridTexture.SetPixel(x, z, c);
                    }
                }
            }
        }
    }

    private void PaintIslandColor(int islandIndex, Color baseColor)
    {
        var origin = IslandOrigins[islandIndex];
        Color gridColor = baseColor * 0.85f;
        gridColor.a = 1f;
        for (int dx = 0; dx < IslandSize; dx++)
        {
            for (int dz = 0; dz < IslandSize; dz++)
            {
                int x = origin.x + dx;
                int z = origin.y + dz;
                if (x >= 0 && x < GridSize && z >= 0 && z < GridSize)
                {
                    Color c = (dx % BlockSize == 0 || dz % BlockSize == 0) ? gridColor : baseColor;
                    gridTexture.SetPixel(x, z, c);
                }
            }
        }
        gridDirty = true;
    }

    private void EnsureWorld()
    {
        // ── Light ──
        if (GameObject.Find("IWLight") == null)
        {
            var lightGo = new GameObject("IWLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        // ── Ground ──
        groundObj = GameObject.Find("IWGround");
        if (groundObj == null)
        {
            groundObj = new GameObject("IWGround");
            groundObj.transform.position = Vector3.zero;

            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, 0, 0), new Vector3(GridSize, 0, 0),
                new Vector3(0, 0, GridSize), new Vector3(GridSize, 0, GridSize)
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

        // ── Island Height (raise island tiles slightly above water) ──
        for (int i = 0; i < IslandOrigins.Length; i++)
        {
            string name = "IslandPlatform_" + i;
            if (GameObject.Find(name) == null)
            {
                var origin = IslandOrigins[i];
                var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
                platform.name = name;
                platform.transform.position = new Vector3(
                    origin.x + IslandSize * 0.5f,
                    0.15f,
                    origin.y + IslandSize * 0.5f);
                platform.transform.localScale = new Vector3(IslandSize, 0.3f, IslandSize);
                var r = platform.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = CreateMat(ColorIsland);
                // Keep collider for raycasting builds
            }
        }

        // ── Local Player ──
        if (localPlayer == null)
        {
            localPlayer = GameObject.Find("IWLocalPlayer");
        }
        if (localPlayer == null)
        {
            localPlayer = CreateCharacterModel(characterPrefab, "IWLocalPlayer");
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0.3f, GridSize * 0.5f);
        }

        // ── Camera ──
        var cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = localPlayer.transform.position + cameraOffset;
            cam.transform.LookAt(localPlayer.transform.position);
        }
    }

    // ═══════════════════════════════════════════
    //  ISLAND ASSIGNMENT
    // ═══════════════════════════════════════════

    private void AssignIslands()
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

        // Shuffle with game seed
        var rng = new System.Random(gameSeed);
        for (int i = memberIds.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var tmp = memberIds[i];
            memberIds[i] = memberIds[j];
            memberIds[j] = tmp;
        }

        playerIslandMap.Clear();
        for (int i = 0; i < memberIds.Count && i < MaxPlayers; i++)
        {
            playerIslandMap[memberIds[i]] = i;
        }

        // Initialize island HP
        for (int i = 0; i < 4; i++)
        {
            islandHP[i] = (i < memberIds.Count) ? IslandMaxHP : 0;
            islandAlive[i] = (i < memberIds.Count);
        }

        // Set local island
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && localId.IsValid())
        {
            string localStr = localId.ToString();
            if (playerIslandMap.TryGetValue(localStr, out int idx))
                localIslandIndex = idx;
        }

        // Broadcast assignments
        if (isHosting)
        {
            string assignJson = BuildAssignJson(memberIds);
            var update = BuildLobbyUpdate();
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeAssign, ValueType = AttributeType.String,
                AsString = assignJson, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
            lobbyManager.ModifyLobby(update, r => { });

            byte[] jsonBytes = Encoding.UTF8.GetBytes(assignJson);
            byte[] data = new byte[1 + jsonBytes.Length];
            data[0] = (byte)'A';
            Buffer.BlockCopy(jsonBytes, 0, data, 1, jsonBytes.Length);
            SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
        }

        // Spawn player on their island
        SpawnPlayerOnIsland();

        // Paint islands with player colors
        for (int i = 0; i < 4; i++)
        {
            if (islandAlive[i])
                PaintIslandColor(i, IslandPlayerColors[i]);
        }

        // Spawn resources
        SpawnResources();

        // Color the local player
        if (localIslandIndex >= 0 && localIslandIndex < IslandPlayerColors.Length)
            SetCharacterColor(localPlayer, IslandPlayerColors[localIslandIndex]);
    }

    private string BuildAssignJson(List<string> memberIds)
    {
        var sb = new StringBuilder();
        sb.Append("{\"assign\":[");
        for (int i = 0; i < memberIds.Count && i < MaxPlayers; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"id\":\"").Append(memberIds[i]).Append("\",\"island\":").Append(i).Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void ParseAssignJson(string json)
    {
        playerIslandMap.Clear();

        // Parse {"assign":[{"id":"xxx","island":0},…]}
        string search = "\"assign\":[";
        int start = json.IndexOf(search, StringComparison.Ordinal);
        if (start < 0) return;
        start += search.Length;

        int memberCount = 0;
        int pos = start;
        while (pos < json.Length)
        {
            int idStart = json.IndexOf("\"id\":\"", pos, StringComparison.Ordinal);
            if (idStart < 0) break;
            idStart += 6;
            int idEnd = json.IndexOf("\"", idStart, StringComparison.Ordinal);
            if (idEnd < 0) break;
            string id = json.Substring(idStart, idEnd - idStart);

            int islandStart = json.IndexOf("\"island\":", idEnd, StringComparison.Ordinal);
            if (islandStart < 0) break;
            islandStart += 9;
            int islandEnd = islandStart;
            while (islandEnd < json.Length && char.IsDigit(json[islandEnd])) islandEnd++;
            int island = int.Parse(json.Substring(islandStart, islandEnd - islandStart));

            playerIslandMap[id] = island;
            memberCount++;
            pos = islandEnd;
        }

        for (int i = 0; i < 4; i++)
        {
            islandHP[i] = (i < memberCount) ? IslandMaxHP : 0;
            islandAlive[i] = (i < memberCount);
        }

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && localId.IsValid())
        {
            string localStr = localId.ToString();
            if (playerIslandMap.TryGetValue(localStr, out int idx))
                localIslandIndex = idx;
        }

        SpawnPlayerOnIsland();

        for (int i = 0; i < 4; i++)
        {
            if (islandAlive[i])
                PaintIslandColor(i, IslandPlayerColors[i]);
        }

        SpawnResources();

        if (localIslandIndex >= 0 && localIslandIndex < IslandPlayerColors.Length)
            SetCharacterColor(localPlayer, IslandPlayerColors[localIslandIndex]);

        // Color remote players
        foreach (var kvp in remotePlayers)
        {
            if (playerIslandMap.TryGetValue(kvp.Key, out int ri) && ri < IslandPlayerColors.Length)
                SetCharacterColor(kvp.Value, IslandPlayerColors[ri]);
        }
    }

    private void SpawnPlayerOnIsland()
    {
        if (localPlayer == null || localIslandIndex < 0 || localIslandIndex >= IslandOrigins.Length) return;

        var origin = IslandOrigins[localIslandIndex];
        localPlayer.transform.position = new Vector3(
            origin.x + IslandSize * 0.5f,
            0.3f,
            origin.y + IslandSize * 0.5f);
    }

    private Vector3 GetIslandCenter(int islandIndex)
    {
        var origin = IslandOrigins[islandIndex];
        return new Vector3(origin.x + IslandSize * 0.5f, 0.3f, origin.y + IslandSize * 0.5f);
    }

    private bool IsOnIsland(Vector3 pos, int islandIndex)
    {
        if (islandIndex < 0 || islandIndex >= IslandOrigins.Length) return false;
        var origin = IslandOrigins[islandIndex];
        return pos.x >= origin.x && pos.x <= origin.x + IslandSize &&
               pos.z >= origin.y && pos.z <= origin.y + IslandSize;
    }

    // ═══════════════════════════════════════════
    //  RESOURCES
    // ═══════════════════════════════════════════

    private void SpawnResources()
    {
        // Clear existing
        foreach (var obj in resourceObjects)
            if (obj != null) Destroy(obj);
        resourceObjects.Clear();
        resourceNodes.Clear();

        var rng = new System.Random(gameSeed + 100);

        // Guaranteed equal distribution: 3 Wood, 3 Stone, 3 Iron per island
        ResourceType[] guaranteedTypes = {
            ResourceType.Wood, ResourceType.Wood, ResourceType.Wood,
            ResourceType.Stone, ResourceType.Stone, ResourceType.Stone,
            ResourceType.Iron, ResourceType.Iron, ResourceType.Iron
        };

        for (int island = 0; island < 4; island++)
        {
            if (!islandAlive[island]) continue;
            var origin = IslandOrigins[island];

            // Shuffle the guaranteed types for this island
            var shuffled = (ResourceType[])guaranteedTypes.Clone();
            for (int s = shuffled.Length - 1; s > 0; s--)
            {
                int j = rng.Next(s + 1);
                var tmp = shuffled[s];
                shuffled[s] = shuffled[j];
                shuffled[j] = tmp;
            }

            for (int r = 0; r < ResourcesPerIsland; r++)
            {
                float rx = origin.x + 2f + (float)(rng.NextDouble() * (IslandSize - 4));
                float rz = origin.y + 2f + (float)(rng.NextDouble() * (IslandSize - 4));
                ResourceType rt = shuffled[r];

                var node = new ResourceNode
                {
                    Type = rt,
                    Position = new Vector3(rx, 0.3f, rz),
                    IslandIndex = island,
                    Active = true,
                    RespawnTime = 0f
                };
                resourceNodes.Add(node);

                var obj = CreateResourceVisual(node);
                resourceObjects.Add(obj);
            }
        }
    }

    private GameObject CreateResourceVisual(ResourceNode node)
    {
        GameObject obj;

        switch (node.Type)
        {
            case ResourceType.Wood:
                if (woodPrefab != null)
                {
                    obj = Instantiate(woodPrefab);
                    obj.name = "Resource_Wood";
                    obj.transform.position = node.Position + Vector3.up * 0.3f;
                    obj.transform.localScale = Vector3.one * 0.5f;
                    // Random Y rotation for variety
                    obj.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    foreach (var c in obj.GetComponentsInChildren<Collider>()) Destroy(c);
                    return obj;
                }
                obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "Resource_Wood";
                obj.transform.position = node.Position + Vector3.up * 0.3f;
                obj.transform.localScale = new Vector3(0.4f, 0.6f, 0.4f);
                obj.GetComponent<Renderer>().sharedMaterial = CreateMat(ColorWood);
                var wc = obj.GetComponent<Collider>(); if (wc != null) Destroy(wc);
                return obj;

            case ResourceType.Stone:
                if (stonePrefab != null)
                {
                    obj = Instantiate(stonePrefab);
                    obj.name = "Resource_Stone";
                    obj.transform.position = node.Position + Vector3.up * 0.3f;
                    obj.transform.localScale = Vector3.one * 0.5f;
                    obj.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    foreach (var c in obj.GetComponentsInChildren<Collider>()) Destroy(c);
                    return obj;
                }
                obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "Resource_Stone";
                obj.transform.position = node.Position + Vector3.up * 0.3f;
                obj.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                obj.GetComponent<Renderer>().sharedMaterial = CreateMat(ColorStone);
                var sc = obj.GetComponent<Collider>(); if (sc != null) Destroy(sc);
                return obj;

            default: // Iron
                if (ironPrefab != null)
                {
                    obj = Instantiate(ironPrefab);
                    obj.name = "Resource_Iron";
                    obj.transform.position = node.Position + Vector3.up * 0.3f;
                    obj.transform.localScale = Vector3.one * 0.5f;
                    obj.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    foreach (var c in obj.GetComponentsInChildren<Collider>()) Destroy(c);
                    return obj;
                }
                obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "Resource_Iron";
                obj.transform.position = node.Position + Vector3.up * 0.3f;
                obj.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);
                obj.GetComponent<Renderer>().sharedMaterial = CreateMat(ColorIron);
                var ic = obj.GetComponent<Collider>(); if (ic != null) Destroy(ic);
                return obj;
        }
    }

    private void UpdateResourcePickup()
    {
        if (!gameStarted || gameEnded || localPlayer == null || localIslandIndex < 0) return;

        Vector3 localPos = localPlayer.transform.position;

        for (int i = 0; i < resourceNodes.Count; i++)
        {
            var node = resourceNodes[i];
            if (!node.Active)
            {
                // Check respawn
                if (node.RespawnTime > 0 && Time.time >= node.RespawnTime)
                {
                    node.Active = true;
                    node.RespawnTime = 0f;
                    resourceNodes[i] = node;
                    if (i < resourceObjects.Count && resourceObjects[i] != null)
                        resourceObjects[i].SetActive(true);
                }
                continue;
            }

            if (node.IslandIndex != localIslandIndex) continue;

            float dist = Vector3.Distance(
                new Vector3(localPos.x, 0, localPos.z),
                new Vector3(node.Position.x, 0, node.Position.z));

            if (dist <= PickupRadius)
            {
                // Collect
                switch (node.Type)
                {
                    case ResourceType.Wood: localWood++; break;
                    case ResourceType.Stone: localStone++; break;
                    case ResourceType.Iron: localIron++; break;
                }

                DeactivateResource(i);
                BroadcastResourcePickup(i);
            }
        }
    }

    // ═══════════════════════════════════════════
    //  BUILDING SYSTEM
    // ═══════════════════════════════════════════

    private void UpdateBuildInput()
    {
        if (!gameStarted || gameEnded || localIslandIndex < 0) return;

        // Toggle build mode
        if (Input.GetKeyDown(KeyCode.Alpha1))
            selectedBuildType = (selectedBuildType == BuildingType.Cannon) ? (BuildingType?)null : BuildingType.Cannon;
        if (Input.GetKeyDown(KeyCode.Alpha2))
            selectedBuildType = (selectedBuildType == BuildingType.Wall) ? (BuildingType?)null : BuildingType.Wall;
        if (Input.GetKeyDown(KeyCode.Escape))
            selectedBuildType = null;

        // Place building on click
        if (selectedBuildType.HasValue && Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 200f))
            {
                Vector3 buildPos = hit.point;
                buildPos.y = 0.3f;

                // Must be on own island
                if (!IsOnIsland(buildPos, localIslandIndex))
                {
                    statusMessage = "Build on your island only!";
                    return;
                }

                if (selectedBuildType.Value == BuildingType.Cannon)
                {
                    if (localWood >= CannonCostWood && localIron >= CannonCostIron)
                    {
                        localWood -= CannonCostWood;
                        localIron -= CannonCostIron;
                        int id = nextBuildingId++;
                        PlaceBuilding(id, BuildingType.Cannon, buildPos, localIslandIndex);
                        BroadcastBuild(id, BuildingType.Cannon, buildPos);
                    }
                    else
                    {
                        ShowAlert("Not enough! Need Wood:" + CannonCostWood + " Iron:" + CannonCostIron
                            + " (Have W:" + localWood + " I:" + localIron + ")");
                    }
                }
                else if (selectedBuildType.Value == BuildingType.Wall)
                {
                    if (localWood >= WallCostWood && localStone >= WallCostStone)
                    {
                        localWood -= WallCostWood;
                        localStone -= WallCostStone;
                        int id = nextBuildingId++;
                        float rotY = localPlayer != null ? localPlayer.transform.eulerAngles.y : 0f;
                        PlaceBuilding(id, BuildingType.Wall, buildPos, localIslandIndex, rotY);
                        BroadcastBuild(id, BuildingType.Wall, buildPos, rotY);
                    }
                    else
                    {
                        ShowAlert("Not enough! Need Wood:" + WallCostWood + " Stone:" + WallCostStone
                            + " (Have W:" + localWood + " S:" + localStone + ")");
                    }
                }
            }
        }
    }

    private void PlaceBuilding(int id, BuildingType type, Vector3 position, int ownerIsland, float rotationY = 0f)
    {
        var building = new BuildingData
        {
            Id = id,
            Type = type,
            Position = position,
            OwnerIsland = ownerIsland,
            HP = (type == BuildingType.Wall) ? WallMaxHP : 999,
            LastFireTime = Time.time
        };

        // Visual
        if (type == BuildingType.Cannon)
        {
            GameObject obj;
            if (cannonPrefab != null)
            {
                obj = Instantiate(cannonPrefab);
                obj.name = "Cannon_" + id;
                obj.transform.position = position + Vector3.up * 0.3f;
                obj.transform.localScale = Vector3.one * 0.7f;
            }
            else
            {
                // Fallback to primitives
                obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "Cannon_" + id;
                obj.transform.position = position + Vector3.up * 0.5f;
                obj.transform.localScale = new Vector3(0.6f, 0.5f, 0.6f);
                var r = obj.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = CreateMat(ColorCannon);
                var barrel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                barrel.name = "Barrel";
                barrel.transform.SetParent(obj.transform);
                barrel.transform.localPosition = new Vector3(0f, 0.5f, 0.5f);
                barrel.transform.localScale = new Vector3(0.3f, 0.3f, 1.5f);
                var br = barrel.GetComponent<Renderer>();
                if (br != null) br.sharedMaterial = CreateMat(ColorCannon);
                var bcol = barrel.GetComponent<Collider>();
                if (bcol != null) Destroy(bcol);
            }
            building.Visual = obj;
        }
        else // Wall
        {
            GameObject obj;
            Quaternion wallRot = Quaternion.Euler(0f, rotationY, 0f);
            if (wallPrefab != null)
            {
                obj = Instantiate(wallPrefab);
                obj.name = "Wall_" + id;
                obj.transform.position = position + Vector3.up * 0.0f;
                obj.transform.rotation = wallRot;
                obj.transform.localScale = Vector3.one * 0.8f;
            }
            else
            {
                // Fallback to primitives
                obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "Wall_" + id;
                obj.transform.position = position + Vector3.up * 1f;
                obj.transform.rotation = wallRot;
                obj.transform.localScale = new Vector3(2f, 2f, 0.5f);
                var r = obj.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = CreateMat(ColorWall);
            }
            building.Visual = obj;
        }

        buildings.Add(building);
    }

    // ═══════════════════════════════════════════
    //  COMBAT SYSTEM
    // ═══════════════════════════════════════════

    private void UpdateCannons()
    {
        if (!gameStarted || gameEnded) return;

        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (b.Type != BuildingType.Cannon) continue;
            if (b.OwnerIsland != localIslandIndex) continue; // Only local player fires their cannons

            if (Time.time - b.LastFireTime >= CannonFireInterval)
            {
                b.LastFireTime = Time.time;

                // Find nearest alive enemy island
                int target = FindNearestEnemyIsland(b.Position, b.OwnerIsland);
                if (target >= 0)
                {
                    // Aim cannon visual toward target
                    Vector3 targetCenter = GetIslandCenter(target);
                    if (b.Visual != null)
                    {
                        Vector3 lookDir = targetCenter - b.Position;
                        lookDir.y = 0;
                        if (lookDir.sqrMagnitude > 0.01f)
                            b.Visual.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
                    }

                    FireProjectile(b.Position, target);
                    BroadcastFire(b.Position, target);
                }
            }
        }
    }

    private int FindNearestEnemyIsland(Vector3 fromPos, int ownerIsland)
    {
        int best = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < 4; i++)
        {
            if (i == ownerIsland || !islandAlive[i]) continue;
            float dist = Vector3.Distance(fromPos, GetIslandCenter(i));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }

    private void FireProjectile(Vector3 from, int targetIsland)
    {
        Vector3 targetCenter = GetIslandCenter(targetIsland);
        // Add some random spread
        var rng = new System.Random(Time.frameCount);
        float spreadX = (float)(rng.NextDouble() * 6 - 3);
        float spreadZ = (float)(rng.NextDouble() * 6 - 3);
        Vector3 end = targetCenter + new Vector3(spreadX, 0, spreadZ);

        var proj = new ProjectileData
        {
            Start = from + Vector3.up * 1f,
            End = end,
            Progress = 0f,
            TargetIsland = targetIsland
        };

        // Visual
        GameObject obj;
        if (cannonBallPrefab != null)
        {
            obj = Instantiate(cannonBallPrefab);
            obj.name = "Projectile";
            obj.transform.position = proj.Start;
            obj.transform.localScale = Vector3.one * 0.5f;
            // Remove colliders from FBX so it doesn't block anything
            foreach (var c in obj.GetComponentsInChildren<Collider>())
                Destroy(c);
        }
        else
        {
            // Fallback to sphere primitive
            obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obj.name = "Projectile";
            obj.transform.position = proj.Start;
            obj.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);
            var r = obj.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateMat(ColorProjectile);
            var col = obj.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }
        proj.Visual = obj;

        projectiles.Add(proj);
    }

    private void UpdateProjectiles()
    {
        if (!gameStarted) return;

        for (int i = projectiles.Count - 1; i >= 0; i--)
        {
            var proj = projectiles[i];
            float totalDist = Vector3.Distance(
                new Vector3(proj.Start.x, 0, proj.Start.z),
                new Vector3(proj.End.x, 0, proj.End.z));
            float speed = ProjectileSpeed / Mathf.Max(totalDist, 1f);
            proj.Progress += speed * Time.deltaTime;

            // Parabolic arc position
            Vector3 flatPos = Vector3.Lerp(proj.Start, proj.End, proj.Progress);
            float arc = ProjectileArcHeight * 4f * proj.Progress * (1f - proj.Progress);
            Vector3 currentPos = new Vector3(flatPos.x, flatPos.y + arc, flatPos.z);

            if (proj.Visual != null)
                proj.Visual.transform.position = currentPos;

            // ── After game ended: just animate visuals, no damage ──
            if (gameEnded)
            {
                if (proj.Progress >= 1f)
                {
                    if (proj.Visual != null) Destroy(proj.Visual);
                    projectiles.RemoveAt(i);
                }
                else
                {
                    projectiles[i] = proj;
                }
                continue;
            }

            // ── Check wall collision during flight ──
            // Only check when projectile is descending (past the arc peak, progress > 0.4)
            // and reasonably low (arc height < 4 units, so it could hit a wall)
            if (proj.Progress > 0.4f && arc < 4f)
            {
                BuildingData hitWall = CheckWallCollisionAtPoint(currentPos, proj.TargetIsland);
                if (hitWall != null)
                {
                    DamageWall(hitWall);
                    CreateHitEffect(currentPos);
                    if (proj.Visual != null) Destroy(proj.Visual);
                    projectiles.RemoveAt(i);
                    continue;
                }
            }

            // ── Reached destination ──
            if (proj.Progress >= 1f)
            {
                OnProjectileHitGround(proj);
                if (proj.Visual != null) Destroy(proj.Visual);
                projectiles.RemoveAt(i);
                continue;
            }

            projectiles[i] = proj;
        }
    }

    /// <summary>
    /// Check if a point collides with any wall, considering the wall's orientation.
    /// The wall forms a rectangular barrier: wide along its local X (left-right),
    /// thin along its local Z (front-back).
    /// </summary>
    private BuildingData CheckWallCollisionAtPoint(Vector3 point, int targetIsland)
    {
        const float wallHalfWidth = 1.5f;  // how wide the wall blocks (left-right)
        const float wallHalfDepth = 0.8f;  // how deep the wall blocks (front-back)

        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (b.Type != BuildingType.Wall) continue;
            // Check walls on any island (a projectile could hit a wall on any island it passes over)
            if (!islandAlive[b.OwnerIsland]) continue;

            // Get wall's local axes from its visual rotation
            float rotY = 0f;
            if (b.Visual != null)
                rotY = b.Visual.transform.eulerAngles.y;
            float rad = rotY * Mathf.Deg2Rad;
            Vector3 wallRight = new Vector3(Mathf.Cos(rad), 0, -Mathf.Sin(rad));  // local X
            Vector3 wallForward = new Vector3(Mathf.Sin(rad), 0, Mathf.Cos(rad)); // local Z

            // Project the offset from wall center to point onto wall's local axes
            Vector3 offset = new Vector3(point.x - b.Position.x, 0, point.z - b.Position.z);
            float projRight = Vector3.Dot(offset, wallRight);
            float projForward = Vector3.Dot(offset, wallForward);

            // Check if inside the wall's rectangular area
            if (Mathf.Abs(projRight) <= wallHalfWidth && Mathf.Abs(projForward) <= wallHalfDepth)
            {
                return b;
            }
        }
        return null;
    }

    private void DamageWall(BuildingData wall)
    {
        wall.HP -= CannonDamage;
        if (wall.HP <= 0)
        {
            if (wall.Visual != null) Destroy(wall.Visual);
            buildings.Remove(wall);
            BroadcastDestroyBuilding(wall.Id);
        }
    }

    /// <summary>
    /// Projectile reached its destination without hitting a wall → damage island directly.
    /// </summary>
    private void OnProjectileHitGround(ProjectileData proj)
    {
        int targetIsland = proj.TargetIsland;
        if (targetIsland < 0 || targetIsland >= 4 || !islandAlive[targetIsland]) return;

        // Only the cannon owner (who fired) applies damage to avoid double-counting
        // Remote players see the projectile visually but don't apply damage
        if (!playerIslandMap.ContainsValue(targetIsland)) return;

        // Check if landing point is actually on the target island
        Vector3 landPoint = proj.End;
        if (!IsOnIsland(landPoint, targetIsland))
        {
            // Missed the island entirely
            CreateHitEffect(landPoint);
            return;
        }

        // Damage island
        islandHP[targetIsland] -= CannonDamage;
        if (islandHP[targetIsland] <= 0)
        {
            islandHP[targetIsland] = 0;
            islandAlive[targetIsland] = false;
            OnIslandDestroyed(targetIsland);
        }
        BroadcastIslandHP(targetIsland, islandHP[targetIsland]);

        // Hit effect: brief flash on grid
        CreateHitEffect(proj.End);
    }

    private void CreateHitEffect(Vector3 pos)
    {
        var effect = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        effect.name = "HitEffect";
        effect.transform.position = pos + Vector3.up * 0.5f;
        effect.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
        var r = effect.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = CreateMat(new Color(1f, 0.3f, 0f, 0.8f));
        var col = effect.GetComponent<Collider>();
        if (col != null) Destroy(col);
        Destroy(effect, 0.5f);
    }

    private void OnIslandDestroyed(int islandIndex)
    {
        // Paint island as dark/destroyed
        PaintIslandColor(islandIndex, new Color(0.2f, 0.2f, 0.2f, 1f));

        // Remove buildings on that island
        for (int i = buildings.Count - 1; i >= 0; i--)
        {
            if (buildings[i].OwnerIsland == islandIndex)
            {
                if (buildings[i].Visual != null) Destroy(buildings[i].Visual);
                buildings.RemoveAt(i);
            }
        }

        // Check win condition
        CheckGameEnd();
    }

    // ═══════════════════════════════════════════
    //  TIMER & WIN CONDITION
    // ═══════════════════════════════════════════

    private void UpdateTimer()
    {
        if (!gameStarted || gameEnded) return;

        remainingTime = GameDuration - (Time.time - gameStartTime);
        if (remainingTime <= 0f)
        {
            remainingTime = 0f;
            EndGame();
            return;
        }

        // Host broadcasts timer every 1 second
        if (isHosting && Time.time - lastTimerSendTime >= 1f)
        {
            lastTimerSendTime = Time.time;
            byte[] data = new byte[5];
            data[0] = (byte)'G';
            Buffer.BlockCopy(BitConverter.GetBytes(remainingTime), 0, data, 1, 4);
            SendToAll(new ArraySegment<byte>(data), PacketReliability.UnreliableUnordered);
        }
    }

    private void CheckGameEnd()
    {
        if (gameEnded) return;

        int aliveCount = 0;
        for (int i = 0; i < 4; i++)
            if (islandAlive[i]) aliveCount++;

        if (aliveCount <= 1)
            EndGame();
    }

    private void EndGame()
    {
        gameEnded = true;
        gameState = "ENDED";

        if (isHosting)
        {
            var update = BuildLobbyUpdate();
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeState, ValueType = AttributeType.String,
                AsString = "ENDED", Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
            lobbyManager.ModifyLobby(update, r => { });
        }
    }

    private int GetWinnerIsland()
    {
        int bestIsland = -1;
        int bestHP = -1;
        for (int i = 0; i < 4; i++)
        {
            if (islandAlive[i] && islandHP[i] > bestHP)
            {
                bestHP = islandHP[i];
                bestIsland = i;
            }
        }
        return bestIsland;
    }

    // ═══════════════════════════════════════════
    //  PLAYER MOVEMENT
    // ═══════════════════════════════════════════

    private void UpdateLocalMovement()
    {
        if (localPlayer == null || !gameStarted || gameEnded) return;
        if (localIslandIndex < 0) return;

        // Dead island = no movement
        if (!islandAlive[localIslandIndex]) return;

        float speed = 10f;
        float moveX = Input.GetAxisRaw("Horizontal");
        float moveZ = Input.GetAxisRaw("Vertical");
        Vector3 dir = new Vector3(moveX, 0f, moveZ).normalized;
        Vector3 delta = dir * speed * Time.deltaTime;
        Vector3 newPos = localPlayer.transform.position + delta;

        // Face movement direction
        if (dir.sqrMagnitude > 0.01f)
            localPlayer.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        // Clamp to own island
        var origin = IslandOrigins[localIslandIndex];
        newPos.x = Mathf.Clamp(newPos.x, origin.x + 0.5f, origin.x + IslandSize - 0.5f);
        newPos.z = Mathf.Clamp(newPos.z, origin.y + 0.5f, origin.y + IslandSize - 0.5f);
        newPos.y = 0.3f;

        localPlayer.transform.position = newPos;
    }

    private void UpdateCamera()
    {
        if (localPlayer == null) return;
        var cam = Camera.main;
        if (cam == null) return;

        // Zoom with scroll wheel
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0)
        {
            cameraZoom = Mathf.Clamp(cameraZoom - scroll * 2f, 0.5f, 3f);
        }

        Vector3 offset = cameraOffset * cameraZoom;
        Vector3 target = localPlayer.transform.position + offset;
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
            remote = CreateCharacterModel(characterPrefab, "IWRemote_" + peerId);
            remotePlayers.Add(peerId, remote);

            // Color by island
            if (playerIslandMap.TryGetValue(peerId, out int ri) && ri < IslandPlayerColors.Length)
                SetCharacterColor(remote, IslandPlayerColors[ri]);
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

    private void ShowAlert(string message)
    {
        alertMessage = message;
        alertExpireTime = Time.time + 2f;
    }

    private void DeactivateResource(int index)
    {
        var node = resourceNodes[index];
        node.Active = false;
        node.RespawnTime = Time.time + ResourceRespawnInterval;
        resourceNodes[index] = node;

        if (index < resourceObjects.Count && resourceObjects[index] != null)
            resourceObjects[index].SetActive(false);
    }

    private void BroadcastResourcePickup(int resourceIndex)
    {
        // 'R' + int32 resourceIndex
        byte[] data = new byte[5];
        data[0] = (byte)'R';
        Buffer.BlockCopy(BitConverter.GetBytes(resourceIndex), 0, data, 1, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
    }

    private void BroadcastBuild(int buildingId, BuildingType type, Vector3 pos, float rotationY = 0f)
    {
        // 'B' + int32 id + byte type + float x + float z + int32 ownerIsland + float rotY
        byte[] data = new byte[22];
        data[0] = (byte)'B';
        Buffer.BlockCopy(BitConverter.GetBytes(buildingId), 0, data, 1, 4);
        data[5] = (byte)type;
        Buffer.BlockCopy(BitConverter.GetBytes(pos.x), 0, data, 6, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.z), 0, data, 10, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(localIslandIndex), 0, data, 14, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(rotationY), 0, data, 18, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
    }

    private void BroadcastFire(Vector3 fromPos, int targetIsland)
    {
        // 'F' + float fromX + float fromZ + int32 targetIsland
        byte[] data = new byte[13];
        data[0] = (byte)'F';
        Buffer.BlockCopy(BitConverter.GetBytes(fromPos.x), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(fromPos.z), 0, data, 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(targetIsland), 0, data, 9, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void BroadcastIslandHP(int islandIndex, int hp)
    {
        // 'I' + int32 islandIndex + int32 hp
        byte[] data = new byte[9];
        data[0] = (byte)'I';
        Buffer.BlockCopy(BitConverter.GetBytes(islandIndex), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(hp), 0, data, 5, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
    }

    private void BroadcastDestroyBuilding(int buildingId)
    {
        // 'K' + int32 buildingId
        byte[] data = new byte[5];
        data[0] = (byte)'K';
        Buffer.BlockCopy(BitConverter.GetBytes(buildingId), 0, data, 1, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
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
                string json = Encoding.UTF8.GetString(buffer, 1, (int)bytesWritten - 1);
                ParseAssignJson(json);
            }
            else if (type == (byte)'B' && bytesWritten >= 18)
            {
                int bId = BitConverter.ToInt32(buffer, 1);
                BuildingType bType = (BuildingType)buffer[5];
                float bx = BitConverter.ToSingle(buffer, 6);
                float bz = BitConverter.ToSingle(buffer, 10);
                int owner = BitConverter.ToInt32(buffer, 14);
                float rotY = (bytesWritten >= 22) ? BitConverter.ToSingle(buffer, 18) : 0f;
                PlaceBuilding(bId, bType, new Vector3(bx, 0.3f, bz), owner, rotY);
                if (bId >= nextBuildingId) nextBuildingId = bId + 1;
            }
            else if (type == (byte)'F' && bytesWritten >= 13)
            {
                float fx = BitConverter.ToSingle(buffer, 1);
                float fz = BitConverter.ToSingle(buffer, 5);
                int targetIsland = BitConverter.ToInt32(buffer, 9);
                FireProjectile(new Vector3(fx, 0.3f, fz), targetIsland);
            }
            else if (type == (byte)'I' && bytesWritten >= 9)
            {
                if (!gameEnded)
                {
                    int iIdx = BitConverter.ToInt32(buffer, 1);
                    int hp = BitConverter.ToInt32(buffer, 5);
                    if (iIdx >= 0 && iIdx < 4)
                    {
                        islandHP[iIdx] = hp;
                        if (hp <= 0 && islandAlive[iIdx])
                        {
                            islandAlive[iIdx] = false;
                            OnIslandDestroyed(iIdx);
                        }
                    }
                }
            }
            else if (type == (byte)'K' && bytesWritten >= 5)
            {
                int kId = BitConverter.ToInt32(buffer, 1);
                for (int i = buildings.Count - 1; i >= 0; i--)
                {
                    if (buildings[i].Id == kId)
                    {
                        if (buildings[i].Visual != null) Destroy(buildings[i].Visual);
                        buildings.RemoveAt(i);
                        break;
                    }
                }
            }
            else if (type == (byte)'R' && bytesWritten >= 5)
            {
                int rIdx = BitConverter.ToInt32(buffer, 1);
                if (rIdx >= 0 && rIdx < resourceNodes.Count)
                {
                    DeactivateResource(rIdx);
                }
            }
            else if (type == (byte)'G' && bytesWritten >= 5)
            {
                remainingTime = BitConverter.ToSingle(buffer, 1);
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
            if (go.GetComponent<Collider>() == null)
                go.AddComponent<BoxCollider>();
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
        }
        return go;
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
            case ShaderMode.Clay: return CreateClayMat(c);
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
            BucketId = "ISLANDWAR",
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
        cameraZoom = 1f;
        startUpdateInProgress = false;
        localIslandIndex = -1;
        playerIslandMap.Clear();
        localWood = 0;
        localStone = 0;
        localIron = 0;
        selectedBuildType = null;

        for (int i = 0; i < 4; i++)
        {
            islandHP[i] = IslandMaxHP;
            islandAlive[i] = true;
        }

        // Destroy buildings
        foreach (var b in buildings)
            if (b.Visual != null) Destroy(b.Visual);
        buildings.Clear();
        nextBuildingId = 1;

        // Destroy projectiles
        foreach (var p in projectiles)
            if (p.Visual != null) Destroy(p.Visual);
        projectiles.Clear();

        // Destroy resources
        foreach (var obj in resourceObjects)
            if (obj != null) Destroy(obj);
        resourceObjects.Clear();
        resourceNodes.Clear();

        // Repaint grid
        PaintGrid();
        gridDirty = true;

        // Reset player position
        if (localPlayer != null)
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0.3f, GridSize * 0.5f);
    }

    private void UpdateLobbyState()
    {
        if (!IsInLobby()) { gameState = "WAITING"; gameStarted = false; return; }

        string state = GetLobbyAttribute(LobbyAttributeState);
        if (!string.IsNullOrEmpty(state)) gameState = state;

        if (!gameStarted && string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = true;
            gameStartTime = Time.time;
            remainingTime = GameDuration;

            string seedStr = GetLobbyAttribute(LobbyAttributeSeed);
            if (!string.IsNullOrEmpty(seedStr) && int.TryParse(seedStr, out int parsed))
                gameSeed = parsed;

            cameraOffset = CameraOffsetGame;
            AssignIslands();
            statusMessage = "Game started! Island: " + localIslandIndex + " | Seed: " + gameSeed;
        }

        if (!gameEnded && string.Equals(gameState, "ENDED", StringComparison.OrdinalIgnoreCase))
        {
            gameEnded = true;
        }

        string seedStr2 = GetLobbyAttribute(LobbyAttributeSeed);
        if (!string.IsNullOrEmpty(seedStr2) && int.TryParse(seedStr2, out int parsed2))
            gameSeed = parsed2;

        // Check for assignment from lobby attribute (for late joiners)
        if (gameStarted && playerIslandMap.Count == 0)
        {
            string assignStr = GetLobbyAttribute(LobbyAttributeAssign);
            if (!string.IsNullOrEmpty(assignStr))
                ParseAssignJson(assignStr);
        }
    }

    private void UpdateReadyState()
    {
        if (!IsInLobby() || !isHosting) return;
        if (!AreAllMembersReady() || string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase) || startUpdateInProgress) return;

        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null || lobby.Members.Count < 2) return;

        startUpdateInProgress = true;
        var update = BuildLobbyUpdate();
        update.Attributes.Add(new LobbyAttribute
        {
            Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = "STARTED",
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        });
        lobbyManager.ModifyLobby(update, r =>
        {
            startUpdateInProgress = false;
            if (r == Result.Success) statusMessage = "Game started!";
        });
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
        GUILayout.Label("<b>Island Warfare</b>", titleStyle);
        GUILayout.Label("80x80 Grid · 4 Islands · Max 4 Players · 5min");

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

            if (localIslandIndex >= 0)
            {
                string[] islandNames = { "NW (Blue)", "NE (Red)", "SW (Green)", "SE (Yellow)" };
                var islandStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                islandStyle.normal.textColor = IslandPlayerColors[localIslandIndex];
                GUILayout.Label("Your Island: " + islandNames[localIslandIndex], islandStyle);
            }
        }

        GUILayout.EndArea();

        // ── Right Panel: Game HUD ──
        if (gameStarted)
        {
            DrawGameHUD();
        }

        // ── Center: Result Banner ──
        if (gameEnded)
        {
            DrawResultBanner();
        }
    }

    private void DrawGameHUD()
    {
        GUILayout.BeginArea(new Rect(Screen.width - 320, 10, 310, 400), GUI.skin.box);

        var hudTitle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUILayout.Label("ISLAND WARFARE", hudTitle);

        // Timer
        GUILayout.Space(4);
        var timerStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        timerStyle.normal.textColor = remainingTime <= 30f ? Color.red : Color.white;
        int min = (int)(remainingTime / 60);
        int sec = (int)(remainingTime % 60);
        GUILayout.Label(string.Format("{0}:{1:00}", min, sec), timerStyle);

        GUILayout.Space(6);

        // Resources
        var resStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        GUILayout.Label("--- Resources ---", resStyle);
        resStyle.normal.textColor = ColorWood;
        GUILayout.Label("  Wood: " + localWood, resStyle);
        resStyle.normal.textColor = ColorStone;
        GUILayout.Label("  Stone: " + localStone, resStyle);
        resStyle.normal.textColor = new Color(0.6f, 0.6f, 0.7f);
        GUILayout.Label("  Iron: " + localIron, resStyle);

        GUILayout.Space(6);

        // Build menu
        var buildStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        GUILayout.Label("--- Build (click to place) ---", buildStyle);

        string cannonLabel = "[1] Cannon (W:" + CannonCostWood + " I:" + CannonCostIron + ")";
        string wallLabel = "[2] Wall (W:" + WallCostWood + " S:" + WallCostStone + ")";

        if (selectedBuildType == BuildingType.Cannon)
            cannonLabel = ">> " + cannonLabel + " <<";
        if (selectedBuildType == BuildingType.Wall)
            wallLabel = ">> " + wallLabel + " <<";

        var cannonStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        cannonStyle.normal.textColor = selectedBuildType == BuildingType.Cannon ? Color.yellow : Color.white;
        GUILayout.Label(cannonLabel, cannonStyle);
        var wallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        wallStyle.normal.textColor = selectedBuildType == BuildingType.Wall ? Color.yellow : Color.white;
        GUILayout.Label(wallLabel, wallStyle);

        GUILayout.Space(6);

        // Island HP
        GUILayout.Label("--- Islands ---");
        string[] names = { "NW", "NE", "SW", "SE" };
        for (int i = 0; i < 4; i++)
        {
            if (!islandAlive[i] && islandHP[i] <= 0 && !playerIslandMap.ContainsValue(i)) continue;

            var hpStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            if (i < IslandPlayerColors.Length)
                hpStyle.normal.textColor = islandAlive[i] ? IslandPlayerColors[i] : Color.gray;

            string marker = (i == localIslandIndex) ? " (YOU)" : "";
            string hpText = islandAlive[i] ? "HP: " + islandHP[i] + "/" + IslandMaxHP : "DESTROYED";
            GUILayout.Label("  " + names[i] + marker + " - " + hpText, hpStyle);
        }

        GUILayout.EndArea();

        // Bottom instruction
        string instruction = "WASD: Move | 1: Cannon | 2: Wall | Click: Place | Scroll: Zoom";
        if (localIslandIndex >= 0 && !islandAlive[localIslandIndex])
            instruction = "Your island has been destroyed!";

        var instrStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        instrStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(Screen.width * 0.5f - 250, Screen.height - 35, 500, 25), instruction, instrStyle);

        // Alert message (2-second fade)
        if (!string.IsNullOrEmpty(alertMessage) && Time.time < alertExpireTime)
        {
            float remaining = alertExpireTime - Time.time;
            float alpha = Mathf.Clamp01(remaining); // fade out in last 1 second
            var alertStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
            };
            alertStyle.normal.textColor = new Color(1f, 0.3f, 0.3f, alpha);
            GUI.Label(new Rect(Screen.width * 0.5f - 300, Screen.height - 70, 600, 30), alertMessage, alertStyle);
        }
    }

    private void DrawResultBanner()
    {
        Texture2D overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.6f));
        overlayTex.Apply();
        GUI.DrawTexture(new Rect(0, Screen.height * 0.25f, Screen.width, Screen.height * 0.45f), overlayTex);

        int winnerIsland = GetWinnerIsland();
        float centerY = Screen.height * 0.3f;

        var bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
        };

        var subStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
        };
        subStyle.normal.textColor = Color.white;

        string[] islandNames = { "NW (Blue)", "NE (Red)", "SW (Green)", "SE (Yellow)" };

        if (winnerIsland >= 0 && winnerIsland < IslandPlayerColors.Length)
        {
            bigStyle.normal.textColor = IslandPlayerColors[winnerIsland];
            GUI.Label(new Rect(0, centerY, Screen.width, 60), islandNames[winnerIsland] + " WINS!", bigStyle);
            GUI.Label(new Rect(0, centerY + 65, Screen.width, 40),
                "Island HP: " + islandHP[winnerIsland] + "/" + IslandMaxHP, subStyle);
        }
        else
        {
            bigStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(0, centerY, Screen.width, 60), "DRAW!", bigStyle);
        }

        // Personal result
        var personalStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22, alignment = TextAnchor.MiddleCenter
        };

        if (localIslandIndex == winnerIsland)
        {
            personalStyle.normal.textColor = Color.green;
            GUI.Label(new Rect(0, centerY + 120, Screen.width, 35), "Victory! Your island survived!", personalStyle);
        }
        else if (localIslandIndex >= 0 && islandAlive[localIslandIndex])
        {
            personalStyle.normal.textColor = Color.yellow;
            GUI.Label(new Rect(0, centerY + 120, Screen.width, 35),
                "Your island survived with " + islandHP[localIslandIndex] + " HP", personalStyle);
        }
        else
        {
            personalStyle.normal.textColor = Color.red;
            GUI.Label(new Rect(0, centerY + 120, Screen.width, 35), "Your island was destroyed...", personalStyle);
        }
    }
}

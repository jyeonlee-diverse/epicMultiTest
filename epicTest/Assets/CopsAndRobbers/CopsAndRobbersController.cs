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
    private const int ObstacleCount = 25;
    private const int BlockSize = 8;

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

    // ───── Obstacles ─────
    private GameObject obstaclesRoot;
    private readonly List<BoxCollider> obstacleColliders = new List<BoxCollider>();

    // ───── Markers ─────
    private GameObject jailMarker;
    private readonly List<GameObject> escapeMarkers = new List<GameObject>();

    // ───── Camera ─────
    private static readonly Vector3 CameraOffsetLobby = new Vector3(0f, 30f, -20f);
    private static readonly Vector3 CameraOffsetGame = new Vector3(0f, 12f, -8f);
    private Vector3 cameraOffset = CameraOffsetLobby;

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

        // ── Jail Marker (tall transparent walls outline) ──
        if (jailMarker == null)
        {
            jailMarker = new GameObject("JailMarker");
            float jailWorldX = JailCenter.x - BlockSize / 2 + 0.5f;
            float jailWorldZ = JailCenter.y - BlockSize / 2 + 0.5f;
            // 4 wall segments
            CreateJailWall(jailMarker.transform, new Vector3(jailWorldX + BlockSize * 0.5f - 0.5f, 1.5f, jailWorldZ - 0.5f), new Vector3(BlockSize, 3f, 0.2f));
            CreateJailWall(jailMarker.transform, new Vector3(jailWorldX + BlockSize * 0.5f - 0.5f, 1.5f, jailWorldZ + BlockSize - 0.5f), new Vector3(BlockSize, 3f, 0.2f));
            CreateJailWall(jailMarker.transform, new Vector3(jailWorldX - 0.5f, 1.5f, jailWorldZ + BlockSize * 0.5f - 0.5f), new Vector3(0.2f, 3f, BlockSize));
            CreateJailWall(jailMarker.transform, new Vector3(jailWorldX + BlockSize - 0.5f, 1.5f, jailWorldZ + BlockSize * 0.5f - 0.5f), new Vector3(0.2f, 3f, BlockSize));
        }

        // ── Escape Point Markers ──
        if (escapeMarkers.Count == 0)
        {
            foreach (var ep in EscapePoints)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "EscapePoint_" + ep.x + "_" + ep.y;
                marker.transform.position = new Vector3(ep.x + 0.5f, 0.3f, ep.y + 0.5f);
                marker.transform.localScale = new Vector3(3f, 0.3f, 3f);
                var r = marker.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = CreateMat(ColorEscape);
                // Remove collider so it doesn't block movement
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                escapeMarkers.Add(marker);
            }
        }

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
    //  OBSTACLES
    // ═══════════════════════════════════════════

    private void SpawnObstacles()
    {
        if (obstaclesRoot != null) return;
        obstaclesRoot = new GameObject("ObstaclesRoot");
        obstacleColliders.Clear();

        var rng = new System.Random(gameSeed != 0 ? gameSeed : 12345);
        int numBlocks = GridSize / BlockSize; // 8

        // Collect available block positions (exclude jail, spawn zones, escape corners)
        var available = new List<Vector2Int>();
        for (int bx = 0; bx < numBlocks; bx++)
        {
            for (int bz = 0; bz < numBlocks; bz++)
            {
                int cx = bx * BlockSize + BlockSize / 2;
                int cz = bz * BlockSize + BlockSize / 2;

                // Skip jail block
                if (bx == numBlocks / 2 - 1 && bz == numBlocks / 2 - 1) continue;
                if (bx == numBlocks / 2 && bz == numBlocks / 2) continue;
                if (bx == numBlocks / 2 - 1 && bz == numBlocks / 2) continue;
                if (bx == numBlocks / 2 && bz == numBlocks / 2 - 1) continue;

                // Skip spawn zones (south z<2 blocks, north z>5 blocks)
                if (bz <= 1 || bz >= numBlocks - 2) continue;

                // Skip escape corners
                bool isCorner = (bx <= 0 && bz <= 0) || (bx >= numBlocks - 1 && bz <= 0) ||
                                (bx <= 0 && bz >= numBlocks - 1) || (bx >= numBlocks - 1 && bz >= numBlocks - 1);
                if (isCorner) continue;

                available.Add(new Vector2Int(cx, cz));
            }
        }

        // Shuffle and pick
        for (int i = available.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var tmp = available[i];
            available[i] = available[j];
            available[j] = tmp;
        }

        int count = Mathf.Min(ObstacleCount, available.Count);
        for (int i = 0; i < count; i++)
        {
            var pos = available[i];
            // Random obstacle shape within block
            float w = 1f + (float)(rng.NextDouble() * 4); // width 1-5
            float d = 1f + (float)(rng.NextDouble() * 4); // depth 1-5
            float h = 2f + (float)(rng.NextDouble() * 3); // height 2-5
            float ox = (float)(rng.NextDouble() * 2 - 1); // offset within block
            float oz = (float)(rng.NextDouble() * 2 - 1);

            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = "Obstacle_" + i;
            obstacle.transform.SetParent(obstaclesRoot.transform);
            obstacle.transform.position = new Vector3(pos.x + 0.5f + ox, h * 0.5f, pos.y + 0.5f + oz);
            obstacle.transform.localScale = new Vector3(w, h, d);
            var r = obstacle.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateMat(ColorObstacle);

            var col = obstacle.GetComponent<BoxCollider>();
            if (col != null) obstacleColliders.Add(col);
        }
    }

    // ═══════════════════════════════════════════
    //  ROLE ASSIGNMENT
    // ═══════════════════════════════════════════

    private void AssignRoles()
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null) return;

        // Collect valid member IDs and sort
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

        // Determine police count
        int policeCount = memberIds.Count >= 7 ? 2 : 1;

        playerRoles.Clear();
        thiefStates.Clear();

        var policeIds = new List<string>();
        var thiefIds = new List<string>();

        for (int i = 0; i < memberIds.Count; i++)
        {
            if (i < policeCount)
            {
                playerRoles[memberIds[i]] = PlayerRole.Police;
                policeIds.Add(memberIds[i]);
            }
            else
            {
                playerRoles[memberIds[i]] = PlayerRole.Thief;
                thiefStates[memberIds[i]] = ThiefState.Free;
                thiefIds.Add(memberIds[i]);
            }
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

        // Broadcast roles as JSON (host only)
        if (isHosting)
        {
            string rolesJson = BuildRolesJson(policeIds, thiefIds);
            // Store in lobby attribute
            var update = BuildLobbyUpdate();
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeRoles, ValueType = AttributeType.String,
                AsString = rolesJson, Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
            lobbyManager.ModifyLobby(update, r => { });

            // Also broadcast via P2P for immediate sync
            byte[] jsonBytes = Encoding.UTF8.GetBytes(rolesJson);
            byte[] data = new byte[1 + jsonBytes.Length];
            data[0] = (byte)'O';
            Buffer.BlockCopy(jsonBytes, 0, data, 1, jsonBytes.Length);
            SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableOrdered);
        }

        // Position players based on role
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

    private void SpawnPlayerAtRole()
    {
        if (localPlayer == null) return;

        // Swap model to match role
        GameObject prefab = localRole == PlayerRole.Police ? policePrefab : thiefPrefab;
        SwapCharacterModel(ref localPlayer, prefab, "CRLocalPlayer");

        if (localRole == PlayerRole.Police)
        {
            // North spawn
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, 56f);
        }
        else
        {
            // South spawn
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, 8f);
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
        if (localRole == PlayerRole.Thief && localThiefState == ThiefState.Free)
        {
            // Check escape
            foreach (var ep in EscapePoints)
            {
                Vector3 epWorld = new Vector3(ep.x + 0.5f, 0, ep.y + 0.5f);
                float dist = Vector3.Distance(
                    new Vector3(localPos.x, 0, localPos.z), epWorld);

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

            // Check rescue (free thief near jail → rescue all jailed)
            Vector3 jailWorld = new Vector3(JailCenter.x + 0.5f, 0, JailCenter.y + 0.5f);
            float jailDist = Vector3.Distance(
                new Vector3(localPos.x, 0, localPos.z), jailWorld);

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
            remote.transform.position = new Vector3(JailCenter.x + 0.5f, 0f, JailCenter.y + 0.5f);
            UpdatePlayerVisual(remote, PlayerRole.Thief, ThiefState.Jailed);
        }

        // If it's local player being arrested (via received packet)
        var localId = EOSManager.Instance.GetProductUserId();
        if (localId != null && peerId == localId.ToString())
        {
            localThiefState = ThiefState.Jailed;
            localPlayer.transform.position = new Vector3(JailCenter.x + 0.5f, 0f, JailCenter.y + 0.5f);
            UpdatePlayerVisual(localPlayer, localRole, localThiefState);
        }

        CheckGameEnd();
    }

    private void RescueAllThieves()
    {
        var localId = EOSManager.Instance.GetProductUserId();
        string localStr = localId != null ? localId.ToString() : "";

        var keys = new List<string>(thiefStates.Keys);
        foreach (var key in keys)
        {
            if (thiefStates[key] == ThiefState.Jailed)
            {
                thiefStates[key] = ThiefState.Free;

                if (key == localStr)
                {
                    localThiefState = ThiefState.Free;
                    // Respawn at south
                    localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0f, 8f);
                    UpdatePlayerVisual(localPlayer, localRole, localThiefState);
                }
                else if (remotePlayers.TryGetValue(key, out GameObject remote))
                {
                    remote.transform.position = new Vector3(GridSize * 0.5f, 0f, 8f);
                    UpdatePlayerVisual(remote, PlayerRole.Thief, ThiefState.Free);
                }
            }
        }
    }

    private void CheckGameEnd()
    {
        if (gameEnded) return;

        // Check if all thieves are jailed
        bool allJailed = true;
        foreach (var kvp in thiefStates)
        {
            if (kvp.Value == ThiefState.Free)
            {
                allJailed = false;
                break;
            }
        }

        if (allJailed && thiefStates.Count > 0)
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

    private bool DidPoliceWin()
    {
        // Police win if all thieves are jailed
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

        // Obstacle collision (simple AABB)
        if (!IsBlockedByObstacle(localPlayer.transform.position, newPos))
            localPlayer.transform.position = newPos;
        else
        {
            // Try sliding along axes
            Vector3 slideX = new Vector3(newPos.x, 0f, localPlayer.transform.position.z);
            Vector3 slideZ = new Vector3(localPlayer.transform.position.x, 0f, newPos.z);

            if (!IsBlockedByObstacle(localPlayer.transform.position, slideX))
                localPlayer.transform.position = slideX;
            else if (!IsBlockedByObstacle(localPlayer.transform.position, slideZ))
                localPlayer.transform.position = slideZ;
        }
    }

    private bool IsBlockedByObstacle(Vector3 from, Vector3 to)
    {
        float playerRadius = 0.5f;
        Bounds playerBounds = new Bounds(to, new Vector3(playerRadius * 2, 1f, playerRadius * 2));

        foreach (var col in obstacleColliders)
        {
            if (col == null) continue;
            if (playerBounds.Intersects(col.bounds))
                return true;
        }
        return false;
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

        // Destroy obstacles
        if (obstaclesRoot != null) { Destroy(obstaclesRoot); obstaclesRoot = null; }
        obstacleColliders.Clear();

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
            SpawnObstacles();
            AssignRoles();
            statusMessage = "Game started! Role: " + localRole + " | Seed: " + gameSeed;
        }

        if (!gameEnded && string.Equals(gameState, "ENDED", StringComparison.OrdinalIgnoreCase))
        {
            gameEnded = true;
        }

        string seedStr2 = GetLobbyAttribute(LobbyAttributeSeed);
        if (!string.IsNullOrEmpty(seedStr2) && int.TryParse(seedStr2, out int parsed2))
            gameSeed = parsed2;

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
        if (!AreAllMembersReady() || string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase) || startUpdateInProgress) return;

        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby?.Members == null || lobby.Members.Count < 2) return; // Need at least 2 players

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
        GUILayout.Label("<b>Cops & Robbers</b>", titleStyle);
        GUILayout.Label("64x64 Grid · Max 10 Players · 3min");

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
        GUILayout.BeginArea(new Rect(Screen.width - 300, 10, 290, 300), GUI.skin.box);

        var hudTitle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUILayout.Label("COPS & ROBBERS", hudTitle);

        // Timer
        GUILayout.Space(4);
        var timerStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        timerStyle.normal.textColor = remainingTime <= 30f ? Color.red : Color.white;
        int min = (int)(remainingTime / 60);
        int sec = (int)(remainingTime % 60);
        GUILayout.Label(string.Format("{0}:{1:00}", min, sec), timerStyle);

        // Police delay warning
        if (localRole == PlayerRole.Police && !gameEnded)
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

    private void DrawResultBanner()
    {
        Texture2D overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.6f));
        overlayTex.Apply();
        GUI.DrawTexture(new Rect(0, Screen.height * 0.25f, Screen.width, Screen.height * 0.45f), overlayTex);

        bool policeWon = DidPoliceWin();
        float centerY = Screen.height * 0.3f;

        // Winner title
        var bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
        };

        var subStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
        };
        subStyle.normal.textColor = Color.white;

        if (policeWon)
        {
            bigStyle.normal.textColor = Color.cyan;
            GUI.Label(new Rect(0, centerY, Screen.width, 60), "POLICE WIN!", bigStyle);
            GUI.Label(new Rect(0, centerY + 65, Screen.width, 40), "All thieves captured!", subStyle);
        }
        else
        {
            bigStyle.normal.textColor = Color.red;
            GUI.Label(new Rect(0, centerY, Screen.width, 60), "THIEVES WIN!", bigStyle);

            int escaped = 0, free = 0;
            foreach (var kvp in thiefStates)
            {
                if (kvp.Value == ThiefState.Escaped) escaped++;
                else if (kvp.Value == ThiefState.Free) free++;
            }
            string detail = escaped > 0 ? escaped + " escaped!" : free + " survived!";
            GUI.Label(new Rect(0, centerY + 65, Screen.width, 40), detail, subStyle);
        }

        // Your result
        var personalStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22, alignment = TextAnchor.MiddleCenter
        };

        string personal;
        if (localRole == PlayerRole.Police)
        {
            personalStyle.normal.textColor = policeWon ? Color.green : Color.red;
            personal = policeWon ? "Mission Complete!" : "Mission Failed...";
        }
        else
        {
            if (localThiefState == ThiefState.Escaped)
            {
                personalStyle.normal.textColor = Color.green;
                personal = "You Escaped!";
            }
            else if (localThiefState == ThiefState.Free)
            {
                personalStyle.normal.textColor = Color.green;
                personal = "You Survived!";
            }
            else
            {
                personalStyle.normal.textColor = Color.red;
                personal = "You Got Caught...";
            }
        }
        GUI.Label(new Rect(0, centerY + 120, Screen.width, 35), personal, personalStyle);
    }
}

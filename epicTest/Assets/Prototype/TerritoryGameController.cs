using System;
using System.Collections.Generic;
using UnityEngine;
using PlayEveryWare.EpicOnlineServices;
using PlayEveryWare.EpicOnlineServices.Samples;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Sessions;

/// <summary>
/// Territory capture game on a 128x128 grid.
/// 9 yellow cubes are placed randomly. Walking onto one claims a 3x3 area.
/// Local territory = green, remote territory = red.
/// Based on EosPrototypeController (original file is untouched).
/// </summary>
public class TerritoryGameController : MonoBehaviour
{
    // ───── EOS Constants ─────
    private const string LobbyAttributeCode = "CODE";
    private const string LobbyAttributeSessionId = "SESSIONID";
    private const string LobbyAttributeState = "STATE";
    private const string LobbyAttributeSeed = "SEED";
    private const string LobbyMemberReadyKey = "READY";
    private const string P2PSocketName = "TERRITORY";

    // ───── Grid Constants ─────
    private const int GridSize = 64;
    private const int CubeCount = 15;
    private const float CellSize = 1f;
    private const float CaptureRadius = 1.5f;
    private const int BlockSize = 8; // territory block = 8x8, aligned to grid lines

    // ───── EOS Managers ─────
    private EOSLobbyManager lobbyManager;
    private EOSSessionsManager sessionsManager;
    private P2PInterface p2p;
    private ulong connectionNotifyId;
    private bool isQuitting;

    // ───── State ─────
    private bool isLoggedIn;
    private bool isHosting;
    private bool isSearchingLobbies;
    private bool isReady;
    private bool gameStarted;
    private bool stateDirty;
    private bool startUpdateInProgress;

    private string displayName;
    private string lobbyCode = "TERR";
    private string statusMessage = "";
    private string gameState = "WAITING";
    private string devAuthAddress = "localhost:6547";
    private string devAuthCredential = "";
    private int gameSeed;

    private float sendInterval = 0.1f;
    private float lastSendTime;

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

    // ───── Territory Grid ─────
    // 0 = neutral, 1 = local player, 2 = remote player
    private int[,] gridOwner;
    private Texture2D gridTexture;
    private GameObject groundObj;
    private Material groundMaterial;
    private bool gridDirty;

    private static readonly Color ColorNeutral = new Color(0.3f, 0.3f, 0.3f, 1f);
    private static readonly Color ColorLocal = new Color(0.15f, 0.75f, 0.2f, 1f);
    private static readonly Color ColorRemote = new Color(0.85f, 0.15f, 0.15f, 1f);
    private static readonly Color ColorGridLine = new Color(0.25f, 0.25f, 0.25f, 1f);

    // ───── Shader Mode ─────
    public enum ShaderMode { Standard, Jelly, Clay }
    [Header("Shader Mode")]
    public ShaderMode shaderMode = ShaderMode.Jelly;

    // ───── Capture Cubes ─────
    private GameObject cubesRoot;
    private readonly List<GameObject> captureCubes = new List<GameObject>();
    private readonly List<Vector2Int> cubeGridPositions = new List<Vector2Int>();
    private readonly HashSet<int> capturedCubes = new HashSet<int>();
    // Track who captured each cube: true = local, false = remote
    private readonly Dictionary<int, bool> cubeOwners = new Dictionary<int, bool>();

    private int localTerritoryCount;
    private int remoteTerritoryCount;

    // ───── Camera ─────
    private Vector3 cameraOffset = new Vector3(0f, 30f, -20f);

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
        displayName = "Player" + UnityEngine.Random.Range(1000, 9999);
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
        UpdateCubeCapture();
        UpdateRemotePlayers();
        UpdateCamera();
        SendLocalPositionIfNeeded();
        SendStateIfDirty();
        ReceivePackets();

        if (gridDirty)
        {
            ApplyGridTexture();
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
        gridOwner = new int[GridSize, GridSize];
        gridTexture = new Texture2D(GridSize, GridSize, TextureFormat.RGBA32, false);
        gridTexture.filterMode = FilterMode.Point;
        gridTexture.wrapMode = TextureWrapMode.Clamp;

        // Fill neutral
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                gridOwner[x, z] = 0;
                gridTexture.SetPixel(x, z, ColorNeutral);
            }
        }

        // Draw subtle grid lines every 8 cells
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                if (x % 8 == 0 || z % 8 == 0)
                {
                    gridTexture.SetPixel(x, z, ColorGridLine);
                }
            }
        }

        gridTexture.Apply();
    }

    private void ApplyGridTexture()
    {
        for (int x = 0; x < GridSize; x++)
        {
            for (int z = 0; z < GridSize; z++)
            {
                Color c;
                switch (gridOwner[x, z])
                {
                    case 1: c = ColorLocal; break;
                    case 2: c = ColorRemote; break;
                    default:
                        c = (x % 8 == 0 || z % 8 == 0) ? ColorGridLine : ColorNeutral;
                        break;
                }
                gridTexture.SetPixel(x, z, c);
            }
        }
        gridTexture.Apply();

        // Update emission map so territory colors are always visible
        if (groundMaterial != null)
        {
            groundMaterial.SetTexture("_EmissionMap", gridTexture);
        }
    }

    private void EnsureWorld()
    {
        // ── Directional Light (ensure scene is lit) ──
        if (GameObject.Find("TerritoryLight") == null)
        {
            var lightGo = new GameObject("TerritoryLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        // ── Ground (128x128 units) ── using custom mesh for exact UV-to-world mapping
        groundObj = GameObject.Find("TerritoryGround");
        if (groundObj == null)
        {
            groundObj = new GameObject("TerritoryGround");
            groundObj.transform.position = Vector3.zero;

            // Build a flat quad mesh: world (0,0,0)→UV(0,0), world (128,0,128)→UV(1,1)
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(GridSize, 0, 0),
                new Vector3(0, 0, GridSize),
                new Vector3(GridSize, 0, GridSize)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            };
            mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            mesh.normals = new Vector3[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up
            };

            var meshFilter = groundObj.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;
            var meshRenderer = groundObj.AddComponent<MeshRenderer>();

            // Add a MeshCollider so raycasts/physics work on the ground
            var collider = groundObj.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;

            // Use Standard shader with emission for always-visible territory colors
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            groundMaterial = new Material(shader);
            groundMaterial.mainTexture = gridTexture;
            groundMaterial.EnableKeyword("_EMISSION");
            groundMaterial.SetTexture("_EmissionMap", gridTexture);
            groundMaterial.SetColor("_EmissionColor", Color.white * 0.6f);
            meshRenderer.sharedMaterial = groundMaterial;
        }

        // ── Local Player ──
        if (localPlayer == null)
        {
            localPlayer = GameObject.Find("TerritoryLocalPlayer");
        }
        if (localPlayer == null)
        {
            localPlayer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            localPlayer.name = "TerritoryLocalPlayer";
            localPlayer.transform.position = new Vector3(GridSize * 0.5f, 0.5f, GridSize * 0.25f);
            localPlayer.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
            var r = localPlayer.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateMat(new Color(0.2f, 0.95f, 0.3f, 1f));
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
    //  CAPTURE CUBES
    // ═══════════════════════════════════════════

    private void EnsureCaptureCubes()
    {
        if (cubesRoot == null)
        {
            cubesRoot = new GameObject("CaptureCubesRoot");
        }

        if (captureCubes.Count > 0) return;

        var rng = new System.Random(gameSeed != 0 ? gameSeed : 54321);
        int numBlocks = GridSize / BlockSize; // 64/8 = 8 blocks per axis

        // Collect all available block indices and shuffle to pick 9
        List<int> blockIndices = new List<int>();
        for (int b = 0; b < numBlocks * numBlocks; b++)
            blockIndices.Add(b);
        // Fisher-Yates shuffle
        for (int i = blockIndices.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            int tmp = blockIndices[i];
            blockIndices[i] = blockIndices[j];
            blockIndices[j] = tmp;
        }

        for (int i = 0; i < CubeCount && i < blockIndices.Count; i++)
        {
            int blockIdx = blockIndices[i];
            int bx = blockIdx % numBlocks;
            int bz = blockIdx / numBlocks;

            // Grid position = center of the 8x8 block
            int gx = bx * BlockSize + BlockSize / 2;
            int gz = bz * BlockSize + BlockSize / 2;

            cubeGridPositions.Add(new Vector2Int(gx, gz));

            // World position at block center
            Vector3 worldPos = GridToWorld(gx, gz);
            worldPos.y = 0.75f;

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "CaptureCube_" + i;
            cube.transform.position = worldPos;
            cube.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
            cube.transform.SetParent(cubesRoot.transform);
            var r = cube.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateMat(new Color(1f, 0.9f, 0.1f, 1f));
            captureCubes.Add(cube);
        }
    }

    private Vector3 GridToWorld(int gx, int gz)
    {
        return new Vector3(gx + 0.5f, 0f, gz + 0.5f);
    }

    private Vector2Int WorldToGrid(Vector3 pos)
    {
        int gx = Mathf.Clamp(Mathf.FloorToInt(pos.x), 0, GridSize - 1);
        int gz = Mathf.Clamp(Mathf.FloorToInt(pos.z), 0, GridSize - 1);
        return new Vector2Int(gx, gz);
    }

    private void UpdateCubeCapture()
    {
        if (!gameStarted || localPlayer == null) return;

        Vector3 playerPos = localPlayer.transform.position;

        for (int i = 0; i < captureCubes.Count; i++)
        {
            if (capturedCubes.Contains(i)) continue;

            var cube = captureCubes[i];
            if (cube == null) continue;

            float dist = Vector3.Distance(
                new Vector3(playerPos.x, 0, playerPos.z),
                new Vector3(cube.transform.position.x, 0, cube.transform.position.z));

            if (dist <= CaptureRadius)
            {
                CaptureCube(i, true);
                BroadcastCapture(i);
            }
        }
    }

    private void CaptureCube(int index, bool isLocal)
    {
        if (capturedCubes.Contains(index)) return;

        capturedCubes.Add(index);
        cubeOwners[index] = isLocal;

        // Hide the cube
        if (index >= 0 && index < captureCubes.Count && captureCubes[index] != null)
        {
            // Change color to indicate captured (green or red), then shrink
            var r = captureCubes[index].GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = CreateMat(isLocal ? ColorLocal : ColorRemote);
            }
            // Keep it visible but flat as a marker
            captureCubes[index].transform.localScale = new Vector3(0.9f, 0.15f, 0.9f);
            captureCubes[index].transform.position = new Vector3(
                captureCubes[index].transform.position.x,
                0.08f,
                captureCubes[index].transform.position.z);
        }

        // Claim the entire 8x8 block that this cube belongs to
        Vector2Int center = cubeGridPositions[index];
        int blockStartX = (center.x / BlockSize) * BlockSize;
        int blockStartZ = (center.y / BlockSize) * BlockSize;
        int ownerValue = isLocal ? 1 : 2;

        for (int dx = 0; dx < BlockSize; dx++)
        {
            for (int dz = 0; dz < BlockSize; dz++)
            {
                int gx = blockStartX + dx;
                int gz = blockStartZ + dz;
                if (gx >= 0 && gx < GridSize && gz >= 0 && gz < GridSize)
                {
                    int prev = gridOwner[gx, gz];
                    gridOwner[gx, gz] = ownerValue;

                    if (prev == 1) localTerritoryCount--;
                    else if (prev == 2) remoteTerritoryCount--;

                    if (ownerValue == 1) localTerritoryCount++;
                    else remoteTerritoryCount++;
                }
            }
        }

        gridDirty = true;
        stateDirty = true;
    }

    // ═══════════════════════════════════════════
    //  PLAYER MOVEMENT
    // ═══════════════════════════════════════════

    private void UpdateLocalMovement()
    {
        if (localPlayer == null || !gameStarted) return;

        float speed = 12f; // faster for 128x128 map
        float moveX = Input.GetAxisRaw("Horizontal");
        float moveZ = Input.GetAxisRaw("Vertical");
        Vector3 delta = new Vector3(moveX, 0f, moveZ).normalized * speed * Time.deltaTime;
        Vector3 newPos = localPlayer.transform.position + delta;

        // Clamp to grid bounds
        newPos.x = Mathf.Clamp(newPos.x, 0.5f, GridSize - 0.5f);
        newPos.z = Mathf.Clamp(newPos.z, 0.5f, GridSize - 0.5f);
        newPos.y = 0.5f;

        localPlayer.transform.position = newPos;
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
            remote = GameObject.CreatePrimitive(PrimitiveType.Cube);
            remote.name = "TerritoryRemote_" + peerId;
            remote.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
            var r = remote.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateMat(new Color(0.95f, 0.3f, 0.2f, 1f));
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

            float age = Mathf.Clamp(Time.time - state.LastRecvTime, 0f, maxPredict);
            Vector3 predicted = state.Target + state.Velocity * age;
            Vector3 current = remote.transform.position;

            if (Vector3.Distance(current, predicted) > snapDist)
            {
                remote.transform.position = predicted;
                continue;
            }

            float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
            remote.transform.position = Vector3.Lerp(current, predicted, t);
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

    private void BroadcastCapture(int cubeIndex)
    {
        byte[] data = new byte[5];
        data[0] = (byte)'T'; // Territory capture
        Buffer.BlockCopy(BitConverter.GetBytes(cubeIndex), 0, data, 1, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void SendStateIfDirty()
    {
        if (!stateDirty || !isLoggedIn || !IsInLobby()) return;
        stateDirty = false;

        // Send territory count as state
        byte[] data = new byte[9];
        data[0] = (byte)'S';
        Buffer.BlockCopy(BitConverter.GetBytes(localTerritoryCount), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(capturedCubes.Count), 0, data, 5, 4);
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
            {
                safety++; continue;
            }
            if (bytesWritten == 0) { safety++; continue; }

            byte type = buffer[0];

            if (type == (byte)'P' && bytesWritten >= 13)
            {
                float x = BitConverter.ToSingle(buffer, 1);
                float y = BitConverter.ToSingle(buffer, 5);
                float z = BitConverter.ToSingle(buffer, 9);
                UpdateRemotePlayer(peerId.ToString(), new Vector3(x, y, z));
            }
            else if (type == (byte)'T' && bytesWritten >= 5)
            {
                int cubeIdx = BitConverter.ToInt32(buffer, 1);
                if (captureCubes.Count == 0) EnsureCaptureCubes();
                if (!capturedCubes.Contains(cubeIdx) && cubeIdx >= 0 && cubeIdx < CubeCount)
                {
                    CaptureCube(cubeIdx, false); // remote player captured
                }
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
    //  EOS LOGIN / LOBBY (same pattern as original)
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
            BucketId = "TERRITORY",
            MaxNumLobbyMembers = 2,
            LobbyPermissionLevel = Epic.OnlineServices.Lobby.LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = true
        };

        lobbyManager.CreateLobby(lobby, result =>
        {
            if (result != Result.Success) { statusMessage = "Create lobby failed: " + result; return; }
            SetReady(false);
            var update = BuildLobbyUpdate();
            lobbyManager.ModifyLobby(update, r => { statusMessage = r == Result.Success ? "Lobby created (Code: " + lobbyCode + ")" : "Lobby update fail"; });
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
        gameState = "WAITING";
        localTerritoryCount = 0;
        remoteTerritoryCount = 0;
        stateDirty = true;
        startUpdateInProgress = false;
        capturedCubes.Clear();
        cubeOwners.Clear();
        captureCubes.Clear();
        cubeGridPositions.Clear();
        if (cubesRoot != null) { Destroy(cubesRoot); cubesRoot = null; }

        // Reset grid
        if (gridOwner != null)
        {
            for (int x = 0; x < GridSize; x++)
                for (int z = 0; z < GridSize; z++)
                    gridOwner[x, z] = 0;
            gridDirty = true;
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
            EnsureCaptureCubes();
        }

        if (gameStarted && string.Equals(gameState, "WAITING", StringComparison.OrdinalIgnoreCase))
            gameStarted = false;

        string seedStr = GetLobbyAttribute(LobbyAttributeSeed);
        if (!string.IsNullOrEmpty(seedStr) && int.TryParse(seedStr, out int parsed))
            gameSeed = parsed;
    }

    private void UpdateReadyState()
    {
        if (!IsInLobby() || !isHosting) return;
        if (!AreAllMembersReady() || string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase) || startUpdateInProgress) return;

        startUpdateInProgress = true;
        var update = BuildLobbyUpdate();
        update.Attributes.Add(new LobbyAttribute
        {
            Key = LobbyAttributeState, ValueType = AttributeType.String, AsString = "STARTED",
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        });
        lobbyManager.ModifyLobby(update, r => { startUpdateInProgress = false; if (r == Result.Success) statusMessage = "Game started!"; });
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
        GUILayout.BeginArea(new Rect(10, 10, 380, 550), GUI.skin.box);
        GUILayout.Label("<b>Territory Capture Game</b>", new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 });
        GUILayout.Label("64x64 Grid · 15 Cubes · 8x8 Block Capture");

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
            GUILayout.Label("─── Lobby Results ───");
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
            GUILayout.Label("─── Lobby ───");
            GUILayout.Label("State: " + gameState + " | Ready: " + (isReady ? "Yes" : "No"));
            GUILayout.Label("Members: " + lobby.Members.Count + "/2");
        }

        GUILayout.EndArea();

        // ── Right Panel: Game Score (when playing) ──
        if (gameStarted)
        {
            GUILayout.BeginArea(new Rect(Screen.width - 260, 10, 250, 180), GUI.skin.box);

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("TERRITORY", titleStyle);
            GUILayout.Space(4);

            // Local score
            var greenStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            greenStyle.normal.textColor = Color.green;
            GUILayout.Label("■ My Territory: " + localTerritoryCount + " cells", greenStyle);

            // Remote score
            var redStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            redStyle.normal.textColor = Color.red;
            GUILayout.Label("■ Enemy Territory: " + remoteTerritoryCount + " cells", redStyle);

            GUILayout.Space(4);
            int remaining = CubeCount - capturedCubes.Count;
            GUILayout.Label("Cubes remaining: " + remaining + "/" + CubeCount);

            if (remaining == 0)
            {
                var resultStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                if (localTerritoryCount > remoteTerritoryCount)
                {
                    resultStyle.normal.textColor = Color.green;
                    GUILayout.Label("YOU WIN!", resultStyle);
                }
                else if (localTerritoryCount < remoteTerritoryCount)
                {
                    resultStyle.normal.textColor = Color.red;
                    GUILayout.Label("YOU LOSE!", resultStyle);
                }
                else
                {
                    resultStyle.normal.textColor = Color.yellow;
                    GUILayout.Label("DRAW!", resultStyle);
                }
            }

            GUILayout.EndArea();

            // Mini instruction
            GUI.Label(new Rect(Screen.width * 0.5f - 120, Screen.height - 40, 240, 30),
                "WASD to move · Reach yellow cubes to capture!");

            // ── BIG CENTER WINNER BANNER ──
            if (remaining == 0)
            {
                DrawWinnerBanner();
            }
        }
    }

    private void DrawWinnerBanner()
    {
        // Semi-transparent dark overlay
        Texture2D overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0, 0, 0, 0.5f));
        overlayTex.Apply();
        GUI.DrawTexture(new Rect(0, Screen.height * 0.3f, Screen.width, Screen.height * 0.35f), overlayTex);

        string winnerName;
        Color bannerColor;

        if (localTerritoryCount > remoteTerritoryCount)
        {
            winnerName = displayName;
            bannerColor = Color.green;
        }
        else if (localTerritoryCount < remoteTerritoryCount)
        {
            // Find remote player name
            winnerName = "Opponent";
            foreach (var kvp in remotePlayers)
            {
                winnerName = kvp.Key.Length > 8 ? kvp.Key.Substring(0, 8) + "..." : kvp.Key;
                break;
            }
            bannerColor = Color.red;
        }
        else
        {
            winnerName = "DRAW";
            bannerColor = Color.yellow;
        }

        // Winner title - big
        var bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 52,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        bigStyle.normal.textColor = bannerColor;

        // Score subtitle
        var subStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        subStyle.normal.textColor = Color.white;

        float centerY = Screen.height * 0.35f;

        if (localTerritoryCount != remoteTerritoryCount)
        {
            GUI.Label(new Rect(0, centerY, Screen.width, 70), "WINNER", bigStyle);
            GUI.Label(new Rect(0, centerY + 65, Screen.width, 50), winnerName, subStyle);
        }
        else
        {
            GUI.Label(new Rect(0, centerY, Screen.width, 70), "DRAW!", bigStyle);
        }

        // Score line
        var scoreStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            alignment = TextAnchor.MiddleCenter
        };
        scoreStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(0, centerY + 120, Screen.width, 40),
            localTerritoryCount + " vs " + remoteTerritoryCount + " cells", scoreStyle);
    }
}

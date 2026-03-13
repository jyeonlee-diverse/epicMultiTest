using System;
using System.Collections.Generic;
using UnityEngine;
using PlayEveryWare.EpicOnlineServices;
using PlayEveryWare.EpicOnlineServices.Samples;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Sessions;

public class EosPrototypeController : MonoBehaviour
{
    private const string LobbyAttributeCode = "CODE";
    private const string LobbyAttributeSessionId = "SESSIONID";
    private const string LobbyAttributeState = "STATE";
    private const string LobbyAttributeSeed = "SEED";
    private const string LobbyMemberReadyKey = "READY";
    private const string P2PSocketName = "PROTO";

    private EOSLobbyManager lobbyManager;
    private EOSSessionsManager sessionsManager;
    private P2PInterface p2p;
    private ulong connectionNotifyId;
    private bool isQuitting;

    private bool isLoggedIn;
    private bool isHosting;
    private bool isSearchingLobbies;
    private bool isSearchingSession;
    private bool isReady;
    private bool gameStarted;
    private bool stateDirty;
    private bool startUpdateInProgress;
    private bool useSessions = false;

    private string displayName;
    private string lobbyCode = "TEST";
    private string statusMessage = "";
    private string gameState = "WAITING";
    private string devAuthAddress = "localhost:6547";
    private string devAuthCredential = "";

    private int gameSeed;
    private int localScore;
    private int localHp = 100;

    private string hostingSessionName = "";
    private string hostingSessionId = "";
    private string pendingJoinSessionId = "";

    private float sendInterval = 0.1f;
    private float lastSendTime;

    private GameObject localPlayer;
    private readonly Dictionary<string, GameObject> remotePlayers = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, RemoteMotionState> remoteStates = new Dictionary<string, RemoteMotionState>();

    private struct RemoteMotionState
    {
        public Vector3 Target;
        public Vector3 Velocity;
        public float LastRecvTime;
    }
    private readonly Dictionary<string, int> remoteScores = new Dictionary<string, int>();
    private readonly Dictionary<string, int> remoteHps = new Dictionary<string, int>();

    private GameObject coinsRoot;
    private readonly List<GameObject> coins = new List<GameObject>();
    private readonly HashSet<int> collectedCoins = new HashSet<int>();
    private const int CoinCount = 10;
    private const float CoinPickupRadius = 1.0f;

    private class LobbySearchEntry
    {
        public Lobby Lobby;
        public Epic.OnlineServices.Lobby.LobbyDetails Details;
    }

    private readonly List<LobbySearchEntry> lobbyResults = new List<LobbySearchEntry>();

    private void Awake()
    {
        displayName = "Player" + UnityEngine.Random.Range(1000, 9999);
    }

    private void Start()
    {
        EnsureWorld();
    }

    private void Update()
    {
        TryResolveManagers();
        if (useSessions)
        {
            TryResolveSessionId();
            TryJoinPendingSession();
        }
        UpdateLobbyState();
        UpdateReadyState();
        UpdateLocalMovement();
        HandleDebugInput();
        UpdateCoins();
        UpdateRemotePlayers();
        SendLocalPositionIfNeeded();
        SendStateIfDirty();
        ReceivePackets();
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
    }

    private void OnDestroy()
    {
        if (isQuitting)
        {
            return;
        }

        if (p2p != null && connectionNotifyId != 0)
        {
            p2p.RemoveNotifyPeerConnectionRequest(connectionNotifyId);
            connectionNotifyId = 0;
        }
    }

    private void TryResolveManagers()
    {
        if (lobbyManager == null)
        {
            lobbyManager = EOSManager.Instance.GetOrCreateManager<EOSLobbyManager>();
        }

        if (sessionsManager == null)
        {
            sessionsManager = EOSManager.Instance.GetOrCreateManager<EOSSessionsManager>();
        }

        if (p2p == null)
        {
            p2p = EOSManager.Instance.GetEOSP2PInterface();
        }

        if (p2p != null && connectionNotifyId == 0 && isLoggedIn)
        {
            var socketId = new SocketId { SocketName = P2PSocketName };
            var options = new AddNotifyPeerConnectionRequestOptions
            {
                LocalUserId = EOSManager.Instance.GetProductUserId(),
                SocketId = socketId
            };
            connectionNotifyId = p2p.AddNotifyPeerConnectionRequest(ref options, null, OnIncomingConnectionRequest);
        }
    }

    private Material CreateSimpleMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Legacy Shaders/Diffuse");
        }
        var material = new Material(shader != null ? shader : Shader.Find("Unlit/Color"));
        material.color = color;
        return material;
    }
    private void EnsureWorld()
    {
        var ground = GameObject.Find("Ground");
        if (ground == null)
        {
            ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2f, 1f, 2f);
            var groundRenderer = ground.GetComponent<Renderer>();
            if (groundRenderer != null)
            {
                groundRenderer.sharedMaterial = CreateSimpleMaterial(new Color(0.35f, 0.35f, 0.35f, 1f));
            }
        }

        var cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = new Vector3(0f, 6f, -8f);
            cam.transform.rotation = Quaternion.Euler(30f, 0f, 0f);
        }

        if (localPlayer == null)
        {
            localPlayer = GameObject.Find("LocalPlayer");
        }

        if (localPlayer == null)
        {
            localPlayer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            localPlayer.name = "LocalPlayer";
            localPlayer.transform.position = new Vector3(0f, 0.5f, 0f);
            var renderer = localPlayer.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateSimpleMaterial(new Color(0.2f, 0.9f, 0.2f, 1f));
            }
        }
    }

    private void UpdateLocalMovement()
    {
        if (localPlayer == null)
        {
            return;
        }

        if (!gameStarted)
        {
            return;
        }

        float speed = 4f;
        float moveX = Input.GetAxisRaw("Horizontal");
        float moveZ = Input.GetAxisRaw("Vertical");
        Vector3 delta = new Vector3(moveX, 0f, moveZ).normalized * speed * Time.deltaTime;
        localPlayer.transform.position += delta;
    }

    private void HandleDebugInput()
    {
        if (!gameStarted)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.H))
        {
            localHp = Mathf.Max(0, localHp - 10);
            stateDirty = true;
        }

        if (Input.GetKeyDown(KeyCode.J))
        {
            localHp = Mathf.Min(100, localHp + 10);
            stateDirty = true;
        }
    }

    private void SetReady(bool ready)
    {
        if (!IsInLobby())
        {
            return;
        }

        isReady = ready;
        var attr = new LobbyAttribute
        {
            Key = LobbyMemberReadyKey,
            ValueType = AttributeType.Boolean,
            AsBool = ready,
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        };
        lobbyManager.SetMemberAttribute(attr);
    }

    private void ResetGameState()
    {
        gameStarted = false;
        gameState = "WAITING";
        localScore = 0;
        localHp = 100;
        stateDirty = true;
        startUpdateInProgress = false;
        collectedCoins.Clear();
        coins.Clear();
        if (coinsRoot != null)
        {
            Destroy(coinsRoot);
            coinsRoot = null;
        }
        remoteScores.Clear();
        remoteHps.Clear();
    }
    private void SendLocalPositionIfNeeded()
    {
        if (!isLoggedIn || !IsInLobby())
        {
            return;
        }

        if (!gameStarted)
        {
            return;
        }

        if (Time.time - lastSendTime < sendInterval)
        {
            return;
        }

        lastSendTime = Time.time;

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid())
        {
            return;
        }

        var members = lobbyManager.GetCurrentLobby().Members;
        if (members == null)
        {
            return;
        }

        foreach (var member in members)
        {
            if (member == null || member.ProductId == null || !member.ProductId.IsValid())
            {
                continue;
            }

            if (member.ProductId.ToString() == localId.ToString())
            {
                continue;
            }

            SendPosition(member.ProductId, localPlayer.transform.position);
        }
    }

    private void SendPosition(ProductUserId remoteUserId, Vector3 position)
    {
        if (p2p == null)
        {
            return;
        }

        byte[] data = new byte[13];
        data[0] = (byte)'P';
        Buffer.BlockCopy(BitConverter.GetBytes(position.x), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(position.y), 0, data, 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(position.z), 0, data, 9, 4);

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

    private void ReceivePackets()
    {
        if (!isLoggedIn || p2p == null)
        {
            return;
        }

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid())
        {
            return;
        }

        int safety = 0;
        while (safety < 20)
        {
            var sizeOptions = new GetNextReceivedPacketSizeOptions
            {
                LocalUserId = localId,
                RequestedChannel = null
            };

            p2p.GetNextReceivedPacketSize(ref sizeOptions, out uint nextSize);
            if (nextSize == 0)
            {
                return;
            }

            byte[] buffer = new byte[nextSize];
            var segment = new ArraySegment<byte>(buffer);
            ProductUserId peerId = null;
            SocketId socketId = default;
            var receiveOptions = new ReceivePacketOptions
            {
                LocalUserId = localId,
                MaxDataSizeBytes = nextSize,
                RequestedChannel = null
            };
            Result result = p2p.ReceivePacket(ref receiveOptions, ref peerId, ref socketId, out byte channel, segment, out uint bytesWritten);

            if (result != Result.Success)
            {
                return;
            }

            if (peerId == null || !peerId.IsValid() || socketId.SocketName != P2PSocketName)
            {
                safety++;
                continue;
            }

            if (bytesWritten == 0)
            {
                safety++;
                continue;
            }

            byte type = buffer[0];
            if (type == (byte)'P' && bytesWritten >= 13)
            {
                float x = BitConverter.ToSingle(buffer, 1);
                float y = BitConverter.ToSingle(buffer, 5);
                float z = BitConverter.ToSingle(buffer, 9);
                UpdateRemotePlayer(peerId.ToString(), new Vector3(x, y, z));
            }
            else if (type == (byte)'S' && bytesWritten >= 9)
            {
                int score = BitConverter.ToInt32(buffer, 1);
                int hp = BitConverter.ToInt32(buffer, 5);
                string key = peerId.ToString();
                remoteScores[key] = score;
                remoteHps[key] = hp;
            }
            else if (type == (byte)'C' && bytesWritten >= 17)
            {
                int index = BitConverter.ToInt32(buffer, 1);
                if (coins.Count == 0)
                {
                    EnsureCoins();
                }
                if (!collectedCoins.Contains(index))
                {
                    collectedCoins.Add(index);
                    if (index >= 0 && index < coins.Count && coins[index] != null)
                    {
                        coins[index].SetActive(false);
                    }
                }
            }

            safety++;
        }
    }

    private void UpdateRemotePlayer(string peerId, Vector3 position)
    {
        if (!remotePlayers.TryGetValue(peerId, out GameObject remote))
        {
            remote = GameObject.CreatePrimitive(PrimitiveType.Cube);
            remote.name = "Remote_" + peerId;
            var renderer = remote.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateSimpleMaterial(new Color(0.2f, 0.6f, 1f, 1f));
            }
            remotePlayers.Add(peerId, remote);
        }

        if (!remoteStates.TryGetValue(peerId, out RemoteMotionState state))
        {
            state = new RemoteMotionState
            {
                Target = position,
                LastRecvTime = Time.time,
                Velocity = Vector3.zero
            };
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
        if (remotePlayers.Count == 0)
        {
            return;
        }

        const float smooth = 12f;
        const float maxPredict = 0.15f;
        const float snapDistance = 2.5f;

        foreach (var kvp in remotePlayers)
        {
            string peerId = kvp.Key;
            GameObject remote = kvp.Value;
            if (remote == null)
            {
                continue;
            }

            if (!remoteStates.TryGetValue(peerId, out RemoteMotionState state))
            {
                continue;
            }

            float age = Mathf.Clamp(Time.time - state.LastRecvTime, 0f, maxPredict);
            Vector3 predicted = state.Target + state.Velocity * age;
            Vector3 current = remote.transform.position;

            if (Vector3.Distance(current, predicted) > snapDistance)
            {
                remote.transform.position = predicted;
                continue;
            }

            float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
            remote.transform.position = Vector3.Lerp(current, predicted, t);
        }
    }

    private void OnIncomingConnectionRequest(ref OnIncomingConnectionRequestInfo data)
    {
        if (p2p == null)
        {
            return;
        }

        if (!data.SocketId.HasValue || data.SocketId.Value.SocketName != P2PSocketName)
        {
            return;
        }

        var socketId = data.SocketId.Value;
        var acceptOptions = new AcceptConnectionOptions
        {
            LocalUserId = EOSManager.Instance.GetProductUserId(),
            RemoteUserId = data.RemoteUserId,
            SocketId = socketId
        };

        p2p.AcceptConnection(ref acceptOptions);
    }

    private void StartLogin()
    {
        statusMessage = "Logging in (Dev Auth)...";
        TryResolveManagers();
        StartAuthLogin();
    }

    private void StartAuthLogin()
    {
        var authInterface = EOSManager.Instance.GetEOSAuthInterface();
        if (authInterface == null)
        {
            statusMessage = "Auth interface unavailable";
            return;
        }

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
                else
                {
                    statusMessage = "Create user failed: " + createInfo.ResultCode;
                }
            });
            return;
        }

        statusMessage = "Connect login failed: " + info.ResultCode;
    }

    private void Host()
    {
        if (!isLoggedIn)
        {
            statusMessage = "Login first";
            return;
        }

        isHosting = true;
        hostingSessionName = "ProtoSession_" + UnityEngine.Random.Range(1000, 9999);
        hostingSessionId = "";
        gameSeed = UnityEngine.Random.Range(10000, 99999);
        ResetGameState();

        CreateLobby();
        if (useSessions)
        {
            CreateSession();
        }
    }

    private void CreateLobby()
    {
        var lobby = new Lobby
        {
            BucketId = "PROTO",
            MaxNumLobbyMembers = 4,
            LobbyPermissionLevel = Epic.OnlineServices.Lobby.LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = true
        };

        lobbyManager.CreateLobby(lobby, result =>
        {
            if (result != Result.Success)
            {
                statusMessage = "Create lobby failed: " + result;
                return;
            }

            SetReady(false);
            var update = BuildLobbyAttributeUpdate();
            lobbyManager.ModifyLobby(update, modifyResult =>
            {
                if (modifyResult == Result.Success)
                {
                    statusMessage = "Lobby created";
                }
                else
                {
                    statusMessage = "Lobby update failed: " + modifyResult;
                }
            });
        });
    }

    private Lobby BuildLobbyAttributeUpdate()
    {
        var current = lobbyManager.GetCurrentLobby();
        var update = new Lobby
        {
            BucketId = current.BucketId,
            LobbyPermissionLevel = current.LobbyPermissionLevel,
            AllowInvites = current.AllowInvites
        };

        update.Attributes.Add(new LobbyAttribute
        {
            Key = LobbyAttributeCode,
            ValueType = AttributeType.String,
            AsString = lobbyCode,
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        });

        update.Attributes.Add(new LobbyAttribute
        {
            Key = LobbyAttributeState,
            ValueType = AttributeType.String,
            AsString = gameState,
            Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
        });

        if (gameSeed != 0)
        {
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeSeed,
                ValueType = AttributeType.String,
                AsString = gameSeed.ToString(),
                Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
        }

        if (useSessions && !string.IsNullOrEmpty(hostingSessionId))
        {
            update.Attributes.Add(new LobbyAttribute
            {
                Key = LobbyAttributeSessionId,
                ValueType = AttributeType.String,
                AsString = hostingSessionId,
                Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
            });
        }

        return update;
    }

    private void CreateSession()
    {
        var session = new Session
        {
            Name = hostingSessionName,
            MaxPlayers = 4,
            AllowJoinInProgress = true,
            InvitesAllowed = true,
            SanctionsEnabled = false,
            PermissionLevel = OnlineSessionPermissionLevel.PublicAdvertised
        };

        session.Attributes.Add(new SessionAttribute
        {
            Key = LobbyAttributeCode,
            ValueType = AttributeType.String,
            AsString = lobbyCode,
            Advertisement = SessionAttributeAdvertisementType.Advertise
        });

        if (!sessionsManager.CreateSession(session, false, () => { }))
        {
            statusMessage = "Create session failed";
        }
    }

    private void TryResolveSessionId()
    {
        if (!isHosting || string.IsNullOrEmpty(hostingSessionName) || !string.IsNullOrEmpty(hostingSessionId))
        {
            return;
        }

        var sessions = sessionsManager.GetCurrentSessions();
        if (sessions != null && sessions.TryGetValue(hostingSessionName, out Session session))
        {
            if (!string.IsNullOrEmpty(session.Id))
            {
                hostingSessionId = session.Id;
                var update = BuildLobbyAttributeUpdate();
                lobbyManager.ModifyLobby(update, result =>
                {
                    if (result == Result.Success)
                    {
                        statusMessage = "Lobby linked to session";
                    }
                });
            }
        }
    }

    private void SearchLobbies()
    {
        if (!isLoggedIn)
        {
            statusMessage = "Login first";
            return;
        }

        isSearchingLobbies = true;
        lobbyManager.SearchByAttribute(LobbyAttributeCode, lobbyCode, result =>
        {
            isSearchingLobbies = false;
            if (result != Result.Success)
            {
                statusMessage = "Search failed: " + result;
                return;
            }

            CacheLobbyResults();
            statusMessage = "Search complete";
        });
    }

    private void CacheLobbyResults()
    {
        lobbyResults.Clear();
        foreach (var kvp in lobbyManager.GetSearchResults())
        {
            lobbyResults.Add(new LobbySearchEntry
            {
                Lobby = kvp.Key,
                Details = kvp.Value
            });
        }
    }

    private void JoinLobby(LobbySearchEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        lobbyManager.JoinLobby(entry.Lobby.Id, entry.Details, false, result =>
        {
            if (result != Result.Success)
            {
                statusMessage = "Join lobby failed: " + result;
                return;
            }

            statusMessage = "Joined lobby";
            isHosting = false;
            ResetGameState();
            SetReady(false);
            if (useSessions)
            {
                pendingJoinSessionId = GetLobbyAttribute(LobbyAttributeSessionId);
                if (!string.IsNullOrEmpty(pendingJoinSessionId))
                {
                    isSearchingSession = true;
                    sessionsManager.SearchById(pendingJoinSessionId);
                }
            }
        });
    }

    private void TryJoinPendingSession()
    {
        if (!isSearchingSession || string.IsNullOrEmpty(pendingJoinSessionId))
        {
            return;
        }

        var search = sessionsManager.GetCurrentSearch();
        if (search == null)
        {
            return;
        }

        var handle = search.GetSessionHandleById(pendingJoinSessionId);
        if (handle == null)
        {
            return;
        }

        isSearchingSession = false;
        sessionsManager.JoinSession(handle, false, result =>
        {
            if (result == Result.Success)
            {
                statusMessage = "Joined session";
            }
            else
            {
                statusMessage = "Join session failed: " + result;
            }
        });
    }

    private string GetLobbyAttribute(string key)
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby == null)
        {
            return "";
        }

        foreach (var attribute in lobby.Attributes)
        {
            if (string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.AsString ?? "";
            }
        }

        return "";
    }

    private bool IsInLobby()
    {
        if (lobbyManager == null)
        {
            return false;
        }
        var lobby = lobbyManager.GetCurrentLobby();
        return lobby != null && lobby.IsValid();
    }

    private void LeaveLobby()
    {
        if (!IsInLobby())
        {
            return;
        }

        lobbyManager.LeaveLobby(result =>
        {
            statusMessage = "Left lobby";
        });

        isHosting = false;
        isReady = false;
        ResetGameState();

        foreach (var kvp in remotePlayers)
        {
            if (kvp.Value != null)
            {
                Destroy(kvp.Value);
            }
        }
        remotePlayers.Clear();
        remoteStates.Clear();

        pendingJoinSessionId = "";
    }

    private void UpdateLobbyState()
    {
        if (!IsInLobby())
        {
            gameState = "WAITING";
            gameStarted = false;
            return;
        }

        string state = GetLobbyAttribute(LobbyAttributeState);
        if (!string.IsNullOrEmpty(state))
        {
            gameState = state;
        }

        if (!gameStarted && string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = true;
            EnsureCoins();
        }

        if (gameStarted && string.Equals(gameState, "WAITING", StringComparison.OrdinalIgnoreCase))
        {
            gameStarted = false;
        }

        string seedStr = GetLobbyAttribute(LobbyAttributeSeed);
        if (!string.IsNullOrEmpty(seedStr))
        {
            int parsedSeed;
            if (int.TryParse(seedStr, out parsedSeed))
            {
                gameSeed = parsedSeed;
            }
        }
    }

    private void UpdateReadyState()
    {
        if (!IsInLobby())
        {
            return;
        }

        if (isHosting)
        {
            bool allReady = AreAllMembersReady();
            if (allReady && !string.Equals(gameState, "STARTED", StringComparison.OrdinalIgnoreCase) && !startUpdateInProgress)
            {
                startUpdateInProgress = true;
                var update = BuildLobbyAttributeUpdate();
                update.Attributes.Add(new LobbyAttribute
                {
                    Key = LobbyAttributeState,
                    ValueType = AttributeType.String,
                    AsString = "STARTED",
                    Visibility = Epic.OnlineServices.Lobby.LobbyAttributeVisibility.Public
                });
                lobbyManager.ModifyLobby(update, result =>
                {
                    startUpdateInProgress = false;
                    if (result == Result.Success)
                    {
                        statusMessage = "Game started";
                    }
                });
            }
        }
    }

    private bool AreAllMembersReady()
    {
        var lobby = lobbyManager.GetCurrentLobby();
        if (lobby == null || lobby.Members == null || lobby.Members.Count == 0)
        {
            return false;
        }

        foreach (var member in lobby.Members)
        {
            if (member == null || member.ProductId == null || !member.ProductId.IsValid())
            {
                return false;
            }

            if (!member.MemberAttributes.TryGetValue(LobbyMemberReadyKey, out LobbyAttribute readyAttr))
            {
                return false;
            }

            if (readyAttr.ValueType == AttributeType.Boolean)
            {
                if (readyAttr.AsBool != true)
                {
                    return false;
                }
            }
            else
            {
                if (!string.Equals(readyAttr.AsString, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void EnsureCoins()
    {
        if (coinsRoot == null)
        {
            coinsRoot = GameObject.Find("CoinsRoot");
            if (coinsRoot == null)
            {
                coinsRoot = new GameObject("CoinsRoot");
            }
        }

        if (coins.Count > 0)
        {
            return;
        }

        var rng = new System.Random(gameSeed != 0 ? gameSeed : 12345);
        for (int i = 0; i < CoinCount; i++)
        {
            float x = (float)(rng.NextDouble() * 14.0 - 7.0);
            float z = (float)(rng.NextDouble() * 14.0 - 7.0);
            var coin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            coin.name = "Coin_" + i;
            coin.transform.position = new Vector3(x, 0.5f, z);
            coin.transform.localScale = new Vector3(0.6f, 0.2f, 0.6f);
            coin.transform.SetParent(coinsRoot.transform);
            var renderer = coin.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = CreateSimpleMaterial(new Color(1f, 0.85f, 0.1f, 1f));
            }
            coins.Add(coin);
        }
    }

    private void UpdateCoins()
    {
        if (!gameStarted || localPlayer == null)
        {
            return;
        }

        for (int i = 0; i < coins.Count; i++)
        {
            if (collectedCoins.Contains(i))
            {
                continue;
            }

            var coin = coins[i];
            if (coin == null)
            {
                continue;
            }

            float dist = Vector3.Distance(localPlayer.transform.position, coin.transform.position);
            if (dist <= CoinPickupRadius)
            {
                CollectCoin(i, localPlayer.transform.position);
            }
        }
    }

    private void CollectCoin(int index, Vector3 position)
    {
        if (collectedCoins.Contains(index))
        {
            return;
        }

        collectedCoins.Add(index);
        if (index >= 0 && index < coins.Count && coins[index] != null)
        {
            coins[index].SetActive(false);
        }

        localScore += 1;
        stateDirty = true;
        BroadcastCoinCollected(index, position);
    }

    private void BroadcastCoinCollected(int index, Vector3 position)
    {
        if (p2p == null)
        {
            return;
        }

        byte[] data = new byte[1 + 4 + 12];
        data[0] = (byte)'C';
        Buffer.BlockCopy(BitConverter.GetBytes(index), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(position.x), 0, data, 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(position.y), 0, data, 9, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(position.z), 0, data, 13, 4);

        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void SendStateIfDirty()
    {
        if (!stateDirty || !isLoggedIn || !IsInLobby())
        {
            return;
        }

        stateDirty = false;
        SendStatePacket();
    }

    private void SendStatePacket()
    {
        byte[] data = new byte[1 + 4 + 4];
        data[0] = (byte)'S';
        Buffer.BlockCopy(BitConverter.GetBytes(localScore), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(localHp), 0, data, 5, 4);
        SendToAll(new ArraySegment<byte>(data), PacketReliability.ReliableUnordered);
    }

    private void SendToAll(ArraySegment<byte> data, PacketReliability reliability)
    {
        if (p2p == null)
        {
            return;
        }

        var localId = EOSManager.Instance.GetProductUserId();
        if (localId == null || !localId.IsValid())
        {
            return;
        }

        var members = lobbyManager.GetCurrentLobby().Members;
        if (members == null)
        {
            return;
        }

        foreach (var member in members)
        {
            if (member == null || member.ProductId == null || !member.ProductId.IsValid())
            {
                continue;
            }

            if (member.ProductId.ToString() == localId.ToString())
            {
                continue;
            }

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
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 420, 640), GUI.skin.box);
        GUILayout.Label("EOS Prototype (Lobby + Session + P2P)");

        GUILayout.Space(6);
        GUILayout.Label("Display Name");
        displayName = GUILayout.TextField(displayName, 24);

        GUILayout.Label("Dev Auth Address");
        devAuthAddress = GUILayout.TextField(devAuthAddress, 32);

        GUILayout.Label("Dev Auth Credential");
        devAuthCredential = GUILayout.TextField(devAuthCredential, 32);

        GUILayout.Label("Lobby Code");
        lobbyCode = GUILayout.TextField(lobbyCode, 16);

        GUILayout.Space(6);
        if (!isLoggedIn)
        {
            if (GUILayout.Button("Login (Device ID)"))
            {
                StartLogin();
            }
        }
        else
        {
            GUILayout.Label("Logged in: " + EOSManager.Instance.GetProductUserId());
        }

        GUILayout.Space(6);
        if (GUILayout.Button("Host (Create Lobby + Session)"))
        {
            Host();
        }

        if (GUILayout.Button(isSearchingLobbies ? "Searching..." : "Search Lobbies"))
        {
            if (!isSearchingLobbies)
            {
                SearchLobbies();
            }
        }

        if (GUILayout.Button("Leave Lobby"))
        {
            LeaveLobby();
        }

        if (IsInLobby())
        {
            if (GUILayout.Button(isReady ? "Unready" : "Ready"))
            {
                SetReady(!isReady);
            }
        }

        GUILayout.Space(8);
        GUILayout.Label("Status: " + statusMessage);

        GUILayout.Space(8);
        GUILayout.Label("Lobby Results");
        if (lobbyResults.Count == 0)
        {
            GUILayout.Label("No results");
        }
        else
        {
            for (int i = 0; i < lobbyResults.Count; i++)
            {
                var entry = lobbyResults[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(entry.Lobby.Id, GUILayout.Width(220));
                if (GUILayout.Button("Join", GUILayout.Width(80)))
                {
                    JoinLobby(entry);
                }
                GUILayout.EndHorizontal();
            }
        }

        GUILayout.Space(8);
        if (IsInLobby())
        {
            var lobby = lobbyManager.GetCurrentLobby();
            GUILayout.Label("Lobby: " + lobby.Id);
            GUILayout.Label("Game: " + gameState + " | Ready: " + (isReady ? "Yes" : "No"));
            GUILayout.Label("Score: " + localScore + " | HP: " + localHp + " (H/J)");
            GUILayout.Label("Members: " + lobby.Members.Count);
            foreach (var member in lobby.Members)
            {
                GUILayout.Label("- " + member.ProductId);
            }

            var localId = EOSManager.Instance.GetProductUserId();
            GUILayout.Label("Remote Stats");
            foreach (var member in lobby.Members)
            {
                if (member == null || member.ProductId == null || !member.ProductId.IsValid())
                {
                    continue;
                }

                if (localId != null && localId.IsValid() && member.ProductId.ToString() == localId.ToString())
                {
                    continue;
                }

                string key = member.ProductId.ToString();
                int score = remoteScores.ContainsKey(key) ? remoteScores[key] : 0;
                int hp = remoteHps.ContainsKey(key) ? remoteHps[key] : 100;
                GUILayout.Label("  " + key + "  S:" + score + " HP:" + hp);
            }

            if (useSessions && !string.IsNullOrEmpty(hostingSessionId))
            {
                GUILayout.Label("SessionId: " + hostingSessionId);
            }
        }

        GUILayout.EndArea();
    }
}























































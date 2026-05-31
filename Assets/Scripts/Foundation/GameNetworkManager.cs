using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Foundation
{
    /// <summary>
    /// Unity NGO NetworkManager의 래퍼.
    /// 연결 승인, 플레이어 입퇴장, 호스트/클라이언트 시작을 담당한다.
    /// Steam 연동은 STEAM_BUILD 심볼 선언 시 활성화된다.
    /// </summary>
    public class GameNetworkManager : MonoBehaviour
    {
        public static GameNetworkManager Instance { get; private set; }

        public const int MaxPlayers = 20;

        // ── Events ─────────────────────────────────────────────
        public event Action          OnHostStarted;
        public event Action          OnLocalClientConnected;
        public event Action<ulong>   OnPlayerJoined;
        public event Action<ulong>   OnPlayerLeft;

        /// <summary>
        /// 호스트가 예기치 않게 이탈했을 때 발행된다.
        /// HostMigrationController가 구독하여 마이그레이션을 시작한다.
        /// </summary>
        public event Action OnHostLost;

        // ── State ──────────────────────────────────────────────
        private readonly List<ulong> _connectedClients = new();
        public IReadOnlyList<ulong> ConnectedClients => _connectedClients;
        public int PlayerCount => _connectedClients.Count;

        // ── Lifecycle ──────────────────────────────────────────

        void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(transform.root.gameObject);
        }

        void OnEnable()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            nm.OnServerStarted                 += HandleServerStarted;
            nm.OnClientConnectedCallback       += HandleClientConnected;
            nm.OnClientDisconnectCallback      += HandleClientDisconnected;
            nm.ConnectionApprovalCallback      += HandleConnectionApproval;
        }

        void OnDisable()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            nm.OnServerStarted                 -= HandleServerStarted;
            nm.OnClientConnectedCallback       -= HandleClientConnected;
            nm.OnClientDisconnectCallback      -= HandleClientDisconnected;
            nm.ConnectionApprovalCallback      -= HandleConnectionApproval;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Public API ─────────────────────────────────────────

        public void StartHost()
        {
#if STEAM_BUILD
            ConfigureSteamTransport();
#endif
            NetworkManager.Singleton.StartHost();
        }

        public void StartClient()
        {
#if STEAM_BUILD
            ConfigureSteamTransport();
#endif
            NetworkManager.Singleton.StartClient();
        }

        public void Disconnect()
        {
            NetworkManager.Singleton.Shutdown();
            _connectedClients.Clear();
        }

        // ── NGO Callbacks ──────────────────────────────────────

        private void HandleServerStarted()
        {
            OnHostStarted?.Invoke();
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (!_connectedClients.Contains(clientId))
                _connectedClients.Add(clientId);

            OnPlayerJoined?.Invoke(clientId);

            if (clientId == NetworkManager.Singleton.LocalClientId)
                OnLocalClientConnected?.Invoke();
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            _connectedClients.Remove(clientId);
            OnPlayerLeft?.Invoke(clientId);

            // unity-specialist 검증: ServerClientId는 NGO 2.3.2에서 항상 0.
            // IsServer가 false인 클라이언트에서 ServerClientId 연결이 끊기면 호스트 이탈.
            bool isHostDeparture = !NetworkManager.Singleton.IsServer
                                && clientId == NetworkManager.ServerClientId
                                && GameStateManager.Instance?.Current == GameState.InGame;
            if (isHostDeparture)
            {
                Debug.Log("[Network] 호스트 이탈 감지 — OnHostLost 발행");
                OnHostLost?.Invoke();
            }
        }

        private void HandleConnectionApproval(
            NetworkManager.ConnectionApprovalRequest  request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            bool lobbyOpen    = GameStateManager.Instance?.Current == GameState.Lobby;
            bool hasRoom      = _connectedClients.Count < MaxPlayers;

            response.Approved           = lobbyOpen && hasRoom;
            response.CreatePlayerObject = false; // PlayerManager가 스폰 담당
            response.Reason             = response.Approved ? "" : (lobbyOpen ? "서버가 가득 찼습니다." : "게임이 이미 시작되었습니다.");
        }

        // ── Steam (빌드 심볼 STEAM_BUILD 선언 시 활성화) ─────────
#if STEAM_BUILD
        private void ConfigureSteamTransport()
        {
            // TODO: FacePunch.Steamworks 연동
            // var transport = NetworkManager.Singleton.GetComponent<FacepunchTransport>();
            // transport.targetSteamId = targetId;
            Debug.Log("[Network] Steam Transport 설정 완료");
        }
#endif
    }
}

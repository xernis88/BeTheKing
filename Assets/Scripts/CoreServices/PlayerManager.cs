using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using BeTheKing.Foundation;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 플레이어 스폰·사망·정체(Identity) 배정을 담당한다.
    /// 서버 권위적: 모든 스폰·정체 배정 로직은 IsServer 가드 안에서만 실행된다.
    /// Host Migration 내성: 정체 정보를 NetworkList로 영속화하여 새 Host에서도 유지된다.
    /// </summary>
    public class PlayerManager : NetworkBehaviour
    {
        public static PlayerManager Instance { get; private set; }

        [SerializeField] private GameObject _playerPrefab;

        // Host Migration 내성: NetworkList로 영속화.
        // Owner = 서버(Host) 소유 scene object이므로 클라이언트는 이 데이터를 수신하지 않음.
        private readonly NetworkList<PlayerIdentityEntry> _identityList = new(
            default,
            NetworkVariableReadPermission.Owner,
            NetworkVariableWritePermission.Owner
        );

        // 생존 플레이어 수. 서버 권위(Server write), 전체 클라이언트 읽기(Everyone read).
        private readonly NetworkVariable<int> _aliveCount = new(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>생존 플레이어 수. 서버 권위, 전체 클라이언트 읽기 가능.</summary>
        public NetworkVariable<int> AliveCount => _aliveCount;

        // ── Events ─────────────────────────────────────────────
        public event Action<ulong> OnPlayerSpawned;
        public event Action<ulong> OnPlayerDied;

        /// <summary>플레이어 스폰 + 직업 배정 후 발행 (clientId, playerNetworkObjectId, jobId). DisguiseSystem 구독용.</summary>
        public static event Action<ulong, ulong, int> OnPlayerSpawnedWithJob;

        /// <summary>전 클라이언트에 브로드캐스트되는 사망 공지 이벤트. UI 레이어에서 구독.</summary>
        public static event Action<ulong> OnPlayerDeathAnnounced;

        /// <summary>
        /// OnPlayerDeathAnnounced 이벤트를 발행한다.
        /// AnnouncePlayerDeathClientRpc 수신 시 호출되며, unit test에서 직접 호출 가능하다.
        /// </summary>
        internal static void RaiseOnPlayerDeathAnnounced(ulong clientId)
            => OnPlayerDeathAnnounced?.Invoke(clientId);

        // ── Lifecycle ──────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(transform.root.gameObject);
        }

        public override void OnNetworkSpawn()
        {
            if (GameStateManager.Instance != null)
                GameStateManager.Instance.OnStateChanged += HandleStateChanged;
            else
                StartCoroutine(WaitAndSubscribe());
        }

        public override void OnNetworkDespawn()
        {
            if (GameStateManager.Instance != null)
                GameStateManager.Instance.OnStateChanged -= HandleStateChanged;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        private IEnumerator WaitAndSubscribe()
        {
            yield return new WaitUntil(() => GameStateManager.Instance != null);
            GameStateManager.Instance.OnStateChanged += HandleStateChanged;
        }

        // ── State Handler ──────────────────────────────────────

        private void HandleStateChanged(GameState prev, GameState next)
        {
            if (!IsServer) return;
            if (next == GameState.InGame) SpawnAllPlayers();
        }

        // ── Spawn ──────────────────────────────────────────────

        private void SpawnAllPlayers()
        {
            if (!IsServer) return;
            if (_playerPrefab == null)
            {
                Debug.LogError("[PlayerManager] _playerPrefab이 할당되지 않았습니다.");
                return;
            }

            var clients = GameNetworkManager.Instance.ConnectedClients;
            for (int i = 0; i < clients.Count; i++)
            {
                ulong clientId = clients[i];
                var pos        = SpawnPointHelper.CalculatePosition(i);

                var go = Instantiate(_playerPrefab, pos, Quaternion.identity);
                var no = go.GetComponent<NetworkObject>();
                if (no == null)
                {
                    Debug.LogError($"[PlayerManager] prefab에 NetworkObject 없음 — clientId={clientId}");
                    Destroy(go);
                    continue;
                }
                no.SpawnAsPlayerObject(clientId);

                bool isTarget = (_identityList.Count == 0);
                int jobId = 0; // MVP: 단일 직업 (0). 직업 시스템 확장 시 변경.
                _identityList.Add(new PlayerIdentityEntry
                {
                    ClientId = clientId,
                    JobId    = jobId,
                    IsTarget = isTarget
                });

                // DisguiseSystem이 구독하여 플레이어 머티리얼 적용
                OnPlayerSpawnedWithJob?.Invoke(clientId, no.NetworkObjectId, jobId);

                OnPlayerSpawned?.Invoke(clientId);
                _aliveCount.Value++;
                Debug.Log($"[PlayerManager] 스폰 — clientId={clientId}, zone={SpawnPointHelper.GetZoneIndex(i)}");
#if UNITY_EDITOR
                Debug.Log($"[PlayerManager][EDITOR ONLY] clientId={clientId}, isTarget={isTarget}");
#endif
            }
        }

        // ── Death ──────────────────────────────────────────────

        /// <summary>
        /// 플레이어 사망 처리. 캐릭터를 Despawn하고 LootManager에 드롭 이벤트를 전달한다.
        /// 서버 전용.
        /// </summary>
        public void HandlePlayerDeath(ulong clientId)
        {
            if (!IsServer) return;

            var no = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
            if (no != null)
                no.Despawn(destroy: true);
            else
                Debug.LogWarning($"[PlayerManager] HandlePlayerDeath: NetworkObject 없음 — clientId={clientId}");

            // _identityList에서 해당 엔트리 제거
            for (int i = 0; i < _identityList.Count; i++)
            {
                if (_identityList[i].ClientId == clientId)
                {
                    _identityList.RemoveAt(i);
                    break;
                }
            }

            OnPlayerDied?.Invoke(clientId);
            AnnouncePlayerDeathClientRpc(clientId);
            if (_aliveCount.Value > 0) _aliveCount.Value--;
            Debug.Log($"[PlayerManager] 사망 처리 완료 — clientId={clientId}");
        }

        /// <summary>
        /// 전 클라이언트에 사망 공지를 브로드캐스트한다.
        /// 공개 정보(clientId)만 전달 — 신원(역할·IsTarget 등)은 절대 포함하지 않는다.
        /// </summary>
        [ClientRpc]
        private void AnnouncePlayerDeathClientRpc(ulong clientId)
        {
            OnPlayerDeathAnnounced?.Invoke(clientId);
        }

        // ── Identity API ───────────────────────────────────────

        /// <summary>서버 전용. 해당 클라이언트의 직업 ID를 반환한다.</summary>
        public int GetJobId(ulong clientId)
        {
            if (!IsServer) return -1;
            foreach (var entry in _identityList)
                if (entry.ClientId == clientId) return entry.JobId;
            return -1;
        }

        /// <summary>서버 전용. 해당 클라이언트가 왕족 혈통인지 반환한다.</summary>
        public bool IsRoyalBlood(ulong clientId)
        {
            if (!IsServer) return false;
            foreach (var entry in _identityList)
                if (entry.ClientId == clientId) return entry.IsTarget;
            return false;
        }
    }

    // ── 데이터 타입 ─────────────────────────────────────────────

    /// <summary>
    /// 플레이어 1인의 정체 정보. NetworkList 직렬화용.
    /// ReadPermission.Server 설정으로 클라이언트에 노출되지 않는다.
    /// Host Migration 시 새 Host의 NetworkList에 자동 복원된다.
    /// </summary>
    public struct PlayerIdentityEntry : INetworkSerializable, IEquatable<PlayerIdentityEntry>
    {
        /// <summary>소유 클라이언트 ID.</summary>
        public ulong ClientId;
        /// <summary>직업 ID. MVP: 항상 0.</summary>
        public int   JobId;
        /// <summary>왕족 혈통 여부. Host 메모리에만 존재, 네트워크 전송 금지.</summary>
        public bool  IsTarget;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref JobId);
            serializer.SerializeValue(ref IsTarget);
        }

        public bool Equals(PlayerIdentityEntry other) => ClientId == other.ClientId;
        public override int GetHashCode() => ClientId.GetHashCode();
    }

    // ── 스폰 위치 헬퍼 ──────────────────────────────────────────

    /// <summary>250×250 맵 기준 4구역 round-robin 스폰 위치 계산기.</summary>
    public static class SpawnPointHelper
    {
        private static readonly Vector3[] ZoneCenters =
        {
            new(62.5f,  0f, 62.5f),   // 북서
            new(187.5f, 0f, 62.5f),   // 북동
            new(62.5f,  0f, 187.5f),  // 남서
            new(187.5f, 0f, 187.5f),  // 남동
        };
        internal const float ZoneHalfSize = 50f;

        /// <summary>playerIndex를 4구역에 round-robin으로 배분해 구역 내 랜덤 위치를 반환한다.</summary>
        public static Vector3 CalculatePosition(int playerIndex)
        {
            var center = ZoneCenters[playerIndex % 4];
            float dx   = UnityEngine.Random.Range(-ZoneHalfSize, ZoneHalfSize);
            float dz   = UnityEngine.Random.Range(-ZoneHalfSize, ZoneHalfSize);
            return center + new Vector3(dx, 0f, dz);
        }

        /// <summary>playerIndex → zoneIndex (0~3). 테스트 및 디버그용.</summary>
        public static int GetZoneIndex(int playerIndex) => playerIndex % 4;

        /// <summary>zoneIndex → 구역 중심 좌표. 테스트용.</summary>
        public static Vector3 GetZoneCenter(int zoneIndex) => ZoneCenters[zoneIndex % 4];
    }
}

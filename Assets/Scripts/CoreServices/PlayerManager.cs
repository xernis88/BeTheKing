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
    /// ADR-004: _identityMap 키 해싱 적용.
    /// </summary>
    public class PlayerManager : NetworkBehaviour
    {
        public static PlayerManager Instance { get; private set; }

        [SerializeField] private GameObject _playerPrefab;

        // ADR-004: 키 해싱 — ulong 원본값 대신 int 모듈러 해시로 저장 (메모리 덤프 가독성 저하)
        private readonly Dictionary<int, PlayerIdentity> _identityMap = new();

        // ── Events ─────────────────────────────────────────────
        public event Action<ulong> OnPlayerSpawned;
        public event Action<ulong> OnPlayerDied;

        // ── Lifecycle ──────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
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

                bool isTarget    = (_identityMap.Count == 0);
                int  key         = (int)(clientId % int.MaxValue);
                _identityMap[key] = new PlayerIdentity { JobId = 0, IsTarget = isTarget };

                OnPlayerSpawned?.Invoke(clientId);
                Debug.Log($"[PlayerManager] 스폰 — clientId={clientId}, zone={SpawnPointHelper.GetZoneIndex(i)}, isTarget={isTarget}");
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

            OnPlayerDied?.Invoke(clientId);
            Debug.Log($"[PlayerManager] 사망 처리 완료 — clientId={clientId}");
        }

        // ── Identity API ───────────────────────────────────────

        /// <summary>서버 전용. 해당 클라이언트의 직업 ID를 반환한다.</summary>
        public int GetJobId(ulong clientId)
        {
            if (!IsServer) return -1;
            int key = (int)(clientId % int.MaxValue);
            return _identityMap.TryGetValue(key, out var id) ? id.JobId : -1;
        }

        /// <summary>서버 전용. 해당 클라이언트가 왕족 혈통인지 반환한다.</summary>
        public bool IsRoyalBlood(ulong clientId)
        {
            if (!IsServer) return false;
            int key = (int)(clientId % int.MaxValue);
            return _identityMap.TryGetValue(key, out var id) && id.IsTarget;
        }
    }

    // ── 데이터 타입 ─────────────────────────────────────────────

    /// <summary>플레이어 1인의 정체 정보. 서버 메모리에만 존재한다.</summary>
    public struct PlayerIdentity
    {
        /// <summary>직업 ID. MVP: 항상 0.</summary>
        public int  JobId;
        /// <summary>왕족 혈통 여부. Host 메모리에만 존재, 네트워크 전송 금지.</summary>
        public bool IsTarget;
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

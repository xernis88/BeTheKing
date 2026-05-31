// Implements: design/gdd/01-core-services.md — NPCManager
// Story: production/epics/epic-core-services/story-003-npc-pool-placement.md

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using BeTheKing.Foundation;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 일반 NPC ~70명, 자객 NPC 8~10마리, 왕자 NPC 1명의 스폰·배치·풀 반환을 담당한다.
    /// 풀링은 Despawn(destroy:false) + Spawn() 재사용 패턴(NGO 서버 사이드 풀링)으로 구현한다.
    /// 서버 권위적: 모든 풀 조작과 배치는 IsServer 가드 안에서만 실행된다.
    /// </summary>
    public class NPCManager : NetworkBehaviour
    {
        public static NPCManager Instance { get; private set; }

        /// <summary>Civilian NPC 스폰·JobId 배정 후 발행. DisguiseSystem이 구독하여 머티리얼 적용.</summary>
        public static event System.Action<ulong, int> OnCivilianNpcSpawned;

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("Prefabs")]
        [SerializeField] private GameObject _civilianPrefab;
        [SerializeField] private GameObject _assassinPrefab;
        [SerializeField] private GameObject _princePrefab;

        [Header("Config")]
        [Tooltip("assets/data/NpcPlacementConfig.asset")]
        [SerializeField] private NpcPlacementConfig _config;

        [Header("Zone Spawn Bounds (Inspector 설정)")]
        [Tooltip("ZoneId 순서대로 구역 경계 입력 (Central 제외, North/East/South/West 순)")]
        [SerializeField] private ZoneBounds[] _zoneBounds;

        // ── Pools ──────────────────────────────────────────────────────────────

        private NpcPoolHandler _civilianPool;
        private NpcPoolHandler _assassinPool;

        // 왕자 NPC는 단일 인스턴스 — 풀 불필요
        private NetworkObject _princeInstance;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            // 풀 프리워밍 — 런타임 Instantiate 최소화 (AC-4)
            _civilianPool = new NpcPoolHandler(_civilianPrefab, _config.CivilianPrewarm);
            _assassinPool = new NpcPoolHandler(_assassinPrefab, _config.AssassinPrewarm);

            if (GameStateManager.Instance != null)
                GameStateManager.Instance.OnStateChanged += HandleStateChanged;
            else
                StartCoroutine(WaitAndSubscribeGameState());
        }

        public override void OnNetworkDespawn()
        {
            if (!IsServer) return;
            if (GameStateManager.Instance != null)
                GameStateManager.Instance.OnStateChanged -= HandleStateChanged;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        private IEnumerator WaitAndSubscribeGameState()
        {
            yield return new WaitUntil(() => GameStateManager.Instance != null);
            GameStateManager.Instance.OnStateChanged += HandleStateChanged;
        }

        // ── State Handler ──────────────────────────────────────────────────────

        private void HandleStateChanged(GameState prev, GameState next)
        {
            if (!IsServer) return;
            if (next == GameState.InGame) PlaceNPCs();
        }

        // ── Placement ─────────────────────────────────────────────────────────

        /// <summary>
        /// 4구역(Central 제외)에 일반 NPC와 자객 NPC를 분산 배치하고
        /// 왕좌 영역에 왕자 NPC를 비활성 상태로 배치한다. (AC-1, AC-2, AC-3)
        /// </summary>
        public void PlaceNPCs()
        {
            if (!IsServer) return;

            // 직업 풀 — 일반 NPC 복장 카피용 (AC-5: JobId 배정)
            List<int> jobPool = BuildJobPool();
            int jobIndex = 0;

            // 구역 4개 순회 (Central 제외)
            ZoneId[] outerZones = { ZoneId.North, ZoneId.East, ZoneId.South, ZoneId.West };
            foreach (ZoneId zone in outerZones)
            {
                ZoneBounds bounds = GetBounds(zone);

                for (int i = 0; i < _config.CivilianPerZone; i++)
                {
                    Vector3 pos = RandomPointInBounds(bounds);
                    var no = _civilianPool.Get(pos, Quaternion.identity);

                    // JobId 배정 — 순환 (AC-5)
                    if (no.TryGetComponent<CivilianNpc>(out var npc))
                    {
                        npc.NpcJobId = jobPool[jobIndex++ % jobPool.Count];
                        OnCivilianNpcSpawned?.Invoke(no.NetworkObjectId, npc.NpcJobId);
                    }
                }

                for (int i = 0; i < _config.AssassinPerZone; i++)
                {
                    Vector3 pos = RandomPointInBounds(bounds);
                    _assassinPool.Get(pos, Quaternion.identity);
                }
            }

            PlacePrince();
        }

        /// <summary>
        /// 왕자 NPC를 왕좌 영역(Central)에 Instantiate만 수행하고 비활성 상태로 유지한다. (AC-3)
        /// Spawn은 하지 않으므로 클라이언트에 존재가 노출되지 않는다.
        /// 3일차 CoronationTrigger → PrinceNPCAI.Activate() → 서버 gameObject 활성화 경로를 사용한다.
        /// </summary>
        private void PlacePrince()
        {
            if (_princeInstance != null) return; // 이미 배치됨

            ZoneBounds central = GetBounds(ZoneId.Central);
            Vector3 pos = central.Center; // 왕좌 중심에 고정 배치

            var go = UnityEngine.Object.Instantiate(_princePrefab, pos, Quaternion.identity);
            _princeInstance = go.GetComponent<NetworkObject>();

            // Spawn 전 비활성화 — 클라이언트에 존재 자체가 노출되지 않는다.
            go.SetActive(false);
        }

        /// <summary>
        /// 왕자 NPC를 활성화하고 Spawn한다. Day 3 대관식 시점에 호출.
        /// PrinceNPCAI.Activate()가 gameObject.SetActive(true)를 수행하므로
        /// 이 메서드는 Spawn 전 활성화 + Spawn 순서를 보장한다.
        /// </summary>
        public void ActivatePrince()
        {
            if (!IsServer) return;
            if (_princeInstance == null || _princeInstance.IsSpawned) return;

            _princeInstance.gameObject.SetActive(true);
            _princeInstance.Spawn(destroyWithScene: true);
        }

        // ── Pool Return API ────────────────────────────────────────────────────

        /// <summary>일반 NPC를 풀에 반환한다. 서버 전용.</summary>
        public void ReturnCivilian(NetworkObject no)
        {
            if (!IsServer) return;
            _civilianPool.Return(no);
        }

        /// <summary>자객 NPC를 풀에 반환한다. 서버 전용.</summary>
        public void ReturnAssassin(NetworkObject no)
        {
            if (!IsServer) return;
            _assassinPool.Return(no);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// 현재 플레이어 직업 목록에서 NPC 복장 카피용 잡 풀을 생성한다.
        /// PlayerManager._identityMap에서 JobId 조회 (AC-5).
        /// </summary>
        private List<int> BuildJobPool()
        {
            var pool = new List<int>();
            if (PlayerManager.Instance == null) return pool;

            var clients = GameNetworkManager.Instance.ConnectedClients;
            // 플레이어 직업 중 3~4개를 NPC에 할당 (GDD §3 NPCManager)
            int copyCount = Mathf.Clamp(_config.JobCopyCount, 3, 4);
            for (int i = 0; i < copyCount && i < clients.Count; i++)
                pool.Add(PlayerManager.Instance.GetJobId(clients[i]));

            if (pool.Count == 0) pool.Add(0); // 폴백: 기본 직업
            return pool;
        }

        private ZoneBounds GetBounds(ZoneId zone)
        {
            foreach (var b in _zoneBounds)
                if (b.Zone == zone) return b;

            // 폴백: 원점 중심 50×50 박스
            return new ZoneBounds { Zone = zone, Center = Vector3.zero, HalfExtents = new Vector3(25f, 0f, 25f) };
        }

        private static Vector3 RandomPointInBounds(ZoneBounds b)
        {
            float x = UnityEngine.Random.Range(b.Center.x - b.HalfExtents.x, b.Center.x + b.HalfExtents.x);
            float z = UnityEngine.Random.Range(b.Center.z - b.HalfExtents.z, b.Center.z + b.HalfExtents.z);
            return new Vector3(x, b.Center.y, z);
        }

        // ── Inner Pool Handler ─────────────────────────────────────────────────

        /// <summary>
        /// NGO 서버 사이드 풀링 핸들러.
        /// Despawn(destroy:false) + Spawn() 재사용 패턴 — INetworkPrefabInstanceHandler 불필요.
        /// </summary>
        private sealed class NpcPoolHandler
        {
            private readonly Queue<NetworkObject> _pool = new();
            private readonly GameObject _prefab;

            public NpcPoolHandler(GameObject prefab, int prewarmCount)
            {
                _prefab = prefab;
                for (int i = 0; i < prewarmCount; i++)
                {
                    var go = UnityEngine.Object.Instantiate(prefab);
                    go.SetActive(false);
                    _pool.Enqueue(go.GetComponent<NetworkObject>());
                }
            }

            /// <summary>풀에서 NetworkObject를 꺼내 지정 위치에 Spawn한다.</summary>
            public NetworkObject Get(Vector3 position, Quaternion rotation)
            {
                NetworkObject no;
                if (_pool.TryDequeue(out var pooled))
                {
                    no = pooled;
                    no.transform.SetPositionAndRotation(position, rotation);
                    no.gameObject.SetActive(true);
                }
                else
                {
                    // 풀 소진 시 새 인스턴스 생성
                    var go = UnityEngine.Object.Instantiate(_prefab, position, rotation);
                    no = go.GetComponent<NetworkObject>();
                }
                no.Spawn(destroyWithScene: true);
                return no;
            }

            /// <summary>NetworkObject를 Despawn하고 풀에 반환한다.</summary>
            public void Return(NetworkObject no)
            {
                no.Despawn(destroy: false);
                no.gameObject.SetActive(false);
                _pool.Enqueue(no);
            }
        }
    }

    // ── Supporting Types ───────────────────────────────────────────────────────

    /// <summary>구역 경계 정의. Inspector에서 설정.</summary>
    [Serializable]
    public struct ZoneBounds
    {
        [Tooltip("구역 식별자")]
        public ZoneId Zone;
        [Tooltip("구역 중심 월드 좌표")]
        public Vector3 Center;
        [Tooltip("구역 반경 (X·Z 축, Y=0)")]
        public Vector3 HalfExtents;
    }

    /// <summary>
    /// 일반 NPC 컴포넌트 — 직업 ID 보유.
    /// 비주얼 메시 교체는 DisguiseSystem 담당 (Out of Scope).
    /// </summary>
    public class CivilianNpc : MonoBehaviour
    {
        /// <summary>배정된 직업 ID. DisguiseSystem이 읽어 메시를 교체한다.</summary>
        public int NpcJobId { get; set; }
    }
}

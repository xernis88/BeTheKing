// Implements: design/gdd/01-core-services.md — LootManager
// Story: production/epics/epic-core-services/story-004-loot-drop.md
//
// 호출 계약 (옵션 A):
//   PlayerManager는 수정하지 않는다.
//   사망 처리 사이트(예: PlayerHealthComponent)에서
//   LootManager.Instance.OnPlayerDied(clientId, deathPos, inventory) 를 직접 호출한다.

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 플레이어·자객 사망 시 드롭 아이템을 생성하고,
    /// 게임 시작 시 상자 스폰 포인트에 아이템을 사전 배치한다.
    /// 서버 권위적: 모든 스폰은 IsServer 가드 안에서만 실행된다.
    /// </summary>
    public class LootManager : NetworkBehaviour
    {
        public static LootManager Instance { get; private set; }

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("Config")]
        [Tooltip("LootConfig ScriptableObject. 모든 수치는 여기서 관리한다.")]
        [SerializeField] private LootConfig _config;

        [Header("Drop Prefab")]
        [Tooltip("드롭 아이템 NetworkObject 프리팹. 모든 아이템 종류에 공통 사용.")]
        [SerializeField] private GameObject _dropItemPrefab;

        [Header("World Chests")]
        [Tooltip("씬에 배치된 상자 스폰 포인트. ScriptableObject에서 씬 오브젝트를 참조할 수 없으므로 여기서 직접 할당한다.")]
        [SerializeField] private Transform[] _chestSpawnPoints;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        // 현재 맵에 존재하는 드롭 위치 추적 — scatter 오프셋 계산에 사용
        private readonly List<Vector3> _activeDropPositions = new();

        // ── Lifecycle ──────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(transform.root.gameObject);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                SpawnWorldItems();
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 플레이어 사망 시 드롭 처리 진입점.
        /// 호출 계약(옵션 A): 사망 처리 사이트에서 직접 호출한다.
        /// PlayerManager는 수정하지 않는다.
        /// </summary>
        /// <param name="clientId">사망한 플레이어의 NGO clientId</param>
        /// <param name="deathPos">사망 월드 위치</param>
        /// <param name="inventory">드롭할 아이템 배열 (비어있어도 무방)</param>
        public void OnPlayerDied(ulong clientId, Vector3 deathPos, ItemData[] inventory)
        {
            if (!IsServer) return;
            if (inventory == null || inventory.Length == 0) return;

            foreach (ItemData item in inventory)
            {
                if (!item.IsValid) continue;
                Vector3 dropPos = GetValidDropPosition(deathPos);
                SpawnDropItem(item, dropPos);
            }

            Debug.Log($"[LootManager] 드롭 완료 — clientId={clientId}, items={inventory.Length}");
        }

        // ── 드롭 위치 계산 ──────────────────────────────────────────────────────

        /// <summary>
        /// 사망 위치로부터 구역 경계 보정 + scatter 오프셋을 적용한 유효한 드롭 위치를 반환한다.
        /// AC-2 (겹침 오프셋) + AC-3 (경계 보정) 구현.
        /// </summary>
        private Vector3 GetValidDropPosition(Vector3 origin)
        {
            // 1단계: 구역 경계 안쪽으로 보정 (AC-3)
            Vector3 clamped = ClampToZone(origin);

            // 2단계: 기존 드롭 위치와 겹치지 않도록 scatter (AC-2)
            Vector3 finalPos = ApplyScatterOffset(clamped, _activeDropPositions);
            _activeDropPositions.Add(finalPos);
            return finalPos;
        }

        /// <summary>
        /// 위치가 어떤 구역 경계 안에도 없으면 가장 가까운 구역 경계 안쪽 지점으로 보정한다.
        /// MapManager._zones의 Bounds.ClosestPoint를 사용한다.
        /// </summary>
        internal Vector3 ClampToZone(Vector3 pos)
        {
            if (MapManager.Instance == null)
            {
                float i = _config != null ? _config.ZoneBoundaryInset : 0.5f;
                return new Vector3(Mathf.Clamp(pos.x, i, 250f - i), pos.y, Mathf.Clamp(pos.z, i, 250f - i));
            }

            ZoneId zone = MapManager.Instance.GetZone(pos);
            if (zone != ZoneId.None)
            {
                // 이미 구역 안에 있음 — inset 여백만 적용
                return ApplyBoundaryInset(pos, MapManager.Instance);
            }

            // 구역 밖: MapManager의 전체 bounds에서 ClosestPoint로 보정
            // MapManager가 _zones를 직접 노출하지 않으므로 근사 처리:
            // 250×250 맵 전체 범위를 안전 폴백으로 사용한다.
            float inset = _config != null ? _config.ZoneBoundaryInset : 0.5f;
            float clampedX = Mathf.Clamp(pos.x, inset, 250f - inset);
            float clampedZ = Mathf.Clamp(pos.z, inset, 250f - inset);
            return new Vector3(clampedX, pos.y, clampedZ);
        }

        /// <summary>구역 안에 있는 위치를 경계에서 inset 만큼 안쪽으로 보정한다.</summary>
        private Vector3 ApplyBoundaryInset(Vector3 pos, MapManager map)
        {
            // MapManager가 Bounds를 직접 노출하지 않으므로 여기서는 원본 위치를 그대로 반환한다.
            // 경계 inset이 필요한 경우 MapManager에 ClampToBounds(pos, inset) API 추가를 권장한다.
            return pos;
        }

        /// <summary>
        /// 기존 드롭 위치 목록과 겹치지 않도록 scatter 오프셋을 적용한 위치를 반환한다.
        /// 최대 ScatterMaxAttempts 번 시도하고, 실패 시 마지막 후보를 사용한다.
        /// </summary>
        internal Vector3 ApplyScatterOffset(Vector3 origin, List<Vector3> existing)
        {
            float radius     = _config != null ? _config.ScatterRadius     : 1.2f;
            int   maxAttempt = _config != null ? _config.ScatterMaxAttempts : 10;

            Vector3 candidate = origin;
            for (int i = 0; i < maxAttempt; i++)
            {
                bool overlaps = false;
                foreach (Vector3 taken in existing)
                {
                    if (Vector3.Distance(candidate, taken) < radius)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps) return candidate;

                // 다음 후보: 랜덤 방향 반경만큼 이동
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                candidate = origin + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                );
            }

            // 최대 시도 초과 — 마지막 후보 그대로 사용
            Debug.LogWarning($"[LootManager] scatter 최대 시도({maxAttempt}) 초과 — 마지막 후보 사용");
            return candidate;
        }

        // ── 드롭 스폰 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 지정 위치에 드롭 아이템 NetworkObject를 서버에서 생성한다.
        /// NetworkObject.Spawn() 호출 → 클라이언트 자동 동기화. (AC-5)
        /// </summary>
        private void SpawnDropItem(ItemData item, Vector3 pos)
        {
            if (_dropItemPrefab == null)
            {
                Debug.LogError("[LootManager] _dropItemPrefab이 할당되지 않았습니다.");
                return;
            }

            GameObject go = Instantiate(_dropItemPrefab, pos, Quaternion.identity);
            NetworkObject no = go.GetComponent<NetworkObject>();
            if (no == null)
            {
                Debug.LogError("[LootManager] _dropItemPrefab에 NetworkObject 컴포넌트가 없습니다.");
                Destroy(go);
                return;
            }

            no.Spawn(destroyWithScene: true);

            // Spawn() 이후 NetworkVariable 쓰기 — Spawn 전 쓰기는 NGO에서 무시됨
            WorldDropItem drop = go.GetComponent<WorldDropItem>();
            if (drop != null)
                drop.Initialize(item);
            Debug.Log($"[LootManager] 드롭 스폰 — itemId={item.ItemId}, pos={pos}");
        }

        // ── 월드 상자 사전 배치 ────────────────────────────────────────────────

        /// <summary>
        /// 게임 시작(OnNetworkSpawn) 시 상자 스폰 포인트에 아이템을 배치한다.
        /// AC-4 구현.
        /// </summary>
        private void SpawnWorldItems()
        {
            if (_config == null)
            {
                Debug.LogWarning("[LootManager] LootConfig가 할당되지 않았습니다. 상자 배치 건너뜀.");
                return;
            }

            if (_chestSpawnPoints == null || _chestSpawnPoints.Length == 0) return;
            if (_config.ChestItemPool == null || _config.ChestItemPool.Length == 0) return;

            foreach (Transform spawnPoint in _chestSpawnPoints)
            {
                if (spawnPoint == null) continue;
                ItemData item = GetRandomChestItem();
                if (!item.IsValid) continue;
                SpawnDropItem(item, spawnPoint.position);
            }

            Debug.Log($"[LootManager] 월드 상자 배치 완료 — {_chestSpawnPoints.Length}개");
        }

        /// <summary>ChestItemPool에서 랜덤 아이템 하나를 선택한다.</summary>
        private ItemData GetRandomChestItem()
        {
            if (_config.ChestItemPool.Length == 0) return default;
            return _config.ChestItemPool[Random.Range(0, _config.ChestItemPool.Length)];
        }
    }

    // ── 드롭 아이템 컴포넌트 ────────────────────────────────────────────────────

    /// <summary>
    /// 드롭 프리팹에 붙이는 컴포넌트. 아이템 데이터를 보관한다.
    /// 획득 상호작용은 GeneralInteractionSystem(L2)이 담당한다 (이 스토리 범위 밖).
    /// </summary>
    public class WorldDropItem : NetworkBehaviour
    {
        /// <summary>서버에서 Spawn() 직후에 호출하여 아이템 데이터를 주입한다.</summary>
        public void Initialize(ItemData item)
        {
            Item.Value = item;
        }

        /// <summary>이 드롭 오브젝트가 나타내는 아이템 데이터. 모든 클라이언트에서 읽기 가능.</summary>
        public NetworkVariable<ItemData> Item { get; } = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
    }
}

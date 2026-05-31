// Implements: design/gdd/02-gameplay-systems.md — DisguiseSystem MVP
// Story: production/sprints/sprint-007.md#POLISH-002
// ADR-002: 서버 권위 — 모든 직업 정보는 서버에서만 관리

using BeTheKing.CoreServices;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 직업 기반 외형 매칭 시스템 MVP.
    /// NPCManager.OnCivilianNpcSpawned, PlayerManager.OnPlayerSpawnedWithJob 이벤트를 구독하여
    /// 서버 → ClientRpc 경로로 각 클라이언트의 MeshRenderer 머티리얼을 교체한다.
    /// 순환 의존성 방지: CoreServices → GameplaySystems 방향 금지 → 이벤트 역방향 구독 사용.
    /// </summary>
    public class DisguiseSystem : NetworkBehaviour
    {
        public static DisguiseSystem Instance { get; private set; }

        [Header("직업별 머티리얼 (index = jobId, 0 = 기본 회색)")]
        [Tooltip("jobId 0부터 순서대로 배정. PlayerManager의 JobId와 인덱스 일치 필수.")]
        [SerializeField] private Material[] _jobMaterials;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;
            NPCManager.OnCivilianNpcSpawned += HandleCivilianNpcSpawned;
            PlayerManager.OnPlayerSpawnedWithJob += HandlePlayerSpawnedWithJob;
        }

        public override void OnNetworkDespawn()
        {
            NPCManager.OnCivilianNpcSpawned -= HandleCivilianNpcSpawned;
            PlayerManager.OnPlayerSpawnedWithJob -= HandlePlayerSpawnedWithJob;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        // ── 이벤트 핸들러 ─────────────────────────────────────────────────────

        private void HandleCivilianNpcSpawned(ulong networkObjectId, int jobId)
        {
            ApplyNpcDisguiseClientRpc(networkObjectId, jobId);
        }

        private void HandlePlayerSpawnedWithJob(ulong clientId, ulong playerNetworkObjectId, int jobId)
        {
            var clientRpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            };
            ApplyPlayerDisguiseClientRpc(playerNetworkObjectId, jobId, clientRpcParams);
        }

        // ── ClientRpc ──────────────────────────────────────────────────────────

        [ClientRpc]
        private void ApplyNpcDisguiseClientRpc(ulong networkObjectId, int jobId)
        {
            ApplyMaterialToNetworkObject(networkObjectId, jobId);
        }

        [ClientRpc]
        private void ApplyPlayerDisguiseClientRpc(ulong networkObjectId, int jobId,
            ClientRpcParams clientRpcParams = default)
        {
            ApplyMaterialToNetworkObject(networkObjectId, jobId);
        }

        // ── 내부 헬퍼 ─────────────────────────────────────────────────────────

        private void ApplyMaterialToNetworkObject(ulong networkObjectId, int jobId)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var no))
                return;

            var renderer = no.GetComponentInChildren<MeshRenderer>();
            if (renderer == null) return;

            if (_jobMaterials == null || jobId < 0 || jobId >= _jobMaterials.Length) return;
            if (_jobMaterials[jobId] == null) return;

            renderer.material = _jobMaterials[jobId];
        }

        // ── 편집기 유효성 검사 ─────────────────────────────────────────────────

        /// <summary>Inspector에서 jobMaterials가 설정됐는지 확인.</summary>
        public bool IsConfigured => _jobMaterials != null && _jobMaterials.Length > 0;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_jobMaterials == null || _jobMaterials.Length == 0)
                Debug.LogWarning("[DisguiseSystem] _jobMaterials가 비어 있습니다. Inspector에서 설정하세요.");
        }
#endif
    }
}

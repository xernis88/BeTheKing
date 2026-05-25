// Implements: design/gdd/01-core-services.md — MapManager
// Story: production/epics/epic-core-services/story-002-map-zone-gate.md

using Unity.Netcode;
using UnityEngine;
using BeTheKing.Foundation;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 250×250 마름모형 맵의 5구역 경계와 성문 콜라이더를 관리한다.
    /// CoronationTrigger 이벤트 수신 시 성문을 개방한다.
    /// </summary>
    public class MapManager : NetworkBehaviour
    {
        public static MapManager Instance { get; private set; }

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("Gate")]
        [Tooltip("성문을 구성하는 콜라이더 배열. 활성=차단, 비활성=개방.")]
        [SerializeField] private Collider[] _gateColliders;

        [Header("Zones")]
        [Tooltip("5구역(중앙·북·동·남·서) 정의.")]
        [SerializeField] private ZoneData[] _zones;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        // ── NetworkBehaviour ───────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            // 게임 시작 시(1~2일차) 성문 폐쇄 보장 — AC-2
            SetGateColliders(enabled: true);
        }

        public override void OnNetworkDespawn() { }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 주어진 월드 위치가 속한 구역을 반환한다.
        /// 경계에 걸릴 경우 먼저 매칭되는 구역을 반환한다.
        /// </summary>
        /// <param name="position">쿼리할 월드 좌표</param>
        /// <returns>해당 <see cref="ZoneId"/>. 어느 구역에도 속하지 않으면 <see cref="ZoneId.None"/> 반환.</returns>
        public ZoneId GetZone(Vector3 position)
        {
            if (_zones == null) return ZoneId.None;

            foreach (ZoneData zone in _zones)
            {
                if (zone.Bounds.Contains(position))
                    return zone.Id;
            }

            return ZoneId.None;
        }

        // ── Gate control ───────────────────────────────────────────────────────

        /// <summary>성문을 개방한다. CoronationTrigger에서 호출. 서버 전용.</summary>
        public void OpenGates()
        {
            if (!IsServer) return;
            OpenGatesServerInternal();
        }

        /// <summary>서버에서 성문 콜라이더를 비활성화하고 클라이언트에 동기화한다.</summary>
        private void OpenGatesServerInternal()
        {
            SetGateColliders(enabled: false);
            if (IsSpawned) OpenGatesClientRpc();
        }

        [ClientRpc]
        private void OpenGatesClientRpc()
        {
            SetGateColliders(enabled: false);
        }

        /// <summary>
        /// 모든 게이트 콜라이더의 enabled 상태를 일괄 설정한다.
        /// enabled=true  → 콜라이더 활성(차단), enabled=false → 콜라이더 비활성(개방).
        /// </summary>
        private void SetGateColliders(bool enabled)
        {
            if (_gateColliders == null) return;

            foreach (Collider col in _gateColliders)
            {
                if (col != null)
                    col.enabled = enabled;
            }
        }
    }

    // ── Supporting types ───────────────────────────────────────────────────────

    /// <summary>5구역 식별자. GDD §3 MapManager 표 참조.</summary>
    public enum ZoneId
    {
        None    = 0,
        Central = 1,   // 중앙 — 성
        North   = 2,   // 북 — 대장간
        East    = 3,   // 동 — 서커스
        South   = 4,   // 남 — 연금술
        West    = 5,   // 서 — 뒷골목
    }

    /// <summary>
    /// 단일 구역의 식별자와 경계 박스 정의.
    /// Inspector에서 ZoneId와 Bounds를 직접 입력한다.
    /// </summary>
    [System.Serializable]
    public struct ZoneData
    {
        [Tooltip("구역 식별자")]
        public ZoneId Id;

        [Tooltip("구역 경계 (월드 좌표 기준 AABB)")]
        public Bounds Bounds;
    }
}

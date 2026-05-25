// Implements: design/gdd/02-gameplay-systems.md — SuspicionSystem
// Story: production/epics/epic-gameplay-systems/story-004-suspicion-system.md
// ADR: docs/architecture/ADR-009-suspicion-system-overlap-sphere.md
//
// 설계 결정:
//   Physics.OverlapSphereNonAlloc으로 GC 할당 없이 반경 내 관찰자 탐지.
//   Collider[32] 고정 버퍼 — 현재 최대 관찰자 수(자객 NPC 10 + 플레이어 20 = 30) 기준.
//   AssassinNPCAI(ISuspicionObserver)는 OnSuspicionDetected() 직접 호출.
//   플레이어 관찰자는 OnSuspicionEventClientRpc로 이벤트 전파.
//   Report() 호출 책임은 호출자(JobInteractionSystem, GeneralInteractionSystem, CombatSystem)에 위임.
//   직업 일치 상호작용 성공 시 호출자가 Report()를 호출하지 않음 — 이 클래스는 탐지 이벤트만 처리.

using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 수상행동 발생 위치 반경 내 관찰자(자객 NPC, 플레이어)에게 탐지 이벤트를 전파하는 컴포넌트를 구현하는 인터페이스.
    /// AssassinNPCAI 및 기타 관찰자 NPC가 구현한다.
    /// </summary>
    public interface ISuspicionObserver
    {
        /// <summary>
        /// 수상행동 탐지 이벤트 수신.
        /// <paramref name="actorClientId"/>가 저지른 수상행동이 <paramref name="position"/>에서 발생했음을 알린다.
        /// </summary>
        /// <param name="actorClientId">수상행동을 저지른 플레이어의 ClientId.</param>
        /// <param name="position">수상행동 발생 월드 위치.</param>
        void OnSuspicionDetected(ulong actorClientId, Vector3 position);
    }

    /// <summary>
    /// 수상행동 발생 시 반경 R 이내 관찰자에게 탐지 이벤트를 전파한다. 서버 권위적.
    /// <para>
    ///   - <see cref="Report"/>: 수상행동 보고 진입점. 서버에서만 실행.
    ///   - 반경 내 <see cref="ISuspicionObserver"/> 컴포넌트(자객 NPC 등)에게 직접 호출.
    ///   - 반경 내 플레이어에게 <see cref="OnSuspicionEventClientRpc"/>로 이벤트 전파.
    /// </para>
    /// <para>
    ///   ADR-009: OverlapSphereNonAlloc 버퍼 크기 32 — 관찰자 수 상한 변경 시 함께 조정.
    ///   ADR-009: _observerLayerMask 미설정 시 Awake에서 Assert로 조기 실패.
    /// </para>
    /// </summary>
    public class SuspicionSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. OnNetworkSpawn/OnNetworkDespawn에서 관리된다.</summary>
        public static SuspicionSystem Instance { get; private set; }

        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Detection")]
        [Tooltip("수상행동 탐지 반경(유닛). GDD 02: R = TBD, 범위 5~15. 기본값 10.")]
        [SerializeField] private float _detectionRadius = 10f;

        [Tooltip("관찰자 레이어 마스크. 자객 NPC 및 플레이어 레이어를 포함해야 한다. 미설정 시 Awake Assert 실패.")]
        [SerializeField] private LayerMask _observerLayerMask;

        // ── ADR-009: GC 없는 반경 탐지 버퍼 ──────────────────────────────────

        // ADR-009: 현재 최대 관찰자 수(자객 NPC 10 + 플레이어 20) 기준 32. 플레이어 수 상한 변경 시 함께 조정.
        private const int OverlapBufferSize = 32;
        private readonly Collider[] _overlapBuffer = new Collider[OverlapBufferSize];

        // ── 이벤트 (클라이언트 UI 구독용) ─────────────────────────────────────

        /// <summary>
        /// 이 클라이언트의 시야 반경 내에서 수상행동이 발생했을 때 발생.
        /// 클라이언트 UI가 구독하여 시각적 알림을 처리한다.
        /// 매개변수: actorClientId — 수상행동을 저지른 플레이어 ClientId, position — 발생 위치.
        /// </summary>
        public event System.Action<ulong, Vector3> OnSuspicionEvent;

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>현재 설정된 탐지 반경. 테스트 및 외부 시스템에서 읽기 가능.</summary>
        public float DetectionRadius => _detectionRadius;

        // ── Unity Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            // ADR-009: 레이어 마스크 미설정 시 조기 실패 — 런타임 침묵 버그 방지.
            Assert.AreNotEqual(0, (int)_observerLayerMask,
                "[SuspicionSystem] _observerLayerMask가 설정되지 않았습니다. " +
                "Inspector에서 자객 NPC 및 플레이어 레이어를 할당하세요.");
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 수상행동을 보고하고 반경 R 이내 관찰자에게 이벤트를 전파한다. 서버에서만 실행.
        /// <para>
        ///   GDD 02: 비직업 상호작용, 직업 불일치 상호작용 실패, 전투 행동 발생 시 호출.
        ///   GDD 02: 직업 일치 상호작용 성공 시 호출자(JobInteractionSystem)가 이 메서드를 호출하지 않는다.
        /// </para>
        /// </summary>
        /// <param name="actorClientId">수상행동을 저지른 플레이어의 ClientId.</param>
        /// <param name="position">수상행동 발생 월드 위치.</param>
        public void Report(ulong actorClientId, Vector3 position)
        {
            // ADR-009: IsServer 가드 필수 — 클라이언트 호출 시 즉시 반환.
            if (!IsServer) return;

            int count = Physics.OverlapSphereNonAlloc(position, _detectionRadius, _overlapBuffer, _observerLayerMask);

            for (int i = 0; i < count; i++)
            {
                Collider hit = _overlapBuffer[i];
                if (hit == null) continue;

                // 자객 NPC 등 ISuspicionObserver 구현체에 직접 호출.
                ISuspicionObserver observer = hit.GetComponent<ISuspicionObserver>();
                if (observer != null)
                {
                    observer.OnSuspicionDetected(actorClientId, position);
                    continue;
                }

                // 플레이어 NetworkObject에 ClientRpc로 이벤트 전파.
                NetworkObject netObj = hit.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    NotifySuspicionClientRpc(actorClientId, position, netObj.OwnerClientId);
                }
            }
        }

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 반경 내 플레이어에게 수상행동 탐지 이벤트를 전파한다.
        /// ADR-009: 모든 클라이언트가 수신하지만, LocalClientId가 일치하는 경우에만 이벤트를 발행한다.
        /// </summary>
        [ClientRpc]
        private void NotifySuspicionClientRpc(ulong actorClientId, Vector3 position, ulong targetClientId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClientId == targetClientId)
                OnSuspicionEvent?.Invoke(actorClientId, position);
        }
    }
}

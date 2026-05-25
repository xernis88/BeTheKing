// Implements: design/gdd/02-gameplay-systems.md — GeneralInteractionSystem (Hold Gauge)
// Story: production/epics/epic-gameplay-systems/story-006-general-interaction.md
// ADR: docs/architecture/ADR-010-general-interaction-hold-timer.md
// TR: TR-GAME-013
//
// 설계 결정:
//   클라이언트 소유 타이머 누적 + CompleteHoldServerRpc 서버 완료 패턴 (ADR-010).
//   _isHolding = false를 CompleteHoldServerRpc 호출 전에 클라이언트에서 설정 — 중복 발화 방지.
//   IGeneralInteractable 인터페이스로 상호작용 가능 오브젝트 추상화.
//   OnHoldStarted/OnHoldCancelled 이벤트로 PlayerController 이동 취소 연동.
//   모든 일반 상호작용 완료 시 SuspicionSystem.Report() 항상 호출 (GDD 02: 항상 수상행동).

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 일반 상호작용 가능 오브젝트(상자, 레버 등)가 구현하는 계약.
    /// 홀드 완료 시 서버에서 <see cref="OnInteractionComplete"/>가 호출된다.
    /// </summary>
    public interface IGeneralInteractable
    {
        /// <summary>홀드 완료 시 서버에서 호출되는 콜백.</summary>
        void OnInteractionComplete();

        /// <summary>서버 역참조에 사용되는 NetworkObject ID.</summary>
        ulong NetworkObjectId { get; }
    }

    /// <summary>
    /// 일반 오브젝트 상호작용의 홀드 게이지를 관리한다. 플레이어 NetworkObject에 부착.
    /// <para>
    ///   소유 클라이언트(IsOwner)가 Update()에서 holdProgress를 누적하고,
    ///   holdDuration 도달 시 클라이언트에서 _isHolding = false로 재진입을 차단한 뒤
    ///   CompleteHoldServerRpc를 발화한다.
    /// </para>
    /// <para>
    ///   ADR-010: _isHolding = false를 CompleteHoldServerRpc() 호출 전에 설정 (중복 발화 방지).
    ///   ADR-010: RequireOwnership = true (기본값) 유지.
    ///   ADR-010: SuspicionSystem.Report() 호출 순서 — OnInteractionComplete() 이후.
    /// </para>
    /// </summary>
    public class GeneralInteractionSystem : NetworkBehaviour
    {
        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Hold Interaction")]
        [Tooltip("홀드 완료까지 필요한 시간(초). TR-GAME-013. 기본값 2초 — 밸런스 시 확정.")]
        [SerializeField] private float _holdDuration = 2f;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private float _holdProgress;
        private bool _isHolding;
        private IGeneralInteractable _target;

        // ── 이벤트 (PlayerController 이동 잠금 구독) ──────────────────────────

        /// <summary>
        /// 홀드 시작 시 발행. PlayerController가 구독하여 이동 입력 모니터링을 시작한다.
        /// 이동 입력 감지 시 PlayerController가 CancelHold()를 호출하여 홀드를 취소한다.
        /// GDD 02: "홀드 중 이동·전투 입력 감지 시 홀드 취소"
        /// </summary>
        public event System.Action OnHoldStarted;

        /// <summary>
        /// 홀드 취소 또는 완료 시 발행. PlayerController가 구독하여 이동 입력 모니터링을 해제한다.
        /// </summary>
        public event System.Action OnHoldCancelled;

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>설정된 홀드 완료 시간(초). 테스트 및 UI에서 읽기 가능.</summary>
        public float HoldDuration => _holdDuration;

        /// <summary>현재 홀드 진행 시간(초). 0 이상 HoldDuration 이하.</summary>
        public float HoldProgress => _holdProgress;

        /// <summary>현재 홀드 진행 중 여부.</summary>
        public bool IsHolding => _isHolding;

        // ── Unity Lifecycle ────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isHolding || !IsOwner) return;

            _holdProgress += Time.deltaTime;

            if (_holdProgress >= _holdDuration)
            {
                // ADR-010 Guideline 1: _isHolding = false를 ServerRpc 호출 전에 설정 — 중복 발화 방지.
                _isHolding = false;
                CompleteHoldServerRpc(_target.NetworkObjectId);
            }
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 홀드 게이지를 시작한다. 소유 클라이언트에서 호출.
        /// <para>GDD 02: "일반 오브젝트 접근 시 홀드 게이지가 시작된다"</para>
        /// </summary>
        /// <param name="target">상호작용 대상. 완료 시 OnInteractionComplete()가 서버에서 호출된다.</param>
        public void StartHold(IGeneralInteractable target)
        {
            _isHolding = true;
            _target = target;
            _holdProgress = 0f;
            OnHoldStarted?.Invoke();
        }

        /// <summary>
        /// 홀드 게이지를 취소하고 진행도를 초기화한다. 소유 클라이언트에서 호출.
        /// <para>GDD 02: "홀드 중 ESC 또는 이동 입력 시 게이지 초기화된다. 키 해제로는 취소되지 않음."</para>
        /// </summary>
        public void CancelHold()
        {
            _isHolding = false;
            _holdProgress = 0f;
            OnHoldCancelled?.Invoke();
        }

        // ── ServerRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 홀드 완료를 서버에서 처리한다. RequireOwnership = true (기본값).
        /// <para>
        ///   ADR-010: SpawnedObjects로 역참조. NetworkObject가 Despawn된 경우 조기 반환.
        ///   ADR-010: OnInteractionComplete() → SuspicionSystem.Report() 순서 유지.
        /// </para>
        /// </summary>
        /// <param name="targetNetObjId">상호작용 대상의 NetworkObject ID.</param>
        [ServerRpc]
        private void CompleteHoldServerRpc(ulong targetNetObjId)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetNetObjId, out NetworkObject netObj))
            {
                Debug.LogWarning($"[GeneralInteractionSystem] 역참조 실패: NetworkObjectId={targetNetObjId}");
                return;
            }

            IGeneralInteractable interactable = netObj.GetComponent<IGeneralInteractable>();
            if (interactable == null)
            {
                Debug.LogWarning($"[GeneralInteractionSystem] IGeneralInteractable 없음: {netObj.name}");
                return;
            }

            // ADR-010 Guideline 4: OnInteractionComplete() → Report() 순서 유지.
            interactable.OnInteractionComplete();
            SuspicionSystem.Instance?.Report(OwnerClientId, transform.position);
        }
    }
}

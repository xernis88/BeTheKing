// Implements: design/gdd/02-gameplay-systems.md — StaminaSystem
// Story: production/epics/epic-gameplay-systems/story-001-stamina-system.md
// ADR: docs/architecture/ADR-005-stamina-system-network-variable.md
//
// 설계 결정:
//   NetworkVariable<float>으로 서버 권위 동기화. 클라이언트 예측 없음.
//   회복 타이머는 서버 전용 float (_lastConsumeTime).
//   클라이언트 UI는 OnCurrentChanged 이벤트를 구독하여 갱신한다.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 스태미나를 관리한다. 서버 권위적.
    /// <para>
    ///   - <see cref="TryConsume"/>: 서버에서 소모 시도. 성공 시 true 반환.
    ///   - <see cref="CanAct"/>: 스태미나 > 0 여부 확인 (이동·전투 시스템이 참조).
    ///   - 마지막 소모로부터 <see cref="RecoveryDelay"/>초 경과 시 자동 회복 시작.
    /// </para>
    /// </summary>
    public class StaminaSystem : NetworkBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        /// <summary>스태미나 최대값. GDD: 기본 100, 레벨업/특성으로 상한 증가 예정.</summary>
        public const float Max = 100f;

        /// <summary>마지막 소모 후 회복 시작까지의 대기 시간(초). GDD: 5초.</summary>
        private const float RecoveryDelay = 5f;

        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("초당 스태미나 회복량. 기본 10/s. 생존형 특성으로 증가 예정.")]
        [SerializeField] private float _recoveryRate = 10f;

        // ── 네트워크 상태 ──────────────────────────────────────────────────────

        // ADR-005: 서버 쓰기 / 클라이언트 읽기(read-only). NGO delta 최적화로 효율 동기화.
        private readonly NetworkVariable<float> _current = new(
            Max,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // ADR-005: 타이머는 매 프레임 변경 불필요 — NetworkVariable 불필요.
        private float _lastConsumeTime = float.NegativeInfinity;

        // ── 이벤트 (클라이언트 UI 구독용) ─────────────────────────────────────

        /// <summary>
        /// 스태미나 값이 변경될 때 발생. 클라이언트 UI 게이지가 구독한다.
        /// OnNetworkSpawn에서 구독, OnNetworkDespawn에서 해제해야 한다.
        /// </summary>
        public event System.Action<float, float> OnCurrentChanged;

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>현재 스태미나 값 (클라이언트 읽기 가능).</summary>
        public float Current => _current.Value;

        /// <summary>
        /// 스태미나가 1 이상인지 여부. MovementSystem·CombatSystem이 행동 가능 여부를 확인한다.
        /// GDD: 스태미나 0 상태에서 달리기/공격 불가.
        /// </summary>
        public bool CanAct => _current.Value > 0f;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            // ADR-005: 반드시 OnNetworkSpawn에서 구독해야 NGO 직렬화 이후 이벤트를 수신한다.
            _current.OnValueChanged += HandleCurrentChanged;
        }

        public override void OnNetworkDespawn()
        {
            _current.OnValueChanged -= HandleCurrentChanged;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 스태미나를 amount만큼 소모한다. 서버에서만 유효.
        /// </summary>
        /// <param name="amount">소모할 스태미나 양. 양수여야 한다.</param>
        /// <returns>소모 성공 시 true. 잔량 부족 또는 클라이언트 호출 시 false.</returns>
        public bool TryConsume(float amount)
        {
            // ADR-005: IsServer 가드 필수.
            if (!IsServer) return false;
            if (_current.Value < amount) return false;

            _current.Value -= amount;
            _lastConsumeTime = Time.time;
            return true;
        }

        // ── Unity Lifecycle ────────────────────────────────────────────────────

        private void Update()
        {
            // ADR-005: 회복 로직은 서버에서만 실행.
            if (!IsServer) return;
            if (_current.Value >= Max) return;
            if (Time.time - _lastConsumeTime < RecoveryDelay) return;

            // 프레임률 독립: Time.deltaTime 사용.
            _current.Value = Mathf.Min(Max, _current.Value + _recoveryRate * Time.deltaTime);
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private void HandleCurrentChanged(float previous, float current)
        {
            OnCurrentChanged?.Invoke(previous, current);
        }
    }
}

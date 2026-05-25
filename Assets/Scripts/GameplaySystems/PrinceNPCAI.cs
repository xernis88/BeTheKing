// Implements: design/gdd/02-gameplay-systems.md — PrinceNPCAI
// Story: production/epics/epic-gameplay-systems/story-008-prince-npc-ai.md
// Requirement: TR-GAME-011
//
// 설계 결정:
//   무적 플래그(_isInvincible)는 서버 전용 bool. 초기값 true.
//   _isActive 초기값 false. 대관식 이벤트(OnCoronationStarted) 수신 시 Activate() 호출.
//   Activate()는 서버 전용 — IsServer 가드 필수.
//   OnDeath() → _throneZone?.OpenFully() (VE-002 stub).
//   왕좌 영역 진입 플레이어 공격: MVP — 단순 추적 + 공격 (복잡성 확장은 Out of Scope).
//   체력/데미지 수치: SerializeField — 밸런스 단계 확정.

using BeTheKing.Foundation;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕자 NPC의 AI를 관리한다. 서버 권위적.
    /// <para>
    ///   - 대관식 전(<see cref="_isInvincible"/> = true): 모든 피해를 무시한다.
    ///   - <see cref="SessionTimeManager.OnCoronationStarted"/> 수신 시 활성화.
    ///   - 활성화 후 왕좌 영역 진입 플레이어를 공격한다.
    ///   - 처치 시 <see cref="ThroneZone.OpenFully"/>를 발행한다.
    /// </para>
    /// </summary>
    public class PrinceNPCAI : NetworkBehaviour
    {
        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("최대 체력. 기본 300. 밸런스 단계에서 확정.")]
        [SerializeField] private float _maxHp = 300f;

        [Tooltip("기본 공격 데미지. 기본 25. 밸런스 단계에서 확정.")]
        [SerializeField] private float _attackDamage = 25f;

        [Tooltip("공격 쿨다운(초). 기본 1.5s. 밸런스 단계에서 확정.")]
        [SerializeField] private float _attackCooldown = 1.5f;

        [Tooltip("플레이어 감지 반경(m). 기본 8m. 밸런스 단계에서 확정.")]
        [SerializeField] private float _detectionRadius = 8f;

        [Tooltip("공격 사거리(m). 기본 2m. 밸런스 단계에서 확정.")]
        [SerializeField] private float _attackRange = 2f;

        [Tooltip("이동 속도(m/s). 기본 3m/s. 밸런스 단계에서 확정.")]
        [SerializeField] private float _moveSpeed = 3f;

        [Header("References")]
        [Tooltip("처치 시 완전 개방할 왕좌 영역. VE-002 구현 전 stub.")]
        [SerializeField] private ThroneZone _throneZone;

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        /// <summary>
        /// 대관식 전 무적 상태. 서버 전용.
        /// true 동안 TakeDamage()는 항상 false를 반환한다.
        /// </summary>
        private bool _isInvincible = true;

        /// <summary>
        /// 활성화 여부. 서버 전용.
        /// false 동안 Update의 AI 루프가 실행되지 않는다.
        /// </summary>
        private bool _isActive = false;

        /// <summary>현재 체력. 서버 전용.</summary>
        private float _currentHp;

        /// <summary>마지막 공격 시각(Time.time). 서버 전용.</summary>
        private float _lastAttackTime = float.NegativeInfinity;

        /// <summary>현재 추적 대상. 서버 전용.</summary>
        private Transform _target;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            _currentHp = _maxHp;
        }

        public override void OnNetworkDespawn() { }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 피해를 처리한다. 서버에서만 유효.
        /// </summary>
        /// <param name="amount">피해량. 0 이상이어야 한다.</param>
        /// <returns>
        /// 피해가 실제로 적용되면 true.
        /// 무적 상태(_isInvincible)이거나 클라이언트 호출 시 false.
        /// </returns>
        public bool TakeDamage(float amount)
        {
            if (!IsServer) return false;
            if (_isInvincible) return false;
            if (_currentHp <= 0f) return false;

            _currentHp -= amount;

            if (_currentHp <= 0f)
            {
                _currentHp = 0f;
                OnDeath();
            }

            return true;
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 대관식 이벤트 수신 시 호출. 서버 전용.
        /// 무적 해제, 활성화, gameObject 활성화.
        /// CoronationTrigger에서 직접 호출된다.
        /// </summary>
        public void Activate()
        {
            if (!IsServer) return;

            // 중복 호출 방지 — 이미 활성화된 경우 무시.
            if (_isActive) return;

            _isInvincible = false;
            _isActive = true;
            gameObject.SetActive(true);
        }

        /// <summary>
        /// 처치 시 호출. 왕좌 영역 완전 개방 이벤트를 발행한다.
        /// </summary>
        private void OnDeath()
        {
            _isActive = false;
            _throneZone?.OpenFully();
        }

        // ── AI Update (서버 전용, MVP: 단순 추적 + 공격) ────────────────────────
        // REQUIRES: Prefab에 NetworkTransform (Server Authority, Interpolate=true) 컴포넌트 필요 — 없으면 클라이언트에 이동이 보이지 않음.

        private void Update()
        {
            // 서버 전용 + 활성화 상태에서만 AI 루프 실행.
            if (!IsServer) return;
            if (!_isActive) return;

            // 대상 갱신 — 감지 범위 내 가장 가까운 플레이어.
            _target = FindNearestPlayer();
            if (_target == null) return;

            float distance = Vector3.Distance(transform.position, _target.position);

            if (distance > _attackRange)
            {
                // 추적: 프레임률 독립 이동.
                transform.position = Vector3.MoveTowards(
                    transform.position,
                    _target.position,
                    _moveSpeed * Time.deltaTime
                );
            }
            else
            {
                // 공격 쿨다운 확인.
                if (Time.time - _lastAttackTime < _attackCooldown) return;

                _lastAttackTime = Time.time;
                AttackTarget(_target);
            }
        }

        /// <summary>감지 반경 내 가장 가까운 플레이어 Transform을 반환한다.</summary>
        private Transform FindNearestPlayer()
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, _detectionRadius);
            Transform nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var hit in hits)
            {
                // 플레이어 레이어만 대상 — LayerMask 없이 태그로 MVP 처리.
                if (!hit.CompareTag("Player")) continue;

                float dist = Vector3.Distance(transform.position, hit.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = hit.transform;
                }
            }

            return nearest;
        }

        /// <summary>대상에게 공격 데미지를 적용한다.</summary>
        private void AttackTarget(Transform target)
        {
            // 대상 NetworkObject에서 피해 처리 컴포넌트를 찾아 호출.
            // CombatSystem 인터페이스 확정 전 직접 호출 — 추후 CombatSystem 연동 시 교체.
            var stamina = target.GetComponent<StaminaSystem>();
            if (stamina != null)
            {
                stamina.TryConsume(_attackDamage);
            }
        }
    }
}

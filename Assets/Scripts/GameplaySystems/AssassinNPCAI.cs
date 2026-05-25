// Implements: design/gdd/02-gameplay-systems.md — AssassinNPCAI
// Story: production/epics/epic-gameplay-systems/story-007-assassin-npc-ai.md
// Requirement: TR-GAME-009, TR-GAME-010

using BeTheKing.CoreServices;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 자객 NPC의 4-상태 FSM(Idle/Chase/Attack/Return) AI를 구현한다. 서버 권위적.
    /// <para>
    ///   - <see cref="ISuspicionObserver"/>를 구현해 <see cref="SuspicionSystem"/>으로부터 탐지 이벤트를 수신한다.
    ///   - IDLE 상태에서는 일반 NPC와 동일하게 보인다 (TODO: 배회 구현 예정).
    ///   - 타겟이 <see cref="_chaseAbandonDistance"/> 이상 이탈하면 RETURN 전환.
    ///   - 타겟이 <see cref="_attackRange"/> 이내 진입하면 ATTACK 전환.
    ///   - 처치 시 <see cref="NPCManager.ReturnAssassin"/>으로 풀 반환.
    /// </para>
    /// <para>
    ///   REQUIRES: Prefab에 NavMeshAgent 및 NetworkTransform(Server Authority) 컴포넌트 필요.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class AssassinNPCAI : NetworkBehaviour, ISuspicionObserver
    {
        // ── 내부 FSM 상태 정의 ────────────────────────────────────────────────

        private enum State { Idle, Chase, Attack, Return }

        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("추적 포기 거리(m). 타겟이 이 거리 이상 벗어나면 RETURN 전환. 기본 20m.")]
        [SerializeField] private float _chaseAbandonDistance = 20f;

        [Tooltip("공격 사거리(m). 타겟이 이 거리 이내 진입하면 ATTACK 전환. 기본 2m.")]
        [SerializeField] private float _attackRange = 2f;

        [Tooltip("이동 속도(m/s). 기본 3.5m/s.")]
        [SerializeField] private float _moveSpeed = 3.5f;

        [Tooltip("기본 공격 데미지. 기본 15. CombatSystem 연동 시 교체.")]
        [SerializeField] private float _attackDamage = 15f;

        [Tooltip("최대 체력. 기본 100.")]
        [SerializeField] private float _maxHp = 100f;

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        /// <summary>현재 FSM 상태. 서버 전용.</summary>
        private State _state = State.Idle;

        /// <summary>현재 추적 대상 ClientId. 서버 전용.</summary>
        private ulong _targetClientId;

        /// <summary>스폰 위치. OnNetworkSpawn에서 저장. 서버 전용.</summary>
        private Vector3 _spawnPos;

        /// <summary>현재 체력. 서버 전용.</summary>
        private float _currentHp;

        // ── 컴포넌트 참조 ─────────────────────────────────────────────────────

        private NavMeshAgent _agent;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// 컴포넌트 참조 초기화. NavMeshAgent는 RequireComponent로 보장.
        /// </summary>
        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        /// <summary>
        /// 스폰 위치 저장 및 PlayerManager.OnPlayerDied 이벤트 구독. 서버 전용.
        /// </summary>
        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            _spawnPos = transform.position;
            _currentHp = _maxHp;

            if (_agent != null)
                _agent.speed = _moveSpeed;

            if (PlayerManager.Instance != null)
                PlayerManager.Instance.OnPlayerDied += HandleTargetDied;
        }

        /// <summary>
        /// PlayerManager.OnPlayerDied 이벤트 구독 해제.
        /// </summary>
        public override void OnNetworkDespawn()
        {
            if (PlayerManager.Instance != null)
                PlayerManager.Instance.OnPlayerDied -= HandleTargetDied;
        }

        // ── Unity Update ──────────────────────────────────────────────────────

        private void Update() => Tick(Time.deltaTime);

        /// <summary>
        /// FSM 틱. 서버에서만 실행. 테스트에서 직접 주입 가능.
        /// </summary>
        /// <param name="deltaTime">경과 시간(초).</param>
        internal void Tick(float deltaTime)
        {
            if (!IsServer) return;

            switch (_state)
            {
                case State.Idle:   UpdateIdle();            break;
                case State.Chase:  UpdateChase();           break;
                case State.Attack: UpdateAttack(deltaTime); break;
                case State.Return: UpdateReturn();          break;
            }
        }

        // ── ISuspicionObserver 구현 ────────────────────────────────────────────

        /// <summary>
        /// SuspicionSystem에서 수상행동 탐지 이벤트를 수신한다.
        /// IDLE 상태일 때만 타겟을 설정하고 CHASE 전환. 이미 활성화된 경우 무시.
        /// </summary>
        /// <param name="actorClientId">수상행동을 저지른 플레이어의 ClientId.</param>
        /// <param name="position">수상행동 발생 월드 위치 (미사용 — 타겟 추적은 ConnectedClients로).</param>
        public void OnSuspicionDetected(ulong actorClientId, Vector3 position)
        {
            if (!IsServer) return;
            if (_state != State.Idle) return;

            _targetClientId = actorClientId;
            _state = State.Chase;
        }

        // ── 공개 전투 API ─────────────────────────────────────────────────────

        /// <summary>
        /// 피해를 처리한다. 서버에서만 유효.
        /// HP가 0 이하가 되면 <see cref="OnAssassinDeath"/>를 호출한다.
        /// </summary>
        /// <param name="amount">피해량. 0 이상이어야 한다.</param>
        public void TakeDamage(float amount)
        {
            if (!IsServer) return;
            if (_currentHp <= 0f) return;

            _currentHp -= amount;

            if (_currentHp <= 0f)
            {
                _currentHp = 0f;
                OnAssassinDeath();
            }
        }

        // ── FSM 상태별 업데이트 ────────────────────────────────────────────────

        /// <summary>
        /// IDLE: 일반 NPC 위장 행동.
        /// TODO: CivilianNpc 웨이포인트 배회 로직 구현 예정 (현재 제자리 대기).
        /// </summary>
        private void UpdateIdle()
        {
            // TODO: CivilianNpc 배회 행동과 동기화 — epic-core-services CivilianNpc 구현 후 연동.
        }

        /// <summary>
        /// CHASE: 타겟 추적. 포기 거리 이탈 시 RETURN, 공격 사거리 진입 시 ATTACK.
        /// </summary>
        private void UpdateChase()
        {
            Vector3 targetPos;
            if (!TryGetTargetPosition(out targetPos))
            {
                // 타겟을 찾을 수 없으면 RETURN
                TransitionToReturn();
                return;
            }

            float dist = Vector3.Distance(transform.position, targetPos);

            if (dist >= _chaseAbandonDistance)
            {
                TransitionToReturn();
                return;
            }

            if (dist <= _attackRange)
            {
                _state = State.Attack;
                _agent.ResetPath();
                return;
            }

            _agent.SetDestination(targetPos);
        }

        /// <summary>
        /// ATTACK: 타겟 뒤편 암습.
        /// TODO: CombatSystem 연동 전 직접 스태미나 공격 처리. 공격 쿨다운 미구현(MVP).
        /// </summary>
        private void UpdateAttack(float deltaTime)
        {
            Vector3 targetPos;
            if (!TryGetTargetPosition(out targetPos))
            {
                TransitionToReturn();
                return;
            }

            float dist = Vector3.Distance(transform.position, targetPos);

            // 타겟이 공격 사거리 밖으로 이탈하면 CHASE 재전환
            if (dist > _attackRange)
            {
                _state = State.Chase;
                return;
            }

            // TODO: CombatSystem 연동 시 아래 직접 호출 교체.
            // 현재는 스태미나 직접 차감 (PrinceNPCAI 패턴 참조).
            // var stamina = targetTransform.GetComponent<StaminaSystem>();
            // if (stamina != null) stamina.TryConsume(_attackDamage);
        }

        /// <summary>
        /// RETURN: 스폰 위치로 복귀. 1m 이내 도달 시 IDLE 전환.
        /// </summary>
        private void UpdateReturn()
        {
            _agent.SetDestination(_spawnPos);

            if (Vector3.Distance(transform.position, _spawnPos) < 1f)
            {
                _agent.ResetPath();
                _state = State.Idle;
            }
        }

        // ── 이벤트 핸들러 ─────────────────────────────────────────────────────

        /// <summary>
        /// PlayerManager.OnPlayerDied 수신 핸들러.
        /// 현재 추적/공격 중인 타겟이 사망했을 때 IDLE로 복귀한다.
        /// </summary>
        /// <param name="clientId">사망한 플레이어의 ClientId.</param>
        private void HandleTargetDied(ulong clientId)
        {
            if (!IsServer) return;
            if (clientId != _targetClientId) return;
            if (_state != State.Chase && _state != State.Attack) return;

            _agent.ResetPath();
            _state = State.Idle;
        }

        // ── 사망 처리 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 자객 NPC 처치 시 호출. 풀 반환 및 보상 드롭.
        /// </summary>
        private void OnAssassinDeath()
        {
            // 풀 반환
            NPCManager.Instance?.ReturnAssassin(GetComponent<NetworkObject>());

            // TODO: LootManager 구현 후 연동 — 돈·경험치 드롭 NetworkObject Spawn.
            // LootManager.Instance?.DropLoot(transform.position, lootTableId);
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 타겟 플레이어의 현재 위치를 가져온다.
        /// ConnectedClients 또는 SpawnManager에서 PlayerObject를 조회한다.
        /// </summary>
        /// <param name="position">타겟 위치 출력. 찾지 못할 경우 Vector3.zero.</param>
        /// <returns>타겟을 찾았으면 true, 없으면 false.</returns>
        private bool TryGetTargetPosition(out Vector3 position)
        {
            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.ConnectedClients.TryGetValue(_targetClientId, out var client) &&
                client.PlayerObject != null)
            {
                position = client.PlayerObject.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        /// <summary>RETURN 상태 진입 시 NavMeshAgent 목적지를 스폰 위치로 설정한다.</summary>
        private void TransitionToReturn()
        {
            _state = State.Return;
            _agent.SetDestination(_spawnPos);
        }

#if UNITY_EDITOR
        // ── 에디터 디버그 시각화 ─────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            // 추적 포기 거리 — 노란색
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, _chaseAbandonDistance);

            // 공격 사거리 — 빨간색
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, _attackRange);
        }
#endif
    }
}

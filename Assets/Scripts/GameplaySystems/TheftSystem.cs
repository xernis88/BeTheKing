// Implements: design/gdd/TheftSystem
// Story: production/epics/epic-gameplay-systems/story-010-theft-system.md
// ADR: docs/architecture/ADR-012-fame-system-server-dictionary.md (동일 서버 Dictionary 패턴)
//
// 설계 결정:
//   도둑질 시도 결과는 서버 전용 Dictionary<ulong, float>로 쿨타임 관리.
//   발각 시 FameSystem.LoseFame 호출. 피해 적용은 CombatSystem 구현 후 연동 예정.
//   아이템/골드 이전은 Out of Scope (story-010 명시).

using Unity.Netcode;
using System.Collections.Generic;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>도둑질 시도의 결과를 나타낸다.</summary>
    public enum TheftResult
    {
        /// <summary>도둑질 성공. 아이템/골드 이전은 호출자가 처리한다.</summary>
        Success,

        /// <summary>도둑질 도중 발각됨. 명성치 패널티가 적용된다.</summary>
        Detected,

        /// <summary>쿨타임 중 재시도. 시도 자체가 무효화된다.</summary>
        OnCooldown
    }

    /// <summary>
    /// 플레이어 간 도둑질 시도를 서버 권위적으로 처리한다.
    /// <para>
    ///   - <see cref="AttemptTheft"/>: 도둑질을 시도한다. 결과(성공/발각/쿨타임)를 반환한다.
    ///   - 발각 시 <see cref="FameSystem"/>을 통해 도둑의 명성치를 감소시킨다.
    ///   - 피해 적용(<c>_failureDamage</c>)은 CombatSystem 구현 후 연동 예정.
    /// </para>
    /// </summary>
    public class TheftSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. OnNetworkSpawn에서 서버 전용으로 등록된다.</summary>
        public static TheftSystem Instance { get; private set; }

        // ── 설정값 (Inspector / 외부 config에서 튜닝 가능) ─────────────────────

        [SerializeField]
        [Tooltip("동일 도둑이 연속 시도하기까지 필요한 최소 대기 시간(초).")]
        private float _theftCooldown = 5f;

        [SerializeField]
        [Tooltip("도둑질 도중 발각될 확률. 0 = 항상 성공, 1 = 항상 발각.")]
        [Range(0f, 1f)]
        private float _detectionChance = 0.4f;

        [SerializeField]
        [Tooltip("발각 시 도둑에게 적용할 피해량. CombatSystem 연동 후 사용됨.")]
        private float _failureDamage = 20f;

        [SerializeField]
        [Tooltip("발각 시 도둑에게 부과할 명성치 패널티.")]
        private int _famePenalty = 15;

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // 각 도둑(clientId)의 마지막 도둑질 시도 시각(Time.time).
        private readonly Dictionary<ulong, float> _lastAttemptTime = new();

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (IsServer) Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && Instance == this) Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 도둑질을 시도한다. 서버에서만 유효.
        /// </summary>
        /// <param name="thieverId">도둑질을 시도하는 클라이언트 ID.</param>
        /// <param name="targetId">도둑질 대상 클라이언트 ID.</param>
        /// <returns>
        ///   <see cref="TheftResult.OnCooldown"/>: 쿨타임 미경과.<br/>
        ///   <see cref="TheftResult.Detected"/>: 발각됨 (명성치 패널티 적용).<br/>
        ///   <see cref="TheftResult.Success"/>: 성공 (아이템/골드 이전은 호출자 책임).
        /// </returns>
        public TheftResult AttemptTheft(ulong thieverId, ulong targetId)
        {
            // ADR-012: IsServer 가드 필수. 클라이언트 호출 시 기본값 반환.
            if (!IsServer) return default;

            // 쿨타임 체크
            float lastAttempt = _lastAttemptTime.GetValueOrDefault(thieverId, float.NegativeInfinity);
            if (Time.time - lastAttempt < _theftCooldown)
            {
                return TheftResult.OnCooldown;
            }

            // 쿨타임 갱신
            _lastAttemptTime[thieverId] = Time.time;

            // 발각 여부 판정
            if (Random.value < _detectionChance)
            {
                // 명성치 패널티 적용
                FameSystem.Instance?.LoseFame(thieverId, _famePenalty);

                // TODO: CombatSystem 구현 후 연동 — 도둑에게 _failureDamage 적용
                // CombatSystem.Instance?.ApplyDamage(thieverId, _failureDamage);

                return TheftResult.Detected;
            }

            // 성공 — 아이템/골드 이전은 Out of Scope (story-010)
            return TheftResult.Success;
        }
    }
}

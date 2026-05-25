// Implements: design/gdd/03-progression-economy.md — LevelSystem
// Story: production/epics/epic-progression-economy/story-002-level-system.md
// ADR: docs/architecture/ADR-011-level-system-server-dictionary.md
// TR: TR-PROG-002, TR-PROG-003
//
// 설계 결정:
//   서버 전용 Dictionary 2개 (_playerLevel, _lastLevelUpDay)로 레벨 상태 관리 (ADR-006 동일 계열).
//   검증 순서: 날짜 → 레벨 상한 → CurrencySystem.TrySpend() (금화는 마지막 차감).
//   ApplyStatsClientRpc에서 clientId 필터링으로 타 플레이어 노출 차단.
//   MaxLevel = 3 (GDD: 3일 게임, 일 1회 레벨업).

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 레벨업을 관리한다. 서버에서만 상태를 소유하며, 레벨업 성공 시 스탯 증분을 ClientRpc로 동기화한다.
    /// <para>
    ///   ADR-011: 서버 Dictionary + ClientRpc 패턴. 1일 1회 제한 및 최대 레벨 3 검증은 서버에서 수행.
    ///   CurrencySystem.TrySpend()로 비용 차감 — 검증 통과 후 마지막에 실행.
    /// </para>
    /// </summary>
    public class LevelSystem : NetworkBehaviour
    {
        // ── 상수 ──────────────────────────────────────────────────────────────

        /// <summary>GDD: 3일 게임 기준 최대 레벨업 횟수.</summary>
        public const int MaxLevel = 3;

        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Level Up Stats")]
        [Tooltip("레벨업당 MaxHP 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _hpPerLevel = 20f;

        [Tooltip("레벨업당 공격력 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _attackPerLevel = 5f;

        [Tooltip("레벨업당 MaxStamina 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _staminaPerLevel = 10f;

        [Header("Level Up Cost")]
        [Tooltip("레벨업 비용 (금화). 밸런스 시 확정.")]
        [SerializeField] private int _levelUpCost = 50;

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        private readonly Dictionary<ulong, int> _playerLevel = new();
        private readonly Dictionary<ulong, int> _lastLevelUpDay = new();

        // ── 싱글턴 ────────────────────────────────────────────────────────────

        /// <summary>서버에서 LevelSystem을 참조할 때 사용. 서버 전용.</summary>
        public static LevelSystem Instance { get; private set; }

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>레벨업 비용 (금화). 서버 전용.</summary>
        public int LevelUpCost => _levelUpCost;

        // ── Unity Lifecycle ────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API (서버 전용) ────────────────────────────────────────────────

        /// <summary>
        /// 레벨업을 시도한다. 서버에서만 호출해야 한다.
        /// <para>
        ///   검증 순서: 날짜 → 레벨 상한(MaxLevel) → CurrencySystem.TrySpend().
        ///   성공 시 ApplyStatsClientRpc로 해당 클라이언트에 스탯 증분을 전달한다.
        /// </para>
        /// </summary>
        /// <param name="clientId">레벨업 요청 플레이어의 clientId.</param>
        /// <returns>레벨업 성공 여부.</returns>
        public bool TryLevelUp(ulong clientId)
        {
            if (!IsServer) return false;

            int today = SessionTimeManager.Instance?.CurrentDay ?? 0;

            // ADR-011 Guideline 1: 날짜 제한
            _lastLevelUpDay.TryGetValue(clientId, out int lastDay);
            if (lastDay >= today) return false;

            // ADR-011 Guideline 2: 레벨 상한
            int currentLevel = _playerLevel.GetValueOrDefault(clientId);
            if (currentLevel >= MaxLevel) return false;

            // ADR-011 Guideline 3: 금화 차감 (마지막 검증)
            if (CurrencySystem.Instance == null) return false;
            if (!CurrencySystem.Instance.TrySpend(clientId, _levelUpCost)) return false;

            _lastLevelUpDay[clientId] = today;
            _playerLevel[clientId] = currentLevel + 1;

            ApplyStatsClientRpc(clientId, _hpPerLevel, _attackPerLevel, _staminaPerLevel);
            return true;
        }

        /// <summary>
        /// 플레이어의 현재 레벨을 반환한다. 서버 전용.
        /// </summary>
        /// <param name="clientId">조회할 플레이어의 clientId.</param>
        public int GetLevel(ulong clientId) => _playerLevel.GetValueOrDefault(clientId);

        /// <summary>
        /// 테스트용: 레벨업 마지막 일자를 직접 설정한다. 서버 전용.
        /// </summary>
        internal void SetLastLevelUpDay(ulong clientId, int day) => _lastLevelUpDay[clientId] = day;

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 레벨업 스탯 증분을 해당 클라이언트에 적용한다.
        /// <para>
        ///   ADR-011 Guideline 4: clientId 필터링으로 수신자를 제한 — 타 플레이어 레벨 노출 차단.
        ///   PlayerStats 컴포넌트가 존재하면 MaxHP·AttackPower·MaxStamina를 갱신한다.
        ///   (PlayerStats는 Out of Scope 범위 — 인터페이스 연동은 별도 스토리)
        /// </para>
        /// </summary>
        [ClientRpc]
        private void ApplyStatsClientRpc(ulong clientId, float hpDelta, float attackDelta, float staminaDelta)
        {
            // ADR-011 Guideline 4: 수신자 필터링
            if ((ulong)OwnerClientId != clientId) return;

            Debug.Log($"[LevelSystem] 레벨업 스탯 적용: clientId={clientId} HP+{hpDelta} ATK+{attackDelta} STA+{staminaDelta}");
            // TODO: PlayerStats.Instance?.ApplyLevelUpStats(hpDelta, attackDelta, staminaDelta)
            // PlayerStats 시스템 구현 후 연동 (현재 Out of Scope)
        }
    }
}

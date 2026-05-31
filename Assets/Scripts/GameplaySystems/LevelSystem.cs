// Implements: design/gdd/03-progression-economy.md — LevelSystem
// Story: production/epics/epic-progression-economy/story-002-level-system.md
// ADR: docs/architecture/ADR-011-level-system-server-dictionary.md
// TR: TR-PROG-002, TR-PROG-003
//
// 설계 결정:
//   XP 축적 → 역치 도달 시 자동 레벨업 (골드 불필요, 1일 제한 없음).
//   xpToNextLevel(level) = baseXp * level 스케일링.
//   서버 전용 Dictionary로 XP·레벨 상태 관리 (ADR-011).
//   레벨업 시 ApplyStatsClientRpc로 해당 클라이언트에만 스탯 증분 동기화.
//   GainXP() 공개 API — JobInteractionSystem·GeneralInteractionSystem·CombatSystem에서 호출.

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// XP 축적 기반 레벨업 시스템. 서버에서만 상태를 소유하며, 레벨업 시 스탯 증분을 ClientRpc로 동기화한다.
    /// <para>
    ///   ADR-011: 서버 Dictionary + ClientRpc 패턴. 골드 비용 없음, 1일 제한 없음.
    ///   xpToNextLevel(level) = baseXpPerLevel × level (레벨마다 요구 XP 증가).
    ///   GainXP() 호출처: JobInteractionSystem(성공), GeneralInteractionSystem(완료), CombatSystem(행동).
    /// </para>
    /// </summary>
    public class LevelSystem : NetworkBehaviour
    {
        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Level Up Stats")]
        [Tooltip("레벨업당 MaxHP 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _hpPerLevel = 20f;

        [Tooltip("레벨업당 공격력 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _attackPerLevel = 5f;

        [Tooltip("레벨업당 MaxStamina 증가량. 밸런스 시 확정.")]
        [SerializeField] private float _staminaPerLevel = 10f;

        [Header("XP Scaling")]
        [Tooltip("레벨 1→2 기준 요구 XP. 레벨 N→N+1은 baseXpPerLevel × N. 밸런스 시 확정.")]
        [SerializeField] private int _baseXpPerLevel = 100;

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        private readonly Dictionary<ulong, int> _playerLevel = new();
        private readonly Dictionary<ulong, int> _playerXP = new();

        // ── 싱글턴 ────────────────────────────────────────────────────────────

        public static LevelSystem Instance { get; private set; }

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>레벨 N→N+1 요구 XP = baseXpPerLevel × N. 서버 전용.</summary>
        public int GetXpToNextLevel(int level) => _baseXpPerLevel * level;

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
        /// 플레이어에게 XP를 지급하고, 역치 도달 시 자동 레벨업을 처리한다. 서버에서만 호출.
        /// <para>
        ///   호출처: JobInteractionSystem(성공), GeneralInteractionSystem(완료), CombatSystem(행동).
        ///   레벨업은 1회 GainXP 호출당 최대 1회만 처리한다.
        /// </para>
        /// </summary>
        /// <param name="clientId">XP를 받을 플레이어의 clientId.</param>
        /// <param name="amount">지급할 XP 양.</param>
        public void GainXP(ulong clientId, int amount)
        {
            if (!IsServer) return;

            int currentXP = _playerXP.GetValueOrDefault(clientId) + amount;
            int currentLevel = _playerLevel.GetValueOrDefault(clientId);
            int required = GetXpToNextLevel(currentLevel + 1);

            if (currentXP >= required)
            {
                currentXP -= required;
                _playerLevel[clientId] = currentLevel + 1;
                _playerXP[clientId] = currentXP;
                ApplyStatsClientRpc(clientId, _hpPerLevel, _attackPerLevel, _staminaPerLevel);
            }
            else
            {
                _playerXP[clientId] = currentXP;
            }
        }

        /// <summary>플레이어의 현재 레벨을 반환한다. 서버 전용.</summary>
        public int GetLevel(ulong clientId) => _playerLevel.GetValueOrDefault(clientId);

        /// <summary>플레이어의 현재 누적 XP를 반환한다. 서버 전용.</summary>
        public int GetXP(ulong clientId) => _playerXP.GetValueOrDefault(clientId);

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 레벨업 스탯 증분을 해당 클라이언트에 적용한다.
        /// ADR-011 Guideline 4: clientId 필터링으로 수신자를 제한.
        /// </summary>
        [ClientRpc]
        private void ApplyStatsClientRpc(ulong clientId, float hpDelta, float attackDelta, float staminaDelta)
        {
            if (NetworkManager.Singleton.LocalClientId != clientId) return;

            Debug.Log($"[LevelSystem] 레벨업: clientId={clientId} HP+{hpDelta} ATK+{attackDelta} STA+{staminaDelta}");
            // TODO: PlayerStats.Instance?.ApplyLevelUpStats(hpDelta, attackDelta, staminaDelta)
        }
    }
}

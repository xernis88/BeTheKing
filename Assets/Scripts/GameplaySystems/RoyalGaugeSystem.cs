// Implements: design/gdd/04-victory-endgame.md — RoyalGaugeSystem
// Story: production/epics/epic-victory-endgame/story-002-throne-gauge.md
// Requirement: TR-VICT-003, TR-VICT-004, TR-VICT-006
//
// 설계 결정:
//   - 게이지는 서버에서만 계산 (IsServer 가드). 클라이언트는 ClientRpc로 동기화.
//   - VictoryManager(VE-003)는 미구현. OnVictoryConditionMet 이벤트로 decoupling.
//     VE-003 구현 후 VictoryManager가 이 이벤트를 구독한다.
//   - _inZone SortedSet: 승리 판정 순서를 clientId 오름차순으로 고정 (동시 도달 시 결정적).
//   - _victoryDeclared: 승리 이벤트 중복 발행 방지.
//   - Tick(float deltaTime): Update() 로직을 분리하여 단위 테스트 가능.
//
// GDD 규칙:
//   - 게이지 최대값 = 120 (2분)
//   - 상승 속도 = 1/초
//   - 영역 이탈 → 게이지 즉시 0 초기화 + cumulativePoints 가산
//   - 여러 플레이어 동시 진입 → 독립 게이지 (각 clientId별 별도 추적)

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕좌 영역 체류 게이지를 관리한다. 서버 권위적.
    /// <para>
    ///   플레이어가 왕좌 영역에 진입하면 1초당 1씩 게이지가 상승하고,
    ///   이탈 즉시 0으로 초기화되며 누적 포인트에 가산된다.
    ///   게이지가 <see cref="MaxGauge"/>에 도달하면 <see cref="OnVictoryConditionMet"/>을 발행한다.
    /// </para>
    /// <para>
    ///   VictoryManager(VE-003) 구현 전까지 <see cref="OnVictoryConditionMet"/>을
    ///   구독하여 승리 조건을 처리한다.
    /// </para>
    /// </summary>
    public class RoyalGaugeSystem : NetworkBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("게이지 최대값. GDD: 120 (2분). Inspector에서 튜닝 가능.")]
        [SerializeField] private float _maxGauge = 120f;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>게이지 최대값 (Inspector 설정값).</summary>
        public float MaxGauge => _maxGauge;

        /// <summary>
        /// 승리 조건 충족 시 발행된다. (clientId, reason)
        /// VE-003 VictoryManager가 이 이벤트를 구독하여 실제 승리 처리를 수행한다.
        /// </summary>
        public event Action<ulong, VictoryReason> OnVictoryConditionMet;

        /// <summary>
        /// 게이지 동기화 RPC가 클라이언트에 수신될 때 발행된다. (clientId, currentGauge, cumulative)
        /// VE-004 RoyalGaugeVisibility 및 UI-001 HUDManager에서 구독하여 HUD를 갱신한다.
        /// </summary>
        public static event Action<ulong, float, float> OnGaugeSyncedToClient;

        /// <summary>
        /// OnGaugeSyncedToClient 이벤트를 발행한다.
        /// SyncGaugeClientRpc 수신 시 호출되며, unit test에서 직접 호출 가능하다.
        /// </summary>
        internal static void RaiseOnGaugeSyncedToClient(ulong clientId, float gauge, float cumulative)
            => OnGaugeSyncedToClient?.Invoke(clientId, gauge, cumulative);

        // ── 서버 전용 상태 ──────────────────────────────────────────────────────

        /// <summary>clientId별 현재 게이지 (서버 전용).</summary>
        internal readonly Dictionary<ulong, float> _gauges = new();

        /// <summary>clientId별 누적 포인트 — 이탈마다 게이지 합산 (서버 전용).</summary>
        internal readonly Dictionary<ulong, float> _cumulative = new();

        /// <summary>
        /// 현재 왕좌 영역 내에 있는 clientId 집합 (서버 전용).
        /// SortedSet으로 관리하여 동시 120 도달 시 clientId 오름차순으로 결정적 승리자 선정.
        /// </summary>
        internal readonly SortedSet<ulong> _inZone = new();

        // _victoryDeclared: 승리 이벤트가 한 번 발행된 이후 추가 발행을 방지
        private bool _victoryDeclared;

        // ── 플레이어 진입 / 이탈 ───────────────────────────────────────────────

        /// <summary>
        /// 플레이어가 왕좌 영역에 진입했음을 기록한다. 서버 전용.
        /// <para><see cref="ThroneZoneManager.OnTriggerEnter"/>에서 호출된다.</para>
        /// </summary>
        /// <param name="clientId">진입한 플레이어의 NGO OwnerClientId.</param>
        public void OnPlayerEnter(ulong clientId)
        {
            if (!IsServer) return;
            _inZone.Add(clientId);
        }

        /// <summary>
        /// 플레이어가 왕좌 영역을 이탈했음을 처리한다. 서버 전용.
        /// <para>
        ///   이탈 즉시 게이지를 0으로 초기화하고, 이탈 전 게이지를 누적 포인트에 가산한다.
        ///   GDD Edge Case: 119에서 이탈 시 누적만 적립하고 승리 처리하지 않는다.
        /// </para>
        /// </summary>
        /// <param name="clientId">이탈한 플레이어의 NGO OwnerClientId.</param>
        public void OnPlayerExit(ulong clientId)
        {
            if (!IsServer) return;

            _inZone.Remove(clientId);

            float currentGauge = _gauges.GetValueOrDefault(clientId, 0f);
            float previousCumulative = _cumulative.GetValueOrDefault(clientId, 0f);
            float newCumulative = previousCumulative + currentGauge;

            _cumulative[clientId] = newCumulative;
            _gauges[clientId] = 0f;

            SyncGaugeClientRpc(clientId, 0f, newCumulative);
        }

        // ── Update 루프 ────────────────────────────────────────────────────────

        private void Update() => Tick(Time.deltaTime);

        /// <summary>
        /// 게이지 상승 및 승리 조건 판정 로직. 매 프레임 Update()에서 호출된다.
        /// <para>테스트에서 Time.deltaTime 대신 임의의 deltaTime을 주입할 수 있도록 분리.</para>
        /// </summary>
        /// <param name="deltaTime">경과 시간 (초). 실 게임: Time.deltaTime, 테스트: 주입값.</param>
        internal void Tick(float deltaTime)
        {
            if (!IsServer) return;
            if (_inZone.Count == 0) return;
            if (_victoryDeclared) return;

            // foreach 순회 중 컬렉션 수정 금지 — 승리 판정은 루프 종료 후 처리
            ulong? victoryWinner = null;

            foreach (ulong clientId in _inZone)
            {
                float newGauge = _gauges.GetValueOrDefault(clientId, 0f) + deltaTime;
                _gauges[clientId] = newGauge;

                SyncGaugeClientRpc(clientId, newGauge, _cumulative.GetValueOrDefault(clientId, 0f));

                if (newGauge >= _maxGauge)
                {
                    victoryWinner = clientId;
                    break; // 한 프레임에 한 명만 처리 (SortedSet으로 결정적 순서 보장)
                }
            }

            if (victoryWinner.HasValue)
            {
                _victoryDeclared = true;

                // 승리 시점 게이지를 누적에 반영 (이탈 없이 승리한 경우 cumulative 보정)
                ulong winner = victoryWinner.Value;
                _cumulative[winner] = _cumulative.GetValueOrDefault(winner, 0f) + _gauges[winner];

                OnVictoryConditionMet?.Invoke(winner, VictoryReason.GaugeFull);
            }
        }

        // ── 타임아웃 승리 지원 API ───────────────────────────────────────────────

        /// <summary>
        /// 누적 포인트(cumulative + 현재 inZone gauge) 기준 최고점 플레이어를 반환한다.
        /// <para>
        ///   영역 이탈 없이 아직 inZone 상태인 플레이어의 게이지도 cumulative에 합산하여 계산한다.
        ///   동점 시 clientId 오름차순으로 낮은 값이 승자.
        /// </para>
        /// <para>[TBD] GDD §5 — 동점 처리 정책 미확정. 현재 clientId 오름차순, 향후 생존 시간 기준으로 변경 예정.</para>
        /// </summary>
        /// <returns>최고 누적 포인트 보유자의 clientId. 추적된 플레이어가 없으면 null.</returns>
        public ulong? GetHighestCumulativePlayer()
        {
            ulong? best = null;
            float bestScore = float.MinValue;

            // _cumulative와 _inZone 모두 포함한 추적 대상 집합
            var allTracked = new HashSet<ulong>(_cumulative.Keys);
            foreach (ulong id in _inZone) allTracked.Add(id);

            // 결정적 순서를 위해 clientId 오름차순 정렬 (동점 시 낮은 clientId 우선)
            var sorted = new List<ulong>(allTracked);
            sorted.Sort();

            foreach (ulong id in sorted)
            {
                float score = _cumulative.GetValueOrDefault(id, 0f)
                            + _gauges.GetValueOrDefault(id, 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }

            return best;
        }

        // ── RPC ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 게이지 및 누적 포인트를 전체 클라이언트에 동기화한다.
        /// RoyalGaugeVisibility(VE-004)에서 구독하여 HUD를 갱신한다.
        /// </summary>
        /// <param name="clientId">대상 플레이어의 NGO OwnerClientId.</param>
        /// <param name="gauge">현재 게이지 (0 ~ MaxGauge).</param>
        /// <param name="cumulative">누적 포인트 합계.</param>
        [ClientRpc]
        internal void SyncGaugeClientRpc(ulong clientId, float gauge, float cumulative)
        {
            RaiseOnGaugeSyncedToClient(clientId, gauge, cumulative);
        }
    }
}

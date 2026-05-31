// Implements: design/gdd/04-victory-endgame.md — VictoryManager
// Story: production/epics/epic-victory-endgame/story-003-victory-manager.md
// Requirement: TR-VICT-005, TR-VICT-007, TR-VICT-009
//
// 설계 결정:
//   - 타이머 책임 없음: SessionTimeManager.OnSessionEnded 이벤트 구독으로 타임아웃 처리.
//     (스토리 노트의 _coronationElapsed 타이머 접근법은 채택하지 않음)
//   - 생존자 추적: PlayerManager.GetAliveCount/GetLastAlive 미존재.
//     내부 HashSet<ulong> _alivePlayers로 직접 관리.
//   - 중복 방지: _victoryDeclared 플래그로 첫 번째 승리 이후 추가 판정 차단.
//   - 서버 권위적: IsServer 가드 — 모든 판정은 서버에서만 실행.
//   - RoyalGaugeSystem 의존: Inspector 주입 [SerializeField].

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using BeTheKing.Foundation;
using BeTheKing.CoreServices;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 승리 조건 판정과 게임 종료 전환을 담당한다. 서버 권위적.
    /// <para>
    ///   판정 우선순위: (1) 게이지 120 도달 (RoyalGaugeSystem 이벤트), (2) 생존자 1명 (PlayerManager 이벤트),
    ///   (3) 세션 시간 종료 (SessionTimeManager 이벤트 → 누적 포인트 최고자 승리).
    /// </para>
    /// <para>
    ///   승리 선언 후 <see cref="AnnounceWinnerClientRpc"/>로 모든 클라이언트에 알리고
    ///   <see cref="GameStateManager"/>를 GameOver 상태로 전환한다.
    /// </para>
    /// </summary>
    public class VictoryManager : NetworkBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("왕좌 게이지 시스템 참조. Inspector에서 할당.")]
        [SerializeField] private RoyalGaugeSystem _royalGaugeSystem;

        // ── Singleton ──────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스 참조.</summary>
        public static VictoryManager Instance { get; private set; }

        /// <summary>
        /// 승리 공지 RPC가 클라이언트에 수신될 때 발행된다. (winnerId, reason)
        /// UI-001 HUDManager 및 UI-005 GameOverUI에서 구독한다.
        /// </summary>
        public static event Action<ulong, VictoryReason> OnVictoryAnnounced;

        /// <summary>
        /// OnVictoryAnnounced 이벤트를 발행한다.
        /// AnnounceWinnerClientRpc 수신 시 호출되며, unit test에서 직접 호출 가능하다.
        /// </summary>
        internal static void RaiseOnVictoryAnnounced(ulong winnerId, VictoryReason reason)
            => OnVictoryAnnounced?.Invoke(winnerId, reason);

        // ── 서버 전용 상태 ──────────────────────────────────────────────────────

        /// <summary>승리 선언 후 추가 판정 차단 플래그 (서버 전용).</summary>
        private bool _victoryDeclared;

        /// <summary>
        /// 현재 생존 중인 플레이어 집합 (서버 전용).
        /// PlayerManager.OnPlayerSpawned → Add, OnPlayerDied → Remove로 관리.
        /// </summary>
        private readonly HashSet<ulong> _alivePlayers = new();

        // ── 테스트용 공개 API ──────────────────────────────────────────────────

        /// <summary>승리가 이미 선언되었는지 여부. 테스트 및 외부 시스템 확인용.</summary>
        public bool IsVictoryDeclared => _victoryDeclared;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// NGO 스폰 시 이벤트 구독. 서버에서만 등록한다.
        /// </summary>
        public override void OnNetworkSpawn()
        {
            if (IsServer)
                Instance = this;

            if (!IsServer) return;

            // 게이지 120 도달 승리 구독
            if (_royalGaugeSystem != null)
                _royalGaugeSystem.OnVictoryConditionMet += HandleVictoryEvent;

            // 세션 시간 종료 구독 (타임아웃 승리)
            if (SessionTimeManager.Instance != null)
                SessionTimeManager.Instance.OnSessionEnded += HandleSessionEnded;

            // 생존자 추적 구독
            if (PlayerManager.Instance != null)
            {
                PlayerManager.Instance.OnPlayerSpawned += HandlePlayerSpawned;
                PlayerManager.Instance.OnPlayerDied    += HandlePlayerDied;
            }
        }

        /// <summary>
        /// NGO 디스폰 시 이벤트 구독 해제.
        /// </summary>
        public override void OnNetworkDespawn()
        {
            if (IsServer && Instance == this)
                Instance = null;

            if (_royalGaugeSystem != null)
                _royalGaugeSystem.OnVictoryConditionMet -= HandleVictoryEvent;

            if (SessionTimeManager.Instance != null)
                SessionTimeManager.Instance.OnSessionEnded -= HandleSessionEnded;

            if (PlayerManager.Instance != null)
            {
                PlayerManager.Instance.OnPlayerSpawned -= HandlePlayerSpawned;
                PlayerManager.Instance.OnPlayerDied    -= HandlePlayerDied;
            }
        }

        /// <summary>
        /// 오브젝트 파괴 시 싱글톤 참조 해제.
        /// </summary>
        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────────────────────

        /// <summary>
        /// RoyalGaugeSystem.OnVictoryConditionMet 핸들러.
        /// 게이지 120 도달 또는 다른 게이지 기반 승리 조건을 처리한다.
        /// </summary>
        /// <param name="winnerId">승리한 플레이어의 NGO OwnerClientId.</param>
        /// <param name="reason">승리 이유.</param>
        private void HandleVictoryEvent(ulong winnerId, VictoryReason reason)
        {
            DeclareVictory(winnerId, reason);
        }

        /// <summary>
        /// SessionTimeManager.OnSessionEnded 핸들러.
        /// 세션 시간 종료 시 누적 포인트 최고점 플레이어를 승자로 선정한다.
        /// </summary>
        private void HandleSessionEnded()
        {
            if (_victoryDeclared) return;

            ulong? winner = _royalGaugeSystem?.GetHighestCumulativePlayer();
            if (winner.HasValue)
                DeclareVictory(winner.Value, VictoryReason.TimeUp);
            else
                Debug.LogWarning("[VictoryManager] HandleSessionEnded: 추적된 플레이어가 없어 승리 선언 불가.");
        }

        /// <summary>
        /// PlayerManager.OnPlayerSpawned 핸들러.
        /// 스폰된 플레이어를 생존자 집합에 추가한다.
        /// </summary>
        /// <param name="clientId">스폰된 플레이어의 NGO OwnerClientId.</param>
        private void HandlePlayerSpawned(ulong clientId)
        {
            _alivePlayers.Add(clientId);
        }

        /// <summary>
        /// PlayerManager.OnPlayerDied 핸들러.
        /// 사망한 플레이어를 집합에서 제거하고 생존자 1명 조건을 검사한다.
        /// </summary>
        /// <param name="clientId">사망한 플레이어의 NGO OwnerClientId.</param>
        private void HandlePlayerDied(ulong clientId)
        {
            _alivePlayers.Remove(clientId);

            if (_victoryDeclared) return;

            if (_alivePlayers.Count == 1)
            {
                // HashSet은 Min 프로퍼티 없음 — Enumerator로 유일한 원소 획득
                ulong lastAlive = 0UL;
                foreach (ulong id in _alivePlayers) { lastAlive = id; break; }
                DeclareVictory(lastAlive, VictoryReason.LastSurvivor);
            }
        }

        // ── 승리 선언 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 승리를 선언한다. 서버 전용. 중복 호출은 무시된다.
        /// <para>
        ///   승리 선언 시 <see cref="AnnounceWinnerClientRpc"/>로 모든 클라이언트에 알리고
        ///   <see cref="GameStateManager.TransitionTo"/>로 GameOver 상태로 전환한다.
        /// </para>
        /// </summary>
        /// <param name="winnerId">승자의 NGO OwnerClientId.</param>
        /// <param name="reason">승리 이유.</param>
        internal void DeclareVictory(ulong winnerId, VictoryReason reason)
        {
            if (!IsServer) return;
            if (_victoryDeclared) return;

            _victoryDeclared = true;

            AnnounceWinnerClientRpc(winnerId, reason);
            GameStateManager.Instance?.TransitionTo(GameState.GameOver);

            Debug.Log($"[VictoryManager] 승리 선언 — winnerId={winnerId}, reason={reason}");
        }

        // ── RPC ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 승자 정보를 모든 클라이언트에 전파한다.
        /// 게임 오버 화면 표시는 epic-ui-presentation에서 이 RPC를 수신하여 처리한다.
        /// </summary>
        /// <param name="winnerId">승자의 NGO OwnerClientId.</param>
        /// <param name="reason">승리 이유.</param>
        [ClientRpc]
        internal void AnnounceWinnerClientRpc(ulong winnerId, VictoryReason reason)
        {
            RaiseOnVictoryAnnounced(winnerId, reason);
            Debug.Log($"[VictoryManager] 승자 공지 수신 — winnerId={winnerId}, reason={reason}");
        }
    }
}

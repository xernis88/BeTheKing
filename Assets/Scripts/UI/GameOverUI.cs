// Implements: design/gdd/05-ui-presentation.md — GameOverUI
// Story: production/epics/epic-ui-presentation/story-005-game-over-ui.md
// Requirement: TR-UI-008
//
// 설계 결정:
//   사망 감지: PlayerManager.OnPlayerDeathAnnounced (TECH-003 ClientRpc) 구독.
//   로컬 플레이어 판별: NetworkManager.Singleton.LocalClientId 비교.
//   생존 시간: SessionTimeManager.Elapsed (전체 세션 경과 시간 사용).
//   누적 왕권 포인트: RoyalGaugeSystem.OnGaugeSyncedToClient 구독으로 로컬 추적.
//   처치 수: MVP에서 별도 집계 시스템 미구현 → 0 표시. (tech-debt)
//
// ⚠ IMPORTANT — 프리팹 설정 필수:
//   이 컴포넌트는 OnPlayerDeathAnnounced / OnVictoryAnnounced가 일회성(edge) 이벤트이므로
//   InGame 진입 시점부터 enabled 상태를 유지해야 한다.
//   패널(_deathPanel, _victoryPanel)은 비활성으로 두되, 이 GameObject는 활성 상태여야 한다.

using BeTheKing.CoreServices;
using BeTheKing.Foundation;
using BeTheKing.GameplaySystems;
using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BeTheKing.UI
{
    /// <summary>
    /// 사망 오버레이와 승리 화면을 담당한다. Screen Space Canvas에 부착.
    /// <para>
    ///   사망: <see cref="PlayerManager.OnPlayerDeathAnnounced"/> 수신 후 로컬 플레이어 판별 → 사망 패널 표시.<br/>
    ///   승리: <see cref="VictoryManager.OnVictoryAnnounced"/> 수신 후 로컬 플레이어 판별 → 승리 패널 표시.
    /// </para>
    /// </summary>
    public class GameOverUI : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject _deathPanel;
        [SerializeField] private GameObject _victoryPanel;

        [Header("Stats — Death")]
        [SerializeField] private TMP_Text _survivalTimeText;
        [SerializeField] private TMP_Text _killCountText;
        [SerializeField] private TMP_Text _gaugePointsText;

        [Header("Button")]
        [SerializeField] private Button _lobbyButton;

        // 로컬 플레이어의 마지막 수신 누적 왕권 포인트
        private float _localCumulativeGauge;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Start()
        {
            _deathPanel?.SetActive(false);
            _victoryPanel?.SetActive(false);
            if (_lobbyButton != null) _lobbyButton.interactable = false;
        }

        private void OnEnable()
        {
            PlayerManager.OnPlayerDeathAnnounced  += HandlePlayerDeathAnnounced;
            VictoryManager.OnVictoryAnnounced      += HandleVictoryAnnounced;
            RoyalGaugeSystem.OnGaugeSyncedToClient += HandleGaugeSynced;
        }

        private void OnDisable()
        {
            PlayerManager.OnPlayerDeathAnnounced  -= HandlePlayerDeathAnnounced;
            VictoryManager.OnVictoryAnnounced      -= HandleVictoryAnnounced;
            RoyalGaugeSystem.OnGaugeSyncedToClient -= HandleGaugeSynced;
        }

        // ── Event Handlers ─────────────────────────────────────────────────────

        private void HandlePlayerDeathAnnounced(ulong clientId)
        {
            if (!IsLocalClient(clientId)) return;
            ShowDeathScreen();
        }

        private void HandleVictoryAnnounced(ulong winnerId, VictoryReason reason)
        {
            if (IsLocalClient(winnerId))
                ShowVictoryScreen();
        }

        private void HandleGaugeSynced(ulong clientId, float gauge, float cumulative)
        {
            if (IsLocalClient(clientId))
                _localCumulativeGauge = cumulative;
        }

        // ── Screen Display ─────────────────────────────────────────────────────

        private void ShowDeathScreen()
        {
            _deathPanel?.SetActive(true);

            float elapsed = SessionTimeManager.Instance != null
                ? SessionTimeManager.Instance.Elapsed
                : 0f;
            int min = Mathf.FloorToInt(elapsed / 60f);
            int sec = Mathf.FloorToInt(elapsed % 60f);

            if (_survivalTimeText != null) _survivalTimeText.text = $"생존 시간: {min}:{sec:D2}";
            if (_killCountText    != null) _killCountText.text    = "처치: 0";
            if (_gaugePointsText  != null) _gaugePointsText.text  = $"왕권 포인트: {_localCumulativeGauge:F0}";
            if (_lobbyButton      != null) _lobbyButton.interactable = true;
        }

        private void ShowVictoryScreen()
        {
            _victoryPanel?.SetActive(true);
            if (_lobbyButton != null) _lobbyButton.interactable = true;
        }

        // ── Button Handler ─────────────────────────────────────────────────────

        /// <summary>로비 복귀 버튼 클릭 핸들러. 네트워크 종료 후 다음 프레임에 로비 씬 전환.</summary>
        public void OnLobbyButtonClicked()
        {
            if (_lobbyButton != null) _lobbyButton.interactable = false; // 중복 클릭 방지
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.Shutdown();
            StartCoroutine(LoadLobbyNextFrame());
        }

        private IEnumerator LoadLobbyNextFrame()
        {
            yield return null; // NGO Shutdown 완료 후 씬 전환
            SceneManager.LoadScene("Lobby");
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        private static bool IsLocalClient(ulong clientId)
            => NetworkManager.Singleton != null
            && clientId == NetworkManager.Singleton.LocalClientId;
    }
}

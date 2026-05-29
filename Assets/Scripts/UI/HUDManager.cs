// Implements: design/gdd/02-gameplay-systems.md — HUD
// Story: production/epics/epic-ui-presentation/story-001-hud-manager.md
//
// 설계 결정:
//   - MonoBehaviour (NetworkBehaviour 아님): 렌더링 전용, 서버 로직 없음.
//   - OnEnable/OnDisable 구독 패턴: 씬 전환·오브젝트 비활성화 시 이벤트 누수 방지 (RoyalGaugeUI 패턴 준수).
//   - WeaponSystem 폴링: OnCurrentWeaponChanged 이벤트 미노출 → InvokeRepeating 0.5s 간격.
//   - 로컬 플레이어 StaminaSystem 바인딩: Start에서 즉시 시도 + OnPlayerSpawned 대기.
//   - JobId: MVP에서는 항상 0 → JobIconRegistry.Get(0) 고정.
//   - UI 텍스트: 모든 문자열은 하드코드 금지 원칙이나, 생존 포맷·등급 레이블은
//     현재 로컬라이제이션 시스템 미구현으로 임시 한국어 리터럴 사용.
//     TODO: 로컬라이제이션 시스템 도입 시 교체.

using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using BeTheKing.CoreServices;
using BeTheKing.GameplaySystems;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인게임 HUD를 관리한다. Screen Space Canvas에 부착.
    /// <para>
    ///   표시 항목: 스태미나 바, 보유 금화, 직업 아이콘, 무기 아이콘+등급, 생존 인원.
    /// </para>
    /// <para>
    ///   데이터 소스:
    ///   <list type="bullet">
    ///     <item><see cref="StaminaSystem.OnCurrentChanged"/> — 로컬 플레이어 PlayerObject에서 GetComponent.</item>
    ///     <item><see cref="CurrencySystem.OnBalanceUpdated"/> — 싱글톤 이벤트 구독.</item>
    ///     <item><see cref="PlayerManager.AliveCount"/> NetworkVariable — OnValueChanged 구독.</item>
    ///     <item><see cref="WeaponSystem.CurrentWeapon"/> — 0.5s InvokeRepeating 폴링.</item>
    ///   </list>
    /// </para>
    /// </summary>
    public class HUDManager : MonoBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        /// <summary>게임 최대 참가 인원. GDD 고정값.</summary>
        private const int TotalPlayers = 20;

        /// <summary>무기 아이콘 폴링 간격(초). WeaponSystem 변경 이벤트 미제공으로 폴링 사용.</summary>
        private const float WeaponPollInterval = 0.5f;

        // ── Inspector — 스태미나 ───────────────────────────────────────────────

        [Header("스태미나")]
        [Tooltip("스태미나 게이지 Slider. Min=0, Max=1 (정규화).")]
        [SerializeField] private Slider _staminaBar;

        // ── Inspector — 금화 ──────────────────────────────────────────────────

        [Header("금화")]
        [Tooltip("보유 금화량을 표시하는 TMP_Text.")]
        [SerializeField] private TMP_Text _goldText;

        // ── Inspector — 생존 인원 ─────────────────────────────────────────────

        [Header("생존 인원")]
        [Tooltip("'생존 X/20' 형식으로 표시하는 TMP_Text.")]
        [SerializeField] private TMP_Text _survivorText;

        // ── Inspector — 무기 ──────────────────────────────────────────────────

        [Header("무기")]
        [Tooltip("현재 장착 무기 아이콘 Image.")]
        [SerializeField] private Image _weaponIcon;

        [Tooltip("무기 등급 텍스트 (일반/희귀/영웅).")]
        [SerializeField] private TMP_Text _weaponGradeText;

        // ── Inspector — 직업 ──────────────────────────────────────────────────

        [Header("직업")]
        [Tooltip("현재 직업 아이콘 Image.")]
        [SerializeField] private Image _jobIcon;

        [Tooltip("직업 ID → 스프라이트 매핑 ScriptableObject.")]
        [SerializeField] private JobIconRegistry _jobIconRegistry;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private StaminaSystem _localStamina;
        private WeaponData    _lastWeapon;

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            ValidateInspectorFields();
        }

        private void Start()
        {
            // 초기 1회 작업: 직업 아이콘 갱신 + 이미 스폰된 경우 즉시 바인딩 시도.
            // OnPlayerSpawned 구독은 OnEnable에서 처리 (OnEnable/OnDisable 대칭 유지).
            if (NetworkManager.Singleton != null)
                TryBindLocalStamina(NetworkManager.Singleton.LocalClientId);

            // MVP: 직업 아이콘은 항상 jobId=0.
            RefreshJobIcon();
        }

        private void OnEnable()
        {
            if (CurrencySystem.Instance != null)
                CurrencySystem.Instance.OnBalanceUpdated += UpdateGold;

            // A1: AliveCount 구독 + 현재값 즉시 반영 (OnValueChanged는 다음 변경 때만 발화).
            // A3: OnPlayerSpawned 구독도 OnEnable에서 처리하여 OnDisable과 대칭 유지.
            if (PlayerManager.Instance != null)
            {
                PlayerManager.Instance.OnPlayerSpawned += TryBindLocalStamina;

                var aliveCount = PlayerManager.Instance.AliveCount;
                if (aliveCount != null)
                {
                    aliveCount.OnValueChanged += UpdateSurvivorCount;
                    UpdateSurvivorCount(0, aliveCount.Value);
                }
            }

            InvokeRepeating(nameof(RefreshWeaponDisplay), 0f, WeaponPollInterval);
        }

        private void OnDisable()
        {
            if (CurrencySystem.Instance != null)
                CurrencySystem.Instance.OnBalanceUpdated -= UpdateGold;

            if (PlayerManager.Instance != null)
            {
                PlayerManager.Instance.OnPlayerSpawned -= TryBindLocalStamina;

                var aliveCount = PlayerManager.Instance.AliveCount;
                if (aliveCount != null)
                    aliveCount.OnValueChanged -= UpdateSurvivorCount;
            }

            CancelInvoke(nameof(RefreshWeaponDisplay));

            // StaminaSystem 구독 해제.
            if (_localStamina != null)
                _localStamina.OnCurrentChanged -= UpdateStamina;
        }

        // ── 로컬 플레이어 바인딩 ───────────────────────────────────────────────

        /// <summary>
        /// 로컬 플레이어의 StaminaSystem을 찾아 이벤트를 구독한다.
        /// OnPlayerSpawned 콜백 또는 Start에서 직접 호출된다.
        /// </summary>
        /// <param name="clientId">스폰된 플레이어의 NGO ClientId.</param>
        private void TryBindLocalStamina(ulong clientId)
        {
            if (NetworkManager.Singleton == null) return;
            if (clientId != NetworkManager.Singleton.LocalClientId) return;

            var playerObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
            if (playerObj == null) return;

            // 이미 바인딩된 경우 중복 구독 방지.
            if (_localStamina != null)
                _localStamina.OnCurrentChanged -= UpdateStamina;

            _localStamina = playerObj.GetComponent<StaminaSystem>();
            if (_localStamina != null)
            {
                _localStamina.OnCurrentChanged += UpdateStamina;
                // 초기값 즉시 반영.
                UpdateStamina(StaminaSystem.Max, _localStamina.Current);
            }
            else
            {
                Debug.LogWarning("[HUDManager] 로컬 플레이어 PlayerObject에서 StaminaSystem을 찾지 못했습니다.");
            }
        }

        // ── UI 갱신 핸들러 ─────────────────────────────────────────────────────

        /// <summary>
        /// 스태미나 게이지를 갱신한다. StaminaSystem.OnCurrentChanged 구독 핸들러.
        /// </summary>
        /// <param name="previous">이전 스태미나 값 (미사용, 이벤트 시그니처 일치용).</param>
        /// <param name="current">현재 스태미나 값.</param>
        private void UpdateStamina(float previous, float current)
        {
            if (_staminaBar == null) return;
            _staminaBar.value = current / StaminaSystem.Max;
        }

        /// <summary>
        /// 금화 표시를 갱신한다. CurrencySystem.OnBalanceUpdated 구독 핸들러.
        /// </summary>
        /// <param name="balance">갱신된 금화 잔액.</param>
        private void UpdateGold(int balance)
        {
            if (_goldText == null) return;
            _goldText.text = $"{balance}";
        }

        /// <summary>
        /// 생존 인원 표시를 갱신한다. PlayerManager.AliveCount.OnValueChanged 구독 핸들러.
        /// </summary>
        /// <param name="previous">이전 생존 인원 (미사용, NetworkVariable 이벤트 시그니처 일치용).</param>
        /// <param name="next">현재 생존 인원.</param>
        private void UpdateSurvivorCount(int previous, int next)
        {
            if (_survivorText == null) return;
            _survivorText.text = $"생존 {next}/{TotalPlayers}";
        }

        /// <summary>
        /// 무기 아이콘과 등급 텍스트를 폴링으로 갱신한다.
        /// WeaponSystem이 변경 이벤트를 제공하지 않으므로 InvokeRepeating으로 0.5s마다 호출.
        /// A2: 동일 무기일 경우 sprite/text 재할당 생략 — Canvas dirty 불필요한 유발 방지.
        /// </summary>
        private void RefreshWeaponDisplay()
        {
            if (NetworkManager.Singleton == null) return;

            var playerObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
            if (playerObj == null) return;

            var weaponSystem = playerObj.GetComponent<WeaponSystem>();
            if (weaponSystem == null) return;

            WeaponData weapon = weaponSystem.CurrentWeapon;
            if (weapon == _lastWeapon) return;
            _lastWeapon = weapon;

            if (_weaponIcon != null)
                _weaponIcon.sprite = weapon?.icon;

            if (_weaponGradeText != null)
                _weaponGradeText.text = weapon != null ? GetGradeLabel(weapon.grade) : string.Empty;
        }

        /// <summary>
        /// 직업 아이콘을 갱신한다. MVP에서는 jobId=0 고정.
        /// </summary>
        private void RefreshJobIcon()
        {
            if (_jobIcon == null || _jobIconRegistry == null) return;
            // MVP: 모든 플레이어 jobId=0.
            _jobIcon.sprite = _jobIconRegistry.Get(0);
        }

        // ── 내부 유틸리티 ──────────────────────────────────────────────────────

        /// <summary>
        /// WeaponGrade 열거형을 한국어 레이블로 변환한다.
        /// TODO: 로컬라이제이션 시스템 도입 시 로컬라이제이션 키로 교체.
        /// </summary>
        private static string GetGradeLabel(WeaponGrade grade)
        {
            return grade switch
            {
                WeaponGrade.Common => "일반",
                WeaponGrade.Rare   => "희귀",
                WeaponGrade.Hero   => "영웅",
                _                  => string.Empty,
            };
        }

        // ── Inspector 검증 ─────────────────────────────────────────────────────

        /// <summary>
        /// Inspector SerializeField 누락 여부를 런타임 Awake에서 검증한다.
        /// </summary>
        private void ValidateInspectorFields()
        {
            if (_staminaBar == null)
                Debug.LogError("[HUDManager] _staminaBar가 Inspector에 연결되지 않았습니다.", this);
            if (_goldText == null)
                Debug.LogError("[HUDManager] _goldText가 Inspector에 연결되지 않았습니다.", this);
            if (_survivorText == null)
                Debug.LogError("[HUDManager] _survivorText가 Inspector에 연결되지 않았습니다.", this);
            if (_weaponIcon == null)
                Debug.LogError("[HUDManager] _weaponIcon이 Inspector에 연결되지 않았습니다.", this);
            if (_weaponGradeText == null)
                Debug.LogError("[HUDManager] _weaponGradeText가 Inspector에 연결되지 않았습니다.", this);
            if (_jobIcon == null)
                Debug.LogError("[HUDManager] _jobIcon이 Inspector에 연결되지 않았습니다.", this);
            if (_jobIconRegistry == null)
                Debug.LogError("[HUDManager] _jobIconRegistry가 Inspector에 연결되지 않았습니다.", this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_staminaBar == null)
                Debug.LogWarning("[HUDManager] _staminaBar 연결 필요.", this);
            if (_goldText == null)
                Debug.LogWarning("[HUDManager] _goldText 연결 필요.", this);
            if (_survivorText == null)
                Debug.LogWarning("[HUDManager] _survivorText 연결 필요.", this);
            if (_weaponIcon == null)
                Debug.LogWarning("[HUDManager] _weaponIcon 연결 필요.", this);
            if (_weaponGradeText == null)
                Debug.LogWarning("[HUDManager] _weaponGradeText 연결 필요.", this);
            if (_jobIcon == null)
                Debug.LogWarning("[HUDManager] _jobIcon 연결 필요.", this);
            if (_jobIconRegistry == null)
                Debug.LogWarning("[HUDManager] _jobIconRegistry 연결 필요.", this);
        }
#endif
    }
}

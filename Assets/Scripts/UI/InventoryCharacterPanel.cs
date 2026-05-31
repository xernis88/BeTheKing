// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md §캐릭터 패널
//
// 설계 결정:
//   낙관적 업데이트 + 롤백: InventoryEquipComponent.OnEquipResultReceived 구독.
//   InCombat 슬롯 잠금: CombatSystem.IsInCombat() 폴링 (변경 이벤트 미제공 가정).
//     TODO: CombatSystem이 변경 이벤트를 제공하면 폴링을 이벤트 구독으로 교체.
//   RollbackAnimation: DOTween 없이 코루틴으로 구현 — desaturation + Y-axis bounce 0.2초.
//   LockShakeCoroutine: 0.3초 진동 (UX Spec §Transitions).
//   스탯 바: LevelSystem/TraitTreeSystem은 서버 전용 — 클라이언트에서는
//     OnStatsUpdated 이벤트 수신 시 로컬 미러 값 사용.
//     TODO: PlayerStats 클라이언트 미러 시스템 구현 후 실제 값 연동.

using System.Collections;
using BeTheKing.CoreServices;
using BeTheKing.GameplaySystems;
using BeTheKing.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 좌측 캐릭터 패널.
    /// <para>
    ///   3D 캐릭터 뷰어 RawImage 연결, 복장/무기 장착 슬롯, 캐릭터 정보, 스탯 바를 표시한다.<br/>
    ///   InCombat 상태에서 슬롯을 잠그고 🔒 진동 피드백을 제공한다.<br/>
    ///   서버 장착 결과를 <see cref="InventoryEquipComponent.OnEquipResultReceived"/> 로 수신하여
    ///   롤백 애니메이션을 처리한다.
    /// </para>
    /// </summary>
    public class InventoryCharacterPanel : MonoBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        private const float RollbackDuration   = 0.2f; // UX Spec §서버 장착 거부
        private const float LockShakeDuration  = 0.3f; // UX Spec §Transitions
        private const float LockShakeAmplitude = 8f;   // px
        private const float EquipTimeoutSec    = 2f;
        private const float CombatPollInterval = 0.5f; // InCombat 상태 폴링 간격

        // ── Inspector — 장착 슬롯 ─────────────────────────────────────────────

        [Header("장착 슬롯")]
        [Tooltip("복장 슬롯 루트 Transform (잠금 아이콘 포함).")]
        [SerializeField] private RectTransform _costumeSlotRoot;

        [Tooltip("복장 슬롯 아이콘 Image.")]
        [SerializeField] private Image _costumeIcon;

        [Tooltip("복장 슬롯 잠금 오버레이 (🔒 아이콘). InCombat 시 활성화.")]
        [SerializeField] private GameObject _costumeLockOverlay;

        [Tooltip("복장 슬롯 스피너 (장착 대기 중 표시).")]
        [SerializeField] private GameObject _costumeSpinner;

        [Tooltip("무기 슬롯 루트 Transform.")]
        [SerializeField] private RectTransform _weaponSlotRoot;

        [Tooltip("무기 슬롯 아이콘 Image.")]
        [SerializeField] private Image _weaponIcon;

        [Tooltip("무기 슬롯 잠금 오버레이.")]
        [SerializeField] private GameObject _weaponLockOverlay;

        [Tooltip("무기 슬롯 스피너.")]
        [SerializeField] private GameObject _weaponSpinner;

        // ── Inspector — 캐릭터 정보 ───────────────────────────────────────────

        [Header("캐릭터 정보")]
        [Tooltip("캐릭터 이름 텍스트.")]
        [SerializeField] private TMP_Text _characterNameText;

        [Tooltip("'Lv.5  직업명' 형식 텍스트.")]
        [SerializeField] private TMP_Text _levelJobText;

        [Tooltip("XP 진행 바.")]
        [SerializeField] private Slider _xpBar;

        [Tooltip("XP 수치 텍스트 (예: '240 / 300').")]
        [SerializeField] private TMP_Text _xpText;

        // ── Inspector — 스탯 바 ───────────────────────────────────────────────

        [Header("스탯 바")]
        [SerializeField] private Slider _hpBar;
        [SerializeField] private TMP_Text _hpValueText;

        [SerializeField] private Slider _atkBar;
        [SerializeField] private TMP_Text _atkValueText;

        [SerializeField] private Slider _staminaBar;
        [SerializeField] private TMP_Text _staminaValueText;

        [Header("스탯 최대값 (Inspector 튜닝)")]
        [Tooltip("HP 슬라이더 최대값. 실제 MaxHP 시스템 미구현 시 임시 고정값.")]
        [SerializeField] private float _maxHp      = 200f;
        [Tooltip("ATK 슬라이더 최대값.")]
        [SerializeField] private float _maxAtk     = 100f;
        [Tooltip("Stamina 슬라이더 최대값.")]
        [SerializeField] private float _maxStamina = 100f;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private InventoryEquipComponent _equipComponent;
        private bool _inCombat;

        // 낙관적 업데이트 롤백을 위한 이전 슬롯 상태 스냅샷.
        private Sprite _prevCostumeSprite;
        private Sprite _prevWeaponSprite;

        // 현재 대기 중인 장착 코루틴.
        private Coroutine _costumeTimeoutCoroutine;
        private Coroutine _weaponTimeoutCoroutine;

        // 잠금 진동 코루틴 (중복 실행 방지).
        private Coroutine _costumeLockShakeCoroutine;
        private Coroutine _weaponLockShakeCoroutine;

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 인벤토리가 열릴 때 InventoryScreen에서 호출한다. 모든 UI를 현재 상태로 갱신.
        /// </summary>
        public void RefreshAll(InventoryEquipComponent equipComponent)
        {
            _equipComponent = equipComponent;
            RefreshCharacterInfo();
            RefreshStats();
            RefreshEquipSlots();
            RefreshCombatLock();
        }

        /// <summary>
        /// 그리드에서 복장 아이템이 클릭 장착 플로우로 전달될 때 호출된다.
        /// 낙관적 업데이트 → 타임아웃 코루틴 시작.
        /// </summary>
        public void OnCostumeEquipRequested(ItemGridSlot slot, Sprite optimisticSprite)
        {
            if (_inCombat)
            {
                TriggerLockShake(isCostume: true);
                return;
            }

            // 롤백용 스냅샷 저장.
            _prevCostumeSprite = _costumeIcon != null ? _costumeIcon.sprite : null;

            // 낙관적 즉시 반영.
            if (_costumeIcon != null)
                _costumeIcon.sprite = optimisticSprite;

            // 서버 요청.
            _equipComponent?.RequestEquipCostume(slot.ItemData.PrefabKey);

            // 타임아웃 코루틴 시작 (슬롯 위 스피너).
            if (_costumeTimeoutCoroutine != null) StopCoroutine(_costumeTimeoutCoroutine);
            _costumeTimeoutCoroutine = StartCoroutine(EquipTimeoutRoutine(isCostume: true));
        }

        /// <summary>
        /// 그리드에서 무기 아이템이 클릭 장착 플로우로 전달될 때 호출된다.
        /// </summary>
        public void OnWeaponEquipRequested(ItemGridSlot slot, Sprite optimisticSprite)
        {
            if (_inCombat)
            {
                TriggerLockShake(isCostume: false);
                return;
            }

            _prevWeaponSprite = _weaponIcon != null ? _weaponIcon.sprite : null;

            if (_weaponIcon != null)
                _weaponIcon.sprite = optimisticSprite;

            _equipComponent?.RequestEquipWeapon(slot.ItemData.PrefabKey);

            if (_weaponTimeoutCoroutine != null) StopCoroutine(_weaponTimeoutCoroutine);
            _weaponTimeoutCoroutine = StartCoroutine(EquipTimeoutRoutine(isCostume: false));
        }

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void OnEnable()
        {
            if (_equipComponent != null)
                _equipComponent.OnEquipResultReceived += HandleEquipResult;

            // InCombat 폴링 시작.
            InvokeRepeating(nameof(PollCombatState), 0f, CombatPollInterval);
        }

        private void OnDisable()
        {
            if (_equipComponent != null)
                _equipComponent.OnEquipResultReceived -= HandleEquipResult;

            CancelInvoke(nameof(PollCombatState));
            StopAllCoroutines();
        }

        // ── 이벤트 핸들러 ─────────────────────────────────────────────────────

        /// <summary>
        /// InventoryEquipComponent.OnEquipResultReceived 구독 핸들러.
        /// </summary>
        private void HandleEquipResult(InventoryEquipComponent.EquipSlot slot, InventoryEquipComponent.EquipResult result)
        {
            bool isCostume = slot == InventoryEquipComponent.EquipSlot.Costume;
            if (result == InventoryEquipComponent.EquipResult.Success)
            {
                // 확정: 타임아웃 코루틴 취소, 스피너 숨김.
                CancelEquipPending(isCostume);
            }
            else
            {
                // 거부: 롤백 애니메이션.
                CancelEquipPending(isCostume);
                StartCoroutine(RollbackAnimationRoutine(isCostume));
            }
        }

        // ── InCombat 폴링 ─────────────────────────────────────────────────────

        private void PollCombatState()
        {
            // CombatSystem은 서버 전용 — 클라이언트에서는 로컬 PlayerObject에서 상태를 읽어야 한다.
            // TODO: CombatSystem 클라이언트 미러 구현 후 실제 IsInCombat() 연동.
            // 현재 임시 구현: 로컬 플레이어 PlayerObject에서 NetworkVariable 폴링.
            bool combatState = false;
            if (NetworkManager.Singleton?.LocalClient?.PlayerObject != null)
            {
                var playerObj = NetworkManager.Singleton.LocalClient.PlayerObject;
                // TODO: IInCombatProvider 인터페이스 또는 NetworkVariable<bool> 추가 후 실제 값 사용.
                combatState = false; // placeholder
            }

            if (combatState != _inCombat)
            {
                _inCombat = combatState;
                RefreshCombatLock();
            }
        }

        // ── UI 갱신 ───────────────────────────────────────────────────────────

        private void RefreshCharacterInfo()
        {
            if (NetworkManager.Singleton?.LocalClient?.PlayerObject == null) return;

            // TODO: PlayerStats 미러 시스템 구현 후 실제 값 연동.
            // 현재: PlayerManager에서 이름 읽기 시도.
            if (_characterNameText != null)
                _characterNameText.text = "플레이어"; // TODO: PlayerManager에서 고유 캐릭터명 조회

            // TODO: LevelSystem 클라이언트 미러 구현 후 실제 레벨/직업 표시.
            if (_levelJobText != null)
                _levelJobText.text = "Lv.1  알 수 없음";

            if (_xpBar != null)
                _xpBar.value = 0f;
            if (_xpText != null)
                _xpText.text = "0 / 100";
        }

        private void RefreshStats()
        {
            // TODO: PlayerStats 클라이언트 미러 구현 후 실제 값 연동.
            SetStatBar(_hpBar, _hpValueText, 0f, _maxHp);
            SetStatBar(_atkBar, _atkValueText, 0f, _maxAtk);
            SetStatBar(_staminaBar, _staminaValueText, 0f, _maxStamina);
        }

        private void RefreshEquipSlots()
        {
            // TODO: WeaponSystem / DisguiseSystem 클라이언트 미러에서 현재 장착 아이템 읽기.
            // 현재: 빈 슬롯 표시.
            if (_costumeIcon != null) _costumeIcon.sprite = null;
            if (_weaponIcon  != null) _weaponIcon.sprite  = null;
        }

        private void RefreshCombatLock()
        {
            _costumeLockOverlay?.SetActive(_inCombat);
            _weaponLockOverlay?.SetActive(_inCombat);
        }

        // ── 스탯 바 헬퍼 ──────────────────────────────────────────────────────

        /// <summary>
        /// 스탯 바 + 수치 텍스트를 갱신한다. 외부(InventoryTraitPanel.OnStatsUpdated 등)에서도 호출 가능.
        /// </summary>
        public void SetStatBar(Slider bar, TMP_Text label, float value, float max)
        {
            if (bar != null)
            {
                bar.minValue = 0f;
                bar.maxValue = max;
                bar.value    = Mathf.Clamp(value, 0f, max);
            }
            if (label != null)
                label.text = $"{Mathf.RoundToInt(value)}";
        }

        // ── 잠금 진동 ─────────────────────────────────────────────────────────

        private void TriggerLockShake(bool isCostume)
        {
            ref Coroutine coroutine = ref isCostume
                ? ref _costumeLockShakeCoroutine
                : ref _weaponLockShakeCoroutine;

            RectTransform target = isCostume ? _costumeSlotRoot : _weaponSlotRoot;

            if (coroutine != null) StopCoroutine(coroutine);
            if (target != null)
                coroutine = StartCoroutine(LockShakeRoutine(target));
        }

        private IEnumerator LockShakeRoutine(RectTransform target)
        {
            Vector2 originalPos = target.anchoredPosition;
            float elapsed = 0f;

            while (elapsed < LockShakeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = elapsed / LockShakeDuration;
                float shake = Mathf.Sin(t * Mathf.PI * 8f) * LockShakeAmplitude * (1f - t);
                target.anchoredPosition = originalPos + Vector2.right * shake;
                yield return null;
            }

            target.anchoredPosition = originalPos;
        }

        // ── 장착 타임아웃 코루틴 ──────────────────────────────────────────────

        private IEnumerator EquipTimeoutRoutine(bool isCostume)
        {
            // 스피너 표시.
            GameObject spinner = isCostume ? _costumeSpinner : _weaponSpinner;
            spinner?.SetActive(true);

            yield return new WaitForSeconds(EquipTimeoutSec);

            // 타임아웃 도달 → 롤백 + 1회 재시도.
            spinner?.SetActive(false);

            bool retried = false;
            if (_equipComponent != null && !retried)
            {
                retried = true;
                // TODO: 재시도 API — InventoryEquipComponent.RetryLastEquip() 구현 후 호출.
                StartCoroutine(RollbackAnimationRoutine(isCostume));
            }
        }

        private void CancelEquipPending(bool isCostume)
        {
            if (isCostume)
            {
                if (_costumeTimeoutCoroutine != null)
                {
                    StopCoroutine(_costumeTimeoutCoroutine);
                    _costumeTimeoutCoroutine = null;
                }
                _costumeSpinner?.SetActive(false);
            }
            else
            {
                if (_weaponTimeoutCoroutine != null)
                {
                    StopCoroutine(_weaponTimeoutCoroutine);
                    _weaponTimeoutCoroutine = null;
                }
                _weaponSpinner?.SetActive(false);
            }
        }

        // ── 롤백 애니메이션 코루틴 ────────────────────────────────────────────

        /// <summary>
        /// 낙관적 업데이트 롤백: desaturation (0→1→0) + Y-axis bounce, 0.2초.
        /// DOTween 없이 코루틴으로 구현 (UX Spec §서버 장착 거부).
        /// </summary>
        private IEnumerator RollbackAnimationRoutine(bool isCostume)
        {
            Image iconImage  = isCostume ? _costumeIcon : _weaponIcon;
            Sprite prevSprite = isCostume ? _prevCostumeSprite : _prevWeaponSprite;
            RectTransform slotRoot = isCostume ? _costumeSlotRoot : _weaponSlotRoot;

            if (iconImage == null) yield break;

            float elapsed = 0f;
            Vector2 originalPos = slotRoot != null ? slotRoot.anchoredPosition : Vector2.zero;

            while (elapsed < RollbackDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / RollbackDuration);

                // Desaturation: Color 보간 (흰색 → 회색 → 흰색).
                float desatT = Mathf.Sin(t * Mathf.PI); // 0→1→0 sine curve
                float grayVal = Mathf.Lerp(1f, 0.4f, desatT);
                iconImage.color = new Color(grayVal, grayVal, grayVal, 1f);

                // Y-axis bounce.
                if (slotRoot != null)
                {
                    float bounceY = Mathf.Sin(t * Mathf.PI * 3f) * 6f * (1f - t);
                    slotRoot.anchoredPosition = originalPos + Vector2.up * bounceY;
                }

                yield return null;
            }

            // 원상 복원: 이전 스프라이트, 정상 색상, 원래 위치.
            iconImage.sprite = prevSprite;
            iconImage.color  = Color.white;
            if (slotRoot != null)
                slotRoot.anchoredPosition = originalPos;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_costumeSlotRoot == null)
                Debug.LogWarning("[InventoryCharacterPanel] _costumeSlotRoot 연결 필요.", this);
            if (_weaponSlotRoot == null)
                Debug.LogWarning("[InventoryCharacterPanel] _weaponSlotRoot 연결 필요.", this);
        }
#endif
    }
}

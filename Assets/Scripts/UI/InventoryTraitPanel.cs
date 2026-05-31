// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md §특성 탭
//
// 설계 결정:
//   TraitTreeSystem.InvestPointServerRpc: 기존 API가 TraitDirection enum을 받는다.
//     이 패널에서는 nodeId(int)를 TraitDirection으로 매핑하여 호출한다.
//     OQ-04(특성 리셋 여부) 미확정 → 리셋 버튼 없음.
//   방사형 3갈래 노드 위치: Inspector SerializeField로 설정 (하드코딩 회피).
//   OnStatsUpdated: TraitTreeSystem.SyncPointsClientRpc가 현재 stub 상태.
//     → SyncPointsClientRpc 확장 시 이 클래스의 OnTraitPointsSynced 연동 필요.
//     현재는 TraitNodeInvested 로컬 이벤트로 임시 처리.
//   서버 확인 후 스탯 반영: InventoryCharacterPanel.SetStatBar 직접 호출.

using System.Collections.Generic;
using BeTheKing.GameplaySystems;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 [특성] 탭 패널.
    /// <para>
    ///   방사형 3갈래 특성 트리(공격형/생존형/암살형) 노드를 표시하고,<br/>
    ///   클릭 시 <see cref="TraitTreeSystem.InvestPointServerRpc"/>를 호출한다.<br/>
    ///   서버 확인 완료 시 잔여 포인트와 스탯 바를 갱신한다.
    /// </para>
    /// </summary>
    public class InventoryTraitPanel : MonoBehaviour
    {
        // ── 내부 노드 정보 ────────────────────────────────────────────────────

        [System.Serializable]
        private class TraitNodeUI
        {
            [Tooltip("노드 버튼.")]
            public Button Button;

            [Tooltip("노드 레이블 텍스트.")]
            public TMP_Text Label;

            [Tooltip("투자량 카운터 텍스트 (예: '2').")]
            public TMP_Text CountText;

            [Tooltip("해금/잠금 시각적 구분 오버레이.")]
            public GameObject LockedOverlay;

            [Tooltip("이 노드에 대응하는 특성 방향.")]
            public TraitDirection Direction;

            [Tooltip("선행 노드 인덱스 목록 (이 노드가 잠금 해제되려면 모두 투자되어야 함). 없으면 비움.")]
            public List<int> PrerequisiteIndices = new();
        }

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("특성 포인트 표시")]
        [Tooltip("잔여 포인트 텍스트 (예: '남은 포인트: 3').")]
        [SerializeField] private TMP_Text _pointsText;

        [Header("특성 노드 목록 (방사형 3갈래)")]
        [Tooltip("공격형/생존형/암살형 노드 순으로 설정. 위치는 각 Button의 RectTransform으로 제어.")]
        [SerializeField] private List<TraitNodeUI> _nodes = new();

        [Header("스탯 패널 참조 (서버 확인 후 갱신)")]
        [Tooltip("InventoryCharacterPanel. 특성 투자 완료 시 스탯 바 갱신.")]
        [SerializeField] private InventoryCharacterPanel _characterPanel;

        [Tooltip("HP 바 참조 (직접 연결 방식).")]
        [SerializeField] private Slider _hpBar;
        [SerializeField] private TMP_Text _hpText;

        [Tooltip("ATK 바 참조.")]
        [SerializeField] private Slider _atkBar;
        [SerializeField] private TMP_Text _atkText;

        [Tooltip("STA 바 참조.")]
        [SerializeField] private Slider _staBar;
        [SerializeField] private TMP_Text _staText;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        // 클라이언트 측 표시용 미러 (서버 동기화 전까지 로컬 추적).
        private int _displayAvailablePoints;

        // 노드별 투자 카운트 로컬 미러.
        private readonly Dictionary<TraitDirection, int> _localInvested = new()
        {
            { TraitDirection.Attack,   0 },
            { TraitDirection.Survival, 0 },
            { TraitDirection.Assassin, 0 },
        };

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            RegisterNodeButtonCallbacks();
        }

        private void OnEnable()
        {
            // TODO: TraitTreeSystem.SyncPointsClientRpc 확장 시
            //       정적 이벤트 Action<ulong, int> OnPointsSynced를 여기서 구독.
            RefreshAllNodes();
        }

        private void OnDisable()
        {
            StopAllCoroutines();
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 패널이 열릴 때 호출. 서버로부터 수신한 잔여 포인트로 초기화.
        /// </summary>
        /// <param name="availablePoints">서버 동기화된 잔여 포인트 수.</param>
        public void Initialize(int availablePoints)
        {
            _displayAvailablePoints = availablePoints;
            RefreshPointsDisplay();
            RefreshAllNodes();
        }

        /// <summary>
        /// 서버에서 포인트 동기화 ClientRpc 수신 시 호출된다.
        /// TraitTreeSystem.SyncPointsClientRpc 확장 후 연동.
        /// </summary>
        public void OnTraitPointsSynced(int available)
        {
            _displayAvailablePoints = available;
            RefreshPointsDisplay();
            RefreshAllNodes();
        }

        // ── 노드 버튼 등록 ────────────────────────────────────────────────────

        private void RegisterNodeButtonCallbacks()
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                int nodeIndex = i; // 클로저 캡처 방지.
                TraitNodeUI node = _nodes[i];
                if (node.Button != null)
                    node.Button.onClick.AddListener(() => OnNodeClicked(nodeIndex));
            }
        }

        // ── 노드 클릭 처리 ────────────────────────────────────────────────────

        private void OnNodeClicked(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _nodes.Count) return;

            var node = _nodes[nodeIndex];

            // 잔여 포인트 확인.
            if (_displayAvailablePoints <= 0)
            {
                Debug.Log("[InventoryTraitPanel] 잔여 특성 포인트가 없습니다.");
                return;
            }

            // 선행 노드 투자 여부 확인.
            foreach (int prereqIdx in node.PrerequisiteIndices)
            {
                if (prereqIdx < 0 || prereqIdx >= _nodes.Count) continue;
                var prereqNode = _nodes[prereqIdx];
                if (_localInvested.GetValueOrDefault(prereqNode.Direction) <= 0)
                {
                    Debug.Log($"[InventoryTraitPanel] 선행 노드 미투자: index={prereqIdx}");
                    return;
                }
            }

            // 서버 RPC 호출.
            // TraitTreeSystem.InvestPointServerRpc는 RequireOwnership=false + SenderClientId 검증.
            if (TraitTreeSystem.Instance == null)
            {
                Debug.LogWarning("[InventoryTraitPanel] TraitTreeSystem.Instance가 없습니다. 서버 연결을 확인하세요.");
                return;
            }

            TraitTreeSystem.Instance.InvestPointServerRpc(node.Direction);

            // 낙관적 로컬 반영 (서버 검증 후 OnTraitPointsSynced로 확정됨).
            _displayAvailablePoints--;
            _localInvested[node.Direction]++;

            RefreshPointsDisplay();
            RefreshAllNodes();

            // TODO: OnTraitNodeResultClientRpc 수신 시 스탯 바 갱신.
            // 현재: 즉시 갱신 (낙관적) — 서버 롤백 시 OnTraitPointsSynced로 재동기화.
            ApplyTraitStatPreview(node.Direction);
        }

        // ── 노드 비주얼 갱신 ──────────────────────────────────────────────────

        private void RefreshAllNodes()
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                RefreshNodeUI(i);
            }
        }

        private void RefreshNodeUI(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _nodes.Count) return;
            var node = _nodes[nodeIndex];

            int invested = _localInvested.GetValueOrDefault(node.Direction);

            // 투자 카운트 표시.
            if (node.CountText != null)
                node.CountText.text = invested > 0 ? $"{invested}" : string.Empty;

            // 잠금 상태: 잔여 포인트 없거나 선행 노드 미투자.
            bool isLocked = _displayAvailablePoints <= 0 || !ArePrerequisitesMet(nodeIndex);

            if (node.LockedOverlay != null)
                node.LockedOverlay.SetActive(isLocked);

            if (node.Button != null)
                node.Button.interactable = !isLocked;

            // 노드 레이블 (방향명). TODO: 로컬라이제이션 키 교체.
            if (node.Label != null)
            {
                node.Label.text = node.Direction switch
                {
                    TraitDirection.Attack   => "공격형",
                    TraitDirection.Survival => "생존형",
                    TraitDirection.Assassin => "암살형",
                    _                       => string.Empty,
                };
            }
        }

        private bool ArePrerequisitesMet(int nodeIndex)
        {
            var node = _nodes[nodeIndex];
            foreach (int prereqIdx in node.PrerequisiteIndices)
            {
                if (prereqIdx < 0 || prereqIdx >= _nodes.Count) continue;
                var prereqNode = _nodes[prereqIdx];
                if (_localInvested.GetValueOrDefault(prereqNode.Direction) <= 0)
                    return false;
            }
            return true;
        }

        // ── 포인트 표시 ────────────────────────────────────────────────────────

        private void RefreshPointsDisplay()
        {
            if (_pointsText != null)
                _pointsText.text = $"남은 포인트: {_displayAvailablePoints}"; // TODO: 로컬라이제이션 키 교체
        }

        // ── 특성 투자 스탯 미리 반영 ─────────────────────────────────────────

        /// <summary>
        /// 낙관적 스탯 미리 반영. 실제 수치는 서버 OnTraitNodeResultClientRpc 수신 시 확정.
        /// TODO: 실제 스탯 증분값은 TraitTreeSystem 또는 PlayerStats 미러에서 조회.
        /// </summary>
        private void ApplyTraitStatPreview(TraitDirection direction)
        {
            // TODO: 실제 증분값 조회 후 SetStatBar 호출.
            // 현재: InventoryCharacterPanel.SetStatBar 직접 호출 방식 placeholder.
            if (_characterPanel == null) return;

            // 방향별 스탯 증분 예시값 (실제 시스템 연동 전 placeholder).
            switch (direction)
            {
                case TraitDirection.Attack:
                    // ATK 증가
                    _characterPanel.SetStatBar(_atkBar, _atkText, 0f, 100f); // TODO: 실제 값
                    break;
                case TraitDirection.Survival:
                    // HP 증가
                    _characterPanel.SetStatBar(_hpBar, _hpText, 0f, 200f); // TODO: 실제 값
                    break;
                case TraitDirection.Assassin:
                    // STA 증가
                    _characterPanel.SetStatBar(_staBar, _staText, 0f, 100f); // TODO: 실제 값
                    break;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_nodes == null || _nodes.Count == 0)
                Debug.LogWarning("[InventoryTraitPanel] _nodes가 비어 있습니다. Inspector에서 3개 노드를 설정하세요.", this);
        }
#endif
    }
}

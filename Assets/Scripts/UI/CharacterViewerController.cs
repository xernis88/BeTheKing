// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md
//
// 설계 결정:
//   On-Demand RenderTexture 패턴: 카메라를 상시 렌더하지 않고, 장착 변경/회전 입력 시에만
//   RequestRender()를 호출하여 LateUpdate에서 1프레임 수동 렌더 후 중단한다.
//   이유: 인벤토리가 닫힌 동안 GPU 사이클 낭비 방지.
//   camera.enabled = false 기본값: Inspector에서 카메라를 비활성 상태로 프리팹 저장할 것.

using UnityEngine;
using UnityEngine.UI;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 3D 캐릭터 뷰어 카메라를 On-Demand 방식으로 제어한다.
    /// <para>
    ///   RenderTexture → UI RawImage 흐름:
    ///   <list type="bullet">
    ///     <item><see cref="OnInventoryOpen"/> 호출 시 카메라 활성화 + 즉시 1프레임 렌더.</item>
    ///     <item><see cref="RequestRender"/> 호출 시 다음 LateUpdate에서 단일 프레임 렌더 후 중단.</item>
    ///     <item><see cref="OnInventoryClose"/> 호출 시 카메라 비활성화.</item>
    ///   </list>
    /// </para>
    /// </summary>
    public class CharacterViewerController : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("렌더 타겟")]
        [Tooltip("InventoryPreview 레이어만 렌더하는 전용 카메라. 기본값: disabled.")]
        [SerializeField] private Camera _previewCamera;

        [Tooltip("RenderTexture를 표시할 UI RawImage.")]
        [SerializeField] private RawImage _targetRawImage;

        [Tooltip("렌더 해상도. 기본 512x512.")]
        [SerializeField] private Vector2Int _renderResolution = new Vector2Int(512, 512);

        [Header("캐릭터 모델 루트")]
        [Tooltip("InventoryPreview 레이어에 배치된 캐릭터 모델 루트 Transform.")]
        [SerializeField] private Transform _characterRoot;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private RenderTexture _renderTexture;
        private bool _renderRequested;

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            ValidateInspectorFields();
            InitRenderTexture();
        }

        private void OnDestroy()
        {
            if (_renderTexture != null)
            {
                if (_previewCamera != null && _previewCamera.targetTexture == _renderTexture)
                    _previewCamera.targetTexture = null;
                _renderTexture.Release();
                Destroy(_renderTexture);
            }
        }

        private void LateUpdate()
        {
            if (!_renderRequested) return;
            _renderRequested = false;

            if (_previewCamera == null) return;
            _previewCamera.Render();
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 인벤토리가 열릴 때 호출한다. 카메라를 활성화하고 초기 프레임을 렌더한다.
        /// </summary>
        public void OnInventoryOpen()
        {
            if (_previewCamera != null)
                _previewCamera.enabled = true;

            RequestRender();
        }

        /// <summary>
        /// 인벤토리가 닫힐 때 호출한다. 카메라를 비활성화하여 GPU 비용을 절감한다.
        /// </summary>
        public void OnInventoryClose()
        {
            if (_previewCamera != null)
                _previewCamera.enabled = false;

            _renderRequested = false;
        }

        /// <summary>
        /// 다음 LateUpdate에서 1프레임 렌더를 예약한다.
        /// 장착 아이템 변경 또는 회전 입력 발생 시 호출한다.
        /// </summary>
        public void RequestRender()
        {
            _renderRequested = true;
        }

        /// <summary>
        /// 캐릭터 모델을 Y축으로 회전시키고 렌더를 예약한다.
        /// 마우스 드래그 회전 지원 시 외부에서 호출한다.
        /// </summary>
        /// <param name="deltaAngle">회전량(도). 양수=오른쪽, 음수=왼쪽.</param>
        public void RotateCharacter(float deltaAngle)
        {
            if (_characterRoot != null)
                _characterRoot.Rotate(Vector3.up, deltaAngle, Space.World);
            RequestRender();
        }

        // ── 내부 초기화 ────────────────────────────────────────────────────────

        private void InitRenderTexture()
        {
            if (_previewCamera == null || _targetRawImage == null) return;

            _renderTexture = new RenderTexture(
                _renderResolution.x,
                _renderResolution.y,
                16,
                RenderTextureFormat.ARGB32)
            {
                name = "InventoryPreviewRT"
            };
            _renderTexture.Create();

            _previewCamera.targetTexture = _renderTexture;
            _previewCamera.enabled = false; // On-Demand 패턴: 기본 비활성

            _targetRawImage.texture = _renderTexture;
        }

        // ── Inspector 검증 ─────────────────────────────────────────────────────

        private void ValidateInspectorFields()
        {
            if (_previewCamera == null)
                Debug.LogError("[CharacterViewerController] _previewCamera가 Inspector에 연결되지 않았습니다.", this);
            if (_targetRawImage == null)
                Debug.LogError("[CharacterViewerController] _targetRawImage가 Inspector에 연결되지 않았습니다.", this);
            if (_characterRoot == null)
                Debug.LogWarning("[CharacterViewerController] _characterRoot가 연결되지 않았습니다. 회전 기능이 비활성화됩니다.", this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_previewCamera == null)
                Debug.LogWarning("[CharacterViewerController] _previewCamera 연결 필요.", this);
            if (_targetRawImage == null)
                Debug.LogWarning("[CharacterViewerController] _targetRawImage 연결 필요.", this);
        }
#endif
    }
}

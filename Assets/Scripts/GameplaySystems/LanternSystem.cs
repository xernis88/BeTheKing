// Implements: design/gdd/02-gameplay-systems.md — LanternSystem
// Story: production/epics/epic-gameplay-systems/story-003-vision-lantern.md
// ADR: docs/architecture/ADR-014-vision-lantern-fow-render-feature.md
//
// 설계 결정:
//   NetworkVariable<bool>으로 서버 권위 동기화.
//   OnValueChanged → VisionSystem.Instance?.UpdateNightVision() 연동.
//   Toggle()은 IsOwner 가드 후 ServerRpc로 위임.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 등불 켜기/끄기 상태를 관리한다. 서버 권위적.
    /// <para>
    ///   - <see cref="Toggle"/>: 소유 클라이언트에서 호출. ServerRpc로 위임.
    ///   - <see cref="IsOn"/>: 현재 등불 상태. 클라이언트 읽기 가능.
    ///   - 상태 변경 시 <see cref="VisionSystem.UpdateNightVision"/>을 호출해 FOW 반경 갱신.
    /// </para>
    /// </summary>
    public class LanternSystem : NetworkBehaviour
    {
        // ── 네트워크 상태 ──────────────────────────────────────────────────────

        // ADR-014: 서버 쓰기 / 클라이언트 읽기. 등불 상태는 NGO delta 동기화.
        private readonly NetworkVariable<bool> _isOn = new(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

        // ── 싱글턴 ────────────────────────────────────────────────────────────

        /// <summary>로컬 플레이어의 LanternSystem 인스턴스. VisionSystem이 참조한다.</summary>
        public static LanternSystem Instance { get; private set; }

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>현재 등불 켜짐 여부. 클라이언트 읽기 가능.</summary>
        public bool IsOn => _isOn.Value;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            // ADR-014: OnNetworkSpawn에서 구독해야 NGO 직렬화 이후 이벤트를 수신한다.
            _isOn.OnValueChanged += HandleIsOnChanged;

            // 로컬 소유자의 인스턴스만 싱글턴으로 등록한다.
            if (IsOwner)
                Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            _isOn.OnValueChanged -= HandleIsOnChanged;

            if (IsOwner && Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 등불 상태를 토글한다. 소유 클라이언트에서만 유효.
        /// 내부적으로 ServerRpc를 통해 서버에서 상태를 변경한다.
        /// </summary>
        public void Toggle()
        {
            if (!IsOwner) return;
            ToggleServerRpc();
        }

        // ── ServerRpc ──────────────────────────────────────────────────────────

        /// <summary>서버에서 등불 상태를 반전시킨다.</summary>
        [ServerRpc]
        private void ToggleServerRpc()
        {
            _isOn.Value = !_isOn.Value;
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private void HandleIsOnChanged(bool previous, bool current)
        {
            // 등불 상태 변경 시 VisionSystem에 FOW 반경 갱신 요청.
            VisionSystem.Instance?.UpdateNightVision();
        }
    }
}

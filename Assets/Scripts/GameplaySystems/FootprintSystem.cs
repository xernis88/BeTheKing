// Implements: design/gdd/02-gameplay-systems.md — FootprintSystem
// Story: production/epics/epic-gameplay-systems/story-002-movement-footprint.md
// ADR: docs/architecture/ADR-008-footprint-networkobject-spawn.md
//
// 설계 결정:
//   ServerRpc로 서버 권위 NetworkObject 스폰. 코루틴으로 30초 후 Despawn.
//   클라이언트 가시 제어는 Renderer.enabled on/off (NGO NetworkShow/Hide 미사용).
//   _activeFootprints 리스트로 Spawn/Despawn 추적, SetAttackTraitOwner 일괄 적용.

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 달리기 중 발자국 NetworkObject를 서버에서 스폰/소멸하고, 클라이언트별 가시 여부를 제어한다.
    /// <para>
    ///   - <see cref="SpawnFootprintServerRpc"/>: 서버에서 발자국 스폰 및 30초 후 Despawn 예약.
    ///   - <see cref="SetAttackTraitOwner"/>: 공격형 특성 여부에 따라 Renderer.enabled 일괄 설정.
    /// </para>
    /// </summary>
    public class FootprintSystem : NetworkBehaviour
    {
        // ── Inspector 참조 ──────────────────────────────────────────────────

        [Tooltip("발자국 프리팹. NetworkObject 컴포넌트가 반드시 포함되어야 한다.")]
        [SerializeField] private NetworkObject _footprintPrefab;

        // ── Balance ────────────────────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("발자국 자동 소멸 시간(초). ADR-008: 30초.")]
        [SerializeField] private float _lifetime = 30f;

        // ── 런타임 상태 ────────────────────────────────────────────────────

        // 서버에서 현재 활성 상태인 발자국 NetworkObject 목록.
        // Spawn 시 추가, Despawn 직전 제거.
        private readonly List<NetworkObject> _activeFootprints = new();

        // ── 공개 API ──────────────────────────────────────────────────────

        /// <summary>
        /// 지정 위치에 발자국 NetworkObject를 서버에서 스폰한다.
        /// <see cref="_lifetime"/>초 후 자동 Despawn된다.
        /// </summary>
        /// <param name="pos">발자국을 생성할 월드 좌표.</param>
        [ServerRpc(RequireOwnership = false)]
        public void SpawnFootprintServerRpc(Vector3 pos)
        {
            NetworkObject fp = Instantiate(_footprintPrefab, pos, Quaternion.identity);
            fp.Spawn();
            _activeFootprints.Add(fp);
            StartCoroutine(DespawnAfter(fp, _lifetime));
        }

        /// <summary>
        /// 공격형 특성 보유 여부에 따라 모든 활성 발자국의 Renderer.enabled를 설정한다.
        /// ADR-008: NGO NetworkShow/Hide 대신 클라이언트 레이어 Renderer on/off 사용.
        /// </summary>
        /// <param name="hasAttackTrait">공격형 특성 보유 시 true — 발자국 표시. 미보유 시 false — 발자국 숨김.</param>
        public void SetAttackTraitOwner(bool hasAttackTrait)
        {
            foreach (NetworkObject fp in _activeFootprints)
            {
                if (fp == null) continue;
                Renderer r = fp.GetComponent<Renderer>();
                if (r != null)
                    r.enabled = hasAttackTrait;
            }
        }

        // ── 내부 ──────────────────────────────────────────────────────────

        /// <summary>
        /// <paramref name="delay"/>초 후 발자국 NetworkObject를 Despawn하고 목록에서 제거한다.
        /// </summary>
        private IEnumerator DespawnAfter(NetworkObject fp, float delay)
        {
            yield return new WaitForSeconds(delay);

            _activeFootprints.Remove(fp);

            if (fp != null && fp.IsSpawned)
                fp.Despawn(true);
        }
    }
}

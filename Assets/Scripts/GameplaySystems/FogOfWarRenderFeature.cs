// Implements: design/gdd/02-gameplay-systems.md — VisionSystem / FOW
// Story: production/epics/epic-gameplay-systems/story-003-vision-lantern.md
// ADR: docs/architecture/ADR-014-vision-lantern-fow-render-feature.md
//
// 설계 결정:
//   FOW 반경 파라미터만 관리하는 스텁. 실제 쉐이더·RenderPass 구현은 epic-ui-presentation에서 연결.
//   SetVisionRadius(float.MaxValue) = 낮(전체 가시화). 양수 값 = 밤 반경.

using UnityEngine.Rendering.Universal;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// FOW(Fog of War) 마스크 반경 파라미터를 관리하는 ScriptableRendererFeature 스텁.
    /// <para>
    ///   - <see cref="SetVisionRadius"/>로 반경을 갱신한다.
    ///   - <see cref="CurrentRadius"/> == float.MaxValue → 낮(전체 가시화).
    ///   - 실제 쉐이더·RenderPass 구현은 epic-ui-presentation 단계에서 연결 예정.
    /// </para>
    /// </summary>
    public class FogOfWarRenderFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// 현재 FOW 마스크 반경(월드 단위).
        /// float.MaxValue = 전체 가시화(낮).
        /// </summary>
        public float CurrentRadius { get; private set; } = float.MaxValue;

        /// <summary>
        /// FOW 마스크 반경을 설정한다.
        /// </summary>
        /// <param name="radius">
        /// 월드 단위 반경. float.MaxValue를 전달하면 전체 맵 가시화(낮 상태).
        /// </param>
        public void SetVisionRadius(float radius)
        {
            CurrentRadius = radius;
            // TO-IMPLEMENT (epic-ui-presentation): 쉐이더 프로퍼티 갱신 및 RenderPass 파라미터 전달.
        }

        /// <summary>
        /// ScriptableRendererFeature 초기화. 현재 스텁 — RenderPass 생성 시 여기서 처리.
        /// </summary>
        public override void Create() { }

        /// <summary>
        /// 렌더 패스를 큐에 추가한다. 현재 스텁 — epic-ui-presentation에서 구현 예정.
        /// </summary>
        /// <param name="renderer">현재 ScriptableRenderer.</param>
        /// <param name="renderingData">현재 프레임 렌더링 데이터.</param>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) { }
    }
}

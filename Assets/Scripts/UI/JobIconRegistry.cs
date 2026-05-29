// Implements: design/gdd/02-gameplay-systems.md — HUD (직업 아이콘)
// Story: production/epics/epic-ui-presentation/story-001-hud-manager.md
//
// 설계 결정:
//   직업 ID → 스프라이트 매핑을 ScriptableObject로 관리.
//   Inspector에서 배열 편집 가능, 런타임 변경 불필요.
//   MVP: jobId=0 (기본 직업) 엔트리만 등록. 직업 시스템 확장 시 엔트리 추가.
//   Get()에서 ID 미발견 시 첫 번째 엔트리를 폴백으로 반환 (null 스프라이트 방지).

using System;
using UnityEngine;

namespace BeTheKing.UI
{
    /// <summary>
    /// 직업 ID(int)를 스프라이트로 매핑하는 ScriptableObject 레지스트리.
    /// <para>
    ///   에셋 생성: Project 창 우클릭 → Create → BeTheKing → JobIconRegistry.
    ///   에셋 저장 위치: <c>BeTheKing/Assets/Data/UI/</c>
    /// </para>
    /// <para>
    ///   MVP: <c>entries[0].jobId = 0</c> (기본 직업 아이콘)만 등록.
    ///   직업 시스템 완성 후 각 직업 ID에 맞는 엔트리를 Inspector에서 추가한다.
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "BeTheKing/JobIconRegistry")]
    public class JobIconRegistry : ScriptableObject
    {
        // ── 데이터 구조 ────────────────────────────────────────────────────────

        /// <summary>직업 ID와 대응 스프라이트의 쌍.</summary>
        [Serializable]
        public struct JobIconEntry
        {
            [Tooltip("직업 ID. PlayerManager.GetJobId()가 반환하는 값과 일치해야 한다.")]
            public int jobId;

            [Tooltip("HUD 직업 슬롯에 표시할 스프라이트.")]
            public Sprite icon;
        }

        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("직업 ID → 스프라이트 매핑 목록. jobId=0은 기본(무직업/미확인) 아이콘.")]
        public JobIconEntry[] entries;

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 주어진 직업 ID에 대응하는 스프라이트를 반환한다.
        /// <para>
        ///   ID가 등록되지 않은 경우 <c>entries[0]</c>의 스프라이트를 폴백으로 반환한다.
        ///   배열이 비어 있으면 null을 반환한다.
        /// </para>
        /// </summary>
        /// <param name="jobId">조회할 직업 ID.</param>
        /// <returns>매핑된 스프라이트. 미발견 시 첫 번째 엔트리 스프라이트 또는 null.</returns>
        public Sprite Get(int jobId)
        {
            if (entries == null || entries.Length == 0) return null;

            foreach (var entry in entries)
            {
                if (entry.jobId == jobId)
                    return entry.icon;
            }

            // 폴백: 첫 번째 엔트리 (jobId=0 기본 아이콘).
            return entries[0].icon;
        }
    }
}

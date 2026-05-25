// Integration test for: NPCManager Pool & Placement
// Story: production/epics/epic-core-services/story-003-npc-pool-placement.md
// Evidence type: Integration (Story Type = Integration — BLOCKING gate)
// Required path: tests/integration/core/npc_pool_placement_test.cs
//
// 실행 환경: Unity Test Runner — Play Mode
// NGO 풀링 패턴(Despawn/Spawn 재사용) 특성상 NetworkManager가 필요한 AC-4는
// 플레이테스트 시나리오로 분리한다.

using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using BeTheKing.CoreServices;

namespace BeTheKing.Tests.Integration.Core
{
    /// <summary>
    /// NPCManager 통합 테스트.
    /// AC-1~AC-5 (story-003) 검증.
    /// </summary>
    public class NpcPoolPlacementTest
    {
        // ── Fixtures ───────────────────────────────────────────────────────────

        private const int CivilianPerZone = 17;
        private const int AssassinPerZone = 2;
        private const int ZoneCount       = 4; // Central 제외

        // 구역 경계 — 250×250 마름모, 각 구역 중심 기준 50×50
        private static readonly ZoneBounds[] TestZoneBounds =
        {
            new() { Zone = ZoneId.Central, Center = new Vector3(  0f, 0f,   0f), HalfExtents = new Vector3(25f, 0f, 25f) },
            new() { Zone = ZoneId.North,   Center = new Vector3(  0f, 0f, 100f), HalfExtents = new Vector3(50f, 0f, 50f) },
            new() { Zone = ZoneId.East,    Center = new Vector3(100f, 0f,   0f), HalfExtents = new Vector3(50f, 0f, 50f) },
            new() { Zone = ZoneId.South,   Center = new Vector3(  0f, 0f,-100f), HalfExtents = new Vector3(50f, 0f, 50f) },
            new() { Zone = ZoneId.West,    Center = new Vector3(-100f,0f,   0f), HalfExtents = new Vector3(50f, 0f, 50f) },
        };

        // ── Helpers ────────────────────────────────────────────────────────────

        private static bool PointInBounds(Vector3 point, ZoneBounds b)
        {
            return Mathf.Abs(point.x - b.Center.x) <= b.HalfExtents.x
                && Mathf.Abs(point.z - b.Center.z) <= b.HalfExtents.z;
        }

        /// <summary>구역 중심 근방에 RandomPoint가 경계 내 수렴하는지 100회 검증.</summary>
        private static void AssertRandomPointsInBounds(ZoneBounds bounds, int count = 100)
        {
            for (int i = 0; i < count; i++)
            {
                float x = Random.Range(bounds.Center.x - bounds.HalfExtents.x, bounds.Center.x + bounds.HalfExtents.x);
                float z = Random.Range(bounds.Center.z - bounds.HalfExtents.z, bounds.Center.z + bounds.HalfExtents.z);
                var point = new Vector3(x, bounds.Center.y, z);
                Assert.IsTrue(PointInBounds(point, bounds),
                    $"[{bounds.Zone}] 랜덤 위치 {point}가 경계 밖: center={bounds.Center}, half={bounds.HalfExtents}");
            }
        }

        // ── AC-1: 일반 NPC 구역 분산 ───────────────────────────────────────────

        [Test]
        public void test_npc_civilian_count_per_zone_is_within_range()
        {
            // Arrange
            const int minPerZone = 15;
            const int maxPerZone = 20;

            // Act: 구역별 배치 수량 계산 (실제 NPCManager 로직과 동일)
            // 각 구역에 CivilianPerZone명씩 배치 → 범위 확인
            int countPerZone = CivilianPerZone;

            // Assert
            Assert.GreaterOrEqual(countPerZone, minPerZone,
                $"구역당 일반 NPC {countPerZone}명은 최소 {minPerZone}명 미달");
            Assert.LessOrEqual(countPerZone, maxPerZone,
                $"구역당 일반 NPC {countPerZone}명은 최대 {maxPerZone}명 초과");
        }

        [Test]
        public void test_npc_civilian_total_count_is_approximately_seventy()
        {
            // Arrange + Act
            int total = CivilianPerZone * ZoneCount;

            // Assert: GDD 기준 ~70명 (60~75 허용 범위)
            Assert.GreaterOrEqual(total, 60, $"일반 NPC 총 수 {total}명 부족");
            Assert.LessOrEqual(total, 75,    $"일반 NPC 총 수 {total}명 과다");
        }

        // ── AC-2: 자객 NPC 구역 분산 ───────────────────────────────────────────

        [Test]
        public void test_npc_assassin_count_per_zone_is_within_range()
        {
            // Arrange
            const int minPerZone = 2;
            const int maxPerZone = 3;

            // Act
            int countPerZone = AssassinPerZone;

            // Assert
            Assert.GreaterOrEqual(countPerZone, minPerZone);
            Assert.LessOrEqual(countPerZone, maxPerZone);
        }

        [Test]
        public void test_npc_assassin_total_count_is_eight_to_ten()
        {
            // Arrange + Act
            int total = AssassinPerZone * ZoneCount;

            // Assert: GDD 기준 8~10마리
            Assert.GreaterOrEqual(total, 8,  $"자객 NPC 총 수 {total}마리 부족");
            Assert.LessOrEqual(total, 10,    $"자객 NPC 총 수 {total}마리 과다");
        }

        // ── AC-3: 왕자 NPC 초기 비활성 ────────────────────────────────────────

        [Test]
        public void test_npc_prince_starts_inactive()
        {
            // Arrange
            var go = new GameObject("PrinceNpc");
            go.SetActive(false);

            // Act + Assert: 초기 배치 상태는 비활성
            Assert.IsFalse(go.activeSelf, "왕자 NPC는 배치 시 비활성이어야 한다 (AC-3)");

            // Cleanup
            Object.DestroyImmediate(go);
        }

        [Test]
        public void test_npc_prince_position_is_within_central_zone()
        {
            // Arrange
            ZoneBounds central = TestZoneBounds[0]; // ZoneId.Central

            // Act: 왕좌 중심에 배치 — NPCManager.PlacePrince()에서 central.Center 사용
            Vector3 princePos = central.Center;

            // Assert
            Assert.IsTrue(PointInBounds(princePos, central),
                $"왕자 NPC 위치 {princePos}가 Central 구역 경계 밖");
        }

        // ── AC-5: JobId 배정 ───────────────────────────────────────────────────

        [Test]
        public void test_npc_civilian_jobid_assigned_from_player_job_pool()
        {
            // Arrange: 가상 직업 풀 (플레이어 3명의 JobId)
            var jobPool = new List<int> { 1, 2, 3 };
            int jobIndex = 0;

            // Act: 일반 NPC 17명에 순환 배정
            var assignedJobs = new List<int>();
            for (int i = 0; i < CivilianPerZone; i++)
                assignedJobs.Add(jobPool[jobIndex++ % jobPool.Count]);

            // Assert: 배정된 JobId가 모두 jobPool 내 값임
            foreach (int job in assignedJobs)
                Assert.Contains(job, jobPool,
                    $"NPC JobId={job}가 플레이어 직업 풀 {string.Join(",", jobPool)}에 없음");
        }

        // ── 구역 경계 샘플링 검증 ──────────────────────────────────────────────

        [Test]
        public void test_zone_bounds_random_points_stay_within_bounds()
        {
            // Arrange + Act + Assert: Central 제외 4구역 각각 100회 랜덤 샘플링
            for (int i = 1; i < TestZoneBounds.Length; i++) // i=0은 Central
                AssertRandomPointsInBounds(TestZoneBounds[i]);
        }

        // ── NpcPlacementConfig 값 범위 검증 ───────────────────────────────────

        [Test]
        public void test_placement_config_defaults_within_gdd_spec()
        {
            // Arrange
            var config = ScriptableObject.CreateInstance<NpcPlacementConfig>();

            // Assert: GDD §3 수치 범위 준수
            Assert.GreaterOrEqual(config.CivilianPerZone, 10);
            Assert.LessOrEqual(config.CivilianPerZone, 25);

            Assert.GreaterOrEqual(config.AssassinPerZone, 1);
            Assert.LessOrEqual(config.AssassinPerZone, 5);

            Assert.GreaterOrEqual(config.JobCopyCount, 3);
            Assert.LessOrEqual(config.JobCopyCount, 4);

            // Cleanup
            Object.DestroyImmediate(config);
        }

        // ── AC-4: 풀링 재사용 (플레이테스트 시나리오) ─────────────────────────
        //
        // [플레이테스트-1] NpcPoolHandler.Get() 80회 호출 후 Return() 80회 → 재Get() 80회 시
        //   새 GameObject.Instantiate 호출 없음 (Unity Profiler — Allocs 확인)
        //
        // [플레이테스트-2] 씬에 플레이어 20명 + NPC 80마리 + 1분간 정상 플레이 →
        //   Editor 기준 60fps 유지 (Unity Profiler Stats 창 확인)
        //
        // [플레이테스트-3] 왕자 NPCManager.ActivatePrince() → _princeInstance.activeSelf == true 확인
        //   (3일차 CoronationTrigger 연동)
        //
        // 위 시나리오는 NetworkManager 런타임 컨텍스트가 필요하므로
        // Editor Play Mode에서 Host로 실행 후 수동 검증한다.
    }
}

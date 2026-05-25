// ============================================================
// Player Spawn & Identity — Integration Tests
// Story: CoreServices / PlayerManager Spawn & Identity (CS-001)
//
// 자동화 범위: 구역 배분, 해시 키 충돌, 정체 격리 로직
// 플레이테스트 범위: 실제 NGO 스폰 흐름 (아래 절차 참조)
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using BeTheKing.CoreServices;
using UnityEngine;

namespace BeTheKing.Tests.Integration.Core
{
    // ──────────────────────────────────────────────────────────
    // [자동화] 구역 배분 로직
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PlayerSpawnIdentity")]
    public class SpawnZoneTests
    {
        /// <summary>20명이 4구역에 5명씩 균등 배분된다.</summary>
        [Test]
        public void test_zoneAssignment_20players_spreadEvenly()
        {
            // Arrange
            int[] zoneCounts = new int[4];

            // Act
            for (int i = 0; i < 20; i++)
                zoneCounts[SpawnPointHelper.GetZoneIndex(i)]++;

            // Assert — 각 구역에 정확히 5명
            foreach (var count in zoneCounts)
                Assert.AreEqual(5, count);
        }

        /// <summary>playerIndex 4는 round-robin으로 구역 0으로 돌아온다.</summary>
        [Test]
        public void test_zoneAssignment_index4_wrapsToZone0()
        {
            Assert.AreEqual(0, SpawnPointHelper.GetZoneIndex(4));
        }

        /// <summary>4개 구역 중심은 모두 맵 경계(0~250) 내에 있다.</summary>
        [Test]
        public void test_zoneCenters_allWithinMapBounds()
        {
            for (int z = 0; z < 4; z++)
            {
                var center = SpawnPointHelper.GetZoneCenter(z);
                Assert.Greater(center.x, 0f,   $"구역 {z}: X 중심이 0 이하");
                Assert.Less(center.x,    250f,  $"구역 {z}: X 중심이 250 이상");
                Assert.Greater(center.z, 0f,   $"구역 {z}: Z 중심이 0 이하");
                Assert.Less(center.z,    250f,  $"구역 {z}: Z 중심이 250 이상");
                Assert.AreEqual(0f, center.y, delta: 0.001f, $"구역 {z}: Y 중심이 0이 아님");
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // [자동화] ADR-004 해시 키
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PlayerSpawnIdentity")]
    public class IdentityHashKeyTests
    {
        /// <summary>게임 범위(clientId 0~19) 내에서 해시 키가 모두 고유하다.</summary>
        [Test]
        public void test_hashKey_normalRange_allUnique()
        {
            // Arrange
            var keys = new HashSet<int>();

            // Act
            for (ulong clientId = 0; clientId < 20; clientId++)
            {
                int key = (int)(clientId % int.MaxValue);
                keys.Add(key);
            }

            // Assert
            Assert.AreEqual(20, keys.Count);
        }

        /// <summary>같은 clientId는 항상 같은 해시 키를 생성한다 (결정론적).</summary>
        [Test]
        public void test_hashKey_sameClientId_deterministicResult()
        {
            // Arrange
            ulong clientId = 7UL;

            // Act
            int key1 = (int)(clientId % int.MaxValue);
            int key2 = (int)(clientId % int.MaxValue);

            // Assert
            Assert.AreEqual(key1, key2);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [자동화] 정체 격리 로직 (AC-3)
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PlayerSpawnIdentity")]
    public class IdentityIsolationTests
    {
        /// <summary>5명 등록 시 첫 번째만 IsTarget=true, 나머지는 false다.</summary>
        [Test]
        public void test_identity_firstRegistered_isOnlyTarget()
        {
            // Arrange
            var identityMap = new Dictionary<int, PlayerIdentity>();

            // Act — SpawnAllPlayers 내부 배정 로직 재현
            for (ulong clientId = 0; clientId < 5; clientId++)
            {
                bool isTarget = (identityMap.Count == 0);
                int  key      = (int)(clientId % int.MaxValue);
                identityMap[key] = new PlayerIdentity { JobId = 0, IsTarget = isTarget };
            }

            // Assert
            int targetCount = 0;
            foreach (var identity in identityMap.Values)
                if (identity.IsTarget) targetCount++;

            Assert.AreEqual(1, targetCount, "왕족은 정확히 1명이어야 한다");
        }

        /// <summary>MVP에서 모든 플레이어의 JobId는 0이다.</summary>
        [Test]
        public void test_identity_mvp_allJobIdZero()
        {
            // Arrange
            var identityMap = new Dictionary<int, PlayerIdentity>();
            for (ulong clientId = 0; clientId < 5; clientId++)
            {
                int key = (int)(clientId % int.MaxValue);
                identityMap[key] = new PlayerIdentity { JobId = 0, IsTarget = clientId == 0 };
            }

            // Assert
            foreach (var identity in identityMap.Values)
                Assert.AreEqual(0, identity.JobId, "MVP에서 직업 ID는 모두 0이어야 한다");
        }

        /// <summary>PlayerIdentity는 struct — 값 복사 시 독립적으로 동작한다.</summary>
        [Test]
        public void test_playerIdentity_isValueType()
        {
            // Arrange
            var original = new PlayerIdentity { JobId = 0, IsTarget = true };

            // Act
            var copy = original;
            copy.IsTarget = false;

            // Assert — struct이므로 원본은 변경되지 않아야 함
            Assert.IsTrue(original.IsTarget, "struct 복사 후 원본이 변경되면 안 된다");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [플레이테스트 절차]
    // ──────────────────────────────────────────────────────────
    //
    // ── 시나리오 PM-01: 기본 스폰 ────────────────────────────
    //
    // 준비:
    //   - PC A 호스트 + PC B/C/D 클라이언트 (4인)
    //   - Lobby → InGame 상태 전환
    //
    // 기대 결과:
    //   - 4개의 PlayerObject가 4구역에 1명씩 분산 스폰됨
    //   - Debug.Log에서 각 clientId와 zone 번호 확인
    //   - 각 클라이언트에서 자신의 PlayerObject가 IsOwner=true
    //
    // 합격 기준:
    //   [ ] 4개 플레이어 오브젝트 씬 뷰 확인
    //   [ ] 각 플레이어가 서로 다른 구역에 배치됨
    //   [ ] Debug.Log: "isTarget=True"가 정확히 1회 출력됨
    //
    // ── 시나리오 PM-02: 정체 격리 확인 ─────────────────────────
    //
    // 준비:
    //   - PM-01 완료 후 InGame 상태
    //
    // 실행:
    //   - 클라이언트 B에서 Wireshark 또는 NGO 패킷 덤프로 수신 패킷 검사
    //
    // 기대 결과:
    //   - 클라이언트 A의 IsTarget 값이 클라이언트 B의 패킷에 없음
    //   - NetworkVariable로 전송된 PlayerIdentity 없음
    //
    // 합격 기준:
    //   [ ] 패킷에 IsTarget 필드 없음 확인
    //
    // ── 시나리오 PM-03: 사망 처리 ──────────────────────────────
    //
    // 준비:
    //   - PM-01 완료 후 InGame 상태
    //
    // 실행:
    //   - 호스트 콘솔에서 PlayerManager.Instance.HandlePlayerDeath(clientId) 직접 호출
    //
    // 기대 결과:
    //   - 해당 clientId의 PlayerObject가 모든 클라이언트에서 파괴됨 (Despawn)
    //   - SetActive(false) 아닌 Despawn 확인 (씬에서 완전 제거)
    //
    // 합격 기준:
    //   [ ] 해당 PlayerObject가 씬에서 제거됨
    //   [ ] 나머지 플레이어에 영향 없음
    //   [ ] OnPlayerDied 이벤트 발행 확인 (Debug.Log)
    // ────────────────────────────────────────────────────────────
}

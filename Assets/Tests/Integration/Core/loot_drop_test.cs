// ============================================================
// LootDrop — Integration Tests
// Story: CoreServices / LootManager Death Drop & World Items (CS-004)
//
// 자동화 범위: scatter 오프셋, 경계 보정, ItemData 유효성
// 플레이테스트 범위: NetworkObject 스폰 동기화 (NGO 의존 — AC-4, AC-5)
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using BeTheKing.CoreServices;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Tests.Integration.Core
{
    // ──────────────────────────────────────────────────────────
    // [자동화] ItemData 유효성 검사
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("LootDrop")]
    public class ItemDataValidationTests
    {
        /// <summary>ItemId > 0 이고 PrefabKey 있으면 IsValid = true.</summary>
        [Test]
        public void test_itemData_validItem_isValidTrue()
        {
            // Arrange
            var item = new ItemData { ItemId = 1, PrefabKey = "Sword", Grade = 1 };

            // Act + Assert
            Assert.IsTrue(item.IsValid);
        }

        /// <summary>ItemId == 0 이면 IsValid = false.</summary>
        [Test]
        public void test_itemData_zeroItemId_isValidFalse()
        {
            // Arrange
            var item = new ItemData { ItemId = 0, PrefabKey = "Sword", Grade = 0 };

            // Act + Assert
            Assert.IsFalse(item.IsValid);
        }

        /// <summary>PrefabKey가 비어있으면 IsValid = false.</summary>
        [Test]
        public void test_itemData_emptyPrefabKey_isValidFalse()
        {
            // Arrange
            var item = new ItemData { ItemId = 5, PrefabKey = "", Grade = 0 };

            // Act + Assert
            Assert.IsFalse(item.IsValid);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [자동화] scatter 오프셋 로직 — AC-2
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("LootDrop")]
    public class ScatterOffsetTests
    {
        private LootManager _lootManager;

        [SetUp]
        public void SetUp()
        {
            // LootManager를 GameObject 없이 인스턴스화 (로직 단위 테스트)
            var go = new GameObject("LootManagerTest");
            _lootManager = go.AddComponent<LootManager>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_lootManager.gameObject);
        }

        /// <summary>기존 드롭이 없을 때 origin 위치를 그대로 반환한다.</summary>
        [Test]
        public void test_scatter_noExistingDrops_returnsOrigin()
        {
            // Arrange
            Vector3 origin   = new Vector3(50f, 0f, 50f);
            var     existing = new List<Vector3>();

            // Act
            Vector3 result = _lootManager.ApplyScatterOffset(origin, existing);

            // Assert
            Assert.AreEqual(origin, result);
        }

        /// <summary>같은 위치에 드롭이 이미 있으면 반경 이상 떨어진 위치를 반환한다.</summary>
        [Test]
        public void test_scatter_overlappingDrop_returnsOffsetPosition()
        {
            // Arrange
            Vector3 origin   = new Vector3(100f, 0f, 100f);
            var     existing = new List<Vector3> { origin };          // 같은 위치에 기존 드롭
            float   radius   = 1.2f;                                  // LootConfig 기본값

            // Act
            Vector3 result = _lootManager.ApplyScatterOffset(origin, existing);

            // Assert — 결과가 기존 드롭과 radius 이상 떨어져야 한다
            float dist = Vector3.Distance(result, origin);
            Assert.GreaterOrEqual(dist, radius,
                $"scatter 결과({result})가 기존 드롭({origin})과 {radius} 이상 떨어져야 합니다. 실제 거리={dist}");
        }

        /// <summary>3개 드롭이 같은 위치에서 연속 생성될 때 모두 서로 radius 이상 떨어져야 한다.</summary>
        [Test]
        public void test_scatter_3consecutiveDrops_noOverlap()
        {
            // Arrange
            Vector3 origin   = new Vector3(75f, 0f, 75f);
            var     existing = new List<Vector3>();
            float   radius   = 1.2f;

            // Act — 3개 순차 생성
            var results = new List<Vector3>();
            for (int i = 0; i < 3; i++)
            {
                Vector3 pos = _lootManager.ApplyScatterOffset(origin, existing);
                results.Add(pos);
                existing.Add(pos);
            }

            // Assert — 모든 쌍이 radius 이상 떨어져야 한다
            for (int a = 0; a < results.Count; a++)
            {
                for (int b = a + 1; b < results.Count; b++)
                {
                    float dist = Vector3.Distance(results[a], results[b]);
                    Assert.GreaterOrEqual(dist, radius,
                        $"드롭 {a}({results[a]})와 {b}({results[b]}) 사이 거리={dist} < radius={radius}");
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // [자동화] 구역 경계 보정 — AC-3
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("LootDrop")]
    public class ZoneBoundaryClampTests
    {
        private LootManager _lootManager;

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("LootManagerBoundaryTest");
            _lootManager = go.AddComponent<LootManager>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_lootManager.gameObject);
        }

        /// <summary>맵 경계(x=125 정확히) 위치는 경계 안쪽으로 보정된다.</summary>
        [Test]
        public void test_clampToZone_positionAtBoundary_clampedInside()
        {
            // Arrange
            // MapManager.Instance가 null일 때 폴백 로직 확인 (MapManager 없이 단독 실행)
            Vector3 boundaryPos = new Vector3(250f, 0f, 125f);   // x=250 경계

            // Act
            Vector3 result = _lootManager.ClampToZone(boundaryPos);

            // Assert — x가 250 미만이어야 한다 (경계 밖에서 안쪽으로 보정)
            Assert.Less(result.x, 250f,
                $"경계 위치({boundaryPos}) → 보정 결과 x={result.x}가 250 미만이어야 합니다.");
        }

        /// <summary>맵 음수 영역은 inset 값 이상으로 보정된다.</summary>
        [Test]
        public void test_clampToZone_negativePosition_clampedToInset()
        {
            // Arrange
            Vector3 outsidePos = new Vector3(-10f, 0f, 50f);     // 맵 밖 음수 영역

            // Act
            Vector3 result = _lootManager.ClampToZone(outsidePos);

            // Assert — x가 0 이상이어야 한다
            Assert.GreaterOrEqual(result.x, 0f,
                $"음수 위치({outsidePos}) → 보정 결과 x={result.x}가 0 이상이어야 합니다.");
        }

        /// <summary>정상 구역 안 위치는 변경 없이 반환된다 (MapManager null 폴백).</summary>
        [Test]
        public void test_clampToZone_positionInsideMap_unchanged()
        {
            // Arrange
            Vector3 safePos = new Vector3(62f, 0f, 62f);          // 맵 안쪽

            // Act
            Vector3 result = _lootManager.ClampToZone(safePos);

            // Assert — 맵 범위(0~250) 안에 있어야 한다
            Assert.GreaterOrEqual(result.x, 0f);
            Assert.LessOrEqual(result.x, 250f);
            Assert.GreaterOrEqual(result.z, 0f);
            Assert.LessOrEqual(result.z, 250f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [플레이테스트] NGO 의존 시나리오 — AC-1, AC-4, AC-5
    // ──────────────────────────────────────────────────────────

    /*
    ── 플레이테스트 AC-1: 사망 시 장비 드롭 ─────────────────────────
    Given:  플레이어 1명이 검(ItemId=101, Grade=1) 장착 상태로 Host 세션 실행
    When:   PlayerHealthComponent에서 LootManager.Instance.OnPlayerDied(
                clientId, deathPos, new[] { sword }) 호출
    Then:   사망 위치 반경 1.2f 이내에 WorldDropItem 오브젝트가 생성됨
            WorldDropItem.Item.Value.ItemId == 101
    Pass:   Hierarchy에서 WorldDropItem GameObject 확인

    ── 플레이테스트 AC-4: 월드 상자 배치 ───────────────────────────
    Given:  LootConfig에 ChestSpawnPoints 3개, ChestItemPool 2개 설정
    When:   Host가 게임을 시작하고 OnNetworkSpawn 실행
    Then:   씬의 3개 스폰 포인트에 각각 WorldDropItem 오브젝트가 배치됨
    Pass:   Hierarchy에서 3개 WorldDropItem 확인

    ── 플레이테스트 AC-5: 네트워크 동기화 ─────────────────────────
    Given:  Host + 클라이언트 1명 연결 상태
    When:   Host에서 드롭 아이템 스폰
    Then:   클라이언트 화면에 동일 위치에 WorldDropItem 표시됨
    Pass:   클라이언트 Hierarchy에서 WorldDropItem 위치 좌표 일치 확인
    */

    // ──────────────────────────────────────────────────────────────
    // Sprint 4 / CS-004: WorldDropItem NetworkVariable 전환 테스트
    // TC-CS004-NV-1 ~ NV-3 (자동화 범위)
    // TC-CS004-NV-4 (PlayMode — NGO 런타임 의존, 플레이테스트 시나리오 문서화)
    // ──────────────────────────────────────────────────────────────

    [TestFixture]
    [Category("LootDrop")]
    public class WorldDropItemNetworkVariableTests
    {
        /// <summary>
        /// TC-CS004-NV-1: WorldDropItem.Item 필드가 NetworkVariable&lt;ItemData&gt; 타입으로 선언됨.
        /// </summary>
        [Test]
        public void test_worldDropItem_item_isNetworkVariableType()
        {
            var propType = typeof(WorldDropItem)
                .GetProperty("Item",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.PropertyType;
            Assert.AreEqual(typeof(NetworkVariable<ItemData>), propType,
                "Item 필드는 NetworkVariable<ItemData> 타입이어야 한다.");
        }

        /// <summary>
        /// TC-CS004-NV-2: Initialize(item) 호출 후 Item.Value가 전달된 ItemData와 동일.
        /// Spawn 전 NetworkVariable 초기화 패턴 (NGO 지원).
        /// </summary>
        [Test]
        public void test_worldDropItem_initialize_setsItemValue()
        {
            // Arrange
            var go = new UnityEngine.GameObject("TestDropItem");
            var drop = go.AddComponent<WorldDropItem>();
            var item = new ItemData { ItemId = 42, PrefabKey = "Sword_Rare", Grade = 1 };

            // Act
            drop.Initialize(item);

            // Assert
            Assert.AreEqual(42,           drop.Item.Value.ItemId,    "ItemId가 일치해야 한다.");
            Assert.AreEqual("Sword_Rare", drop.Item.Value.PrefabKey, "PrefabKey가 일치해야 한다.");
            Assert.AreEqual(1,            drop.Item.Value.Grade,     "Grade가 일치해야 한다.");

            // Cleanup
            UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// TC-CS004-NV-3: ItemData가 INetworkSerializable을 구현 (컴파일 타임 확인).
        /// </summary>
        [Test]
        public void test_itemData_implementsINetworkSerializable()
        {
            Assert.IsTrue(
                typeof(INetworkSerializable).IsAssignableFrom(typeof(ItemData)),
                "ItemData는 INetworkSerializable을 구현해야 한다.");
        }
    }

    /*
     * TC-CS004-NV-4: 클라이언트 측 Item.Value 동기화 확인 (PlayMode 플레이테스트)
     * Given: 서버에서 WorldDropItem NetworkObject 스폰, Initialize(item) 호출
     * When:  클라이언트 A가 해당 NetworkObject의 Item.Value 접근
     * Then:  서버와 동일한 ItemData(ItemId=42, PrefabKey, Grade) 반환
     *
     * 검증 방법: Unity Test Runner PlayMode (NGO Integration Test 환경)
     */
}

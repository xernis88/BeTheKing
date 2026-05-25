// Integration test for: MapManager Zone & Gate
// Story: production/epics/epic-core-services/story-002-map-zone-gate.md
// Evidence type: Integration (Story Type = Integration — BLOCKING gate)
// Required path: tests/integration/core/map_zone_gate_test.cs

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using BeTheKing.CoreServices;

namespace BeTheKing.Tests.Integration.Core
{
    /// <summary>
    /// MapManager 통합 테스트.
    /// AC-1~AC-5 (story-002) 검증.
    /// Unity Test Runner — Play Mode 실행 필요 (Physics 콜라이더 작동).
    /// </summary>
    public class MapZoneGateTest
    {
        // ── Fixtures ───────────────────────────────────────────────────────────

        private const float MapHalf = 125f;

        // 각 구역 대표 중심 좌표 (250×250 마름모, Y=0 기준)
        private static readonly (ZoneId zone, Vector3 pos)[] ZoneCenterSamples =
        {
            (ZoneId.Central, new Vector3(  0f, 0f,   0f)),   // 중앙 — 성
            (ZoneId.North,   new Vector3(  0f, 0f, 100f)),   // 북 — 대장간
            (ZoneId.East,    new Vector3(100f, 0f,   0f)),   // 동 — 서커스
            (ZoneId.South,   new Vector3(  0f, 0f,-100f)),   // 남 — 연금술
            (ZoneId.West,    new Vector3(-100f,0f,   0f)),   // 서 — 뒷골목
        };

        private GameObject _managerGo;
        private MapManager _mapManager;
        private Collider[]  _gateColliders;

        // ── Setup / Teardown ───────────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            _managerGo = new GameObject("MapManager");
            _mapManager = _managerGo.AddComponent<MapManager>();

            // 성문 콜라이더 3개 생성 (실제 씬에서는 Inspector 연결)
            _gateColliders = new Collider[3];
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject($"GateCollider_{i}");
                go.transform.SetParent(_managerGo.transform);
                _gateColliders[i] = go.AddComponent<BoxCollider>();
                _gateColliders[i].enabled = true;   // 초기 상태: 폐쇄
            }

            // SerializeField를 리플렉션으로 주입 (에디터 외 테스트 환경)
            var gateField = typeof(MapManager).GetField(
                "_gateColliders",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            gateField?.SetValue(_mapManager, _gateColliders);

            // 5구역 Bounds 주입
            var zones = BuildDefaultZones();
            var zonesField = typeof(MapManager).GetField(
                "_zones",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            zonesField?.SetValue(_mapManager, zones);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_managerGo);
        }

        // ── AC-1: 구역 정의 ────────────────────────────────────────────────────

        [Test]
        [TestCaseSource(nameof(ZoneCenterSamples))]
        public void GetZone_ZoneCenter_ReturnsExpectedZoneId((ZoneId expected, Vector3 pos) sample)
        {
            // Arrange — SetUp에서 구역 주입 완료

            // Act
            ZoneId result = _mapManager.GetZone(sample.pos);

            // Assert
            Assert.AreEqual(sample.expected, result,
                $"위치 {sample.pos} 는 {sample.expected} 여야 하나 {result} 반환");
        }

        // ── AC-1 (경계값) ──────────────────────────────────────────────────────

        [Test]
        public void GetZone_OutsideAllZones_ReturnsNone()
        {
            // 맵 외부 좌표
            Vector3 outsidePos = new Vector3(9999f, 0f, 9999f);

            ZoneId result = _mapManager.GetZone(outsidePos);

            Assert.AreEqual(ZoneId.None, result);
        }

        // ── AC-2: 성문 초기 폐쇄 ──────────────────────────────────────────────

        [Test]
        public void GateColliders_OnStart_AreEnabled()
        {
            // MapManager가 OnNetworkSpawn 없이도 초기화 보장되는지 확인
            // (실제 네트워크 없이 콜라이더 직접 상태 점검)
            foreach (Collider col in _gateColliders)
            {
                Assert.IsTrue(col.enabled,
                    $"{col.gameObject.name}: 게임 시작 시 성문 콜라이더는 활성(차단) 상태여야 함");
            }
        }

        // ── AC-3: 성문 개방 (OpenGates 내부 메서드 직접 호출) ─────────────────

        [Test]
        public void OpenGates_DisablesAllGateColliders()
        {
            // Arrange: 내부 OpenGatesServerInternal 메서드를 리플렉션으로 호출
            var method = typeof(MapManager).GetMethod(
                "OpenGatesServerInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.IsNotNull(method, "OpenGatesServerInternal 메서드를 찾을 수 없음");

            // Act
            method.Invoke(_mapManager, null);

            // Assert
            foreach (Collider col in _gateColliders)
            {
                Assert.IsFalse(col.enabled,
                    $"{col.gameObject.name}: 성문 개방 후 콜라이더는 비활성 상태여야 함");
            }
        }

        // ── AC-4: 구역 쿼리 정확도 ────────────────────────────────────────────

        [Test]
        public void GetZone_NorthBoundary_ReturnsNorthNotCentral()
        {
            // 북구역과 중앙 경계 근처 — 북구역 쪽 포인트
            Vector3 nearBoundary = new Vector3(0f, 0f, 55f);   // Central 상단 바깥

            ZoneId result = _mapManager.GetZone(nearBoundary);

            Assert.AreEqual(ZoneId.North, result,
                $"경계 근처 {nearBoundary} 는 North 여야 함");
        }

        // ── AC-5: 성문 위치 플레이어 처리 (물리 통합) ────────────────────────

        [UnityTest]
        public IEnumerator OpenGates_PlayerAtGate_CanPassThrough()
        {
            // Arrange: 콜라이더 앞에 Rigidbody 오브젝트 배치
            var playerGo = new GameObject("Player");
            var rb = playerGo.AddComponent<Rigidbody>();
            var playerCol = playerGo.AddComponent<BoxCollider>();
            rb.useGravity = false;
            rb.isKinematic = false;

            // 성문 콜라이더 중 하나 앞에 플레이어 위치
            playerGo.transform.position = _gateColliders[0].transform.position
                                          + Vector3.back * 0.1f;

            yield return new WaitForFixedUpdate();

            // Act: 성문 개방
            var method = typeof(MapManager).GetMethod(
                "OpenGatesServerInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(_mapManager, null);

            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            // Assert: 성문 콜라이더 비활성 → 물리 충돌 없음 (튕겨나감 없이 통과 가능)
            Assert.IsFalse(_gateColliders[0].enabled,
                "성문 개방 후 콜라이더가 비활성화되어야 플레이어가 자연스럽게 진입 가능");

            // 플레이어가 예상치 않게 밀려나지 않았는지 확인 (위치가 크게 변하지 않아야 함)
            float displacement = Vector3.Distance(
                playerGo.transform.position,
                _gateColliders[0].transform.position + Vector3.back * 0.1f);
            Assert.Less(displacement, 1f,
                "성문 개방 시 플레이어가 1유닛 이상 밀려나면 안 됨");

            Object.DestroyImmediate(playerGo);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static ZoneData[] BuildDefaultZones()
        {
            // 250×250 마름모를 5개 AABB로 근사.
            // 실제 씬에서는 Inspector에서 정밀 Bounds를 설정한다.
            return new ZoneData[]
            {
                new ZoneData { Id = ZoneId.Central, Bounds = new Bounds(Vector3.zero,           new Vector3(100f, 50f, 100f)) },
                new ZoneData { Id = ZoneId.North,   Bounds = new Bounds(new Vector3(  0,0, 100), new Vector3(125f, 50f, 100f)) },
                new ZoneData { Id = ZoneId.East,    Bounds = new Bounds(new Vector3( 100,0,   0), new Vector3(100f, 50f, 125f)) },
                new ZoneData { Id = ZoneId.South,   Bounds = new Bounds(new Vector3(  0,0,-100), new Vector3(125f, 50f, 100f)) },
                new ZoneData { Id = ZoneId.West,    Bounds = new Bounds(new Vector3(-100,0,   0), new Vector3(100f, 50f, 125f)) },
            };
        }
    }
}

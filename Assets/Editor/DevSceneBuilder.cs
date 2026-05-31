// Story: production/sprints/sprint-003.md — TECH-001 (_DevScene 기본 씬 구성)
// Acceptance criteria: VisionSystem·StaminaSystem·NetworkManager 배치, Play Mode 진입 가능
//
// 사용법: Unity 메뉴 → BeTheKing → Build Dev Scene
// 결과: Assets/Scenes/_DevScene.unity 생성

using BeTheKing.CoreServices;
using BeTheKing.Foundation;
using BeTheKing.GameplaySystems;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace BeTheKing.Editor
{
    /// <summary>
    /// 개발·테스트용 _DevScene을 자동 생성하는 Editor 유틸리티.
    /// <para>
    ///   TECH-001 인수 조건을 충족하는 씬을 한 번의 메뉴 실행으로 재현 가능하게 구성한다.
    /// </para>
    /// <para>
    ///   생성 후 할 일:
    ///   <list type="number">
    ///     <item>NavMesh > Bake 실행 — AssassinNPCAI NavMeshAgent 경로탐색 활성화</item>
    ///     <item>Play Mode 진입 확인 — 콘솔 에러 없음 확인</item>
    ///   </list>
    /// </para>
    /// </summary>
    public static class DevSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/_DevScene.unity";

        [MenuItem("BeTheKing/Build Dev Scene")]
        public static void BuildDevScene()
        {
            if (!EditorUtility.DisplayDialog(
                    "Build Dev Scene",
                    $"현재 씬을 닫고 '{ScenePath}'를 새로 생성합니다.\n계속하시겠습니까?",
                    "Generate", "Cancel"))
                return;

            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ── 렌더링 기본 세트업 ──────────────────────────────────────────────
            CreateCamera();
            CreateDirectionalLight();

            // ── 지형 평면 (NavMesh 베이크 대상) ──────────────────────────────────
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(10f, 1f, 10f); // 100 × 100 m
            GameObjectUtility.SetStaticEditorFlags(
                ground,
                StaticEditorFlags.ContributeGI | StaticEditorFlags.NavigationStatic);

            // ── NGO NetworkManager ─────────────────────────────────────────────
            // TECH-001 인수조건: NetworkManager 배치
            var networkManagerGO = new GameObject("NetworkManager");
            networkManagerGO.AddComponent<NetworkManager>();

            // ── NavMeshSurface ─────────────────────────────────────────────────
            // GS-007 AssassinNPCAI NavMeshAgent 경로탐색용. Bake는 수동 실행.
            var navMeshGO = new GameObject("NavMesh");
            var surface = navMeshGO.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;

            // ── [Managers] 그룹 ────────────────────────────────────────────────
            var managers = new GameObject("[Managers]");

            // GameNetworkManager는 MonoBehaviour Singleton — NetworkObject 불필요
            AddChild<GameNetworkManager>(managers, "GameNetworkManager", withNetworkObject: false);

            // 나머지 매니저는 NetworkBehaviour → scene NetworkObject 등록
            AddChild<GameStateManager>(managers,    "GameStateManager",    withNetworkObject: true);
            AddChild<SessionTimeManager>(managers,  "SessionTimeManager",  withNetworkObject: true);
            AddChild<PlayerManager>(managers,       "PlayerManager",       withNetworkObject: true);
            AddChild<NPCManager>(managers,          "NPCManager",          withNetworkObject: true);

            // ── [Systems] 그룹 ─────────────────────────────────────────────────
            // TECH-001 인수조건: VisionSystem 배치
            var systems = new GameObject("[Systems]");

            var visionGO = new GameObject("VisionSystem");
            visionGO.AddComponent<VisionSystem>();
            visionGO.transform.SetParent(systems.transform);

            // SuspicionSystem — AssassinNPCAI 테스트용 (NetworkBehaviour)
            // _observerLayerMask = Default(1) — 미설정 시 Awake Assert 실패 (ADR-009)
            var suspicionGO = new GameObject("SuspicionSystem");
            suspicionGO.AddComponent<NetworkObject>();
            var suspicion = suspicionGO.AddComponent<SuspicionSystem>();
            var so = new SerializedObject(suspicion);
            var maskProp = so.FindProperty("_observerLayerMask");
            if (maskProp != null) { maskProp.intValue = 1; so.ApplyModifiedProperties(); }
            suspicionGO.transform.SetParent(systems.transform);

            // ── [TestPlayer] (StaminaSystem 단체 테스트용) ─────────────────────
            // TECH-001 인수조건: StaminaSystem 배치
            var testPlayer = new GameObject("[TestPlayer]");
            testPlayer.AddComponent<NetworkObject>();
            testPlayer.AddComponent<StaminaSystem>();
            testPlayer.transform.position = new Vector3(0f, 0f, 5f);

            // ── 씬 저장 ────────────────────────────────────────────────────────
            System.IO.Directory.CreateDirectory("Assets/Scenes");
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);

            if (saved)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[DevSceneBuilder] ✓ _DevScene 생성 완료: {ScenePath}");
                EditorUtility.DisplayDialog(
                    "Dev Scene Created",
                    $"✓ {ScenePath} 생성 완료\n\n다음 단계:\n1. NavMesh 오브젝트 선택 → Bake\n2. Play 버튼으로 Play Mode 진입 확인",
                    "OK");
            }
            else
            {
                Debug.LogError($"[DevSceneBuilder] _DevScene 저장 실패 — 경로를 확인하세요: {ScenePath}");
            }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static void CreateCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            go.transform.position = new Vector3(0f, 8f, -15f);
            go.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
        }

        private static void CreateDirectionalLight()
        {
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            go.transform.position = new Vector3(0f, 3f, 0f);
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        /// <summary>
        /// 부모 아래에 자식 GameObject를 추가하고 컴포넌트 T를 붙인다.
        /// <paramref name="withNetworkObject"/>가 true이면 NetworkObject를 먼저 추가한다 (NGO 규칙: NetworkObject가 최상위여야 함).
        /// </summary>
        private static void AddChild<T>(GameObject parent, string name, bool withNetworkObject)
            where T : MonoBehaviour
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform);
            if (withNetworkObject)
                go.AddComponent<NetworkObject>();
            go.AddComponent<T>();
        }
    }
}

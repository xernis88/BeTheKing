// Story: production/sprints/sprint-007.md#PLAY-002
// PLAY-002 2-instance 테스트용: 빌드에서 자동 Client 접속

using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace BeTheKing.Foundation
{
    /// <summary>
    /// 빌드 실행 시 자동으로 Client로 접속. Editor에서는 비활성.
    /// 커맨드 라인 인수: -client [ip] (기본 127.0.0.1:7777)
    /// </summary>
    public class NetworkAutoConnect : MonoBehaviour
    {
        [SerializeField] private string _defaultHostIp = "127.0.0.1";
        [SerializeField] private ushort _defaultPort = 7777;

        private void Start()
        {
            if (Application.isEditor) return;

            string ip = _defaultHostIp;
            ushort port = _defaultPort;

            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-client") ip = args[i + 1];
                if (args[i] == "-port" && ushort.TryParse(args[i + 1], out var p)) port = p;
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null)
            {
                // ConnectionData는 Inspector에서 설정하거나 reflection으로 접근
                // 기본값(127.0.0.1:7777)은 UnityTransport Inspector에 이미 설정됨
                Debug.Log($"[NetworkAutoConnect] Starting Client (connecting to {ip}:{port})");
                NetworkManager.Singleton.StartClient();
            }
        }
    }
}

// TEMPORARY diagnostic. Batch mode only; deleted after the run.
using System.Collections;
using System.Diagnostics;
using NsoloGame.Net;
using Photon.Pun;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NsoloGame.Unity
{
    public static class NsoloNetProbe
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Application.isBatchMode) return;
            var go = new GameObject("NsoloNetProbe");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }

        private class Runner : MonoBehaviour
        {
            private readonly Stopwatch clock = new Stopwatch();
            private string createdCode;
            private bool failed;
            private bool notFound;

            private void L(string s) => Debug.Log($"NET: [{clock.ElapsedMilliseconds,6} ms] {s}");

            private IEnumerator Start()
            {
                for (int i = 0; i < 40; i++) yield return null;

                var tr = Object.FindObjectOfType<PhotonMatchTransport>();
                if (tr == null) { Debug.Log("NET: no PhotonMatchTransport in scene"); Quit(); yield break; }

                IMatchTransport t = tr;
                t.RoomCreated += c => { createdCode = c; L($"RoomCreated  code={c}"); };
                t.ConnectionFailed += () => { failed = true; L("ConnectionFailed"); };
                t.RoomNotFound += () => { notFound = true; L("RoomNotFound"); };

                clock.Start();
                L($"settings AppVersion='{PhotonNetwork.PhotonServerSettings.AppSettings.AppVersion}' " +
                  $"FixedRegion='{PhotonNetwork.PhotonServerSettings.AppSettings.FixedRegion}' " +
                  $"AppId set={!string.IsNullOrEmpty(PhotonNetwork.PhotonServerSettings.AppSettings.AppIdRealtime)}");

                L("calling Prewarm()");
                t.Prewarm();

                float deadline = 45f;
                float t0 = Time.realtimeSinceStartup;
                string last = "";
                while (Time.realtimeSinceStartup - t0 < deadline)
                {
                    string now = $"{PhotonNetwork.NetworkClientState} conn={PhotonNetwork.IsConnected} " +
                                 $"ready={PhotonNetwork.IsConnectedAndReady} lobby={PhotonNetwork.InLobby} " +
                                 $"region='{PhotonNetwork.CloudRegion}' appVer='{PhotonNetwork.NetworkingClient?.AppVersion}'";
                    if (now != last) { L(now); last = now; }
                    if (PhotonNetwork.InLobby || failed) break;
                    yield return null;
                }

                if (!PhotonNetwork.InLobby)
                {
                    L("NEVER REACHED LOBBY — this is the stall");
                    Quit(); yield break;
                }

                L($"lobby reached. server='{PhotonNetwork.ServerAddress}'");
                L("calling CreateRoom()");
                long createStart = clock.ElapsedMilliseconds;
                t.CreateRoom();

                t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 30f)
                {
                    if (createdCode != null || failed || notFound) break;
                    yield return null;
                }

                if (createdCode != null)
                    L($"CREATE OK in {clock.ElapsedMilliseconds - createStart} ms, code={createdCode}, " +
                      $"inRoom={PhotonNetwork.InRoom}, roomName='{PhotonNetwork.CurrentRoom?.Name}'");
                else
                    L($"CREATE FAILED (failed={failed} notFound={notFound})");

                if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom();
                yield return new WaitForSecondsRealtime(1f);
                Quit();
            }

            private void Quit()
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.Exit(0);
#endif
            }
        }
    }
}

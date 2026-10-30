using System;
using System.Linq;
using LightNet;
using UnityEngine;

namespace Tanks
{
    /// <summary>
    /// What the game needs from a netcode: host a room on the relay, join one, stop. Each netcode scene
    /// has one component implementing it (MirrorSession, FishNetSession, PurrNetSession, NgoSession).
    /// </summary>
    public interface ITanksSession
    {
        Netcode Netcode { get; }

        /// <summary>Start a server + local client and create a room on the relay; <paramref name="onRoomCode"/> gets its code.</summary>
        void StartHost(TanksSettings relay, Action<string> onRoomCode);

        /// <summary>Join the room <paramref name="code"/> on the relay.</summary>
        void StartClient(TanksSettings relay, string code);

        void Stop();

        bool IsServer { get; }
        /// <summary>Server running or client connected/connecting.</summary>
        bool IsActive { get; }
        /// <summary>Room code while hosting.</summary>
        string RoomCode { get; }
    }

    public static class TanksSessions
    {
        public static ITanksSession Find()
        {
            return UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).OfType<ITanksSession>().FirstOrDefault();
        }

        /// <summary>Forwards LightNet's thread-safe log queue to the Unity console. Transports call it every frame.</summary>
        public static void FlushLightNetLogs()
        {
            while (LogQueue.GetNext(out LogMessage log))
            {
                if (log.Type == ELogType.Error) Debug.LogError("LightNet: " + log.Message);
                else if (log.Type == ELogType.Warning) Debug.LogWarning("LightNet: " + log.Message);
                else Debug.Log("LightNet: " + log.Message);
            }
        }
    }
}

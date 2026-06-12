using System;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.Networking;
using Action = System.Action;
using Application = UnityEngine.Application;
#if UNITY_ANDROID
using Permission = UnityEngine.Android.Permission;
#endif

namespace Client
{
    /// <summary>
    /// Simple main-thread dispatcher for Unity.
    /// Allows code running on background threads to schedule actions on the Unity main thread.
    /// Add this MonoBehaviour to a persistent GameObject in your scene.
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Action> _queue = new();
        private static UnityMainThreadDispatcher _instance;

        private void Awake()
        {
            var uni = UnityWebRequest.Get("google.com");
            uni.SendWebRequest();
            Application.targetFrameRate = 60;
#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
            }
#endif
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MainThreadDispatcher] Exception: {ex}");
                }
            }
        }

        /// <summary>
        /// Enqueue an action to run on the Unity main thread next Update().
        /// Safe to call from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }
    }
}

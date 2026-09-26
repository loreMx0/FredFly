using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace LumaflyFredCompanion
{
    [BepInPlugin("lumafly.fred", "Lumafly Fred Companion", "1.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("[lumafly] Awake v1.5.0");

            try
            {
                GameObject host = new GameObject("lumafly_Controller");
                DontDestroyOnLoad(host);

                LampBugController ctrl = host.AddComponent<LampBugController>();
                Log.LogInfo("[lumafly] LampBugController component added");

                ctrl.Init(Config, Logger);
                Log.LogInfo("[lumafly] ctrl.Init() returned");
            }
            catch (Exception ex)
            {
                Log.LogError("[lumafly] Plugin.Awake crashed: " + ex);
            }
        }
    }
}

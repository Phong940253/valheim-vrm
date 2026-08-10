using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using UnityEngine;

#if DEBUG

using System.Diagnostics;
using System.Threading;

#endif

namespace ValheimVRM
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    public class MainPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.yoship1639.plugins.valheimvrm";
        public const string PluginName = "ValheimVRM";
        public const string PluginVersion = "1.7.2.0";

        public static MainPlugin Instance { get; private set; }

        private static Harmony _harmony = new Harmony("com.yoship1639.plugins.valheimvrm.patch");

        void Awake()
        {
            Instance = this;

            // avoid float parsing error on computers with different cultures
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            // Bind global settings to BepInEx Config (visible in F1 Configuration Manager)
            BindGlobalConfig(Config);

            Settings.ReloadGlobalSettings();

            // a semi hacky way of loading a default character, no one can name a character with and underscore as far as i am aware.
            Settings.AddSettingsFromFile("___Default", false);

            // Apply Harmony patches after the FejdStartup, this is needed because textures load way later now.
            PatchFejdStartup.Apply(_harmony);
        }

        private void BindGlobalConfig(ConfigFile config)
        {
            var g = Settings.globalSettings;
            
            config.Bind("General", "ReloadInMenu", g.ReloadInMenu, "Reload VRM settings when entering main menu")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "ReloadInMenu", g.ReloadInMenu } });
            config.Bind("General", "AcceptVrmSharing", g.AcceptVrmSharing, "Allow receiving VRM models from other players")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "AcceptVrmSharing", g.AcceptVrmSharing } });
            config.Bind("General", "DrawPlayerSizeGizmo", g.DrawPlayerSizeGizmo, "Draw player size gizmo in scene view")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "DrawPlayerSizeGizmo", g.DrawPlayerSizeGizmo } });
            config.Bind("General", "StartVrmShareDelay", g.StartVrmShareDelay, "Delay before starting VRM share (seconds)")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "StartVrmShareDelay", g.StartVrmShareDelay } });
            config.Bind("General", "ForceWindDisabled", g.ForceWindDisabled, "Disable wind on all spring bones")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "ForceWindDisabled", g.ForceWindDisabled } });
            config.Bind("General", "AllowIndividualWinds", g.AllowIndividualWinds, "Allow individual wind zones per spring bone")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "AllowIndividualWinds", g.AllowIndividualWinds } });
            config.Bind("General", "EnableProfileCode", g.EnableProfileCode, "Enable profiling code (performance impact)")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "EnableProfileCode", g.EnableProfileCode } });
            config.Bind("General", "ProfileLogThresholdMs", g.ProfileLogThresholdMs, "Log threshold for profiling (ms)")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "ProfileLogThresholdMs", g.ProfileLogThresholdMs } });
            config.Bind("General", "CameraDebug", g.CameraDebug, "Enable camera debug tracing (F8)")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "CameraDebug", g.CameraDebug } });
            config.Bind("General", "CameraSmoothing", g.CameraSmoothing, "Enable experimental camera smoothing (default off)")
                .SettingChanged += (_, _) => g.OnUpdate(new Dictionary<string, object> { { "CameraSmoothing", g.CameraSmoothing } });
        }

        internal static void PatchAll()
        {
            if (Settings.globalSettings.EnableProfileCode) PatchAllUpdateMethods.ApplyPatches(_harmony);

            _harmony.PatchAll();
            VRMShaders.Initialize();
        }
    }
}
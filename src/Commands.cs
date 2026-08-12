using System.Text;
using UnityEngine;
using VRM;

namespace ValheimVRM
{
    public static class Commands
    {
        public static readonly Console.ConsoleCommand ReloadSettings = new Console.ConsoleCommand(
            "reload_settings",
            "reload VRM settings for your character (from .txt)",
            args =>
            {
                string name = VrmManager.PlayerToName[Player.m_localPlayer];

                if (!VrmManager.VrmDic.ContainsKey(name)) return;

                Settings.AddSettingsFromFile(name, VrmManager.VrmDic[name].Source == VRM.SourceType.Shared);
                VrmManager.VrmDic[name].RecalculateSettingsHash();

                args.Context.AddString("Settings for " + name + " were reloaded");

                Player.m_localPlayer.GetComponent<VrmController>().ShareVrm(false);
            }
        );

        public static readonly Console.ConsoleCommand ReloadGlobalSettings = new Console.ConsoleCommand(
            "reload_global_settings",
            "reload global VRM settings (from BepInEx .cfg)",
            args =>
            {
                MainPlugin.Instance.Config.Reload();
                Settings.ReloadGlobalSettings();

                args.Context.AddString("Global settings were reloaded");
            }
        );

        public static readonly Console.ConsoleCommand VrmPerf = new Console.ConsoleCommand(
            "vrm_perf",
            "print per-frame cost diagnostics for all active VRM models (animation sync, spring bones, culling, texture formats)",
            args =>
            {
                args.Context.AddString("--- [ValheimVRM] vrm_perf ---");

                float fps = Time.deltaTime > 0.0001f ? 1.0f / Time.deltaTime : 0.0f;
                args.Context.AddString($"FPS: {fps.ToString("0.0")}");

                var g = Settings.globalSettings;
                args.Context.AddString($"Culling: distance={g.DistanceCullingEnabled} ({g.VrmCullingDistance}m) | invisible={g.InvisibleModelCulling} | textures={g.CompressTextures}");
                args.Context.AddString($"Profile: EnableProfileCode={g.EnableProfileCode} | ProfileLogThresholdMs={g.ProfileLogThresholdMs}");

                foreach (var entry in VrmManager.PlayerToVrmInstance)
                {
                    var player = entry.Key;
                    var visual = entry.Value;
                    if (player == null || visual == null) continue;

                    var sb = new StringBuilder();

                    var animSync = visual.GetComponent<VRMAnimationSync>();

                    bool isLocal = player == Player.m_localPlayer;

                    float dist = Player.m_localPlayer != null
                        ? Vector3.Distance(player.transform.position, Player.m_localPlayer.transform.position)
                        : 0.0f;

                    sb.Append(VrmManager.PlayerToName.TryGetValue(player, out var pname) ? (pname ?? player.name) : player.name);
                    sb.Append(isLocal ? " [LOCAL]" : " [REMOTE]");
                    sb.Append($" | dist={dist.ToString("0.0")}m");
                    sb.Append(animSync != null && animSync.enabled ? " | anim=ON" : " | anim=OFF");

                    var springBones = visual.GetComponentsInChildren<VRMSpringBone>(true);
                    int activeSpring = 0;
                    foreach (var bone in springBones)
                    {
                        if (bone.enabled) activeSpring++;
                    }
                    sb.Append($" | springBones={activeSpring}/{springBones.Length}");

                    var renderers = visual.GetComponentsInChildren<Renderer>(true);
                    int visible = 0;
                    foreach (var renderer in renderers)
                    {
                        if (renderer.isVisible) visible++;
                    }
                    sb.Append($" | renders={visible}/{renderers.Length}");

                    // Texture formats used by this model's materials.
                    var formats = new StringBuilder();
                    foreach (var renderer in renderers)
                    {
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (material == null) continue;
                            var tex = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") as Texture2D : null;
                            if (tex == null) continue;

                            string label = $"{tex.format}{(tex.mipmapCount > 1 ? "+mips" : "-nomips")}";
                            if (!formats.ToString().Contains(label))
                            {
                                if (formats.Length > 0) formats.Append(", ");
                                formats.Append(label);
                            }
                        }
                    }
                    sb.Append($" | tex=[{formats}]");

                    args.Context.AddString(sb.ToString());
                }

                args.Context.AddString("--- vrm_perf end ---");
            }
        );

        public static int Trigger()
        {
            return 1;
        }
    }
}
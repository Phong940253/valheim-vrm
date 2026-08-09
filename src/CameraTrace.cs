using System;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimVRM
{
    // Diagnostic CSV trace of the local player camera chain. Toggle with F8 in game
    // (or set global setting CameraDebug=true). Writes ValheimVRM/camera_trace.csv.
    public static class CameraTrace
    {
        private static StreamWriter writer;
        private static bool enabled;
        private static bool hasPrev;
        private static Vector3 prevEye;
        private static Vector3 prevCam;

        public static bool Enabled
        {
            get => enabled;
            set
            {
                if (value == enabled) return;
                enabled = value;
                if (enabled) Start(); else Stop();
            }
        }

        private static void Start()
        {
            try
            {
                Directory.CreateDirectory(Settings.ValheimVRMDir);
                var path = Path.Combine(Settings.ValheimVRMDir, "camera_trace.csv");
                writer = new StreamWriter(path, false, Encoding.UTF8);
                writer.WriteLine("time,eye_x,eye_y,eye_z,head_x,head_y,head_z,vrmHead_x,vrmHead_y,vrmHead_z,smoothHead_x,smoothHead_y,smoothHead_z,cam_x,cam_y,cam_z,cam_dx,cam_dy,cam_dz,eye_dx,eye_dy,eye_dz,nearClip");
                hasPrev = false;
                Debug.Log("[ValheimVRM] Camera trace started: " + path);
            }
            catch (Exception e)
            {
                Debug.LogError("[ValheimVRM] Camera trace start failed: " + e);
                CloseWriter();
            }
        }

        private static void Stop()
        {
            CloseWriter();
            Debug.Log("[ValheimVRM] Camera trace stopped");
        }

        private static void CloseWriter()
        {
            if (writer == null) return;
            try { writer.Flush(); writer.Close(); } catch { }
            writer = null;
        }

        internal static void RecordFrame(GameCamera camera, Player player)
        {
            if (writer == null || camera == null || player == null || player.m_eye == null) return;

            try
            {
                Vector3 eye = player.m_eye.position;

                Vector3 head = Vector3.zero;
                var anim = player.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    var ht = anim.GetBoneTransform(HumanBodyBones.Head);
                    if (ht != null) head = ht.position;
                }

                Vector3 vrmHead = Vector3.zero;
                if (VrmManager.PlayerToVrmInstance.TryGetValue(player, out var vrm) && vrm != null)
                {
                    var va = vrm.GetComponentInChildren<Animator>();
                    if (va != null)
                    {
                        var ht = va.GetBoneTransform(HumanBodyBones.Head);
                        if (ht != null) vrmHead = ht.position;
                    }
                }

                Vector3 smoothHead = Vector3.zero;
                if (Patch_Character_GetHeadPoint.headStates.TryGetValue(player, out var state))
                {
                    smoothHead = state.Position;
                }

                Vector3 cam = camera.transform.position;
                float nearClip = -1f;
                var camComp = camera.GetComponent<Camera>();
                if (camComp != null) nearClip = camComp.nearClipPlane;

                Vector3 camDelta = hasPrev ? cam - prevCam : Vector3.zero;
                Vector3 eyeDelta = hasPrev ? eye - prevEye : Vector3.zero;
                prevCam = cam;
                prevEye = eye;
                hasPrev = true;

                writer.WriteLine(string.Join(",",
                    Time.time.ToString("F4"),
                    eye.x.ToString("F4"), eye.y.ToString("F4"), eye.z.ToString("F4"),
                    head.x.ToString("F4"), head.y.ToString("F4"), head.z.ToString("F4"),
                    vrmHead.x.ToString("F4"), vrmHead.y.ToString("F4"), vrmHead.z.ToString("F4"),
                    smoothHead.x.ToString("F4"), smoothHead.y.ToString("F4"), smoothHead.z.ToString("F4"),
                    cam.x.ToString("F4"), cam.y.ToString("F4"), cam.z.ToString("F4"),
                    camDelta.x.ToString("F4"), camDelta.y.ToString("F4"), camDelta.z.ToString("F4"),
                    eyeDelta.x.ToString("F4"), eyeDelta.y.ToString("F4"), eyeDelta.z.ToString("F4"),
                    nearClip.ToString("F4")));
            }
            catch (Exception e)
            {
                Debug.LogError("[ValheimVRM] Camera trace record failed: " + e.Message);
                CloseWriter();
            }
        }
    }

    // Clamp the local player's eye Y movement per frame. VRMEyePositionSync copies
    // the skeleton eye bone height into m_eye; the skeleton can glitch a one-frame
    // ~40cm jump (animation race with our bone writes) which previously made the
    // camera lurch. Capping the per-frame delta removes the jump without lag.
    // The vanilla skeleton's head can oscillate ~8cm at 30Hz (animation
    // cross-blend race between our bone writes and the game's Animator, plus the
    // head's parent chain being re-written). The camera reads player.m_eye inside
    // GameCamera.LateUpdate, so we stabilize the eye transform at the exact point
    // it is consumed: a low-pass filter (EMA) with a large-step hard clamp. Normal
    // movement (walking, jumping, boats) is slow and passes almost unchanged; the
    // high-frequency wobble is attenuated.
    [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
    static class Patch_GameCamera_EyeStabilizer
    {
        private static Vector3 smoothed;
        private static bool hasValue;
        private static float lastTime;

        private const float TimeConst = 0.08f; // seconds; EMA time constant
        private const float MaxStep = 0.15f;   // m/frame hard cap on eye change

        private static bool Enabled() => Settings.globalSettings.CameraSmoothing;

        [HarmonyPrefix]
        static void Prefix()
        {
            if (!Enabled())
            {
                hasValue = false;
                return;
            }

            Player local = Player.m_localPlayer;
            if (local == null || local.m_eye == null)
            {
                hasValue = false;
                return;
            }

            Vector3 target = local.m_eye.position;
            float dt = hasValue ? Mathf.Max(0f, Time.time - lastTime) : 0f;
            lastTime = Time.time;

            if (!hasValue)
            {
                smoothed = target;
                hasValue = true;
                return;
            }

            // EMA: y' = y + (target - y) * (1 - exp(-dt/T))
            float alpha = 1f - Mathf.Exp(-dt / TimeConst);
            Vector3 delta = target - smoothed;
            if (delta.magnitude > MaxStep)
            {
                delta = delta.normalized * MaxStep;
                alpha = 1f;
            }
            smoothed += delta * alpha;
            local.m_eye.position = smoothed;
        }
    }

    // Last-resort clamp on the actual camera transform. GameCamera's wall collide
    // or its SmoothDamp following a glitched eye can reposition the camera by more
    // than a meter in one frame; cap the FINAL transform position so such a jump
    // cannot be rendered.
    [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
    static class Patch_GameCamera_FinalClamp
    {
        private static Vector3 prevPos;
        private static bool hasPrev;

        private const float MaxCamStep = 0.35f; // m/frame

        private static bool Enabled() => Settings.globalSettings.CameraSmoothing;

        [HarmonyPostfix]
        static void Postfix(GameCamera __instance)
        {
            if (!Enabled())
            {
                hasPrev = false;
                return;
            }

            var t = __instance.transform;
            if (!hasPrev)
            {
                prevPos = t.position;
                hasPrev = true;
                return;
            }

            Vector3 delta = t.position - prevPos;
            if (delta.magnitude > MaxCamStep)
            {
                t.position = prevPos + delta.normalized * MaxCamStep;
            }
            prevPos = t.position;
        }
    }

    // Clamp RATE the local player's eye Y movement per frame. VRMEyePositionSync
    // copies the skeleton eye bone height into m_eye; the skeleton can glitch a
    // one-frame ~40cm jump (animation race) which used to make the camera lurch.
    // A hard clamp naively caps sustained movement too (running uphill at 45°
    // moves the eye ~9-11cm/frame; an 8cm cap made the camera lag down the slope
    // and stick near the ground until teleport). So the clamp is adaptive: it is
    // sized to ~1.5x the EMA of recent per-frame eye speed, so real movement
    // always passes and only anomaly jumps are clipped.
    [HarmonyPatch(typeof(VRMEyePositionSync), "LateUpdate")]
    static class Patch_VRMEyePositionSync_Smooth
    {
        private static float lastY;
        private static bool hasLast;
        private static float avgRate = 0f; // smoothed m/frame

        private const float MinEyeStep = 0.08f;
        private const float RateAlpha = 0.2f;

        [HarmonyPostfix]
        static void Postfix(VRMEyePositionSync __instance)
        {
            try
            {
                var orgEye = (Transform)typeof(VRMEyePositionSync).GetField("orgEye", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance);
                if (orgEye == null) return;

                float cur = orgEye.position.y;
                if (hasLast)
                {
                    float delta = cur - lastY;
                    avgRate = Mathf.Lerp(avgRate, Mathf.Abs(delta), RateAlpha);

                    float maxStep = Mathf.Max(MinEyeStep, avgRate * 1.5f);
                    if (Mathf.Abs(delta) > maxStep)
                    {
                        var p = orgEye.position;
                        p.y = lastY + Mathf.Sign(delta) * maxStep;
                        orgEye.position = p;
                        cur = p.y;
                    }
                }
                lastY = cur;
                hasLast = true;
            }
            catch { }
        }
    }

    // Outlier-damping of the final camera position. GameCamera's wall-collision
    // can snap the camera by several meters in one frame (5.5m seen in traces)
    // when the eye position glitches near geometry. Limit per-frame camera
    // movement to ~3x the recent average speed, converting snaps into quick
    // glides. Normal aiming/rotating is untouched because it's not an outlier.
    [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
    static class Patch_GameCamera_PositionDamp
    {
        private static Vector3 prevPos;
        private static bool hasPrev;
        private static float avgSpeed; // smoothed m/frame

        private const float MinLimit = 0.12f;  // m/frame floor even if idle
        private const float OutlierFactor = 2.5f;
        private const float AvgAlpha = 0.1f;

        [HarmonyPostfix]
        static void Postfix(ref Vector3 pos)
        {
            if (!hasPrev)
            {
                prevPos = pos;
                avgSpeed = 0f;
                hasPrev = true;
                return;
            }

            float dist = Vector3.Distance(pos, prevPos);
            avgSpeed += (dist - avgSpeed) * AvgAlpha;

            float limit = Mathf.Max(MinLimit, avgSpeed * OutlierFactor);
            if (dist > limit)
            {
                pos = prevPos + (pos - prevPos).normalized * limit;
            }
            prevPos = pos;
        }
    }

    [HarmonyPatch(typeof(GameCamera), "UpdateNearClipping")]
    static class Patch_GameCamera_UpdateNearClipping
    {
        private static float lastApplied = -1f;
        private static int sustainFrames;

        // UpdateNearClipping flips the near clip plane between min/max based on a
        // per-frame physics check. When the camera is pressed against geometry the
        // check can oscillate every frame -> visible flicker. Hold the previous
        // value until the new one is sustained for several frames (hysteresis).
        [HarmonyPostfix]
        static void Postfix(GameCamera __instance)
        {
            var camField = Utils.GetField<GameCamera>("m_camera");
            if (camField == null) return;
            var cam = (Camera)camField.GetValue(__instance);
            if (cam == null) return;

            float desired = cam.nearClipPlane;
            if (lastApplied < 0f) lastApplied = desired;

            if (Mathf.Abs(desired - lastApplied) < 0.001f)
            {
                sustainFrames = 0;
                return;
            }

            sustainFrames++;
            if (sustainFrames >= 6)
            {
                lastApplied = desired;
                sustainFrames = 0;
            }
            else
            {
                cam.nearClipPlane = lastApplied;
            }
        }
    }

    [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
    static class Patch_GameCamera_Trace
    {
        [HarmonyPostfix]
        static void Postfix(GameCamera __instance)
        {
            if (ZInput.GetKeyDown(KeyCode.F8))
            {
                CameraTrace.Enabled = !CameraTrace.Enabled;
                Utils.SendNotification("[ValheimVRM] Camera trace " + (CameraTrace.Enabled ? "ON" : "OFF"));
            }

            if (!CameraTrace.Enabled) return;
            CameraTrace.RecordFrame(__instance, Player.m_localPlayer);
        }
    }
}

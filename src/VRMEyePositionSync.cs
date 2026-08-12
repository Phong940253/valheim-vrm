using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimVRM
{
    public class VRMEyePositionSync : MonoBehaviour
    {
        private Transform vrmEye;
        private Transform orgEye;
        private float heightOffset;

        public void Setup(Transform vrmEye)
        {
            this.vrmEye = vrmEye;
            Player player = GetComponent<Player>();
            if (player != null)
            {
                this.orgEye = player.m_eye;
            }
            else
            {
                Debug.LogError("Player component or m_eye is null. Ensure the component exists.");
            }
        }

        public void SetHeightOffset(float offset)
        {
            this.heightOffset = offset;
        }

        /// <summary>
        /// The cached VRM eye/head/neck bone this component follows. Used as a
        /// cheaper fallback than Animator.GetBoneTransform in hot camera polls.
        /// </summary>
        public Transform GetEyeBone()
        {
            return vrmEye;
        }

        void LateUpdate()
        {
            // Stale components (left over from a disconnect, or attached to a dead
            // player object) must never write the eye height - a destroyed vrmEye
            // reads as (0,0,0) and would drag the camera below the ground.
            if (vrmEye == null || orgEye == null) return;
            if (Player.m_localPlayer == null) return;
            if (!isActiveAndEnabled) return;
            var owningPlayer = GetComponent<Player>();
            if (owningPlayer == null || owningPlayer.gameObject == null || !owningPlayer.gameObject.activeInHierarchy) return;

            // While any inventory UI is open the vanilla animation state drops the
            // head bone; writing the VRM eye height would push the camera below the
            // ground. Keep the vanilla eye height during those states.
            if (InventoryGui.instance != null)
            {
                bool uiOpen = false;
                try { uiOpen = InventoryGui.IsVisible(); }
                catch (Exception) { uiOpen = false; }
                if (uiOpen) return;
            }

            var pos = this.orgEye.position;
            float eyeY = this.vrmEye.position.y + this.heightOffset;

            // The player root sits at floor level. Never let the camera anchor
            // dive below the ground.
            float minY = transform.position.y + 0.25f;
            if (eyeY < minY) eyeY = minY;

            pos.y = eyeY;
            this.orgEye.position = pos;
        }
    }
}


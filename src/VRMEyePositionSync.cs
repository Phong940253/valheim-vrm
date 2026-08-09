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

        void LateUpdate()
        {
            if (orgEye != null && vrmEye != null)
            {
                // When interacting with a container (chest/cart/etc.) the vanilla animation
                // state drops the head bone and the written eye Y would push the camera
                // below the ground. Keep the vanilla eye height while the chest UI is open.
                if (InventoryGui.instance != null && InventoryGui.instance.IsContainerOpen())
                {
                    return;
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
            else if (orgEye == null)
            {
                Debug.LogError("orgEye is null. Make sure Setup method is called and Player component is available.");
            }
        }
    }
}


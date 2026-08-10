using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVRM
{
	[DefaultExecutionOrder(int.MaxValue)]
	public class VRMAnimationSync : MonoBehaviour
	{
		private Animator orgAnim, vrmAnim;
		private HumanPoseHandler orgPose, vrmPose;
		private HumanPose hp = new HumanPose();
		private bool ragdoll;
		private Settings.VrmSettingsContainer settings;
		private Vector3? adjustPos;
		private int oldStateHash;

		// Cached bone lookups: Animator.GetBoneTransform is a dictionary lookup that
		// was being called ~220 times per frame per player. The human bone mapping is
		// fixed for an avatar, so the results are cached once in Setup().
		private Transform[] orgBones = new Transform[55];
		private Transform[] vrmBones = new Transform[55];
		private bool bonesCached;
		private int remoteFrameCounter;
		private bool runThisFrame;
		private bool isLocalPlayer = true;

		public void Setup(Animator orgAnim, Settings.VrmSettingsContainer settings, bool isRagdoll = false, bool localPlayer = false)
		{
			this.ragdoll = isRagdoll;
			this.isLocalPlayer = localPlayer;
			this.settings = settings;
			this.orgAnim = orgAnim;
			this.vrmAnim = GetComponent<Animator>();
			this.vrmAnim.applyRootMotion = true;
			this.vrmAnim.updateMode = orgAnim.updateMode;
			this.vrmAnim.feetPivotActive = orgAnim.feetPivotActive;
			this.vrmAnim.layersAffectMassCenter = orgAnim.layersAffectMassCenter;
			this.vrmAnim.stabilizeFeet = orgAnim.stabilizeFeet;

			PoseHandlerCreate(orgAnim, vrmAnim);

			for (var i = 0; i < 55; i++)
			{
				orgBones[i] = orgAnim.GetBoneTransform((HumanBodyBones)i);
				vrmBones[i] = vrmAnim.GetBoneTransform((HumanBodyBones)i);
			}
			bonesCached = true;
		}

		/// <summary>
		/// Cached head bone so per-frame GetHeadPoint polls don't redo lookup chains.
		/// </summary>
		public Transform GetCachedHead()
		{
			return bonesCached ? vrmBones[(int)HumanBodyBones.Head] : vrmAnim != null ? vrmAnim.GetBoneTransform(HumanBodyBones.Head) : null;
		}

		// Writes vrmTrans's position into orgTrans. For the local player the entire
		// head chain is left to the vanilla Animator: neck/spine/chest are the
		// head's parent bones, so writing them from the VRM moves the skeleton
		// head's world position every frame. Doing so raced the vanilla pose and
		// made the heads oscillate ~8cm at ~30Hz (seen in camera traces), shaking
		// the camera when pressed against geometry. Arms/legs are still synced so
		// equipment stays attached to the VRM stance.
		private void WriteBonePosition(Transform orgTrans, Transform vrmTrans, HumanBodyBones bone)
		{
			if (isLocalPlayer && settings.SmoothCameraHead)
			{
				switch (bone)
				{
					case HumanBodyBones.Spine:
					case HumanBodyBones.Chest:
					case HumanBodyBones.UpperChest:
					case HumanBodyBones.Neck:
					case HumanBodyBones.Head:
						return;
				}
			}

			Vector3 target = vrmTrans.position + Vector3.up * settings.ModelOffsetY;
			orgTrans.position = target;
		}

		void PoseHandlerCreate(Animator org, Animator vrm)
		{
			OnDestroy();
			orgPose = new HumanPoseHandler(org.avatar, org.transform);
			vrmPose = new HumanPoseHandler(vrm.avatar, vrm.transform);
		}

		void OnDestroy()
		{
			if (orgPose != null)
				orgPose.Dispose();
			if (vrmPose != null)
				vrmPose.Dispose();
		}

		const int FirstTime = -161139084;
		const int Usually = 229373857;  // standing idle
		const int FirstRise = -1536343465; // stand up upon login
		const int RiseUp = -805461806;
		const int StartToSitDown = 890925016;
		const int SittingIdle = -1544306596;
		const int StandingUpFromSit = -805461806;
		const int SittingChair = -1829310159;
		const int SittingThrone = 1271596;
		const int SittingShip = -675369009;
		const int StartSleeping = 337039637;
		const int Sleeping = -1603096;
		const int GetUpFromBed = -496559199;
		const int Crouch = -2015693266;
		const int HoldingMast = -2110678410;
		const int HoldingDragon = -2076823180; // that thing in a front of longship

		private static List<int> adjustHipHashes = new List<int>()
		{
			SittingChair,
			SittingThrone,
			SittingShip,
			Sleeping
		};
		private Vector3 StateHashToOffset(int stateHash, out float interpSpeed)
		{
			interpSpeed = Time.deltaTime * 5;
			switch (stateHash)
			{
				case StartToSitDown:
				case SittingIdle:
					return settings.SittingIdleOffset;

				case SittingChair:
					return settings.SittingOnChairOffset;

				case SittingThrone:
					return settings.SittingOnThroneOffset;

				case SittingShip:
					return settings.SittingOnShipOffset;

				case HoldingMast:
					return settings.HoldingMastOffset;

				case HoldingDragon:
					return settings.HoldingDragonOffset;

				case Sleeping:
					return settings.SleepingOffset;

				default:
					interpSpeed = 1;
					return Vector3.zero;
			}
		}
		void Update()
		{
			// Remote players get pose sync at ~30 Hz; visually indistinguishable from
			// 60 Hz but halves the per-frame bone-write work for every other player.
			runThisFrame = true;
			if (!isLocalPlayer)
			{
				remoteFrameCounter++;
				if ((remoteFrameCounter & 1) != 0) runThisFrame = false;
			}

			if (!runThisFrame || !bonesCached) return;

			vrmAnim.transform.localPosition = Vector3.zero;
			if (!ragdoll)
			{
				for (var i = 0; i < 55; i++)
				{
					var orgTrans = orgBones[i];
					var vrmTrans = vrmBones[i];

					if (i > 0 && orgTrans != null && vrmTrans != null)
					{
						if ((HumanBodyBones)i == HumanBodyBones.LeftFoot || (HumanBodyBones)i == HumanBodyBones.RightFoot)
						{
							orgTrans.position = vrmTrans.position;
						}
						else
						{
							WriteBonePosition(orgTrans, vrmTrans, (HumanBodyBones)i);
						}
					}
				}
			}

			vrmAnim.transform.localPosition += Vector3.up * settings.ModelOffsetY;
		}

		void LateUpdate()
		{
			if (!runThisFrame)
			{
				// LateUpdate can be called without Update (script disabled mid-frame);
				// keep the previous decision valid, but never repeat the work.
				return;
			}

			if (ragdoll)
			{
				vrmAnim.transform.localPosition = Vector3.zero;
				var verticalOffset = Vector3.up * settings.ModelOffsetY;

				for (var i = 0; i < 55; i++)
				{
					var orgTrans = orgBones[i];
					var vrmTrans = vrmBones[i];
					if (orgTrans != null && vrmTrans != null)
					{
						vrmTrans.position = orgTrans.position + verticalOffset;
						vrmTrans.rotation = orgTrans.rotation;
					}
				}
				return;
			}

			float playerScaleFactor = settings.PlayerHeight / 1.85f;

			vrmAnim.transform.localPosition = Vector3.zero;

			orgPose.GetHumanPose(ref hp);
			vrmPose.SetHumanPose(ref hp);

			var curStateHash = orgAnim.GetCurrentAnimatorStateInfo(0).shortNameHash;
			var nextState = orgAnim.GetNextAnimatorStateInfo(0);
			var nextStateHash = nextState.shortNameHash;

			var vrmHip = vrmBones[(int)HumanBodyBones.Hips];
			var orgHip = orgBones[(int)HumanBodyBones.Hips];

			if (vrmHip == null || orgHip == null) return;

			vrmHip.position = orgHip.position;

			Vector3 actualAdjustHipPos;
			float actualInterpSpeed;

			// Phase 1: Calculate current state adjustment

			var curAdjustPos = Vector3.zero;

			if (!adjustHipHashes.Contains(curStateHash))
			{
				Vector3 curOrgHipPos = orgHip.position - orgHip.parent.position;
				Vector3 curVrmHipPos = curOrgHipPos * playerScaleFactor;

				curAdjustPos = curVrmHipPos - curOrgHipPos;
			}

			float curInterpSpeed = Time.deltaTime * 5;
			Vector3 curOffset = StateHashToOffset(curStateHash, out curInterpSpeed);
			if (curOffset != Vector3.zero) curAdjustPos += orgHip.transform.rotation * curOffset;

			// Phase 2: Calculate next state adjustment

			var nextAdjustPos = Vector3.zero;

			if (nextStateHash != 0)
			{
				if (!adjustHipHashes.Contains(nextStateHash))
				{
					Vector3 nextOrgHipPos = orgHip.position - orgHip.parent.position;
					Vector3 nextVrmHipPos = nextOrgHipPos * playerScaleFactor;

					nextAdjustPos = nextVrmHipPos - nextOrgHipPos;
				}

				float nextInterpSpeed = Time.deltaTime * 5;
				Vector3 nextOffset = StateHashToOffset(nextStateHash, out nextInterpSpeed);
				if (nextOffset != Vector3.zero) nextAdjustPos += orgHip.transform.rotation * nextOffset;

				float trans = Mathf.Clamp01(nextState.normalizedTime * nextState.length / 0.5f);

				actualInterpSpeed = Mathf.Lerp(curInterpSpeed, nextInterpSpeed, trans);

				actualAdjustHipPos = Vector3.Lerp(curAdjustPos, nextAdjustPos, trans);
			}
			else
			{
				actualInterpSpeed = curInterpSpeed;

				actualAdjustHipPos = curAdjustPos;
			}

			// Phase 3: Lerp and apply

			adjustPos = adjustPos.HasValue ? Vector3.Lerp(adjustPos.Value, actualAdjustHipPos, actualInterpSpeed) : curAdjustPos;

			vrmHip.position += adjustPos.Value;

			if (!ragdoll)
			{
				for (var i = 0; i < 55; i++)
				{
					var orgTrans = orgBones[i];
					var vrmTrans = vrmBones[i];

					if (i > 0 && orgTrans != null && vrmTrans != null)
					{
						if ((HumanBodyBones)i == HumanBodyBones.LeftFoot || (HumanBodyBones)i == HumanBodyBones.RightFoot)
						{
							orgTrans.position = vrmTrans.position;
						}
						else
						{
							WriteBonePosition(orgTrans, vrmTrans, (HumanBodyBones)i);
						}
					}
				}
			}

			vrmAnim.transform.localPosition += Vector3.up * settings.ModelOffsetY;

			oldStateHash = curStateHash;
		}
	}
}

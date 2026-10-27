using System.Collections.Generic;
using UnityEngine;

// Drives a Humanoid avatar from the metric camera-space landmarks published by
// MediaPipeBodyTracker. The hips and chest are rotated to match the tracked
// body frames, so each limb bone can then be aimed straight at its tracked
// joint direction in world space. The avatar's rest pose is captured at Start
// and the tracked pose is applied as a delta from it, so no animation
// controller or manual rest-pose setup is required.
public class HumanoidPoseDriver : MonoBehaviour
{
    public bool HasValidPose { get; private set; }

    private const int LandmarkCount = 33;
    private const float MinimumDirectionSqrMagnitude = 0.0001f;

    private const int LeftShoulderLandmark = 11;
    private const int RightShoulderLandmark = 12;
    private const int LeftElbowLandmark = 13;
    private const int RightElbowLandmark = 14;
    private const int LeftWristLandmark = 15;
    private const int RightWristLandmark = 16;
    private const int LeftHipLandmark = 23;
    private const int RightHipLandmark = 24;
    private const int LeftKneeLandmark = 25;
    private const int RightKneeLandmark = 26;
    private const int LeftAnkleLandmark = 27;
    private const int RightAnkleLandmark = 28;
    private const int LeftFootLandmark = 31;
    private const int RightFootLandmark = 32;

    [Header("Sources")]
    [SerializeField]
    private MediaPipeBodyTracker bodyTracker;

    [SerializeField]
    private Animator targetAnimator;

    [Header("Placement")]
    [Tooltip("Move the avatar so its hips sit at the tracked hip position.")]
    [SerializeField]
    private bool followHipsPosition = true;

    [Tooltip(
        "Scales tracked movement before it is applied to the scene. " +
        "1 is real-world meters."
    )]
    [SerializeField]
    [Min(0f)]
    private float positionScale = 1f;

    [Tooltip(
        "Closest the avatar is allowed to get to the camera. Scaling the tracked " +
        "position along its view ray keeps the avatar on the same part of the " +
        "screen without letting it clip the near plane or fill the frame."
    )]
    [SerializeField]
    [Min(0.1f)]
    private float minimumDistance = 1.2f;

    [Header("Motion")]
    [Tooltip(
        "Higher values follow the tracker more tightly. " +
        "0 applies every detection immediately."
    )]
    [SerializeField]
    [Min(0f)]
    private float smoothing = 12f;

    private struct LimbBone
    {
        public Transform Bone;
        public Quaternion RestLocalRotation;
        public Vector3 RestDirectionInParent;
        public int StartLandmark;
        public int EndLandmark;
    }

    private readonly List<LimbBone> limbBones = new List<LimbBone>();

    private Transform hips;
    private Transform spine;
    private Transform chest;

    private Quaternion hipsRestRotation;
    private Quaternion spineRestRotation;
    private Quaternion chestRestRotation;

    // Rest body frames, built from the bone joints rather than the bone axes.
    private Quaternion restPelvisRotation;
    private Quaternion restChestRotation;

    private Transform trackingCamera;

    private readonly Vector3[] pose = new Vector3[LandmarkCount];
    private readonly bool[] poseValid = new bool[LandmarkCount];

    private bool hasPlacedRoot;

    private void Start()
    {
        if (bodyTracker == null)
        {
            bodyTracker = FindAnyObjectByType<MediaPipeBodyTracker>();
        }

        if (targetAnimator == null)
        {
            targetAnimator = GetComponentInChildren<Animator>();
        }

        if (bodyTracker == null)
        {
            Debug.LogError(
                "HumanoidPoseDriver: no MediaPipeBodyTracker in the scene.",
                this
            );

            enabled = false;
            return;
        }

        if (targetAnimator == null || targetAnimator.avatar == null || !targetAnimator.isHuman)
        {
            Debug.LogError(
                "HumanoidPoseDriver: assign an Animator whose Avatar is Humanoid.",
                this
            );

            enabled = false;
            return;
        }

        if (!CacheTorso())
        {
            enabled = false;
            return;
        }

        CacheLimbs();

        if (followHipsPosition)
        {
            Camera mainCamera = Camera.main;

            if (mainCamera != null)
            {
                trackingCamera = mainCamera.transform;
            }
            else
            {
                Debug.LogWarning(
                    "HumanoidPoseDriver: no Main Camera, so the avatar will stay in place.",
                    this
                );
            }
        }
    }

    private bool CacheTorso()
    {
        hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
        spine = targetAnimator.GetBoneTransform(HumanBodyBones.Spine);
        chest = targetAnimator.GetBoneTransform(HumanBodyBones.Chest);
        Transform neck = targetAnimator.GetBoneTransform(HumanBodyBones.Neck);
        Transform leftArm = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightArm = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform leftLeg = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rightLeg = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg);

        if (hips == null || spine == null || chest == null || neck == null ||
            leftArm == null || rightArm == null || leftLeg == null || rightLeg == null)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the Humanoid avatar is missing a required bone.",
                this
            );

            return false;
        }

        hipsRestRotation = hips.rotation;
        spineRestRotation = spine.rotation;
        chestRestRotation = chest.rotation;

        bool hasPelvisFrame = TryBuildBodyRotation(
            rightLeg.position - leftLeg.position,
            spine.position - hips.position,
            out restPelvisRotation
        );

        bool hasChestFrame = TryBuildBodyRotation(
            rightArm.position - leftArm.position,
            neck.position - chest.position,
            out restChestRotation
        );

        if (!hasPelvisFrame || !hasChestFrame)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the avatar's rest pose has degenerate hips or shoulders.",
                this
            );

            return false;
        }

        return true;
    }

    private void CacheLimbs()
    {
        AddLimb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
            LeftShoulderLandmark, LeftElbowLandmark);

        AddLimb(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            LeftElbowLandmark, LeftWristLandmark);

        AddLimb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
            RightShoulderLandmark, RightElbowLandmark);

        AddLimb(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            RightElbowLandmark, RightWristLandmark);

        AddLimb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
            LeftHipLandmark, LeftKneeLandmark);

        AddLimb(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            LeftKneeLandmark, LeftAnkleLandmark);

        AddLimb(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            LeftAnkleLandmark, LeftFootLandmark);

        AddLimb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
            RightHipLandmark, RightKneeLandmark);

        AddLimb(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            RightKneeLandmark, RightAnkleLandmark);

        AddLimb(HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
            RightAnkleLandmark, RightFootLandmark);
    }

    private void AddLimb(
        HumanBodyBones boneId,
        HumanBodyBones childId,
        int startLandmark,
        int endLandmark)
    {
        Transform bone = targetAnimator.GetBoneTransform(boneId);
        Transform child = targetAnimator.GetBoneTransform(childId);

        if (bone == null || child == null || bone.parent == null)
        {
            return;
        }

        Vector3 restDirectionWorld = child.position - bone.position;

        if (restDirectionWorld.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return;
        }

        limbBones.Add(new LimbBone
        {
            Bone = bone,
            RestLocalRotation = bone.localRotation,
            RestDirectionInParent =
                Quaternion.Inverse(bone.parent.rotation) * restDirectionWorld.normalized,
            StartLandmark = startLandmark,
            EndLandmark = endLandmark
        });
    }

    private void Update()
    {
        HasValidPose = false;

        if (!ReadPose())
        {
            return;
        }

        if (!TryGetBodyRotations(out Quaternion pelvisDelta, out Quaternion chestDelta))
        {
            return;
        }

        HasValidPose = true;

        float blend = smoothing > 0f
            ? 1f - Mathf.Exp(-smoothing * Time.deltaTime)
            : 1f;

        ApplyWorldRotation(hips, pelvisDelta * hipsRestRotation, blend);
        ApplyWorldRotation(spine, chestDelta * spineRestRotation, blend);
        ApplyWorldRotation(chest, chestDelta * chestRestRotation, blend);

        for (int i = 0; i < limbBones.Count; i++)
        {
            LimbBone limb = limbBones[i];

            if (!poseValid[limb.StartLandmark] || !poseValid[limb.EndLandmark])
            {
                continue;
            }

            Vector3 direction = pose[limb.EndLandmark] - pose[limb.StartLandmark];

            if (direction.sqrMagnitude < MinimumDirectionSqrMagnitude)
            {
                continue;
            }

            Vector3 targetParentDirection =
                Quaternion.Inverse(limb.Bone.parent.rotation) * direction.normalized;

            Quaternion targetLocalRotation =
                Quaternion.FromToRotation(
                    limb.RestDirectionInParent,
                    targetParentDirection) *
                limb.RestLocalRotation;

            limb.Bone.localRotation = Quaternion.Slerp(
                limb.Bone.localRotation,
                targetLocalRotation,
                blend
            );
        }

        if (followHipsPosition && trackingCamera != null)
        {
            FollowHips(blend);
        }
    }

    private bool ReadPose()
    {
        if (!bodyTracker.BodyDetected)
        {
            return false;
        }

        IReadOnlyList<Vector3> cameraSpaceLandmarks = bodyTracker.CameraSpaceLandmarks;

        if (cameraSpaceLandmarks == null || cameraSpaceLandmarks.Count < LandmarkCount)
        {
            return false;
        }

        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector3 landmark = cameraSpaceLandmarks[i];

            // Kinect camera space is X right, Y down, Z forward; Unity is Y up.
            pose[i] = new Vector3(landmark.x, -landmark.y, landmark.z);
            poseValid[i] = landmark.z > 0f;
        }

        return true;
    }

    private bool TryGetBodyRotations(out Quaternion pelvisDelta, out Quaternion chestDelta)
    {
        pelvisDelta = Quaternion.identity;
        chestDelta = Quaternion.identity;

        if (!poseValid[LeftHipLandmark] || !poseValid[RightHipLandmark] ||
            !poseValid[LeftShoulderLandmark] || !poseValid[RightShoulderLandmark])
        {
            return false;
        }

        Vector3 hipRight = pose[RightHipLandmark] - pose[LeftHipLandmark];
        Vector3 shoulderRight =
            pose[RightShoulderLandmark] - pose[LeftShoulderLandmark];
        Vector3 torsoUp =
            (pose[LeftShoulderLandmark] + pose[RightShoulderLandmark]) * 0.5f -
            (pose[LeftHipLandmark] + pose[RightHipLandmark]) * 0.5f;

        if (!TryBuildBodyRotation(hipRight, torsoUp, out Quaternion pelvisRotation) ||
            !TryBuildBodyRotation(shoulderRight, torsoUp, out Quaternion chestRotation))
        {
            return false;
        }

        pelvisDelta = pelvisRotation * Quaternion.Inverse(restPelvisRotation);
        chestDelta = chestRotation * Quaternion.Inverse(restChestRotation);

        return true;
    }

    // Builds the world rotation of a body frame whose X axis is "right" and Y
    // axis is "up". The frame's Z axis is their cross product, i.e. forward.
    private static bool TryBuildBodyRotation(
        Vector3 right,
        Vector3 up,
        out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        right.Normalize();
        up.Normalize();

        Vector3 forward = Vector3.Cross(right, up);

        if (forward.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return false;
        }

        rotation = Quaternion.LookRotation(forward, up);

        return true;
    }

    private static void ApplyWorldRotation(
        Transform bone,
        Quaternion targetWorldRotation,
        float blend)
    {
        Quaternion targetLocalRotation =
            Quaternion.Inverse(bone.parent.rotation) * targetWorldRotation;

        bone.localRotation = Quaternion.Slerp(
            bone.localRotation,
            targetLocalRotation,
            blend
        );
    }

    private void FollowHips(float blend)
    {
        Vector3 trackedHips = (pose[LeftHipLandmark] + pose[RightHipLandmark]) * 0.5f;

        if (trackedHips.z > 0f && trackedHips.z < minimumDistance)
        {
            trackedHips *= minimumDistance / trackedHips.z;
        }

        Vector3 targetPosition =
            trackingCamera.TransformPoint(trackedHips * positionScale);
        Vector3 error = targetPosition - hips.position;

        if (!hasPlacedRoot)
        {
            targetAnimator.transform.position += error;
            hasPlacedRoot = true;
            return;
        }

        targetAnimator.transform.position += error * blend;
    }
}

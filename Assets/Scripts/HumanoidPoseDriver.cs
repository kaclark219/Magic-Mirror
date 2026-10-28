using System.Collections.Generic;
using UnityEngine;

// Drives a Humanoid avatar from the metric camera-space landmarks published by
// MediaPipeBodyTracker. The hips and chest are rotated to match the tracked
// body frames, so each limb bone can then be aimed straight at its tracked
// joint direction in world space. The avatar's rest pose is captured at Start
// and the tracked pose is applied as a delta from it, so no animation
// controller or manual rest-pose setup is required.
//
// The avatar is fitted to the person by their shoulders, because the chest and
// shoulders are what an overlay has to sit on. Its own rest shoulder width is
// measured at Start, then the avatar is scaled until that width matches the
// tracked shoulder width and moved until its shoulder joints sit on the tracked
// shoulders. Both widths are in meters between the same joints, and the overlay
// camera projects with the Kinect color intrinsics, so the same shoulder width
// lands on the same pixels whether the person is close or far away. Everything
// below the chest then follows from the avatar's own proportions.
public class HumanoidPoseDriver : MonoBehaviour
{
    public bool HasValidPose { get; private set; }

    // True once the avatar has been scaled to a measured body.
    public bool IsFitted { get; private set; }

    private const int LandmarkCount = 33;
    private const float MinimumDirectionSqrMagnitude = 0.0001f;

    // Landmark depths further apart than this cannot belong to one torso.
    private const float MaximumDepthSpread = 0.5f;

    private const float FitSmoothingSeconds = 0.2f;
    private const float FitResetSeconds = 2f;

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
    [Tooltip("Move the avatar so its shoulders sit on the tracked shoulders.")]
    [SerializeField]
    private bool followChestPosition = true;

    [Tooltip(
        "Scales tracked movement before it is applied to the scene. " +
        "1 is real-world meters."
    )]
    [SerializeField]
    [Min(0f)]
    private float positionScale = 1f;

    [Tooltip(
        "Closest the avatar's chest is allowed to get to the camera. Scaling the " +
        "tracked position along its view ray keeps the avatar on the same part " +
        "of the screen while keeping its mesh in front of the near plane."
    )]
    [SerializeField]
    [Min(0.1f)]
    private float minimumDistance = 0.6f;

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
    private Transform leftShoulder;
    private Transform rightShoulder;

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

    private Vector3 authoredScale;
    private float restShoulderWidth;

    private float appliedScale = 1f;
    private float lastBodyTime = float.NegativeInfinity;

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

        if (!CacheFitReferences())
        {
            enabled = false;
            return;
        }

        if (followChestPosition)
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

        leftShoulder = leftArm;
        rightShoulder = rightArm;

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

        UpdateFit();

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

        if (followChestPosition && trackingCamera != null)
        {
            FollowChest(blend);
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

    private void FollowChest(float blend)
    {
        Vector3 trackedChest =
            (pose[LeftShoulderLandmark] + pose[RightShoulderLandmark]) * 0.5f;

        if (trackedChest.z > 0f && trackedChest.z < minimumDistance)
        {
            trackedChest *= minimumDistance / trackedChest.z;
        }

        Vector3 targetPosition =
            trackingCamera.TransformPoint(trackedChest * positionScale);
        Vector3 error =
            targetPosition - (leftShoulder.position + rightShoulder.position) * 0.5f;

        if (!hasPlacedRoot)
        {
            targetAnimator.transform.position += error;
            hasPlacedRoot = true;
            return;
        }

        targetAnimator.transform.position += error * blend;
    }

    // Records the avatar's own shoulder width, which the tracked shoulders are
    // matched against. Measuring the avatar instead of assuming a size keeps the
    // fit correct for any rig and any import scale.
    private bool CacheFitReferences()
    {
        authoredScale = targetAnimator.transform.localScale;
        restShoulderWidth = Vector3.Distance(
            leftShoulder.position,
            rightShoulder.position
        );

        if (restShoulderWidth <= 0f)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the avatar has no measurable shoulder width.",
                this
            );

            return false;
        }

        return true;
    }

    // Fits the avatar to the measured body every frame. The first body after a
    // gap is fitted immediately, and later measurements are only lightly
    // smoothed, because a slow fit would lag behind someone walking towards or
    // away from the mirror.
    private void UpdateFit()
    {
        if (bodyTracker.BodyDetected)
        {
            lastBodyTime = Time.unscaledTime;
        }
        else if (IsFitted && Time.unscaledTime - lastBodyTime > FitResetSeconds)
        {
            // Someone else may be standing in front of the mirror now.
            IsFitted = false;
        }

        if (TryMeasureShoulderWidth(out float shoulderWidth))
        {
            float targetScale = shoulderWidth / restShoulderWidth;

            if (IsFitted)
            {
                float blend = 1f - Mathf.Exp(-Time.deltaTime / FitSmoothingSeconds);

                appliedScale = Mathf.Lerp(appliedScale, targetScale, blend);
            }
            else
            {
                appliedScale = targetScale;
                IsFitted = true;

                Debug.Log(
                    $"HumanoidPoseDriver: sized the avatar to a " +
                    $"{shoulderWidth:F2} m shoulder width.",
                    this
                );
            }
        }

        targetAnimator.transform.localScale = authoredScale * appliedScale;
    }

    private bool TryMeasureShoulderWidth(out float width)
    {
        width = 0f;

        IReadOnlyList<Vector3> cameraSpace = bodyTracker.CameraSpaceLandmarks;
        IReadOnlyList<Vector3> normalized = bodyTracker.Landmarks;

        if (cameraSpace == null || normalized == null ||
            cameraSpace.Count < LandmarkCount || normalized.Count < LandmarkCount ||
            !IsInFrame(normalized[LeftShoulderLandmark]) ||
            !IsInFrame(normalized[RightShoulderLandmark]))
        {
            return false;
        }

        Vector3 left = cameraSpace[LeftShoulderLandmark];
        Vector3 right = cameraSpace[RightShoulderLandmark];

        // A shoulder depth that landed on the background would fake a much
        // longer or shorter width.
        if (left.z <= 0f || right.z <= 0f ||
            Mathf.Abs(left.z - right.z) > MaximumDepthSpread)
        {
            return false;
        }

        width = Vector3.Distance(left, right);

        return true;
    }

    private static bool IsInFrame(Vector3 normalized)
    {
        return normalized.x >= 0f && normalized.x <= 1f &&
            normalized.y >= 0f && normalized.y <= 1f;
    }
}

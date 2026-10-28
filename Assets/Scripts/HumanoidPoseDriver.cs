using System.Collections.Generic;
using UnityEngine;

// Drives a Humanoid avatar from the metric camera-space landmarks published by
// MediaPipeBodyTracker. The hips and chest are rotated to match the tracked
// body frames, so each limb bone can then be aimed straight at its tracked
// joint direction in world space. The avatar's rest pose is captured at Start
// and the tracked pose is applied as a delta from it, so no animation
// controller or manual rest-pose setup is required.
//
// The avatar is also scaled to the person in front of the mirror. Its own rest
// shoulder-to-ankle span is measured at Start, then the avatar is scaled so the
// same span on the avatar matches the tracked one. Both spans are in meters
// between the same joints, so no body proportions or preset avatar height are
// involved. Because the overlay camera projects with the Kinect color
// intrinsics, a correctly sized avatar standing at the tracked hip position
// covers the person whether they are close to the mirror or far away.
public class HumanoidPoseDriver : MonoBehaviour
{
    public bool HasValidPose { get; private set; }

    // True once the avatar has been scaled to a measured body.
    public bool IsFitted { get; private set; }

    private const int LandmarkCount = 33;
    private const float MinimumDirectionSqrMagnitude = 0.0001f;

    // Bounds on the height the fitted avatar is allowed to render at, so a
    // depth sample that landed on the background cannot produce an absurd one.
    private const float MinimumHeight = 1.2f;
    private const float MaximumHeight = 2.2f;

    // Landmark depths further apart than this cannot belong to one standing body.
    private const float MaximumDepthSpread = 0.5f;

    private const float FitSmoothingSeconds = 1f;
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
        "screen while keeping its mesh in front of the near plane."
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
    private float authoredHeight;
    private float shoulderToAnkleSpan;
    private float shoulderToHipSpan;

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

    // Records the avatar's own rest dimensions, which the tracked body is
    // matched against. Measuring the avatar instead of assuming a height keeps
    // the fit correct for any rig and any import scale.
    private bool CacheFitReferences()
    {
        Transform leftArm = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightArm = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform leftLeg = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rightLeg = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        Transform leftFoot = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);

        authoredScale = targetAnimator.transform.localScale;
        authoredHeight = GetAvatarHeight();
        shoulderToHipSpan = GetSpan(leftArm, rightArm, leftLeg, rightLeg);
        shoulderToAnkleSpan = GetSpan(leftArm, rightArm, leftFoot, rightFoot);

        if (authoredHeight <= 0f || shoulderToHipSpan <= 0f || shoulderToAnkleSpan <= 0f)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the avatar has no measurable height or body span.",
                this
            );

            return false;
        }

        return true;
    }

    private static float GetSpan(
        Transform leftTop,
        Transform rightTop,
        Transform leftBottom,
        Transform rightBottom)
    {
        if (leftBottom == null || rightBottom == null)
        {
            return 0f;
        }

        Vector3 top = (leftTop.position + rightTop.position) * 0.5f;
        Vector3 bottom = (leftBottom.position + rightBottom.position) * 0.5f;

        return Vector3.Distance(top, bottom);
    }

    private float GetAvatarHeight()
    {
        Renderer[] renderers = targetAnimator.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            return 0f;
        }

        Bounds bounds = renderers[0].bounds;

        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds.size.y;
    }

    // Fits the avatar to the measured body every frame. The first body after a
    // gap is fitted immediately, and later measurements are smoothed so tracker
    // jitter does not visibly resize the avatar.
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

        if (TryMeasureBody(out float span, out bool usesAnkles))
        {
            float referenceSpan = usesAnkles ? shoulderToAnkleSpan : shoulderToHipSpan;

            float targetScale = Mathf.Clamp(
                span / referenceSpan,
                MinimumHeight / authoredHeight,
                MaximumHeight / authoredHeight
            );

            if (IsFitted)
            {
                float blend = 1f - Mathf.Exp(-Time.deltaTime / FitSmoothingSeconds);

                appliedScale = Mathf.Lerp(appliedScale, targetScale, blend);
            }
            else
            {
                appliedScale = targetScale;
                IsFitted = true;

                float fittedHeight = authoredHeight * appliedScale;

                Debug.Log(
                    $"HumanoidPoseDriver: sized the avatar to {fittedHeight:F2} m from " +
                    $"a {(usesAnkles ? "shoulder-to-ankle" : "shoulder-to-hip")} span " +
                    $"of {span:F2} m.",
                    this
                );
            }
        }

        targetAnimator.transform.localScale = authoredScale * appliedScale;
    }

    // Measures the tracked body against the same joints the avatar was measured
    // with. The ankles are preferred because that span covers most of the body;
    // when they are out of frame the shoulder-to-hip span stands in for it.
    private bool TryMeasureBody(out float span, out bool usesAnkles)
    {
        span = 0f;
        usesAnkles = false;

        IReadOnlyList<Vector3> cameraSpace = bodyTracker.CameraSpaceLandmarks;
        IReadOnlyList<Vector3> normalized = bodyTracker.Landmarks;

        if (cameraSpace == null || normalized == null ||
            cameraSpace.Count < LandmarkCount || normalized.Count < LandmarkCount)
        {
            return false;
        }

        if (!IsInFrame(normalized[LeftShoulderLandmark]) ||
            !IsInFrame(normalized[RightShoulderLandmark]) ||
            !IsInFrame(normalized[LeftHipLandmark]) ||
            !IsInFrame(normalized[RightHipLandmark]))
        {
            return false;
        }

        if (!TryGetJointMid(cameraSpace, LeftShoulderLandmark, RightShoulderLandmark,
                out Vector3 shoulderMid) ||
            !TryGetJointMid(cameraSpace, LeftHipLandmark, RightHipLandmark,
                out Vector3 hipMid))
        {
            return false;
        }

        Vector3 lowerMid = hipMid;

        usesAnkles =
            IsInFrame(normalized[LeftAnkleLandmark]) &&
            IsInFrame(normalized[RightAnkleLandmark]) &&
            TryGetJointMid(cameraSpace, LeftAnkleLandmark, RightAnkleLandmark,
                out lowerMid);

        // A single landmark depth that landed on the background would fake a
        // much longer or shorter body.
        float minDepth = Mathf.Min(shoulderMid.z, Mathf.Min(hipMid.z, lowerMid.z));
        float maxDepth = Mathf.Max(shoulderMid.z, Mathf.Max(hipMid.z, lowerMid.z));

        if (maxDepth - minDepth > MaximumDepthSpread)
        {
            return false;
        }

        span = Vector3.Distance(shoulderMid, lowerMid);

        return true;
    }

    private static bool TryGetJointMid(
        IReadOnlyList<Vector3> landmarks,
        int left,
        int right,
        out Vector3 mid)
    {
        mid = (landmarks[left] + landmarks[right]) * 0.5f;

        return landmarks[left].z > 0f && landmarks[right].z > 0f;
    }

    private static bool IsInFrame(Vector3 normalized)
    {
        return normalized.x >= 0f && normalized.x <= 1f &&
            normalized.y >= 0f && normalized.y <= 1f;
    }
}

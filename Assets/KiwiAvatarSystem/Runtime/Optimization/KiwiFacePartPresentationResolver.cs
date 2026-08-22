using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// v5.1 Phase 4 presentation arbitration.
///
/// CanvasRenderer alpha is a presentation output and therefore has one final
/// runtime writer. Other systems submit reason-coded constraints instead of
/// mutating the renderer directly.
///
/// Arbitration:
/// 1) Hard hide (mask not ready / unsafe presentation state) always wins.
/// 2) Otherwise the coordinator's filtered quality/geometry visibility is the
///    normal target.
/// 3) Side-view continuity may raise only the geometrically near eye.
/// 4) Front recovery may raise a target only while its own existing near-frontal
///    tracking gate is active, so it cannot reopen a normal side-view far eye.
///
/// FacePartShapeMask remains the semantic/blink material-opacity owner. This
/// resolver never changes _MaskVisibility and never writes avatar/root pose.
/// </summary>
[DefaultExecutionOrder(1390)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartPresentationResolver : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Face-Part Presentation Resolver";

    public const string ResolverVersion =
        "5.1.0-phase4";

    public enum Part
    {
        LeftEye = 0,
        RightEye = 1,
        Mouth = 2
    }

    [Flags]
    public enum Reason
    {
        None = 0,
        QualityGuard = 1 << 0,
        MaskNotReady = 1 << 1,
        FrontRecovery = 1 << 2,
        SideViewNearEyeContinuity = 1 << 3,
        AnatomyConstraint = 1 << 4
    }

    private struct PartRequest
    {
        public bool hasQualityCap;
        public float qualityCap;

        public bool hardHide;

        public bool hasRecoveryFloor;
        public float recoveryFloor;

        public bool hasNearEyeFloor;
        public float nearEyeFloor;

        public Reason reasons;
    }

    [Header("Diagnostics")]
    [SerializeField] private float debugLeftEyeAlpha = 1f;
    [SerializeField] private float debugRightEyeAlpha = 1f;
    [SerializeField] private float debugMouthAlpha = 1f;
    [SerializeField] private string debugLeftEyeReasons = "None";
    [SerializeField] private string debugRightEyeReasons = "None";
    [SerializeField] private string debugMouthReasons = "None";
    [SerializeField] private int debugResolvedFrame = -1;

    private static KiwiFacePartPresentationResolver _instance;

    private readonly PartRequest[] _requests =
        new PartRequest[3];

    private readonly float[] _resolvedAlpha =
        { 1f, 1f, 1f };

    private FacePartCropper _cropper;
    private int _requestFrame = -1;

    public float LeftEyeAlpha =>
        _resolvedAlpha[(int)Part.LeftEye];

    public float RightEyeAlpha =>
        _resolvedAlpha[(int)Part.RightEye];

    public float MouthAlpha =>
        _resolvedAlpha[(int)Part.Mouth];

    public string LeftEyeReasons =>
        debugLeftEyeReasons;

    public string RightEyeReasons =>
        debugRightEyeReasons;

    public string MouthReasons =>
        debugMouthReasons;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        EnsureInstance();
    }

    private void Awake()
    {
        if (
            _instance != null &&
            _instance != this
        )
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _cropper = null;
        _requestFrame = -1;
        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);
        BeginRequestFrame();

        ResolveAndApply(
            Part.LeftEye,
            _cropper != null
                ? _cropper.leftEyeImage
                : null);

        ResolveAndApply(
            Part.RightEye,
            _cropper != null
                ? _cropper.rightEyeImage
                : null);

        ResolveAndApply(
            Part.Mouth,
            _cropper != null
                ? _cropper.mouthImage
                : null);

        debugLeftEyeAlpha =
            _resolvedAlpha[(int)Part.LeftEye];
        debugRightEyeAlpha =
            _resolvedAlpha[(int)Part.RightEye];
        debugMouthAlpha =
            _resolvedAlpha[(int)Part.Mouth];

        debugLeftEyeReasons =
            FormatReasons(
                _requests[(int)Part.LeftEye].reasons);
        debugRightEyeReasons =
            FormatReasons(
                _requests[(int)Part.RightEye].reasons);
        debugMouthReasons =
            FormatReasons(
                _requests[(int)Part.Mouth].reasons);

        debugResolvedFrame =
            Time.frameCount;
    }

    public static void SubmitQualityCap(
        RawImage image,
        float alpha)
    {
        KiwiFacePartPresentationResolver resolver =
            EnsureInstance();

        if (
            resolver == null ||
            !resolver.TryResolvePart(
                image,
                out Part part)
        )
        {
            return;
        }

        resolver.BeginRequestFrame();

        int index =
            (int)part;

        PartRequest request =
            resolver._requests[index];

        alpha =
            Mathf.Clamp01(alpha);

        request.qualityCap =
            request.hasQualityCap
                ? Mathf.Min(
                    request.qualityCap,
                    alpha)
                : alpha;

        request.hasQualityCap =
            true;

        if (alpha < 0.9995f)
        {
            request.reasons |=
                Reason.QualityGuard;
        }

        resolver._requests[index] =
            request;
    }

    public static void SubmitHardHide(
        RawImage image,
        Reason reason)
    {
        KiwiFacePartPresentationResolver resolver =
            EnsureInstance();

        if (
            resolver == null ||
            !resolver.TryResolvePart(
                image,
                out Part part)
        )
        {
            return;
        }

        resolver.BeginRequestFrame();

        int index =
            (int)part;

        PartRequest request =
            resolver._requests[index];

        request.hardHide =
            true;
        request.reasons |=
            reason;

        resolver._requests[index] =
            request;
    }

    public static void SubmitRecoveryFloor(
        RawImage image,
        float alpha)
    {
        SubmitFloor(
            image,
            alpha,
            false,
            Reason.FrontRecovery);
    }

    public static void SubmitNearEyeFloor(
        RawImage image,
        float alpha)
    {
        SubmitFloor(
            image,
            alpha,
            true,
            Reason.SideViewNearEyeContinuity);
    }

    public static void SubmitAdvisoryReason(
        RawImage image,
        Reason reason)
    {
        KiwiFacePartPresentationResolver resolver =
            EnsureInstance();

        if (
            resolver == null ||
            !resolver.TryResolvePart(
                image,
                out Part part)
        )
        {
            return;
        }

        resolver.BeginRequestFrame();

        int index =
            (int)part;

        PartRequest request =
            resolver._requests[index];

        request.reasons |=
            reason;

        resolver._requests[index] =
            request;
    }

    public static bool TryGetResolvedState(
        RawImage image,
        out float alpha,
        out Reason reasons)
    {
        alpha = 1f;
        reasons = Reason.None;

        KiwiFacePartPresentationResolver resolver =
            _instance;

        if (
            resolver == null ||
            !resolver.TryResolvePart(
                image,
                out Part part)
        )
        {
            return false;
        }

        int index =
            (int)part;

        alpha =
            resolver._resolvedAlpha[index];
        reasons =
            resolver._requests[index].reasons;

        return true;
    }

    private static void SubmitFloor(
        RawImage image,
        float alpha,
        bool nearEye,
        Reason reason)
    {
        KiwiFacePartPresentationResolver resolver =
            EnsureInstance();

        if (
            resolver == null ||
            !resolver.TryResolvePart(
                image,
                out Part part)
        )
        {
            return;
        }

        resolver.BeginRequestFrame();

        int index =
            (int)part;

        PartRequest request =
            resolver._requests[index];

        alpha =
            Mathf.Clamp01(alpha);

        if (nearEye)
        {
            request.nearEyeFloor =
                request.hasNearEyeFloor
                    ? Mathf.Max(
                        request.nearEyeFloor,
                        alpha)
                    : alpha;

            request.hasNearEyeFloor =
                true;
        }
        else
        {
            request.recoveryFloor =
                request.hasRecoveryFloor
                    ? Mathf.Max(
                        request.recoveryFloor,
                        alpha)
                    : alpha;

            request.hasRecoveryFloor =
                true;
        }

        request.reasons |=
            reason;

        resolver._requests[index] =
            request;
    }

    private void ResolveAndApply(
        Part part,
        RawImage image)
    {
        int index =
            (int)part;

        PartRequest request =
            _requests[index];

        float qualityCap =
            request.hasQualityCap
                ? Mathf.Clamp01(
                    request.qualityCap)
                : 1f;

        float resolved =
            qualityCap;

        if (request.hardHide)
        {
            resolved = 0f;
        }
        else
        {
            // Near-eye continuity is an intentional exception to the regular
            // far/side quality cap, but cannot override a hard safety hide.
            if (request.hasNearEyeFloor)
            {
                resolved =
                    Mathf.Max(
                        resolved,
                        Mathf.Clamp01(
                            request.nearEyeFloor));
            }

            // Front recovery is submitted only by the existing near-frontal
            // tracking gate. It can therefore repair a stuck low renderer
            // without reopening the normal side-view far eye.
            if (request.hasRecoveryFloor)
            {
                resolved =
                    Mathf.Max(
                        resolved,
                        Mathf.Clamp01(
                            request.recoveryFloor));
            }
        }

        resolved =
            Mathf.Clamp01(resolved);

        _resolvedAlpha[index] =
            resolved;

        if (image == null)
        {
            return;
        }

        float current =
            image.canvasRenderer.GetAlpha();

        if (
            Mathf.Abs(
                current -
                resolved) >
            0.0001f
        )
        {
            image.canvasRenderer.SetAlpha(
                resolved);
        }
    }

    private bool TryResolvePart(
        RawImage image,
        out Part part)
    {
        part =
            Part.Mouth;

        if (image == null)
        {
            return false;
        }

        RefreshReferences(false);

        if (_cropper == null)
        {
            return false;
        }

        if (image == _cropper.leftEyeImage)
        {
            part = Part.LeftEye;
            return true;
        }

        if (image == _cropper.rightEyeImage)
        {
            part = Part.RightEye;
            return true;
        }

        if (image == _cropper.mouthImage)
        {
            part = Part.Mouth;
            return true;
        }

        return false;
    }

    private void BeginRequestFrame()
    {
        int frame =
            Time.frameCount;

        if (_requestFrame == frame)
        {
            return;
        }

        _requestFrame =
            frame;

        for (int i = 0; i < _requests.Length; i++)
        {
            _requests[i] = default;
        }
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _cropper == null
        )
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }
    }

    private static KiwiFacePartPresentationResolver EnsureInstance()
    {
        if (_instance != null)
        {
            return _instance;
        }

        _instance =
            FindFirstObjectByType<
                KiwiFacePartPresentationResolver>(
                FindObjectsInactive.Include);

        if (_instance != null)
        {
            return _instance;
        }

        if (!Application.isPlaying)
        {
            return null;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(host);

        _instance =
            host.AddComponent<
                KiwiFacePartPresentationResolver>();

        return _instance;
    }

    private static string FormatReasons(
        Reason reasons)
    {
        return
            reasons == Reason.None
                ? "None"
                : reasons.ToString();
    }
}

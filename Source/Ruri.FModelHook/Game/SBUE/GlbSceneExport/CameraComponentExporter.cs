using System;
using System.Globalization;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using SharpGLTF.Scenes;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Translates a placed UCameraComponent (or its UCineCameraComponent subclass)
// into a SharpGLTF CameraBuilder + node transform and pushes both into the
// shared scene graph through GlbSceneContext.AddCamera.
//
// ---------------------------------------------------------------------------
// Ground-truth property + math sources (verified ASCII files on disk):
//   * UCameraComponent class declaration (FieldOfView / OrthoWidth /
//     OrthoNearClipPlane / OrthoFarClipPlane / AspectRatio /
//     bConstrainAspectRatio / ProjectionMode):
//     E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/Engine/Classes/
//       Camera/CameraComponent.h:44, 66, 84, 90, 108, 120, 226.
//   * UCineCameraComponent fields (Filmback / LensSettings / FocusSettings /
//     CropSettings / CurrentFocalLength / CurrentAperture / ExposureMethod /
//     bOverride_CustomNearClippingPlane / CustomNearClippingPlane):
//     E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/CinematicCamera/
//       Public/CineCameraComponent.h:38, 45, 52, 59, 66, 70, 81, 84, 88.
//   * UCineCameraComponent::GetHorizontalFieldOfViewInternal — the canonical
//     focal-length -> horizontal FOV formula (with anamorphic Squeeze, plate
//     crop, and uniform / asymmetric Overscan):
//     E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/CinematicCamera/
//       Private/CineCameraComponent.cpp:286-305.
//   * UCineCameraComponent::GetVerticalFieldOfViewInternal — vertical FOV
//     formula (used directly for the glTF VerticalFOV value):
//     E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/CinematicCamera/
//       Private/CineCameraComponent.cpp:312-331.
//   * Filmback / Lens default values (SensorWidth=24.89mm, SensorHeight=18.67mm,
//     SqueezeFactor=1, defaults match the cooked-asset native init order):
//     E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/CinematicCamera/
//       Public/CineCameraSettings.h:57-64, 89-130.
//   * UE class identity in CUE4Parse (empty marker types — every property is
//     read via GetOrDefault on the IPropertyHolder):
//     D:/Ruri/Git/FractalTools/Ruri-RipperHook/FModel/CUE4Parse/CUE4Parse/UE4/
//       Assets/Exports/Component/ActorComponent.cs:135, 150.
//   * SharpGLTF camera surface (verified by reflection against the bound
//     AssetRipper.SharpGLTF.Toolkit 1.0.2 net8.0 DLL):
//       CameraBuilder.Perspective(float? aspectRatio, float fovy /* radians */,
//                                 float znear, float zfar = +Inf)
//       CameraBuilder.Orthographic(float xmag, float ymag, float znear,
//                                  float zfar)
//       SceneBuilder.AddCamera(CameraBuilder camera,
//                              AffineTransform cameraTransform)  // matrix overload.
//     CameraBuilder.Perspective.VerticalFOV is documented as RADIANS in the
//     toolkit XML doc (SharpGLTF.Toolkit.xml line 1903).
//
// ---------------------------------------------------------------------------
// Coordinate system mapping (camera-axis remap):
//   * Unreal camera local axes: forward = +X, right = +Y, up = +Z.
//   * glTF   camera local axes: forward = -Z, right = +X, up = +Y.
//   * FModel's Transform.Matrix (Transform.cs:20-23) bakes the (X,Y,Z) ->
//     (X,Z,Y) swizzle into both rotation and position so the placement
//     is already expressed in glTF-world space. After that swizzle the
//     Unreal-camera local axes land in the placement-local frame as:
//         UE forward (+X) -> placement-local +X (X stays in slot X)
//         UE right   (+Y) -> placement-local +Z (Y moves into slot Z)
//         UE up      (+Z) -> placement-local +Y (Z moves into slot Y)
//     To turn that placement into a glTF-conformant camera node frame
//     we need a rotation R that maps each glTF camera-local axis to the
//     placement-local axis carrying the same semantic meaning:
//         glTF camera-local -Z  (forward) ->  placement +X
//         glTF camera-local +X  (right)   ->  placement +Z
//         glTF camera-local +Y  (up)      ->  placement +Y
//
//     System.Numerics uses ROW-VECTOR convention (out = v * M; row k of
//     M is the image of the k-th basis vector). Matrix4x4.CreateRotationY
//     packs (M11=cos, M13=-sin, M31=sin, M33=cos), so for any
//     v = (a, b, c, d): v * M = (a*cos + c*sin, b, -a*sin + c*cos, d).
//
//     At angle = +pi/2 (cos=0, sin=1):
//         (+X) -> (0, 0, -1) = -Z
//         (+Z) -> (1, 0, 0)  = +X     => (-Z) -> -X   WRONG
//     At angle = -pi/2 (cos=0, sin=-1):
//         (+X) -> (0, 0, 1)  = +Z     CORRECT
//         (+Z) -> (-1, 0, 0) = -X     => (-Z) -> +X   CORRECT
//         (+Y) -> (0, 1, 0)  = +Y     CORRECT
//
//     So the correct remap is Ry(-pi/2), NOT Ry(+pi/2). The original
//     comment claimed Ry(+90) sent (-Z) to (+X), which is the column-vec
//     answer; System.Numerics is row-vec and the sign flips.
//
//   * In System.Numerics row-vector convention a local-frame correction
//     composes on the LEFT (Matrix4x4.Multiply(A, B) = A * B applied as
//     v * A * B, so A acts in the mesh/camera-local frame before W). The mesh
//     path needs no such correction (SceneTransform.NodeMatrix = W, because the
//     exported mesh already lives in FModel's glTF-local space); the camera,
//     however, must rotate its Unreal-local axes into the glTF camera frame, so
//     the camera node matrix is `cameraAxisRemap * placement.WorldTransform.Matrix`.
//
// ---------------------------------------------------------------------------
// Lossless preservation:
//   * Every property read here is also captured byte-for-byte by the lossless
//     layer (CompleteSceneDataExporter) for the same actor / component. So
//     the JSON sidecar is the ground-truth round-trip; this exporter only
//     writes the glTF-renderable summary plus extras that downstream bridges
//     (Blender, Houdini, Unity / Unreal re-imports) consume.
//
// ---------------------------------------------------------------------------
// Manifest:
//   * Each successfully translated camera appends one [GlbScene][Camera] note
//     to manifest.Notes so the audit trail can confirm camera count + key
//     parameters without parsing the GLB JSON chunk.
//   * Failures (mesh-style ProcessActor exceptions cannot happen here because
//     we touch no asset packages, but defensive guards still report dropped
//     state via manifest.RecordDroppedComponent so `dropped == 0` stays
//     auditable).
public sealed class CameraComponentExporter : IComponentExporter
{
    // Native default values lifted from the canonical UE FCameraFilmbackSettings
    // / FCameraLensSettings constructors (CineCameraSettings.h:57-64, 89-130).
    // Cooked-asset native init follows the engine constructor before any tagged
    // property override is applied, so when an actor's serialized data omits
    // (for example) Filmback.SensorWidth, the engine instantiates that field at
    // 24.89 mm. Mirroring those constants here keeps the FOV math identical
    // even on placements whose tagged properties only carry the diff from
    // default.
    private const float NativeDefaultSensorWidthMillimeters = 24.89f;
    private const float NativeDefaultSensorHeightMillimeters = 18.67f;
    private const float NativeDefaultSqueezeFactor = 1.0f;
    private const float NativeDefaultCurrentFocalLengthMillimeters = 35.0f;
    private const float NativeDefaultMinFocalLengthMillimeters = 50.0f;
    private const float NativeDefaultMaxFocalLengthMillimeters = 50.0f;
    private const float NativeDefaultMinFStop = 2.0f;
    private const float NativeDefaultMaxFStop = 2.0f;
    private const float NativeDefaultMinimumFocusDistanceCentimeters = 15.0f;

    // UCameraComponent::FieldOfView native default in the C++ constructor is
    // 90 deg (CameraComponent.cpp:78). It is exposed in the header as a plain
    // float without an in-class initializer (CameraComponent.h:44), so the
    // safe default mirrors the engine constructor.
    private const float NativeDefaultCameraComponentFieldOfViewDegrees = 90.0f;

    // UCameraComponent constructor (CameraComponent.cpp:81) sets
    // AspectRatio = 1.777778f. Keep the literal at 7 decimal digits so the
    // float bit-pattern matches what the engine writes into cooked tagged
    // properties (this is what UE's CDO holds when AspectRatio is omitted
    // from the property bag).
    private const float NativeDefaultAspectRatio = 1.777778f;

    // UCameraComponent ortho-frustum defaults from CameraComponent.cpp:82, 87,
    // 88 -- referencing EngineDefines.h:37, 65-67:
    //   DEFAULT_ORTHOWIDTH     = 1536.0f
    //   DEFAULT_ORTHONEARPLANE = -DEFAULT_ORTHOWIDTH / 2.0f = -768.0f
    //   DEFAULT_ORTHOFARPLANE  = UE_OLD_WORLD_MAX = 2097152.0f
    // These live in UE world units (centimeters). The near default being
    // NEGATIVE is deliberate in the engine: UE's orthographic projection
    // accepts a signed near plane and only the editor 2D viewports rely on
    // the default. For glTF emission we still scale by cm -> m and clamp
    // to a positive floor at build time (glTF 2.0 spec requires znear > 0).
    private const float NativeDefaultOrthoWidthCentimeters = 1536.0f;
    private const float NativeDefaultOrthoNearClipPlaneCentimeters =
        -NativeDefaultOrthoWidthCentimeters / 2.0f;
    private const float NativeDefaultOrthoFarClipPlaneCentimeters = 2_097_152.0f;

    // glTF "infinite" far plane sentinel for Perspective: SharpGLTF defaults
    // ZFar to float.PositiveInfinity. We mirror that when the placement does
    // not constrain it (UCameraComponent has no near/far plane for perspective
    // mode, so a sensible glTF default is required).
    private const float DefaultPerspectiveNearPlaneMeters = 0.1f;
    private const float DefaultPerspectiveFarPlaneMeters = float.PositiveInfinity;

    // FModel preview converts 1 Unreal centimeter to 1 viewer meter via the
    // ScaleDownRatio = 0.01f baked into SceneTransform (SceneTransform.cs:37).
    // Orthographic XMag / YMag and clip-plane values must be expressed in the
    // same world unit as the placement matrix (meters in glTF-space), so any
    // cm-denominated UE field is scaled by this factor on emission.
    private const float UnrealCentimeterToGltfMeter = 0.01f;

    // Pre-baked -90 deg rotation about Y. Applied on the LEFT (row-vector
    // convention) to the placement world matrix so the camera node's local
    // -Z axis aligns with the Unreal camera forward (UE +X mapped through
    // FModel's YZ swizzle into glTF +X). The sign is NEGATIVE because
    // System.Numerics CreateRotationY at +pi/2 maps row-vec (-Z) to -X,
    // not +X (see the multi-line derivation in the file header). Static
    // readonly so each camera placement reuses one Matrix4x4 instead of
    // recomputing it.
    private static readonly Matrix4x4 CameraAxisRemapGltfFromUnreal =
        Matrix4x4.CreateRotationY(-MathF.PI / 2.0f);

    public bool CanExport(UObject component)
    {
        // UCameraComponent in CUE4Parse is an empty marker type
        // (ActorComponent.cs:135) and UCineCameraComponent : UCameraComponent
        // (ActorComponent.cs:150), so a single `is UCameraComponent` check
        // claims both families plus any engine subclass that derives from
        // them (e.g. LevelSequence-attached cine cameras still surface as
        // UCineCameraComponent here).
        return component is UCameraComponent;
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        UObject component = placed.Component;

        // Treat any unhandled translation failure as a dropped component so
        // the manifest audit trail stays accurate. Property reads themselves
        // do not throw on missing keys (GetOrDefault returns the default), so
        // the catch here only triggers on genuinely corrupt struct properties
        // or invalid math (NaN focal length etc.).
        try
        {
            EProjectionMode projectionMode = ResolveProjectionMode(component);

            CameraBuilder cameraBuilder = projectionMode == EProjectionMode.Orthographic
                ? (CameraBuilder)BuildOrthographicCamera(component)
                : (CameraBuilder)BuildPerspectiveCamera(component);

            Matrix4x4 cameraNodeMatrix = Matrix4x4.Multiply(
                CameraAxisRemapGltfFromUnreal,
                placed.WorldTransform.Matrix);

            context.AddCamera(cameraBuilder, cameraNodeMatrix, component.Name);

            RecordAuditNote(component, projectionMode, cameraBuilder, context);
        }
        catch (Exception exception)
        {
            string componentPath = component.GetPathName();
            context.LogError($"[GlbScene] CameraComponent '{componentPath}' translation failed: {exception.Message}");
            context.Manifest.RecordDroppedComponent($"{componentPath}: camera translation: {exception.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Perspective build
    // -----------------------------------------------------------------------
    //
    // Vertical FOV is the glTF projection parameter (radians). For a plain
    // UCameraComponent the only horizontal-FOV-style property is
    // `FieldOfView` (degrees, horizontal); we derive the vertical FOV from
    // it via the AspectRatio per the same math UE uses in
    // FMinimalViewInfo / SetupView (FOV is horizontal when the constraint is
    // MaintainXFOV, and vertical = 2 * atan(tan(hfov/2) / aspect)).
    //
    // For a UCineCameraComponent we ignore the inherited FieldOfView (which
    // gets recomputed by SetFieldOfView -> RecalcDerivedData on the engine
    // side) and use the canonical Filmback / LensSettings + Overscan +
    // CropSettings formula from CineCameraComponent.cpp:286-305 / 312-331.
    private static CameraBuilder.Perspective BuildPerspectiveCamera(UObject component)
    {
        // Aspect ratio: prefer the actor-authored AspectRatio when the
        // bConstrainAspectRatio flag is on, otherwise leave the glTF
        // AspectRatio property null so the consuming viewer picks one from
        // the framebuffer aspect. UCameraComponent.h:108-122.
        float? aspectRatio = ResolveOptionalAspectRatio(component);

        float verticalFieldOfViewRadians;
        float nearPlaneMeters;
        float farPlaneMeters;

        if (component is UCineCameraComponent cineCameraComponent)
        {
            verticalFieldOfViewRadians = ComputeCineCameraVerticalFieldOfViewRadians(cineCameraComponent);
            nearPlaneMeters = ResolveCineCameraNearPlaneMeters(cineCameraComponent);
            farPlaneMeters = DefaultPerspectiveFarPlaneMeters;
        }
        else
        {
            // Plain UCameraComponent: FieldOfView is a horizontal FOV in
            // degrees (CameraComponent.h:36-44). The glTF Perspective takes a
            // vertical FOV in radians, so convert (hfov, aspect) -> vfov.
            float horizontalFieldOfViewDegrees = component.GetOrDefault(
                "FieldOfView",
                NativeDefaultCameraComponentFieldOfViewDegrees);
            float horizontalFieldOfViewRadians = MathF.PI / 180.0f * horizontalFieldOfViewDegrees;

            // UE plain-camera path applies Overscan + AsymmetricOverscan to
            // the projection at SceneView build time via
            // FMinimalViewInfo::ApplyOverscan and ApplyAsymmetricOverscan
            // (CameraComponent.cpp:479-480 -> CameraStackTypes.cpp:500-582).
            // ApplyOverscan scales tan(half FOV) by (1 + Overscan); the
            // asymmetric pass scales tan(half FOV) by 0.5 *
            // (AsymmetricOverscanScalar.X + .Y) where Scalar = (Asym + 1).
            // Fold both into the horizontal FOV BEFORE the hfov -> vfov
            // conversion so the resulting glTF camera has the same view
            // frustum as the engine projection.
            CameraOverscanData plainCameraOverscan = ReadOverscanFields(component);
            float tangentHalfHorizontalFieldOfView = MathF.Tan(0.5f * horizontalFieldOfViewRadians);
            float uniformOverscanScalar = 1.0f + plainCameraOverscan.UniformOverscan;
            float asymmetricHorizontalScalar =
                0.5f * ((1.0f + plainCameraOverscan.AsymmetricOverscanX)
                      + (1.0f + plainCameraOverscan.AsymmetricOverscanY));
            tangentHalfHorizontalFieldOfView *= uniformOverscanScalar * asymmetricHorizontalScalar;
            horizontalFieldOfViewRadians = 2.0f * MathF.Atan(tangentHalfHorizontalFieldOfView);

            float aspectRatioForVerticalFromHorizontal = aspectRatio
                ?? component.GetOrDefault("AspectRatio", NativeDefaultAspectRatio);
            verticalFieldOfViewRadians = ConvertHorizontalToVerticalFieldOfViewRadians(
                horizontalFieldOfViewRadians,
                aspectRatioForVerticalFromHorizontal);
            nearPlaneMeters = DefaultPerspectiveNearPlaneMeters;
            farPlaneMeters = DefaultPerspectiveFarPlaneMeters;
        }

        // SharpGLTF's Perspective .ctor: (aspectRatio, fovy /* radians */,
        // znear, zfar). Validity check happens via SharpGLTF.IsValid; we
        // guard against trivially bogus VFOVs to avoid a downstream
        // ToGltf2() throw.
        if (!float.IsFinite(verticalFieldOfViewRadians) || verticalFieldOfViewRadians <= 0.0f)
        {
            verticalFieldOfViewRadians = MathF.PI / 180.0f * NativeDefaultCameraComponentFieldOfViewDegrees;
        }
        if (!float.IsFinite(nearPlaneMeters) || nearPlaneMeters <= 0.0f)
        {
            nearPlaneMeters = DefaultPerspectiveNearPlaneMeters;
        }

        return new CameraBuilder.Perspective(
            aspectRatio,
            verticalFieldOfViewRadians,
            nearPlaneMeters,
            farPlaneMeters);
    }

    // 1:1 of UCineCameraComponent::GetVerticalFieldOfViewInternal
    // (CineCameraComponent.cpp:312-331), with bIncludeOverscan = true mirroring
    // the public GetVerticalFieldOfView() default at line 309. Steps:
    //   1. CropedSensorHeight starts at Filmback.SensorHeight.
    //   2. If CropSettings.AspectRatio > 0, compute the DesqueezeAspectRatio =
    //      SensorWidth * SqueezeFactor / SensorHeight and, when the desqueeze
    //      ratio is shallower than the requested crop ratio, scale the
    //      sensor height down by their ratio. This matches the cooked engine
    //      behaviour even for placements without crop overrides (CropSettings
    //      defaults to AspectRatio = 0, in which case the whole branch is
    //      skipped).
    //   3. Apply the same Overscan + AsymmetricOverscan scalar UE uses:
    //      (1 + Overscan) * 0.5 * (Z + W + 2.0) where Z / W are the top / bottom
    //      asymmetric overscan components. UE asymmetric overscan defaults to
    //      FVector4f::Zero -> the scalar collapses to (1 + Overscan).
    //   4. Vertical FOV (radians) = 2 * atan(CropedSensorHeight * scalar /
    //      (2 * CurrentFocalLength)).
    // CurrentFocalLength == 0 (or missing) maps the source's early-return 0
    // path to the engine FieldOfView fallback so we still emit a usable
    // camera, then audit the deviation in the per-camera note.
    private static float ComputeCineCameraVerticalFieldOfViewRadians(UCineCameraComponent cineCameraComponent)
    {
        float currentFocalLengthMillimeters = cineCameraComponent.GetOrDefault(
            "CurrentFocalLength",
            NativeDefaultCurrentFocalLengthMillimeters);
        if (!float.IsFinite(currentFocalLengthMillimeters) || currentFocalLengthMillimeters <= 0.0f)
        {
            // Engine source returns 0 here; downstream code treats that as
            // "use the inherited FieldOfView". We do the same so the GLB stays
            // viewable.
            float fallbackHorizontalDegrees = cineCameraComponent.GetOrDefault(
                "FieldOfView",
                NativeDefaultCameraComponentFieldOfViewDegrees);
            float aspectRatioForFallback = cineCameraComponent.GetOrDefault(
                "AspectRatio",
                NativeDefaultAspectRatio);
            return ConvertHorizontalToVerticalFieldOfViewRadians(
                MathF.PI / 180.0f * fallbackHorizontalDegrees,
                aspectRatioForFallback);
        }

        CameraFilmbackData filmback = ReadFilmbackSettings(cineCameraComponent);
        CameraLensSettingsData lens = ReadLensSettings(cineCameraComponent);
        CameraCropSettingsData crop = ReadCropSettings(cineCameraComponent);
        CameraOverscanData overscan = ReadOverscanFields(cineCameraComponent);

        float cropedSensorHeightMillimeters = filmback.SensorHeightMillimeters;
        if (crop.CroppedAspectRatio > 0.0f)
        {
            float desqueezeAspectRatio = filmback.SensorWidthMillimeters * lens.SqueezeFactor
                                       / filmback.SensorHeightMillimeters;
            if (desqueezeAspectRatio < crop.CroppedAspectRatio)
            {
                cropedSensorHeightMillimeters *= desqueezeAspectRatio / crop.CroppedAspectRatio;
            }
        }

        float overscanScalar = (1.0f + overscan.UniformOverscan) * 0.5f
                             * (overscan.AsymmetricOverscanZ + overscan.AsymmetricOverscanW + 2.0f);
        float verticalFieldOfViewRadians = 2.0f * MathF.Atan(
            cropedSensorHeightMillimeters * overscanScalar
            / (2.0f * currentFocalLengthMillimeters));
        return verticalFieldOfViewRadians;
    }

    // Standard hfov -> vfov conversion: vfov = 2 * atan(tan(hfov/2) / aspect).
    // Used when a plain UCameraComponent supplies a horizontal FieldOfView and
    // no separate Filmback dimensions are available. UE's FMinimalViewInfo uses
    // the same identity when the aspect-ratio axis constraint is
    // MaintainXFOV (CameraComponent.h:114).
    private static float ConvertHorizontalToVerticalFieldOfViewRadians(
        float horizontalFieldOfViewRadians,
        float aspectRatio)
    {
        if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
        {
            return horizontalFieldOfViewRadians;
        }
        float tangentHalfHorizontalFieldOfView = MathF.Tan(0.5f * horizontalFieldOfViewRadians);
        return 2.0f * MathF.Atan(tangentHalfHorizontalFieldOfView / aspectRatio);
    }

    // CineCameraComponent.h:84-88: when bOverride_CustomNearClippingPlane is
    // true the explicit CustomNearClippingPlane (centimeters) replaces the
    // engine's GNearClippingPlane. Express the override in meters for glTF.
    private static float ResolveCineCameraNearPlaneMeters(UCineCameraComponent cineCameraComponent)
    {
        bool overrideCustomNear = cineCameraComponent.GetOrDefault(
            "bOverride_CustomNearClippingPlane",
            false);
        if (!overrideCustomNear)
        {
            return DefaultPerspectiveNearPlaneMeters;
        }
        float customNearClippingPlaneCentimeters = cineCameraComponent.GetOrDefault(
            "CustomNearClippingPlane",
            DefaultPerspectiveNearPlaneMeters / UnrealCentimeterToGltfMeter);
        return customNearClippingPlaneCentimeters * UnrealCentimeterToGltfMeter;
    }

    // -----------------------------------------------------------------------
    // Orthographic build
    // -----------------------------------------------------------------------
    //
    // UCameraComponent.h:66-92 — OrthoWidth (centimeters in world units),
    // OrthoNearClipPlane, OrthoFarClipPlane. glTF orthographic projection
    // expects XMag / YMag (half-width / half-height in viewer units).
    // SharpGLTF API: CameraBuilder.Orthographic(xmag, ymag, znear, zfar).
    private static CameraBuilder.Orthographic BuildOrthographicCamera(UObject component)
    {
        // UE engine constructor defaults (CameraComponent.cpp:82, 87, 88):
        //   OrthoWidth         = DEFAULT_ORTHOWIDTH         = 1536.0f
        //   OrthoNearClipPlane = DEFAULT_ORTHONEARPLANE     = -1536.0/2 = -768.0f
        //   OrthoFarClipPlane  = DEFAULT_ORTHOFARPLANE      = UE_OLD_WORLD_MAX = 2097152.0f
        //   AspectRatio        = 1.777778f (CameraComponent.cpp:81; 7 digits)
        // (EngineDefines.h:37, 65-67). All three are in cm world units.
        float orthographicWorldWidthCentimeters = component.GetOrDefault(
            "OrthoWidth",
            NativeDefaultOrthoWidthCentimeters);
        float aspectRatio = component.GetOrDefault("AspectRatio", NativeDefaultAspectRatio);
        if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
        {
            aspectRatio = NativeDefaultAspectRatio;
        }

        // OrthoWidth is the full horizontal extent of the orthographic frustum
        // in Unreal world units (centimeters). glTF XMag is the magnification
        // factor on the x axis, i.e. HALF-width in viewer meters; YMag is the
        // half-height derived from the aspect ratio.
        float orthographicWorldWidthMeters = orthographicWorldWidthCentimeters * UnrealCentimeterToGltfMeter;
        float xMagnification = 0.5f * orthographicWorldWidthMeters;
        float yMagnification = xMagnification / aspectRatio;

        float orthographicNearPlaneCentimeters = component.GetOrDefault(
            "OrthoNearClipPlane",
            NativeDefaultOrthoNearClipPlaneCentimeters);
        float orthographicFarPlaneCentimeters = component.GetOrDefault(
            "OrthoFarClipPlane",
            NativeDefaultOrthoFarClipPlaneCentimeters);
        float nearPlaneMeters = orthographicNearPlaneCentimeters * UnrealCentimeterToGltfMeter;
        float farPlaneMeters = orthographicFarPlaneCentimeters * UnrealCentimeterToGltfMeter;

        // SharpGLTF / glTF 2.0 require XMag, YMag > 0 and 0 < ZNear < ZFar.
        // UE's default OrthoNearClipPlane is NEGATIVE (-768 cm), which cannot
        // be expressed in a glTF orthographic camera. Clamp to a positive
        // floor so the file is loadable while keeping the lossless JSON
        // sidecar as the ground truth for the raw UE values.
        if (xMagnification <= 0.0f) xMagnification = 1.0f;
        if (yMagnification <= 0.0f) yMagnification = xMagnification;
        if (nearPlaneMeters <= 0.0f) nearPlaneMeters = DefaultPerspectiveNearPlaneMeters;
        if (!(farPlaneMeters > nearPlaneMeters)) farPlaneMeters = nearPlaneMeters + 1.0f;

        return new CameraBuilder.Orthographic(
            xMagnification,
            yMagnification,
            nearPlaneMeters,
            farPlaneMeters);
    }

    // -----------------------------------------------------------------------
    // Property readers
    // -----------------------------------------------------------------------

    // ECameraProjectionMode is a TEnumAsByte in UE (CameraComponent.h:226).
    // Under unversioned property serialisation TEnumAsByte<EFoo> surfaces in
    // CUE4Parse as an FName whose PlainText is "EFoo::ValueName". Default
    // engine value is Perspective.
    private static EProjectionMode ResolveProjectionMode(UObject component)
    {
        FName projectionModeName = component.GetOrDefault<FName>("ProjectionMode");
        string projectionModeText = projectionModeName.PlainText ?? string.Empty;
        if (projectionModeText.EndsWith("Orthographic", StringComparison.Ordinal))
        {
            return EProjectionMode.Orthographic;
        }
        return EProjectionMode.Perspective;
    }

    // UCameraComponent.h:108-122 -- AspectRatio is only meaningful when
    // bConstrainAspectRatio is set. The default value of bConstrainAspectRatio
    // differs between subclasses:
    //   * UCameraComponent  -> false (CameraComponent.cpp:89)
    //   * UCineCameraComponent -> true (CineCameraComponent.cpp:45)
    // When the cooked tagged property bag omits the flag, we must fall back
    // to the per-class default so a CineCameraComponent without an explicit
    // override still gets its constrained aspect (which for CineCamera is
    // RecalcDerivedData's SensorAspectRatio * SqueezeFactor; the cooked
    // AspectRatio property carries that derived value).
    private static float? ResolveOptionalAspectRatio(UObject component)
    {
        bool defaultConstrain = component is UCineCameraComponent;
        bool constrainAspectRatio = component.GetOrDefault("bConstrainAspectRatio", defaultConstrain);
        if (!constrainAspectRatio)
        {
            return null;
        }
        float aspectRatio = component.GetOrDefault("AspectRatio", NativeDefaultAspectRatio);
        if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
        {
            return null;
        }
        return aspectRatio;
    }

    // FCameraFilmbackSettings reader. CineCameraComponent.h:38, struct
    // declaration at CineCameraSettings.h:15-65. SensorAspectRatio is
    // engine-computed (RecalcSensorAspectRatio at CineCameraSettings.h:52-55)
    // and not consumed by the FOV math; we still capture it via the lossless
    // layer (the per-actor JSON sidecar holds the raw struct).
    private static CameraFilmbackData ReadFilmbackSettings(UObject component)
    {
        FStructFallback? filmbackStruct = component.GetOrDefault<FStructFallback>("Filmback");
        float sensorWidthMillimeters = NativeDefaultSensorWidthMillimeters;
        float sensorHeightMillimeters = NativeDefaultSensorHeightMillimeters;
        float sensorHorizontalOffsetMillimeters = 0.0f;
        float sensorVerticalOffsetMillimeters = 0.0f;
        if (filmbackStruct != null)
        {
            sensorWidthMillimeters = filmbackStruct.GetOrDefault("SensorWidth", NativeDefaultSensorWidthMillimeters);
            sensorHeightMillimeters = filmbackStruct.GetOrDefault("SensorHeight", NativeDefaultSensorHeightMillimeters);
            sensorHorizontalOffsetMillimeters = filmbackStruct.GetOrDefault("SensorHorizontalOffset", 0.0f);
            sensorVerticalOffsetMillimeters = filmbackStruct.GetOrDefault("SensorVerticalOffset", 0.0f);
        }
        return new CameraFilmbackData(
            sensorWidthMillimeters,
            sensorHeightMillimeters,
            sensorHorizontalOffsetMillimeters,
            sensorVerticalOffsetMillimeters);
    }

    // FCameraLensSettings reader. CineCameraSettings.h:89-130. The only field
    // the FOV math touches is SqueezeFactor; the rest are captured so the
    // audit note + lossless sidecar carry the full lens record.
    private static CameraLensSettingsData ReadLensSettings(UObject component)
    {
        FStructFallback? lensStruct = component.GetOrDefault<FStructFallback>("LensSettings");
        float minimumFocalLengthMillimeters = NativeDefaultMinFocalLengthMillimeters;
        float maximumFocalLengthMillimeters = NativeDefaultMaxFocalLengthMillimeters;
        float minimumFStop = NativeDefaultMinFStop;
        float maximumFStop = NativeDefaultMaxFStop;
        float minimumFocusDistanceCentimeters = NativeDefaultMinimumFocusDistanceCentimeters;
        float squeezeFactor = NativeDefaultSqueezeFactor;
        int diaphragmBladeCount = 0;
        if (lensStruct != null)
        {
            minimumFocalLengthMillimeters = lensStruct.GetOrDefault("MinFocalLength", NativeDefaultMinFocalLengthMillimeters);
            maximumFocalLengthMillimeters = lensStruct.GetOrDefault("MaxFocalLength", NativeDefaultMaxFocalLengthMillimeters);
            minimumFStop = lensStruct.GetOrDefault("MinFStop", NativeDefaultMinFStop);
            maximumFStop = lensStruct.GetOrDefault("MaxFStop", NativeDefaultMaxFStop);
            minimumFocusDistanceCentimeters = lensStruct.GetOrDefault("MinimumFocusDistance", NativeDefaultMinimumFocusDistanceCentimeters);
            squeezeFactor = lensStruct.GetOrDefault("SqueezeFactor", NativeDefaultSqueezeFactor);
            diaphragmBladeCount = lensStruct.GetOrDefault("DiaphragmBladeCount", 0);
        }
        if (!float.IsFinite(squeezeFactor) || squeezeFactor <= 0.0f) squeezeFactor = NativeDefaultSqueezeFactor;
        return new CameraLensSettingsData(
            minimumFocalLengthMillimeters,
            maximumFocalLengthMillimeters,
            minimumFStop,
            maximumFStop,
            minimumFocusDistanceCentimeters,
            squeezeFactor,
            diaphragmBladeCount);
    }

    // FPlateCropSettings reader. CineCameraSettings.h:161-173. CropSettings
    // defaults to AspectRatio == 0, which the engine treats as "no crop"
    // (CineCameraComponent.cpp:291 / 317 short-circuits on > 0).
    private static CameraCropSettingsData ReadCropSettings(UObject component)
    {
        FStructFallback? cropStruct = component.GetOrDefault<FStructFallback>("CropSettings");
        float croppedAspectRatio = 0.0f;
        if (cropStruct != null)
        {
            croppedAspectRatio = cropStruct.GetOrDefault("AspectRatio", 0.0f);
        }
        return new CameraCropSettingsData(croppedAspectRatio);
    }

    // Overscan + AsymmetricOverscan are flat fields on UCameraComponent
    // (CameraComponent.h:136, 146). FVector4f is read via CUE4Parse's struct
    // fallback as four float fields named X / Y / Z / W per the standard
    // FVector4f UPROPERTY serialisation.
    private static CameraOverscanData ReadOverscanFields(UObject component)
    {
        float uniformOverscan = component.GetOrDefault("Overscan", 0.0f);
        FStructFallback? asymmetricOverscanStruct = component.GetOrDefault<FStructFallback>("AsymmetricOverscan");
        float asymmetricOverscanX = 0.0f;
        float asymmetricOverscanY = 0.0f;
        float asymmetricOverscanZ = 0.0f;
        float asymmetricOverscanW = 0.0f;
        if (asymmetricOverscanStruct != null)
        {
            asymmetricOverscanX = asymmetricOverscanStruct.GetOrDefault("X", 0.0f);
            asymmetricOverscanY = asymmetricOverscanStruct.GetOrDefault("Y", 0.0f);
            asymmetricOverscanZ = asymmetricOverscanStruct.GetOrDefault("Z", 0.0f);
            asymmetricOverscanW = asymmetricOverscanStruct.GetOrDefault("W", 0.0f);
        }
        return new CameraOverscanData(
            uniformOverscan,
            asymmetricOverscanX,
            asymmetricOverscanY,
            asymmetricOverscanZ,
            asymmetricOverscanW);
    }

    // -----------------------------------------------------------------------
    // Audit note
    // -----------------------------------------------------------------------
    //
    // Adds one entry per translated camera to manifest.Notes so the run log /
    // scene-manifest.json carries the camera count and projection summary.
    // The full property tree (every CineCamera field, post-process settings,
    // override flags, etc.) is preserved by the lossless layer's per-actor
    // JSON sidecar; this note is just the audit hook.
    private static void RecordAuditNote(
        UObject component,
        EProjectionMode projectionMode,
        CameraBuilder cameraBuilder,
        GlbSceneContext context)
    {
        string componentPath = component.GetPathName();
        string componentKind = component is UCineCameraComponent ? "cine" : "plain";
        string note;
        if (cameraBuilder is CameraBuilder.Perspective perspectiveCamera)
        {
            float verticalFieldOfViewDegrees = perspectiveCamera.VerticalFOV * 180.0f / MathF.PI;
            string aspectRatioText = perspectiveCamera.AspectRatio.HasValue
                ? perspectiveCamera.AspectRatio.Value.ToString("F4", CultureInfo.InvariantCulture)
                : "(viewer)";
            string farPlaneText = float.IsPositiveInfinity(perspectiveCamera.ZFar)
                ? "inf"
                : perspectiveCamera.ZFar.ToString("F4", CultureInfo.InvariantCulture);
            // For CineCamera, surface the canonical identity fields too --
            // the lossless JSON sidecar carries every byte verbatim, this is
            // only the manifest summary line so an operator can spot-check
            // the camera without parsing the per-actor JSON.
            string cineCameraExtras = string.Empty;
            if (component is UCineCameraComponent cineCameraComponent)
            {
                CameraFilmbackData filmback = ReadFilmbackSettings(cineCameraComponent);
                CameraLensSettingsData lens = ReadLensSettings(cineCameraComponent);
                float currentFocalLength = cineCameraComponent.GetOrDefault(
                    "CurrentFocalLength",
                    NativeDefaultCurrentFocalLengthMillimeters);
                float currentAperture = cineCameraComponent.GetOrDefault("CurrentAperture", 2.0f);
                cineCameraExtras = string.Create(
                    CultureInfo.InvariantCulture,
                    $" sensor={filmback.SensorWidthMillimeters:F2}x{filmback.SensorHeightMillimeters:F2}mm focal={currentFocalLength:F2}mm aperture={currentAperture:F2} squeeze={lens.SqueezeFactor:F3}");
            }
            note = string.Create(
                CultureInfo.InvariantCulture,
                $"[GlbScene][Camera] perspective kind={componentKind} path='{componentPath}' vfovDeg={verticalFieldOfViewDegrees:F3} aspect={aspectRatioText} zNear={perspectiveCamera.ZNear:F4} zFar={farPlaneText}{cineCameraExtras}");
        }
        else if (cameraBuilder is CameraBuilder.Orthographic orthographicCamera)
        {
            note = string.Create(
                CultureInfo.InvariantCulture,
                $"[GlbScene][Camera] orthographic kind={componentKind} path='{componentPath}' xMag={orthographicCamera.XMag:F4} yMag={orthographicCamera.YMag:F4} zNear={orthographicCamera.ZNear:F4} zFar={orthographicCamera.ZFar:F4}");
        }
        else
        {
            note = $"[GlbScene][Camera] unknown projection path='{componentPath}' mode={projectionMode}";
        }
        context.Manifest.Notes.Add(note);
    }

    // Local-only projection-mode enum mirrors UE's ECameraProjectionMode
    // (Perspective = 0, Orthographic = 1). Anything else surfaces as
    // Perspective so the rendered scene is at least viewable; the lossless
    // layer carries the exact FName for round-trip.
    private enum EProjectionMode
    {
        Perspective,
        Orthographic,
    }

    // -----------------------------------------------------------------------
    // Local record-shaped value types
    // -----------------------------------------------------------------------
    //
    // Kept as `readonly struct` so the per-camera read path does not allocate.
    // Each one mirrors a UE struct and is consumed only in this file.

    private readonly struct CameraFilmbackData
    {
        public readonly float SensorWidthMillimeters;
        public readonly float SensorHeightMillimeters;
        public readonly float SensorHorizontalOffsetMillimeters;
        public readonly float SensorVerticalOffsetMillimeters;

        public CameraFilmbackData(
            float sensorWidthMillimeters,
            float sensorHeightMillimeters,
            float sensorHorizontalOffsetMillimeters,
            float sensorVerticalOffsetMillimeters)
        {
            SensorWidthMillimeters = sensorWidthMillimeters;
            SensorHeightMillimeters = sensorHeightMillimeters;
            SensorHorizontalOffsetMillimeters = sensorHorizontalOffsetMillimeters;
            SensorVerticalOffsetMillimeters = sensorVerticalOffsetMillimeters;
        }
    }

    private readonly struct CameraLensSettingsData
    {
        public readonly float MinimumFocalLengthMillimeters;
        public readonly float MaximumFocalLengthMillimeters;
        public readonly float MinimumFStop;
        public readonly float MaximumFStop;
        public readonly float MinimumFocusDistanceCentimeters;
        public readonly float SqueezeFactor;
        public readonly int DiaphragmBladeCount;

        public CameraLensSettingsData(
            float minimumFocalLengthMillimeters,
            float maximumFocalLengthMillimeters,
            float minimumFStop,
            float maximumFStop,
            float minimumFocusDistanceCentimeters,
            float squeezeFactor,
            int diaphragmBladeCount)
        {
            MinimumFocalLengthMillimeters = minimumFocalLengthMillimeters;
            MaximumFocalLengthMillimeters = maximumFocalLengthMillimeters;
            MinimumFStop = minimumFStop;
            MaximumFStop = maximumFStop;
            MinimumFocusDistanceCentimeters = minimumFocusDistanceCentimeters;
            SqueezeFactor = squeezeFactor;
            DiaphragmBladeCount = diaphragmBladeCount;
        }
    }

    private readonly struct CameraCropSettingsData
    {
        public readonly float CroppedAspectRatio;

        public CameraCropSettingsData(float croppedAspectRatio)
        {
            CroppedAspectRatio = croppedAspectRatio;
        }
    }

    private readonly struct CameraOverscanData
    {
        public readonly float UniformOverscan;
        public readonly float AsymmetricOverscanX;
        public readonly float AsymmetricOverscanY;
        public readonly float AsymmetricOverscanZ;
        public readonly float AsymmetricOverscanW;

        public CameraOverscanData(
            float uniformOverscan,
            float asymmetricOverscanX,
            float asymmetricOverscanY,
            float asymmetricOverscanZ,
            float asymmetricOverscanW)
        {
            UniformOverscan = uniformOverscan;
            AsymmetricOverscanX = asymmetricOverscanX;
            AsymmetricOverscanY = asymmetricOverscanY;
            AsymmetricOverscanZ = asymmetricOverscanZ;
            AsymmetricOverscanW = asymmetricOverscanW;
        }
    }
}

using System;
using System.Globalization;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using SharpGLTF.Scenes;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class CameraComponentExporter : IComponentExporter
{
    private const float NativeDefaultSensorWidthMillimeters = 24.89f;
    private const float NativeDefaultSensorHeightMillimeters = 18.67f;
    private const float NativeDefaultSqueezeFactor = 1.0f;
    private const float NativeDefaultCurrentFocalLengthMillimeters = 35.0f;
    private const float NativeDefaultMinFocalLengthMillimeters = 50.0f;
    private const float NativeDefaultMaxFocalLengthMillimeters = 50.0f;
    private const float NativeDefaultMinFStop = 2.0f;
    private const float NativeDefaultMaxFStop = 2.0f;
    private const float NativeDefaultMinimumFocusDistanceCentimeters = 15.0f;

    private const float NativeDefaultCameraComponentFieldOfViewDegrees = 90.0f;

    private const float NativeDefaultAspectRatio = 1.777778f;

    private const float NativeDefaultOrthoWidthCentimeters = 1536.0f;
    private const float NativeDefaultOrthoNearClipPlaneCentimeters =
        -NativeDefaultOrthoWidthCentimeters / 2.0f;
    private const float NativeDefaultOrthoFarClipPlaneCentimeters = 2_097_152.0f;

    private const float DefaultPerspectiveNearPlaneMeters = 0.1f;
    private const float DefaultPerspectiveFarPlaneMeters = float.PositiveInfinity;

    private const float UnrealCentimeterToGltfMeter = 0.01f;

    private static readonly Matrix4x4 CameraAxisRemapGltfFromUnreal =
        Matrix4x4.CreateRotationY(-MathF.PI / 2.0f);

    public bool CanExport(UObject component)
    {
        return component is UCameraComponent;
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        UObject component = placed.Component;

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

    private static CameraBuilder.Perspective BuildPerspectiveCamera(UObject component)
    {
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
            float horizontalFieldOfViewDegrees = component.GetOrDefault(
                "FieldOfView",
                NativeDefaultCameraComponentFieldOfViewDegrees);
            float horizontalFieldOfViewRadians = MathF.PI / 180.0f * horizontalFieldOfViewDegrees;

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

    private static float ComputeCineCameraVerticalFieldOfViewRadians(UCineCameraComponent cineCameraComponent)
    {
        float currentFocalLengthMillimeters = cineCameraComponent.GetOrDefault(
            "CurrentFocalLength",
            NativeDefaultCurrentFocalLengthMillimeters);
        if (!float.IsFinite(currentFocalLengthMillimeters) || currentFocalLengthMillimeters <= 0.0f)
        {
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

    private static CameraBuilder.Orthographic BuildOrthographicCamera(UObject component)
    {
        float orthographicWorldWidthCentimeters = component.GetOrDefault(
            "OrthoWidth",
            NativeDefaultOrthoWidthCentimeters);
        float aspectRatio = component.GetOrDefault("AspectRatio", NativeDefaultAspectRatio);
        if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
        {
            aspectRatio = NativeDefaultAspectRatio;
        }

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

    private enum EProjectionMode
    {
        Perspective,
        Orthographic,
    }


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

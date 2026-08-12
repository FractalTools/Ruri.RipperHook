using System;
using System.Globalization;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class LightComponentExporter : IComponentExporter
{
    private const float SpotConeMaximumRadians = 1.5707963267948966f;    private const float SpotConeEpsilonRadians = 0.001f;

    public bool CanExport(UObject component)
    {
        return component is ULightComponentBase;
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        UObject component = placed.Component;
        if (component is not ULightComponentBase lightComponentBase)
        {
            string defensivePath = component.GetPathName();
            context.LogError($"[GlbScene] LightComponent '{defensivePath}' did not surface as ULightComponentBase; dropped.");
            context.Manifest.RecordDroppedComponent($"{defensivePath}: light component cast to ULightComponentBase failed.");
            return;
        }

        try
        {
            LightTranslationResult translation = TranslateLight(lightComponentBase);

            Matrix4x4 lightNodeMatrix = translation.RequiresDirectionalAxisRemap
                ? Matrix4x4.Multiply(LightAxisRemapGltfFromUnreal, placed.WorldTransform.Matrix)
                : placed.WorldTransform.Matrix;

            context.AddLight(
                translation.LightType,
                translation.Common.LinearColor,
                translation.IntensityCandela,
                translation.RangeMeters,
                translation.InnerConeAngleRadians,
                translation.OuterConeAngleRadians,
                component.Name,
                lightNodeMatrix);
            RecordAuditNote(component, translation, context);
        }
        catch (Exception exception)
        {
            string componentPath = component.GetPathName();
            context.LogError($"[GlbScene] LightComponent '{componentPath}' translation failed: {exception.Message}");
            context.Manifest.RecordDroppedComponent($"{componentPath}: light translation: {exception.Message}");
        }
    }

    private static readonly Matrix4x4 LightAxisRemapGltfFromUnreal =
        Matrix4x4.CreateRotationY(MathF.PI / 2.0f);


    private static LightTranslationResult TranslateLight(ULightComponentBase lightComponentBase)
    {
        return lightComponentBase switch
        {
            USpotLightComponent spot       => TranslateSpotLight(spot),
            URectLightComponent rect       => TranslateRectLight(rect),
            UPointLightComponent point     => TranslatePointLight(point),
            UDirectionalLightComponent dir => TranslateDirectionalLight(dir),
            USkyLightComponent sky         => TranslateSkyLight(sky),
            ULightComponent generic        => TranslateGenericLight(generic),
            _                              => TranslateBaseOnlyLight(lightComponentBase),
        };
    }

    private static LightTranslationResult TranslateSpotLight(USpotLightComponent spot)
    {
        LightCommonReadout common = ReadLightCommon(spot);
        float attenuationRadiusMeters = ReadAttenuationRadiusMeters(spot);
        float cosHalfConeAngle = spot.GetCosHalfConeAngle();
        float intensityCandela = ConvertLocalLightIntensityToCandela(
            spot,
            common.IntensityRawValue,
            cosHalfConeAngle);

        (float innerConeAngleRadians, float outerConeAngleRadians) = ResolveSpotConeAnglesRadians(spot);

        return new LightTranslationResult(
            PunctualLightType.Spot,
            PunctualLightFamily.Spot,
            requiresDirectionalAxisRemap: true,
            intensityCandela: intensityCandela,
            rangeMeters: attenuationRadiusMeters,
            innerConeAngleRadians: innerConeAngleRadians,
            outerConeAngleRadians: outerConeAngleRadians,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }

    private static (float Inner, float Outer) ResolveSpotConeAnglesRadians(USpotLightComponent spot)
    {
        float innerConeAngleDegrees = spot.InnerConeAngle;
        float outerConeAngleDegrees = spot.OuterConeAngle;

        float innerClampedRadians = Math.Clamp(innerConeAngleDegrees, 0.0f, 89.0f)
                                  * MathF.PI / 180.0f;
        float outerClampedRadians = Math.Clamp(
            outerConeAngleDegrees * MathF.PI / 180.0f,
            innerClampedRadians + SpotConeEpsilonRadians,
            SpotConeMaximumRadians + SpotConeEpsilonRadians);

        if (outerClampedRadians > SpotConeMaximumRadians) outerClampedRadians = SpotConeMaximumRadians;
        if (innerClampedRadians > outerClampedRadians)    innerClampedRadians = outerClampedRadians;

        return (innerClampedRadians, outerClampedRadians);
    }

    private static LightTranslationResult TranslatePointLight(UPointLightComponent point)
    {
        LightCommonReadout common = ReadLightCommon(point);
        float attenuationRadiusMeters = ReadAttenuationRadiusMeters(point);
        float intensityCandela = ConvertLocalLightIntensityToCandela(
            point,
            common.IntensityRawValue,
            cosHalfConeAngle: -1.0f);

        return new LightTranslationResult(
            PunctualLightType.Point,
            PunctualLightFamily.Point,
            requiresDirectionalAxisRemap: false,
            intensityCandela: intensityCandela,
            rangeMeters: attenuationRadiusMeters,
            innerConeAngleRadians: 0.0f,
            outerConeAngleRadians: 0.0f,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }

    private static LightTranslationResult TranslateRectLight(URectLightComponent rect)
    {
        LightCommonReadout common = ReadLightCommon(rect);
        float attenuationRadiusMeters = ReadAttenuationRadiusMeters(rect);

        float sourceWidthCentimeters = rect.SourceWidth;
        float sourceHeightCentimeters = rect.SourceHeight;
        float rectAreaSquareMeters = (sourceWidthCentimeters / 100.0f)
                                   * (sourceHeightCentimeters / 100.0f);

        float barnDoorAngleDegrees = rect.BarnDoorAngle;
        float halfConeAngleRadians = Math.Clamp(barnDoorAngleDegrees, 1.0f, 89.0f)
                                   * MathF.PI / 180.0f;
        float cosHalfConeAngle = MathF.Cos(halfConeAngleRadians);
        float outerConeAngleRadians = Math.Min(halfConeAngleRadians, SpotConeMaximumRadians);
        float innerConeAngleRadians = 0.0f;

        float intensityCandela = ConvertLocalLightIntensityToCandela(
            rect,
            common.IntensityRawValue,
            cosHalfConeAngle);

        return new LightTranslationResult(
            PunctualLightType.Spot,
            PunctualLightFamily.RectAsSpotFallback,
            requiresDirectionalAxisRemap: true,
            intensityCandela: intensityCandela,
            rangeMeters: attenuationRadiusMeters,
            innerConeAngleRadians: innerConeAngleRadians,
            outerConeAngleRadians: outerConeAngleRadians,
            common: common,
            rectAreaSquareMeters: rectAreaSquareMeters,
            barnDoorAngleDegrees: barnDoorAngleDegrees,
            barnDoorLengthCentimeters: rect.BarnDoorLength,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }

    private static LightTranslationResult TranslateDirectionalLight(UDirectionalLightComponent directional)
    {
        LightCommonReadout common = ReadLightCommon(directional);
        float intensityLux = common.IntensityRawValue;

        return new LightTranslationResult(
            PunctualLightType.Directional,
            PunctualLightFamily.Directional,
            requiresDirectionalAxisRemap: true,
            intensityCandela: intensityLux,
            rangeMeters: 0.0f,
            innerConeAngleRadians: 0.0f,
            outerConeAngleRadians: 0.0f,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }

    private static LightTranslationResult TranslateSkyLight(USkyLightComponent skyLight)
    {
        LightCommonReadout common = ReadLightCommonBaseOnly(skyLight);
        float intensityCandela = MathF.Max(common.IntensityRawValue, 0.0f);

        string skyCubemapPathName = ReadSkyLightCubemapPathName(skyLight);
        bool skyRealTimeCapture = skyLight.GetOrDefault("bRealTimeCapture", false);

        return new LightTranslationResult(
            PunctualLightType.Point,
            PunctualLightFamily.SkyAsAmbientPointFallback,
            requiresDirectionalAxisRemap: false,
            intensityCandela: intensityCandela,
            rangeMeters: 0.0f,
            innerConeAngleRadians: 0.0f,
            outerConeAngleRadians: 0.0f,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: skyCubemapPathName,
            skyRealTimeCapture: skyRealTimeCapture);
    }

    private static string ReadSkyLightCubemapPathName(USkyLightComponent skyLight)
    {
        FPackageIndex cubemapIndex = skyLight.GetOrDefault("Cubemap", new FPackageIndex());
        if (cubemapIndex is null || cubemapIndex.IsNull)
        {
            return string.Empty;
        }
        return cubemapIndex.ResolvedObject?.GetPathName() ?? string.Empty;
    }

    private static LightTranslationResult TranslateGenericLight(ULightComponent generic)
    {
        LightCommonReadout common = ReadLightCommon(generic);
        float assumedAreaInSqMeters = 1.0f;        float assumedSolidAngle = 4.0f * MathF.PI;
        float intensityCandela = LightUtils.ConvertToIntensityToNits(
            common.IntensityRawValue,
            assumedAreaInSqMeters,
            assumedSolidAngle,
            generic.GetLightUnits());

        return new LightTranslationResult(
            PunctualLightType.Point,
            PunctualLightFamily.GenericAsPoint,
            requiresDirectionalAxisRemap: false,
            intensityCandela: intensityCandela,
            rangeMeters: 0.0f,
            innerConeAngleRadians: 0.0f,
            outerConeAngleRadians: 0.0f,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }

    private static LightTranslationResult TranslateBaseOnlyLight(ULightComponentBase lightComponentBase)
    {
        LightCommonReadout common = ReadLightCommonBaseOnly(lightComponentBase);
        return new LightTranslationResult(
            PunctualLightType.Point,
            PunctualLightFamily.BaseAsPoint,
            requiresDirectionalAxisRemap: false,
            intensityCandela: common.IntensityRawValue,
            rangeMeters: 0.0f,
            innerConeAngleRadians: 0.0f,
            outerConeAngleRadians: 0.0f,
            common: common,
            rectAreaSquareMeters: 0.0f,
            barnDoorAngleDegrees: 0.0f,
            barnDoorLengthCentimeters: 0.0f,
            skyCubemapPathName: string.Empty,
            skyRealTimeCapture: false);
    }


    private static LightCommonReadout ReadLightCommon(ULightComponent lightComponent)
    {
        Vector3 linearColor = ResolveLightColor(lightComponent);
        return new LightCommonReadout(
            intensityRawValue: lightComponent.Intensity,
            linearColor: linearColor,
            temperatureKelvin: lightComponent.Temperature,
            useTemperature: lightComponent.bUseTemperature,
            castShadows: lightComponent.CastShadows,
            maxDrawDistance: lightComponent.MaxDrawDistance,
            iesPathName: ResolveIesPathName(lightComponent),
            useIesBrightness: lightComponent.bUseIESBrightness,
            iesBrightnessScale: lightComponent.IESBrightnessScale);
    }

    private static LightCommonReadout ReadLightCommonBaseOnly(ULightComponentBase lightComponentBase)
    {
        Vector3 linearColor = FLinearColorToGlbColor(lightComponentBase.GetLightColor());
        return new LightCommonReadout(
            intensityRawValue: lightComponentBase.Intensity,
            linearColor: linearColor,
            temperatureKelvin: 6500.0f,
            useTemperature: false,
            castShadows: lightComponentBase.CastShadows,
            maxDrawDistance: 0.0f,
            iesPathName: string.Empty,
            useIesBrightness: false,
            iesBrightnessScale: 1.0f);
    }

    private static Vector3 ResolveLightColor(ULightComponent lightComponent)
    {
        Vector3 baseLinear = FLinearColorToGlbColor(lightComponent.GetLightColor());
        if (!lightComponent.bUseTemperature)
        {
            return baseLinear;
        }
        Vector3 temperatureLinear = ColorTemperatureKelvinToLinearRgb(lightComponent.Temperature);
        return new Vector3(
            baseLinear.X * temperatureLinear.X,
            baseLinear.Y * temperatureLinear.Y,
            baseLinear.Z * temperatureLinear.Z);
    }

    private static Vector3 FLinearColorToGlbColor(CUE4Parse.UE4.Objects.Core.Math.FLinearColor lightColor)
    {
        return new Vector3(
            MathF.Max(lightColor.R, 0.0f),
            MathF.Max(lightColor.G, 0.0f),
            MathF.Max(lightColor.B, 0.0f));
    }

    private static Vector3 ColorTemperatureKelvinToLinearRgb(float temperatureKelvin)
    {
        float clampedKelvin = Math.Clamp(temperatureKelvin, 1000.0f, 15000.0f);
        float u = (0.860117757f + 1.54118254e-4f * clampedKelvin + 1.28641212e-7f * clampedKelvin * clampedKelvin)
                / (1.0f + 8.42420235e-4f * clampedKelvin + 7.08145163e-7f * clampedKelvin * clampedKelvin);
        float v = (0.317398726f + 4.22806245e-5f * clampedKelvin + 4.20481691e-8f * clampedKelvin * clampedKelvin)
                / (1.0f - 2.89741816e-5f * clampedKelvin + 1.61456053e-7f * clampedKelvin * clampedKelvin);

        float xChromaticity = 3.0f * u / (2.0f * u - 8.0f * v + 4.0f);
        float yChromaticity = 2.0f * v / (2.0f * u - 8.0f * v + 4.0f);
        float zChromaticity = 1.0f - xChromaticity - yChromaticity;

        float yLuminance = 1.0f;
        float xTristimulus = yLuminance / yChromaticity * xChromaticity;
        float zTristimulus = yLuminance / yChromaticity * zChromaticity;

        float r =  3.2404542f * xTristimulus + -1.5371385f * yLuminance + -0.4985314f * zTristimulus;
        float g = -0.9692660f * xTristimulus +  1.8760108f * yLuminance +  0.0415560f * zTristimulus;
        float b =  0.0556434f * xTristimulus + -0.2040259f * yLuminance +  1.0572252f * zTristimulus;

        return new Vector3(
            MathF.Max(r, 0.0f),
            MathF.Max(g, 0.0f),
            MathF.Max(b, 0.0f));
    }

    private static string ResolveIesPathName(ULightComponent lightComponent)
    {
        FPackageIndex iesTexture = lightComponent.IESTexture;
        if (iesTexture is null || iesTexture.IsNull)
        {
            return string.Empty;
        }
        return iesTexture.ResolvedObject?.GetPathName() ?? string.Empty;
    }

    private static float ReadAttenuationRadiusMeters(ULocalLightComponent localLight)
    {
        float radiusCentimeters = localLight.AttenuationRadius;
        return radiusCentimeters * 0.01f;
    }

    private static float ConvertLocalLightIntensityToCandela(
        ULocalLightComponent localLight,
        float intensityRawValue,
        float cosHalfConeAngle)
    {
        ELightUnits sourceUnits = localLight.GetLightUnits();
        float conversionFactor = LightUtils.GetUnitsConversionFactor(
            sourceUnits,
            ELightUnits.Candelas,
            cosHalfConeAngle);
        float candela = intensityRawValue * conversionFactor;
        if (!float.IsFinite(candela) || candela < 0.0f)
        {
            candela = 0.0f;
        }
        return candela;
    }

    private static void RecordAuditNote(
        UObject component,
        in LightTranslationResult translation,
        GlbSceneContext context)
    {
        string componentPath = component.GetPathName();
        LightCommonReadout common = translation.Common;

        string baseFields = string.Create(
            CultureInfo.InvariantCulture,
            $"path='{componentPath}' family={translation.Family} intensity={translation.IntensityCandela:F4} range={translation.RangeMeters:F4} colorR={common.LinearColor.X:F4} colorG={common.LinearColor.Y:F4} colorB={common.LinearColor.Z:F4} temperatureK={common.TemperatureKelvin:F1} useTemperature={common.UseTemperature} castShadows={common.CastShadows} maxDrawDist={common.MaxDrawDistance:F2}");

        string familySpecific = translation.Family switch
        {
            PunctualLightFamily.Spot => string.Create(
                CultureInfo.InvariantCulture,
                $"innerConeRad={translation.InnerConeAngleRadians:F4} outerConeRad={translation.OuterConeAngleRadians:F4}"),
            PunctualLightFamily.RectAsSpotFallback => string.Create(
                CultureInfo.InvariantCulture,
                $"rectAreaM2={translation.RectAreaSquareMeters:F6} barnDoorAngleDeg={translation.BarnDoorAngleDegrees:F4} barnDoorLengthCm={translation.BarnDoorLengthCentimeters:F2}"),
            PunctualLightFamily.SkyAsAmbientPointFallback => string.Create(
                CultureInfo.InvariantCulture,
                $"realTimeCapture={translation.SkyRealTimeCapture} cubemap='{translation.SkyCubemapPathName}'"),
            PunctualLightFamily.Directional => string.Empty,
            PunctualLightFamily.Point      => string.Empty,
            _                              => string.Empty,
        };

        string iesField = string.IsNullOrEmpty(common.IesPathName)
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $" ies='{common.IesPathName}' useIesBrightness={common.UseIesBrightness} iesBrightnessScale={common.IesBrightnessScale:F4}");

        string note = familySpecific.Length > 0
            ? $"[GlbScene][Light] {baseFields} {familySpecific}{iesField}"
            : $"[GlbScene][Light] {baseFields}{iesField}";

        context.Manifest.Notes.Add(note);
    }


    private enum PunctualLightFamily
    {
        Point,
        Spot,
        Directional,
        RectAsSpotFallback,
        SkyAsAmbientPointFallback,
        GenericAsPoint,
        BaseAsPoint,
    }

    private readonly struct LightTranslationResult
    {
        public readonly PunctualLightType LightType;
        public readonly PunctualLightFamily Family;
        public readonly bool RequiresDirectionalAxisRemap;
        public readonly float IntensityCandela;
        public readonly float RangeMeters;
        public readonly float InnerConeAngleRadians;
        public readonly float OuterConeAngleRadians;
        public readonly LightCommonReadout Common;
        public readonly float RectAreaSquareMeters;
        public readonly float BarnDoorAngleDegrees;
        public readonly float BarnDoorLengthCentimeters;
        public readonly string SkyCubemapPathName;
        public readonly bool SkyRealTimeCapture;

        public LightTranslationResult(
            PunctualLightType lightType,
            PunctualLightFamily family,
            bool requiresDirectionalAxisRemap,
            float intensityCandela,
            float rangeMeters,
            float innerConeAngleRadians,
            float outerConeAngleRadians,
            LightCommonReadout common,
            float rectAreaSquareMeters,
            float barnDoorAngleDegrees,
            float barnDoorLengthCentimeters,
            string skyCubemapPathName,
            bool skyRealTimeCapture)
        {
            LightType = lightType;
            Family = family;
            RequiresDirectionalAxisRemap = requiresDirectionalAxisRemap;
            IntensityCandela = intensityCandela;
            RangeMeters = rangeMeters;
            InnerConeAngleRadians = innerConeAngleRadians;
            OuterConeAngleRadians = outerConeAngleRadians;
            Common = common;
            RectAreaSquareMeters = rectAreaSquareMeters;
            BarnDoorAngleDegrees = barnDoorAngleDegrees;
            BarnDoorLengthCentimeters = barnDoorLengthCentimeters;
            SkyCubemapPathName = skyCubemapPathName;
            SkyRealTimeCapture = skyRealTimeCapture;
        }
    }

    private readonly struct LightCommonReadout
    {
        public readonly float IntensityRawValue;
        public readonly Vector3 LinearColor;
        public readonly float TemperatureKelvin;
        public readonly bool UseTemperature;
        public readonly bool CastShadows;
        public readonly float MaxDrawDistance;
        public readonly string IesPathName;
        public readonly bool UseIesBrightness;
        public readonly float IesBrightnessScale;

        public LightCommonReadout(
            float intensityRawValue,
            Vector3 linearColor,
            float temperatureKelvin,
            bool useTemperature,
            bool castShadows,
            float maxDrawDistance,
            string iesPathName,
            bool useIesBrightness,
            float iesBrightnessScale)
        {
            IntensityRawValue = intensityRawValue;
            LinearColor = linearColor;
            TemperatureKelvin = temperatureKelvin;
            UseTemperature = useTemperature;
            CastShadows = castShadows;
            MaxDrawDistance = maxDrawDistance;
            IesPathName = iesPathName;
            UseIesBrightness = useIesBrightness;
            IesBrightnessScale = iesBrightnessScale;
        }
    }

}

namespace FoamWorkbench.Services;

public static class PorousPresetFactory
{
    public const string TreeShieldSevenLayerId = "treeshield-7-layer";
    public const string ProposalRainfallId = "treeshield-proposal-2026-08-18-rainfall";
    public const string ProposalGravityDrainageId = "treeshield-proposal-2026-08-18-gravity-drainage";
    public const string ProposalWaterHeadId = "treeshield-proposal-2026-08-18-water-head";

    private const string ProposalReference =
        "트리쉴드필터 CFD해석 제안서 간략판 (전우준, 2026-08-18), §2-6; " +
        "투과율은 §4의 잠정값이며 문헌 또는 실험값으로 교체 필요";

    public static IReadOnlyList<PorousBuiltInPreset> CreateBuiltInPresets() =>
    [
        new(ProposalRainfallId,
            "연구 표준 A · 강우 20 mm/hr (Steady)",
            "기본 표준입니다. PDF의 80 x 25 mm, 7-zone, 강우 20 mm/hr 조건을 적용합니다."),
        new(ProposalWaterHeadId,
            "연구 표준 B · 수두 50 mm (Transient)",
            "PDF의 동일 7-zone 필터에 50 mm 수두(약 490 Pa)를 적용하는 비정상 해석입니다."),
        new(ProposalGravityDrainageId,
            "연구 보조 · 중력 배수 (Steady)",
            "같은 25 mm 필터에서 상·하단 압력을 동일하게 두고 중력만으로 Qgravity/Ugravity를 계산합니다."),
        new(TreeShieldSevenLayerId,
            "사용자 입력형 · Abaca-Bamboo 7층",
            "별도 사용자 설계입니다. 두께·투과율은 정의되지 않으며 연구 표준 A/B와 혼합하지 않습니다.")
    ];

    public static PorousCaseSettings CreateBuiltInSettings(string presetId) => presetId switch
    {
        ProposalRainfallId => CreateProposalSettings(PorousFlowMode.RainfallFlux, PorousSimulationType.Steady),
        ProposalGravityDrainageId => CreateProposalSettings(PorousFlowMode.GravityDrainage, PorousSimulationType.Steady),
        ProposalWaterHeadId => CreateProposalSettings(PorousFlowMode.WaterHead, PorousSimulationType.Transient),
        _ => new PorousCaseSettings
        {
            PresetId = TreeShieldSevenLayerId,
            PresetName = "TreeShield 7-Layer",
            ProjectName = "TreeShieldPorous",
            Layers = CreateTreeShieldSevenLayer()
        }
    };

    public static IReadOnlyList<PorousLayer> CreateTreeShieldSevenLayer() =>
    [
        Layer(1, "layer1_abaca", "아바카 섬유 부직포", "Abaca Fiber Nonwoven",
            PorousMaterialCategory.FiberMembrane, "Thin membrane", "#E8D5B5",
            "light beige natural-fibre nonwoven sheet"),
        Layer(2, "layer2_vermiculite", "버미큘라이트", "Vermiculite",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", "#B7853D",
            "golden-brown irregular platy mineral particles"),
        Layer(3, "layer3_cotton", "면", "Cotton Fiber Pad",
            PorousMaterialCategory.FiberMembrane, "Thin membrane", "#F3F0E8",
            "soft dense off-white fibre pad"),
        Layer(4, "layer4_activatedCarbon", "활성탄", "Activated Carbon",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", "#17191D",
            "near-black rough porous granular carbon"),
        Layer(5, "layer5_coir", "코이어(코코넛) 섬유", "Coir Fiber Mat",
            PorousMaterialCategory.FiberMembrane, "Thin membrane", "#8A5835",
            "coarse natural-brown fibre mat"),
        Layer(6, "layer6_coffeeGrounds", "커피 찌꺼기", "Spent Coffee Grounds",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", "#4A2D22",
            "dark-brown irregular compressed coffee particles"),
        Layer(7, "layer7_bamboo", "대나무 섬유", "Bamboo Fiber Sheet",
            PorousMaterialCategory.FiberMembrane, "Thin membrane", "#D9DEA5",
            "pale yellow-green uniform natural-fibre sheet")
    ];

    public static IReadOnlyList<PorousLayer> CreateProposalSevenZoneLayers() =>
    [
        ProposalLayer(1, "1", "layer1_coirWovenMesh", "코이어 직조망", "Coir Woven Mesh",
            PorousMaterialCategory.FiberMembrane, "Woven fibre mesh", 4, 5.0e-10, 800, 2000,
            "#9B6A43", "coarse natural-brown woven coir mesh"),
        ProposalLayer(2, "2", "layer2_bananaNonwoven", "바나나섬유 부직포", "Banana Fiber Nonwoven",
            PorousMaterialCategory.FiberMembrane, "Fibre nonwoven", 3, 1.0e-10, 150, 300,
            "#D8BD82", "light tan banana-fibre nonwoven"),
        ProposalLayer(3, "3", "layer3_biocharActivatedCarbon", "바이오차+활성탄", "Biochar + Activated Carbon",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", 8, 1.2e-11, 100, 200,
            "#222326", "near-black porous carbon granules"),
        ProposalLayer(4, "4", "layer4a_cldh", "4A · CLDH", "4A · CLDH",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", 3, 5.0e-12, 50, 150,
            "#A88D70", "fine beige-brown CLDH bed"),
        ProposalLayer(5, "4", "layer4b_acidTreatedZeolite", "4B · 산처리 제올라이트", "4B · Acid-treated Zeolite",
            PorousMaterialCategory.GranularFill, "Granular / Thick fill layer", 3, 5.0e-12, 50, 150,
            "#C7B58E", "fine pale mineral granules"),
        ProposalLayer(6, "5", "layer5upper_bambooNonwoven", "5상 · 대나무 부직포", "5-upper · Bamboo Nonwoven",
            PorousMaterialCategory.FiberMembrane, "Fibre nonwoven", 1, 3.0e-12, 50, 100,
            "#D9DEA5", "pale yellow-green bamboo nonwoven"),
        ProposalLayer(7, "5", "layer5lower_coirDrainageMesh", "5하 · 코이어 배수망", "5-lower · Coir Drainage Mesh",
            PorousMaterialCategory.FiberMembrane, "Drainage fibre mesh", 3, 3.0e-10, 500, 1500,
            "#75482F", "open natural-brown coir drainage mesh")
    ];

    private static PorousCaseSettings CreateProposalSettings(
        PorousFlowMode flowMode,
        PorousSimulationType simulationType) => new()
    {
        PresetId = flowMode switch
        {
            PorousFlowMode.RainfallFlux => ProposalRainfallId,
            PorousFlowMode.GravityDrainage => ProposalGravityDrainageId,
            _ => ProposalWaterHeadId
        },
        PresetName = flowMode switch
        {
            PorousFlowMode.RainfallFlux => "PDF Proposal Scenario A - Rainfall",
            PorousFlowMode.GravityDrainage => "PDF Proposal - Gravity Drainage",
            _ => "PDF Proposal Scenario B - Water Head"
        },
        PresetSourceReference = ProposalReference,
        MinimumHydraulicConductivity = 5.56e-6,
        CfdAnalyticalTolerancePercent = 10,
        ProjectName = flowMode switch
        {
            PorousFlowMode.RainfallFlux => "TreeShieldProposalRainfall",
            PorousFlowMode.GravityDrainage => "TreeShieldProposalGravityDrainage",
            _ => "TreeShieldProposalWaterHead"
        },
        DomainWidthMm = 80,
        Layers = CreateProposalSevenZoneLayers(),
        Density = 998.2,
        DynamicViscosity = 1.003e-3,
        GravityEnabled = true,
        GravityX = 0,
        GravityY = -9.81,
        GravityZ = 0,
        FlowMode = flowMode,
        RainfallMmPerHour = 20,
        WaterHeadMm = 50,
        SimulationType = simulationType,
        MeshPreset = PorousMeshPreset.Medium,
        TargetCellSizeMm = 0.25,
        MinimumCellsPerLayer = 4,
        EndTime = 400,
        WriteInterval = simulationType == PorousSimulationType.Transient ? 10 : 40,
        DeltaT = 0.001,
        ProcessCount = 1
    };

    private static PorousLayer ProposalLayer(
        int id,
        string designGroup,
        string name,
        string ko,
        string en,
        PorousMaterialCategory category,
        string materialType,
        double thickness,
        double permeability,
        double poreSizeMin,
        double poreSizeMax,
        string color,
        string texture) => new()
    {
        Id = id,
        DesignGroup = designGroup,
        Name = name,
        DisplayNameKo = ko,
        DisplayNameEn = en,
        Category = category,
        MaterialType = materialType,
        Thickness = thickness,
        ThicknessUnit = "mm",
        PermeabilityType = PorousPermeabilityType.Isotropic,
        Permeability = permeability,
        ForchheimerCoefficient = 0,
        Porosity = null,
        PoreSizeMin = poreSizeMin,
        PoreSizeMax = poreSizeMax,
        ParticleSize = null,
        ParameterSource = PorousParameterSource.Estimated,
        ParameterSourceReference = ProposalReference,
        VisualMetadata = new PorousVisualMetadata(color, texture, texture)
    };

    private static PorousLayer Layer(
        int id,
        string name,
        string ko,
        string en,
        PorousMaterialCategory category,
        string materialType,
        string color,
        string texture) => new()
    {
        Id = id,
        DesignGroup = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Name = name,
        DisplayNameKo = ko,
        DisplayNameEn = en,
        Category = category,
        MaterialType = materialType,
        Thickness = null,
        ThicknessUnit = "mm",
        PermeabilityType = PorousPermeabilityType.Isotropic,
        Permeability = null,
        PermeabilityX = null,
        PermeabilityY = null,
        PermeabilityZ = null,
        ForchheimerCoefficient = 0,
        Porosity = null,
        PoreSizeMin = null,
        PoreSizeMax = null,
        ParticleSize = null,
        ParameterSource = PorousParameterSource.Undefined,
        ParameterSourceReference = "",
        VisualMetadata = new PorousVisualMetadata(color, texture, texture)
    };
}

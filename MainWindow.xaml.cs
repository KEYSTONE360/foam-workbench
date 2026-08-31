using FoamWorkbench.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FoamWorkbench;

public partial class MainWindow : Window
{
    private readonly ProcessRunner _runner = new();
    private readonly ResidualParser _residualParser = new();
    private readonly List<ResidualSample> _residualSamples = [];
    private readonly DispatcherTimer _jobTimer;
    private readonly Stopwatch _jobWatch = new();
    private AppSettings _settings;
    private OpenFoamService _openFoam;
    private CaseInfo? _caseInfo;
    private CancellationTokenSource? _jobCancellation;
    private string? _editorPath;
    private bool _editorLoading;
    private bool _editorDirty;
    private bool _ignoreTreeSelection;
    private TreeViewItem? _lastTreeSelection;
    private readonly StringBuilder _sessionLog = new();
    private string[] _runtimeCatalogAll = [];
    private bool _pythonPlotRunning;
    private PorousCaseSettings _activePorousPreset =
        PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalRainfallId);
    private PorousCaseSettings? _porousLastSettings;
    private PorousResultSummary? _porousLastResult;
    private string? _porousCasePath;
    private bool _syncingPorousFlowMode;
    private double _activePorousEndTime = double.NaN;
    private double _lastReportedPorousTime = double.NegativeInfinity;
    private readonly object _liveOutputLock = new();
    private readonly StringBuilder _pendingConsoleOutput = new();
    private bool _consoleFlushQueued;
    private const int MaximumLiveResidualSamples = 100_000;
    private const int MaximumSessionLogCharacters = 16_000_000;
    private static readonly Regex SolverTimeRegex = new(
        @"^\s*Time\s*=\s*(?<time>[-+0-9.eE]+)", RegexOptions.Compiled);

    public ObservableCollection<PipelineStep> PipelineSteps { get; } = [];
    public ObservableCollection<ResidualSummary> ResidualSummaries { get; } = [];
    public ObservableCollection<string> RuntimeCatalogItems { get; } = [];
    public ObservableCollection<PorousLayer> PorousLayers { get; } =
        new(PorousPresetFactory.CreateProposalSevenZoneLayers());
    public IReadOnlyList<PorousBuiltInPreset> PorousBuiltInPresets { get; } =
        PorousPresetFactory.CreateBuiltInPresets();
    public IReadOnlyList<PorousPermeabilityType> PorousPermeabilityTypes { get; } =
        Enum.GetValues<PorousPermeabilityType>();
    public IReadOnlyList<PorousParameterSource> PorousParameterSources { get; } =
        Enum.GetValues<PorousParameterSource>();
    public IReadOnlyList<PorousFlowMode> PorousFlowModes { get; } =
        Enum.GetValues<PorousFlowMode>();
    public IReadOnlyList<PorousSimulationType> PorousSimulationTypes { get; } =
        Enum.GetValues<PorousSimulationType>();
    public IReadOnlyList<PorousMeshPreset> PorousMeshPresets { get; } =
        Enum.GetValues<PorousMeshPreset>();

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _openFoam = new OpenFoamService(_settings, _runner);
        DataContext = this;
        SetPorousFlowMode(PorousFlowMode.RainfallFlux);
        PorousPreview.Layers = PorousLayers;
        PorousLayers.CollectionChanged += PorousLayers_CollectionChanged;
        foreach (var layer in PorousLayers) AttachPorousLayer(layer);
        PorousLayerGrid.SelectedIndex = 0;

        CreatePipeline();
        LoadSettingsIntoUi();
        var standardScenarioA = PorousPresetFactory
            .CreateBuiltInSettings(PorousPresetFactory.ProposalRainfallId)
            .CloneWith(outputRootPath: _settings.LastOutputRoot);
        ApplyPorousSettings(standardScenarioA);
        PorousPresetStatusText.Text =
            "연구 표준 A 적용 · 80 x 25 mm · 7-zone · 강우 20 mm/hr · 잠정 투과율은 Estimated입니다.";

        _runner.OutputReceived += Runner_OutputReceived;
        _residualParser.SampleParsed += ResidualParser_SampleParsed;

        _jobTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _jobTimer.Tick += (_, _) =>
            JobTimeText.Text = _jobWatch.IsRunning ? $"경과 {_jobWatch.Elapsed:hh\\:mm\\:ss}" : "실행 중인 프로세스 없음";

        Loaded += async (_, _) =>
        {
            await ProbeRuntimeAsync();
            await LoadRuntimeCatalogAsync("-functionObjects", showErrors: false);
            var startupArgument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(startupArgument)) return;

            var startupPath = Path.GetFullPath(startupArgument);
            if (File.Exists(startupPath) &&
                Path.GetExtension(startupPath).Equals(".foam", StringComparison.OrdinalIgnoreCase))
                startupPath = Path.GetDirectoryName(startupPath)!;

            if (File.Exists(startupPath) &&
                new[] { ".step", ".stp", ".iges", ".igs", ".brep", ".stl" }
                    .Contains(Path.GetExtension(startupPath), StringComparer.OrdinalIgnoreCase))
            {
                CadFileBox.Text = startupPath;
                CadProjectNameBox.Text = Path.GetFileNameWithoutExtension(startupPath);
                _settings.LastCadDirectory = Path.GetDirectoryName(startupPath) ?? "";
                CadStatusText.Text = $"CAD 선택됨: {Path.GetFileName(startupPath)}";
                ExternalCadTab.IsSelected = true;
                return;
            }

            if (Directory.Exists(startupPath)) OpenCase(startupPath);
        };
    }

    private void CreatePipeline()
    {
        PipelineSteps.Add(new PipelineStep
        {
            Title = "기본 메시",
            Command = "blockMesh",
            Description = "system/blockMeshDict로 기본 격자를 생성합니다.",
            IsEnabled = true
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "표면 특징선",
            Command = "surfaceFeatures",
            Description = "surfaceFeaturesDict에 따라 OpenFOAM 14 특징선을 추출합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "형상 적합 메시",
            Command = "snappyHexMesh -overwrite",
            Description = "snappyHexMeshDict 설정 전체를 사용해 castellate, snap, layers를 수행합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "내부유동 패치 분리",
            Command = "createPatch -overwrite",
            Description = "닫힌 유체 체적의 축 양 끝 평면을 inlet/outlet 패치로 분리합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "메시 정밀 검사",
            Command = "checkMesh -allTopology -allGeometry",
            Description = "토폴로지와 기하 품질 항목을 모두 검사합니다.",
            IsEnabled = true
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "영역 분할",
            Command = "decomposePar -force",
            Description = "decomposeParDict에 따라 병렬 계산 영역을 생성합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "솔버 · 직렬",
            Command = "$SOLVER",
            Description = "controlDict의 application/solver로 실제 OpenFOAM 솔버를 실행합니다.",
            IsEnabled = true
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "솔버 · MPI 병렬",
            Command = "$SOLVER_PARALLEL",
            Description = "설정한 프로세스 수로 원본 OpenFOAM MPI 계산을 실행합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "병렬 결과 병합",
            Command = "reconstructPar",
            Description = "processor 디렉터리의 계산 결과를 단일 케이스로 병합합니다.",
            IsEnabled = false
        });
        PipelineSteps.Add(new PipelineStep
        {
            Title = "후처리 함수",
            Command = "postProcess -func residuals",
            Description = "controlDict/functions 및 지정 functionObject를 실행합니다.",
            IsEnabled = false
        });
    }

    private void LoadSettingsIntoUi()
    {
        BackendCombo.SelectedIndex = _settings.Backend == RuntimeBackend.Wsl ? 0 : 1;
        WslDistributionBox.Text = _settings.WslDistribution;
        BashrcBox.Text = _settings.OpenFoamBashrc;
        DockerImageBox.Text = _settings.DockerImage;
        ParaViewPathBox.Text = _settings.ParaViewPath;
        ProcessCountBox.Text = _settings.ProcessCount.ToString();
        OutputRootBox.Text = !string.IsNullOrWhiteSpace(_settings.LastOutputRoot)
            ? _settings.LastOutputRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FoamWorkbenchResults");
        PorousOutputRootBox.Text = OutputRootBox.Text;
        UpdateBackendPanels();
        UpdateAnalysisPanels();
        UpdateForceDirectionInputs();
        UpdateTurbulenceResultAvailability();
        UpdatePorousUiSummary();
    }

    private bool SaveSettingsFromUi(bool showConfirmation)
    {
        if (!int.TryParse(ProcessCountBox.Text, out var processCount) || processCount < 1)
        {
            MessageBox.Show(this, "병렬 프로세스 수는 1 이상의 정수여야 합니다.", "설정 확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.Backend = BackendCombo.SelectedIndex == 1 ? RuntimeBackend.Docker : RuntimeBackend.Wsl;
        _settings.WslDistribution = WslDistributionBox.Text.Trim();
        _settings.OpenFoamBashrc = BashrcBox.Text.Trim();
        _settings.DockerImage = DockerImageBox.Text.Trim();
        _settings.ParaViewPath = ParaViewPathBox.Text.Trim();
        _settings.ProcessCount = processCount;
        SettingsStore.Save(_settings);
        _openFoam = new OpenFoamService(_settings, _runner);
        EngineDetailText.Text = _settings.Backend == RuntimeBackend.Wsl
            ? $"WSL2 / {_settings.WslDistribution}"
            : $"Docker / {_settings.DockerImage}";

        if (showConfirmation) SetFooter("런타임 설정을 저장했습니다.");
        return true;
    }

    private async void ProbeRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettingsFromUi(false)) return;
        await ProbeRuntimeAsync();
    }

    private async Task ProbeRuntimeAsync()
    {
        if (_runner.IsRunning) return;
        SetFooter("실제 OpenFOAM 실행 파일을 탐지하는 중…");
        EngineStatusText.Text = "확인 중";
        EngineDot.Fill = (Brush)FindResource("WarningBrush");

        var probe = await _openFoam.ProbeAsync();
        if (probe.IsAvailable)
        {
            EngineStatusText.Text = probe.Version;
            EngineDot.Fill = (Brush)FindResource("AccentBrush");
            SetFooter($"OpenFOAM 엔진 연결됨: {probe.Version}");
        }
        else
        {
            EngineStatusText.Text = "설치 필요";
            EngineDot.Fill = (Brush)FindResource("DangerBrush");
            SetFooter("WSL 런타임에서 OpenFOAM 또는 Gmsh를 찾지 못했습니다. README의 설치 절차를 확인하세요.");
        }
    }

    private async void LoadRuntimeCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettingsFromUi(false) || _runner.IsRunning) return;
        var option = (CatalogCategoryCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                     ?? "-functionObjects";
        await LoadRuntimeCatalogAsync(option, showErrors: true);
    }

    private async Task LoadRuntimeCatalogAsync(string option, bool showErrors)
    {
        if (_runner.IsRunning) return;
        CatalogStatusText.Text = "OpenFOAM 런타임 선택 테이블을 읽는 중…";
        RuntimeCatalogItems.Clear();

        try
        {
            var result = await _openFoam.ReadRuntimeCatalogAsync(option);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.Output.Trim());

            _runtimeCatalogAll = result.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 &&
                               !line.StartsWith("Contents of table", StringComparison.OrdinalIgnoreCase) &&
                               !line.EndsWith(':') &&
                               line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplyRuntimeCatalogFilter();
            CatalogStatusText.Text = $"현재 엔진에서 {_runtimeCatalogAll.Length:N0}개 항목을 확인했습니다.";
            RuntimeInfoBox.Text = result.Output;
        }
        catch (Exception ex)
        {
            _runtimeCatalogAll = [];
            CatalogStatusText.Text = "카탈로그를 읽지 못했습니다.";
            RuntimeInfoBox.Text = ex.Message;
            if (showErrors)
                MessageBox.Show(this, ex.Message, "OpenFOAM 카탈로그", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CatalogSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyRuntimeCatalogFilter();

    private void ApplyRuntimeCatalogFilter()
    {
        if (RuntimeCatalogItems is null || CatalogSearchBox is null) return;
        var filter = CatalogSearchBox.Text.Trim();
        RuntimeCatalogItems.Clear();
        foreach (var item in _runtimeCatalogAll.Where(item =>
                     filter.Length == 0 || item.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            RuntimeCatalogItems.Add(item);
    }

    private void RuntimeCatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = RuntimeCatalogList.SelectedItem?.ToString();
        CatalogSelectionText.Text = string.IsNullOrWhiteSpace(selection)
            ? "항목을 선택하세요"
            : selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private async void LookupRuntimeInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning || RuntimeCatalogList.SelectedItem is not string selection) return;
        var name = selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        RuntimeInfoBox.Text = $"{name} 정보를 OpenFOAM에서 찾는 중…";
        try
        {
            var result = await _openFoam.ReadRuntimeInfoAsync(name);
            RuntimeInfoBox.Text = string.IsNullOrWhiteSpace(result.Output)
                ? $"{name}: foamInfo가 별도 설명이나 예제 경로를 반환하지 않았습니다."
                : result.Output;
        }
        catch (Exception ex)
        {
            RuntimeInfoBox.Text = ex.Message;
        }
    }

    private void OpenCase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "OpenFOAM 케이스 폴더 선택",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        OpenCase(dialog.FolderName);
    }

    private void BrowseCad_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "CFD 형상 CAD 선택",
            Filter = "3D CAD (*.step;*.stp;*.iges;*.igs;*.brep;*.stl)|*.step;*.stp;*.iges;*.igs;*.brep;*.stl|" +
                     "STEP (*.step;*.stp)|*.step;*.stp|IGES (*.iges;*.igs)|*.iges;*.igs|" +
                     "BREP (*.brep)|*.brep|STL (*.stl)|*.stl",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_settings.LastCadDirectory) &&
            Directory.Exists(_settings.LastCadDirectory))
            dialog.InitialDirectory = _settings.LastCadDirectory;

        if (dialog.ShowDialog(this) != true) return;
        CadFileBox.Text = dialog.FileName;
        _settings.LastCadDirectory = Path.GetDirectoryName(dialog.FileName) ?? "";
        if (CadProjectNameBox.Text is "NewCfdProject" or "")
            CadProjectNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        SettingsStore.Save(_settings);
        CadStatusText.Text = $"CAD 선택됨: {Path.GetFileName(dialog.FileName)}";
    }

    private async void PreviewCad_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "다른 OpenFOAM 작업이 실행 중입니다.", "3D 미리보기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!File.Exists(CadFileBox.Text))
        {
            MessageBox.Show(this, "먼저 STEP/IGES/BREP/STL CAD 파일을 선택하세요.", "3D 미리보기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveSettingsFromUi(false)) return;

        double surfaceSize;
        try
        {
            surfaceSize = ParseDouble(CadSurfaceSizeBox.Text, "CAD 표면 요소 크기");
            if (surfaceSize <= 0) throw new ArgumentException("CAD 표면 요소 크기는 0보다 커야 합니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "3D 미리보기 설정",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _jobCancellation = new CancellationTokenSource();
        CadStatusText.Text = "CAD 3D 미리보기를 준비하는 중…";
        SetFooter("Gmsh/OpenCASCADE로 방향 확인용 CAD 표면을 준비하는 중…");

        try
        {
            var unit = CadUnitCombo.SelectedIndex == 1
                ? CadLengthUnit.Metre
                : CadLengthUnit.Millimetre;
            var axis = (FlowAxis)Math.Clamp(FlowAxisCombo.SelectedIndex, 0, 5);
            var service = new CadPreviewService(_openFoam);
            var previewData = await service.BuildAsync(
                CadFileBox.Text, unit, surfaceSize, _jobCancellation.Token);
            AppendConsole($"\n━━ CAD 3D 미리보기 변환 ━━\n{previewData.ConversionOutput}\n", false);

            CadStatusText.Text =
                $"미리보기 준비 완료 · {previewData.OriginalTriangleCount:N0} 삼각형 · 유동 {FlowAxisCombo.Text}";
            SetFooter("3D 미리보기에서 모델 방향과 유동 화살표를 확인하세요.");
            new CadPreviewWindow(previewData, axis) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            CadStatusText.Text = "3D 미리보기 준비를 중지했습니다.";
            SetFooter("미리보기 중지");
        }
        catch (Exception ex)
        {
            CadStatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "3D 미리보기 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _jobCancellation.Dispose();
            _jobCancellation = null;
        }
    }

    private void BrowseOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "OpenFOAM 케이스와 솔버 결과를 저장할 폴더 선택",
            Multiselect = false,
            InitialDirectory = Directory.Exists(OutputRootBox.Text) ? OutputRootBox.Text : null
        };
        if (dialog.ShowDialog(this) != true) return;
        OutputRootBox.Text = dialog.FolderName;
        _settings.LastOutputRoot = dialog.FolderName;
        SettingsStore.Save(_settings);
        CadStatusText.Text = $"결과 위치: {dialog.FolderName}";
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(_settings.LastOutputRoot)
            ? _settings.LastOutputRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new SaveFileDialog
        {
            Title = "메시·계산 설정 프리셋 저장",
            Filter = "FoamWorkbench 설정 프리셋 (*.fwpreset.json)|*.fwpreset.json|JSON 파일 (*.json)|*.json",
            DefaultExt = ".fwpreset.json",
            AddExtension = true,
            FileName = "FoamWorkbench-Mesh-Calculation.fwpreset.json",
            InitialDirectory = initialDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var fileName = Path.GetFileName(dialog.FileName);
            var presetName = fileName.EndsWith(".fwpreset.json", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".fwpreset.json".Length]
                : Path.GetFileNameWithoutExtension(fileName);
            var preset = ReadMeshCalculationPreset(presetName);
            MeshCalculationPresetStore.Save(dialog.FileName, preset);
            PresetStatusText.Text = $"저장됨: {preset.Name} · CAD/결과 경로와 프로젝트명은 포함하지 않음";
            SetFooter($"설정 프리셋을 저장했습니다: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "프리셋 저장 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(_settings.LastOutputRoot)
            ? _settings.LastOutputRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new OpenFileDialog
        {
            Title = "메시·계산 설정 프리셋 불러오기",
            Filter = "FoamWorkbench 설정 프리셋 (*.fwpreset.json)|*.fwpreset.json|JSON 파일 (*.json)|*.json",
            Multiselect = false,
            InitialDirectory = initialDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var preset = MeshCalculationPresetStore.Load(dialog.FileName);
            ApplyMeshCalculationPreset(preset);
            var savedAt = preset.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            PresetStatusText.Text = $"불러옴: {preset.Name} · 저장 시각 {savedAt}";
            SetFooter("설정 프리셋을 적용했습니다. CAD 파일·프로젝트명·결과 폴더는 그대로 유지됩니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "프리셋 불러오기 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnalysisTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAnalysisPanels();

    private void UpdateAnalysisPanels()
    {
        if (FluidPointPanel is null || AnalysisTypeCombo is null) return;
        FluidPointPanel.Visibility = AnalysisTypeCombo.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenResultSettings_Click(object sender, RoutedEventArgs e) =>
        ResultSettingsTab.IsSelected = true;

    private void FlowAxisCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateForceDirectionInputs();

    private void UpdateForceDirectionInputs()
    {
        if (FlowAxisCombo is null || DragDirectionBox is null ||
            LiftDirectionBox is null || PitchAxisBox is null) return;

        var directions = (FlowAxis)Math.Clamp(FlowAxisCombo.SelectedIndex, 0, 5) switch
        {
            FlowAxis.PositiveX => ("1 0 0", "0 0 1", "0 1 0"),
            FlowAxis.NegativeX => ("-1 0 0", "0 0 1", "0 -1 0"),
            FlowAxis.PositiveY => ("0 1 0", "0 0 1", "-1 0 0"),
            FlowAxis.NegativeY => ("0 -1 0", "0 0 1", "1 0 0"),
            FlowAxis.PositiveZ => ("0 0 1", "0 1 0", "1 0 0"),
            _ => ("0 0 -1", "0 1 0", "-1 0 0")
        };

        DragDirectionBox.Text = directions.Item1;
        LiftDirectionBox.Text = directions.Item2;
        PitchAxisBox.Text = directions.Item3;
    }

    private void TurbulenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTurbulenceResultAvailability();

    private void UpdateTurbulenceResultAvailability()
    {
        if (TurbulenceCombo is null || YPlusCheck is null || TurbulenceIntensityCheck is null) return;
        var turbulent = TurbulenceCombo.SelectedIndex != 1;
        YPlusCheck.IsEnabled = turbulent;
        TurbulenceIntensityCheck.IsEnabled = turbulent;
        if (!turbulent)
        {
            YPlusCheck.IsChecked = false;
            TurbulenceIntensityCheck.IsChecked = false;
            YPlusCheck.ToolTip = "y+는 난류 모델이 활성화된 경우에 계산할 수 있습니다.";
            TurbulenceIntensityCheck.ToolTip = "난류 강도장은 난류 모델이 활성화된 경우에 계산할 수 있습니다.";
        }
        else
        {
            YPlusCheck.ToolTip = null;
            TurbulenceIntensityCheck.ToolTip = null;
        }
    }

    private async void GenerateCadCase_Click(object sender, RoutedEventArgs e) =>
        await GenerateCadProjectAsync(runMesh: false, runSolver: false);

    private async void GenerateAndMesh_Click(object sender, RoutedEventArgs e) =>
        await GenerateCadProjectAsync(runMesh: true, runSolver: false);

    private async void GenerateMeshAndSolve_Click(object sender, RoutedEventArgs e) =>
        await GenerateCadProjectAsync(runMesh: true, runSolver: true);

    private async Task GenerateCadProjectAsync(bool runMesh, bool runSolver)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "다른 OpenFOAM 작업이 실행 중입니다.", "작업 실행 중",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveSettingsFromUi(false)) return;

        CadProjectSettings cad;
        try
        {
            cad = ReadCadSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CAD 프로젝트 설정 확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.LastOutputRoot = cad.OutputRootPath;
        _settings.LastCadDirectory = Path.GetDirectoryName(cad.CadFilePath) ?? "";
        SettingsStore.Save(_settings);
        _jobCancellation = new CancellationTokenSource();
        _sessionLog.Clear();
        ResetResidualMonitor("새 CAD 계산의 잔차를 기다리는 중입니다.");
        CadStatusText.Text = "CAD를 읽고 OpenFOAM 케이스를 생성하는 중…";
        StartJobUi("CAD 프로젝트 생성");

        try
        {
            var generator = new CadCaseGenerator(_openFoam);
            var generated = await generator.GenerateAsync(cad, _jobCancellation.Token);
            AppendConsole($"\n━━ CAD 변환 및 표면 검사 ━━\n{generated.ConversionOutput}\n", false);
            AppendConsole(
                $"형상 경계 [m]: {generated.GeometryBounds.Min} .. {generated.GeometryBounds.Max}\n" +
                $"계산영역 [m]: {generated.DomainBounds.Min} .. {generated.DomainBounds.Max}\n" +
                $"예상 배경 셀: {generated.BaseCellCount:N0}\n", false);

            OpenCase(generated.CasePath);
            ConfigureGeneratedPipeline(cad, runMesh, runSolver);
            CadStatusText.Text = $"생성 완료: {generated.CasePath}";

            if (runMesh)
            {
                var commands = new List<(string Title, string Command)>
                {
                    ("기본 메시", "blockMesh"),
                    ("표면 특징선", "surfaceFeatures"),
                    ("형상 적합 메시", "snappyHexMesh -overwrite")
                };
                if (cad.AnalysisType == CadAnalysisType.InternalFluidVolume)
                    commands.Add(("내부유동 inlet/outlet 분리", "createPatch -overwrite"));
                commands.Add(("메시 정밀 검사", "checkMesh -allTopology -allGeometry"));
                if (runSolver) commands.Add(("원본 OpenFOAM 솔버", "foamRun"));

                foreach (var (title, command) in commands)
                {
                    _jobCancellation.Token.ThrowIfCancellationRequested();
                    CadStatusText.Text = $"{title} 실행 중…";
                    AppendConsole($"\n━━ {title} ━━\n$ {command}\n", false);
                    var result = await _openFoam.RunCaseCommandAsync(
                        generated.CasePath, command, _jobCancellation.Token);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException(
                            $"{title} 단계가 종료 코드 {result.ExitCode}(으)로 실패했습니다. 콘솔 로그를 확인하세요.");
                    if (command.StartsWith("checkMesh", StringComparison.Ordinal) &&
                        result.Output.Contains("Failed ", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendConsole(
                            "\n[메시 진단] allTopology/allGeometry 확장 검사에 경고가 있습니다. " +
                            "OpenFOAM의 기본 솔버 적합성 검사를 추가로 실행합니다.\n", true);
                        var solverMeshCheck = await _openFoam.RunCaseCommandAsync(
                            generated.CasePath, "checkMesh", _jobCancellation.Token);
                        AppendConsole(solverMeshCheck.Output + "\n", solverMeshCheck.ExitCode != 0);
                        if (solverMeshCheck.ExitCode != 0 ||
                            !solverMeshCheck.Output.Contains("Mesh OK", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                "OpenFOAM 기본 checkMesh를 통과하지 못해 솔버를 자동 시작하지 않았습니다.");
                        AppendConsole(
                            "[메시 진단] 기본 checkMesh는 Mesh OK입니다. 확장 검사 경고는 로그에 보존하고 계산을 계속합니다.\n",
                            true);
                    }
                }
            }

            var completion = runSolver
                ? "CAD → 메시 → 솔버 계산 완료"
                : runMesh ? "CAD → 메시 생성 및 검사 완료" : "OpenFOAM 케이스 생성 완료";
            CadStatusText.Text = completion + $"\n{generated.CasePath}";
            CompleteJobUi(completion, true);

            if (runSolver)
            {
                _openFoam.OpenParaView(generated.CasePath);
                SetFooter("계산을 완료하고 Windows ParaView에서 결과를 열었습니다.");
            }
        }
        catch (OperationCanceledException)
        {
            CadStatusText.Text = "사용자가 작업을 중지했습니다.";
            CompleteJobUi("CAD 프로젝트 작업 중지", false);
        }
        catch (Exception ex)
        {
            AppendConsole($"\n[CAD 프로젝트 오류] {ex.Message}\n", true);
            CadStatusText.Text = ex.Message;
            CompleteJobUi("CAD 프로젝트 실패", false);
            MessageBox.Show(this, ex.Message, "CAD 프로젝트 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveSessionLog();
            _jobCancellation.Dispose();
            _jobCancellation = null;
        }
    }

    private CadProjectSettings ReadCadSettings()
    {
        if (!File.Exists(CadFileBox.Text))
            throw new FileNotFoundException("STEP/IGES/BREP/STL CAD 파일을 선택하세요.");
        if (string.IsNullOrWhiteSpace(OutputRootBox.Text))
            throw new ArgumentException("케이스와 결과를 저장할 폴더를 선택하세요.");
        Directory.CreateDirectory(OutputRootBox.Text);

        var preset = ReadMeshCalculationPreset("현재 설정");
        return preset.ToProjectSettings(
            Path.GetFullPath(CadFileBox.Text),
            Path.GetFullPath(OutputRootBox.Text),
            CadProjectNameBox.Text.Trim());
    }

    private MeshCalculationPreset ReadMeshCalculationPreset(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Mesh and calculation preset" : name.Trim(),
        SavedAtUtc = DateTimeOffset.UtcNow,
        CadUnit = CadUnitCombo.SelectedIndex == 1 ? CadLengthUnit.Metre : CadLengthUnit.Millimetre,
        AnalysisType = AnalysisTypeCombo.SelectedIndex == 1
            ? CadAnalysisType.InternalFluidVolume
            : CadAnalysisType.ExternalFlow,
        FlowAxis = (FlowAxis)Math.Clamp(FlowAxisCombo.SelectedIndex, 0, 5),
        Turbulence = TurbulenceCombo.SelectedIndex == 1
            ? TurbulenceChoice.Laminar
            : TurbulenceChoice.KOmegaSst,
        Velocity = ParseDouble(VelocityBox.Text, "입구 속도"),
        KinematicViscosity = ParseDouble(NuBox.Text, "동점성계수"),
        TurbulenceIntensityPercent = ParseDouble(IntensityBox.Text, "난류 강도"),
        TurbulenceLengthScale = ParseDouble(TurbLengthScaleBox.Text, "난류 길이 척도"),
        CadSurfaceSize = ParseDouble(CadSurfaceSizeBox.Text, "CAD 표면 요소 크기"),
        BaseCellSize = ParseDouble(BaseCellSizeBox.Text, "배경 셀 크기"),
        SurfaceRefinementMin = ParseInt(SurfaceMinBox.Text, "표면 최소 레벨"),
        SurfaceRefinementMax = ParseInt(SurfaceMaxBox.Text, "표면 최대 레벨"),
        FeatureRefinementLevel = ParseInt(FeatureLevelBox.Text, "특징선 레벨"),
        BoundaryLayerCount = ParseInt(LayerCountBox.Text, "경계층 수"),
        LayerExpansionRatio = ParseDouble(LayerExpansionBox.Text, "층 팽창률"),
        FinalLayerThickness = ParseDouble(FinalLayerThicknessBox.Text, "최종 층 상대 두께"),
        MaxGlobalCells = ParseInt(MaxGlobalCellsBox.Text, "최대 전체 셀 수"),
        UpstreamLengths = ParseDouble(UpstreamBox.Text, "상류 여유"),
        DownstreamLengths = ParseDouble(DownstreamBox.Text, "하류 여유"),
        SideLengths = ParseDouble(SideLengthsBox.Text, "측면 여유"),
        EndTime = ParseInt(EndTimeBox.Text, "최대 반복"),
        WriteInterval = ParseInt(WriteIntervalBox.Text, "결과 저장 간격"),
        ProcessCount = ParseInt(ProcessCountBox.Text, "병렬 프로세스 수"),
        FluidPointText = FluidPointBox.Text,
        CalculateResiduals = ResidualsCheck.IsChecked == true,
        CalculateForces = ForcesCheck.IsChecked == true,
        CalculateForceCoefficients = ForceCoeffsCheck.IsChecked == true,
        CalculateWallShearStress = WallShearStressCheck.IsChecked == true,
        CalculateYPlus = YPlusCheck.IsChecked == true,
        CalculateQCriterion = QCriterionCheck.IsChecked == true,
        CalculateVorticity = VorticityCheck.IsChecked == true,
        CalculateTurbulenceIntensity = TurbulenceIntensityCheck.IsChecked == true,
        CalculateFieldAverage = FieldAverageCheck.IsChecked == true,
        ForcePatches = ForcePatchesBox.Text.Trim(),
        Density = ParseDouble(DensityBox.Text, "유체 밀도"),
        ReferenceArea = ParseDouble(ReferenceAreaBox.Text, "기준 면적"),
        ReferenceLength = ParseDouble(ReferenceLengthBox.Text, "기준 길이"),
        CentreOfRotationText = CentreOfRotationBox.Text,
        DragDirectionText = DragDirectionBox.Text,
        LiftDirectionText = LiftDirectionBox.Text,
        PitchAxisText = PitchAxisBox.Text,
        AveragedFields = AveragedFieldsBox.Text,
        CustomFunctionObjects = CustomFunctionObjectsBox.Text
    };

    private void ApplyMeshCalculationPreset(MeshCalculationPreset preset)
    {
        CadUnitCombo.SelectedIndex = preset.CadUnit == CadLengthUnit.Metre ? 1 : 0;
        AnalysisTypeCombo.SelectedIndex = preset.AnalysisType == CadAnalysisType.InternalFluidVolume ? 1 : 0;
        FlowAxisCombo.SelectedIndex = (int)preset.FlowAxis;
        TurbulenceCombo.SelectedIndex = preset.Turbulence == TurbulenceChoice.Laminar ? 1 : 0;

        VelocityBox.Text = FormatPresetNumber(preset.Velocity);
        NuBox.Text = FormatPresetNumber(preset.KinematicViscosity);
        IntensityBox.Text = FormatPresetNumber(preset.TurbulenceIntensityPercent);
        TurbLengthScaleBox.Text = FormatPresetNumber(preset.TurbulenceLengthScale);
        CadSurfaceSizeBox.Text = FormatPresetNumber(preset.CadSurfaceSize);
        BaseCellSizeBox.Text = FormatPresetNumber(preset.BaseCellSize);
        SurfaceMinBox.Text = preset.SurfaceRefinementMin.ToString(CultureInfo.InvariantCulture);
        SurfaceMaxBox.Text = preset.SurfaceRefinementMax.ToString(CultureInfo.InvariantCulture);
        FeatureLevelBox.Text = preset.FeatureRefinementLevel.ToString(CultureInfo.InvariantCulture);
        LayerCountBox.Text = preset.BoundaryLayerCount.ToString(CultureInfo.InvariantCulture);
        LayerExpansionBox.Text = FormatPresetNumber(preset.LayerExpansionRatio);
        FinalLayerThicknessBox.Text = FormatPresetNumber(preset.FinalLayerThickness);
        MaxGlobalCellsBox.Text = preset.MaxGlobalCells.ToString(CultureInfo.InvariantCulture);
        UpstreamBox.Text = FormatPresetNumber(preset.UpstreamLengths);
        DownstreamBox.Text = FormatPresetNumber(preset.DownstreamLengths);
        SideLengthsBox.Text = FormatPresetNumber(preset.SideLengths);
        EndTimeBox.Text = preset.EndTime.ToString(CultureInfo.InvariantCulture);
        WriteIntervalBox.Text = preset.WriteInterval.ToString(CultureInfo.InvariantCulture);
        ProcessCountBox.Text = preset.ProcessCount.ToString(CultureInfo.InvariantCulture);
        FluidPointBox.Text = preset.FluidPointText ?? "";

        ResidualsCheck.IsChecked = preset.CalculateResiduals;
        ForcesCheck.IsChecked = preset.CalculateForces;
        ForceCoeffsCheck.IsChecked = preset.CalculateForceCoefficients;
        WallShearStressCheck.IsChecked = preset.CalculateWallShearStress;
        YPlusCheck.IsChecked = preset.CalculateYPlus;
        QCriterionCheck.IsChecked = preset.CalculateQCriterion;
        VorticityCheck.IsChecked = preset.CalculateVorticity;
        TurbulenceIntensityCheck.IsChecked = preset.CalculateTurbulenceIntensity;
        FieldAverageCheck.IsChecked = preset.CalculateFieldAverage;
        ForcePatchesBox.Text = preset.ForcePatches;
        DensityBox.Text = FormatPresetNumber(preset.Density);
        ReferenceAreaBox.Text = FormatPresetNumber(preset.ReferenceArea);
        ReferenceLengthBox.Text = FormatPresetNumber(preset.ReferenceLength);
        CentreOfRotationBox.Text = preset.CentreOfRotationText;
        DragDirectionBox.Text = preset.DragDirectionText;
        LiftDirectionBox.Text = preset.LiftDirectionText;
        PitchAxisBox.Text = preset.PitchAxisText;
        AveragedFieldsBox.Text = preset.AveragedFields;
        CustomFunctionObjectsBox.Text = preset.CustomFunctionObjects;

        UpdateAnalysisPanels();
        UpdateTurbulenceResultAvailability();
    }

    private static string FormatPresetNumber(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static double ParseDouble(string text, string label)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label} 값을 숫자로 입력하세요.");
        return value;
    }

    private static int ParseInt(string text, string label)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label} 값을 정수로 입력하세요.");
        return value;
    }

    private void ConfigureGeneratedPipeline(CadProjectSettings settings, bool runMesh, bool runSolver)
    {
        foreach (var step in PipelineSteps)
        {
            step.Status = "대기";
            step.IsEnabled = step.Command switch
            {
                "blockMesh" => runMesh,
                "surfaceFeatures" => runMesh,
                "snappyHexMesh -overwrite" => runMesh,
                "createPatch -overwrite" => runMesh &&
                                             settings.AnalysisType == CadAnalysisType.InternalFluidVolume,
                "checkMesh -allTopology -allGeometry" => runMesh,
                "$SOLVER" => runSolver,
                _ => false
            };
        }
    }

    private void OpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _caseInfo?.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            path = OutputRootBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(this, "먼저 결과 폴더를 선택하거나 케이스를 생성하세요.", "결과 폴더",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenCase(string path)
    {
        if (!ConfirmDiscardEditor()) return;

        _caseInfo = CaseInspector.Inspect(path);
        CaseNameText.Text = new DirectoryInfo(path).Name;
        CasePathText.Text = path;
        CasePathText.ToolTip = path;
        SolverText.Text = string.IsNullOrWhiteSpace(_caseInfo.Application)
            ? (string.IsNullOrWhiteSpace(_caseInfo.SolverModule) ? "감지 안 됨" : "foamRun")
            : _caseInfo.Application;
        SolverModuleText.Text = string.IsNullOrWhiteSpace(_caseInfo.SolverModule)
            ? "solver 모듈 항목 없음"
            : $"module: {_caseInfo.SolverModule}";

        ZeroStatus.Text = _caseInfo.HasZero ? "정상" : "누락";
        ConstantStatus.Text = _caseInfo.HasConstant ? "정상" : "누락";
        SystemStatus.Text = _caseInfo.HasSystem ? "정상" : "누락";
        ZeroStatus.Foreground = StatusBrush(_caseInfo.HasZero);
        ConstantStatus.Foreground = StatusBrush(_caseInfo.HasConstant);
        SystemStatus.Foreground = StatusBrush(_caseInfo.HasSystem);

        CaseValidationText.Text = _caseInfo.IsValid
            ? "표준 케이스 구조가 확인되었습니다. GUI는 기존 사전 내용을 변경하지 않습니다."
            : "필수 디렉터리가 누락되었습니다. 계산 전에 케이스 구조를 확인하세요.";

        LoadCaseTree();
        ResetEditor();
        var restored = RestoreLatestResidualLog(path);
        SetFooter(restored
            ? $"케이스를 열고 최신 잔차 로그를 복구했습니다: {path}"
            : $"케이스를 열었습니다: {path}");
    }

    private Brush StatusBrush(bool ok) =>
        (Brush)FindResource(ok ? "AccentBrush" : "DangerBrush");

    private void LoadCaseTree()
    {
        CaseFileTree.Items.Clear();
        if (_caseInfo is null) return;

        foreach (var directoryName in new[] { "0", "0.orig", "constant", "system" })
        {
            var path = Path.Combine(_caseInfo.Path, directoryName);
            if (!Directory.Exists(path)) continue;
            CaseFileTree.Items.Add(CreateDirectoryNode(new DirectoryInfo(path), 0));
        }

        foreach (var file in Directory.EnumerateFiles(_caseInfo.Path, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsTextCandidate)
                     .OrderBy(Path.GetFileName))
        {
            CaseFileTree.Items.Add(CreateFileNode(file));
        }
    }

    private TreeViewItem CreateDirectoryNode(DirectoryInfo directory, int depth)
    {
        var node = new TreeViewItem
        {
            Header = directory.Name,
            Tag = directory.FullName,
            FontWeight = FontWeights.SemiBold
        };

        if (depth > 8) return node;

        try
        {
            foreach (var childDirectory in directory.EnumerateDirectories()
                         .Where(d => !d.Name.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => d.Name))
                node.Items.Add(CreateDirectoryNode(childDirectory, depth + 1));

            foreach (var file in directory.EnumerateFiles().Where(f => IsTextCandidate(f.FullName)).OrderBy(f => f.Name))
                node.Items.Add(CreateFileNode(file.FullName));
        }
        catch (UnauthorizedAccessException)
        {
            // The remaining accessible case files are still displayed.
        }

        if (depth == 0) node.IsExpanded = true;
        return node;
    }

    private static TreeViewItem CreateFileNode(string path) => new()
    {
        Header = Path.GetFileName(path),
        Tag = path,
        FontFamily = new FontFamily("Consolas"),
        FontWeight = FontWeights.Normal
    };

    private static bool IsTextCandidate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 5 * 1024 * 1024) return false;
            var extension = info.Extension.ToLowerInvariant();
            return extension is not ".gz" and not ".zip" and not ".png" and not ".jpg" and not ".jpeg"
                and not ".vtk" and not ".vtp" and not ".stl" and not ".obj" and not ".bin";
        }
        catch
        {
            return false;
        }
    }

    private void CaseFileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_ignoreTreeSelection || e.NewValue is not TreeViewItem item || item.Tag is not string path || !File.Exists(path))
            return;

        if (!ConfirmDiscardEditor())
        {
            _ignoreTreeSelection = true;
            if (_lastTreeSelection is not null) _lastTreeSelection.IsSelected = true;
            _ignoreTreeSelection = false;
            return;
        }

        LoadEditor(path);
        _lastTreeSelection = item;
    }

    private void LoadEditor(string path)
    {
        try
        {
            _editorLoading = true;
            _editorPath = path;
            DictionaryEditor.Text = File.ReadAllText(path);
            DictionaryEditor.IsEnabled = true;
            EditorFileName.Text = _caseInfo is null ? path : Path.GetRelativePath(_caseInfo.Path, path);
            _editorDirty = false;
            EditorDirtyMark.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "파일 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _editorLoading = false;
        }
    }

    private void DictionaryEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editorLoading || _editorPath is null) return;
        _editorDirty = true;
        EditorDirtyMark.Visibility = Visibility.Visible;
    }

    private void SaveEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_editorPath is null) return;
        try
        {
            File.WriteAllText(_editorPath, DictionaryEditor.Text, new UTF8Encoding(false));
            _editorDirty = false;
            EditorDirtyMark.Visibility = Visibility.Collapsed;
            _caseInfo = CaseInspector.Inspect(_caseInfo!.Path);
            SolverText.Text = string.IsNullOrWhiteSpace(_caseInfo.Application)
                ? (string.IsNullOrWhiteSpace(_caseInfo.SolverModule) ? "감지 안 됨" : "foamRun")
                : _caseInfo.Application;
            SolverModuleText.Text = string.IsNullOrWhiteSpace(_caseInfo.SolverModule)
                ? "solver 모듈 항목 없음"
                : $"module: {_caseInfo.SolverModule}";
            SetFooter($"저장됨: {_editorPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_editorPath is null || !ConfirmDiscardEditor()) return;
        LoadEditor(_editorPath);
    }

    private bool ConfirmDiscardEditor()
    {
        if (!_editorDirty) return true;
        var result = MessageBox.Show(this, "저장하지 않은 사전 변경 사항을 버리시겠습니까?",
            "변경 사항 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void ResetEditor()
    {
        _editorLoading = true;
        _editorPath = null;
        _editorDirty = false;
        DictionaryEditor.Text = "";
        DictionaryEditor.IsEnabled = false;
        EditorFileName.Text = "파일을 선택하세요";
        EditorDirtyMark.Visibility = Visibility.Collapsed;
        _editorLoading = false;
    }

    private async void RunPipeline_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunCase()) return;
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "이미 실행 중인 작업이 있습니다.", "작업 실행",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_editorDirty)
        {
            MessageBox.Show(this, "계산 전에 열려 있는 사전 변경 사항을 저장하세요.", "저장 필요",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _caseInfo = CaseInspector.Inspect(_caseInfo!.Path);
        _jobCancellation = new CancellationTokenSource();
        _sessionLog.Clear();
        ResetResidualMonitor("새 파이프라인 계산의 잔차를 기다리는 중입니다.");
        StartJobUi("파이프라인 실행");

        try
        {
            foreach (var step in PipelineSteps.Where(s => s.IsEnabled))
            {
                _jobCancellation.Token.ThrowIfCancellationRequested();
                step.Status = "실행 중";
                var command = ResolvePipelineCommand(step.Command);
                AppendConsole($"\n━━ {step.Title} ━━\n$ {command}\n", false);
                var result = await _openFoam.RunCaseCommandAsync(_caseInfo.Path, command, _jobCancellation.Token);
                step.Status = result.ExitCode == 0 ? "완료" : $"실패 ({result.ExitCode})";
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"{step.Title}이 종료 코드 {result.ExitCode}(으)로 실패했습니다.");
            }

            CompleteJobUi("전체 파이프라인 완료", true);
        }
        catch (OperationCanceledException)
        {
            CompleteJobUi("사용자가 작업을 중지함", false);
        }
        catch (Exception ex)
        {
            AppendConsole($"\n[오류] {ex.Message}\n", true);
            CompleteJobUi("파이프라인 실패", false);
            MessageBox.Show(this, ex.Message, "OpenFOAM 실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveSessionLog();
            _jobCancellation.Dispose();
            _jobCancellation = null;
        }
    }

    private string ResolvePipelineCommand(string command)
    {
        if (_caseInfo is null) return command;
        return command switch
        {
            "$SOLVER" => _openFoam.ResolveSolverCommand(_caseInfo, false),
            "$SOLVER_PARALLEL" => _openFoam.ResolveSolverCommand(_caseInfo, true),
            _ => command
        };
    }

    private async void RunCustomCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunCase() || string.IsNullOrWhiteSpace(CustomCommandBox.Text) || _runner.IsRunning) return;
        _jobCancellation = new CancellationTokenSource();
        StartJobUi("사용자 명령 실행");
        AppendConsole($"\n━━ 사용자 명령 ━━\n$ {CustomCommandBox.Text}\n", false);

        try
        {
            var result = await _openFoam.RunCaseCommandAsync(_caseInfo!.Path, CustomCommandBox.Text,
                _jobCancellation.Token);
            CompleteJobUi(result.ExitCode == 0 ? "사용자 명령 완료" : $"명령 실패 ({result.ExitCode})",
                result.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            CompleteJobUi("사용자가 작업을 중지함", false);
        }
        catch (Exception ex)
        {
            AppendConsole($"\n[오류] {ex.Message}\n", true);
            CompleteJobUi("명령 실행 실패", false);
        }
        finally
        {
            SaveSessionLog();
            _jobCancellation.Dispose();
            _jobCancellation = null;
        }
    }

    private bool CanRunCase()
    {
        if (_caseInfo is null)
        {
            MessageBox.Show(this, "먼저 OpenFOAM 케이스 폴더를 여세요.", "케이스 필요",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!_caseInfo.IsValid)
        {
            MessageBox.Show(this, "0/, constant/, system/ 디렉터리를 모두 갖춘 케이스가 필요합니다.",
                "케이스 구조 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return SaveSettingsFromUi(false);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_runner.IsRunning) return;
        JobStatusText.Text = "중지 요청";
        SetFooter("실행 중인 프로세스 트리에 중지 신호를 보냈습니다.");
        _jobCancellation?.Cancel();
        _runner.Cancel();
    }

    private void StartJobUi(string title)
    {
        _jobWatch.Restart();
        _jobTimer.Start();
        JobStatusText.Text = title;
        JobStatusText.Foreground = (Brush)FindResource("WarningBrush");
        MonitorTab.IsSelected = true;
        SetFooter(title);
    }

    private void CompleteJobUi(string message, bool success)
    {
        _jobWatch.Stop();
        _jobTimer.Stop();
        JobStatusText.Text = message;
        JobStatusText.Foreground = (Brush)FindResource(success ? "AccentBrush" : "DangerBrush");
        JobTimeText.Text = $"소요 {_jobWatch.Elapsed:hh\\:mm\\:ss}";
        SetFooter(message);
    }

    private void Runner_OutputReceived(object? sender, ProcessOutputEventArgs e)
    {
        lock (_liveOutputLock)
        {
            _sessionLog.AppendLine(e.Line);
            if (_sessionLog.Length > MaximumSessionLogCharacters + 1_000_000)
                _sessionLog.Remove(0, _sessionLog.Length - MaximumSessionLogCharacters);

            _pendingConsoleOutput.AppendLine(e.Line);
            if (!_consoleFlushQueued)
            {
                _consoleFlushQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushPendingConsoleOutput);
            }
        }

        _residualParser.ParseLine(e.Line);

        var match = SolverTimeRegex.Match(e.Line);
        if (!match.Success ||
            !double.TryParse(match.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return;

        var reportingStep = double.IsFinite(_activePorousEndTime) && _activePorousEndTime > 0
            ? Math.Max(_activePorousEndTime / 400, 1e-12)
            : 1;
        if (time < _lastReportedPorousTime + reportingStep && time < _activePorousEndTime) return;
        _lastReportedPorousTime = time;
        Dispatcher.BeginInvoke(() =>
        {
            var progress = double.IsFinite(_activePorousEndTime) && _activePorousEndTime > 0
                ? $" · {Math.Clamp(time / _activePorousEndTime * 100, 0, 100):F1}%"
                : "";
            PorousRunStatusText.Text = $"SOLVER RUNNING · Time {time:G8} / {_activePorousEndTime:G8}{progress}";
        });
    }

    private void FlushPendingConsoleOutput()
    {
        string text;
        lock (_liveOutputLock)
        {
            text = _pendingConsoleOutput.ToString();
            _pendingConsoleOutput.Clear();
            _consoleFlushQueued = false;
        }
        if (text.Length > 0) AppendConsole(text, false);
    }

    private void ResidualParser_SampleParsed(ResidualSample sample)
    {
        void AddSample()
        {
            _residualSamples.Add(sample);
            if (_residualSamples.Count > MaximumLiveResidualSamples)
                _residualSamples.RemoveRange(0, _residualSamples.Count - MaximumLiveResidualSamples);
            var summary = ResidualSummaries.FirstOrDefault(x => x.Field == sample.Field);
            if (summary is null)
            {
                summary = new ResidualSummary { Field = sample.Field };
                ResidualSummaries.Add(summary);
            }
            summary.Initial = sample.Initial;
            summary.Final = sample.Final;
            summary.Iterations = sample.Iterations;
            summary.Samples++;
            if (_residualSamples.Count < 200 || _residualSamples.Count % 25 == 0)
                ResidualChart.Samples = DownsampleResiduals(_residualSamples, 4_000);
            ResidualLogStatusText.Text = $"실시간 계산 로그 분석 중 · {_residualSamples.Count:N0}개 표본";
            ResidualLogStatusText.ToolTip = null;
        }

        if (Dispatcher.CheckAccess())
            AddSample();
        else
            Dispatcher.BeginInvoke(AddSample);
    }

    private static ResidualSample[] DownsampleResiduals(IReadOnlyList<ResidualSample> samples, int maximum)
    {
        if (samples.Count <= maximum) return samples.ToArray();
        var stride = (int)Math.Ceiling(samples.Count / (double)maximum);
        var reduced = new List<ResidualSample>(maximum + 1);
        for (var index = 0; index < samples.Count; index += stride)
            reduced.Add(samples[index]);
        if (!ReferenceEquals(reduced[^1], samples[^1])) reduced.Add(samples[^1]);
        return reduced.ToArray();
    }

    private bool RestoreLatestResidualLog(string casePath)
    {
        ResetResidualMonitor("저장된 잔차 로그를 확인하는 중입니다…");

        try
        {
            var data = ResidualLogRecovery.LoadLatest(casePath);
            if (data is null)
            {
                ResidualLogStatusText.Text = "이 케이스에는 복구 가능한 잔차 로그가 없습니다.";
                return false;
            }

            ApplyResidualLog(data, "자동 복구");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResidualLogStatusText.Text = "잔차 로그를 읽지 못했습니다.";
            ResidualLogStatusText.ToolTip = ex.Message;
            return false;
        }
    }

    private void LoadResidualLog_Click(object sender, RoutedEventArgs e)
    {
        if (_caseInfo is null)
        {
            MessageBox.Show(this, "먼저 OpenFOAM 케이스 폴더를 여세요.", "잔차 로그 복구",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var logDirectory = Path.Combine(_caseInfo.Path, "FoamWorkbenchLogs");
        var dialog = new OpenFileDialog
        {
            Title = "복구할 OpenFOAM 잔차 로그 선택",
            InitialDirectory = Directory.Exists(logDirectory) ? logDirectory : _caseInfo.Path,
            Filter = "OpenFOAM 로그 (*.log;log.*)|*.log;log.*|모든 파일 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var data = ResidualLogRecovery.Load(dialog.FileName);
            if (data.Samples.Count == 0)
            {
                MessageBox.Show(this,
                    "선택한 로그에 OpenFOAM Initial/Final residual 기록이 없습니다.",
                    "복구할 잔차 없음", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyResidualLog(data, "수동 복구");
            MonitorTab.IsSelected = true;
            SetFooter($"잔차 로그를 복구했습니다: {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "잔차 로그 읽기 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GeneratePythonResidualPlot_Click(object sender, RoutedEventArgs e)
    {
        if (_caseInfo is null)
        {
            MessageBox.Show(this, "먼저 OpenFOAM 케이스 폴더를 여세요.", "Python 잔차 그래프",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_residualSamples.Count == 0)
        {
            MessageBox.Show(this, "먼저 솔버를 실행하거나 잔차 로그를 복구하세요.", "출력할 잔차 없음",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_pythonPlotRunning) return;

        var snapshot = _residualSamples.ToArray();
        _pythonPlotRunning = true;
        PythonResidualPlotButton.IsEnabled = false;
        PythonResidualPlotButton.Content = "생성 중…";
        SetFooter($"Python matplotlib로 {snapshot.Length:N0}개 잔차 표본을 그리는 중…");

        try
        {
            // Use a dedicated runner so a snapshot can be exported while the OpenFOAM solver is still running.
            var plotRuntime = new OpenFoamService(_settings, new ProcessRunner());
            var plotService = new PythonResidualPlotService(plotRuntime);
            var result = await plotService.GenerateAsync(_caseInfo.Path, snapshot, recentSamplesPerField: 100);

            AppendConsole(
                $"\n[Python 잔차 그래프]\n" +
                $"PNG: {result.Artifacts.PngPath}\n" +
                $"SVG: {result.Artifacts.SvgPath}\n" +
                $"CSV: {result.Artifacts.CsvPath}\n", false);
            SetFooter(
                $"Python 상세 잔차 그래프 완료 · {result.Artifacts.FieldCount}개 변수 · " +
                $"{result.Artifacts.SampleCount:N0}개 표본");
            Process.Start(new ProcessStartInfo(result.Artifacts.PngPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendConsole($"\n[Python 그래프 오류] {ex.Message}\n", true);
            SetFooter("Python 잔차 그래프 생성 실패");
            MessageBox.Show(this, ex.Message, "Python 잔차 그래프 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _pythonPlotRunning = false;
            PythonResidualPlotButton.IsEnabled = true;
            PythonResidualPlotButton.Content = "Python 상세";
        }
    }

    private void ApplyResidualLog(ResidualLogData data, string source)
    {
        ResetResidualMonitor();

        foreach (var sample in data.Samples)
        {
            _residualSamples.Add(sample);
            var summary = ResidualSummaries.FirstOrDefault(x => x.Field == sample.Field);
            if (summary is null)
            {
                summary = new ResidualSummary { Field = sample.Field };
                ResidualSummaries.Add(summary);
            }

            summary.Initial = sample.Initial;
            summary.Final = sample.Final;
            summary.Iterations = sample.Iterations;
            summary.Samples++;
        }

        _residualParser.Reset(_residualSamples.Count);
        ResidualChart.Samples = _residualSamples.ToArray();
        ResidualLogStatusText.Text =
            $"{source} · {Path.GetFileName(data.FilePath)} · {_residualSamples.Count:N0}개 표본";
        ResidualLogStatusText.ToolTip = data.FilePath;
    }

    private void ResetResidualMonitor(string? status = null)
    {
        _residualSamples.Clear();
        ResidualSummaries.Clear();
        _residualParser.Reset();
        ResidualChart.Samples = [];
        if (status is not null)
        {
            ResidualLogStatusText.Text = status;
            ResidualLogStatusText.ToolTip = null;
        }
    }

    private void AppendConsole(string text, bool isError)
    {
        ConsoleBox.AppendText(text);
        if (ConsoleBox.Text.Length > 2_000_000)
            ConsoleBox.Text = ConsoleBox.Text[^1_500_000..];
        ConsoleBox.ScrollToEnd();
    }

    private void SaveSessionLog()
    {
        if (_caseInfo is null || _sessionLog.Length == 0) return;
        try
        {
            var directory = Path.Combine(_caseInfo.Path, "FoamWorkbenchLogs");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file, _sessionLog.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppendConsole($"[경고] 세션 로그 저장 실패: {ex.Message}\n", true);
        }
    }

    private void OpenParaView_Click(object sender, RoutedEventArgs e)
    {
        if (_caseInfo is null)
        {
            MessageBox.Show(this, "먼저 케이스를 여세요.", "ParaView", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveSettingsFromUi(false)) return;
        try
        {
            _openFoam.OpenParaView(_caseInfo.Path);
            SetFooter("Windows ParaView에서 현재 .foam 케이스를 열었습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ParaView 실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseParaView_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ParaView 실행 파일 선택",
            Filter = "ParaView (paraview.exe)|paraview.exe|실행 파일 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) ParaViewPathBox.Text = dialog.FileName;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettingsFromUi(true);

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBackendPanels();

    private void UpdateBackendPanels()
    {
        if (WslSettingsPanel is null || DockerSettingsPanel is null) return;
        var isWsl = BackendCombo.SelectedIndex != 1;
        WslSettingsPanel.Visibility = isWsl ? Visibility.Visible : Visibility.Collapsed;
        DockerSettingsPanel.Visibility = isWsl ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => LoadCaseTree();

    private void ClearMonitor_Click(object sender, RoutedEventArgs e)
    {
        ResetResidualMonitor("잔차 모니터를 초기화했습니다.");
        SetFooter("잔차 모니터를 초기화했습니다.");
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleBox.Clear();

    private void ExternalMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ExternalCadTab is not null && IsLoaded) ExternalCadTab.IsSelected = true;
    }

    private void PorousMode_Checked(object sender, RoutedEventArgs e)
    {
        if (PorousMediaTab is not null && IsLoaded) PorousMediaTab.IsSelected = true;
    }

    private void OpenExternalMode_Click(object sender, RoutedEventArgs e)
    {
        ExternalModeRadio.IsChecked = true;
        ExternalCadTab.IsSelected = true;
    }

    private void OpenPorousMode_Click(object sender, RoutedEventArgs e)
    {
        PorousModeRadio.IsChecked = true;
        PorousMediaTab.IsSelected = true;
    }

    private void PorousLayers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (PorousLayer layer in e.OldItems) layer.PropertyChanged -= PorousLayer_PropertyChanged;
        if (e.NewItems is not null)
            foreach (PorousLayer layer in e.NewItems) AttachPorousLayer(layer);
        RenumberPorousLayers();
        UpdatePorousUiSummary();
    }

    private void AttachPorousLayer(PorousLayer layer)
    {
        layer.PropertyChanged -= PorousLayer_PropertyChanged;
        layer.PropertyChanged += PorousLayer_PropertyChanged;
    }

    private void PorousLayer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        UpdatePorousUiSummary();

    private void RenumberPorousLayers()
    {
        for (var i = 0; i < PorousLayers.Count; i++) PorousLayers[i].Id = i + 1;
    }

    private void UpdatePorousUiSummary()
    {
        if (PorousPreview is null || PorousTotalThicknessBox is null) return;
        PorousPreview.Layers = PorousLayers;
        PorousPreview.Refresh();
        var known = PorousLayers.Count > 0 && PorousLayers.All(layer => layer.Thickness is > 0);
        PorousTotalThicknessBox.Text = known
            ? $"{PorousLayers.Sum(layer => layer.Thickness!.Value).ToString("G8", CultureInfo.CurrentCulture)} mm"
            : "INPUT REQUIRED";
    }

    private void PorousInput_TextChanged(object sender, TextChangedEventArgs e) => UpdatePorousUiSummary();

    private void PorousLayerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePorousUiSummary();

    private void PorousLayerGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdatePorousUiSummary, DispatcherPriority.Background);

    private void AddPorousLayer_Click(object sender, RoutedEventArgs e)
    {
        var id = PorousLayers.Count + 1;
        var layer = new PorousLayer
        {
            Id = id,
            DesignGroup = id.ToString(CultureInfo.InvariantCulture),
            Name = $"layer{id}_custom",
            DisplayNameEn = "Custom material",
            DisplayNameKo = "사용자 재료",
            Category = PorousMaterialCategory.GranularFill,
            MaterialType = "Granular / Thick fill layer",
            ParameterSource = PorousParameterSource.Undefined,
            VisualMetadata = new PorousVisualMetadata("#718096", "custom", "User-defined visual metadata")
        };
        PorousLayers.Add(layer);
        PorousLayerGrid.SelectedItem = layer;
        PorousLayerGrid.ScrollIntoView(layer);
    }

    private void RemovePorousLayer_Click(object sender, RoutedEventArgs e)
    {
        if (PorousLayerGrid.SelectedItem is not PorousLayer selected) return;
        PorousLayers.Remove(selected);
    }

    private void MovePorousLayerUp_Click(object sender, RoutedEventArgs e) => MovePorousLayer(-1);
    private void MovePorousLayerDown_Click(object sender, RoutedEventArgs e) => MovePorousLayer(1);

    private void MovePorousLayer(int offset)
    {
        if (PorousLayerGrid.SelectedItem is not PorousLayer selected) return;
        var current = PorousLayers.IndexOf(selected);
        var target = current + offset;
        if (target < 0 || target >= PorousLayers.Count) return;
        PorousLayers.Move(current, target);
        PorousLayerGrid.SelectedItem = selected;
    }

    private void ResetPorousPreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyResearchScenario(PorousPresetFactory.ProposalRainfallId, "연구 표준 A로 초기화했습니다.");
    }

    private void ApplyScenarioA_Click(object sender, RoutedEventArgs e) =>
        ApplyResearchScenario(PorousPresetFactory.ProposalRainfallId, "Scenario A · 강우 20 mm/hr 표준을 적용했습니다.");

    private void ApplyScenarioB_Click(object sender, RoutedEventArgs e) =>
        ApplyResearchScenario(PorousPresetFactory.ProposalWaterHeadId, "Scenario B · 수두 50 mm 표준을 적용했습니다.");

    private void ApplyGravityStudy_Click(object sender, RoutedEventArgs e) =>
        ApplyResearchScenario(PorousPresetFactory.ProposalGravityDrainageId, "연구 보조 · 중력 배수 조건을 적용했습니다.");

    private void ApplyResearchScenario(string presetId, string status)
    {
        var outputRoot = PorousOutputRootBox.Text.Trim();
        var settings = PorousPresetFactory.CreateBuiltInSettings(presetId)
            .CloneWith(outputRootPath: outputRoot);
        ApplyPorousSettings(settings);
        PorousPresetStatusText.Text = status + " PDF의 7-zone과 잠정 물성 출처를 유지합니다.";
        SetFooter(status);
    }

    private void ApplyBuiltInPorousPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PorousBuiltInPresetCombo.SelectedItem is not PorousBuiltInPreset preset) return;
        var outputRoot = PorousOutputRootBox.Text.Trim();
        var settings = PorousPresetFactory.CreateBuiltInSettings(preset.Id)
            .CloneWith(outputRootPath: outputRoot);
        ApplyPorousSettings(settings);
        PorousBuiltInPresetDescription.Text = preset.Description;
        PorousPresetStatusText.Text = $"적용됨: {preset.DisplayName} · {preset.Description}";
        SetFooter($"Porous Media 내장 프리셋을 적용했습니다: {preset.DisplayName}");
    }

    private void BrowsePorousOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Porous Media 케이스와 솔버 결과를 저장할 폴더 선택",
            Multiselect = false,
            InitialDirectory = Directory.Exists(PorousOutputRootBox.Text) ? PorousOutputRootBox.Text : null
        };
        if (dialog.ShowDialog(this) != true) return;
        PorousOutputRootBox.Text = dialog.FolderName;
        _settings.LastOutputRoot = dialog.FolderName;
        SettingsStore.Save(_settings);
    }

    private void PorousMeshPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PorousCellSizeBox is null || PorousMeshPresetCombo.SelectedItem is not PorousMeshPreset preset) return;
        PorousCellSizeBox.Text = preset switch
        {
            PorousMeshPreset.Coarse => "0.5",
            PorousMeshPreset.Medium => "0.25",
            PorousMeshPreset.Fine => "0.1",
            _ => PorousCellSizeBox.Text
        };
    }

    private void PorousFlowMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingPorousFlowMode || sender is not RadioButton radio ||
            !Enum.TryParse<PorousFlowMode>(radio.Tag?.ToString(), out var mode)) return;
        SetPorousFlowMode(mode);
    }

    private void SetPorousFlowMode(PorousFlowMode mode)
    {
        // Checked can fire while InitializeComponent is still constructing later controls.
        if (PorousFlowModeCombo is null || PorousRainfallModeRadio is null ||
            PorousGravityDrainageModeRadio is null || PorousWaterHeadModeRadio is null ||
            PorousRainfallBox is null || PorousWaterHeadBox is null ||
            PorousFlowModeDescription is null) return;

        _syncingPorousFlowMode = true;
        try
        {
            PorousFlowModeCombo.SelectedItem = mode;
            PorousRainfallModeRadio.IsChecked = mode == PorousFlowMode.RainfallFlux;
            PorousGravityDrainageModeRadio.IsChecked = mode == PorousFlowMode.GravityDrainage;
            PorousWaterHeadModeRadio.IsChecked = mode == PorousFlowMode.WaterHead;
            PorousRainfallBox.IsEnabled = mode == PorousFlowMode.RainfallFlux;
            PorousWaterHeadBox.IsEnabled = mode == PorousFlowMode.WaterHead;

            PorousFlowModeDescription.Text = mode switch
            {
                PorousFlowMode.RainfallFlux =>
                    "Rainfall Flux · 상단에서 -Y 방향 속도를 강제하고 하단 압력을 0으로 둡니다.",
                PorousFlowMode.GravityDrainage =>
                    "Gravity Drainage · 상·하단 기준압력은 같고 속도는 강제하지 않습니다. buoyancyForce만으로 Qgravity와 Ugravity를 구합니다.",
                _ =>
                    "Water Head · 입력 수두를 ρgh로 변환한 압력 차로 유동을 구동합니다."
            };

            if (mode == PorousFlowMode.GravityDrainage && PorousGravityCheck is not null)
                PorousGravityCheck.IsChecked = true;
        }
        finally
        {
            _syncingPorousFlowMode = false;
        }
    }

    private PorousCaseSettings ReadPorousSettings()
    {
        PorousLayerGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PorousLayerGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var outputRoot = PorousOutputRootBox.Text.Trim();
        var projectName = PorousProjectNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot)) throw new InvalidOperationException("결과 저장 루트를 선택하세요.");
        if (string.IsNullOrWhiteSpace(projectName)) throw new InvalidOperationException("프로젝트 이름을 입력하세요.");
        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("프로젝트 이름에 폴더명으로 사용할 수 없는 문자가 있습니다.");

        return new PorousCaseSettings
        {
            PresetId = _activePorousPreset.PresetId,
            PresetName = _activePorousPreset.PresetName,
            PresetSourceReference = _activePorousPreset.PresetSourceReference,
            MinimumHydraulicConductivity = _activePorousPreset.MinimumHydraulicConductivity,
            CfdAnalyticalTolerancePercent = _activePorousPreset.CfdAnalyticalTolerancePercent,
            OutputRootPath = outputRoot,
            ProjectName = projectName,
            DomainWidthMm = ParseUiDouble(PorousWidthBox, "Domain Width"),
            Layers = PorousLayers.Select(layer => layer.Clone()).ToArray(),
            Density = ParseUiDouble(PorousDensityBox, "Density"),
            DynamicViscosity = ParseUiDouble(PorousViscosityBox, "Dynamic viscosity"),
            GravityEnabled = PorousGravityCheck.IsChecked == true,
            GravityX = ParseUiDouble(PorousGxBox, "gx"),
            GravityY = ParseUiDouble(PorousGyBox, "gy"),
            GravityZ = ParseUiDouble(PorousGzBox, "gz"),
            FlowMode = PorousFlowModeCombo.SelectedItem is PorousFlowMode flow ? flow : PorousFlowMode.RainfallFlux,
            RainfallMmPerHour = ParseUiDouble(PorousRainfallBox, "Rainfall"),
            WaterHeadMm = ParseUiDouble(PorousWaterHeadBox, "Water Head"),
            SimulationType = PorousSimulationCombo.SelectedItem is PorousSimulationType simulation
                ? simulation : PorousSimulationType.Steady,
            MeshPreset = PorousMeshPresetCombo.SelectedItem is PorousMeshPreset mesh
                ? mesh : PorousMeshPreset.Medium,
            TargetCellSizeMm = ParseUiDouble(PorousCellSizeBox, "Target cell size Y"),
            MinimumCellsPerLayer = ParseUiInt(PorousMinCellsBox, "Minimum cells through each layer"),
            EndTime = ParseUiInt(PorousEndTimeBox, "End time / iterations"),
            WriteInterval = ParseUiInt(PorousWriteIntervalBox, "Write interval"),
            DeltaT = ParseUiDouble(PorousDeltaTBox, "Delta t"),
            ProcessCount = Math.Max(1, _settings.ProcessCount)
        };
    }

    private static double ParseUiDouble(TextBox box, string label)
    {
        var text = box.Text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var current)) return current;
        throw new InvalidOperationException($"{label}: 숫자 형식이 아닙니다.");
    }

    private static int ParseUiInt(TextBox box, string label)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)) return value;
        throw new InvalidOperationException($"{label}: 정수를 입력하세요.");
    }

    private static JsonSerializerOptions PorousJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private void SavePorousPreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadPorousSettings();
            var dialog = new SaveFileDialog
            {
                Title = "Porous Media 설정 저장",
                Filter = "FoamWorkbench Porous preset (*.fwporous.json)|*.fwporous.json|JSON (*.json)|*.json",
                DefaultExt = ".fwporous.json",
                AddExtension = true,
                FileName = "TreeShield-7Layer.fwporous.json",
                InitialDirectory = Directory.Exists(settings.OutputRootPath) ? settings.OutputRootPath : null
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(settings, PorousJsonOptions()), new UTF8Encoding(false));
            PorousPresetStatusText.Text = $"저장됨: {dialog.FileName}";
            SetFooter("Porous Media 층·유체·메시·솔버 설정을 저장했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Porous 설정 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadPorousPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Porous Media 설정 불러오기",
            Filter = "FoamWorkbench Porous preset (*.fwporous.json)|*.fwporous.json|JSON (*.json)|*.json",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<PorousCaseSettings>(File.ReadAllText(dialog.FileName), PorousJsonOptions())
                         ?? throw new InvalidDataException("설정 파일을 읽을 수 없습니다.");
            ApplyPorousSettings(loaded);
            PorousPresetStatusText.Text = $"불러옴: {dialog.FileName}";
            SetFooter("Porous Media 설정을 적용했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Porous 설정 불러오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyPorousSettings(PorousCaseSettings settings)
    {
        _activePorousPreset = settings;
        UpdateResearchScenarioButtons(settings.PresetId);
        var builtIn = PorousBuiltInPresets.FirstOrDefault(item => item.Id == settings.PresetId);
        if (builtIn is not null)
        {
            PorousBuiltInPresetCombo.SelectedItem = builtIn;
            PorousBuiltInPresetDescription.Text = builtIn.Description;
        }
        else
        {
            PorousBuiltInPresetCombo.SelectedIndex = -1;
            PorousBuiltInPresetDescription.Text = string.IsNullOrWhiteSpace(settings.PresetSourceReference)
                ? "사용자 정의 또는 외부 설정 파일"
                : settings.PresetSourceReference;
        }
        PorousOutputRootBox.Text = settings.OutputRootPath;
        PorousProjectNameBox.Text = settings.ProjectName;
        PorousWidthBox.Text = Fui(settings.DomainWidthMm);
        PorousDensityBox.Text = Fui(settings.Density);
        PorousViscosityBox.Text = Fui(settings.DynamicViscosity);
        PorousGravityCheck.IsChecked = settings.GravityEnabled;
        PorousGxBox.Text = Fui(settings.GravityX);
        PorousGyBox.Text = Fui(settings.GravityY);
        PorousGzBox.Text = Fui(settings.GravityZ);
        PorousRainfallBox.Text = Fui(settings.RainfallMmPerHour);
        PorousWaterHeadBox.Text = Fui(settings.WaterHeadMm);
        SetPorousFlowMode(settings.FlowMode);
        PorousSimulationCombo.SelectedItem = settings.SimulationType;
        PorousMeshPresetCombo.SelectedItem = settings.MeshPreset;
        PorousCellSizeBox.Text = Fui(settings.TargetCellSizeMm);
        PorousMinCellsBox.Text = settings.MinimumCellsPerLayer.ToString(CultureInfo.CurrentCulture);
        PorousEndTimeBox.Text = settings.EndTime.ToString(CultureInfo.CurrentCulture);
        PorousWriteIntervalBox.Text = settings.WriteInterval.ToString(CultureInfo.CurrentCulture);
        PorousDeltaTBox.Text = Fui(settings.DeltaT);
        PorousLayers.Clear();
        foreach (var layer in settings.Layers) PorousLayers.Add(layer.Clone());
        PorousLayerGrid.SelectedIndex = PorousLayers.Count > 0 ? 0 : -1;
        UpdatePorousUiSummary();
    }

    private void UpdateResearchScenarioButtons(string presetId)
    {
        foreach (var (button, selected) in new[]
        {
            (ScenarioAButton, presetId == PorousPresetFactory.ProposalRainfallId),
            (ScenarioBButton, presetId == PorousPresetFactory.ProposalWaterHeadId),
            (GravityStudyButton, presetId == PorousPresetFactory.ProposalGravityDrainageId)
        })
        {
            button.Background = (Brush)FindResource(selected ? "AccentBrush" : "PanelRaisedBrush");
            button.BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "BorderBrush");
            button.Foreground = (Brush)FindResource(selected ? "TextBrush" : "MutedBrush");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // UI input boxes do not need the binary round-trip tail (for example
    // 998.20000000000005). Calculations and serialized case values remain double.
    private static string Fui(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

    private void ValidatePorous_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadPorousSettings();
            ShowPorousValidation(settings);
        }
        catch (Exception ex)
        {
            PorousValidationBox.Text = "ERROR\n" + ex.Message;
        }
    }

    private PorousValidationResult ShowPorousValidation(PorousCaseSettings settings)
    {
        var validation = PorousPhysics.Validate(settings);
        var text = new StringBuilder();
        text.AppendLine(validation.IsValid ? "PASS · CFD 실행에 필요한 값이 준비되었습니다." : "Cannot start CFD simulation.");
        if (validation.Errors.Count > 0) text.AppendLine("\nMissing or invalid required physical properties:");
        foreach (var issue in validation.Issues)
            text.AppendLine($"{(issue.IsError ? "ERROR" : "WARNING")} · {issue.Field}\n  {issue.Message}");
        PorousValidationBox.Text = text.ToString();
        if (validation.IsValid)
        {
            var analytical = PorousPhysics.CalculateAnalytical(settings);
            var acceptance = settings.MinimumHydraulicConductivity is > 0 and var minimum
                ? $"\nAcceptance K ≥ {minimum:G8} m/s: {(analytical.HydraulicConductivity >= minimum ? "PASS" : "FAIL")}"
                : "";
            PorousAnalyticalText.Text =
                $"Equivalent permeability: {analytical.EquivalentPermeability:G8} m²\n" +
                $"Hydraulic conductivity: {analytical.HydraulicConductivity:G8} m/s\n" +
                $"Required rainfall: {analytical.RequiredRainfallVelocity:G8} m/s · Safety factor: {analytical.SafetyFactor:G6}\n" +
                $"Individual-zone bottleneck: {analytical.Bottleneck.DisplayName} · {analytical.Bottleneck.ResistanceFraction:P2}\n" +
                $"Design-stage bottleneck: Group {analytical.BottleneckGroup.GroupId} · {analytical.BottleneckGroup.ResistanceFraction:P2}\n" +
                $"Resistance fraction total: {analytical.Layers.Sum(item => item.ResistanceFraction):P6}{acceptance}";
        }
        else PorousAnalyticalText.Text = "Analytical Darcy: INPUT REQUIRED";
        return validation;
    }

    private async void GeneratePorousCase_Click(object sender, RoutedEventArgs e) =>
        await GeneratePorousProjectAsync(runMesh: false, runSolver: false);

    private async void MeshPorousCase_Click(object sender, RoutedEventArgs e) =>
        await GeneratePorousProjectAsync(runMesh: true, runSolver: false);

    private async void RunPorousCase_Click(object sender, RoutedEventArgs e) =>
        await GeneratePorousProjectAsync(runMesh: true, runSolver: true);

    private async Task GeneratePorousProjectAsync(bool runMesh, bool runSolver)
    {
        if (_runner.IsRunning)
        {
            MessageBox.Show(this, "다른 OpenFOAM 작업이 실행 중입니다.", "작업 실행 중",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SaveSettingsFromUi(false)) return;
        PorousCaseSettings settings;
        try
        {
            settings = ReadPorousSettings();
            if (!ShowPorousValidation(settings).IsValid)
                throw new InvalidOperationException("필수 물성이 비어 있거나 잘못되어 CFD 실행을 차단했습니다. Validation 목록을 확인하세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Porous Media 설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.LastOutputRoot = settings.OutputRootPath;
        SettingsStore.Save(_settings);
        _jobCancellation = new CancellationTokenSource();
        lock (_liveOutputLock)
        {
            _sessionLog.Clear();
            _pendingConsoleOutput.Clear();
            _consoleFlushQueued = false;
        }
        _activePorousEndTime = settings.EndTime;
        _lastReportedPorousTime = double.NegativeInfinity;
        ResetResidualMonitor("Porous Media 층류 솔버의 U·p 잔차를 기다리는 중입니다.");
        StartJobUi(runSolver ? "Porous Media CFD" : runMesh ? "Porous Media mesh" : "Porous Media case");
        PorousRunStatusText.Text = "CASE GENERATION 시작…";
        try
        {
            var generator = new PorousCaseGenerator();
            var simulation = new PorousSimulationService(_openFoam, generator);
            var progress = new Progress<string>(stage => PorousRunStatusText.Text = stage);
            var generated = await simulation.GenerateMeshAndOptionallySolveAsync(
                settings, runMesh, runSolver, _jobCancellation.Token, progress);
            _porousLastSettings = settings;
            _porousCasePath = generated.CasePath;
            OpenCase(generated.CasePath);
            if (runSolver)
            {
                _porousLastResult = PorousResultProcessor.Load(generated.CasePath, settings);
                ShowPorousResults(_porousLastResult);
                PorousRunStatusText.Text =
                    $"{FormatPorousStatus(_porousLastResult.SimulationStatus)} · Final Time {_porousLastResult.FinalTime:G8} · " +
                    $"결과 {_porousLastResult.ResultDirectoryCount}개 · POSTPROCESSING COMPLETED";
            }
            else PorousRunStatusText.Text = runMesh
                ? "MESH VALIDATED · 7개 cellZone 검증 통과 · solver는 아직 실행하지 않았습니다."
                : "CASE GENERATED · solver는 아직 실행하지 않았습니다.";
            CompleteJobUi(PorousRunStatusText.Text, true);
        }
        catch (OperationCanceledException)
        {
            PorousRunStatusText.Text = "사용자가 작업을 중지했습니다.";
            CompleteJobUi("Porous Media 작업 중지", false);
        }
        catch (Exception ex)
        {
            var stage = ex is PorousPipelineException pipeline ? pipeline.Stage : "CASE GENERATION";
            var detail = $"[{stage}] {ex.Message}";
            PorousRunStatusText.Text = detail;
            AppendConsole($"\n[POROUS MEDIA ERROR]\n{detail}\n", true);
            CompleteJobUi($"Porous Media 실패 · {stage}", false);
            MessageBox.Show(this, detail, "Porous Media CFD 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FlushPendingConsoleOutput();
            SaveSessionLog();
            _jobCancellation.Dispose();
            _jobCancellation = null;
            _activePorousEndTime = double.NaN;
        }
    }

    private void LoadPorousResults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadPorousSettings();
            var path = _porousCasePath ?? Path.Combine(Path.GetFullPath(settings.OutputRootPath), settings.ProjectName);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("생성된 Porous Media 케이스 폴더가 없습니다.");
            _porousLastSettings = settings;
            _porousCasePath = path;
            _porousLastResult = PorousResultProcessor.Load(path, settings);
            ShowPorousResults(_porousLastResult);
            PorousRunStatusText.Text =
                $"{FormatPorousStatus(_porousLastResult.SimulationStatus)} · Final Time {_porousLastResult.FinalTime:G8} · " +
                $"결과 {_porousLastResult.ResultDirectoryCount}개";
            OpenCase(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Porous 결과 불러오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowPorousResults(PorousResultSummary result)
    {
        var balance = result.FlowBalance;
        var analyticalKeff = _porousLastSettings is null
            ? double.NaN
            : PorousPhysics.CalculateAnalytical(_porousLastSettings).EquivalentPermeability;
        var text = new StringBuilder();
        text.AppendLine($"Simulation Status: {FormatPorousStatus(result.SimulationStatus)}");
        text.AppendLine($"Final Time: {result.FinalTime:G8} · Result directories: {result.ResultDirectoryCount}");
        text.AppendLine($"Final maximum residual: {result.FinalResidualMaximum:G8}");
        text.AppendLine($"Pressure field: {result.PressureFieldName} · {result.PressureUnitDescription}");
        text.AppendLine($"Inlet velocity: {result.InletAverageVelocity:G8} m/s");
        text.AppendLine($"Outlet velocity: {result.OutletAverageVelocity:G8} m/s");
        if (double.IsFinite(result.ExpectedInletVelocity))
            text.AppendLine($"Expected Scenario A inlet: {result.ExpectedInletVelocity:G8} m/s · {(result.InletVelocityPreserved ? "PASS" : "WARNING")}");
        text.AppendLine($"Pressure drop: {result.PressureDropPa:G8} Pa");
        text.AppendLine($"Analytical/CFD k_eff: {analyticalKeff:G8} / {result.CfdEquivalentPermeability:G8} m²");
        text.AppendLine($"CFD hydraulic conductivity: {result.CfdHydraulicConductivity:G8} m/s · Difference: {result.CfdAnalyticalDifferencePercent:G6}%");
        text.AppendLine($"Inlet flow: {result.InletFlowRate:G8} m³/s");
        text.AppendLine($"Outlet flow: {result.OutletFlowRate:G8} m³/s");
        if (balance is not null)
            text.AppendLine($"Flow balance: {balance.DifferencePercent:G6}% · {(balance.Pass ? "PASS" : "WARNING")}");
        foreach (var warning in result.SanityMessages)
            text.AppendLine($"WARNING: {warning}");
        if (_porousLastSettings?.FlowMode == PorousFlowMode.GravityDrainage)
        {
            var area = PorousUnitConverter.MillimetresToMetres(_porousLastSettings.DomainWidthMm) *
                       PorousUnitConverter.MillimetresToMetres(_porousLastSettings.TargetCellSizeMm);
            var qGravity = Math.Abs(result.OutletFlowRate);
            text.AppendLine($"Qgravity: {qGravity:G8} m³/s");
            text.AppendLine($"Ugravity: {qGravity / area:G8} m/s");
        }
        text.AppendLine($"Centerline CSV: {(string.IsNullOrWhiteSpace(result.CenterlineCsvPath) ? "not found" : result.CenterlineCsvPath)}");
        text.AppendLine();
        foreach (var layer in result.Layers)
            text.AppendLine($"{layer.Name}: Pin={layer.AverageInletPressurePa:G6} Pa · Pout={layer.AverageOutletPressurePa:G6} Pa · ΔP={layer.PressureDropPa:G6} Pa · Uy={layer.AverageThroughVelocity:G6} m/s · nominal t={layer.NominalResidenceTime:G6} s");
        text.AppendLine("\nNominal residence time = thickness / volume-weighted through-flow velocity; particle tracking RTD가 아닙니다.");
        PorousResultsText.Text = text.ToString();
    }

    private void OpenPorousParaView_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadPorousSettings();
            var path = _porousCasePath ?? Path.Combine(Path.GetFullPath(settings.OutputRootPath), settings.ProjectName);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("먼저 Porous Media 케이스를 생성하세요.");
            var published = PorousSimulationService.PublishVisualizationFieldsToResultTimes(path);
            var latestTime = PorousResultProcessor.FindResultTimes(path).LastOrDefault(double.NaN);
            _openFoam.OpenParaView(path);
            SetFooter($"ParaView latest time: {latestTime:G8} · Color By → layerId / permeability / U Magnitude / p · 시각화장 {published.Count}개 확인");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ParaView 실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatPorousStatus(PorousSimulationStatus status) => status switch
    {
        PorousSimulationStatus.Converged => "SOLVER CONVERGED",
        PorousSimulationStatus.Failed => "FAILED",
        _ => "NOT CONVERGED"
    };

    private void GeneratePorousReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadPorousSettings();
            if (!ShowPorousValidation(settings).IsValid) throw new InvalidOperationException("보고서에는 유효한 모든 층 물성이 필요합니다.");
            var casePath = _porousCasePath ?? Path.Combine(Path.GetFullPath(settings.OutputRootPath), settings.ProjectName);
            if (!Directory.Exists(casePath)) throw new DirectoryNotFoundException("먼저 케이스를 생성하세요.");
            var analytical = PorousPhysics.CalculateAnalytical(settings);
            var files = PorousReportGenerator.Generate(settings, analytical, _porousLastResult, casePath);
            PorousRunStatusText.Text = "CFD Report 생성: " + string.Join(" · ", files.Select(Path.GetFileName));
            SetFooter("HTML · CSV · JSON CFD 보고서를 생성했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CFD Report 생성 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunPorousSweep_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning) return;
        try
        {
            var settings = ReadPorousSettings();
            if (!ShowPorousValidation(settings).IsValid) throw new InvalidOperationException("Sweep 전에 모든 층 물성을 입력하세요.");
            var index = PorousLayerGrid.SelectedIndex;
            if (index < 0) throw new InvalidOperationException("Sweep할 층을 표에서 선택하세요.");
            var request = new PorousSweepRequest(index,
                ParseUiDouble(SweepStartBox, "Sweep start k"),
                ParseUiDouble(SweepEndBox, "Sweep end k"),
                ParseUiInt(SweepStepsBox, "Sweep steps"),
                SweepRunSolverCheck.IsChecked == true);
            _jobCancellation = new CancellationTokenSource();
            StartJobUi("Porous parameter sweep");
            var generator = new PorousCaseGenerator();
            var simulation = new PorousSimulationService(_openFoam, generator);
            var sweep = new PorousSweepService(generator, simulation);
            var (csv, rows) = await sweep.RunAsync(settings, request, _jobCancellation.Token);
            PorousRunStatusText.Text = $"Parameter Sweep 완료 · {rows.Count} cases · {csv}";
            CompleteJobUi("Porous parameter sweep 완료", true);
        }
        catch (Exception ex)
        {
            PorousRunStatusText.Text = ex.Message;
            CompleteJobUi("Porous parameter sweep 실패", false);
            MessageBox.Show(this, ex.Message, "Parameter Sweep 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _jobCancellation?.Dispose();
            _jobCancellation = null;
        }
    }

    private void CalculateKozenyCarman_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var particleMicrometres = ParseUiDouble(KcParticleBox, "Particle diameter");
            var porosity = ParseUiDouble(KcPorosityBox, "Porosity");
            var k = PorousPhysics.KozenyCarman(
                PorousUnitConverter.MicrometresToMetres(particleMicrometres), porosity);
            KcResultText.Text = $"Estimated permeability: {k:G10} m²\nEstimated only · 섬유 매트, 부직포, 압축성 커피층에는 자동 적용하지 않습니다.";
        }
        catch (Exception ex)
        {
            KcResultText.Text = "입력 오류: " + ex.Message;
        }
    }

    private void SetFooter(string text) => FooterStatus.Text = text;

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_runner.IsRunning)
        {
            var result = MessageBox.Show(this, "OpenFOAM 작업이 실행 중입니다. 중지하고 종료하시겠습니까?",
                "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _runner.Cancel();
        }

        if (!ConfirmDiscardEditor())
        {
            e.Cancel = true;
            return;
        }

        SaveSettingsFromUi(false);
    }
}

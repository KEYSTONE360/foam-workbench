using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FoamWorkbench;

public partial class CadPreviewWindow : Window
{
    private readonly CadPreviewData _data;
    private readonly Point3D _target;
    private readonly double _modelLength;
    private bool _orbiting;
    private Point _lastMouse;
    private double _yaw = 42;
    private double _pitch = 24;
    private double _distance;

    public CadPreviewWindow(CadPreviewData data, FlowAxis flowAxis)
    {
        InitializeComponent();
        _data = data;
        _target = ToPoint(new Point3(
            (data.Bounds.Min.X + data.Bounds.Max.X) * 0.5,
            (data.Bounds.Min.Y + data.Bounds.Max.Y) * 0.5,
            (data.Bounds.Min.Z + data.Bounds.Max.Z) * 0.5));
        _modelLength = Math.Max(1e-9, data.Bounds.CharacteristicLength);
        _distance = _modelLength * 2.8;

        BuildScene(flowAxis);
        SetIsometric();

        var reduced = data.WasDisplayReduced
            ? $" · 화면 표시용 {data.Triangles.Count:N0}개로 축소"
            : "";
        PreviewInfoText.Text =
            $"원본 표면 {data.OriginalTriangleCount:N0} 삼각형{reduced} · 실제 OpenFOAM 메시 생성 전 형상 확인";
        FlowDirectionText.Text = $"선택 유동 방향: {AxisLabel(flowAxis)}";
        BoundsText.Text =
            $"크기 [m]  X {data.Bounds.XLength:G5}  Y {data.Bounds.YLength:G5}  Z {data.Bounds.ZLength:G5}";
    }

    private void BuildScene(FlowAxis flowAxis)
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(90, 102, 116)));
        root.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(116, 158, 190), new Vector3D(1, 0.4, -0.2)));

        var mesh = new MeshGeometry3D();
        foreach (var triangle in _data.Triangles)
        {
            var a = ToPoint(triangle.A);
            var b = ToPoint(triangle.B);
            var c = ToPoint(triangle.C);
            var normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.LengthSquared > 1e-24) normal.Normalize();

            var start = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
            mesh.Normals.Add(normal); mesh.Normals.Add(normal); mesh.Normals.Add(normal);
            mesh.TriangleIndices.Add(start);
            mesh.TriangleIndices.Add(start + 1);
            mesh.TriangleIndices.Add(start + 2);
        }

        var bodyMaterial = new MaterialGroup();
        bodyMaterial.Children.Add(new DiffuseMaterial(
            new SolidColorBrush(Color.FromRgb(102, 164, 206))));
        bodyMaterial.Children.Add(new SpecularMaterial(Brushes.White, 42));
        root.Children.Add(new GeometryModel3D(mesh, bodyMaterial)
        {
            BackMaterial = new DiffuseMaterial(
                new SolidColorBrush(Color.FromRgb(65, 104, 132)))
        });

        var origin = new Point3D(
            _data.Bounds.Min.X - _modelLength * 0.12,
            _data.Bounds.Min.Y - _modelLength * 0.12,
            _data.Bounds.Min.Z - _modelLength * 0.12);
        AddArrow(root, origin, new Vector3D(1, 0, 0), _modelLength * 0.28,
            Color.FromRgb(255, 82, 82), 0.016);
        AddArrow(root, origin, new Vector3D(0, 1, 0), _modelLength * 0.28,
            Color.FromRgb(66, 214, 112), 0.016);
        AddArrow(root, origin, new Vector3D(0, 0, 1), _modelLength * 0.28,
            Color.FromRgb(70, 146, 255), 0.016);

        var flow = AxisVector(flowAxis);
        var offsetDirection = Math.Abs(flow.Z) > 0.5
            ? new Vector3D(0, 1, 0)
            : new Vector3D(0, 0, 1);
        var flowStart = _target - flow * (_modelLength * 0.72) +
                        offsetDirection * (_modelLength * 0.72);
        AddArrow(root, flowStart, flow, _modelLength * 1.44,
            Color.FromRgb(66, 214, 181), 0.025);

        PreviewViewport.Children.Add(new ModelVisual3D { Content = root });
    }

    private static void AddArrow(
        Model3DGroup root,
        Point3D start,
        Vector3D direction,
        double length,
        Color color,
        double radiusScale)
    {
        direction.Normalize();
        var helper = Math.Abs(Vector3D.DotProduct(direction, new Vector3D(0, 0, 1))) < 0.9
            ? new Vector3D(0, 0, 1)
            : new Vector3D(0, 1, 0);
        var u = Vector3D.CrossProduct(direction, helper);
        u.Normalize();
        var v = Vector3D.CrossProduct(direction, u);
        v.Normalize();

        const int segments = 16;
        var shaftLength = length * 0.74;
        var shaftRadius = length * radiusScale;
        var headRadius = shaftRadius * 2.5;
        var shaftEnd = start + direction * shaftLength;
        var tip = start + direction * length;
        var mesh = new MeshGeometry3D();

        for (var i = 0; i < segments; i++)
        {
            var angle = i * Math.PI * 2 / segments;
            var radial = u * Math.Cos(angle) + v * Math.Sin(angle);
            mesh.Positions.Add(start + radial * shaftRadius);
            mesh.Positions.Add(shaftEnd + radial * shaftRadius);
            mesh.Positions.Add(shaftEnd + radial * headRadius);
        }
        mesh.Positions.Add(tip);
        var tipIndex = mesh.Positions.Count - 1;

        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var a0 = i * 3;
            var a1 = next * 3;
            AddTriangle(mesh, a0, a1, a0 + 1);
            AddTriangle(mesh, a0 + 1, a1, a1 + 1);
            AddTriangle(mesh, a0 + 2, next * 3 + 2, tipIndex);
        }

        var material = new DiffuseMaterial(new SolidColorBrush(color));
        root.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static void AddTriangle(MeshGeometry3D mesh, int a, int b, int c)
    {
        mesh.TriangleIndices.Add(a);
        mesh.TriangleIndices.Add(b);
        mesh.TriangleIndices.Add(c);
    }

    private void SetIsometric()
    {
        _yaw = 42;
        _pitch = 24;
        _distance = _modelLength * 2.8;
        UpdateCamera();
    }

    private void SetAxisView(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        _distance = _modelLength * 2.8;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var direction = new Vector3D(
            Math.Cos(pitch) * Math.Cos(yaw),
            Math.Cos(pitch) * Math.Sin(yaw),
            Math.Sin(pitch));
        var position = _target + direction * _distance;
        PreviewCamera.Position = position;
        PreviewCamera.LookDirection = _target - position;
        PreviewCamera.UpDirection = Math.Abs(_pitch) > 85
            ? new Vector3D(0, 1, 0)
            : new Vector3D(0, 0, 1);
        PreviewCamera.NearPlaneDistance = Math.Max(1e-7, _modelLength * 0.001);
        PreviewCamera.FarPlaneDistance = _modelLength * 100;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _orbiting = true;
        _lastMouse = e.GetPosition(PreviewViewport);
        PreviewViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _orbiting = false;
        PreviewViewport.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_orbiting) return;
        var current = e.GetPosition(PreviewViewport);
        _yaw -= (current.X - _lastMouse.X) * 0.35;
        _pitch = Math.Clamp(_pitch + (current.Y - _lastMouse.Y) * 0.3, -85, 85);
        _lastMouse = current;
        UpdateCamera();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance *= e.Delta > 0 ? 0.86 : 1.16;
        _distance = Math.Clamp(_distance, _modelLength * 0.25, _modelLength * 30);
        UpdateCamera();
    }

    private void Isometric_Click(object sender, RoutedEventArgs e) => SetIsometric();
    private void ViewX_Click(object sender, RoutedEventArgs e) => SetAxisView(0, 0);
    private void ViewY_Click(object sender, RoutedEventArgs e) => SetAxisView(90, 0);
    private void ViewZ_Click(object sender, RoutedEventArgs e) => SetAxisView(0, 89.5);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static Point3D ToPoint(Point3 point) => new(point.X, point.Y, point.Z);

    private static Vector3D AxisVector(FlowAxis axis) => axis switch
    {
        FlowAxis.PositiveX => new Vector3D(1, 0, 0),
        FlowAxis.NegativeX => new Vector3D(-1, 0, 0),
        FlowAxis.PositiveY => new Vector3D(0, 1, 0),
        FlowAxis.NegativeY => new Vector3D(0, -1, 0),
        FlowAxis.PositiveZ => new Vector3D(0, 0, 1),
        _ => new Vector3D(0, 0, -1)
    };

    private static string AxisLabel(FlowAxis axis) => axis switch
    {
        FlowAxis.PositiveX => "+X",
        FlowAxis.NegativeX => "-X",
        FlowAxis.PositiveY => "+Y",
        FlowAxis.NegativeY => "-Y",
        FlowAxis.PositiveZ => "+Z",
        _ => "-Z"
    };
}

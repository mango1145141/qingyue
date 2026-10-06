using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EpubKindleFix;

public enum BookPageLayer { Cover, FirstLeaf, SecondLeaf }

public static class BookPageGeometry
{
    public const int Columns = 64, Rows = 20;
    public const double Width = 160, Height = 240;
    private static readonly List<int> EdgeIndices = Perimeter();

    public static MeshGeometry3D Create(BookPageLayer layer = BookPageLayer.Cover)
    {
        var mesh = new MeshGeometry3D();
        for (var row = 0; row <= Rows; row++)
            for (var col = 0; col <= Columns; col++)
                mesh.TextureCoordinates.Add(new Point((double)col / Columns, 1d - (double)row / Rows));
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Columns; col++)
            {
                var a = row * (Columns + 1) + col;
                var b = a + 1;
                var c = a + Columns + 2;
                var d = a + Columns + 1;
                foreach (var index in new[] { a, b, c, a, c, d }) mesh.TriangleIndices.Add(index);
            }
        Update(mesh, 0, layer);
        return mesh;
    }

    public static void Update(MeshGeometry3D mesh, double progress, BookPageLayer layer = BookPageLayer.Cover)
    {
        var delay = layer switch { BookPageLayer.FirstLeaf => 0.14, BookPageLayer.SecondLeaf => 0.26, _ => 0d };
        var raw = Math.Clamp((progress - delay) / (1 - delay), 0, 1.04);
        var p = Math.Min(raw, 1);
        var openAngle = layer switch { BookPageLayer.FirstLeaf => 146d, BookPageLayer.SecondLeaf => 133d, _ => 158d };
        // Spine rotation: zero velocity at both ends; spring overshoot whips
        // the page slightly past flat before it settles, like real paper.
        var ease = p * p * (3 - 2 * p);
        var turn = ease + Math.Max(0, raw - 1) * 0.55;
        var hinge = turn * openAngle * Math.PI / 180;
        var flex = layer == BookPageLayer.Cover ? 0.55 : 0.95;
        var restCurl = layer switch { BookPageLayer.FirstLeaf => 0.30, BookPageLayer.SecondLeaf => 0.42, _ => 0.05 };
        // Traveling crease: the bend is concentrated around a crease line that
        // sweeps from the free edge toward the spine as the page turns, and is
        // sharpest mid-flight — this is what makes paper look like paper.
        var crease = 0.92 - 0.80 * ease;
        var sharpness = Math.Sin(Math.PI * Math.Min(p * 1.06, 1));
        var width = 0.26 - 0.15 * sharpness;
        const double bendGain = 3.4;
        var depth = layer switch { BookPageLayer.FirstLeaf => -0.8, BookPageLayer.SecondLeaf => -1.6, _ => 0d };
        var du = 1.0 / Columns;
        var step = Width / Columns;
        var points = new Point3DCollection((Columns + 1) * (Rows + 1));
        for (var row = 0; row <= Rows; row++)
        {
            var v = (double)row / Rows;
            // The top corner leads (fingers lift the top of the page), so the
            // crease line tilts and the bottom lags behind.
            var lead = 1 + 0.30 * (0.5 - v);
            var rowCrease = crease + 0.16 * v;
            var theta = hinge * (1 + 0.10 * (0.5 - v));
            double x = 0, z = depth;
            for (var col = 0; col <= Columns; col++)
            {
                var u = (double)col / Columns;
                if (col > 0)
                {
                    var middle = (col - 0.5) / Columns;
                    // Curvature: a Gaussian bump traveling with the crease, plus
                    // the paper's own residual curl — which must fade IN with the
                    // turn, or the inner leaves would bulge through the closed cover.
                    var g = (middle - rowCrease) / width;
                    var kappa = flex * sharpness * bendGain * Math.Exp(-0.5 * g * g) + restCurl * ease * middle;
                    theta += kappa * du * lead;
                    x += step * Math.Cos(theta);
                    z += step * Math.Sin(theta);
                }
                var corner = 5 * sharpness * u * u * u * (v - 0.5);
                points.Add(new Point3D(x, v * Height + corner, z));
            }
        }
        var normals = new Vector3DCollection(points.Count);
        for (var row = 0; row <= Rows; row++)
            for (var col = 0; col <= Columns; col++)
            {
                var across = points[row * (Columns + 1) + Math.Min(Columns, col + 1)]
                    - points[row * (Columns + 1) + Math.Max(0, col - 1)];
                var along = points[Math.Min(Rows, row + 1) * (Columns + 1) + col]
                    - points[Math.Max(0, row - 1) * (Columns + 1) + col];
                var normal = Vector3D.CrossProduct(across, along);
                normal.Normalize();
                normals.Add(normal);
            }
        mesh.Positions = points;
        mesh.Normals = normals;
    }

    public static MeshGeometry3D CreateEdge(MeshGeometry3D page)
    {
        var edge = new MeshGeometry3D();
        var count = EdgeIndices.Count;
        for (var i = 0; i < count; i++)
        {
            var a = i * 2;
            var b = ((i + 1) % count) * 2;
            foreach (var index in new[] { a, a + 1, b + 1, a, b + 1, b }) edge.TriangleIndices.Add(index);
        }
        UpdateEdge(edge, page);
        return edge;
    }

    public static void UpdateEdge(MeshGeometry3D edge, MeshGeometry3D page)
    {
        var positions = new Point3DCollection();
        foreach (var index in EdgeIndices)
        {
            var point = page.Positions[index];
            var normal = page.Normals[index] * 0.38;
            positions.Add(point + normal);
            positions.Add(point - normal);
        }
        edge.Positions = positions;
    }

    private static List<int> Perimeter()
    {
        var indices = new List<int>();
        for (var col = 0; col < Columns; col++) indices.Add(col);
        for (var row = 0; row < Rows; row++) indices.Add(row * (Columns + 1) + Columns);
        for (var col = Columns; col > 0; col--) indices.Add(Rows * (Columns + 1) + col);
        for (var row = Rows; row > 0; row--) indices.Add(row * (Columns + 1));
        return indices;
    }
}

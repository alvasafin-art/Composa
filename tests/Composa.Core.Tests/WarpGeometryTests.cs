using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class WarpGeometryTests
{
    [Fact]
    public void Identity_covers_the_actual_input_rectangle_even_outside_the_original_control_region()
    {
        var region = new SKRect(-17, -9, 1018, 514); var geometry = new WarpGeometry(WarpMesh.Create(domain: new(.2, .1, .5, .7)), 1000, 500, region);
        Assert.Equal(region, geometry.Bounds);
        foreach (var patch in geometry.Patches) for (var y = 0; y <= 4; y++) for (var x = 0; x <= 4; x++) Close(geometry.SourcePoint(patch, x / 4.0, y / 4.0), patch.Evaluate(x / 4.0, y / 4.0));
        Assert.Equal(1, geometry.Subdivisions(1, .2));
    }

    [Fact]
    public void Extended_cells_exactly_represent_the_mesh_and_their_hulls_include_curvature_overshoot()
    {
        var mesh = WarpMesh.Create(3, 4, new(.2, .15, .5, .6)).WithPoint(5, new(.25, .18)).WithPoint(10, new(.64, .64));
        var geometry = new WarpGeometry(mesh, 640, 480, new(-18, -9, 660, 501));
        Assert.True(geometry.Patches.Length > mesh.Columns * mesh.Rows);
        foreach (var patch in geometry.Patches) for (var y = 0; y <= 16; y++) for (var x = 0; x <= 16; x++)
        {
            var u = x / 16.0; var v = y / 16.0; var value = patch.Evaluate(u, v);
            var expected = mesh.EvaluateExtended(patch.U0 + (patch.U1 - patch.U0) * u, patch.V0 + (patch.V1 - patch.V0) * v);
            Close(new(expected.X * 640, expected.Y * 480), value);
            Assert.InRange(value.X, geometry.Bounds.Left, geometry.Bounds.Right); Assert.InRange(value.Y, geometry.Bounds.Top, geometry.Bounds.Bottom);
        }
        Assert.True(geometry.Subdivisions(1, .2) >= geometry.Subdivisions(.25, .2));
    }

    [Fact]
    public void Invalid_or_numerically_unbounded_input_is_rejected_before_native_allocations()
    {
        Assert.Throws<ArgumentException>(() => new WarpGeometry(WarpMesh.Create(), 100, 80, new(0, 0, float.NaN, 80)));
        Assert.Throws<ArgumentException>(() => new WarpGeometry(WarpMesh.Create(), 100, 80, SKRect.Empty));
        var tiny = WarpMesh.Create(domain: new(0, 0, double.Epsilon, 1)).WithPoint(5, new(.1, .3));
        Assert.Throws<InvalidOperationException>(() => new WarpGeometry(tiny, 100, 80, new(0, 0, 100, 80)));
    }

    private static void Close(WarpPoint expected, WarpPoint actual) => Assert.InRange(Math.Abs(expected.X - actual.X) + Math.Abs(expected.Y - actual.Y), 0, 1e-7);
}

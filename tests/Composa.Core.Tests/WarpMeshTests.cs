using System.Collections.Immutable;
using System.Text.Json;
using Composa.Model;

namespace Composa.Core.Tests;

public class WarpMeshTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Identity_grid_reproduces_the_source_region_and_every_node_interpolates_exactly(int divisions)
    {
        var area = new WarpDomain(.1, .2, .7, .6); var mesh = WarpMesh.Create(divisions, divisions, area); Assert.True(mesh.IsIdentity);
        for (var y = 0; y <= 30; y++) for (var x = 0; x <= 30; x++)
        { var expected = area.At(x / 30.0, y / 30.0); var actual = mesh.Evaluate(x / 30.0, y / 30.0); Close(expected, actual); }
        var changed = mesh.WithPoint(mesh.Points.Length / 2, new(.2, -.1)); Assert.False(changed.IsIdentity); Assert.True(mesh.IsIdentity);
        for (var y = 0; y <= divisions; y++) for (var x = 0; x <= divisions; x++) Close(changed.Point(x, y), changed.Evaluate((double)x / divisions, (double)y / divisions));
    }

    [Fact]
    public void Exact_control_hulls_cover_curve_overshoot_and_internal_derivatives_are_continuous()
    {
        var mesh = WarpMesh.Create().WithPoint(5, new(.2, -.4)).WithPoint(10, new(.8, 1.3)); var bounds = mesh.Bounds(1000, 700);
        for (var y = 0; y <= 180; y++) for (var x = 0; x <= 180; x++)
        { var p = mesh.Evaluate(x / 180.0, y / 180.0); Assert.InRange(p.X * 1000, bounds.Left, bounds.Right); Assert.InRange(p.Y * 700, bounds.Top, bounds.Bottom); }
        const double epsilon = 1e-6;
        foreach (var knot in new[] { 1.0 / 3, 2.0 / 3 })
        {
            var at = mesh.Evaluate(knot, .45); var before = mesh.Evaluate(knot - epsilon, .45); var after = mesh.Evaluate(knot + epsilon, .45);
            Assert.InRange(Math.Abs((at.X - before.X) / epsilon - (after.X - at.X) / epsilon), 0, .0001);
            Assert.InRange(Math.Abs((at.Y - before.Y) / epsilon - (after.Y - at.Y) / epsilon), 0, .0001);
        }
    }

    [Fact]
    public void Immutable_value_round_trips_and_rejects_invalid_or_unbounded_serialized_grids()
    {
        var mesh = WarpMesh.Create(4, 3).WithPoint(6, new(.32, -.1)); var restored = JsonSerializer.Deserialize<WarpMesh>(JsonSerializer.Serialize(mesh))!;
        Assert.Equal(mesh, restored); Assert.Equal(mesh.GetHashCode(), restored.GetHashCode());
        Assert.Throws<ArgumentOutOfRangeException>(() => WarpMesh.Create(0));
        Assert.Throws<ArgumentException>(() => new WarpMesh(3, 3, WarpDomain.Unit, ImmutableArray<WarpPoint>.Empty));
        Assert.Throws<ArgumentException>(() => mesh.WithPoint(0, new(double.NaN, 0)));
        Assert.Throws<ArgumentException>(() => mesh.WithPoint(0, new(DocumentLimits.MaxWarpCoordinate + 1, 0)));
        Assert.Throws<ArgumentException>(() => WarpMesh.Create(domain: new(0, 0, 0, 1)));
    }

    [Fact]
    public void Extended_surface_preserves_affine_geometry_outside_the_original_control_region()
    {
        var area = new WarpDomain(.2, .1, .5, .7); var mesh = WarpMesh.Create(4, 3, area);
        static WarpPoint Map(WarpPoint point) => new(1.2 * point.X + .1 * point.Y + .03, -.1 * point.X + .9 * point.Y - .04);
        for (var i = 0; i < mesh.Points.Length; i++) mesh = mesh.WithPoint(i, Map(mesh.Points[i]));
        for (var y = -8; y <= 8; y++) for (var x = -8; x <= 8; x++) Close(Map(area.At(x / 4.0, y / 4.0)), mesh.EvaluateExtended(x / 4.0, y / 4.0));
        Assert.Throws<ArgumentException>(() => mesh.EvaluateExtended(double.NaN, 0));
    }

    [Fact]
    public void Extended_surface_keeps_both_first_derivatives_continuous_across_sides_and_corner_strips()
    {
        var mesh = WarpMesh.Create().WithPoint(5, new(.2, .13)).WithPoint(10, new(.78, .83));
        for (var y = 0; y <= 20; y++) for (var x = 0; x <= 20; x++) Close(mesh.Evaluate(x / 20.0, y / 20.0), mesh.EvaluateExtended(x / 20.0, y / 20.0));
        const double epsilon = 1e-6;
        foreach (var side in new[] { 0.0, 1.0 }) foreach (var other in new[] { -.4, .25, .7, 1.3 }) foreach (var axis in new[] { 0, 1 })
        {
            WarpPoint At(double coordinate) => axis == 0 ? mesh.EvaluateExtended(coordinate, other) : mesh.EvaluateExtended(other, coordinate);
            var at = At(side); var before = (at - At(side - epsilon)) * (1 / epsilon); var after = (At(side + epsilon) - at) * (1 / epsilon);
            Assert.InRange(Math.Abs(before.X - after.X) + Math.Abs(before.Y - after.Y), 0, .0002);
        }
    }

    private static void Close(WarpPoint expected, WarpPoint actual)
    { Assert.InRange(Math.Abs(actual.X - expected.X), 0, 1e-10); Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 1e-10); }
}

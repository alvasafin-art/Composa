using System.Numerics;

namespace Composa.Painting;

/// <summary>Estimate the missing low frequencies from pairs of intact profiles across each selected run.</summary>
internal static class DirectionalGuide
{
    private const int Reach = 96;
    private const int Directions = 48;
    private readonly record struct Offset(int X, int Y);
    private static readonly Offset[][] Rays = Enumerable.Range(0, Directions).Select(i =>
    {
        double angle = i * Math.PI / Directions;
        return Enumerable.Range(0, Reach + 1).Select(s => new Offset(
            (int)Math.Round(s * Math.Cos(angle)), (int)Math.Round(s * Math.Sin(angle)))).ToArray();
    }).ToArray();

    internal static void Fill(Vector3[] source, Vector3[] guide, float[] confidence, bool[] known, bool[] selected,
        int width, int height, CancellationToken cancellation)
    {
        // Integrate only gradients whose entire stencil is known. A selection edge must never guide a repair.
        int stride = width + 1;
        var tensors = new Vector3[stride * (height + 1)];
        for (int y = 1; y < height - 1; y++)
        {
            var row = Vector3.Zero;
            for (int x = 1; x < width; x++)
            {
                int i = y * width + x;
                if (x < width - 1 && known[i] && known[i - 1] && known[i + 1] && known[i - width] && known[i + width])
                {
                    var gx = source[i + 1] - source[i - 1];
                    var gy = source[i + width] - source[i - width];
                    row += new Vector3(gx.LengthSquared(), Vector3.Dot(gx, gy), gy.LengthSquared());
                }
                tensors[(y + 1) * stride + x + 1] = tensors[y * stride + x + 1] + row;
            }
        }
        Vector3 Tensor(int x, int y)
        {
            int l = Math.Max(0, x - 8), r = Math.Min(width, x + 9);
            int t = Math.Max(0, y - 8), b = Math.Min(height - 1, y + 9);
            return tensors[b * stride + r] - tensors[t * stride + r] - tensors[b * stride + l] + tensors[t * stride + l];
        }
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            bool Valid(int x, int yy) => (uint)x < width && (uint)yy < height && known[yy * width + x];
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (!selected[index]) continue;
                float total = 0, evidence = 0;
                Vector3 sum = default;
                for (int direction = 0; direction < Directions; direction++)
                {
                    var ray = Rays[direction];
                    int Endpoint(int sign)
                    {
                        for (int step = 1; step < Reach - 5; step++)
                        {
                            int xx = x + sign * ray[step].X, yy = y + sign * ray[step].Y;
                            if ((uint)xx >= width || (uint)yy >= height) break;
                            if (known[yy * width + xx]) return step;
                            if (!selected[yy * width + xx]) break;
                        }
                        return 0;
                    }
                    int left = Endpoint(-1), right = Endpoint(1);
                    if (left == 0 || right == 0) continue;
                    int ax = x - ray[left].X, ay = y - ray[left].Y;
                    int bx = x + ray[right].X, by = y + ray[right].Y;
                    var a = source[ay * width + ax]; var b = source[by * width + bx];
                    float cost = 0; int taps = 0;
                    // Compare profiles perpendicular to transport and measure their stability in intact context.
                    for (int k = -4; k <= 4; k += 2)
                    {
                        var cross = ray[Math.Abs(k)];
                        int dx = Math.Sign(k) * cross.Y, dy = -Math.Sign(k) * cross.X;
                        int xa = ax + dx, ya = ay + dy, xb = bx + dx, yb = by + dy;
                        int xo = xa - ray[4].X, yo = ya - ray[4].Y;
                        int xt = xb + ray[4].X, yt = yb + ray[4].Y;
                        if (!Valid(xa, ya) || !Valid(xb, yb) || !Valid(xo, yo) || !Valid(xt, yt)) continue;
                        var pa = source[ya * width + xa]; var pb = source[yb * width + xb];
                        cost += Vector3.DistanceSquared(pa, pb)
                            + .7f * (Vector3.DistanceSquared(pa, source[yo * width + xo]) + Vector3.DistanceSquared(pb, source[yt * width + xt]));
                        taps++;
                    }
                    if (taps == 0) continue;
                    float span = left + right;

                    var tensor = Tensor(ax, ay) + Tensor(bx, by);
                    var tangent = ray[Reach];
                    float vx = (float)tangent.X / Reach, vy = (float)tangent.Y / Reach;
                    float trace = tensor.X + tensor.Z;
                    float discriminant = MathF.Sqrt(Math.Max(0, (tensor.X - tensor.Z) * (tensor.X - tensor.Z) + 4 * tensor.Y * tensor.Y));
                    float across = tensor.X * vx * vx + 2 * tensor.Y * vx * vy + tensor.Z * vy * vy;
                    float angular = Math.Max(0, across - .5f * (trace - discriminant)) / (trace + .004f);
                    // Normalize for local contrast so a faint but consistent line in a dark surface
                    // can outweigh a shorter path that cuts across it.
                    float certainty = discriminant / (trace + .012f);
                    float alignment = (1 + 7 * certainty * certainty) / (1 + 65 * angular * certainty);
                    cost = .0006f + cost / taps + .0000008f * span * span / (1 + 6 * certainty * certainty);
                    float weight = MathF.Sqrt(taps) * alignment * alignment / (cost * cost);
                    float t = left / span;
                    Vector3 Mirror(int sign, int depth, Vector3 edge)
                    {
                        int where = Math.Min(Reach, depth + Math.Min(depth, 18));
                        int xx = x + sign * ray[where].X, yy = y + sign * ray[where].Y;
                        if (!Valid(xx, yy)) return edge;
                        var delta = source[yy * width + xx] - edge;
                        return edge + delta * Math.Min(1, .10f / (.00001f + delta.Length()));
                    }
                    sum += Vector3.Lerp(Mirror(-1, left, a), Mirror(1, right, b), t) * weight;
                    float discrepancy = Vector3.DistanceSquared(a, b);
                    float agreement = 1 / (1 + discrepancy * discrepancy * 9000);
                    evidence += weight * agreement * Math.Min(1, certainty * certainty * 3) / (1 + 12 * angular);
                    total += weight;
                }
                if (total > 0) { guide[index] = sum / total; confidence[index] = evidence / total; }
            }
        });
    }
}

namespace Seein.Pet;

public sealed class PetMotion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public bool IsFlying { get; private set; }

    public void Place()
    {
        IsFlying = false;
        VelocityX = VelocityY = 0;
    }

    public void Launch(double vx, double vy)
    {
        VelocityX = vx;
        VelocityY = vy;
        IsFlying = true;
    }

    public bool Release(IReadOnlyList<PetDragSample> trail, double now, double power = 1)
    {
        var velocity = PetDragRelease.Estimate(trail, now, power);
        if (velocity is null) { Place(); return false; }
        Launch(velocity.Value.X, velocity.Value.Y);
        return true;
    }

    public static void Collide(PetMotion first, double firstWidth, PetMotion second, double secondWidth, double restitution)
    {
        if (!first.IsFlying && !second.IsFlying) return;
        var dx = second.X + secondWidth / 2 - first.X - firstWidth / 2;
        var dy = second.Y + secondWidth * 9 / 32 - first.Y - firstWidth * 9 / 32;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var radius = (firstWidth + secondWidth) * .22;
        if (distance >= radius) return;
        if (distance < .001) { dx = 1; dy = 0; distance = 1; }
        var nx = dx / distance;
        var ny = dy / distance;
        var overlap = radius - distance;
        var inverseFirst = 1 / Math.Max(1, firstWidth * firstWidth);
        var inverseSecond = 1 / Math.Max(1, secondWidth * secondWidth);
        var inverseTotal = inverseFirst + inverseSecond;
        first.X -= nx * overlap * inverseFirst / inverseTotal;
        first.Y -= ny * overlap * inverseFirst / inverseTotal;
        second.X += nx * overlap * inverseSecond / inverseTotal;
        second.Y += ny * overlap * inverseSecond / inverseTotal;
        var speed = (second.VelocityX - first.VelocityX) * nx + (second.VelocityY - first.VelocityY) * ny;
        if (speed >= 0) return;
        var impulse = -(1 + Math.Clamp(restitution, 0, 1)) * speed / inverseTotal;
        first.VelocityX -= impulse * nx * inverseFirst;
        first.VelocityY -= impulse * ny * inverseFirst;
        second.VelocityX += impulse * nx * inverseSecond;
        second.VelocityY += impulse * ny * inverseSecond;
        first.IsFlying = second.IsFlying = true;
    }

    public void Step(double seconds, double left, double top, double right, double bottom,
        double gravity, double restitution, double friction, bool ceilingBounce)
    {
        if (!IsFlying) return;
        seconds = Math.Clamp(seconds, 0, .05);
        var steps = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(VelocityX), Math.Abs(VelocityY)) * seconds / 8), 1, 64);
        for (var index = 0; index < steps && IsFlying; index++) Integrate(seconds / steps, left, top, right, bottom, gravity, restitution, friction, ceilingBounce);
    }

    private void Integrate(double seconds, double left, double top, double right, double bottom,
        double gravity, double restitution, double friction, bool ceilingBounce)
    {
        VelocityY += gravity * seconds;
        X += VelocityX * seconds;
        Y += VelocityY * seconds;
        var contact = false;
        if (X < left) { X = left; VelocityX = Math.Abs(VelocityX) < 40 ? 0 : Math.Abs(VelocityX) * restitution; VelocityY *= Math.Max(0, 1 - friction * seconds); contact = true; }
        if (X > right) { X = right; VelocityX = Math.Abs(VelocityX) < 40 ? 0 : -Math.Abs(VelocityX) * restitution; VelocityY *= Math.Max(0, 1 - friction * seconds); contact = true; }
        if (Y >= bottom)
        {
            Y = bottom;
            VelocityY = Math.Abs(VelocityY) < 40 ? 0 : -Math.Abs(VelocityY) * restitution;
            VelocityX *= Math.Max(0, 1 - friction * seconds);
            contact = true;
        }
        if (ceilingBounce && Y < top) { Y = top; VelocityY = Math.Abs(VelocityY) < 40 ? 0 : Math.Abs(VelocityY) * restitution; VelocityX *= Math.Max(0, 1 - friction * seconds); contact = true; }
        if (contact && ((Y >= bottom || Y <= top) && Math.Abs(VelocityY) < 1 && Math.Abs(VelocityX) < 15
            || (X <= left || X >= right) && Math.Abs(VelocityX) < 1 && Math.Abs(VelocityY) < 15)) Place();
    }
}

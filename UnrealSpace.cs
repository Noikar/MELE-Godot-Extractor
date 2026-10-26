using System.Numerics;

namespace LexExport;

// Converts Unreal Engine 3 space (left-handed, Z-up, ~1 unit = 1 cm) into glTF space
// (right-handed, Y-up, meters).
//
// The conversion swaps Y and Z. That is a reflection, which is exactly what turns
// left-handed into right-handed. Under a reflection M, a rotation of angle t about axis a
// becomes a rotation of -t about M·a, so a quaternion (x, y, z, w) maps to (-x, -z, -y, w).
public static class UnrealSpace
{
    public const float UnitsToMeters = 0.01f;

    public static Vector3 Position(Vector3 ue) => new Vector3(ue.X, ue.Z, ue.Y) * UnitsToMeters;

    public static Vector3 Direction(Vector3 ue) => new Vector3(ue.X, ue.Z, ue.Y);

    public static Quaternion Rotation(Quaternion ue) => Quaternion.Normalize(new Quaternion(-ue.X, -ue.Z, -ue.Y, ue.W));
}

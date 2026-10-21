using System;

namespace CopyCheck;

/// <summary>
/// net48 没有 Math.Clamp（.NET Core 2.0 才加入），这里补上。
/// </summary>
internal static class Num
{
    public static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    public static double Clamp(double value, double min, double max)
        => value < min ? min : (value > max ? max : value);
}

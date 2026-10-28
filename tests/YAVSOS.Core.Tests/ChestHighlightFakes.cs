#pragma warning disable CS0649, IDE1006 // Shapes only: the probe reads members, never values.
namespace YAVSOS.Core.Tests.ChestHighlightFakes;

public sealed class GameObject { }

public sealed class MaterialMan
{
    public static MaterialMan instance => null!;
    public void SetValue<T>(GameObject go, int nameID, T value, bool updateRenderers = false) { }
    public void ResetValue(GameObject go, int nameID) { }
}

public static class ShaderProps
{
    public static readonly int _Color = 1;
    public static readonly int _EmissionColor = 2;
}

public static class NoEmission
{
    public static readonly int _Color = 1;
}

public sealed class NoGenericSet
{
    public static NoGenericSet instance => null!;
    public void SetValue(GameObject go, int nameID, object value, bool updateRenderers = false) { }
    public void ResetValue(GameObject go, int nameID) { }
}

public sealed class NoReset
{
    public static NoReset instance => null!;
    public void SetValue<T>(GameObject go, int nameID, T value, bool updateRenderers = false) { }
}

public sealed class NoInstance
{
    public void SetValue<T>(GameObject go, int nameID, T value, bool updateRenderers = false) { }
    public void ResetValue(GameObject go, int nameID) { }
}

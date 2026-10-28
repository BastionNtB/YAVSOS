// Detached stand-ins for the Unity and Jotunn types JotunnGuiCapability resolves by name.
#pragma warning disable CS0649
namespace YAVSOS.Core.Tests.JotunnGuiFakes;

public class Transform { }
public struct Vector2 { }
public class GameObject { }
public struct Color { }
public class Text { }
public class ScrollRect { }
public enum ContentType { Standard }
public struct Resources { }

public class GuiManager
{
    public static GuiManager Instance { get; } = new GuiManager();
    public static GameObject? CustomGUIFront { get; private set; }
    public Resources ValheimControlResources;
    public Color ValheimOrange, ValheimBeige;
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height, bool draggable) => new GameObject();
    public GameObject CreateButton(string text, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
    public GameObject CreateToggle(Transform parent, float width, float height) => new GameObject();
    public GameObject CreateInputField(Transform parent, Vector2 min, Vector2 max, Vector2 position, ContentType type, string placeholder, int size, float width, float height) => new GameObject();
    public void ApplyTextStyle(Text text, Color color, int size, bool outline) { }
    public void ApplyScrollRectStyle(ScrollRect scroll) { }
}

public class NoToggle
{
    public static NoToggle Instance { get; } = new NoToggle();
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height, bool draggable) => new GameObject();
    public GameObject CreateButton(string text, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
}

public class ReshapedWoodpanel
{
    public static ReshapedWoodpanel Instance { get; } = new ReshapedWoodpanel();
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
}

public class InstanceBlockInput
{
    public static InstanceBlockInput Instance { get; } = new InstanceBlockInput();
    public void BlockInput(bool state) { }
}

public class WrongReturn
{
    public static WrongReturn Instance { get; } = new WrongReturn();
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height, bool draggable) => new GameObject();
    public void CreateButton(string text, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) { }
}

public class ColourAsProperty
{
    public static ColourAsProperty Instance { get; } = new ColourAsProperty();
    public Resources ValheimControlResources;
    public Color ValheimBeige;
    public Color ValheimOrange { get; set; }
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height, bool draggable) => new GameObject();
    public GameObject CreateButton(string text, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
    public GameObject CreateToggle(Transform parent, float width, float height) => new GameObject();
    public GameObject CreateInputField(Transform parent, Vector2 min, Vector2 max, Vector2 position, ContentType type, string placeholder, int size, float width, float height) => new GameObject();
    public void ApplyTextStyle(Text text, Color color, int size, bool outline) { }
    public void ApplyScrollRectStyle(ScrollRect scroll) { }
}

public class NoInstance
{
    public static object Instance { get; } = new object();
}

public class NoFront
{
    public static NoFront Instance { get; } = new NoFront();
    public Resources ValheimControlResources;
    public Color ValheimOrange, ValheimBeige;
    public static GameObject? CustomGUIBack { get; private set; }
    public static void BlockInput(bool state) { }
    public GameObject CreateWoodpanel(Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height, bool draggable) => new GameObject();
    public GameObject CreateButton(string text, Transform parent, Vector2 min, Vector2 max, Vector2 position, float width, float height) => new GameObject();
    public GameObject CreateToggle(Transform parent, float width, float height) => new GameObject();
    public GameObject CreateInputField(Transform parent, Vector2 min, Vector2 max, Vector2 position, ContentType type, string placeholder, int size, float width, float height) => new GameObject();
    public void ApplyTextStyle(Text text, Color color, int size, bool outline) { }
    public void ApplyScrollRectStyle(ScrollRect scroll) { }
}

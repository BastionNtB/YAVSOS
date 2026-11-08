using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Interface;

namespace YAVSOS.Integrations;

/// <summary>
/// X68 (D162): keeps a stack-key click YAVSOS acts on from also running ZenSign's inventory search. ZenSign (checked
/// on 1.9.6) registers its search as a game input button (<c>SignUI.Inputs.InventorySearch</c>, a Zen.ModLib
/// <c>ActionString</c>) on the key from its setting "Inventory Search - Keyboard/Mouse" (default middle mouse), and runs
/// the search in a <c>Hud.Update</c> postfix on <c>ZInput.GetButtonUp</c>, whatever modifiers are held. So when that key
/// is the stack key's main key, Shift + middle click would stash the stack, then search for it, light the chest it went
/// to and close the inventory. Once YAVSOS acts on a stack-key press, this resets that button's state
/// (<c>ZInput.ResetButtonStatus</c>) every frame until the click is released (<see cref="ClickConsume"/>), so ZenSign
/// never sees the release. Plain middle click stays ZenSign's.
///
/// Optional and capability-checked (D058): ZenSign is found by its plugin ID and read by reflection, with no reference to
/// it; the shape is checked on the first stack-key press after the game's input exists. If ZenSign's button, key setting
/// or the game's reset can't be found, or the reset throws, the guard turns itself off and logs once; the stack key keeps
/// working and the overlap comes back. Unity thread only.
///
/// Workaround for ZenSign. Removal condition: ZenSign's search checks modifiers or no longer uses a key YAVSOS's stack
/// key shares by default, or ZenSign no longer ships in the pack's partner mods.
/// </summary>
internal static class ZenSignSearchGuard
{
    internal const string ZenSignGuid = "ZenDragon.ZenSign";
    private enum GuardState { Unchecked, Active, Off, Absent }
    private static GuardState state;
    private static string button = "";
    private static ConfigEntry<KeyCode>? searchKey;
    private static readonly ClickConsume Consume = new ClickConsume();
    private static ManualLogSource? log;

    internal static void Initialize(ManualLogSource logger) => log = logger;

    /// <summary>For the Lab and the bible: Unchecked, Active, Off or Absent, with the button's name while active.</summary>
    internal static string Status => state + (state == GuardState.Active ? " (" + button + ", key " + searchKey?.Value + ")" : "");

    /// <summary>
    /// YAVSOS acted on a stack-key press whose main key is <paramref name="main"/>: when ZenSign's search is on the same
    /// key, this click is kept from it.
    /// </summary>
    internal static void Acted(KeyCode main)
    {
        if (!Bound() || !PartnerKeys.SameMainKey(main.ToString(), searchKey!.Value.ToString())) return;
        Consume.Arm();
        if (Consume.Frame(true)) Reset();
    }

    /// <summary>Every frame (Plugin.UpdateCore): while a consumed click lasts, its button's state is reset again.</summary>
    internal static void Update(KeyCode main)
    {
        if (!Consume.Armed) return;
        if (state != GuardState.Active) { Consume.Cancel(); return; }
        if (Consume.Frame(UnityInput.Current.GetKey(main))) Reset();
    }

    private static bool Bound()
    {
        if (state == GuardState.Active) return true;
        if (state != GuardState.Unchecked) return false;
        if (!Chainloader.PluginInfos.TryGetValue(ZenSignGuid, out var info) || info?.Instance == null) { state = GuardState.Absent; return false; }
        if (ZInput.instance == null) return false; // The game's input isn't up yet; checked again on the next press.
        try
        {
            var assembly = info.Instance.GetType().Assembly;
            const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var inputs = assembly.GetType("ZenSign.SignUI+Inputs") ?? throw new MissingMemberException("ZenSign.SignUI+Inputs");
            var action = inputs.GetField("InventorySearch", Static)?.GetValue(null) ?? throw new MissingMemberException("SignUI.Inputs.InventorySearch");
            var name = action.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
            if (name?.PropertyType != typeof(string)) throw new MissingMemberException(action.GetType().FullName + ".Name");
            var configs = assembly.GetType("ZenSign.Configs") ?? throw new MissingMemberException("ZenSign.Configs");
            var key = configs.GetField("InventorySearchKeyboard", Static);
            if (key?.FieldType != typeof(ConfigEntry<KeyCode>)) throw new MissingMemberException("Configs.InventorySearchKeyboard as ConfigEntry<KeyCode>");
            if (typeof(ZInput).GetMethod(nameof(ZInput.ResetButtonStatus), BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null) == null)
                throw new MissingMemberException("ZInput.ResetButtonStatus(string)");
            var found = name.GetValue(action) as string;
            if (string.IsNullOrEmpty(found)) throw new MissingMemberException("ZenSign's search button has no name");
            if (ZInput.instance.GetButtonDef(found) == null) throw new MissingMemberException("ZenSign's search button " + found + " isn't one of the game's input buttons");
            searchKey = (ConfigEntry<KeyCode>?)key.GetValue(null) ?? throw new MissingMemberException("Configs.InventorySearchKeyboard is null");
            button = found!; state = GuardState.Active;
            log?.LogInfo("ZenSign's inventory search found (input " + button + ", key " + searchKey.Value + "): a stack-key click YAVSOS acts on doesn't also search.");
            return true;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            TurnOff(error);
            return false;
        }
    }

    private static void Reset()
    {
        try { ResetButton(button); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { TurnOff(error); }
    }

    // Its own method, so a game without ZInput.ResetButtonStatus fails here, inside the guard's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ResetButton(string name)
    {
        if (ZInput.instance != null && ZInput.instance.GetButtonDef(name) != null) ZInput.ResetButtonStatus(name);
    }

    private static void TurnOff(Exception error)
    {
        state = GuardState.Off; Consume.Cancel();
        var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        log?.LogWarning("YAVSOS couldn't find or reset ZenSign's inventory search input (" + cause.GetType().Name + ": " + cause.Message +
            "), so a stack-key click can also run ZenSign's search. The stack key still stashes.");
    }
}

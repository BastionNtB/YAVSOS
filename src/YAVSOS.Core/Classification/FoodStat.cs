namespace YAVSOS.Core.Classification;

/// <summary>
/// D064: a food's main stat, by the rule the game uses to colour the inventory's fork icon (InventoryGrid.UpdateGui,
/// game 1.0.17), so a "stamina food" chest takes exactly the foods players see as stamina. The order matters: eitr when
/// it is over twice both others; else health when over twice stamina (eitr not compared); else stamina when over twice
/// health; else balanced (the game's white icon).
/// </summary>
public static class FoodStat
{
    public static string Main(double health, double stamina, double eitr) =>
        health < eitr / 2 && stamina < eitr / 2 ? "eitr"
        : stamina < health / 2 ? "health"
        : health < stamina / 2 ? "stamina"
        : "balanced";
}

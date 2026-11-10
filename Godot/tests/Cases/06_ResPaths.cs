using VTX;

public static class Paths
{
    public const string A = "res://demo/player.tscn";                 // expect: none
    public const string B = "res://demo/missing.tscn";                // expect: VTX008
    public const string C = "res://Demo/player.tscn";                 // expect: VTX008
    public const string D = "res://demo/";                            // expect: none
    public const string E = "res://demo/player.tscn::SubResource_1";  // expect: none
    public static string F(int n) => "res://levels/level_" + n + ".tscn"; // expect: none
    public static string G(int n) => string.Format("res://levels/level_{0}.tscn", n); // expect: none
    public const string H = "res://addons/whatever.tscn";             // expect: none
    public const string I = "res://.godot/imported/x.tscn";           // expect: none
    public const string J = "user://save.json";                       // expect: none
    public static readonly string[] K = { Res.Scenes.Player, Res.Scenes.Level1, Res.Scenes.LevelTwo, Res.Shaders.Player, Res.Textures.Icon, Res.Textures.Textures_, Res.Textures.Hero, Res.Scenes.Ui_AB, Res.Scenes.Ui_AB_2 }; // expect: none
}

public static class CaseRun
{
    public static string Run()
    {
        if (Res.Scenes.Player != "res://demo/player.tscn") return "Player=" + Res.Scenes.Player;
        if (Res.Textures.Hero != "res://Art/Hero.png") return "Hero=" + Res.Textures.Hero;
        if (typeof(Res).IsPublic) return "Res 应为 internal";
        foreach (var f in typeof(Res.Scenes).GetFields())
            if (((string)f.GetValue(null)!).Contains("secret") || ((string)f.GetValue(null)!).Contains("addons")) return "包含了被忽略的资源: " + f.Name;
        return "ok";
    }
}

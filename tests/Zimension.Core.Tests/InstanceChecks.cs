// Instance groups (the native player's InstanceGroups.cpp port): layouts, seeded randomness and the group transform.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Zimension.Core;
using Zimension.Editor;

static class InstanceChecks
{
    static List<InstanceGroups.Copy> Copies(string settings, string transform = "{}", Matrix4x4? source = null) =>
        InstanceGroups.Copies(JsonNode.Parse(settings), JsonNode.Parse(transform), new List<Matrix4x4> { source ?? Matrix4x4.CreateTranslation(2, 0, 3) }, null, Matrix4x4.Identity, new List<string>());

    public static void Run(Action<bool, string> expect)
    {
        var grid = Copies(@"{""sourceWorldItemId"":""a"",""count"":4,""distributionMode"":""pattern"",""patternType"":""grid"",""spacing"":2}");
        var xs = grid.Select(c => c.World.Translation.X).OrderBy(x => x).ToList();
        expect(grid.Count == 4 && Math.Abs(xs[0] - 1) < 1e-4 && Math.Abs(xs[3] - 3) < 1e-4, "instances: a 2x2 grid centred on the source position");
        var rect = Copies(@"{""sourceWorldItemId"":""a"",""count"":40,""distributionMode"":""shape"",""shapeType"":""rectangle"",""width"":20,""depth"":10,""alignAroundShape"":true}", "{}", Matrix4x4.Identity);
        bool onEdge = rect.All(c => { var p = c.World.Translation; return Math.Abs(Math.Abs(p.X) - 10) < 1e-3 || Math.Abs(Math.Abs(p.Z) - 5) < 1e-3; });
        expect(rect.Count == 40 && onEdge, "instances: rectangle perimeter copies lie on its edges");
        var a = Copies(@"{""sourceWorldItemId"":""a"",""count"":12,""distributionMode"":""area"",""areaType"":""circle"",""radius"":5,""seed"":7}");
        var b = Copies(@"{""sourceWorldItemId"":""a"",""count"":12,""distributionMode"":""area"",""areaType"":""circle"",""radius"":5,""seed"":7}");
        expect(a.Count == 12 && a.Zip(b, (x, y) => x.World == y.World).All(s => s), "instances: seeded scatter is repeatable");
        expect(a.All(c => Vector2.Distance(new Vector2(c.World.Translation.X, c.World.Translation.Z), new Vector2(2, 3)) <= 5.001f), "instances: scatter stays inside its circle");
        var moved = Copies(@"{""sourceWorldItemId"":""a"",""count"":1}", @"{""position"":{""x"":10,""y"":0,""z"":0},""rotation"":{""x"":0,""y"":0,""z"":0},""scale"":{""x"":2,""y"":2,""z"":2}}");
        expect(moved.Count == 1 && Math.Abs(moved[0].World.Translation.X - 10) < 1e-4 && Math.Abs(moved[0].World.M11 - 2) < 1e-4, "instances: the group transform places and scales copies");
    }
}

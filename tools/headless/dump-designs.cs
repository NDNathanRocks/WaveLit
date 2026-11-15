// Writes the Overlay Designer's GLSL for every shape × motion × cursor (with and without music) as JSON, for
// tools/headless/check-overlays.mjs to compile and render in headless Chrome.
//   dotnet run tools/headless/dump-designs.cs -- <out.json>
#:project ../../src/Wavelit.Core/Wavelit.Core.csproj
#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:property TreatWarningsAsErrors=false
#:property NuGetAudit=false

using System.Text.Json;
using Wavelit.Core.Create.Shaders;

string output = args.Length > 0 ? args[0] : "designs.json";
var list = new List<object>();
foreach (var s in OverlayDesign.Shapes)
    foreach (var m in OverlayDesign.Motions)
        foreach (var c in OverlayDesign.Cursors)
            foreach (bool music in new[] { false, true })
            {
                var d = new OverlayDesign(s.Id, m.Id, c.Id, Music: music);
                list.Add(new { name = $"{s.Id}/{m.Id}/{c.Id}{(music ? "/music" : "")}", code = d.ToGlsl() });
            }
File.WriteAllText(output, JsonSerializer.Serialize(list));
Console.WriteLine($"{list.Count} designs → {output}");

using System.Text.Json;
using Avalonia.Headless.XUnit;
using Composa.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Opt-in real-model acceptance: prompts go through the production agent; grading examines editor objects, not prose.</summary>
public class AgentAcceptanceTests
{
    private sealed record Scenario(string Id, string Level, string Prompt, Action<EditorSession> Setup, Action<EditorSession> Grade);
    private static readonly Scenario[] Cases =
    [
        new("shape", "basic", "Создай редактируемый голубой прямоугольник с именем Tile: x=30, y=40, ширина=120, высота=80, цвет #0088FF. Нужен один новый слой, холст и фон не меняй.", _ => { }, s =>
        {
            Assert.Equal(2, s.Document.Layers.Count); var tile = s.Document.AllLayers().Single(l => l.Name == "Tile");
            Assert.NotNull(tile.Shape); Assert.Equal(0xFF0088FFu, tile.Shape.Fill); Assert.Equal(30, tile.Transform.X); Assert.Equal(40, tile.Transform.Y);
            Assert.Equal(120, tile.Transform.Width); Assert.Equal(80, tile.Transform.Height); Assert.Equal((600,400), (s.Document.Width,s.Document.Height));
        }),
        new("guides", "basic", "Добавь четыре настоящие направляющие с отступом 40 px от краёв холста. Не рисуй линии и не добавляй слои.", _ => { }, s =>
        {
            Assert.Single(s.Document.Layers); Assert.Equal(4, s.Guides.Count);
            Assert.Equal(new[] {40d,560d}, s.Guides.Where(g => g.Axis == GuideAxis.Vertical).Select(g => g.Position).Order());
            Assert.Equal(new[] {40d,360d}, s.Guides.Where(g => g.Axis == GuideAxis.Horizontal).Select(g => g.Position).Order());
        }),
        new("grouped-text", "intermediate", "В группе Card замени текст слоя Title на Осенняя распродажа и цвет на #FFFFFF. Не создавай слои, не растеризуй текст, Footer и остальные объекты не меняй. Весь текст должен быть внутри холста.", s =>
            new JavaScriptRuntime().Execute(s,"const d=app.activeDocument; const a=d.addRectangle(20,20,400,240,'#0088FF','Card Back'); const b=d.addText('Old',40,50,{size:24,name:'Title'}); d.groupLayers([a.id,b.id],'Card');d.addText('KEEP',30,330,{name:'Footer'});"), s =>
        {
            var title = s.Document.AllLayers().Single(l => l.Name == "Title"); Assert.NotNull(title.Text);
            Assert.Equal("Осенняя распродажа", title.Text.Text); Assert.Equal((uint)SKColors.White, title.Text.Color);
            Assert.Equal("KEEP", s.Document.AllLayers().Single(l => l.Name == "Footer").Text!.Text);
            Assert.Equal(5, s.Document.AllLayers().Count()); Assert.True(title.Bounds.Right <= s.Document.Width);
        }),
        new("mask", "intermediate", "Выдели прямоугольник x=60,y=70,width=100,height=80. По выделению добавь слой-маску существующей фигуре Card, затем сними выделение. Фигура должна остаться редактируемой; больше ничего не меняй.", s =>
            new JavaScriptRuntime().Execute(s,"app.activeDocument.addRectangle(40,50,300,200,'#0088FF','Card');"), s =>
        {
            var card = s.Document.AllLayers().Single(l => l.Name == "Card"); Assert.NotNull(card.Shape); Assert.NotNull(card.Mask); Assert.Null(s.Selection);
            Assert.Equal(255, card.Mask.GetPixel(30,30).Alpha); Assert.Equal(0,card.Mask.GetPixel(180,150).Alpha); Assert.Equal(2,s.Document.Layers.Count);
        }),
        new("batch", "advanced", "Перекрась каждый второй квадрат в желтый #FFFF00, начиная с верхнего квадрата в панели слоёв. Только квадратные фигуры, не прямоугольник Wide. Все существующие объекты должны остаться редактируемыми, без новых слоёв и без растеризации.", s =>
        {
            var r = new JavaScriptRuntime(); r.Execute(s,"const d=app.activeDocument; for(let i=0;i<11;i++)d.addRectangle(10+i*45,30,30,30,'#0000FF','Square '+i);d.addRectangle(20,120,80,30,'#00FF00','Wide');");
        }, s =>
        {
            var squares = s.Document.AllLayers().Where(l => l.Name.StartsWith("Square ")).Reverse().ToArray(); Assert.Equal(11,squares.Length);
            for(var i=0;i<squares.Length;i++) Assert.Equal((uint)(i%2==0?SKColors.Yellow:SKColors.Blue),squares[i].Shape!.Fill);
            Assert.Equal((uint)SKColors.Lime,s.Document.AllLayers().Single(l=>l.Name=="Wide").Shape!.Fill); Assert.Equal(13,s.Document.AllLayers().Count());
        }),
        new("smart-object", "advanced", "Преобразуй существующие Red и Blue вместе в один смарт-объект с редактируемым содержимым. Не объединяй их в обычный растр. Холст и внешний вид не меняй.", s =>
            new JavaScriptRuntime().Execute(s,"const d=app.activeDocument;d.addRectangle(20,20,50,50,'#FF0000','Red');d.addRectangle(90,20,50,50,'#0000FF','Blue');"), s =>
        {
            var smart = Assert.Single(s.Document.AllLayers(), l => l.IsSmartObject); Assert.Equal(2,s.Document.Layers.Count);
            var content=smart.SmartObject!.OpenDocument(); Assert.Equal(2,content.Layers.Count); Assert.All(content.Layers,l=>Assert.NotNull(l.Shape));
            using var rendered = s.Flatten(); Assert.Equal(SKColors.Red,rendered.GetPixel(30,30)); Assert.Equal(SKColors.Blue,rendered.GetPixel(100,30));
        })
    ];

    [AvaloniaFact]
    public async Task Production_agent_passes_basic_intermediate_and_advanced_scenarios_when_live_test_is_requested()
    {
        var executable = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_SERVER");
        var model = Environment.GetEnvironmentVariable("COMPOSA_LIVE_LLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(model)) return;
        var settings = new Settings { AssistantServerExecutable = executable, AssistantModelPath = model,
            AssistantServerUrl = "http://127.0.0.1:18080", AssistantContextSize = 16384, AssistantMaxTokens = 2048 };
        using var server = new LlamaServerHost(settings); var provider = new LlamaAssistantProvider(settings,server);
        var reports = new List<object>(); var failures = new List<string>();
        foreach(var item in Cases)
        {
            Directory.CreateDirectory(Path.Combine("artifacts","assistant-evals"));
            var w = new MainWindow(); w.Settings.CheckForUpdates=false; var s=EditorSession.NewCanvas(600,400,SKColors.White); w.AddSession(s); w.Show();
            var runtime=new JavaScriptRuntime(); item.Setup(s); var before=DocumentFingerprint(s); var history=s.History.Count;
            string log=""; var started=DateTime.UtcNow;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(8));
            try
            {
                var result=await AssistantAgent.RunAsync(provider,new(item.Prompt,JavaScriptRuntime.DescribeCompact(s),JavaScriptRuntime.Reference),s,
                    new(w,s,runtime,w.AiTasks,settings,[],false),message=>File.WriteAllText(
                        Path.Combine("artifacts","assistant-evals","live-progress.txt"),$"{item.Id} · {DateTime.UtcNow:O} · {message}"),deadline.Token);
                log=result.Log; item.Grade(s); Assert.True(result.Changed); Assert.Equal(history+1,s.History.Count);
                Assert.True(Screenshots.Save(w,"agent-acceptance-"+item.Id));
                s.Undo(); Assert.Equal(before,DocumentFingerprint(s)); s.Redo(); item.Grade(s);
                reports.Add(new {item.Id,item.Level,passed=true,result.ToolCount,seconds=(DateTime.UtcNow-started).TotalSeconds,log});
            }
            catch(Exception error)
            {
                if(error is AssistantAgentException agent)log=agent.ActionLog;
                else if(error.Data["AssistantActionLog"] is string cancelledLog)log=cancelledLog;
                failures.Add(item.Id+": "+error.Message); reports.Add(new {item.Id,item.Level,passed=false,error=error.ToString(),log});
            }
            finally
            {
                w.Close(); Directory.CreateDirectory(Path.Combine("artifacts","assistant-evals"));
                await File.WriteAllTextAsync(Path.Combine("artifacts","assistant-evals","live-acceptance.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}),TestContext.Current.CancellationToken);
            }
        }
        Assert.True(failures.Count==0,string.Join("\n",failures));
    }

    private static string DocumentFingerprint(EditorSession session)
    {
        var state=System.Text.Json.Nodes.JsonNode.Parse(AssistantEditorTools.Fingerprint(session))!.AsObject();
        // Showing rulers/guides is a view preference, not undoable document content.
        state.Remove("ShowGuides"); state.Remove("ShowRulers"); state.Remove("LockGuides");
        return state.ToJsonString();
    }
}
